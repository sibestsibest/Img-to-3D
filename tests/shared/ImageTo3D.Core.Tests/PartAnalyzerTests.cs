using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;
using System.Text.Json.Nodes;

namespace ImageTo3D.Core.Tests;

public class PartAnalyzerTests
{
    private static (PartGeometryReport Report, PartSuggestion Suggestion) Analyze(Mesh mesh)
    {
        var report = PartAnalyzer.Measure(mesh);
        return (report, PartAnalyzer.Suggest(mesh, report));
    }

    [Fact]
    public void OrientedBounds_RotatedPlate_RecoversTrueSides()
    {
        var plate = PartFixtures.Rotate(PartFixtures.Box(40, 20, 2), 30);

        var box = OrientedBounds.Compute(plate);

        Assert.Equal(40, box.Extents.X, 1);
        Assert.Equal(20, box.Extents.Y, 1);
        Assert.Equal(2, box.Extents.Z, 1);
        // The axis-aligned box is much larger, which is why the oriented one is used for stock sizes.
        Assert.True(MeshOperations.ComputeBounds(plate).Size.X > 44);
    }

    [Fact]
    public void Measure_Box_ReportsAreaVolumeAndBounds()
    {
        var (report, _) = Analyze(PartFixtures.Box(10, 20, 5));

        Assert.Equal(1000, report.Volume!.Value, 6);
        Assert.Equal(700, report.SurfaceArea, 6);
        Assert.Equal(20, report.Bounds.Size.Y, 6);
        Assert.True(report.Topology.IsClosed);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Plate_IsSuggestedAsSheetWithThickness()
    {
        var (_, suggestion) = Analyze(PartFixtures.Rotate(PartFixtures.Box(40, 20, 1.5), 15));

        Assert.Equal(PartCategories.Sheet, suggestion.Category);
        Assert.Equal(1.5, suggestion.ThicknessMm, 1);
        Assert.Equal(40, suggestion.LengthMm, 1);
        Assert.True(suggestion.Confidence >= 0.5);
        Assert.NotEmpty(suggestion.Reasons);
    }

    [Fact]
    public void PlateWithHole_IsStillASheet()
    {
        var plate = PartFixtures.RingPrism(PartFixtures.Rectangle(30, 30), PartFixtures.Rectangle(10, 10), 1);

        var (report, suggestion) = Analyze(plate);

        Assert.Equal(PartCategories.Sheet, suggestion.Category);
        Assert.Equal(800, report.Volume!.Value, 6);
    }

    [Fact]
    public void Cylinder_IsSuggestedAsRodWithDiameter()
    {
        var (_, suggestion) = Analyze(PartFixtures.Prism(PartFixtures.Circle(2.5, 48), 100, levels: 4));

        Assert.Equal(PartCategories.Rod, suggestion.Category);
        Assert.Equal(5, suggestion.OuterDiameterMm!.Value, 0);
        Assert.Equal(100, suggestion.LengthMm, 3);
    }

    [Fact]
    public void Tube_IsSuggestedAsTubeWithInnerDiameter()
    {
        var tube = PartFixtures.RingPrism(PartFixtures.Circle(5, 48), PartFixtures.Circle(3, 48), 80, levels: 2);

        var (_, suggestion) = Analyze(tube);

        Assert.Equal(PartCategories.Tube, suggestion.Category);
        Assert.Equal(10, suggestion.OuterDiameterMm!.Value, 0);
        Assert.Equal(6, suggestion.InnerDiameterMm!.Value, 0);
    }

    [Fact]
    public void Cube_IsCustom3D()
    {
        var (_, suggestion) = Analyze(PartFixtures.Box(10, 10, 10));

        Assert.Equal(PartCategories.Custom3D, suggestion.Category);
    }

    [Fact]
    public void OpenMesh_NeedsReviewAndWarns()
    {
        var open = PartFixtures.Without(PartFixtures.Box(40, 20, 1.5), (_, _, z) => z > 1.49);

        var (report, suggestion) = Analyze(open);

        Assert.Equal(PartCategories.NeedsReview, suggestion.Category);
        Assert.Null(report.Volume);
        Assert.Contains(report.Issues, i => i.Code == "open_surface");
    }

    [Fact]
    public void DegenerateMesh_IsInvalidAndNeedsReview()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 2, 0, 0], [0, 1, 2]);

        var (report, suggestion) = Analyze(mesh);

        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(PartCategories.NeedsReview, suggestion.Category);
        Assert.Equal(0, suggestion.Confidence);
    }

    [Fact]
    public void InsideOutMesh_IsFlagged()
    {
        var box = PartFixtures.Box(10, 10, 10);
        var flipped = MeshOperations.Transform(box, [1, 0, 0, 0, 1, 0, 0, 0, 1]);
        var reversed = (int[])flipped.Indices.Clone();
        for (var t = 0; t < reversed.Length; t += 3)
            (reversed[t + 1], reversed[t + 2]) = (reversed[t + 2], reversed[t + 1]);

        var (report, _) = Analyze(new Mesh(flipped.Positions, reversed));

        Assert.Contains(report.Issues, i => i.Code == "inverted_normals");
    }

    [Fact]
    public void TinyPart_WarnsAboutScale()
    {
        var (report, _) = Analyze(PartFixtures.Box(0.1, 0.1, 0.1));

        Assert.Contains(report.Issues, i => i.Code == "suspicious_scale");
    }

    [Fact]
    public void GlbReader_ReadNodes_SplitsNamedNodesWithTransforms()
    {
        // One mesh instanced by two named nodes, the second translated by +5 on X.
        var (json, bin) = GlbContainer.Parse(GlbWriter.Write(PartFixtures.Box(1, 1, 1)));
        var source = GlbWriter.Write(PartFixtures.Box(1, 1, 1));
        json["nodes"] = new JsonArray(
            new JsonObject { ["mesh"] = 0, ["name"] = "arm" },
            new JsonObject { ["mesh"] = 0, ["name"] = "leg", ["translation"] = new JsonArray(5, 0, 0) });
        json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 1) });
        var glb = GlbContainer.Assemble(json, source.AsSpan(bin!.Value.Offset, bin.Value.Length));

        var nodes = GlbReader.ReadNodes(glb);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(["arm", "leg"], nodes.Select(n => n.Name).Order());
        var leg = nodes.Single(n => n.Name == "leg");
        Assert.Equal(5, MeshOperations.ComputeBounds(leg.Mesh).Min.X, 5);
        Assert.Equal(GlbReader.Read(glb).TriangleCount, nodes.Sum(n => n.Mesh.TriangleCount));
    }
}
