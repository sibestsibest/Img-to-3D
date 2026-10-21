using System.Globalization;
using ImageTo3D.Core.Drawing;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

public class DxfWriterTests
{
    private static readonly DxfInfo Info = new("Shoulder plate", Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), "Styrene (PS)", 2, 3, "Rev 4");

    private static PlanarOutline PlateWithHole() =>
        PlanarOutlineExtractor.Extract(PartFixtures.RingPrism(PartFixtures.Rectangle(40, 20), PartFixtures.Rectangle(10, 6), 2));

    private static List<(int Code, string Value)> Pairs(string dxf)
    {
        var lines = dxf.Split('\n');
        Assert.Equal("", lines[^1]); // ends with a newline
        Assert.Equal(0, (lines.Length - 1) % 2); // whole code/value pairs
        var pairs = new List<(int, string)>();
        for (var i = 0; i + 1 < lines.Length; i += 2)
            pairs.Add((int.Parse(lines[i], CultureInfo.InvariantCulture), lines[i + 1]));
        return pairs;
    }

    /// <summary>Entities as lists of group pairs, starting at each code-0 record inside ENTITIES.</summary>
    private static List<List<(int Code, string Value)>> Entities(List<(int Code, string Value)> pairs)
    {
        var start = pairs.FindIndex(p => p == (2, "ENTITIES")) + 1;
        var end = pairs.FindIndex(start, p => p == (0, "ENDSEC"));
        var entities = new List<List<(int, string)>>();
        for (var i = start; i < end; i++)
        {
            if (pairs[i].Code == 0)
                entities.Add([]);
            entities[^1].Add(pairs[i]);
        }

        return entities;
    }

    private static string Value(List<(int Code, string Value)> entity, int code) => entity.First(p => p.Code == code).Value;

    private static double Number(List<(int Code, string Value)> entity, int code) => double.Parse(Value(entity, code), CultureInfo.InvariantCulture);

    [Fact]
    public void Header_DeclaresR12AndMillimetres()
    {
        var pairs = Pairs(DxfWriter.Write(PlateWithHole(), Info));

        Assert.Equal((0, "SECTION"), pairs[0]);
        Assert.Equal((0, "EOF"), pairs[^1]);
        Assert.Equal((1, "AC1009"), pairs[pairs.IndexOf((9, "$ACADVER")) + 1]);
        Assert.Equal((70, "4"), pairs[pairs.IndexOf((9, "$INSUNITS")) + 1]);
        var layers = pairs.Select((p, i) => (p, i)).Where(x => x.p == (0, "LAYER")).Select(x => pairs[x.i + 1].Value).ToList();
        Assert.Equal([DxfWriter.CutLayer, DxfWriter.AnnotationLayer], layers);
    }

    [Fact]
    public void CutLayer_HoldsOnlyClosedContoursAtFullSize()
    {
        var outline = PlateWithHole();
        var entities = Entities(Pairs(DxfWriter.Write(outline, Info)));

        var polylines = entities.Where(e => e[0].Value == "POLYLINE").ToList();
        Assert.Equal(outline.Outers.Count + outline.Holes.Count, polylines.Count);
        Assert.All(polylines, p => Assert.Equal(DxfWriter.CutLayer, Value(p, 8)));
        Assert.All(polylines, p => Assert.Equal("1", Value(p, 70))); // closed

        var vertices = entities.Where(e => e[0].Value == "VERTEX").ToList();
        Assert.All(vertices, v => Assert.Equal(DxfWriter.CutLayer, Value(v, 8)));
        Assert.Equal(outline.Outers.Sum(o => o.Points.Count) + outline.Holes.Sum(h => h.Loop.Points.Count), vertices.Count);
        var xs = vertices.Select(v => Number(v, 10)).ToList();
        var ys = vertices.Select(v => Number(v, 20)).ToList();
        Assert.Equal(0, xs.Min(), 6);
        Assert.Equal(0, ys.Min(), 6);
        Assert.Equal(40, xs.Max(), 4);
        Assert.Equal(20, ys.Max(), 4);

        var texts = entities.Where(e => e[0].Value == "TEXT").ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Equal(DxfWriter.AnnotationLayer, Value(t, 8)));
        Assert.Contains(texts, t => Value(t, 1) == "H1");
        Assert.Contains(texts, t => Value(t, 1).Contains("Rev 4", StringComparison.Ordinal));
        // Annotations sit below the part, never over the cut area.
        Assert.All(texts.Where(t => Value(t, 1) != "H1"), t => Assert.True(Number(t, 20) < 0));
    }

    [Theory]
    [InlineData("Plate\nA", "Plate A")]
    [InlineData("Größe", "Gr\\U+00F6\\U+00DFe")]
    [InlineData("C:\\U+0041", "C:/U+0041")]
    [InlineData("50%% off", "50%  off")]
    public void Encode_KeepsTextOnOneLineAndEscapesNonAscii(string input, string expected) =>
        Assert.Equal(expected, DxfWriter.Encode(input));

    [Fact]
    public void UnusableOutline_IsRefused()
    {
        var bulky = PlanarOutlineExtractor.Extract(PartFixtures.Box(30, 30, 30));

        Assert.False(bulky.IsUsable);
        Assert.Throws<InvalidOperationException>(() => DxfWriter.Write(bulky, Info));
    }
}
