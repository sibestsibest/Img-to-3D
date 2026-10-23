using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ImageTo3D.Api.Tests;

public class PartExportTests
{
    private static async Task<(HttpClient Client, PartDesignProjectDto Project)> ProjectAsync(ApiFactory factory, FakeMode mode)
    {
        factory.Inference.Mode = mode;
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var response = await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!);
    }

    private static async Task<PartDesignProjectDto> UpdateAsync(HttpClient client, Guid projectId, Guid partId, object body)
    {
        var response = await client.PutAsJsonAsync($"/api/part-designs/{projectId}/parts/{partId}", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
    }

    private static object Sheet(string name = "Side plate") =>
        new { name, category = "sheet", reviewStatus = "user_confirmed", units = "mm", quantity = 2, material = "Styrene (PS)", thicknessMm = 2.0 };

    private static object Rod(string name, double cutLength, int quantity = 2) =>
        new { name, category = "rod", reviewStatus = "user_confirmed", units = "mm", quantity, material = "Styrene (PS)", diameterMm = 3.0, cutLengthMm = cutLength };

    private static string[][] Csv(byte[] content)
    {
        Assert.Equal(Encoding.UTF8.GetPreamble(), content[..3]);
        // The test rows contain no quoted commas or line breaks.
        return Encoding.UTF8.GetString(content, 3, content.Length - 3)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(','))
            .ToArray();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private static ExportManifestDto Manifest(Dictionary<string, byte[]> files) =>
        JsonSerializer.Deserialize<ExportManifestDto>(files["manifest.json"], ApiFactory.Json)!;

    [Fact]
    public async Task Dxf_IsServedForSheetPart()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/drawing?format=dxf&download=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/vnd.dxf", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith("-sheet.dxf", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var dxf = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("0\nSECTION\n2\nHEADER\n", dxf);
        Assert.Contains("$INSUNITS\n70\n4\n", dxf);
        Assert.Contains(" - Draft\n", dxf);
        Assert.EndsWith("0\nEOF\n", dxf);
    }

    [Fact]
    public async Task PartStl_IsBinaryInMillimetres()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/parts/{part.Id}/stl");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("model/stl", response.Content.Headers.ContentType!.MediaType);
        var stl = await response.Content.ReadAsByteArrayAsync();
        var triangles = BinaryPrimitives.ReadInt32LittleEndian(stl.AsSpan(80));
        Assert.Equal(12, triangles);
        Assert.Equal(84 + triangles * 50, stl.Length);
        var xs = new List<float>();
        for (var t = 0; t < triangles; t++)
            for (var v = 0; v < 3; v++)
                xs.Add(BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(84 + t * 50 + 12 + v * 12)));
        Assert.Equal(100, xs.Max() - xs.Min(), 2); // the plate is 100 mm long
    }

    [Fact]
    public async Task Bom_ListsActivePartsAndNeutralisesFormulas()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();
        await UpdateAsync(client, project.Id, part.Id, Sheet("=HYPERLINK(\"http://x\")"));

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/export/bom");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", text);
        var rows = Csv(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, rows.Length);
        Assert.Equal("item", rows[0][0]);
        var header = rows[0].ToList();
        Assert.Equal("2", rows[1][header.IndexOf("thickness_mm")]);
        Assert.Equal("2", rows[1][header.IndexOf("quantity")]);
        Assert.Equal("sheet", rows[1][header.IndexOf("category")]);
    }

    [Fact]
    public async Task CutList_GroupsRodCutsAndMarksEstimates()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Succeed);
        var first = project.Parts.Single();
        project = await UpdateAsync(client, project.Id, first.Id, Rod("Leg", 40));
        var copy = (await (await client.PostAsync($"/api/part-designs/{project.Id}/parts/{first.Id}/duplicate", null)).Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!
            .Parts.Single(p => p.Id != first.Id);
        var short_ = (await (await client.PostAsync($"/api/part-designs/{project.Id}/parts/{first.Id}/duplicate", null)).Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!
            .Parts.Single(p => p.Id != first.Id && p.Id != copy.Id);
        await UpdateAsync(client, project.Id, short_.Id, new { name = "Brace", category = "rod", reviewStatus = "needs_review", units = "mm", quantity = 1, material = "Styrene (PS)", diameterMm = 3.0 });

        var rows = Csv(await client.GetByteArrayAsync($"/api/part-designs/{project.Id}/export/cut-list"));
        var header = rows[0].ToList();
        string Cell(string[] row, string column) => row[header.IndexOf(column)];

        Assert.Equal(3, rows.Length);
        // Same stock, longest cut first. Brace has no cut length: the model's length (100 mm) is used and marked as an estimate.
        Assert.Equal("S1", Cell(rows[1], "stock_group"));
        Assert.Equal("100", Cell(rows[1], "cut_length_mm"));
        Assert.Equal("estimated", Cell(rows[1], "length_status"));
        Assert.Equal("Brace", Cell(rows[1], "parts"));
        // Leg and its copy: same stock and cut, so one row with the summed quantity.
        Assert.Equal("S1", Cell(rows[2], "stock_group"));
        Assert.Equal("round rod D 3", Cell(rows[2], "profile"));
        Assert.Equal("specified", Cell(rows[2], "size_status"));
        Assert.Equal("4", Cell(rows[2], "quantity"));
        Assert.Equal("160", Cell(rows[2], "total_length_mm"));
        Assert.Equal("specified", Cell(rows[2], "length_status"));
        Assert.Equal("Leg; Leg (copy)", Cell(rows[2], "parts"));
    }

    [Fact]
    public async Task DraftPackage_HoldsEveryFileWithMatchingHashes()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();
        await UpdateAsync(client, project.Id, part.Id, Sheet());

        var response = await client.GetAsync($"/api/part-designs/{project.Id}/export/package");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var files = Unzip(await response.Content.ReadAsByteArrayAsync());
        var manifest = Manifest(files);

        Assert.Equal("draft", manifest.Kind);
        Assert.Null(manifest.Revision);
        Assert.Equal("mm", manifest.Units);
        Assert.Equal(1, manifest.SchemaVersion);
        var kinds = manifest.Files.Select(f => f.Kind).Order().ToList();
        Assert.Equal(["assembly", "bom", "cut-list", "drawing-dxf", "drawing-pdf", "drawing-svg", "quality", "readme", "stl"], kinds);
        Assert.Equal(files.Keys.Where(k => k != "manifest.json").Order(), manifest.Files.Select(f => f.Path).Order());
        foreach (var file in manifest.Files)
        {
            Assert.Equal(file.Bytes, files[file.Path].LongLength);
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(files[file.Path])));
        }

        var entry = Assert.Single(manifest.Parts);
        Assert.Equal(4, entry.Files.Count); // stl, dxf, pdf, svg
        Assert.All(entry.Files, f => Assert.Contains("/01-Side-plate-", f));
        Assert.Contains("Draft", Encoding.UTF8.GetString(files[entry.Files.Single(f => f.EndsWith(".svg"))]));
        Assert.Empty(manifest.Warnings);

        // The draft manifest endpoint describes the same contents.
        var preview = (await client.GetFromJsonAsync<ExportManifestDto>($"/api/part-designs/{project.Id}/export/manifest", ApiFactory.Json))!;
        Assert.Equal(manifest.Files.Select(f => f.Path), preview.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Package_ListsWarningsForUnreviewedParts()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);

        var manifest = (await client.GetFromJsonAsync<ExportManifestDto>($"/api/part-designs/{project.Id}/export/manifest", ApiFactory.Json))!;

        var partId = project.Parts.Single().Id;
        Assert.Contains(manifest.Warnings, w => w.Code == "not_reviewed" && w.PartId == partId);
        Assert.Contains(manifest.Warnings, w => w.Code == "missing_thickness" && w.PartId == partId);
    }

    [Fact]
    public async Task Release_IsNumberedAndServedUnchangedAfterEdits()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();
        await UpdateAsync(client, project.Id, part.Id, Sheet("First name"));

        var created = await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { note = "To the workshop" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<DesignReleaseDto>(ApiFactory.Json))!;
        Assert.Equal(1, first.Revision);
        Assert.Equal("To the workshop", first.Note);
        Assert.EndsWith($"/releases/1/package", created.Headers.Location!.ToString());

        await UpdateAsync(client, project.Id, part.Id, Sheet("Second name"));
        var second = (await (await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { })).Content.ReadFromJsonAsync<DesignReleaseDto>(ApiFactory.Json))!;
        Assert.Equal(2, second.Revision);

        var list = (await client.GetFromJsonAsync<List<DesignReleaseDto>>($"/api/part-designs/{project.Id}/releases", ApiFactory.Json))!;
        Assert.Equal([2, 1], list.Select(r => r.Revision));

        var download = await client.GetAsync($"/api/part-designs/{project.Id}/releases/1/package");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.EndsWith("-rev1.zip", download.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var zip = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(first.Sha256, Convert.ToHexStringLower(SHA256.HashData(zip)));
        var files = Unzip(zip);
        var manifest = Manifest(files);
        Assert.Equal("release", manifest.Kind);
        Assert.Equal(1, manifest.Revision);
        Assert.Equal("First name", manifest.Parts.Single().Name);
        Assert.Contains("Rev 1", Encoding.UTF8.GetString(files[manifest.Files.Single(f => f.Kind == "drawing-svg").Path]));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/part-designs/{project.Id}/releases/3/package")).StatusCode);
    }

    [Fact]
    public async Task Export_IsBlockedByAPartWithErrors()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Succeed);
        var part = project.Parts.Single();
        await UpdateAsync(client, project.Id, part.Id, new { name = "Bad tube", category = "tube", reviewStatus = "user_confirmed", units = "mm", quantity = 1, outerDiameterMm = 3.0, innerDiameterMm = 4.0, cutLengthMm = 10.0 });

        var package = await client.GetAsync($"/api/part-designs/{project.Id}/export/package");
        var release = await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { });

        Assert.Equal(HttpStatusCode.Conflict, package.StatusCode);
        Assert.Equal("export_blocked", (await package.ProblemAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<DesignReleaseDto>>($"/api/part-designs/{project.Id}/releases", ApiFactory.Json))!);
    }

    [Fact]
    public async Task Exports_AreIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        var part = project.Parts.Single();
        (await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { })).EnsureSuccessStatusCode();
        var stranger = factory.CreateClient();

        foreach (var url in new[]
                 {
                     $"/api/part-designs/{project.Id}/parts/{part.Id}/stl",
                     $"/api/part-designs/{project.Id}/export/bom",
                     $"/api/part-designs/{project.Id}/export/cut-list",
                     $"/api/part-designs/{project.Id}/export/package",
                     $"/api/part-designs/{project.Id}/releases",
                     $"/api/part-designs/{project.Id}/releases/1/package",
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { })).StatusCode);
    }

    [Fact]
    public async Task DeletingProject_RemovesItsReleases()
    {
        using var factory = new ApiFactory();
        var (client, project) = await ProjectAsync(factory, FakeMode.Plate);
        (await client.PostAsJsonAsync($"/api/part-designs/{project.Id}/releases", new { })).EnsureSuccessStatusCode();

        (await client.DeleteAsync($"/api/part-designs/{project.Id}")).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ImageTo3D.Api.Data.AppDbContext>();
        Assert.Empty(db.DesignReleases.Where(r => r.ProjectId == project.Id));
    }
}
