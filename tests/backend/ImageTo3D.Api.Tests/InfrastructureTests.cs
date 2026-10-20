using System.Net;
using System.Net.Http.Json;
using System.Text;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Tests;

public class CleanupTests
{
    [Fact]
    public async Task Cleanup_RemovesExpiredFinishedJobsAndOrphans_KeepsActiveAndRecent()
    {
        using var factory = new ApiFactory(new() { ["Storage:RetentionPeriod"] = "1.00:00:00" });
        var client = factory.CreateClient();
        var recent = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var old = NewJob(JobStatus.Succeeded, DateTime.UtcNow.AddDays(-3));
        var oldActive = NewJob(JobStatus.Pending, DateTime.UtcNow.AddDays(-3));
        oldActive.CancelRequested = true; // keep the worker from processing it
        db.Jobs.AddRange(old, oldActive);
        await db.SaveChangesAsync();
        await store.SaveAsync(old.Id, "model.glb", [1, 2, 3], CancellationToken.None);

        var orphan = Path.Combine(factory.Root, "storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-1));
        var freshOrphan = Path.Combine(factory.Root, "storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshOrphan);

        var cleanup = factory.Services.GetServices<IHostedService>().OfType<RetentionCleanupService>().Single();
        var (jobs, orphans) = await cleanup.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(1, jobs);
        Assert.Equal(1, orphans);
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "storage", old.Id.ToString("N"))));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(freshOrphan));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/models/{recent.Id}")).StatusCode);
        db.ChangeTracker.Clear();
        Assert.NotNull(await db.Jobs.FindAsync(oldActive.Id));
    }

    private static GenerationJob NewJob(JobStatus status, DateTime created) => new()
    {
        Id = Guid.NewGuid(),
        OwnerKeyHash = new string('a', 64),
        OriginalFileName = "x.png",
        OriginalFormat = "png",
        Status = status,
        CreatedAt = created,
        UpdatedAt = created,
    };
}

public class HealthTests
{
    [Fact]
    public async Task Health_ReportsDependenciesWithoutSecrets()
    {
        using var factory = new ApiFactory(new() { ["InferenceService:ApiKey"] = "super-secret-key" });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"database\":\"ok\"", body);
        Assert.DoesNotContain("super-secret-key", body);
        Assert.DoesNotContain(factory.Root.Replace("\\", "\\\\"), body);
        Assert.DoesNotContain("5090", body);
    }

    [Fact]
    public async Task Health_IsDegradedWhenInferenceUnreachable()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Unavailable;

        var health = await factory.CreateClient().GetFromJsonAsync<ImageTo3D.Api.DTOs.HealthDto>("/api/health", ApiFactory.Json);

        Assert.Equal("degraded", health!.Status);
        Assert.Equal("unreachable", health.Inference.Status);
    }

    [Fact]
    public async Task Responses_CarryCorrelationId()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-correlation-1");

        var response = await client.GetAsync($"/api/models/{Guid.NewGuid()}");

        Assert.Equal("test-correlation-1", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("test-correlation-1", (await response.ProblemAsync()).GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Errors_DoNotLeakStackTraces()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().PutAsync($"/api/models/{Guid.NewGuid()}/scale", new StringContent("{not json", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("   at ", body);
    }
}

public class ArtifactStoreTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/model.glb")]
    [InlineData("C:\\windows\\win.ini")]
    [InlineData("MODEL.GLB")]
    public async Task RejectsUnsafeNames(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "imageto3d-store", Guid.NewGuid().ToString("N"));
        var store = new LocalFileArtifactStore(Options.Create(new StorageOptions { RootPath = root }), new TestEnvironment(root));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Guid.NewGuid(), name, [1], CancellationToken.None));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task SaveReadDelete_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "imageto3d-store", Guid.NewGuid().ToString("N"));
        var store = new LocalFileArtifactStore(Options.Create(new StorageOptions { RootPath = root }), new TestEnvironment(root));
        var id = Guid.NewGuid();

        await store.SaveAsync(id, "model.stl", [1, 2, 3], CancellationToken.None);
        Assert.Equal([1, 2, 3], await store.ReadAllAsync(id, "model.stl", CancellationToken.None));
        Assert.Null(await store.ReadAllAsync(id, "model.glb", CancellationToken.None));

        await store.DeleteJobAsync(id, CancellationToken.None);
        Assert.Null(await store.OpenReadAsync(id, "model.stl", CancellationToken.None));
        Directory.Delete(root, true);
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public class InferenceClientTests
{
    [Fact]
    public async Task ConnectionFailure_BecomesUnavailable()
    {
        var client = Create(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<InferenceUnavailableException>(() => client.SubmitAsync([1], "c", CancellationToken.None));
    }

    [Fact]
    public async Task HttpTimeout_BecomesUnavailable()
    {
        var client = Create(_ => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<InferenceUnavailableException>(() => client.GetJobAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task QueueFull_BecomesUnavailableWithFriendlyMessage()
    {
        var client = Create(_ => Problem(HttpStatusCode.ServiceUnavailable, InferenceErrorCodes.QueueFull, "The inference queue is full."));
        var ex = await Assert.ThrowsAsync<InferenceUnavailableException>(() => client.SubmitAsync([1], "c", CancellationToken.None));
        Assert.Contains("queue is full", ex.Message);
    }

    [Fact]
    public async Task BadRequest_KeepsCodeAndDetail()
    {
        var client = Create(_ => Problem(HttpStatusCode.BadRequest, InferenceErrorCodes.InvalidImage, "Upload a PNG, JPEG or WebP image."));
        var ex = await Assert.ThrowsAsync<InferenceRequestException>(() => client.SubmitAsync([1], "c", CancellationToken.None));
        Assert.Equal(InferenceErrorCodes.InvalidImage, ex.Code);
        Assert.Equal("Upload a PNG, JPEG or WebP image.", ex.Message);
    }

    [Fact]
    public async Task UnknownJob_ReturnsNull()
    {
        var client = Create(_ => Problem(HttpStatusCode.NotFound, null, "Job or artifact not found."));
        Assert.Null(await client.GetJobAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Submit_SendsMultipartImageAndCorrelationId()
    {
        HttpRequestMessage? captured = null;
        var client = Create(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new InferenceJobDto(Guid.NewGuid(), InferenceJobStatus.Queued, "Queued", null, null, null, DateTimeOffset.UtcNow, null, null, [], null)),
            };
        });

        await client.SubmitAsync([1, 2, 3], "corr-12345678", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/internal/jobs/", captured.RequestUri!.AbsolutePath);
        Assert.Equal("corr-12345678", captured.Headers.GetValues("X-Correlation-ID").Single());
        Assert.IsType<MultipartFormDataContent>(captured.Content);
    }

    [Fact]
    public async Task DownloadArtifact_RejectsUnknownNames()
    {
        var client = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadArtifactAsync(Guid.NewGuid(), "../secrets", CancellationToken.None));
    }

    private static InferenceClient Create(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new StubHandler(handler)) { BaseAddress = new Uri("http://inference.test/") }, NullLogger<InferenceClient>.Instance);

    private static HttpResponseMessage Problem(HttpStatusCode status, string? code, string detail) =>
        new(status) { Content = JsonContent.Create(new { title = "x", status = (int)status, detail, code }) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
