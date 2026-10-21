namespace ImageTo3D.Core.Geometry;

/// <summary>Edge-level facts about a triangle mesh (computed on vertex indices, so weld first).</summary>
public sealed record MeshTopologyReport(
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int InconsistentlyOrientedEdgeCount,
    int DegenerateTriangleCount,
    int ComponentCount)
{
    /// <summary>Every edge is shared by exactly two triangles with opposite directions.</summary>
    public bool IsClosed => BoundaryEdgeCount == 0 && NonManifoldEdgeCount == 0 && InconsistentlyOrientedEdgeCount == 0;
}

public static class MeshTopology
{
    /// <summary>
    /// Merges vertices whose positions coincide within <paramref name="tolerance"/> (snapped to a grid of that
    /// size). Exporters split vertices along UV seams and normal creases; without welding, a single solid looks
    /// like many open fragments. Triangles that collapse are dropped. Colours of the first merged vertex are kept.
    /// </summary>
    public static Mesh Weld(Mesh mesh, double tolerance)
    {
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Weld tolerance must be a positive finite number.");

        var p = mesh.Positions;
        var remap = new int[mesh.VertexCount];
        var cells = new Dictionary<(long, long, long), int>(mesh.VertexCount);
        var positions = new List<float>(p.Length);
        var colors = mesh.Colors is null ? null : new List<byte>(p.Length);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var key = ((long)Math.Round(p[v * 3] / tolerance), (long)Math.Round(p[v * 3 + 1] / tolerance), (long)Math.Round(p[v * 3 + 2] / tolerance));
            if (!cells.TryGetValue(key, out var target))
            {
                target = positions.Count / 3;
                cells[key] = target;
                positions.Add(p[v * 3]);
                positions.Add(p[v * 3 + 1]);
                positions.Add(p[v * 3 + 2]);
                colors?.AddRange([mesh.Colors![v * 3], mesh.Colors[v * 3 + 1], mesh.Colors[v * 3 + 2]]);
            }

            remap[v] = target;
        }

        var idx = mesh.Indices;
        var indices = new List<int>(idx.Length);
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = remap[idx[t]], b = remap[idx[t + 1]], c = remap[idx[t + 2]];
            if (a != b && b != c && a != c)
                indices.AddRange([a, b, c]);
        }

        return new Mesh(positions.ToArray(), indices.ToArray(), colors?.ToArray());
    }

    /// <summary>Weld tolerance relative to the bounding-box diagonal; small enough to keep real gaps apart.</summary>
    public static double DefaultWeldTolerance(Mesh mesh)
    {
        var size = MeshOperations.ComputeBounds(mesh).Size;
        var diagonal = Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z);
        return diagonal > 0 ? diagonal * 1e-6 : 1e-9;
    }

    public static MeshTopologyReport Analyze(Mesh mesh)
    {
        var idx = mesh.Indices;
        var p = mesh.Positions;
        var undirected = new Dictionary<long, int>(idx.Length);
        var directed = new HashSet<long>(idx.Length);
        long n = mesh.VertexCount;
        var inconsistent = 0;
        var degenerate = 0;
        for (var t = 0; t < idx.Length; t += 3)
        {
            if (TriangleArea(p, idx[t], idx[t + 1], idx[t + 2]) <= 0)
                degenerate++;
            for (var k = 0; k < 3; k++)
            {
                long a = idx[t + k], b = idx[t + (k + 1) % 3];
                if (!directed.Add(a * n + b))
                    inconsistent++;
                var key = Math.Min(a, b) * n + Math.Max(a, b);
                undirected[key] = undirected.GetValueOrDefault(key) + 1;
            }
        }

        var boundary = 0;
        var nonManifold = 0;
        foreach (var count in undirected.Values)
        {
            if (count == 1)
                boundary++;
            else if (count > 2)
                nonManifold++;
        }

        return new MeshTopologyReport(boundary, nonManifold, inconsistent, degenerate, ComponentLabels(mesh, out _));
    }

    /// <summary>
    /// Splits a mesh into its connected components (triangles sharing a vertex), largest first by
    /// triangle count. Each component gets compact vertex arrays; colours are carried over.
    /// </summary>
    public static IReadOnlyList<Mesh> ConnectedComponents(Mesh mesh)
    {
        var count = ComponentLabels(mesh, out var labels);
        if (count <= 1)
            return count == 0 ? [] : [Compact(mesh)];

        var idx = mesh.Indices;
        var perComponent = new List<int>[count];
        for (var c = 0; c < count; c++)
            perComponent[c] = [];
        for (var t = 0; t < idx.Length; t += 3)
            perComponent[labels[t / 3]].Add(t);

        return perComponent
            .OrderByDescending(list => list.Count)
            .Select(list => Extract(mesh, list))
            .ToList();
    }

    /// <summary>Drops unreferenced vertices.</summary>
    public static Mesh Compact(Mesh mesh)
    {
        var triangles = new List<int>(mesh.TriangleCount);
        for (var t = 0; t < mesh.Indices.Length; t += 3)
            triangles.Add(t);
        return Extract(mesh, triangles);
    }

    /// <summary>Per-triangle component label; returns the number of components.</summary>
    private static int ComponentLabels(Mesh mesh, out int[] triangleLabels)
    {
        var idx = mesh.Indices;
        var parent = new int[mesh.VertexCount];
        for (var i = 0; i < parent.Length; i++)
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

        for (var t = 0; t < idx.Length; t += 3)
        {
            var a = Find(idx[t]);
            parent[Find(idx[t + 1])] = a;
            parent[Find(idx[t + 2])] = a;
        }

        var labelOfRoot = new Dictionary<int, int>();
        triangleLabels = new int[idx.Length / 3];
        for (var t = 0; t < idx.Length; t += 3)
        {
            var root = Find(idx[t]);
            if (!labelOfRoot.TryGetValue(root, out var label))
            {
                label = labelOfRoot.Count;
                labelOfRoot[root] = label;
            }

            triangleLabels[t / 3] = label;
        }

        return labelOfRoot.Count;
    }

    private static Mesh Extract(Mesh mesh, List<int> triangleOffsets)
    {
        var remap = new Dictionary<int, int>();
        var positions = new List<float>();
        var colors = mesh.Colors is null ? null : new List<byte>();
        var indices = new List<int>(triangleOffsets.Count * 3);
        foreach (var t in triangleOffsets)
        {
            for (var k = 0; k < 3; k++)
            {
                var v = mesh.Indices[t + k];
                if (!remap.TryGetValue(v, out var mapped))
                {
                    mapped = positions.Count / 3;
                    remap[v] = mapped;
                    positions.AddRange([mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]]);
                    colors?.AddRange([mesh.Colors![v * 3], mesh.Colors[v * 3 + 1], mesh.Colors[v * 3 + 2]]);
                }

                indices.Add(mapped);
            }
        }

        return new Mesh(positions.ToArray(), indices.ToArray(), colors?.ToArray());
    }

    internal static double TriangleArea(float[] p, int a, int b, int c)
    {
        var pa = Vec3.At(p, a);
        return (Vec3.At(p, b) - pa).Cross(Vec3.At(p, c) - pa).Length / 2;
    }

    /// <summary>Total surface area.</summary>
    public static double SurfaceArea(Mesh mesh)
    {
        double area = 0;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
            area += TriangleArea(mesh.Positions, mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]);
        return area;
    }
}
