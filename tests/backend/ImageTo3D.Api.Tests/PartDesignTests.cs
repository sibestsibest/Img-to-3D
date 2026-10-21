using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Tests.Infrastructure;
using ImageTo3D.Core.Geometry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImageTo3D.Api.Tests;

public class PartDesignTests
{
    // The fake inference mesh is 0.5 x 1.0 x 0.25 model units; the default export makes the longest side 100 mm.

    private static async Task<(HttpClient Client, PartDesignProjectDto Project, JobDto Job)> CreateProjectAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        Assert.True(job.Status == JobStatus.Succeeded, $"job {job.Status}: {job.ErrorCode} {job.Error}");
        var response = await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!, job);
    }

    private static async Task<PartDesignProjectDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
    }

    private static object UpdateBody(DesignPartDto part, string? name = null, string? category = null, double? outer = null, double? inner = null, string units = "mm") => new
    {
        name = name ?? part.Name,
        category = category ?? part.Category,
        reviewStatus = "user_confirmed",
        units,
        quantity = 2,
        material = "Styrene",
        outerDiameterMm = outer,
        innerDiameterMm = inner,
        cutLengthMm = 100.0,
    };

    private static object Plane(double y, bool allowOpen = false) => new { point = new[] { 0.0, y, 0.0 }, normal = new[] { 0.0, 1.0, 0.0 }, allowOpenResult = allowOpen };

    [Fact]
    public async Task Create_CopiesSourceAndSuggestsOnePartWithEstimatedMillimetreDimensions()
    {
        using var factory = new ApiFactory();
        var (_, project, job) = await CreateProjectAsync(factory);

        Assert.Equal(job.Id, project.SourceJobId);
        Assert.Equal("mm", project.Units);
        Assert.Equal("estimated", project.ScaleStatus);
        Assert.Contains(project.Issues, i => i.Code == "scale_estimated");
        var part = Assert.Single(project.Parts);
        Assert.Equal(DesignPartState.Active, part.State);
        Assert.Equal("needs_review", part.ReviewStatus);
        Assert.Equal("estimated", part.Estimated.Status);
        Assert.Null(part.Confirmed.Status);
        Assert.Equal(100, part.Estimated.LengthMm, 3);
        Assert.Equal(50, part.Estimated.WidthMm, 3);
        Assert.Equal(25, part.Estimated.ThicknessMm, 3);
        Assert.True(part.Geometry.Closed);
        Assert.Equal(125_000, part.Geometry.VolumeMm3!.Value, 1);
        Assert.Equal(100, part.Geometry.Bounds.Size.Y, 3);
        Assert.Equal(0, part.Geometry.Bounds.Min.Y, 6); // resting on Y = 0 like the viewer

        // The source is an exact copy of the job's raw mesh, in the design store, not the job store.
        var raw = await File.ReadAllBytesAsync(Path.Combine(factory.Root, "storage", job.Id.ToString("N"), "raw.glb"));
        var copy = await File.ReadAllBytesAsync(Path.Combine(factory.Root, "designs", project.Id.ToString("N"), "source.glb"));
        Assert.Equal(raw, copy);
    }

    [Fact]
    public async Task PartGeometry_IsServedAsGlbInMetres()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/parts/{project.Parts[0].Id}/geometry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("model/gltf-binary", response.Content.Headers.ContentType!.MediaType);
        var size = MeshOperations.ComputeBounds(GlbReader.Read(await response.Content.ReadAsByteArrayAsync())).Size;
        Assert.Equal(0.1, size.Y, 6);
    }

    [Fact]
    public async Task Project_SurvivesJobDeletionAndRetentionCleanup()
    {
        using var factory = new ApiFactory(new() { ["Storage:RetentionPeriod"] = "00:00:00", ["Storage:OrphanGracePeriod"] = "00:00:00" });
        var (client, project, job) = await CreateProjectAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/models/{job.Id}")).StatusCode);
        await factory.Services.GetServices<IHostedService>().OfType<RetentionCleanupService>().Single().CleanupOnceAsync(CancellationToken.None);

        var reloaded = await ReadAsync(await client.GetAsync($"/api/part-designs/{project.Id}"));
        Assert.Single(reloaded.Parts);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/part-designs/{project.Id}/parts/{project.Parts[0].Id}/geometry")).StatusCode);
    }

    [Fact]
    public async Task Create_RejectsMissingOrForeignSource()
    {
        using var factory = new ApiFactory();
        var (_, _, job) = await CreateProjectAsync(factory);
        var stranger = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/api/part-designs", new { sourceJobId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsJsonAsync("/api/part-designs", new { })).StatusCode);
    }

    [Fact]
    public async Task Projects_AreIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var (_, project, _) = await CreateProjectAsync(factory);
        var stranger = factory.CreateClient();
        var part = project.Parts[0].Id;

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/part-designs/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/part-designs/{project.Id}/parts/{part}/geometry")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part}/split", Plane(50))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/part-designs/{project.Id}")).StatusCode);
        var list = await stranger.GetFromJsonAsync<PagedResult<PartDesignSummaryDto>>("/api/part-designs", ApiFactory.Json);
        Assert.Equal(0, list!.TotalCount);
    }

    [Fact]
    public async Task UpdatePart_PersistsSpecAndReportsInconsistentTube()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];

        var updated = await ReadAsync(await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", UpdateBody(part, "Shoulder pipe", "tube", outer: 4, inner: 4)));

        var p = updated.Parts.Single();
        Assert.Equal("Shoulder pipe", p.Name);
        Assert.Equal("tube", p.Category);
        Assert.Equal("user_confirmed", p.ReviewStatus);
        Assert.Equal(2, p.Spec.Quantity);
        Assert.Equal("invalid", p.Validation.Status);
        Assert.Contains(p.Validation.Issues, i => i.Code == "tube_inner_not_less_than_outer" && i.Source == "manufacturing");

        // Reopen: persisted.
        var reopened = await ReadAsync(await client.GetAsync($"/api/part-designs/{project.Id}"));
        Assert.Equal("Shoulder pipe", reopened.Parts.Single().Name);
        Assert.Equal(4, reopened.Parts.Single().Spec.OuterDiameterMm);
    }

    [Theory]
    [InlineData("in", "tube", 4.0)]
    [InlineData("mm", "plank", 4.0)]
    [InlineData("mm", "tube", -1.0)]
    public async Task UpdatePart_RejectsInvalidUnitsCategoryOrValues(string units, string category, double outer)
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];

        var response = await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", UpdateBody(part, category: category, outer: outer, units: units));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UndoRedo_RestoresAndReappliesEdits()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];
        var original = part.Name;

        var edited = await ReadAsync(await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", UpdateBody(part, "Renamed")));
        Assert.True(edited.CanUndo);
        Assert.False(edited.CanRedo);

        var undone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/undo", null));
        Assert.Equal(original, undone.Parts.Single().Name);
        Assert.Equal(1, undone.Parts.Single().Spec.Quantity);
        Assert.True(undone.CanRedo);

        var redone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/redo", null));
        Assert.Equal("Renamed", redone.Parts.Single().Name);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/part-designs/{project.Id}/redo", null)).StatusCode);
    }

    [Fact]
    public async Task DuplicateAndDelete_AreReversible()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];

        var duplicated = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/duplicate", null));
        Assert.Equal(2, duplicated.Parts.Count);
        var copy = duplicated.Parts.Single(p => p.Id != part.Id);
        Assert.Equal(part.Id, copy.ParentPartId);
        Assert.EndsWith("(copy)", copy.Name);

        var deleted = await ReadAsync(await client.DeleteAsync($"/api/part-designs/{project.Id}/parts/{part.Id}"));
        Assert.Equal([copy.Id], deleted.Parts.Select(p => p.Id));

        var restored = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/undo", null));
        Assert.Equal(2, restored.Parts.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/part-designs/{project.Id}/parts/{copy.Id}/geometry")).StatusCode);
    }

    [Fact]
    public async Task SplitPreview_ComputesClosedHalvesWithoutSaving()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];

        var response = await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split/preview", Plane(40));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<SplitPreviewDto>(ApiFactory.Json))!;

        Assert.True(preview.CompleteSolidSplit);
        Assert.Equal(1, preview.ClosedContours);
        Assert.Equal(50 * 25 * 40, preview.Below!.VolumeMm3!.Value, 1);
        Assert.Equal(50 * 25 * 60, preview.Above!.VolumeMm3!.Value, 1);
        Assert.Equal(50 * 25, preview.Below.CapAreaMm2, 3);
        Assert.Equal(150, preview.ContourLengthMm, 3);
        Assert.Empty((await ReadAsync(await client.GetAsync($"/api/part-designs/{project.Id}"))).History);
    }

    [Fact]
    public async Task Split_CreatesTwoClosedPartsAndKeepsOriginalForUndo()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];

        var split = await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split", Plane(40)));

        var parent = split.Parts.Single(p => p.Id == part.Id);
        Assert.Equal(DesignPartState.Superseded, parent.State);
        var children = split.Parts.Where(p => p.ParentPartId == part.Id).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, c =>
        {
            Assert.Equal(DesignPartState.Active, c.State);
            Assert.Equal("split", c.Origin);
            Assert.True(c.Geometry.Closed);
            Assert.Equal(0, c.Geometry.BoundaryEdges);
        });
        Assert.Equal(125_000, children.Sum(c => c.Geometry.VolumeMm3!.Value), 1);
        Assert.Contains(children, c => Math.Abs(c.Geometry.Bounds.Max.Y - 40) < 1e-3);
        Assert.Contains(children, c => Math.Abs(c.Geometry.Bounds.Min.Y - 40) < 1e-3);

        // The child's stored geometry really is the cut half (not a clipped view of the parent).
        var lower = children.Single(c => c.Geometry.Bounds.Max.Y < 41);
        var glb = await client.GetByteArrayAsync($"/api/part-designs/{project.Id}/parts/{lower.Id}/geometry");
        var mesh = GlbReader.Read(glb);
        Assert.Equal(0.04, MeshOperations.ComputeBounds(mesh).Max.Y, 6);
        Assert.Equal(5e-5, MeshOperations.SignedVolume(mesh), 9); // 50 x 25 x 40 mm in cubic metres

        var undone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/undo", null));
        Assert.Equal([part.Id], undone.Parts.Select(p => p.Id));
        Assert.Equal(DesignPartState.Active, undone.Parts[0].State);

        var redone = await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/redo", null));
        Assert.Equal(2, redone.Parts.Count(p => p.State == DesignPartState.Active));
    }

    [Fact]
    public async Task NewEditAfterUndo_DiscardsRedoBranchAndItsFiles()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];
        var split = await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split", Plane(40)));
        var childIds = split.Parts.Where(p => p.ParentPartId == part.Id).Select(p => p.Id).ToList();
        await ReadAsync(await client.PostAsync($"/api/part-designs/{project.Id}/undo", null));

        var edited = await ReadAsync(await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", UpdateBody(part, "After undo")));

        Assert.False(edited.CanRedo);
        Assert.Single(edited.History);
        foreach (var child in childIds)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/part-designs/{project.Id}/parts/{child}/geometry")).StatusCode);
            Assert.False(File.Exists(Path.Combine(factory.Root, "designs", project.Id.ToString("N"), $"p{child:N}.glb")));
        }
    }

    [Fact]
    public async Task Split_PlaneMissingThePart_IsRejected()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{project.Parts[0].Id}/split", Plane(500));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plane_misses_part", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Split_InvalidPlane_IsRejected()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var url = $"/api/part-designs/{project.Id}/parts/{project.Parts[0].Id}/split";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { point = new[] { 0.0, 1, 0 }, normal = new[] { 0.0, 0, 0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { point = new[] { 0.0, 1 }, normal = new[] { 0.0, 1, 0 } })).StatusCode);
    }

    [Fact]
    public async Task OpenMesh_SplitIsRefusedUnlessExplicitlyAllowed_AndThenMarkedWithWarnings()
    {
        using var factory = new ApiFactory();
        factory.Inference.Mode = FakeMode.OpenMesh;
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];
        Assert.False(part.Geometry.Closed);
        Assert.Equal("needs_review", part.Category);
        Assert.Contains(part.Validation.Issues, i => i.Code == "open_surface");

        var refused = await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split", Plane(40));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("split_not_closed", (await refused.ProblemAsync()).GetProperty("code").GetString());

        var allowed = await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split", Plane(40, allowOpen: true)));
        var children = allowed.Parts.Where(p => p.ParentPartId == part.Id).ToList();
        Assert.Equal(2, children.Count);
        // The cut contour is closed (the walls are intact at y = 40), so the lower half is capped and
        // closed; the upper half keeps the missing top face and is reported as open.
        var lower = children.Single(c => c.Geometry.Bounds.Max.Y < 41);
        var upper = children.Single(c => c.Geometry.Bounds.Min.Y > 39);
        Assert.True(lower.Geometry.Closed);
        Assert.Equal(50 * 25 * 40, lower.Geometry.VolumeMm3!.Value, 1);
        Assert.False(upper.Geometry.Closed);
        Assert.Null(upper.Geometry.VolumeMm3);
        Assert.Equal("warning", upper.Validation.Status);
        Assert.Contains(upper.Validation.Issues, i => i.Code == "open_surface");
    }

    [Fact]
    public async Task OperationsOnSupersededPart_AreRejected()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var part = project.Parts[0];
        await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/split", Plane(40)));

        var response = await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", UpdateBody(part));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProject_RemovesRowsAndFiles()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        var folder = Path.Combine(factory.Root, "designs", project.Id.ToString("N"));
        Assert.True(Directory.Exists(folder));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/part-designs/{project.Id}")).StatusCode);

        Assert.False(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/part-designs/{project.Id}")).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOwnProjectsWithActivePartCount()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);
        await ReadAsync(await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/parts/{project.Parts[0].Id}/split", Plane(40)));

        var list = await client.GetFromJsonAsync<PagedResult<PartDesignSummaryDto>>("/api/part-designs", ApiFactory.Json);

        var item = Assert.Single(list!.Items);
        Assert.Equal(project.Id, item.Id);
        Assert.Equal(2, item.PartCount);
    }

    [Fact]
    public async Task ErrorResponses_DoNotLeakPaths()
    {
        using var factory = new ApiFactory();
        var (client, project, _) = await CreateProjectAsync(factory);

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/parts/{Guid.NewGuid()}/geometry");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(factory.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("designs", body, StringComparison.OrdinalIgnoreCase);
        _ = JsonDocument.Parse(body);
    }
}
