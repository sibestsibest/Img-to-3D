using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.Photogrammetry;
using ImageTo3D.Api.Services.Processes;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageTo3D.Api.Tests;

/// <summary>
/// Runs only when a real Meshroom and a photo set are given: MESHROOM_TEST_PATH (installation folder)
/// and MESHROOM_TEST_PHOTOS (folder of JPEG/PNG photos of one object). Takes minutes to hours.
/// </summary>
public sealed class RequiresMeshroomFactAttribute : FactAttribute
{
    public RequiresMeshroomFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESHROOM_TEST_PATH")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MESHROOM_TEST_PHOTOS")))
            Skip = "Set MESHROOM_TEST_PATH and MESHROOM_TEST_PHOTOS to run against a real Meshroom installation.";
    }
}

[Trait("Category", "RealModel")]
public class MeshroomRealTests
{
    [RequiresMeshroomFact]
    public async Task RealMeshroom_ReconstructsThePhotoSet()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Photogrammetry:MeshroomPath"] = Environment.GetEnvironmentVariable("MESHROOM_TEST_PATH"),
            ["Photogrammetry:ProgressInterval"] = "00:00:05",
        };
        using var factory = new ApiFactory(settings).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            // The real process runner instead of the fake one.
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(new ProcessRunner(NullLogger<ProcessRunner>.Instance));
        }));
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);

        var engine = (await client.GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json))!;
        Assert.True(engine.Status == "ready", engine.Reason);

        var job = (await (await client.PostAsJsonAsync("/api/photogrammetry", new { name = "Real Meshroom test" })).Content.ReadFromJsonAsync<PhotogrammetryJobDto>(ApiFactory.Json))!;
        var files = Directory.GetFiles(Environment.GetEnvironmentVariable("MESHROOM_TEST_PHOTOS")!).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        foreach (var chunk in files.Chunk(10))
        {
            using var form = new MultipartFormDataContent();
            foreach (var f in chunk)
                form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(f)), "photos", Path.GetFileName(f));
            Assert.True((await client.PostAsync($"/api/photogrammetry/{job.Id}/photos", form)).IsSuccessStatusCode);
        }

        Assert.True((await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { })).IsSuccessStatusCode);
        var deadline = DateTime.UtcNow.AddHours(6);
        while (true)
        {
            job = (await client.GetFromJsonAsync<PhotogrammetryJobDto>($"/api/photogrammetry/{job.Id}", ApiFactory.Json))!;
            if (job.Status is "succeeded" or "failed" or "cancelled" || DateTime.UtcNow > deadline)
                break;
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        Assert.True(job.Status == "succeeded", $"{job.ErrorCode}: {job.Error}");
        Assert.True(job.Mesh!.Triangles > 100);
        Assert.Contains(job.Outputs, o => o.Path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase));
    }
}
