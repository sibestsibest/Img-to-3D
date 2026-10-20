using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Tests;

public class JobWorkflowTests
{
    [Fact]
    public async Task SuccessfulJob_TransitionsToSucceededWithDimensionsAndDownloads()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var created = await client.CreateJobAsync();
        Assert.Equal(JobStatus.Pending, created.Status);

        var job = await client.WaitForTerminalAsync(created.Id);

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.True(job.Downloads.Glb);
        Assert.True(job.Downloads.Stl);
        Assert.Equal(12, job.Mesh!.Triangles);

        // Raw box is 0.5 x 1.0 x 0.25 model units; longest side defaults to 100 mm.
        Assert.Equal(50, job.DimensionsMm!.X, 3);
        Assert.Equal(100, job.DimensionsMm.Y, 3);
        Assert.Equal(25, job.DimensionsMm.Z, 3);
        Assert.Equal(ScaleAxis.Longest, job.Scale!.Axis);

        Assert.Single(factory.Inference.Deleted); // inference-side files cleaned up after retrieval
    }

    [Fact]
    public async Task ProcessingStage_IsPersistedWhileRunning()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();

        var created = await client.CreateJobAsync();
        JobDto job;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            job = await client.GetJobAsync(created.Id);
        }
        while (job.Stage != "EvaluatingDensity" && DateTime.UtcNow < deadline);

        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(0.5, job.StageProgress);
    }

    [Fact]
    public async Task InferenceFailure_IsReportedWithCodeAndMessage()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Fail;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("no_foreground", job.ErrorCode);
        Assert.Contains("foreground", job.Error);
        Assert.False(job.Downloads.Glb);
    }

    [Fact]
    public async Task InferenceUnavailable_FailsGracefully()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Unavailable;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("inference_unavailable", job.ErrorCode);
        Assert.DoesNotContain("127.0.0.1", job.Error); // no internal URLs leak
    }

    [Fact]
    public async Task InferenceRejection_KeepsUpstreamCode()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Rejected;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal("invalid_image", job.ErrorCode);
    }

    [Fact]
    public async Task InferenceTimeout_FailsAndCancelsUpstream()
    {
        using var factory = new ApiFactory(new() { ["InferenceService:GenerationTimeout"] = "00:00:00.300" });
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("timeout", job.ErrorCode);
        Assert.NotEmpty(factory.Inference.CancelRequests);
    }

    [Fact]
    public async Task InferenceLostJob_Fails()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Lost;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal("inference_lost", job.ErrorCode);
    }

    [Fact]
    public async Task InvalidGeneratedMesh_Fails()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.InvalidMesh;
        var client = factory.CreateClient();

        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        Assert.Equal(JobStatus.Failed, job.Status);
    }

    [Fact]
    public async Task Cancel_RunningJob_EndsCancelled()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();
        var created = await client.CreateJobAsync();
        await WaitUntilAsync(async () => (await client.GetJobAsync(created.Id)).Status == JobStatus.Processing);

        var response = await client.PostAsync($"/api/models/{created.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await client.WaitForTerminalAsync(created.Id);
        Assert.Equal(JobStatus.Cancelled, job.Status);

        var again = await client.PostAsync($"/api/models/{created.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task DuplicateSubmitWithSameIdempotencyKey_ReturnsSameJob()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var first = await client.UploadAsync(TestImages.Png(), idempotencyKey: "same-key-123");
        var second = await client.UploadAsync(TestImages.Png(), idempotencyKey: "same-key-123");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);
        var b = await second.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json);
        Assert.Equal(a!.Id, b!.Id);
        var list = await client.GetFromJsonAsync<PagedResult<JobDto>>("/api/models", ApiFactory.Json);
        Assert.Equal(1, list!.TotalCount);
    }

    [Fact]
    public async Task ActiveJobLimit_Returns429()
    {
        using var factory = new ApiFactory(new() { ["Upload:MaxActiveJobsPerOwner"] = "1" });
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();

        await client.CreateJobAsync();
        var response = await client.UploadAsync(TestImages.Png());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task List_IsPagedAndNewestFirst()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add((await client.CreateJobAsync()).Id);

        var page1 = await client.GetFromJsonAsync<PagedResult<JobDto>>("/api/models?page=1&pageSize=2", ApiFactory.Json);
        var page2 = await client.GetFromJsonAsync<PagedResult<JobDto>>("/api/models?page=2&pageSize=2", ApiFactory.Json);

        Assert.Equal(3, page1!.TotalCount);
        Assert.Equal([ids[2], ids[1]], page1.Items.Select(j => j.Id));
        Assert.Equal([ids[0]], page2!.Items.Select(j => j.Id));
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=500")]
    public async Task List_RejectsInvalidPaging(string query)
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync("/api/models" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OtherOwner_CannotSeeOrTouchJob()
    {
        using var factory = new ApiFactory();
        var owner = factory.CreateClient();
        var stranger = factory.CreateClient();
        var job = await owner.WaitForTerminalAsync((await owner.CreateJobAsync()).Id);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/models/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/models/{job.Id}/download?format=stl")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/models/{job.Id}")).StatusCode);
        Assert.Equal(0, (await stranger.GetFromJsonAsync<PagedResult<JobDto>>("/api/models", ApiFactory.Json))!.TotalCount);
    }

    [Fact]
    public async Task Delete_RemovesJobAndFiles()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        var folder = Path.Combine(factory.Root, "storage", job.Id.ToString("N"));
        Assert.True(Directory.Exists(folder));

        var response = await client.DeleteAsync($"/api/models/{job.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/models/{job.Id}")).StatusCode);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
