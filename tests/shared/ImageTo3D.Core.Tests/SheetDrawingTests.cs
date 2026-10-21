using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ImageTo3D.Core.Drawing;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

public class SheetDrawingTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static SheetDrawingInfo Info(string name = "Shoulder plate", double thickness = 2, bool specified = true) =>
        new(name, Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), "Test project", "Styrene (PS)", thickness, specified, 2,
            "From the generated model at its estimated scale.", new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc));

    private static PlanarOutline PlateWithHole() =>
        PlanarOutlineExtractor.Extract(PartFixtures.RingPrism(PartFixtures.Rectangle(40, 20), PartFixtures.Rectangle(10, 6), 2));

    private static double[] Numbers(string pathData) =>
        Regex.Matches(pathData, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void TitleBlock_ShowsTheRevision()
    {
        var draft = SheetDrawingBuilder.Build(PlateWithHole(), Info());
        var released = SheetDrawingBuilder.Build(PlateWithHole(), Info() with { Revision = "Rev 2" });

        Assert.Equal("Draft", draft.Document.Texts.Single(t => t.Role == "revision").Text);
        Assert.Equal("Rev 2", released.Document.Texts.Single(t => t.Role == "revision").Text);
        Assert.Empty(released.Issues); // the new cell overlaps nothing
    }

    [Fact]
    public void Build_PlateFitsA4PortraitAndPassesChecks()
    {
        var drawing = SheetDrawingBuilder.Build(PlateWithHole(), Info());

        Assert.Equal(PageSize.A4Portrait, drawing.Document.Page);
        Assert.Empty(drawing.Issues); // no errors, and no overlapping or clipped labels
        Assert.Equal(2, drawing.Document.Lines.Count(l => l.Layer == DrawingLayer.Cut));
        Assert.All(drawing.Document.Lines.Where(l => l.Layer == DrawingLayer.Cut), l => Assert.True(l.Closed));
    }

    [Fact]
    public void Svg_IsMillimetreScaledWithCutContoursSeparateFromAnnotations()
    {
        var svg = SvgDrawingWriter.Write(SheetDrawingBuilder.Build(PlateWithHole(), Info()).Document);
        var root = XDocument.Parse(svg).Root!;

        // 1 user unit = 1 mm: width/height in mm equal the viewBox.
        var width = double.Parse(root.Attribute("width")!.Value.Replace("mm", ""), CultureInfo.InvariantCulture);
        var height = double.Parse(root.Attribute("height")!.Value.Replace("mm", ""), CultureInfo.InvariantCulture);
        Assert.Equal(595 * 25.4 / 72, width, 3);
        Assert.Equal(842 * 25.4 / 72, height, 3);
        Assert.Equal($"0 0 {root.Attribute("width")!.Value.Replace("mm", "")} {root.Attribute("height")!.Value.Replace("mm", "")}", root.Attribute("viewBox")!.Value);

        var groups = root.Elements(Svg + "g").ToDictionary(g => g.Attribute("id")!.Value);
        Assert.Equal(["annotation", "cut", "reference"], groups.Keys.Order());
        var cutPaths = groups["cut"].Elements(Svg + "path").ToList();
        Assert.Equal(2, cutPaths.Count);
        Assert.All(cutPaths, p => Assert.EndsWith("Z", p.Attribute("d")!.Value));
        Assert.Empty(groups["cut"].Elements(Svg + "text")); // no annotation inside the cut layer

        // Cut geometry at 1:1: the outer contour spans exactly 40 x 20 mm, the hole 10 x 6 mm.
        var outer = Numbers(cutPaths.Single(p => p.Attribute("data-role")!.Value == "outline").Attribute("d")!.Value);
        Assert.Equal(40, outer.Where((_, i) => i % 2 == 0).Max() - outer.Where((_, i) => i % 2 == 0).Min(), 4);
        Assert.Equal(20, outer.Where((_, i) => i % 2 == 1).Max() - outer.Where((_, i) => i % 2 == 1).Min(), 4);
        var hole = Numbers(cutPaths.Single(p => p.Attribute("data-role")!.Value == "hole").Attribute("d")!.Value);
        Assert.Equal(10, hole.Where((_, i) => i % 2 == 0).Max() - hole.Where((_, i) => i % 2 == 0).Min(), 4);

        // Verification bar: exactly 100 mm.
        var bar = Numbers(groups["reference"].Elements(Svg + "path").Single(p => p.Attribute("data-role")?.Value == "verification-bar").Attribute("d")!.Value);
        Assert.Equal(100, bar[2] - bar[0], 9);

        // Critical dimensions and title block values.
        var texts = root.Descendants(Svg + "text").ToDictionary(t => t.Attribute("data-role")?.Value ?? Guid.NewGuid().ToString(), t => t.Value);
        Assert.Equal("40.00", texts["width"]);
        Assert.Equal("20.00", texts["height"]);
        Assert.Equal("2.00", texts["thickness"]);
        Assert.Contains("1:1", texts["scale"]);
        Assert.Equal("2.00 mm (specified)", texts["thickness-spec"]);
        Assert.Contains("100%", texts["print-instruction"]);
        Assert.Equal("2", texts["quantity"]);
    }

    [Fact]
    public void Svg_EscapesUserText()
    {
        var svg = SvgDrawingWriter.Write(SheetDrawingBuilder.Build(PlateWithHole(), Info("<script>alert(1)</script> & \"x\"")).Document);

        Assert.DoesNotContain("<script>", svg);
        Assert.Contains("&lt;script&gt;", svg);
        _ = XDocument.Parse(svg); // still well-formed
    }

    [Fact]
    public void EstimatedThickness_IsLabelledAsEstimate()
    {
        var svg = SvgDrawingWriter.Write(SheetDrawingBuilder.Build(PlateWithHole(), Info(specified: false)).Document);

        Assert.Contains("estimated from model", svg);
    }

    [Fact]
    public void Pdf_HasExactPageSizeMillimetreScaleAndNoEmbeddedFont()
    {
        var pdf = PdfDrawingWriter.Write(SheetDrawingBuilder.Build(PlateWithHole(), Info()).Document);
        var raw = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-", raw);
        Assert.Matches(@"/MediaBox\s*\[0 0 595 842\]", raw);
        Assert.DoesNotContain("/FontFile", raw); // text is drawn as outlines
        Assert.DoesNotContain("/Subtype /Image", raw);

        var content = string.Join("\n", Streams(pdf));
        // Canvas scaled by 72/25.4 so that drawing units are millimetres.
        Assert.Contains("2.8346457 0 0 2.8346457", content);
        // The 100 mm verification bar starts at the 10 mm margin and ends at 110 mm.
        Assert.Matches(@"(^|\s)10 (\S+) m\s+110 \2 l", content);
    }

    [Fact]
    public void Pdf_IsSmall()
    {
        var pdf = PdfDrawingWriter.Write(SheetDrawingBuilder.Build(PlateWithHole(), Info()).Document);

        Assert.InRange(pdf.Length, 1_000, 200_000);
    }

    [Fact]
    public void LargePart_UsesLargerPageAtFullSize()
    {
        var outline = PlanarOutlineExtractor.Extract(PartFixtures.Box(250, 120, 1.5));

        var drawing = SheetDrawingBuilder.Build(outline, Info(thickness: 1.5));

        Assert.NotEqual(PageSize.A4Portrait, drawing.Document.Page);
        Assert.DoesNotContain(drawing.Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void PartLargerThanA3_IsRefusedRatherThanScaled()
    {
        var outline = PlanarOutlineExtractor.Extract(PartFixtures.Box(600, 100, 2));

        Assert.Throws<DrawingTooLargeException>(() => SheetDrawingBuilder.Build(outline, Info()));
    }

    [Fact]
    public void Checker_DetectsWrongScaleAndMissingBar()
    {
        var outline = PlateWithHole();
        var doc = SheetDrawingBuilder.Build(outline, Info()).Document;
        var outer = doc.Lines.Single(l => l.Role == "outline");
        doc.Lines.Remove(outer);
        doc.Lines.Add(outer with { Points = outer.Points.Select(p => new Point2(p.X * 1.01, p.Y)).ToList() });
        doc.Lines.RemoveAll(l => l.Role == "verification-bar");

        var issues = DrawingChecker.Check(doc, outline);

        Assert.Contains(issues, i => i.Code == "scale_mismatch");
        Assert.Contains(issues, i => i.Code == "missing_verification_bar");
    }

    [Fact]
    public void Checker_DetectsOpenContour()
    {
        var outline = PlateWithHole();
        var doc = SheetDrawingBuilder.Build(outline, Info()).Document;
        var hole = doc.Lines.Single(l => l.Role == "hole");
        doc.Lines[doc.Lines.IndexOf(hole)] = hole with { Closed = false };

        Assert.Contains(DrawingChecker.Check(doc, outline), i => i.Code == "open_cut_contour");
    }

    [Fact]
    public void Build_RefusesUnusableOutline()
    {
        var outline = PlanarOutlineExtractor.Extract(PartFixtures.Box(10, 10, 10));

        Assert.Throws<InvalidOperationException>(() => SheetDrawingBuilder.Build(outline, Info()));
    }

    /// <summary>Decompressed content of every FlateDecode stream in the PDF.</summary>
    private static IEnumerable<string> Streams(byte[] pdf)
    {
        var raw = Encoding.Latin1.GetString(pdf);
        foreach (Match m in Regex.Matches(raw, @"stream\r?\n"))
        {
            var start = m.Index + m.Length;
            var end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
                continue;
            string? text = null;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var reader = new StreamReader(z, Encoding.Latin1);
                text = reader.ReadToEnd();
            }
            catch (InvalidDataException)
            {
            }

            if (text is not null)
                yield return text;
        }
    }
}
