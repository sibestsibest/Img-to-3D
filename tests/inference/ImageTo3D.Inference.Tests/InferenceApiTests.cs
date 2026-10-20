using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Inference.Jobs;
using ImageTo3D.Inference.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ImageTo3D.Inference.Tests;

/// <summary>HTTP contract tests that need no model files (models are never loaded).</summary>
public sealed class InferenceApiTests : IClassFixture<InferenceApiTests.Factory>
{
    private readonly Factory _factory;

    public InferenceApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Health_ReportsNotReadyWithoutModels()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var health = await response.Content.ReadFromJsonAsync<InferenceHealthDto>();
        Assert.Equal("NotLoaded", health!.ModelState);
    }

    [Fact]
    public async Task Submit_RejectsNonImage()
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()), "image", "x.png" } };

        var response = await _factory.CreateClient().PostAsync("/internal/jobs/", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submit_RejectsMissingFile()
    {
        using var content = new MultipartFormDataContent { { new StringContent("1"), "resolution" } };
        var response = await _factory.CreateClient().PostAsync("/internal/jobs/", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("1000")]
    [InlineData("abc")]
    public async Task Submit_RejectsInvalidResolution(string resolution)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Png()), "image", "x.png" },
            { new StringContent(resolution), "resolution" },
        };
        var response = await _factory.CreateClient().PostAsync("/internal/jobs/", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownJob_Returns404()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/internal/jobs/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Fappsettings.json")]
    [InlineData("secret.txt")]
    [InlineData("MESH.GLB")]
    public async Task Artifact_OnlyWhitelistedNamesAreServed(string name)
    {
        var response = await _factory.CreateClient().GetAsync($"/internal/jobs/{Guid.NewGuid()}/artifacts/{name}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Job_CancelWhileQueued_IsImmediate()
    {
        var job = new InferenceJob(Guid.NewGuid(), Path.GetTempPath(), new GenerationRequest([1]));

        Assert.True(job.RequestCancel());

        Assert.Equal(InferenceJobStatus.Cancelled, job.Status);
        Assert.False(job.TryStart());
        Assert.False(job.RequestCancel());
    }

    [Fact]
    public void Job_ProgressIgnoredAfterTerminal()
    {
        var job = new InferenceJob(Guid.NewGuid(), Path.GetTempPath(), new GenerationRequest([1]));
        Assert.True(job.TryStart());
        job.ReportProgress(new StageProgress(PipelineStage.EvaluatingDensity, 0.3));
        job.Fail(InferenceErrorCodes.NoSurface, "no surface");

        job.ReportProgress(new StageProgress(PipelineStage.Exporting));

        var dto = job.ToDto();
        Assert.Equal(InferenceJobStatus.Failed, dto.Status);
        Assert.Equal("EvaluatingDensity", dto.Stage);
        Assert.Null(dto.StageProgress);
        Assert.Empty(dto.Artifacts);
    }

    internal static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(128, 128);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Inference:LoadModelsOnStartup", "false");
            builder.UseSetting("Inference:ModelDirectory", Path.Combine(Path.GetTempPath(), "no-models-here"));
            builder.UseSetting("Inference:WorkDirectory", Path.Combine(Path.GetTempPath(), "imageto3d-inference-tests"));
        }
    }
}

public sealed class ApiKeyTests : IClassFixture<ApiKeyTests.Factory>
{
    private readonly Factory _factory;

    public ApiKeyTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task InternalEndpoints_RequireKeyWhenConfigured()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-Internal-Api-Key");
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-key-123");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Health_IsOpenForProbes()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Inference:LoadModelsOnStartup", "false");
            builder.UseSetting("Inference:ApiKey", "test-key-123");
            builder.UseSetting("Inference:WorkDirectory", Path.Combine(Path.GetTempPath(), "imageto3d-inference-tests"));
        }
    }
}
