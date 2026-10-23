using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Tests.Infrastructure;

namespace ImageTo3D.Api.Tests;

/// <summary>Phase 7: part relations, fit checks, collisions, quality and release comparison.</summary>
public class AssemblyTests
{
    private static async Task<PartDesignProjectDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
    }

    private sealed class Design(HttpClient client, Guid id)
    {
        public HttpClient Client { get; } = client;

        public Guid Id { get; } = id;

        public string Url(string rest = "") => $"/api/part-designs/{Id}{rest}";

        public async Task<DesignPartDto> AddAsync(object shape)
        {
            var before = (await GetAsync()).Parts.Select(p => p.Id).ToHashSet();
            var project = await ReadAsync(await Client.PostAsJsonAsync(Url("/parts/primitive"), shape));
            return project.Parts.Single(p => !before.Contains(p.Id));
        }

        public async Task<PartDesignProjectDto> GetAsync() => (await Client.GetFromJsonAsync<PartDesignProjectDto>(Url(), ApiFactory.Json))!;

        public Task<HttpResponseMessage> RelateAsync(Guid a, Guid b, string kind, bool allowOverlap = false, double? opening = null) =>
            Client.PostAsJsonAsync(Url("/relations"), new { partAId = a, partBId = b, kind, allowOverlap, openingSizeMm = opening });

        public async Task<AssemblyReportDto> AssemblyAsync() => (await Client.GetFromJsonAsync<AssemblyReportDto>(Url("/assembly"), ApiFactory.Json))!;
    }

    private static async Task<Design> BlankAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/part-designs/blank", new { name = "Assembly" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new Design(client, (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!.Id);
    }

    [Fact]
    public async Task Relations_CheckTheFit_AndFollowUndoRedo()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var tube = await design.AddAsync(new { kind = "tube", outerDiameterMm = 5, innerDiameterMm = 3.2, lengthMm = 20 });
        var rod = await design.AddAsync(new { kind = "rod", diameterMm = 3, lengthMm = 40, center = new[] { 0.0, 20, 0 } });

        var related = await ReadAsync(await design.RelateAsync(tube.Id, rod.Id, "tube_over_rod"));
        var relation = Assert.Single(related.Relations);
        Assert.Equal(("tube_over_rod", "ok", "specified"), (relation.Kind, relation.Fit.Status, relation.Fit.Basis));
        Assert.Equal(0.2, relation.Fit.ClearanceMm!.Value, 4);
        Assert.Equal("valid", relation.Validation.Status);
        Assert.Equal("relate", related.History[0].Kind);

        // Tighter project limits make the same fit too loose.
        await ReadAsync(await design.Client.PutAsJsonAsync(design.Url(), new { name = "Assembly", fitAllowanceMinMm = 0, fitAllowanceMaxMm = 0.1 }));
        relation = Assert.Single((await design.GetAsync()).Relations);
        Assert.Equal("too_loose", relation.Fit.Status);
        Assert.Equal("warning", relation.Validation.Status);

        var edited = await ReadAsync(await design.Client.PutAsJsonAsync(design.Url($"/relations/{relation.Id}"), new { kind = "nested", allowOverlap = true, note = "telescoping" }));
        Assert.Equal(("nested", true, "telescoping"), (edited.Relations[0].Kind, edited.Relations[0].AllowOverlap, edited.Relations[0].Note));

        var undone = await ReadAsync(await design.Client.PostAsync(design.Url("/undo"), null));
        Assert.Equal("tube_over_rod", Assert.Single(undone.Relations).Kind);
        undone = await ReadAsync(await design.Client.PostAsync(design.Url("/undo"), null));
        Assert.Empty(undone.Relations);
        var redone = await ReadAsync(await design.Client.PostAsync(design.Url("/redo"), null));
        Assert.Equal("tube_over_rod", Assert.Single(redone.Relations).Kind);

        var removed = await ReadAsync(await design.Client.DeleteAsync(design.Url($"/relations/{relation.Id}")));
        Assert.Empty(removed.Relations);
        Assert.Equal(HttpStatusCode.Conflict, (await design.Client.DeleteAsync(design.Url($"/relations/{relation.Id}"))).StatusCode);
    }

    [Fact]
    public async Task Relations_RejectSelfDuplicatesAndInactiveParts()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var a = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10 });
        var b = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, center = new[] { 0.0, 10, 0 } });

        var self = await design.RelateAsync(a.Id, a.Id, "glued");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal("relation_self", (await self.ProblemAsync()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await design.RelateAsync(a.Id, b.Id, "welded")).StatusCode);
        await ReadAsync(await design.RelateAsync(a.Id, b.Id, "glued"));
        var duplicate = await design.RelateAsync(b.Id, a.Id, "slot");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("relation_exists", (await duplicate.ProblemAsync()).GetProperty("code").GetString());

        await ReadAsync(await design.Client.DeleteAsync(design.Url($"/parts/{a.Id}")));
        Assert.Equal(HttpStatusCode.Conflict, (await design.RelateAsync(a.Id, b.Id, "glued")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await design.RelateAsync(Guid.NewGuid(), b.Id, "glued")).StatusCode);
    }

    [Fact]
    public async Task DeletingAPart_RemovesItsRelations_AndUndoRestoresBoth()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var a = await design.AddAsync(new { kind = "plate", lengthMm = 20, widthMm = 20, thicknessMm = 1 });
        var b = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, axis = "y", center = new[] { 0.0, 5.5, 0 } });
        await ReadAsync(await design.RelateAsync(b.Id, a.Id, "glued"));

        var deleted = await ReadAsync(await design.Client.DeleteAsync(design.Url($"/parts/{b.Id}")));
        Assert.Empty(deleted.Relations);

        var restored = await ReadAsync(await design.Client.PostAsync(design.Url("/undo"), null));
        Assert.Contains(restored.Parts, p => p.Id == b.Id && p.State == Models.DesignPartState.Active);
        Assert.Single(restored.Relations);
    }

    [Fact]
    public async Task ANewEditAfterUndo_DiscardsTheUndoneRelation()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var a = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10 });
        var b = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, center = new[] { 0.0, 2, 0 } });
        await ReadAsync(await design.RelateAsync(a.Id, b.Id, "glued"));
        await ReadAsync(await design.Client.PostAsync(design.Url("/undo"), null));

        var next = await ReadAsync(await design.Client.PostAsync(design.Url($"/parts/{a.Id}/duplicate"), null));

        Assert.False(next.CanRedo);
        Assert.Empty(next.Relations);
        // The pair can be related again: the discarded relation is gone, not merely hidden.
        Assert.Single((await ReadAsync(await design.RelateAsync(a.Id, b.Id, "glued"))).Relations);
    }

    [Fact]
    public async Task Assembly_ReportsCollisions_AndIntentionalOverlapsAsInfo()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var plate = await design.AddAsync(new { kind = "plate", lengthMm = 40, widthMm = 40, thicknessMm = 2, center = new[] { 0.0, 1, 0 } });
        var peg = await design.AddAsync(new { kind = "rod", diameterMm = 3, lengthMm = 20, axis = "y", center = new[] { 0.0, 5, 0 } });
        var block = await design.AddAsync(new { kind = "box", lengthMm = 10, widthMm = 10, heightMm = 10, center = new[] { 30.0, 5, 0 } });
        var lid = await design.AddAsync(new { kind = "plate", lengthMm = 10, widthMm = 10, thicknessMm = 1, center = new[] { 30.0, 10.5, 0 } });

        var report = await design.AssemblyAsync();

        Assert.True(report.Complete);
        Assert.Equal(0.05, report.ContactToleranceMm);
        var hit = Assert.Single(report.Collisions, c => c.Contact == "intersecting");
        Assert.Equal(("parts_intersect", "warning"), (hit.Code, hit.Severity));
        Assert.Equal(new[] { plate.Id, peg.Id }.Order(), new[] { hit.PartAId, hit.PartBId }.Order());
        Assert.True(hit.IntersectionLengthMm > 15, hit.IntersectionLengthMm.ToString());
        Assert.Contains(report.Collisions, c => c.Contact == "touching" && c.Code == "parts_touch" && new[] { c.PartAId, c.PartBId }.Contains(lid.Id));
        Assert.Equal("warning", report.Status);
        Assert.Equal(4, report.Quality.SolidExportReadyParts);
        Assert.Equal(1, report.Quality.Parts.Single(p => p.PartId == peg.Id).CollisionCount);

        // The peg goes through a hole the plate does not model: allow the overlap.
        await ReadAsync(await design.RelateAsync(peg.Id, plate.Id, "peg_in_hole", allowOverlap: true, opening: 3.1));
        await ReadAsync(await design.RelateAsync(block.Id, lid.Id, "glued"));
        report = await design.AssemblyAsync();
        hit = Assert.Single(report.Collisions, c => c.Contact == "intersecting");
        Assert.Equal(("overlap_allowed", "info"), (hit.Code, hit.Severity));
        Assert.Contains(report.Collisions, c => c.Code == "glued_contact");
        var fit = report.Relations.Single(r => r.Kind == "peg_in_hole").Fit;
        Assert.Equal(("ok", 0.1), (fit.Status, Math.Round(fit.ClearanceMm!.Value, 4)));
        Assert.Equal("valid", report.Status);
    }

    [Fact]
    public async Task Assembly_FlagsGluedPartsApart_ContainmentAndUnmovedDuplicates()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var big = await design.AddAsync(new { kind = "box", lengthMm = 20, widthMm = 20, heightMm = 20, center = new[] { 0.0, 10, 0 } });
        var small = await design.AddAsync(new { kind = "box", lengthMm = 4, widthMm = 4, heightMm = 4, center = new[] { 0.0, 10, 0 } });
        var far = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, center = new[] { 100.0, 1, 0 } });
        await ReadAsync(await design.RelateAsync(big.Id, far.Id, "glued"));
        var copy = (await ReadAsync(await design.Client.PostAsync(design.Url($"/parts/{far.Id}/duplicate"), null))).Parts.Single(p => p.Origin == "duplicate");

        var report = await design.AssemblyAsync();

        var contained = Assert.Single(report.Collisions, c => c.Contact == "contained");
        Assert.Equal(("part_contained", small.Id), (contained.Code, contained.ContainedPartId));
        Assert.Contains(report.Collisions, c => c.Code == "glued_not_touching" && c.Contact == "gap");
        var coincident = Assert.Single(report.Collisions, c => c.Contact == "coincident");
        Assert.Equal("duplicate_in_place", coincident.Code);
        Assert.Contains(copy.Id, new[] { coincident.PartAId, coincident.PartBId });

        // Nested makes the containment intentional.
        await ReadAsync(await design.RelateAsync(big.Id, small.Id, "nested"));
        contained = Assert.Single((await design.AssemblyAsync()).Collisions, c => c.Contact == "contained");
        Assert.Equal(("overlap_allowed", "info"), (contained.Code, contained.Severity));
    }

    [Fact]
    public async Task Assembly_OfAGeneratedModel_ReportsQuality()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        var project = await ReadAsync(await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id }));

        var report = (await client.GetFromJsonAsync<AssemblyReportDto>($"/api/part-designs/{project.Id}/assembly", ApiFactory.Json))!;

        Assert.Equal(project.Parts.Count(p => p.State == Models.DesignPartState.Active), report.Quality.PartCount);
        Assert.Equal(report.Quality.Parts.Sum(p => p.Triangles), report.Quality.TotalTriangles);
        Assert.All(report.Quality.Parts, p => Assert.Equal(p.Closed && p.NonManifoldEdges == 0 && p.InconsistentEdges == 0 && p.DegenerateTriangles == 0, p.SolidExportReady));
    }

    [Fact]
    public async Task Release_CarriesAssemblyAndQuality_AndReleasesCanBeCompared()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var plate = await design.AddAsync(new { kind = "plate", lengthMm = 30, widthMm = 20, thicknessMm = 1 });
        var rod = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, center = new[] { 0.0, 1.5, 0 } });
        await ReadAsync(await design.RelateAsync(rod.Id, plate.Id, "glued"));
        Assert.Equal(HttpStatusCode.Created, (await design.Client.PostAsJsonAsync(design.Url("/releases"), new { note = "first" })).StatusCode);

        var zip = new ZipArchive(new MemoryStream(await design.Client.GetByteArrayAsync(design.Url("/releases/1/package"))));
        var manifest = JsonSerializer.Deserialize<ExportManifestDto>(zip.GetEntry("manifest.json")!.Open(), ApiFactory.Json)!;
        Assert.Equal(1, manifest.Assembly!.RelationCount);
        Assert.True(manifest.Assembly.CollisionCheckComplete);
        Assert.Contains(manifest.Files, f => f.Path == "metadata/assembly.json" && f.Kind == "assembly");
        Assert.Contains(manifest.Files, f => f.Path == "metadata/quality.json" && f.Kind == "quality");
        Assert.All(manifest.Parts, p => Assert.NotNull(p.GeometrySha256));
        Assert.Equal(1, manifest.Parts.Single(p => p.Id == plate.Id).Spec!.ThicknessMm);
        var quality = JsonSerializer.Deserialize<QualityReportDto>(zip.GetEntry("metadata/quality.json")!.Open(), ApiFactory.Json)!;
        Assert.Equal(2, quality.Parts.Count);

        // Change the plate, drop the relation, add a tube, then release again.
        var update = new
        {
            name = "Base plate", category = "sheet", reviewStatus = "user_confirmed", units = "mm", quantity = 2, thicknessMm = 1.5,
            material = "Polystyrene (PS)", needsFurtherModelling = false,
        };
        await ReadAsync(await design.Client.PutAsJsonAsync(design.Url($"/parts/{plate.Id}"), update));
        var relationId = (await design.GetAsync()).Relations.Single().Id;
        await ReadAsync(await design.Client.DeleteAsync(design.Url($"/relations/{relationId}")));
        var tube = await design.AddAsync(new { kind = "tube", outerDiameterMm = 4, innerDiameterMm = 3, lengthMm = 8, center = new[] { 50.0, 2, 0 } });
        Assert.Equal(HttpStatusCode.Created, (await design.Client.PostAsJsonAsync(design.Url("/releases"), new { })).StatusCode);

        var diff = (await design.Client.GetFromJsonAsync<ReleaseDiffDto>(design.Url("/releases/compare?from=1&to=2"), ApiFactory.Json))!;

        Assert.Equal(tube.Id, Assert.Single(diff.Added).Id);
        Assert.Empty(diff.Removed);
        var changed = Assert.Single(diff.Changed);
        Assert.Equal(plate.Id, changed.Id);
        Assert.Contains("name", changed.Changes);
        Assert.Contains("quantity", changed.Changes);
        Assert.DoesNotContain("spec.quantity", changed.Changes);
        Assert.Contains("spec.thicknessMm", changed.Changes);
        Assert.DoesNotContain("geometry", changed.Changes);
        Assert.Equal(1, diff.UnchangedCount);
        Assert.True(diff.SpecCompared && diff.RelationsCompared);
        Assert.Equal((0, 1), (diff.RelationsAdded, diff.RelationsRemoved));

        Assert.Equal(HttpStatusCode.NotFound, (await design.Client.GetAsync(design.Url("/releases/compare?from=1&to=9"))).StatusCode);
    }

    [Fact]
    public async Task AssemblyAndRelations_AreIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var design = await BlankAsync(factory);
        var a = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10 });
        var b = await design.AddAsync(new { kind = "rod", diameterMm = 2, lengthMm = 10, center = new[] { 0.0, 2, 0 } });
        var stranger = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(design.Url("/assembly"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(design.Url("/relations"), new { partAId = a.Id, partBId = b.Id, kind = "glued" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(design.Url("/releases/compare?from=1&to=1"))).StatusCode);
    }
}
