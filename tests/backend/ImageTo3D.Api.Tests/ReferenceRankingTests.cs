using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;
using SkiaSharp;
using static ImageTo3D.Api.Tests.ReferenceSearchTests;

namespace ImageTo3D.Api.Tests;

public class ReferenceRankingTests
{
    public static byte[] Solid(SKColor color, int w = 200, int h = 150)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Wikimedia answers with a red ("Gunpla box art") and a blue ("RX-78-2 front view") picture; the other sources fail.</summary>
    private static void ServeColours(FakeReferenceWeb web)
    {
        web.OnJson(WikimediaApi, WikimediaJson());
        web.OnImage("thumb.wikimedia.org/thumb/a.png", Solid(new SKColor(220, 30, 30), 1200, 900));
        web.OnImage("upload.wikimedia.org/thumb/b.png", Solid(new SKColor(30, 30, 220), 300, 200));
    }

    private static ReferenceCandidateDto Red(ReferenceSessionDto s) => s.Candidates.Single(c => c.Title == "Gunpla box art");

    private static ReferenceCandidateDto Blue(ReferenceSessionDto s) => s.Candidates.Single(c => c.Title == "RX-78-2 front view");

    [Fact]
    public async Task Search_RanksPicturesWithEmbeddingsAndExplainsTheScore()
    {
        using var factory = new ApiFactory();
        ServeColours(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client, image: Solid(new SKColor(200, 40, 40)))).Id);

        Assert.Equal("embeddings", session.Ranking.Mode);
        Assert.NotNull(session.Ranking.RankedAt);
        Assert.Null(session.Ranking.Note);
        Assert.Equal(1, Red(session).Ranking.Position);
        Assert.Equal(2, Blue(session).Ranking.Position);
        Assert.True(Red(session).Ranking.Score > Blue(session).Ranking.Score);
        Assert.True(Red(session).Ranking.InputSimilarity > 0.99);
        Assert.Contains(Red(session).Ranking.Reasons, r => r.Code == "looks_like_input" && r.Effect == "+");
        Assert.Contains(Blue(session).Ranking.Reasons, r => r.Code == "unlike_input" && r.Effect == "-");
        Assert.NotEqual(Red(session).Ranking.Group, Blue(session).Ranking.Group);
        Assert.NotNull(Red(session).Ranking.Viewpoint);
        Assert.NotNull(Red(session).Ranking.Kind);
        Assert.Equal("same_object", Red(session).Ranking.Relation!.Label);
        Assert.True(Red(session).Ranking.Relation!.Confidence <= 0.5);
        Assert.Equal(1.0, Red(session).Ranking.Confidence);
    }

    [Fact]
    public async Task Ranking_FallsBackWhenEmbeddingsAreUnavailable()
    {
        using var factory = new ApiFactory();
        factory.Embedder.Available = false;
        ServeColours(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client, image: Solid(new SKColor(200, 40, 40)))).Id);

        Assert.Equal("basic", session.Ranking.Mode);
        Assert.Equal("Fake embedder switched off.", session.Ranking.Note);
        Assert.All(session.Candidates, c => Assert.NotNull(c.Ranking.Position));
        Assert.All(session.Candidates, c => Assert.Equal(0.25, c.Ranking.Confidence));
        Assert.All(session.Candidates, c => Assert.Contains(c.Ranking.Reasons, r => r.Code == "no_visual"));
        Assert.All(session.Candidates, c => Assert.Null(c.Ranking.InputSimilarity));
        Assert.Equal(0, factory.Embedder.ImageCalls);
        // Resolution still counts: the 3000 x 2000 picture beats the 800 x 600 one.
        Assert.Equal(1, Blue(session).Ranking.Position);
    }

    [Fact]
    public async Task Ranking_FailureNeverFailsTheSearch()
    {
        using var factory = new ApiFactory();
        factory.Embedder.Fail = true;
        ServeColours(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id);

        Assert.Equal(2, session.Candidates.Count);
        Assert.Equal("basic", session.Ranking.Mode);
        Assert.Contains("Visual ranking failed", session.Ranking.Note);
    }

    [Fact]
    public async Task Embeddings_AreCachedUntilTheCacheIsCleared()
    {
        using var factory = new ApiFactory();
        ServeColours(factory.Web);
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client, image: Solid(SKColors.Red))).Id;
        await SearchAsync(client, id);
        Assert.Equal(3, factory.Embedder.ImageCalls); // two previews and the input photo

        await ReadAsync(await client.PostAsync($"/api/references/{id}/rank", null));
        Assert.Equal(3, factory.Embedder.ImageCalls);

        var cleared = await (await client.DeleteAsync("/api/references/cache")).Content.ReadFromJsonAsync<ClearReferenceCacheDto>(ApiFactory.Json);
        Assert.Equal(3, cleared!.Embeddings);
        await ReadAsync(await client.PostAsync($"/api/references/{id}/rank", null));
        Assert.Equal(6, factory.Embedder.ImageCalls);
    }

    [Fact]
    public async Task Rank_UsesAcceptedPicturesAsEvidence()
    {
        using var factory = new ApiFactory();
        ServeColours(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id); // no input photo
        var blue = Blue(session);

        await ReadAsync(await client.PutAsJsonAsync($"/api/references/{session.Id}/candidates/{blue.Id}", new { decision = "accepted" }));
        // A second blue picture added by hand is then "similar to pictures you accepted".
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Solid(new SKColor(40, 40, 210)));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "blue.png");
        var withUpload = await ReadAsync(await client.PostAsync($"/api/references/{session.Id}/candidates", form));
        var upload = withUpload.Candidates.Single(c => c.Origin == "manual");

        Assert.Contains(upload.Ranking.Reasons, r => r.Code == "like_accepted");
        Assert.Equal(Blue(withUpload).Ranking.Group, upload.Ranking.Group);
        Assert.Null(upload.Ranking.InputSimilarity);
        Assert.Equal(0.8, upload.Ranking.Confidence); // embeddings and query, but no input photo
    }

    [Fact]
    public async Task Capabilities_ReportEmbeddingsAndSources()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var caps = (await client.GetFromJsonAsync<ReferenceCapabilitiesDto>("/api/references/capabilities", ApiFactory.Json))!;
        Assert.True(caps.Embeddings.Available);
        Assert.Equal(3, caps.ImageSources.Count);
        Assert.Equal("sketchfab", Assert.Single(caps.ModelSources).Id);

        factory.Embedder.Available = false;
        caps = (await client.GetFromJsonAsync<ReferenceCapabilitiesDto>("/api/references/capabilities", ApiFactory.Json))!;
        Assert.False(caps.Embeddings.Available);
        Assert.NotNull(caps.Embeddings.Reason);
    }
}

public class ModelCandidateTests
{
    internal const string SketchfabApi = "api.sketchfab.com/v3/search";

    private const string SketchfabJson = """
        {"results":[
          {"uid":"aaa111","name":"Gundam RX-78","viewerUrl":"https://sketchfab.com/3d-models/gundam-aaa111","user":{"displayName":"Modeler","username":"modeler"},
           "license":{"label":"CC Attribution-NonCommercial"},"isDownloadable":true,"faceCount":50000,"vertexCount":26000,"isAgeRestricted":false,
           "archives":{"glb":{"size":1234567}},
           "thumbnails":{"images":[{"width":1024,"url":"https://media.sketchfab.com/models/aaa111/1024.jpeg"},{"width":256,"url":"https://media.sketchfab.com/models/aaa111/256.jpeg"},{"width":64,"url":"https://media.sketchfab.com/models/aaa111/64.jpeg"}]}},
          {"uid":"bbb222","name":"Locked statue","license":{"label":"Standard"},"isDownloadable":false,"faceCount":900000,
           "thumbnails":{"images":[{"width":256,"url":"https://evil.example.com/t.jpg"}]}},
          {"uid":"ccc333","name":"Restricted","isAgeRestricted":true}
        ]}
        """;

    private static void ServeSketchfab(FakeReferenceWeb web)
    {
        web.OnJson(SketchfabApi, SketchfabJson);
        web.OnImage("media.sketchfab.com/models/aaa111/256.jpeg", ReferenceRankingTests.Solid(new SKColor(210, 40, 40)));
    }

    private static async Task<ReferenceSessionDto> SearchModelsAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync($"/api/references/{id}/models/search", new { });
        return await ReadAsync(response);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid id, byte[] bytes, string name = "kit-part.glb", Dictionary<string, string>? fields = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        foreach (var (k, v) in fields ?? [])
            form.Add(new StringContent(v), k);
        return await client.PostAsync($"/api/references/{id}/models", form);
    }

    [Fact]
    public async Task Search_ListsModelsWithLicenceAndListingOnlyEvaluation()
    {
        using var factory = new ApiFactory();
        ServeSketchfab(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchModelsAsync(client, (await CreateSessionAsync(client, image: ReferenceRankingTests.Solid(SKColors.Red))).Id);

        var outcome = Assert.Single(session.LastModelSearch);
        Assert.Equal(("sketchfab", "ok", 2, 2), (outcome.Source, outcome.Status, outcome.Hits, outcome.Added));
        Assert.Equal(2, session.Models.Count); // the age-restricted listing is skipped

        var good = session.Models.Single(m => m.Title == "Gundam RX-78");
        Assert.Equal(("search", "sketchfab", "Sketchfab"), (good.Origin, good.Source, good.SourceName));
        Assert.Equal("https://sketchfab.com/3d-models/gundam-aaa111", good.PageUrl);
        Assert.Equal("Modeler", good.Author);
        Assert.Equal("cc-by-nc", good.License.Code);
        Assert.Equal((true, 50000, 1234567L), (good.IsDownloadable!.Value, good.FaceCount!.Value, good.FileBytes!.Value));
        Assert.Equal("metadata_only", good.EvaluationStatus);
        Assert.True(good.HasThumbnail);
        Assert.True(good.VisualSimilarity > 0.95);
        Assert.Equal(0.45, good.Confidence);
        Assert.Contains(good.Reasons, r => r.Code == "listing_only");
        Assert.Contains(good.Reasons, r => r.Code == "looks_like_object");
        Assert.Equal(1, factory.Web.Count("media.sketchfab.com/models/aaa111/256.jpeg")); // the 256 px preview, not 1024

        var locked = session.Models.Single(m => m.Title == "Locked statue");
        Assert.Equal("unknown", locked.License.Code);
        Assert.Equal("Standard", locked.License.Raw);
        Assert.Equal("skipped", locked.DownloadStatus); // untrusted preview host
        Assert.Contains(locked.Reasons, r => r.Code == "not_downloadable");
        Assert.Contains(locked.Reasons, r => r.Code == "dense_mesh");
        Assert.True(good.Score > locked.Score);
        Assert.Equal(good.Id, session.Models[0].Id); // sorted by score
        Assert.DoesNotContain(factory.Web.Requests, r => r.RequestUri!.Host == "evil.example.com");
        Assert.All(factory.Web.Requests, r => Assert.Null(r.Headers.Authorization));

        // Same query again: from the cache, nothing added.
        var again = await SearchModelsAsync(client, session.Id);
        Assert.True(again.LastModelSearch[0].FromCache);
        Assert.Equal(0, again.LastModelSearch[0].Added);
        Assert.Equal(1, factory.Web.Count(SketchfabApi));
    }

    [Fact]
    public async Task Search_SourceFailuresAndTimeoutsAreReportedNotThrown()
    {
        using var factory = new ApiFactory(new() { ["ReferenceSearch:SourceTimeout"] = "00:00:00.300" });
        factory.Web.OnStatus(SketchfabApi, HttpStatusCode.InternalServerError);
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        Assert.Equal("failed", (await SearchModelsAsync(client, id)).LastModelSearch[0].Status);
        factory.Web.OnHang(SketchfabApi);
        var timedOut = await SearchModelsAsync(client, id);
        Assert.Equal("timeout", timedOut.LastModelSearch[0].Status);
        Assert.Empty(timedOut.Models);
    }

    [Fact]
    public async Task Search_CanBeDisabled()
    {
        using var factory = new ApiFactory(new() { ["ModelSearch:SketchfabEnabled"] = "false" });
        var client = factory.CreateClient();
        var session = await SearchModelsAsync(client, (await CreateSessionAsync(client)).Id);
        Assert.Equal("disabled", session.LastModelSearch[0].Status);
        Assert.Empty(factory.Web.Requests);
    }

    [Fact]
    public async Task Upload_GlbIsCheckedGeometrically()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;
        var glb = FakeInferenceClient.SampleGlb(); // closed box 0.5 x 1.0 x 0.25 m

        var session = await ReadAsync(await UploadAsync(client, id, glb, fields: new() { ["license"] = "cc-by", ["author"] = "Me" }));
        var model = Assert.Single(session.Models);
        Assert.Equal(("manual", "upload", "glb", "geometry_checked"), (model.Origin, model.Source, model.Format, model.EvaluationStatus));
        Assert.Equal("kit-part", model.Title);
        Assert.Equal("pending", model.Decision);
        var g = model.Geometry!;
        Assert.Equal((12, 1, true), (g.Triangles, g.Components, g.Closed));
        Assert.Equal((500, 1000, 250), (g.SizeXMm, g.SizeYMm, g.SizeZMm));
        Assert.Equal("plausible", g.ScaleStatus);
        Assert.Contains(model.Reasons, r => r.Code == "closed");
        Assert.Contains(model.Reasons, r => r.Code == "very_coarse"); // a 12-triangle box is coarse
        Assert.True(model.HasThumbnail);
        Assert.Null(model.VisualSimilarity); // no input photo and nothing accepted to compare the render with
        Assert.Contains(model.Reasons, r => r.Code == "no_visual");

        var file = await client.GetAsync($"/api/references/{id}/models/{model.Id}/file");
        Assert.Equal("model/gltf-binary", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(glb, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/references/{id}/models/{model.Id}/thumbnail")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, id, glb)).StatusCode);
    }

    [Fact]
    public async Task Upload_OpenMeshesAndStlAreFlagged()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        var open = (await ReadAsync(await UploadAsync(client, id, FakeInferenceClient.SampleGlb(open: true), "open.glb"))).Models.Single();
        Assert.False(open.Geometry!.Closed);
        Assert.Contains(open.Reasons, r => r.Code == "open" && r.Effect == "-");

        var stl = StlWriter.Write(GlbReader.Read(FakeInferenceClient.SampleGlb()));
        var session = await ReadAsync(await UploadAsync(client, id, stl, "box.stl"));
        var stlModel = session.Models.Single(m => m.Format == "stl");
        Assert.Equal("unitless", stlModel.Geometry!.ScaleStatus);
        Assert.Contains(stlModel.Reasons, r => r.Code == "scale_unitless");
        Assert.True(open.Score < session.Models.Single(m => m.Id == stlModel.Id).Score);
    }

    [Fact]
    public async Task Upload_RejectsUnsupportedOrUnusableFiles()
    {
        using var factory = new ApiFactory(new() { ["ModelSearch:MaxUploadBytes"] = "4096" });
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        var ascii = await UploadAsync(client, id, "solid cube\nfacet normal 0 0 1\nendsolid cube\n"u8.ToArray(), "cube.stl");
        Assert.Equal("unsupported_model", (await ascii.ProblemAsync()).GetProperty("code").GetString());
        var obj = await UploadAsync(client, id, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"u8.ToArray(), "tri.obj");
        Assert.Equal("unsupported_model", (await obj.ProblemAsync()).GetProperty("code").GetString());
        var broken = await UploadAsync(client, id, [0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0, 20, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken.StatusCode);
        var large = await UploadAsync(client, id, new byte[5000]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        var badLicence = await UploadAsync(client, id, FakeInferenceClient.SampleGlb(), fields: new() { ["license"] = "whatever" });
        Assert.Equal("invalid_license", (await badLicence.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Models_CanBeReviewedRemovedAndAreOwnerIsolated()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;
        var model = (await ReadAsync(await UploadAsync(client, id, FakeInferenceClient.SampleGlb()))).Models.Single();

        var stranger = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/references/{id}/models/{model.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/references/{id}/models/{model.Id}", new { decision = "accepted" })).StatusCode);

        var accepted = await ReadAsync(await client.PutAsJsonAsync($"/api/references/{id}/models/{model.Id}", new { decision = "accepted", note = " reuse body " }));
        Assert.Equal(("accepted", "reuse body"), (accepted.Models[0].Decision, accepted.Models[0].Note));
        var bad = await client.PutAsJsonAsync($"/api/references/{id}/models/{model.Id}", new { decision = "maybe" });
        Assert.Equal("invalid_decision", (await bad.ProblemAsync()).GetProperty("code").GetString());

        var folder = Path.Combine(factory.Root, "references", id.ToString("N"));
        Assert.True(File.Exists(Path.Combine(folder, $"m{model.Id:N}.glb")));
        Assert.Empty((await ReadAsync(await client.DeleteAsync($"/api/references/{id}/models/{model.Id}"))).Models);
        Assert.False(File.Exists(Path.Combine(folder, $"m{model.Id:N}.glb")));
        Assert.False(File.Exists(Path.Combine(folder, $"m{model.Id:N}.jpg")));
    }
}
