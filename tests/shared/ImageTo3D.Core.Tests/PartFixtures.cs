using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

/// <summary>Closed, outward-facing test solids built without the code under test.</summary>
internal static class PartFixtures
{
    public static Mesh Box(double sx, double sy, double sz, double ox = 0, double oy = 0, double oz = 0) =>
        Prism(Rectangle(sx, sy, ox + sx / 2, oy + sy / 2), sz, oz, levels: 1, fanCaps: false);

    /// <summary>Counter-clockwise rectangle centred on (cx, cy).</summary>
    public static (double X, double Y)[] Rectangle(double sx, double sy, double cx = 0, double cy = 0) =>
        [(cx - sx / 2, cy - sy / 2), (cx + sx / 2, cy - sy / 2), (cx + sx / 2, cy + sy / 2), (cx - sx / 2, cy + sy / 2)];

    /// <summary>Counter-clockwise regular polygon approximating a circle.</summary>
    public static (double X, double Y)[] Circle(double radius, int segments) =>
        Enumerable.Range(0, segments).Select(i => (radius * Math.Cos(2 * Math.PI * i / segments), radius * Math.Sin(2 * Math.PI * i / segments))).ToArray();

    /// <summary>
    /// Extrudes a convex counter-clockwise polygon along +Z from z0 to z0 + height, with
    /// <paramref name="levels"/> rings of side faces. Caps are fans from the centroid, or
    /// a fan from vertex 0 when <paramref name="fanCaps"/> is false.
    /// </summary>
    public static Mesh Prism((double X, double Y)[] outline, double height, double z0 = 0, int levels = 1, bool fanCaps = true)
    {
        var p = new List<float>();
        var idx = new List<int>();
        var n = outline.Length;
        int Vertex(double x, double y, double z)
        {
            p.AddRange([(float)x, (float)y, (float)z]);
            return p.Count / 3 - 1;
        }

        var rings = new int[levels + 1][];
        for (var l = 0; l <= levels; l++)
            rings[l] = outline.Select(o => Vertex(o.X, o.Y, z0 + height * l / levels)).ToArray();
        for (var l = 0; l < levels; l++)
        {
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                idx.AddRange([rings[l][i], rings[l][j], rings[l + 1][j]]);
                idx.AddRange([rings[l][i], rings[l + 1][j], rings[l + 1][i]]);
            }
        }

        var bottom = rings[0];
        var top = rings[levels];
        if (fanCaps)
        {
            var cx = outline.Average(o => o.X);
            var cy = outline.Average(o => o.Y);
            var cb = Vertex(cx, cy, z0);
            var ct = Vertex(cx, cy, z0 + height);
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                idx.AddRange([cb, bottom[j], bottom[i]]);
                idx.AddRange([ct, top[i], top[j]]);
            }
        }
        else
        {
            for (var i = 1; i + 1 < n; i++)
            {
                idx.AddRange([bottom[0], bottom[i + 1], bottom[i]]);
                idx.AddRange([top[0], top[i], top[i + 1]]);
            }
        }

        return new Mesh(p.ToArray(), idx.ToArray());
    }

    /// <summary>
    /// Extrudes the region between two concentric counter-clockwise outlines with the same vertex
    /// count (a plate with a hole, a tube). Caps are quad strips between the outlines.
    /// </summary>
    public static Mesh RingPrism((double X, double Y)[] outer, (double X, double Y)[] inner, double height, int levels = 1)
    {
        if (outer.Length != inner.Length)
            throw new ArgumentException("Outlines need the same vertex count.");
        var p = new List<float>();
        var idx = new List<int>();
        var n = outer.Length;
        int Vertex(double x, double y, double z)
        {
            p.AddRange([(float)x, (float)y, (float)z]);
            return p.Count / 3 - 1;
        }

        var o = new int[levels + 1][];
        var h = new int[levels + 1][];
        for (var l = 0; l <= levels; l++)
        {
            o[l] = outer.Select(v => Vertex(v.X, v.Y, height * l / levels)).ToArray();
            h[l] = inner.Select(v => Vertex(v.X, v.Y, height * l / levels)).ToArray();
        }

        for (var l = 0; l < levels; l++)
        {
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                idx.AddRange([o[l][i], o[l][j], o[l + 1][j]]);
                idx.AddRange([o[l][i], o[l + 1][j], o[l + 1][i]]);
                idx.AddRange([h[l][j], h[l][i], h[l + 1][i]]); // inner wall faces the axis
                idx.AddRange([h[l][j], h[l + 1][i], h[l + 1][j]]);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            idx.AddRange([o[levels][i], o[levels][j], h[levels][j]]); // top ring
            idx.AddRange([o[levels][i], h[levels][j], h[levels][i]]);
            idx.AddRange([o[0][j], o[0][i], h[0][i]]); // bottom ring
            idx.AddRange([o[0][j], h[0][i], h[0][j]]);
        }

        return new Mesh(p.ToArray(), idx.ToArray());
    }

    public static Mesh Merge(params Mesh[] meshes)
    {
        var p = new List<float>();
        var idx = new List<int>();
        foreach (var m in meshes)
        {
            var offset = p.Count / 3;
            p.AddRange(m.Positions);
            idx.AddRange(m.Indices.Select(i => i + offset));
        }

        return new Mesh(p.ToArray(), idx.ToArray());
    }

    /// <summary>Removes the triangles whose centroid satisfies <paramref name="drop"/> (makes an open mesh).</summary>
    public static Mesh Without(Mesh mesh, Func<double, double, double, bool> drop)
    {
        var keep = new List<int>();
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            double cx = 0, cy = 0, cz = 0;
            for (var k = 0; k < 3; k++)
            {
                var v = mesh.Indices[t + k] * 3;
                cx += mesh.Positions[v] / 3.0;
                cy += mesh.Positions[v + 1] / 3.0;
                cz += mesh.Positions[v + 2] / 3.0;
            }

            if (!drop(cx, cy, cz))
                keep.AddRange(mesh.Indices.AsSpan(t, 3).ToArray());
        }

        return new Mesh(mesh.Positions, keep.ToArray(), mesh.Colors);
    }

    /// <summary>Unwelded copy: every triangle gets its own three vertices (what UV seams do to exports).</summary>
    public static Mesh Soup(Mesh mesh)
    {
        var p = new float[mesh.Indices.Length * 3];
        var idx = new int[mesh.Indices.Length];
        for (var i = 0; i < mesh.Indices.Length; i++)
        {
            Array.Copy(mesh.Positions, mesh.Indices[i] * 3, p, i * 3, 3);
            idx[i] = i;
        }

        return new Mesh(p, idx);
    }

    public static Mesh Rotate(Mesh mesh, double degreesAboutZ)
    {
        var a = degreesAboutZ * Math.PI / 180;
        double c = Math.Cos(a), s = Math.Sin(a);
        return MeshOperations.Transform(mesh, [c, -s, 0, s, c, 0, 0, 0, 1]);
    }
}
