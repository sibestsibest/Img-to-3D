using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

public class PrimitiveBuilderTests
{
    /// <summary>Area of the regular polygon inscribed in a circle of radius r (the shape the builder makes).</summary>
    private static double PolygonArea(double r, int n = PrimitiveBuilder.DefaultSegments) => n / 2.0 * r * r * Math.Sin(2 * Math.PI / n);

    private static void AssertClosedOutward(Mesh mesh)
    {
        var topology = MeshTopology.Analyze(mesh);
        Assert.True(topology.IsClosed, $"boundary {topology.BoundaryEdgeCount}, non-manifold {topology.NonManifoldEdgeCount}, inconsistent {topology.InconsistentlyOrientedEdgeCount}");
        Assert.Equal(0, topology.DegenerateTriangleCount);
        Assert.Equal(1, topology.ComponentCount);
        Assert.True(MeshOperations.SignedVolume(mesh) > 0, "faces must point outward");
    }

    [Fact]
    public void Plate_HasExactSizeWithThicknessAlongY()
    {
        var mesh = PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Plate, LengthMm: 40, WidthMm: 20, ThicknessMm: 0.5, Center: new Vector3D(5, 10, -3)));

        AssertClosedOutward(mesh);
        var b = MeshOperations.ComputeBounds(mesh);
        Assert.Equal(40, b.Size.X, 4);
        Assert.Equal(0.5, b.Size.Y, 4);
        Assert.Equal(20, b.Size.Z, 4);
        Assert.Equal(new Vector3D(5, 10, -3), new Vector3D(Math.Round(b.Center.X, 4), Math.Round(b.Center.Y, 4), Math.Round(b.Center.Z, 4)));
        Assert.Equal(400, MeshOperations.SignedVolume(mesh), 3);
    }

    [Theory]
    [InlineData('x', 0)]
    [InlineData('y', 1)]
    [InlineData('z', 2)]
    public void Plate_ThicknessFollowsTheAxis(char axis, int thinAxis)
    {
        var size = MeshOperations.ComputeBounds(PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Plate, LengthMm: 30, WidthMm: 12, ThicknessMm: 1, Axis: axis))).Size;
        Assert.Equal(1, size[thinAxis], 4);
        Assert.Equal(30, size.MaxComponent, 4);
    }

    [Fact]
    public void Plate_GivesAUsableCuttingOutline()
    {
        var outline = PlanarOutlineExtractor.Extract(PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Plate, LengthMm: 40, WidthMm: 20, ThicknessMm: 0.5)));

        Assert.True(outline.IsUsable);
        Assert.Equal(40, Math.Max(outline.WidthMm, outline.HeightMm), 3);
        Assert.Equal(20, Math.Min(outline.WidthMm, outline.HeightMm), 3);
        Assert.Equal(0.5, outline.MeasuredThicknessMm, 3);
        Assert.Empty(outline.Holes);
    }

    [Fact]
    public void Box_SizesAlongXYZ()
    {
        var mesh = PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Box, LengthMm: 10, WidthMm: 6, HeightMm: 4));

        AssertClosedOutward(mesh);
        var size = MeshOperations.ComputeBounds(mesh).Size;
        Assert.Equal((10.0, 4.0, 6.0), (Math.Round(size.X, 4), Math.Round(size.Y, 4), Math.Round(size.Z, 4)));
        Assert.Equal(240, MeshOperations.SignedVolume(mesh), 3);
    }

    [Theory]
    [InlineData('x', 0)]
    [InlineData('y', 1)]
    [InlineData('z', 2)]
    public void Rod_IsAClosedCylinderAlongTheAxis(char axis, int lengthAxis)
    {
        var mesh = PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: 25, DiameterMm: 3, Axis: axis));

        AssertClosedOutward(mesh);
        var size = MeshOperations.ComputeBounds(mesh).Size;
        Assert.Equal(25, size[lengthAxis], 4);
        Assert.Equal(3, Math.Max(size[(lengthAxis + 1) % 3], size[(lengthAxis + 2) % 3]), 3); // vertices lie on the nominal circle
        Assert.Equal(PolygonArea(1.5) * 25, MeshOperations.SignedVolume(mesh), 2);
    }

    [Fact]
    public void Tube_HasABoreAndTheWallVolume()
    {
        var mesh = PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Tube, LengthMm: 50, OuterDiameterMm: 5, InnerDiameterMm: 3));

        AssertClosedOutward(mesh);
        Assert.Equal((PolygonArea(2.5) - PolygonArea(1.5)) * 50, MeshOperations.SignedVolume(mesh), 2);
        Assert.Equal(50, MeshOperations.ComputeBounds(mesh).Size.X, 4);

        // The axis passes through the bore: no triangle has a vertex inside the inner radius.
        for (var i = 0; i < mesh.Positions.Length; i += 3)
        {
            var r = Math.Sqrt(mesh.Positions[i + 1] * mesh.Positions[i + 1] + mesh.Positions[i + 2] * mesh.Positions[i + 2]);
            Assert.True(r > 1.49, $"vertex at radius {r}");
        }
    }

    [Theory]
    [InlineData(PrimitiveKinds.Tube, 3.0, 3.0, "smaller than the outer")]
    [InlineData(PrimitiveKinds.Tube, 3.0, 2.95, "at least 0.05 mm")]
    public void Tube_RejectsImpossibleWalls(string kind, double outer, double inner, string message)
    {
        var ex = Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(kind, LengthMm: 10, OuterDiameterMm: outer, InnerDiameterMm: inner)));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Build_RejectsMissingOrOutOfRangeSizesAndBadInput()
    {
        Assert.Contains("thickness is required", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Plate, LengthMm: 1, WidthMm: 1))).Message);
        Assert.Contains("between 0.05", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: 6000, DiameterMm: 1))).Message);
        Assert.Contains("between 0.05", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: -1, DiameterMm: 1))).Message);
        Assert.Contains("required", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: double.NaN, DiameterMm: 1))).Message);
        Assert.Contains("axis", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: 1, DiameterMm: 1, Axis: 'w'))).Message);
        Assert.Contains("Unknown primitive", Assert.Throws<InvalidPrimitiveException>(() => PrimitiveBuilder.Build(new PrimitiveSpec("sphere"))).Message);
        Assert.Contains("position", Assert.Throws<InvalidPrimitiveException>(() =>
            PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Box, LengthMm: 1, WidthMm: 1, HeightMm: 1, Center: new Vector3D(double.PositiveInfinity, 0, 0)))).Message);
    }

    [Theory]
    [InlineData(PrimitiveKinds.Plate, PartCategories.Sheet)]
    [InlineData(PrimitiveKinds.Rod, PartCategories.Rod)]
    [InlineData(PrimitiveKinds.Tube, PartCategories.Tube)]
    [InlineData(PrimitiveKinds.Box, PartCategories.Custom3D)]
    public void Category_FollowsTheShape(string kind, string category) => Assert.Equal(category, PrimitiveKinds.Category(kind));
}
