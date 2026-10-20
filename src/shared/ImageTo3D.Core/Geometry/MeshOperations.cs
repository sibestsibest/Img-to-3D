namespace ImageTo3D.Core.Geometry;

public static class MeshOperations
{
    public static BoundingBox ComputeBounds(Mesh mesh)
    {
        if (mesh.VertexCount == 0)
            return new BoundingBox(default, default);

        var p = mesh.Positions;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var i = 0; i < p.Length; i += 3)
        {
            minX = Math.Min(minX, p[i]);
            maxX = Math.Max(maxX, p[i]);
            minY = Math.Min(minY, p[i + 1]);
            maxY = Math.Max(maxY, p[i + 1]);
            minZ = Math.Min(minZ, p[i + 2]);
            maxZ = Math.Max(maxZ, p[i + 2]);
        }

        return new BoundingBox(new Vector3D(minX, minY, minZ), new Vector3D(maxX, maxY, maxZ));
    }

    /// <summary>Applies p' = M * p + t to every vertex. M is row-major 3x3.</summary>
    public static Mesh Transform(Mesh mesh, ReadOnlySpan<double> matrix3x3, Vector3D translation = default)
    {
        if (matrix3x3.Length != 9)
            throw new ArgumentException("Matrix must have 9 elements.", nameof(matrix3x3));

        var src = mesh.Positions;
        var dst = new float[src.Length];
        for (var i = 0; i < src.Length; i += 3)
        {
            double x = src[i], y = src[i + 1], z = src[i + 2];
            dst[i] = (float)(matrix3x3[0] * x + matrix3x3[1] * y + matrix3x3[2] * z + translation.X);
            dst[i + 1] = (float)(matrix3x3[3] * x + matrix3x3[4] * y + matrix3x3[5] * z + translation.Y);
            dst[i + 2] = (float)(matrix3x3[6] * x + matrix3x3[7] * y + matrix3x3[8] * z + translation.Z);
        }

        var indices = mesh.Indices;
        if (Determinant(matrix3x3) < 0)
        {
            // A reflection flips orientation; swap winding to keep faces outward.
            indices = (int[])indices.Clone();
            for (var i = 0; i < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        }

        return new Mesh(dst, indices, mesh.Colors);
    }

    public static Mesh Scale(Mesh mesh, double factor)
    {
        if (!(factor > 0) || double.IsInfinity(factor))
            throw new ArgumentOutOfRangeException(nameof(factor), "Scale factor must be a positive finite number.");
        return Transform(mesh, [factor, 0, 0, 0, factor, 0, 0, 0, factor]);
    }

    /// <summary>
    /// Translates the mesh so its bounding box is centred on X/Z and rests on Y = 0 (Y-up),
    /// which is convenient both for the viewer and for placing on a print bed after conversion.
    /// </summary>
    public static Mesh CenterOnGround(Mesh mesh)
    {
        var b = ComputeBounds(mesh);
        var c = b.Center;
        return Transform(mesh, [1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3D(-c.X, -b.Min.Y, -c.Z));
    }

    /// <summary>Converts a Y-up mesh to Z-up (rotation of +90 degrees about X): (x, y, z) -> (x, -z, y).</summary>
    public static Mesh YUpToZUp(Mesh mesh) => Transform(mesh, [1, 0, 0, 0, 0, -1, 0, 1, 0]);

    /// <summary>Area-weighted per-vertex normals (unit length; zero for isolated vertices).</summary>
    public static float[] ComputeVertexNormals(Mesh mesh)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        var n = new float[p.Length];
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
            float e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
            float e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
            var nx = e1y * e2z - e1z * e2y;
            var ny = e1z * e2x - e1x * e2z;
            var nz = e1x * e2y - e1y * e2x;
            foreach (var v in (ReadOnlySpan<int>)[a, b, c])
            {
                n[v] += nx;
                n[v + 1] += ny;
                n[v + 2] += nz;
            }
        }

        for (var i = 0; i < n.Length; i += 3)
        {
            var len = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (len > 0)
            {
                n[i] /= len;
                n[i + 1] /= len;
                n[i + 2] /= len;
            }
        }

        return n;
    }

    /// <summary>Signed volume (positive for a closed, outward-facing mesh).</summary>
    public static double SignedVolume(Mesh mesh)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        double volume = 0;
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
            volume += p[a] * ((double)p[b + 1] * p[c + 2] - (double)p[b + 2] * p[c + 1])
                    - p[a + 1] * ((double)p[b] * p[c + 2] - (double)p[b + 2] * p[c])
                    + p[a + 2] * ((double)p[b] * p[c + 1] - (double)p[b + 1] * p[c]);
        }

        return volume / 6.0;
    }

    /// <summary>
    /// Keeps only the largest connected component (by triangle count). Marching cubes on a
    /// learned density field often produces small floating fragments that are not printable.
    /// </summary>
    public static Mesh KeepLargestComponent(Mesh mesh)
    {
        var vertexCount = mesh.VertexCount;
        if (vertexCount == 0)
            return mesh;

        var parent = new int[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            parent[i] = i;

        int Find(int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }

            return v;
        }

        var idx = mesh.Indices;
        for (var t = 0; t < idx.Length; t += 3)
        {
            var a = Find(idx[t]);
            var b = Find(idx[t + 1]);
            var c = Find(idx[t + 2]);
            parent[b] = a;
            parent[Find(c)] = a;
        }

        var triangleCountByRoot = new Dictionary<int, int>();
        for (var t = 0; t < idx.Length; t += 3)
        {
            var root = Find(idx[t]);
            triangleCountByRoot[root] = triangleCountByRoot.GetValueOrDefault(root) + 1;
        }

        if (triangleCountByRoot.Count <= 1)
            return mesh;

        var keep = triangleCountByRoot.MaxBy(kv => kv.Value).Key;
        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        var positions = new List<float>();
        var colors = mesh.Colors is null ? null : new List<byte>();
        var indices = new List<int>();
        for (var t = 0; t < idx.Length; t += 3)
        {
            if (Find(idx[t]) != keep)
                continue;
            for (var k = 0; k < 3; k++)
            {
                var v = idx[t + k];
                if (remap[v] < 0)
                {
                    remap[v] = positions.Count / 3;
                    positions.Add(mesh.Positions[v * 3]);
                    positions.Add(mesh.Positions[v * 3 + 1]);
                    positions.Add(mesh.Positions[v * 3 + 2]);
                    if (colors is not null)
                    {
                        colors.Add(mesh.Colors![v * 3]);
                        colors.Add(mesh.Colors[v * 3 + 1]);
                        colors.Add(mesh.Colors[v * 3 + 2]);
                    }
                }

                indices.Add(remap[v]);
            }
        }

        return new Mesh(positions.ToArray(), indices.ToArray(), colors?.ToArray());
    }

    private static double Determinant(ReadOnlySpan<double> m) =>
        m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
}
