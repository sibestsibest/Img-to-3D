using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

internal static class GeometryTestData
{
    /// <summary>Density-like field: positive inside a sphere of the given radius (in grid units).</summary>
    public static float[] SphereField(int n, double radius)
    {
        var field = new float[n * n * n];
        var c = (n - 1) / 2.0;
        for (var x = 0; x < n; x++)
            for (var y = 0; y < n; y++)
                for (var z = 0; z < n; z++)
                {
                    var d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c) + (z - c) * (z - c));
                    field[(x * n + y) * n + z] = (float)(radius - d);
                }

        return field;
    }

    public static Mesh UnitCube()
    {
        float[] p =
        [
            0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0,
            0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1,
        ];
        int[] i =
        [
            0, 2, 1, 0, 3, 2, // z = 0 (normal -Z)
            4, 5, 6, 4, 6, 7, // z = 1 (normal +Z)
            0, 1, 5, 0, 5, 4, // y = 0
            3, 6, 2, 3, 7, 6, // y = 1
            0, 4, 7, 0, 7, 3, // x = 0
            1, 2, 6, 1, 6, 5, // x = 1
        ];
        byte[] colors = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 0, 255, 255, 255, 0, 255, 128, 128, 128, 10, 20, 30];
        return new Mesh(p, i, colors);
    }

    /// <summary>Every undirected edge must be shared by exactly two triangles with opposite directions.</summary>
    public static bool IsClosedAndConsistentlyOriented(Mesh mesh)
    {
        var directed = new HashSet<(int, int)>();
        var idx = mesh.Indices;
        for (var t = 0; t < idx.Length; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var a = idx[t + k];
                var b = idx[t + (k + 1) % 3];
                if (!directed.Add((a, b)))
                    return false;
            }
        }

        return directed.All(e => directed.Contains((e.Item2, e.Item1)));
    }
}
