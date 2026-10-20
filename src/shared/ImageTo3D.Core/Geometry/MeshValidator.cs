namespace ImageTo3D.Core.Geometry;

public sealed record MeshValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    int VertexCount,
    int TriangleCount,
    int DegenerateTriangleCount,
    BoundingBox Bounds);

public static class MeshValidator
{
    /// <summary>
    /// Checks that a mesh contains usable geometry: at least one non-degenerate triangle,
    /// finite coordinates, in-range indices and a non-zero bounding box.
    /// </summary>
    public static MeshValidationResult Validate(Mesh mesh)
    {
        var errors = new List<string>();

        if (mesh.VertexCount == 0)
            errors.Add("Mesh has no vertices.");
        if (mesh.TriangleCount == 0)
            errors.Add("Mesh has no triangles.");

        foreach (var value in mesh.Positions)
        {
            if (!float.IsFinite(value))
            {
                errors.Add("Mesh contains non-finite vertex coordinates.");
                break;
            }
        }

        var degenerate = 0;
        var idx = mesh.Indices;
        var p = mesh.Positions;
        var indicesInRange = true;
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if ((uint)a >= (uint)mesh.VertexCount || (uint)b >= (uint)mesh.VertexCount || (uint)c >= (uint)mesh.VertexCount)
            {
                indicesInRange = false;
                break;
            }

            if (a == b || b == c || a == c || TriangleArea2(p, a, b, c) <= 0)
                degenerate++;
        }

        if (!indicesInRange)
            errors.Add("Mesh contains triangle indices outside the vertex range.");
        else if (mesh.TriangleCount > 0 && degenerate == mesh.TriangleCount)
            errors.Add("All triangles are degenerate (zero area).");

        var bounds = MeshOperations.ComputeBounds(mesh);
        var size = bounds.Size;
        if (errors.Count == 0 && !(size.X > 0 && size.Y > 0 && size.Z > 0))
            errors.Add("Mesh bounding box has zero extent on at least one axis.");

        return new MeshValidationResult(errors.Count == 0, errors, mesh.VertexCount, mesh.TriangleCount, degenerate, bounds);
    }

    private static double TriangleArea2(float[] p, int a, int b, int c)
    {
        a *= 3;
        b *= 3;
        c *= 3;
        double e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
        double e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
        var nx = e1y * e2z - e1z * e2y;
        var ny = e1z * e2x - e1x * e2z;
        var nz = e1x * e2y - e1y * e2x;
        return Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }
}
