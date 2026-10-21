using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

public class MeshTopologyTests
{
    [Fact]
    public void Analyze_ClosedBox_IsClosedSingleComponent()
    {
        var report = MeshTopology.Analyze(PartFixtures.Box(10, 20, 5));

        Assert.True(report.IsClosed);
        Assert.Equal(1, report.ComponentCount);
        Assert.Equal(0, report.DegenerateTriangleCount);
    }

    [Fact]
    public void Weld_TriangleSoup_RecoversClosedSolid()
    {
        var soup = PartFixtures.Soup(PartFixtures.Box(10, 10, 10));
        Assert.Equal(36, soup.VertexCount);
        Assert.False(MeshTopology.Analyze(soup).IsClosed);

        var welded = MeshTopology.Weld(soup, 1e-4);

        Assert.Equal(8, welded.VertexCount);
        Assert.Equal(12, welded.TriangleCount);
        Assert.True(MeshTopology.Analyze(welded).IsClosed);
        Assert.Equal(1000, MeshOperations.SignedVolume(welded), 3);
    }

    [Fact]
    public void Weld_DropsTrianglesThatCollapse()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 1e-7f, 0, 0], [0, 1, 2, 0, 3, 2]);

        var welded = MeshTopology.Weld(mesh, 1e-4);

        Assert.Equal(1, welded.TriangleCount);
    }

    [Fact]
    public void Weld_RejectsNonPositiveTolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshTopology.Weld(PartFixtures.Box(1, 1, 1), 0));
    }

    [Fact]
    public void ConnectedComponents_TwoDisconnectedBoxes_LargestFirst()
    {
        var mesh = PartFixtures.Merge(PartFixtures.Box(10, 10, 10), PartFixtures.Prism(PartFixtures.Circle(3, 16), 30, levels: 3, z0: 0));
        var shifted = MeshOperations.Transform(mesh, [1, 0, 0, 0, 1, 0, 0, 0, 1]);

        var components = MeshTopology.ConnectedComponents(shifted);

        Assert.Equal(2, components.Count);
        Assert.True(components[0].TriangleCount >= components[1].TriangleCount);
        Assert.All(components, c => Assert.True(MeshTopology.Analyze(c).IsClosed));
        Assert.Equal(mesh.TriangleCount, components.Sum(c => c.TriangleCount));
    }

    [Fact]
    public void ConnectedComponents_KeepsVertexColours()
    {
        var components = MeshTopology.ConnectedComponents(GeometryTestData.UnitCube());

        Assert.Single(components);
        Assert.NotNull(components[0].Colors);
    }

    [Fact]
    public void Analyze_OpenBox_CountsBoundaryEdges()
    {
        var open = PartFixtures.Without(PartFixtures.Box(10, 10, 10), (_, _, z) => z > 9.99);

        var report = MeshTopology.Analyze(open);

        Assert.False(report.IsClosed);
        Assert.Equal(4, report.BoundaryEdgeCount);
    }

    [Fact]
    public void Analyze_ThreeTrianglesOnOneEdge_IsNonManifold()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1], [0, 1, 2, 1, 0, 3, 0, 1, 4]);

        var report = MeshTopology.Analyze(mesh);

        Assert.Equal(1, report.NonManifoldEdgeCount);
        Assert.False(report.IsClosed);
    }

    [Fact]
    public void Analyze_CountsDegenerateTriangles()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 2, 0, 0, 0, 1, 0], [0, 1, 2, 0, 1, 3]);

        Assert.Equal(1, MeshTopology.Analyze(mesh).DegenerateTriangleCount);
    }

    [Fact]
    public void SurfaceArea_Box()
    {
        Assert.Equal(2 * (10 * 20 + 20 * 5 + 10 * 5), MeshTopology.SurfaceArea(PartFixtures.Box(10, 20, 5)), 6);
    }
}
