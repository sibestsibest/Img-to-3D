using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;
using SkiaSharp;
using static ImageTo3D.Api.Tests.ReferenceSearchTests;

namespace ImageTo3D.Api.Tests;

/// <summary>Phase 6: the reconstruction decision of a reference session and the routes it starts.</summary>
public class ReconstructionTests
{
    private static byte[] Photo() => ReferenceRankingTests.Solid(new SKColor(200, 40, 40));

    private static async Task<ReconstructionDto> PlanAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync($"/api/references/{sessionId}/reconstruction");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReconstructionDto>(ApiFactory.Json))!;
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid sessionId, string route, Guid? modelId = null) =>
        client.PostAsJsonAsync($"/api/references/{sessionId}/reconstruction", new { route, modelId });

    private static async Task<ReconstructionDto> StartOkAsync(HttpClient client, Guid sessionId, string route)
    {
        var response = await StartAsync(client, sessionId, route);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReconstructionDto>(ApiFactory.Json))!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response) => (await response.ProblemAsync()).GetProperty("code").GetString()!;

    private static async Task<ModelCandidateDto> UploadModelAsync(HttpClient client, Guid sessionId, byte[] bytes, string name, string license = "cc-by")
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", name },
            { new StringContent(license), "license" },
            { new StringContent("Kit Maker"), "author" },
        };
        var session = await ReadAsync(await client.PostAsync($"/api/references/{sessionId}/models", form));
        return session.Models.Single(m => m.Title == Path.GetFileNameWithoutExtension(name));
    }

    private static async Task AcceptModelAsync(HttpClient client, Guid sessionId, Guid modelId) =>
        await ReadAsync(await client.PutAsJsonAsync($"/api/references/{sessionId}/models/{modelId}", new { decision = "accepted" }));

    private static async Task<ReferenceCandidateDto> AddSameObjectPictureAsync(HttpClient client, Guid sessionId, byte[] image, string relation = "same_object")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "back-view.png");
        form.Add(new StringContent("own_work"), "license");
        var session = await ReadAsync(await client.PostAsync($"/api/references/{sessionId}/candidates", form));
        var candidate = session.Candidates.OrderByDescending(c => c.RetrievedAt).First();
        await ReadAsync(await client.PutAsJsonAsync($"/api/references/{sessionId}/candidates/{candidate.Id}", new { decision = "accepted", relation }));
        return candidate;
    }

    private static async Task<PartDesignProjectDto> DesignAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PartDesignProjectDto>($"/api/part-designs/{id}", ApiFactory.Json))!;

    [Fact]
    public async Task Plan_ListsEveryRouteAndOffersNoMultiViewWithoutMeshroom()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());

        var plan = await PlanAsync(client, session.Id);

        Assert.Equal("reference_search", plan.State);
        Assert.Null(plan.Route);
        Assert.Equal(
            ["reuse_existing_model", "multi_view_reconstruction", "hybrid_reconstruction", "single_image_reconstruction", "manual_geometry_required"],
            plan.Routes.Select(r => r.Route));
        Assert.Equal("unsupported", plan.Routes.Single(r => r.Route == "multi_view_reconstruction").Availability);
        Assert.Equal("single_image_reconstruction", plan.Recommended);
        Assert.True(plan.Routes.Single(r => r.Route == "single_image_reconstruction").Recommended);
        Assert.True(plan.EngineReachable);

        Assert.Contains(plan.Routes.Single(r => r.Route == "multi_view_reconstruction").Reasons, r => r.Code == "no_multiview_engine" && r.Message.Contains("Meshroom"));
        Assert.Equal("route_unavailable", await ProblemCodeAsync(await StartAsync(client, session.Id, "multi_view_reconstruction")));
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(client, session.Id, "teleport")).StatusCode);
        Assert.Equal(0, factory.Inference.Submissions);
    }

    [Fact]
    public async Task Reuse_CreatesAPartDesignFromTheAcceptedModelWithoutInference()
    {
        using var factory = new ApiFactory();
        factory.Inference.Engine = "StableFast3D";
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        var model = await UploadModelAsync(client, session.Id, FakeInferenceClient.SampleGlb(), "kit-part.glb");

        Assert.Equal("model_candidate_review", (await PlanAsync(client, session.Id)).State); // the upload is pending review
        await AcceptModelAsync(client, session.Id, model.Id);
        var plan = await PlanAsync(client, session.Id);
        Assert.Equal("reuse_existing_model", plan.Recommended);
        Assert.Equal(model.Id, plan.Routes[0].ModelId);
        Assert.Contains(plan.Routes.Single(r => r.Route == "single_image_reconstruction").Reasons, r => r.Code == "existing_model_accepted");

        // Generating is refused while a reusable model is accepted: no heavy inference.
        Assert.Equal("route_unavailable", await ProblemCodeAsync(await StartAsync(client, session.Id, "single_image_reconstruction")));

        var started = await StartOkAsync(client, session.Id, "reuse_existing_model");
        Assert.Equal("completed", started.State);
        Assert.Equal(model.Id, started.ModelId);
        Assert.Null(started.JobId);
        Assert.Equal(0, factory.Inference.Submissions);
        Assert.Empty((await client.GetFromJsonAsync<PagedResult<JobDto>>("/api/models", ApiFactory.Json))!.Items);

        var design = await DesignAsync(client, started.DesignId!.Value);
        Assert.Equal("reference_model", design.Source.Kind);
        Assert.Null(design.SourceJobId);
        Assert.Equal(session.Id, design.Source.ReferenceSessionId);
        Assert.Equal(model.Id, design.Source.ModelCandidateId);
        Assert.Equal("cc-by", design.Source.License);
        Assert.Contains("Kit Maker", design.Source.Attribution);
        Assert.Equal(1000, design.MmPerModelUnit); // glTF is in metres
        var part = Assert.Single(design.Parts);
        Assert.Equal(500, part.Geometry.Bounds.Size.X, 1);
        Assert.Equal(1000, part.Geometry.Bounds.Size.Y, 1);
        Assert.Contains(design.Issues, i => i.Code == "reused_model");
        Assert.True(File.Exists(Path.Combine(factory.Root, "designs", design.Id.ToString("N"), "source.glb")));

        // The package names the source and its licence.
        var manifest = await client.GetFromJsonAsync<ExportManifestDto>($"/api/part-designs/{design.Id}/export/manifest", ApiFactory.Json);
        Assert.Equal("reference_model", manifest!.Project.Source.Kind);
    }

    [Fact]
    public async Task Reuse_OfAnStlAssumesMillimetresAndConvertsZUp()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, query: "tank hatch");
        // 40 x 30 x 10 mm block, Z-up as STL tools write it.
        var stl = StlWriter.Write(MeshOperations.Scale(GlbReader.Read(FakeInferenceClient.PlateGlb()), 40));
        var model = await UploadModelAsync(client, session.Id, stl, "hatch.stl");
        await AcceptModelAsync(client, session.Id, model.Id);

        var plan = await PlanAsync(client, session.Id);
        Assert.Contains(plan.Routes[0].Reasons, r => r.Code == "model_unitless");
        var design = await DesignAsync(client, (await StartOkAsync(client, session.Id, "reuse_existing_model")).DesignId!.Value);

        Assert.Equal(1, design.MmPerModelUnit);
        Assert.Contains(design.Issues, i => i.Code == "scale_assumed_mm");
        var size = Assert.Single(design.Parts).Geometry.Bounds.Size;
        Assert.Equal((40.0, 20.0, 0.8), (Math.Round(size.X, 2), Math.Round(size.Y, 2), Math.Round(size.Z, 2))); // file Z (20 mm) is now up
    }

    [Fact]
    public async Task Hybrid_SendsAcceptedSameObjectPicturesAsExtraPhotos()
    {
        using var factory = new ApiFactory();
        factory.Inference.Engine = "StableFast3D";
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        await AddSameObjectPictureAsync(client, session.Id, ReferenceRankingTests.Solid(new SKColor(20, 160, 40), 320, 240));
        await AddSameObjectPictureAsync(client, session.Id, ReferenceRankingTests.Solid(new SKColor(20, 40, 200), 320, 240), relation: "variant");

        var plan = await PlanAsync(client, session.Id);
        Assert.Equal("hybrid_reconstruction", plan.Recommended);
        Assert.Equal(1, plan.Routes.Single(r => r.Route == "hybrid_reconstruction").ExtraPhotos); // the variant does not count

        var started = await StartOkAsync(client, session.Id, "hybrid_reconstruction");
        Assert.Equal("hybrid_reconstruction", started.State);
        Assert.Single(started.ExtraReferenceIds);
        Assert.NotNull(started.JobId);

        // A second start while it runs is refused.
        Assert.Equal("reconstruction_in_progress", await ProblemCodeAsync(await StartAsync(client, session.Id, "single_image_reconstruction")));

        var job = await client.WaitForTerminalAsync(started.JobId!.Value);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var extras = Assert.Single(factory.Inference.SubmittedExtras);
        Assert.Equal("auto", Assert.Single(extras).View);

        var done = await PlanAsync(client, session.Id);
        Assert.Equal("completed", done.State);
        Assert.Equal("Succeeded", done.JobStatus);
    }

    [Fact]
    public async Task Hybrid_IsUnavailableWithAnEngineThatIgnoresExtraPhotos()
    {
        using var factory = new ApiFactory();
        factory.Inference.Engine = "TripoSR";
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        await AddSameObjectPictureAsync(client, session.Id, ReferenceRankingTests.Solid(new SKColor(20, 160, 40)));

        var plan = await PlanAsync(client, session.Id);
        Assert.Contains(plan.Routes.Single(r => r.Route == "hybrid_reconstruction").Reasons, r => r.Code == "engine_ignores_extras");
        Assert.Equal("route_unavailable", await ProblemCodeAsync(await StartAsync(client, session.Id, "hybrid_reconstruction")));
        Assert.Equal(0, factory.Inference.Submissions);
    }

    [Fact]
    public async Task SingleImage_FollowsTheJobToFailure()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Fail;
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());

        var started = await StartOkAsync(client, session.Id, "single_image_reconstruction");
        Assert.Equal("single_image_reconstruction", started.State);
        Assert.Empty(started.ExtraReferenceIds);
        var job = await client.WaitForTerminalAsync(started.JobId!.Value);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith(session.Name, job.OriginalFileName);

        var failed = await PlanAsync(client, session.Id);
        Assert.Equal("failed", failed.State);
        Assert.Equal("No foreground object was detected in the image.", failed.Message);
        Assert.Empty(factory.Inference.SubmittedExtras);

        // A failed attempt can be retried.
        factory.Inference.Mode = FakeMode.Succeed;
        var retry = await StartOkAsync(client, session.Id, "single_image_reconstruction");
        Assert.NotEqual(started.JobId, retry.JobId);
        Assert.Null(retry.Message);
    }

    [Fact]
    public async Task DeletedRunningJob_IsReportedAsFailed()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.Hang;
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        var started = await StartOkAsync(client, session.Id, "single_image_reconstruction");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/models/{started.JobId}")).StatusCode);

        var plan = await PlanAsync(client, session.Id);
        Assert.Equal("failed", plan.State);
        Assert.Equal("The generation job no longer exists.", plan.Message);
    }

    [Fact]
    public async Task NoPhoto_RecommendsManualGeometryAndCreatesAnEmptyDesign()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, query: "custom bracket");

        var plan = await PlanAsync(client, session.Id);
        Assert.Equal("manual_geometry_required", plan.Recommended);
        Assert.Equal("route_unavailable", await ProblemCodeAsync(await StartAsync(client, session.Id, "single_image_reconstruction")));

        var started = await StartOkAsync(client, session.Id, "manual_geometry_required");
        Assert.Equal("manual_geometry_required", started.State);
        var design = await DesignAsync(client, started.DesignId!.Value);
        Assert.Equal("none", design.Source.Kind);
        Assert.Empty(design.Parts);
        Assert.Equal("custom bracket", design.Name);
    }

    [Fact]
    public async Task Reconstruction_IsIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        var stranger = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/references/{session.Id}/reconstruction")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(stranger, session.Id, "manual_geometry_required")).StatusCode);
        Assert.Equal(0, factory.Inference.Submissions);
    }

    [Fact]
    public async Task Reuse_RefusesAModelThatIsNotAcceptedOrHasNoFile()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var session = await CreateSessionAsync(client, image: Photo());
        var accepted = await UploadModelAsync(client, session.Id, FakeInferenceClient.SampleGlb(), "a.glb");
        await AcceptModelAsync(client, session.Id, accepted.Id);
        var pending = await UploadModelAsync(client, session.Id, FakeInferenceClient.PlateGlb(), "b.glb");

        Assert.Equal("model_not_reusable", await ProblemCodeAsync(await StartAsync(client, session.Id, "reuse_existing_model", pending.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(client, session.Id, "reuse_existing_model", Guid.NewGuid())).StatusCode);
    }
}
