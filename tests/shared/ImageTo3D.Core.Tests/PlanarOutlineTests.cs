using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

public class PlanarOutlineTests
{
    [Fact]
    public void RotatedPlate_OutlineIsTrueRectangleAtOrigin()
    {
        var plate = PartFixtures.Rotate(PartFixtures.Box(40, 20, 2, ox: 5, oy: -3), 25);

        var outline = PlanarOutlineExtractor.Extract(plate);

        Assert.True(outline.IsUsable, string.Join(" ", outline.Issues.Select(i => i.Message)));
        var outer = Assert.Single(outline.Outers);
        Assert.Empty(outline.Holes);
        Assert.Equal(40, outline.WidthMm, 4);
        Assert.Equal(20, outline.HeightMm, 4);
        Assert.Equal(2, outline.MeasuredThicknessMm, 4);
        Assert.Equal(800, outer.Area, 3);
        Assert.Equal(120, outer.Perimeter, 3);
        Assert.Equal(0, outer.Bounds.Min.X, 9);
        Assert.Equal(0, outer.Bounds.Min.Y, 9);
        Assert.Empty(outline.Issues);
    }

    [Fact]
    public void PlateWithRectangularHole_PreservesHole()
    {
        var plate = PartFixtures.RingPrism(PartFixtures.Rectangle(40, 20), PartFixtures.Rectangle(10, 6), 2);

        var outline = PlanarOutlineExtractor.Extract(plate);

        Assert.True(outline.IsUsable);
        var hole = Assert.Single(outline.Holes);
        Assert.Equal(-60, hole.Loop.Area, 3); // clockwise
        Assert.Equal(10, hole.WidthMm, 4);
        Assert.Equal(6, hole.HeightMm, 4);
        Assert.Equal(20, hole.Centre.X, 4);
        Assert.Equal(10, hole.Centre.Y, 4);
        Assert.Null(hole.DiameterMm);
    }

    [Fact]
    public void DiscWithRoundHole_ReportsDiameter()
    {
        var disc = PartFixtures.RingPrism(PartFixtures.Circle(15, 64), PartFixtures.Circle(3, 64), 1.5);

        var outline = PlanarOutlineExtractor.Extract(disc);

        Assert.True(outline.IsUsable);
        var hole = Assert.Single(outline.Holes);
        Assert.Equal(6, hole.DiameterMm!.Value, 3);
        Assert.Equal(1.5, outline.MeasuredThicknessMm, 3);
    }

    [Fact]
    public void LShapedPlate_KeepsConcaveOutline()
    {
        (double, double)[] l = [(0, 0), (30, 0), (30, 10), (10, 10), (10, 30), (0, 30)];
        var plate = PartFixtures.Prism(l, 1.5, fanCaps: false);

        var outline = PlanarOutlineExtractor.Extract(plate);

        Assert.True(outline.IsUsable);
        var outer = Assert.Single(outline.Outers);
        Assert.Equal(500, outer.Area, 3);
        Assert.Equal(6, outer.Points.Count);
        Assert.Equal(30, outline.WidthMm, 4);
    }

    [Fact]
    public void FinelyTessellatedCircle_IsSimplifiedWithinTolerance()
    {
        const int segments = 720;
        var disc = PartFixtures.Prism(PartFixtures.Circle(20, segments), 1, levels: 1);

        var outline = PlanarOutlineExtractor.Extract(disc, toleranceMm: 0.05);

        var outer = Assert.Single(outline.Outers);
        Assert.True(outer.Points.Count < segments / 4, $"{outer.Points.Count} points");
        var exact = 0.5 * segments * Math.Sin(2 * Math.PI / segments) * 400;
        Assert.InRange(outer.Area / exact, 0.995, 1.0);
        // Every original vertex is within tolerance of the simplified outline.
        var cx = outline.WidthMm / 2;
        var cy = outline.HeightMm / 2;
        foreach (var (x, y) in PartFixtures.Circle(20, segments))
        {
            var p = new Point2(x + cx, y + cy);
            var d = Enumerable.Range(0, outer.Points.Count).Min(i => PlanarOutlineExtractor.SegmentDistance(p, outer.Points[i], outer.Points[(i + 1) % outer.Points.Count]));
            Assert.True(d <= 0.05 + 1e-6, $"deviation {d}");
        }
    }

    [Fact]
    public void TaperedEdges_AreFlagged()
    {
        var box = PartFixtures.Box(40, 20, 4);
        var p = (float[])box.Positions.Clone();
        for (var i = 0; i < p.Length; i += 3)
        {
            if (p[i + 2] > 2)
            {
                p[i] = 20 + (p[i] - 20) * 0.9f;
                p[i + 1] = 10 + (p[i + 1] - 10) * 0.9f;
            }
        }

        var outline = PlanarOutlineExtractor.Extract(new Mesh(p, box.Indices));

        Assert.True(outline.IsUsable);
        Assert.Contains(outline.Issues, i => i.Code == "edges_not_perpendicular" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Cube_IsNotPlanarAndRefused()
    {
        var outline = PlanarOutlineExtractor.Extract(PartFixtures.Box(10, 10, 10));

        Assert.False(outline.IsUsable);
        Assert.Contains(outline.Issues, i => i.Code == "not_planar" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void OpenPlate_IsRefused()
    {
        var open = PartFixtures.Without(PartFixtures.Box(40, 20, 2), (x, _, _) => x > 39.99);

        var outline = PlanarOutlineExtractor.Extract(open);

        Assert.False(outline.IsUsable);
        Assert.Contains(outline.Issues, i => i.Code == "outline_open");
    }

    [Fact]
    public void TwoSeparatePlates_AreBothDrawnWithWarning()
    {
        var mesh = PartFixtures.Merge(PartFixtures.Box(20, 20, 1), PartFixtures.Box(10, 20, 1, ox: 30));

        var outline = PlanarOutlineExtractor.Extract(mesh);

        Assert.Equal(2, outline.Outers.Count);
        Assert.Contains(outline.Issues, i => i.Code == "multiple_outlines");
    }
}
