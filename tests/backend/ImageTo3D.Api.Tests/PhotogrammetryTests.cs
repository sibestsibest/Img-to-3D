using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ImageTo3D.Api.Tests;

/// <summary>Phase 3: Meshroom photogrammetry with the process replaced by <see cref="FakeProcessRunner"/>.</summary>
public class PhotogrammetryTests
{
    /// <summary>A textured photo (random blocks, so it is sharp and photos differ from each other).</summary>
    internal static byte[] Photo(int seed, int w = 1200, int h = 900, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            var rng = new Random(seed);
            using var paint = new SKPaint();
            for (var y = 0; y < h; y += 30)
            {
                for (var x = 0; x < w; x += 30)
                {
                    paint.Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    canvas.DrawRect(x, y, 30, 30, paint);
                }
            }
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static ApiFactory Factory(bool meshroom = true, Dictionary<string, string?>? extra = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "imageto3d-tests", "meshroom-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?>(extra ?? []);
        if (meshroom)
            settings["Photogrammetry:MeshroomPath"] = FakeProcessRunner.CreateInstall(root);
        return new ApiFactory(settings);
    }

    private static async Task<PhotogrammetryJobDto> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PhotogrammetryJobDto>(ApiFactory.Json))!;
    }

    private static async Task<PhotogrammetryJobDto> CreateAsync(HttpClient client, int photos = 3)
    {
        var job = await Read(await client.PostAsJsonAsync("/api/photogrammetry", new { name = "Shoulder armor" }));
        return photos == 0 ? job : await AddAsync(client, job.Id, Enumerable.Range(1, photos).Select(i => Photo(i)).ToArray());
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid id, params byte[][] photos)
    {
        using var form = new MultipartFormDataContent();
        for (var i = 0; i < photos.Length; i++)
            form.Add(new ByteArrayContent(photos[i]), "photos", $"IMG_{i + 1:D4}.JPG");
        return await client.PostAsync($"/api/photogrammetry/{id}/photos", form);
    }

    private static async Task<PhotogrammetryJobDto> AddAsync(HttpClient client, Guid id, params byte[][] photos) => await Read(await UploadAsync(client, id, photos));

    private static async Task<PhotogrammetryJobDto> WaitAsync(HttpClient client, Guid id, Func<PhotogrammetryJobDto, bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var job = (await client.GetFromJsonAsync<PhotogrammetryJobDto>($"/api/photogrammetry/{id}", ApiFactory.Json))!;
            if (done(job))
                return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Job stayed {job.Status} ({job.ErrorCode} {job.Error})");
            await Task.Delay(50);
        }
    }

    private static Task<PhotogrammetryJobDto> WaitTerminalAsync(HttpClient client, Guid id) =>
        WaitAsync(client, id, j => j.Status is "succeeded" or "failed" or "cancelled");

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Fact]
    public async Task WithoutMeshroom_EverythingSaysWhyAndPhotosCanStillBeCollected()
    {
        using var factory = Factory(meshroom: false);
        var client = factory.CreateClient();

        var engine = await client.GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json);
        Assert.Equal("missing", engine!.Status);
        Assert.Contains("MeshroomPath", engine.Reason);
        var caps = await client.GetFromJsonAsync<CapabilitiesDto>("/api/capabilities", ApiFactory.Json);
        Assert.Equal(("missing", "photogrammetry"), (caps!.Engines.Single(e => e.Id == "meshroom").Status, caps.Engines.Single(e => e.Id == "meshroom").Origin));

        var job = await CreateAsync(client);
        Assert.Equal(("draft", 3, true), (job.Status, job.Photos.Count, job.CanStart));
        var start = await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal("meshroom_unavailable", await CodeAsync(start));
        Assert.Empty(factory.Processes.Calls);
    }

    [Fact]
    public async Task Engine_ListsInstalledAllowedPipelinesAndPicksTheDraftWithoutCuda()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var engine = (await client.GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json))!;
        Assert.Equal(("ready", "2025.1.0", false), (engine.Status, engine.Version, engine.Cuda));
        Assert.Equal(["photogrammetry", "photogrammetryDraft", "photogrammetryObject"], engine.Pipelines); // cameraTracking is not offered
        Assert.Equal("photogrammetryDraft", engine.DefaultPipeline);
        Assert.Contains(engine.Notes, n => n.Contains("CUDA"));
        Assert.Contains(factory.Processes.Calls, c => c.Arguments is ["--help"]);
    }

    [Fact]
    public async Task Engine_UsesTheFullPipelineWithACudaGpu()
    {
        using var factory = Factory();
        factory.Processes.Gpu = "NVIDIA GeForce RTX 3060, 8.6";
        var engine = (await factory.CreateClient().GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json))!;
        Assert.Equal((true, "NVIDIA GeForce RTX 3060", "photogrammetry"), (engine.Cuda, engine.Gpu, engine.DefaultPipeline));
    }

    [Theory]
    [InlineData("usage: meshroom_batch -i -p -o", 0, "--cache")]
    [InlineData(FakeProcessRunner.Help, 1, "did not start")]
    public async Task Engine_RefusesAnIncompatibleOrBrokenInstallation(string help, int exit, string reason)
    {
        using var factory = Factory();
        factory.Processes.HelpText = help;
        factory.Processes.HelpExitCode = exit;
        var engine = (await factory.CreateClient().GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json))!;
        Assert.Equal("failed", engine.Status);
        Assert.Contains(reason, engine.Reason);
    }

    [Fact]
    public async Task Engine_ReportsAWrongFolder()
    {
        using var factory = Factory(meshroom: false, new() { ["Photogrammetry:MeshroomPath"] = Path.GetTempPath() + Guid.NewGuid().ToString("N") });
        var engine = (await factory.CreateClient().GetFromJsonAsync<PhotogrammetryEngineDto>("/api/photogrammetry/engine", ApiFactory.Json))!;
        Assert.Equal("missing", engine.Status);
        Assert.Contains("meshroom_batch was not found", engine.Reason);
    }

    [Fact]
    public async Task Photos_AreCheckedStoredUnchangedAndWarnedAbout()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var job = await CreateAsync(client, 0);

        var original = Photo(1);
        job = await AddAsync(client, job.Id, original, Photo(2, 800, 600), Photo(1));
        Assert.Equal(["IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.JPG"], job.Photos.Select(p => p.OriginalName));
        Assert.Equal((1200, 900, "jpg"), (job.Photos[0].Width, job.Photos[0].Height, job.Photos[0].Format));
        Assert.Contains(job.Findings, f => f.Code == "few_photos");
        Assert.Contains(job.Findings, f => f.Code == "low_resolution" && f.PhotoId == job.Photos[1].Id);
        Assert.Contains(job.Findings, f => f.Code == "near_duplicate" && f.PhotoId == job.Photos[2].Id);
        Assert.Contains(job.Findings, f => f.Code == "no_camera_metadata");
        Assert.True(job.CanStart);

        var thumb = await client.GetAsync(job.Photos[0].ThumbnailUrl);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);

        // Stored byte for byte (EXIF must reach Meshroom).
        var stored = Directory.GetFiles(Path.Combine(factory.Root, "photogrammetry", job.Id.ToString("N")), "p*.jpg");
        Assert.Contains(stored, f => File.ReadAllBytes(f).SequenceEqual(original));

        job = await Read(await client.DeleteAsync($"/api/photogrammetry/{job.Id}/photos/{job.Photos[2].Id}"));
        Assert.Equal(2, job.Photos.Count);
        Assert.False(job.CanStart);
        Assert.Contains(job.Findings, f => f.Code == "too_few_photos" && f.Severity == "blocker");
        var start = await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { });
        Assert.Equal(("photo_set_blocked", HttpStatusCode.Conflict), (await CodeAsync(start), start.StatusCode));
    }

    [Fact]
    public async Task Photos_BadFilesRefuseTheWholeBatch()
    {
        using var factory = Factory(extra: new() { ["Photogrammetry:MaxPhotos"] = "4" });
        var client = factory.CreateClient();
        var job = await CreateAsync(client, 0);

        var text = await UploadAsync(client, job.Id, Photo(1), "not an image"u8.ToArray());
        Assert.Equal(("invalid_photo", HttpStatusCode.BadRequest), (await CodeAsync(text), text.StatusCode));
        var webp = await UploadAsync(client, job.Id, Photo(1, format: SKEncodedImageFormat.Webp));
        Assert.Equal("invalid_photo", await CodeAsync(webp));
        Assert.Empty((await client.GetFromJsonAsync<PhotogrammetryJobDto>($"/api/photogrammetry/{job.Id}", ApiFactory.Json))!.Photos);

        var tooMany = await UploadAsync(client, job.Id, Photo(1), Photo(2), Photo(3), Photo(4), Photo(5));
        Assert.Equal("too_many_photos", await CodeAsync(tooMany));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/photogrammetry/{job.Id}/photos", new MultipartFormDataContent { { new StringContent("x"), "other" } })).StatusCode);
    }

    [Fact]
    public async Task Run_ReconstructsAndExportsTheMesh()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var job = await CreateAsync(client);

        job = await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { }));
        Assert.Equal(("queued", "photogrammetryDraft", "2025.1.0"), (job.Status, job.Pipeline, job.EngineVersion));
        job = await WaitTerminalAsync(client, job.Id);
        Assert.True(job.Status == "succeeded", $"{job.ErrorCode}: {job.Error}");

        // Only generated paths and the pipeline reach the command line.
        var run = factory.Processes.Calls.Last();
        var work = Path.Combine(factory.Root, "photogrammetry-work", job.Id.ToString("N"));
        Assert.Equal(
            ["--input", Path.Combine(work, "images"), "--pipeline", "photogrammetryDraft", "--output", Path.Combine(work, "output"), "--cache", Path.Combine(work, "cache"), "--verbose", "info"],
            run.Arguments);
        Assert.Equal(["photo_0001.jpg", "photo_0002.jpg", "photo_0003.jpg"], factory.Processes.InputFiles.Single());
        Assert.False(Directory.Exists(work)); // work folder removed

        Assert.Equal((8, 12, true, 1), (job.Mesh!.Vertices, job.Mesh.Triangles, job.Mesh.Closed, job.Mesh.Components));
        Assert.True(job.Progress.NodesDone >= 5);
        Assert.Null(job.Progress.CurrentNode);
        Assert.Equal(("uncalibrated", 100.0), (job.Scale.Status, job.DimensionsMm!.X));
        Assert.Equal(25.0, job.DimensionsMm.Z, 3);
        Assert.Equal((true, true, true, true), (job.Downloads.Glb, job.Downloads.Stl, job.Downloads.Zip, job.Downloads.Log));
        Assert.Equal(["texture_1001.png", "texturedMesh.mtl", "texturedMesh.obj"], job.Outputs.Select(o => o.Path));
        Assert.Equal("photogrammetry", job.Origin);

        var glb = await client.GetByteArrayAsync($"/api/photogrammetry/{job.Id}/preview");
        Assert.Equal(0.1, MeshOperations.ComputeBounds(GlbReader.Read(glb)).Size.X, 4); // metres
        var stl = StlReader.ReadBinary(await client.GetByteArrayAsync($"/api/photogrammetry/{job.Id}/download?format=stl"));
        Assert.Equal(100, MeshOperations.ComputeBounds(stl).Size.X, 3);
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"/api/photogrammetry/{job.Id}/download?format=zip"))))
            Assert.Equal(["texture_1001.png", "texturedMesh.mtl", "texturedMesh.obj"], zip.Entries.Select(e => e.FullName).Order());
        Assert.Contains("Meshroom finished", await client.GetStringAsync($"/api/photogrammetry/{job.Id}/download?format=log"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/photogrammetry/{job.Id}/download?format=exe")).StatusCode);
    }

    [Fact]
    public async Task Scale_IsCalibratedFromOneMeasuredSide()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { }));
        await WaitTerminalAsync(client, job.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/photogrammetry/{job.Id}/scale", new { axis = "W", targetMm = 10 })).StatusCode);
        job = await Read(await client.PutAsJsonAsync($"/api/photogrammetry/{job.Id}/scale", new { axis = "Y", targetMm = 40 }));
        Assert.Equal(("calibrated", "Y", 40.0), (job.Scale.Status, job.Scale.Axis, job.Scale.TargetMm));
        Assert.Equal((80.0, 40.0, 20.0), (job.DimensionsMm!.X, job.DimensionsMm.Y, job.DimensionsMm.Z));
        var stl = StlReader.ReadBinary(await client.GetByteArrayAsync($"/api/photogrammetry/{job.Id}/download?format=stl"));
        Assert.Equal(40, MeshOperations.ComputeBounds(stl).Size.Z, 3); // Y-up model, Z-up STL

        var design = await client.PostAsJsonAsync("/api/part-designs/photogrammetry", new { jobId = job.Id });
        Assert.Equal(HttpStatusCode.Created, design.StatusCode);
        var project = (await design.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
        Assert.Equal(("photogrammetry", "photogrammetry", "Shoulder armor"), (project.Source.Kind, project.Origin, project.Name));
        Assert.Contains(project.Issues, i => i.Code == "scale_calibrated");
        Assert.Equal(80, project.Parts.Single().Geometry.Bounds.Size.X, 3);
    }

    [Theory]
    [InlineData(FakeMeshroomMode.Fail, "meshroom_failed")]
    [InlineData(FakeMeshroomMode.Timeout, "timeout")]
    [InlineData(FakeMeshroomMode.NoOutput, "no_output_mesh")]
    [InlineData(FakeMeshroomMode.BadObj, "invalid_output_mesh")]
    public async Task Run_FailuresAreReportedWithTheReasonAndLog(FakeMeshroomMode mode, string code)
    {
        using var factory = Factory();
        factory.Processes.Mode = mode;
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { }));
        job = await WaitTerminalAsync(client, job.Id);

        Assert.Equal(("failed", code), (job.Status, job.ErrorCode));
        Assert.False(job.Downloads.Glb);
        Assert.True(job.Downloads.Log);
        if (mode == FakeMeshroomMode.Fail)
        {
            Assert.Contains("exit code 1", job.Error);
            Assert.Contains("not enough matched views", await client.GetStringAsync($"/api/photogrammetry/{job.Id}/download?format=log"));
        }

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/photogrammetry/{job.Id}/download?format=glb")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "photogrammetry-work", job.Id.ToString("N"))));
    }

    [Fact]
    public async Task Run_FallsBackToTheMeshInTheCache()
    {
        using var factory = Factory();
        factory.Processes.Mode = FakeMeshroomMode.CacheOnly;
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { }));
        job = await WaitTerminalAsync(client, job.Id);
        Assert.Equal("succeeded", job.Status);
        Assert.Equal("(cache) mesh.obj", Assert.Single(job.Outputs).Path);
    }

    [Fact]
    public async Task Running_ShowsProgressAndCanBeCancelled()
    {
        using var factory = Factory();
        factory.Processes.Gate = new TaskCompletionSource();
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { pipeline = "photogrammetryObject" }));

        var running = await WaitAsync(client, job.Id, j => j.Progress.CurrentNode == "DepthMap");
        Assert.Equal(("running", 2), (running.Status, running.Progress.NodesDone));
        Assert.Contains("photogrammetryObject", factory.Processes.Calls.Last().Arguments);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, job.Id, Photo(9))).StatusCode);

        await Read(await client.PostAsync($"/api/photogrammetry/{job.Id}/cancel", null));
        var cancelled = await WaitTerminalAsync(client, job.Id);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(1, factory.Processes.CancelledRuns);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/photogrammetry/{job.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Deleting_ARunningJobStopsMeshroomAndRemovesEverything()
    {
        using var factory = Factory();
        factory.Processes.Gate = new TaskCompletionSource();
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        await Read(await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { }));
        await WaitAsync(client, job.Id, j => j.Status == "running");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/photogrammetry/{job.Id}")).StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (factory.Processes.CancelledRuns == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(1, factory.Processes.CancelledRuns);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/photogrammetry/{job.Id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "photogrammetry", job.Id.ToString("N"))));
    }

    [Fact]
    public async Task Start_RefusesPipelinesTheInstallationDoesNotOffer()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var job = await CreateAsync(client);
        foreach (var pipeline in new[] { "cameraTracking", "photogrammetry --forceCompute", "../evil" })
        {
            var response = await client.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { pipeline });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("pipeline", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }

        Assert.DoesNotContain(factory.Processes.Calls, c => c.Arguments.Contains("--input"));
    }

    [Fact]
    public async Task Jobs_AreIsolatedPerBrowser()
    {
        using var factory = Factory();
        var job = await CreateAsync(factory.CreateClient());
        var stranger = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/photogrammetry/{job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(job.Photos[0].ThumbnailUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/photogrammetry/{job.Id}/start", new { })).StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<PagedResult<PhotogrammetrySummaryDto>>("/api/photogrammetry", ApiFactory.Json))!.Items);
    }

    [Fact]
    public async Task ReferenceSearch_MultiViewRouteRunsPhotogrammetryOnTheSameObjectPictures()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var session = await ReferenceSearchTests.CreateSessionAsync(client, image: Photo(100, 640, 480));

        async Task AddPictureAsync(int seed)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Photo(seed, 640, 480)), "image", $"view-{seed}.jpg");
            form.Add(new StringContent("own_work"), "license");
            var s = await ReferenceSearchTests.ReadAsync(await client.PostAsync($"/api/references/{session.Id}/candidates", form));
            var candidate = s.Candidates.OrderByDescending(c => c.RetrievedAt).First();
            await ReferenceSearchTests.ReadAsync(await client.PutAsJsonAsync($"/api/references/{session.Id}/candidates/{candidate.Id}", new { decision = "accepted", relation = "same_object" }));
        }

        for (var i = 1; i <= 6; i++)
            await AddPictureAsync(i);
        var plan = (await client.GetFromJsonAsync<ReconstructionDto>($"/api/references/{session.Id}/reconstruction", ApiFactory.Json))!;
        var multi = plan.Routes.Single(r => r.Route == "multi_view_reconstruction");
        Assert.Equal("unavailable", multi.Availability);
        Assert.Contains(multi.Reasons, r => r.Code == "too_few_views");

        await AddPictureAsync(7);
        plan = (await client.GetFromJsonAsync<ReconstructionDto>($"/api/references/{session.Id}/reconstruction", ApiFactory.Json))!;
        multi = plan.Routes.Single(r => r.Route == "multi_view_reconstruction");
        Assert.Equal(("available", 8), (multi.Availability, multi.ExtraPhotos));
        Assert.Equal("multi_view_reconstruction", plan.Recommended);

        var started = await client.PostAsJsonAsync($"/api/references/{session.Id}/reconstruction", new { route = "multi_view_reconstruction" });
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        var reconstruction = (await started.Content.ReadFromJsonAsync<ReconstructionDto>(ApiFactory.Json))!;
        Assert.Equal("multi_view_reconstruction", reconstruction.State);
        var jobId = reconstruction.JobId!.Value;

        var job = await WaitTerminalAsync(client, jobId);
        Assert.Equal(("succeeded", 8, session.Id), (job.Status, job.Photos.Count, job.SourceReferenceSessionId!.Value));
        Assert.Equal(0, factory.Inference.Submissions);
        plan = (await client.GetFromJsonAsync<ReconstructionDto>($"/api/references/{session.Id}/reconstruction", ApiFactory.Json))!;
        Assert.Equal(("completed", "succeeded"), (plan.State, plan.JobStatus));
    }

    [Fact]
    public async Task Restart_FailsAnInterruptedRunAndRequeuesQueuedJobs()
    {
        var db = Path.Combine(Path.GetTempPath(), "imageto3d-tests", "restart-" + Guid.NewGuid().ToString("N"), "test.db");
        var photos = Path.Combine(Path.GetDirectoryName(db)!, "photogrammetry");
        var settings = new Dictionary<string, string?> { ["Storage:DatabasePath"] = db, ["Storage:PhotogrammetryRootPath"] = photos };
        Guid running, queued;
        using (var first = Factory(meshroom: false, settings))
        {
            var client = first.CreateClient();
            running = (await CreateAsync(client)).Id;
            queued = (await CreateAsync(client)).Id;
            using var scope = first.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await context.PhotogrammetryJobs.FindAsync(running))!.Status = PhotogrammetryStatuses.Running;
            var q = (await context.PhotogrammetryJobs.FindAsync(queued))!;
            q.Status = PhotogrammetryStatuses.Queued;
            q.Pipeline = "photogrammetryDraft";
            await context.SaveChangesAsync();
        }

        using var second = Factory(meshroom: true, settings);
        second.CreateClient();
        using var check = second.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var states = after.PhotogrammetryJobs.AsEnumerable().ToDictionary(j => j.Id, j => (j.Status, j.ErrorCode));
            if (states[queued].Status == "succeeded")
            {
                Assert.Equal(("failed", "interrupted"), states[running]);
                return;
            }

            foreach (var e in after.ChangeTracker.Entries().ToList())
                e.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            await Task.Delay(100);
        }

        Assert.Fail("The queued job was not run after the restart.");
    }
}
