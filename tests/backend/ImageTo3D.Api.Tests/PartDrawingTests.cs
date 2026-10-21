using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Tests.Infrastructure;

namespace ImageTo3D.Api.Tests;

public class PartDrawingTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static async Task<(HttpClient Client, PartDesignProjectDto Project, DesignPartDto Part)> PlateProjectAsync(ApiFactory factory)
    {
        factory.Inference.Mode = FakeMode.Plate;
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        var response = await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
        return (client, project, project.Parts.Single());
    }

    private static string Url(PartDesignProjectDto project, DesignPartDto part, string rest) => $"/api/part-designs/{project.Id}/parts/{part.Id}/drawing{rest}";

    private static async Task SetPartAsync(HttpClient client, PartDesignProjectDto project, DesignPartDto part, string category, double? thickness = null, string? name = null)
    {
        var response = await client.PutAsJsonAsync($"/api/part-designs/{project.Id}/parts/{part.Id}", new
        {
            name = name ?? part.Name,
            category,
            reviewStatus = "user_confirmed",
            units = "mm",
            quantity = 3,
            material = "Styrene (PS)",
            thicknessMm = thickness,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PlatePart_IsSuggestedAsSheetAndReportsItsDrawing()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);
        Assert.Equal("sheet", part.Category);

        var report = (await client.GetFromJsonAsync<DrawingReportDto>(Url(project, part, "/report"), ApiFactory.Json))!;

        Assert.Equal("A4 portrait", report.Page);
        Assert.Equal("1:1", report.Scale);
        Assert.Equal("mm", report.Units);
        Assert.Equal(100, report.OutlineWidthMm, 3);
        Assert.Equal(50, report.OutlineHeightMm, 3);
        Assert.Equal(2, report.ThicknessMm, 3);
        Assert.Equal("estimated", report.ThicknessStatus);
        Assert.Empty(report.Holes);
        Assert.DoesNotContain(report.Issues, i => i.Severity == "error");
        Assert.Equal(["svg", "pdf", "dxf"], report.Formats);
    }

    [Fact]
    public async Task Svg_IsServedSafelyAtMillimetreScale()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);

        var response = await client.GetAsync(Url(project, part, "?format=svg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.EndsWith("mm", root.Attribute("width")!.Value);
        var cut = root.Elements(Svg + "g").Single(g => g.Attribute("id")!.Value == "cut").Elements(Svg + "path").Single();
        var numbers = cut.Attribute("d")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var xs = numbers.Where((_, i) => i % 2 == 0).ToArray();
        var ys = numbers.Where((_, i) => i % 2 == 1).ToArray();
        Assert.Equal(100, xs.Max() - xs.Min(), 3);
        Assert.Equal(50, ys.Max() - ys.Min(), 3);
    }

    [Fact]
    public async Task Pdf_IsVectorA4AndDownloadable()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);

        var response = await client.GetAsync(Url(project, part, "?format=pdf&download=true"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith("-sheet.pdf", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var raw = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF-", raw);
        Assert.Matches(@"/MediaBox\s*\[0 0 595 842\]", raw);
    }

    [Fact]
    public async Task SpecifiedThickness_IsUsedAndLabelled()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);
        await SetPartAsync(client, project, part, "sheet", thickness: 1.5);

        var report = (await client.GetFromJsonAsync<DrawingReportDto>(Url(project, part, "/report"), ApiFactory.Json))!;
        var svg = await client.GetStringAsync(Url(project, part, "?format=svg"));

        Assert.Equal(1.5, report.ThicknessMm);
        Assert.Equal("specified", report.ThicknessStatus);
        Assert.Equal(2, report.MeasuredThicknessMm, 3);
        Assert.Contains("1.50 mm (specified)", svg);
        Assert.Contains(">3<", svg); // quantity in the title block
        Assert.Contains("Styrene (PS)", svg);
    }

    [Fact]
    public async Task UserTextInDrawing_IsEscaped()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);
        await SetPartAsync(client, project, part, "sheet", name: "<script>alert(1)</script>");

        var svg = await client.GetStringAsync(Url(project, part, "?format=svg"));

        Assert.DoesNotContain("<script>", svg);
        Assert.Contains("&lt;script&gt;", svg);
    }

    [Fact]
    public async Task NonSheetPart_IsRefused()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);
        await SetPartAsync(client, project, part, "rod");

        var response = await client.GetAsync(Url(project, part, "?format=pdf"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("drawing_unsupported", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task BulkyPartMarkedAsSheet_IsRefusedAsNotPlanar()
    {
        using var factory = new ApiFactory(); // default fake mesh: a 50 x 100 x 25 mm block
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        var project = (await (await client.PostAsJsonAsync("/api/part-designs", new { sourceJobId = job.Id })).Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;
        var part = project.Parts.Single();
        await SetPartAsync(client, project, part, "sheet");

        var response = await client.GetAsync(Url(project, part, "/report"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("outline_failed", problem.GetProperty("code").GetString());
        Assert.Contains("developed pattern", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task InvalidFormat_IsRejected()
    {
        using var factory = new ApiFactory();
        var (client, project, part) = await PlateProjectAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(project, part, "?format=dwg"))).StatusCode);
    }

    [Fact]
    public async Task Drawings_AreIsolatedPerOwner()
    {
        using var factory = new ApiFactory();
        var (_, project, part) = await PlateProjectAsync(factory);
        var stranger = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url(project, part, "?format=svg"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url(project, part, "/report"))).StatusCode);
    }
}
