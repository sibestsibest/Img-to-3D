using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

public class PlaneSplitterTests
{
    private static readonly Vector3D Up = new(0, 0, 1);

    private static void AssertClosedSolid(PlaneSplitSide? side, double expectedVolume, double tolerance = 1e-3)
    {
        Assert.NotNull(side);
        Assert.True(side.Capped, "the cut must be capped");
        Assert.True(side.Topology.IsClosed, $"open={side.Topology.BoundaryEdgeCount} nonManifold={side.Topology.NonManifoldEdgeCount} inconsistent={side.Topology.InconsistentlyOrientedEdgeCount}");
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(side.Mesh));
        Assert.Equal(0, side.Topology.DegenerateTriangleCount);
        Assert.Equal(expectedVolume, MeshOperations.SignedVolume(side.Mesh), tolerance);
    }

    [Fact]
    public void Cube_CutInHalf_TwoClosedHalvesWithSquareCaps()
    {
        var result = PlaneSplitter.Split(PartFixtures.Box(10, 10, 10), new Vector3D(5, 5, 5), Up);

        AssertClosedSolid(result.Below, 500);
        AssertClosedSolid(result.Above, 500);
        Assert.Equal(100, result.Below!.CapArea, 6);
        Assert.Equal(100, result.Above!.CapArea, 6);
        Assert.Equal(1, result.ClosedContourCount);
        Assert.Equal(0, result.OpenContourCount);
        Assert.Equal(40, result.ContourLength, 6);
        Assert.True(result.IsCompleteSolidSplit);
        Assert.Empty(result.Issues);

        var below = MeshOperations.ComputeBounds(result.Below.Mesh);
        var above = MeshOperations.ComputeBounds(result.Above.Mesh);
        Assert.Equal(5, below.Max.Z, 6);
        Assert.Equal(5, above.Min.Z, 6);
        Assert.Equal(10, above.Max.Z, 6);
    }

    [Fact]
    public void Cube_ObliqueCut_VolumesSumToOriginal()
    {
        var result = PlaneSplitter.Split(PartFixtures.Box(10, 10, 10), new Vector3D(3, 5, 5), new Vector3D(1, 0.5, 0.25));

        Assert.True(result.IsCompleteSolidSplit);
        var total = MeshOperations.SignedVolume(result.Below!.Mesh) + MeshOperations.SignedVolume(result.Above!.Mesh);
        Assert.Equal(1000, total, 3);
    }

    [Fact]
    public void PlateWithHole_CutThroughThickness_AnnularCapsWithoutOverlap()
    {
        // 20 x 20 x 2 plate with a 10 x 10 hole, cut parallel to its faces: the cap is an annulus.
        // geometry3Sharp 1.0.324 fills both loops here (cap area 500 instead of 300).
        var plate = PartFixtures.RingPrism(PartFixtures.Rectangle(20, 20), PartFixtures.Rectangle(10, 10), 2);

        var result = PlaneSplitter.Split(plate, new Vector3D(0, 0, 1), Up);

        AssertClosedSolid(result.Below, 300);
        AssertClosedSolid(result.Above, 300);
        Assert.Equal(300, result.Below!.CapArea, 6);
        Assert.Equal(300, result.Above!.CapArea, 6);
        Assert.Equal(2, result.ClosedContourCount);
        Assert.Equal(1, MeshTopology.Analyze(result.Below.Mesh).ComponentCount);
    }

    [Fact]
    public void PlateWithHole_CutAcrossTheHole_TwoSeparateCapsPerSide()
    {
        var plate = PartFixtures.RingPrism(PartFixtures.Rectangle(20, 20), PartFixtures.Rectangle(10, 10), 2);

        var result = PlaneSplitter.Split(plate, new Vector3D(1, 0, 0), new Vector3D(1, 0, 0));

        Assert.Equal(2, result.ClosedContourCount);
        // x in [-10, 1]: full strip [-10,-5] (5x20) plus two arms [-5,1] (6x5 each), all 2 thick.
        AssertClosedSolid(result.Below, (5 * 20 + 2 * 6 * 5) * 2);
        AssertClosedSolid(result.Above, (5 * 20 + 2 * 4 * 5) * 2);
        Assert.Equal(2 * 5 * 2, result.Below!.CapArea, 6);
    }

    [Fact]
    public void Cylinder_CutAcrossAxis_CapAreaMatchesPolygon()
    {
        const int segments = 64;
        var cylinder = PartFixtures.Prism(PartFixtures.Circle(2.5, segments), 100, levels: 4);
        var polygonArea = 0.5 * segments * Math.Sin(2 * Math.PI / segments) * 2.5 * 2.5;

        var result = PlaneSplitter.Split(cylinder, new Vector3D(0, 0, 40), Up);

        AssertClosedSolid(result.Below, polygonArea * 40);
        AssertClosedSolid(result.Above, polygonArea * 60);
        Assert.Equal(polygonArea, result.Below!.CapArea, 6);
    }

    [Fact]
    public void Tube_CutAcrossAxis_AnnularCap()
    {
        const int segments = 48;
        var tube = PartFixtures.RingPrism(PartFixtures.Circle(5, segments), PartFixtures.Circle(3, segments), 50, levels: 2);
        var ringArea = 0.5 * segments * Math.Sin(2 * Math.PI / segments) * (25 - 9);

        var result = PlaneSplitter.Split(tube, new Vector3D(0, 0, 12), Up);

        AssertClosedSolid(result.Below, ringArea * 12);
        AssertClosedSolid(result.Above, ringArea * 38);
        Assert.Equal(ringArea, result.Above!.CapArea, 6);
    }

    [Fact]
    public void CutThroughExistingVertexRing_DoesNotCreateSlivers()
    {
        // levels: 4 puts a vertex ring exactly at z = 25.
        var cylinder = PartFixtures.Prism(PartFixtures.Circle(5, 32), 100, levels: 4);

        var result = PlaneSplitter.Split(cylinder, new Vector3D(0, 0, 25), Up);

        Assert.True(result.IsCompleteSolidSplit);
        Assert.Equal(0, result.Below!.Topology.DegenerateTriangleCount);
        Assert.Equal(0, result.Above!.Topology.DegenerateTriangleCount);
        // The ring is reused as the contour: 32 vertices, no new cut points.
        Assert.Equal(32 * 2 + 1, result.Below.Mesh.VertexCount);
    }

    [Fact]
    public void TwoDisconnectedBoxes_BothCapped()
    {
        var mesh = PartFixtures.Merge(PartFixtures.Box(10, 10, 10), PartFixtures.Box(4, 4, 20, ox: 20));

        var result = PlaneSplitter.Split(mesh, new Vector3D(0, 0, 5), Up);

        Assert.Equal(2, result.ClosedContourCount);
        AssertClosedSolid(result.Below, 500 + 80);
        AssertClosedSolid(result.Above, 500 + 240);
        Assert.Equal(2, result.Below!.CapLoopCount);
    }

    [Fact]
    public void OpenMesh_IsNotCappedAndSaysWhy()
    {
        var open = PartFixtures.Without(PartFixtures.Box(10, 10, 10), (x, _, _) => x > 9.99);

        var result = PlaneSplitter.Split(open, new Vector3D(0, 0, 5), Up);

        Assert.False(result.IsCompleteSolidSplit);
        Assert.Equal(1, result.OpenContourCount);
        Assert.False(result.Below!.Capped);
        Assert.False(result.Above!.Capped);
        Assert.False(result.Below.Topology.IsClosed);
        Assert.Contains(result.Issues, i => i.Code == "cut_contour_open" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void PlaneOutsidePart_ReportsMiss()
    {
        var result = PlaneSplitter.Split(PartFixtures.Box(10, 10, 10), new Vector3D(0, 0, 50), Up);

        Assert.Null(result.Below);
        Assert.Null(result.Above);
        Assert.Contains(result.Issues, i => i.Code == "plane_misses_part");
    }

    [Fact]
    public void PlaneOnFace_ReportsMiss()
    {
        var result = PlaneSplitter.Split(PartFixtures.Box(10, 10, 10), new Vector3D(0, 0, 10), Up);

        Assert.Contains(result.Issues, i => i.Code == "plane_misses_part");
    }

    [Fact]
    public void ZeroNormal_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaneSplitter.Split(PartFixtures.Box(1, 1, 1), new Vector3D(0, 0, 0), new Vector3D(0, 0, 0)));
    }

    [Fact]
    public void UnweldedInput_IsWeldedBeforeCutting()
    {
        var result = PlaneSplitter.Split(PartFixtures.Soup(PartFixtures.Box(10, 10, 10)), new Vector3D(0, 0, 5), Up);

        Assert.True(result.IsCompleteSolidSplit);
    }

    [Theory]
    [InlineData(0, 0, 1, 0)]     // horizontal through the ring: annular cap per side
    [InlineData(1, 0, 0, 0)]     // vertical through the hole: two caps per side
    [InlineData(1, 1, 1, 1.3)]   // oblique, off-grid
    public void MarchingCubesTorus_ClosedHalvesWhoseVolumesAddUp(double nx, double ny, double nz, double offsetFromCentre)
    {
        // Same kind of mesh the TripoSR pipeline produces: welded marching-cubes output.
        const int n = 64;
        var field = new float[n * n * n];
        for (var x = 0; x < n; x++)
            for (var y = 0; y < n; y++)
                for (var z = 0; z < n; z++)
                {
                    double dx = x - 31.5, dy = y - 31.5, dz = z - 31.5;
                    var ring = Math.Sqrt(dx * dx + dy * dy) - 18;
                    field[(x * n + y) * n + z] = (float)(7 - Math.Sqrt(ring * ring + dz * dz));
                }

        var torus = MarchingCubes.Extract(field, n, n, n, 0f);
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(torus));
        var total = MeshOperations.SignedVolume(torus);

        var normal = new Vector3D(nx, ny, nz);
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        var s = offsetFromCentre / length;
        var point = new Vector3D(31.5 + nx * s, 31.5 + ny * s, 31.5 + nz * s);
        var result = PlaneSplitter.Split(torus, point, normal);

        Assert.True(result.IsCompleteSolidSplit, string.Join(" ", result.Issues.Select(i => i.Message)));
        Assert.True(result.ClosedContourCount >= 2);
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(result.Below!.Mesh));
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(result.Above!.Mesh));
        var sum = MeshOperations.SignedVolume(result.Below.Mesh) + MeshOperations.SignedVolume(result.Above.Mesh);
        Assert.Equal(total, sum, total * 1e-6);
    }

    [Fact]
    public void Colours_AreInterpolatedAtCutPoints()
    {
        var cube = MeshOperations.Scale(GeometryTestData.UnitCube(), 10);

        var result = PlaneSplitter.Split(cube, new Vector3D(5, 5, 5), Up);

        Assert.NotNull(result.Below!.Mesh.Colors);
        Assert.Equal(result.Below.Mesh.VertexCount * 3, result.Below.Mesh.Colors!.Length);
    }
}
