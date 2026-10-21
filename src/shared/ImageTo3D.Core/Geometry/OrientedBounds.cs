namespace ImageTo3D.Core.Geometry;

/// <summary>Oriented box: unit axes sorted by extent, longest first; extents are full side lengths.</summary>
public sealed record OrientedBox(Vector3D Center, IReadOnlyList<Vector3D> Axes, Vector3D Extents)
{
    public double Volume => Extents.X * Extents.Y * Extents.Z;
}

/// <summary>
/// Approximate minimum-volume oriented bounding box: candidate frames (world axes and principal
/// axes) are refined by small rotations about each axis, keeping whichever gives the smallest
/// volume. Deterministic; good enough for stock-size estimates, not a certified minimum.
/// </summary>
public static class OrientedBounds
{
    private const int SampleLimit = 4000;

    public static OrientedBox Compute(Mesh mesh)
    {
        if (mesh.VertexCount == 0)
            return new OrientedBox(default, [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)], default);

        var all = Points(mesh, int.MaxValue);
        var sample = mesh.VertexCount > SampleLimit ? Points(mesh, SampleLimit) : all;

        Vec3[] world = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        var best = Refine(sample, world);
        var principal = Refine(sample, PrincipalAxes(all));
        if (BoxVolume(sample, principal) < BoxVolume(sample, best) * (1 - 1e-9))
            best = principal;

        // Final extents from every vertex.
        var min = new double[3];
        var max = new double[3];
        for (var k = 0; k < 3; k++)
        {
            min[k] = double.MaxValue;
            max[k] = double.MinValue;
        }

        foreach (var p in all)
        {
            for (var k = 0; k < 3; k++)
            {
                var d = p.Dot(best[k]);
                min[k] = Math.Min(min[k], d);
                max[k] = Math.Max(max[k], d);
            }
        }

        var order = Enumerable.Range(0, 3).OrderByDescending(k => max[k] - min[k]).ThenBy(k => k).ToArray();
        var center = best[0] * ((min[0] + max[0]) / 2) + best[1] * ((min[1] + max[1]) / 2) + best[2] * ((min[2] + max[2]) / 2);
        var axes = order.Select(k => Canonical(best[k]).ToVector3D()).ToArray();
        var extents = order.Select(k => max[k] - min[k]).ToArray();
        return new OrientedBox(center.ToVector3D(), axes, new Vector3D(extents[0], extents[1], extents[2]));
    }

    private static List<Vec3> Points(Mesh mesh, int limit)
    {
        var step = Math.Max(1, mesh.VertexCount / limit);
        var list = new List<Vec3>(Math.Min(mesh.VertexCount, limit) + 1);
        for (var v = 0; v < mesh.VertexCount; v += step)
            list.Add(Vec3.At(mesh.Positions, v));
        return list;
    }

    /// <summary>Flip so the largest component is positive, for stable output.</summary>
    private static Vec3 Canonical(Vec3 a)
    {
        var largest = Math.Abs(a.X) >= Math.Abs(a.Y) && Math.Abs(a.X) >= Math.Abs(a.Z) ? a.X : Math.Abs(a.Y) >= Math.Abs(a.Z) ? a.Y : a.Z;
        return largest < 0 ? -a : a;
    }

    private static Vec3[] Refine(List<Vec3> points, Vec3[] frame)
    {
        var best = frame;
        var bestVolume = BoxVolume(points, frame);
        foreach (var range in new[] { 45.0, 5.0, 0.5 })
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var stepCount = 45;
                for (var s = -stepCount; s <= stepCount; s++)
                {
                    if (s == 0)
                        continue;
                    var angle = range * s / stepCount * Math.PI / 180;
                    var candidate = Rotate(best, axis, angle);
                    var volume = BoxVolume(points, candidate);
                    if (volume < bestVolume * (1 - 1e-12))
                    {
                        bestVolume = volume;
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private static Vec3[] Rotate(Vec3[] frame, int axis, double angle)
    {
        int i = (axis + 1) % 3, j = (axis + 2) % 3;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        var result = (Vec3[])frame.Clone();
        result[i] = frame[i] * c + frame[j] * s;
        result[j] = frame[j] * c - frame[i] * s;
        return result;
    }

    private static double BoxVolume(List<Vec3> points, Vec3[] frame)
    {
        double volume = 1;
        for (var k = 0; k < 3; k++)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var p in points)
            {
                var d = p.Dot(frame[k]);
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }

            // Flat parts have one extent near zero; keep the comparison meaningful.
            volume *= Math.Max(max - min, 1e-12);
        }

        return volume;
    }

    private static Vec3[] PrincipalAxes(List<Vec3> points)
    {
        var mean = new Vec3(0, 0, 0);
        foreach (var p in points)
            mean += p;
        mean *= 1.0 / points.Count;

        var m = new double[3, 3];
        foreach (var p in points)
        {
            var d = p - mean;
            double[] c = [d.X, d.Y, d.Z];
            for (var r = 0; r < 3; r++)
                for (var k = 0; k < 3; k++)
                    m[r, k] += c[r] * c[k];
        }

        var vectors = JacobiEigenvectors(m);
        var a0 = vectors[0].Normalized();
        var a1 = (vectors[1] - a0 * vectors[1].Dot(a0)).Normalized();
        if (!(a1.Length > 0.5))
            a1 = Math.Abs(a0.X) < 0.9 ? a0.Cross(new Vec3(1, 0, 0)).Normalized() : a0.Cross(new Vec3(0, 1, 0)).Normalized();
        return [a0, a1, a0.Cross(a1)];
    }

    /// <summary>Eigenvectors of a symmetric 3x3 matrix (cyclic Jacobi), as columns.</summary>
    private static Vec3[] JacobiEigenvectors(double[,] a)
    {
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (var sweep = 0; sweep < 50; sweep++)
        {
            var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-15 * (Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]) + 1e-300))
                break;
            for (var p = 0; p < 2; p++)
            {
                for (var q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300)
                        continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;
                    for (var k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }

        return [new Vec3(v[0, 0], v[1, 0], v[2, 0]), new Vec3(v[0, 1], v[1, 1], v[2, 1]), new Vec3(v[0, 2], v[1, 2], v[2, 2])];
    }
}
