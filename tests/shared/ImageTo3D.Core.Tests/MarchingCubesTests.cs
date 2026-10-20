using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

public class MarchingCubesTests
{
    [Fact]
    public void Sphere_ProducesClosedOutwardMeshWithExpectedVolume()
    {
        const int n = 48;
        const double radius = 18.0;

        var mesh = MarchingCubes.Extract(GeometryTestData.SphereField(n, radius), n, n, n, 0f);

        Assert.True(mesh.TriangleCount > 1000);
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(mesh), "Sphere mesh must be watertight with consistent winding.");

        var volume = MeshOperations.SignedVolume(mesh);
        var expected = 4.0 / 3.0 * Math.PI * radius * radius * radius;
        Assert.True(volume > 0, "Signed volume must be positive (outward-facing triangles).");
        Assert.InRange(volume / expected, 0.98, 1.02);
    }

    [Fact]
    public void Sphere_BoundsMatchRadius()
    {
        const int n = 40;
        var mesh = MarchingCubes.Extract(GeometryTestData.SphereField(n, 15), n, n, n, 0f);

        var size = MeshOperations.ComputeBounds(mesh).Size;
        Assert.InRange(size.X, 29.5, 30.5);
        Assert.InRange(size.Y, 29.5, 30.5);
        Assert.InRange(size.Z, 29.5, 30.5);
    }

    [Fact]
    public void EmptyField_ProducesEmptyMesh()
    {
        var field = new float[8 * 8 * 8];
        Array.Fill(field, -1f);

        var mesh = MarchingCubes.Extract(field, 8, 8, 8, 0f);

        Assert.Equal(0, mesh.TriangleCount);
        Assert.False(MeshValidator.Validate(mesh).IsValid);
    }

    [Fact]
    public void FieldSizeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => MarchingCubes.Extract(new float[10], 8, 8, 8, 0f));
    }

    [Fact]
    public void Cancellation_IsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            MarchingCubes.Extract(GeometryTestData.SphereField(16, 5), 16, 16, 16, 0f, cts.Token));
    }

    [Fact]
    public void KeepLargestComponent_RemovesSmallFragment()
    {
        const int n = 40;
        var field = GeometryTestData.SphereField(n, 12);
        // Add a small separate blob near a corner.
        for (var x = 1; x < 5; x++)
            for (var y = 1; y < 5; y++)
                for (var z = 1; z < 5; z++)
                    field[(x * n + y) * n + z] = 1f;

        var mesh = MarchingCubes.Extract(field, n, n, n, 0f);
        var cleaned = MeshOperations.KeepLargestComponent(mesh);

        Assert.True(cleaned.TriangleCount < mesh.TriangleCount);
        Assert.True(GeometryTestData.IsClosedAndConsistentlyOriented(cleaned));
        Assert.InRange(MeshOperations.ComputeBounds(cleaned).Size.X, 23, 25);
    }
}
