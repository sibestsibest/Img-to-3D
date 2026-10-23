using Microsoft.AspNetCore.Http;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Parametric;
using ImageTo3D.Api.Tests.Infrastructure;

namespace ImageTo3D.Api.Tests;

/// <summary>Phase 2: parametric projects through the API, with the worker replaced by a real fixture output.</summary>
public class ParametricDesignTests
{
    private static async Task<PartDesignProjectDto> CreateAsync(HttpClient client, object? body = null)
    {
        var response = await client.PostAsJsonAsync("/api/part-designs/parametric", body ?? new { template = "assembly", parameters = new { segments = 16 } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
    }

    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FakeParametricWorker.FixtureDirectory, "response.json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Create_MakesOnePartPerComponentWithExactSpecs()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var project = await CreateAsync(client, new { template = "assembly", parameters = new { segments = 16, components = new[] { new { name = "x" } } }, name = "Test rig" });

        var fixture = Fixture();
        var components = fixture.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal("Test rig", project.Name);
        Assert.Equal(("parametric", "parametric", "exact"), (project.Source.Kind, project.Origin, project.ScaleStatus));
        Assert.Equal(components.Select(c => c.GetProperty("name").GetString()), project.Parts.Select(p => p.Name));

        var plate = project.Parts.Single(p => p.Name == "base");
        Assert.Equal(("parametric", "sheet", "user_confirmed"), (plate.Origin, plate.Category, plate.ReviewStatus));
        Assert.Equal(1.5, plate.Spec.ThicknessMm);
        Assert.Equal("PS", plate.Spec.Material);
        Assert.True(plate.Geometry.Closed);
        Assert.Equal(40 * 30 * 1.5, plate.Geometry.VolumeMm3!.Value, 3);
        Assert.Equal("exact", plate.Estimated.Status);

        var rod = project.Parts.Single(p => p.Name == "mast");
        Assert.Equal(("rod", 2.0, 25.0), (rod.Category, rod.Spec.DiameterMm!.Value, rod.Spec.CutLengthMm!.Value));
        var tube = project.Parts.Single(p => p.Name == "sleeve");
        Assert.Equal(("tube", 5.0, 3.0, 8.0), (tube.Category, tube.Spec.OuterDiameterMm!.Value, tube.Spec.InnerDiameterMm!.Value, tube.Spec.CutLengthMm!.Value));
        var shell = project.Parts.Single(p => p.Name == "skin");
        Assert.Contains("roll", shell.Spec.Notes);
        Assert.Equal("custom_3d", project.Parts.Single(p => p.Name == "block").Category);

        // The worker's dimensions and the part geometry measured by the API agree.
        var bounds = fixture.GetProperty("dimensions");
        var min = bounds.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Assert.Equal(min[1], project.Parts.Min(p => p.Geometry.Bounds.Min.Y), 3);

        Assert.Contains(project.Issues, i => i.Code == "parametric_exact");
        Assert.NotNull(project.Parametric);
        Assert.Equal(("assembly", "0.1.0"), (project.Parametric!.Template, project.Parametric.EngineVersion));
        Assert.Equal(16, project.Parametric.Parameters.GetProperty("segments").GetInt32());

        // The request was passed on as given.
        var (template, parameters) = Assert.Single(factory.Parametric.Requests);
        Assert.Equal("assembly", template);
        Assert.Contains("\"segments\":16", parameters);

        // The temporary run folder is gone; the project survives a reload.
        Assert.All(factory.Parametric.RunDirectories, d => Assert.False(Directory.Exists(d)));
        var reloaded = await client.GetFromJsonAsync<PartDesignProjectDto>($"/api/part-designs/{project.Id}", ApiFactory.Json);
        Assert.Equal(project.Parts.Count, reloaded!.Parts.Count);
        Assert.Equal("parametric", reloaded.Origin);
    }

    [Fact]
    public async Task GeneratedFiles_AreKeptAndDownloadable()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var project = await CreateAsync(client);

        var kinds = project.Parametric!.Files.Select(f => f.Kind).ToList();
        Assert.Equal(["templates-pdf", "views-pdf", "views-svg", "bom", "stl", "obj", "package"], kinds);

        foreach (var file in project.Parametric.Files)
        {
            var response = await client.GetAsync(file.Url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(bytes);
            Assert.NotNull(response.Content.Headers.ContentDisposition?.FileName);
            switch (file.Kind)
            {
                case "templates-pdf" or "views-pdf":
                    Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
                    break;
                case "bom":
                    Assert.Equal(File.ReadAllBytes(Path.Combine(FakeParametricWorker.FixtureDirectory, "bom.csv")), bytes);
                    break;
                case "package":
                    using (var zip = new ZipArchive(new MemoryStream(bytes)))
                    {
                        var names = zip.Entries.Select(e => e.FullName).ToList();
                        Assert.Contains("templates/item01_plate.dxf", names);
                        Assert.Contains("model.glb", names);
                        Assert.Contains("response.json", names);
                    }

                    break;
            }
        }

        var unknown = await client.GetAsync($"/api/part-designs/{project.Id}/parametric/files/secrets");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Files_OfOtherProjectsAndOwnersAreRefused()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var project = await CreateAsync(client);

        var blank = await (await client.PostAsJsonAsync("/api/part-designs/blank", new { name = "Hand" })).Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json);
        var notParametric = await client.GetAsync($"/api/part-designs/{blank!.Id}/parametric/files/bom");
        Assert.Equal(HttpStatusCode.Conflict, notParametric.StatusCode);
        Assert.Null(blank.Parametric);
        Assert.Equal("manual", blank.Origin);

        var stranger = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/part-designs/{project.Id}/parametric/files/bom")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/part-designs/{project.Id}")).StatusCode);
    }

    [Fact]
    public async Task List_ShowsTheOrigin()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await CreateAsync(client);
        await client.PostAsJsonAsync("/api/part-designs/blank", new { name = "Hand" });

        var list = await client.GetFromJsonAsync<PagedResult<PartDesignSummaryDto>>("/api/part-designs", ApiFactory.Json);
        Assert.Equal(["manual", "parametric"], list!.Items.Select(i => i.Origin).Order());
    }

    [Fact]
    public async Task Refusals_KeepTheWorkersCodeAndField()
    {
        using var factory = new ApiFactory();
        factory.Parametric.Failure = new ApiException(StatusCodes.Status422UnprocessableEntity, "geometry_conflict", "height must be at least 43 mm.", "parameters.height");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/part-designs/parametric", new { template = "spacecraft", parameters = new { height = 30 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("geometry_conflict", problem.GetProperty("code").GetString());
        Assert.Equal("parameters.height", problem.GetProperty("field").GetString());
        Assert.Equal("height must be at least 43 mm.", problem.GetProperty("detail").GetString());

        var list = await client.GetFromJsonAsync<PagedResult<PartDesignSummaryDto>>("/api/part-designs", ApiFactory.Json);
        Assert.Empty(list!.Items);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"template\":\"Space Craft\"}")]
    [InlineData("{\"template\":\"spacecraft\",\"name\":\"" + "x" + "\"}")]
    public async Task InvalidRequests_AreRefusedBeforeTheWorker(string body)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var content = new StringContent(body.Replace("\"x\"", "\"" + new string('x', 101) + "\""), System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/part-designs/parametric", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Parametric.Requests);
    }

    [Fact]
    public async Task MissingWorker_Is503AndReportedInCapabilities()
    {
        using var factory = new ApiFactory();
        factory.Parametric.Capability = new ParametricCapability("missing", "The worker's Python environment is not set up.", null, null);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/part-designs/parametric", new { template = "spacecraft" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal("geometry_engine_unavailable", (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/part-designs/parametric/templates")).StatusCode);

        var caps = await client.GetFromJsonAsync<CapabilitiesDto>("/api/capabilities", ApiFactory.Json);
        var p = caps!.Engines.Single(e => e.Id == "parametric");
        Assert.Equal(("missing", "parametric"), (p.Status, p.Origin));
        Assert.Contains("Python", p.Reason);

        // Everything else keeps working.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/part-designs/blank", new { name = "Hand" })).StatusCode);
    }

    [Fact]
    public async Task Capabilities_ListEveryEngine()
    {
        using var factory = new ApiFactory();
        factory.Inference.Engine = "TripoSR";
        var client = factory.CreateClient();

        var caps = await client.GetFromJsonAsync<CapabilitiesDto>("/api/capabilities", ApiFactory.Json);
        Assert.Equal(["parametric", "TripoSR", "meshroom"], caps!.Engines.Select(e => e.Id));
        Assert.Equal(("ready", "0.1.0"), (caps.Engines[0].Status, caps.Engines[0].Version));
        Assert.Equal(("single_image_ai", "ai_predicted", "ready"), (caps.Engines[1].Kind, caps.Engines[1].Origin, caps.Engines[1].Status));
        Assert.Equal(("photogrammetry", "unavailable"), (caps.Engines[2].Kind, caps.Engines[2].Status));

        factory.Inference.Mode = FakeMode.Unavailable;
        caps = await client.GetFromJsonAsync<CapabilitiesDto>("/api/capabilities", ApiFactory.Json);
        Assert.Equal("unavailable", caps!.Engines[1].Status);
        Assert.NotNull(caps.Engines[1].Reason);
    }

    [Fact]
    public async Task Templates_ComeFromTheWorker()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var templates = await client.GetFromJsonAsync<JsonElement>("/api/part-designs/parametric/templates");
        Assert.Equal(7, templates.GetProperty("sheetThicknessesMm").GetArrayLength());
        Assert.Equal("assembly", templates.GetProperty("templates")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ParametricProject_WorksWithTheExistingDesignerTools()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var project = await CreateAsync(client);
        var plate = project.Parts.Single(p => p.Name == "base");

        // 1:1 drawing of a generated sheet part, BOM and draft package of the designer still work.
        var drawing = await client.GetAsync($"/api/part-designs/{project.Id}/parts/{plate.Id}/drawing?format=svg");
        Assert.Equal(HttpStatusCode.OK, drawing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/part-designs/{project.Id}/export/bom")).StatusCode);

        var renamed = await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{plate.Id}", new { name = "Base plate", category = "sheet", reviewStatus = "user_confirmed", material = "PS", thicknessMm = 1.5 });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        var undone = await (await client.PostAsync($"/api/part-designs/{project.Id}/undo", null)).Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json);
        Assert.Equal("base", undone!.Parts.Single(p => p.Id == plate.Id).Name);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/part-designs/{project.Id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.Root, "designs", project.Id.ToString("N"))));
    }

    [Fact]
    public async Task Jobs_RecordTheirEngineAndOrigin()
    {
        using var factory = new ApiFactory();
        factory.Inference.Engine = "StableFast3D";
        var client = factory.CreateClient();
        var job = await client.CreateJobAsync();
        var done = await client.WaitForTerminalAsync(job.Id);

        Assert.Equal(("StableFast3D", "ai_predicted"), (done.Engine, done.Origin));
    }
}
