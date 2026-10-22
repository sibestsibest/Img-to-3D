using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;

namespace ImageTo3D.Api.Tests;

/// <summary>Phase 6: empty projects and parts built from stock shapes.</summary>
public class PrimitivePartTests
{
    private static async Task<PartDesignProjectDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
    }

    private static async Task<(HttpClient Client, PartDesignProjectDto Project)> BlankAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/part-designs/blank", new { name = "Backpack" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!);
    }

    [Fact]
    public async Task Blank_HasNoSourceAndListsAsSuch()
    {
        using var factory = new ApiFactory();
        var (client, project) = await BlankAsync(factory);

        Assert.Equal("Backpack", project.Name);
        Assert.Equal("none", project.Source.Kind);
        Assert.Null(project.SourceJobId);
        Assert.Empty(project.Parts);
        Assert.Contains(project.Issues, i => i.Code == "no_source");

        var list = await client.GetFromJsonAsync<PagedResult<PartDesignSummaryDto>>("/api/part-designs", ApiFactory.Json);
        Assert.Equal("none", Assert.Single(list!.Items).SourceKind);
    }

    [Fact]
    public async Task Primitives_AreAddedWithExactSpecsAndUndoable()
    {
        using var factory = new ApiFactory();
        var (client, project) = await BlankAsync(factory);
        var url = $"/api/part-designs/{project.Id}/parts/primitive";

        var withPlate = await ReadAsync(await client.PostAsJsonAsync(url, new { kind = "plate", lengthMm = 40, widthMm = 20, thicknessMm = 0.5, center = new[] { 0.0, 10, 0 } }));
        var plate = Assert.Single(withPlate.Parts);
        Assert.Equal(("primitive", "sheet", "user_confirmed"), (plate.Origin, plate.Category, plate.ReviewStatus));
        Assert.Equal(0.5, plate.Spec.ThicknessMm);
        Assert.Equal("Polystyrene (PS)", plate.Spec.Material);
        Assert.Equal("Plate 40 x 20 x 0.5", plate.Name);
        Assert.True(plate.Geometry.Closed);
        Assert.Equal(400, plate.Geometry.VolumeMm3!.Value, 1);
        Assert.Equal(9.75, plate.Geometry.Bounds.Min.Y, 3);
        Assert.Equal("valid", plate.Validation.Status);

        var withTube = await ReadAsync(await client.PostAsJsonAsync(url, new { kind = "tube", outerDiameterMm = 5, innerDiameterMm = 3, lengthMm = 30, axis = "y", name = "Thruster" }));
        var tube = withTube.Parts.Single(p => p.Name == "Thruster");
        Assert.Equal(("tube", 5.0, 3.0, 30.0), (tube.Category, tube.Spec.OuterDiameterMm!.Value, tube.Spec.InnerDiameterMm!.Value, tube.Spec.CutLengthMm!.Value));
        Assert.Equal(30, tube.Geometry.Bounds.Size.Y, 3);
        Assert.Equal("valid", tube.Validation.Status);

        // The plate is a sheet: its 1:1 drawing works.
        var report = await client.GetFromJsonAsync<DrawingReportDto>($"/api/part-designs/{project.Id}/parts/{plate.Id}/drawing/report", ApiFactory.Json);
        Assert.Equal(40, Math.Max(report!.OutlineWidthMm, report.OutlineHeightMm), 2);
        Assert.Equal("specified", report.ThicknessStatus);

        // The tube is in the cut list with its specified sizes.
        var cutList = await client.GetStringAsync($"/api/part-designs/{project.Id}/export/cut-list");
        Assert.Contains("tube OD 5 / ID 3", cutList);
        Assert.Contains(",30,specified,", cutList);

        var undone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/undo", null));
        Assert.DoesNotContain(undone.Parts, p => p.Name == "Thruster");
        var redone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/redo", null));
        Assert.Contains(redone.Parts, p => p.Name == "Thruster" && p.State == DesignPartState.Active);
        Assert.Equal("add_primitive", redone.History[0].Kind);
    }

    [Fact]
    public async Task Primitives_CanBeAddedToAGeneratedModelsProject()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        var project = await ReadAsync(await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id }));

        var next = await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/primitive", new { kind = "rod", diameterMm = 1.2, lengthMm = 15 }));

        Assert.Equal(project.Parts.Count + 1, next.Parts.Count);
        Assert.Equal("generation_job", next.Source.Kind);
        Assert.Equal(job.Id, next.SourceJobId);
        var rod = next.Parts.Single(p => p.Origin == "primitive");
        Assert.Equal((1.2, 15.0), (rod.Spec.DiameterMm!.Value, rod.Spec.CutLengthMm!.Value));
    }

    [Theory]
    [InlineData("{\"kind\":\"tube\",\"outerDiameterMm\":3,\"innerDiameterMm\":4,\"lengthMm\":10}", "invalid_primitive")]
    [InlineData("{\"kind\":\"plate\",\"lengthMm\":10,\"widthMm\":10}", "invalid_primitive")]
    [InlineData("{\"kind\":\"sphere\",\"diameterMm\":3}", null)]
    [InlineData("{\"kind\":\"rod\",\"diameterMm\":3,\"lengthMm\":10,\"units\":\"inch\"}", null)]
    [InlineData("{\"kind\":\"rod\",\"diameterMm\":3,\"lengthMm\":10,\"axis\":\"w\"}", null)]
    [InlineData("{\"kind\":\"rod\",\"diameterMm\":3,\"lengthMm\":99999}", null)]
    [InlineData("{\"kind\":\"rod\",\"diameterMm\":3,\"lengthMm\":10,\"center\":[1,2]}", null)]
    public async Task Primitives_RejectInvalidInput(string body, string? code)
    {
        using var factory = new ApiFactory();
        var (client, project) = await BlankAsync(factory);

        var response = await client.PostAsync($"/api/part-designs/{project.Id}/parts/primitive", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (code is not null)
            Assert.Equal(code, (await response.ProblemAsync()).GetProperty("code").GetString());
        Assert.Empty((await ReadAsync(await client.GetAsync($"/api/part-designs/{project.Id}"))).Parts);
    }

    [Fact]
    public async Task Primitives_AreIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var (_, project) = await BlankAsync(factory);
        var stranger = factory.CreateClient();

        var response = await stranger.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/primitive", new { kind = "rod", diameterMm = 1, lengthMm = 5 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
