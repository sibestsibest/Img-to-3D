namespace ImageTo3D.Core.Geometry;

/// <summary>How two triangles meet, within the contact tolerance.</summary>
public enum TriangleContact
{
    /// <summary>Apart by more than the tolerance.</summary>
    None,

    /// <summary>Touching (shared face, edge or point, or closer than the tolerance) without crossing.</summary>
    Touching,

    /// <summary>Each triangle passes through the other's interior.</summary>
    Crossing,
}

/// <summary>How two solids relate, from the most to the least serious.</summary>
public enum MeshContact
{
    /// <summary>No triangle is within the tolerance of the other mesh, and neither is inside the other.</summary>
    Clear,

    /// <summary>Surfaces touch (glued faces, a rod end on a plate) but do not cross.</summary>
    Touching,

    /// <summary>One closed mesh lies entirely inside the other without the surfaces crossing.</summary>
    Contained,

    /// <summary>The surfaces cross: the solids interpenetrate.</summary>
    Intersecting,
}

/// <param name="Contact">The most serious relation found.</param>
/// <param name="ContainedPart">For <see cref="MeshContact.Contained"/>: "a" when A is inside B, "b" when B is inside A.</param>
/// <param name="CrossingPairs">Triangle pairs whose interiors cross.</param>
/// <param name="TouchingPairs">Triangle pairs that only touch.</param>
/// <param name="IntersectionLength">Total length of the crossing segments (mesh units): the length of the intersection curve.</param>
/// <param name="OverlapBounds">Box around the crossing segments, or around the contained mesh; null when clear or only touching.</param>
/// <param name="ContainmentChecked">False when a mesh is open, so inside/outside is undefined and containment was not tested.</param>
/// <param name="Complete">False when the triangle-test budget ran out before every candidate pair was tested.</param>
public sealed record MeshContactResult(
    MeshContact Contact,
    string? ContainedPart,
    int CrossingPairs,
    int TouchingPairs,
    double IntersectionLength,
    BoundingBox? OverlapBounds,
    bool ContainmentChecked,
    bool Complete,
    long TriangleTests);

/// <summary>
/// Exact collision checks between triangle meshes: triangle–triangle tests over BVH candidate pairs,
/// with a distance tolerance so that faces glued together count as touching, not colliding; and
/// point-in-solid by ray parity for closed meshes, to find a part entirely inside another.
/// </summary>
public static class MeshIntersection
{
    /// <summary>
    /// Compares two meshes in the same coordinates. <paramref name="tolerance"/> is the distance
    /// under which surfaces count as touching (and above which a penetration counts as crossing).
    /// <paramref name="closedA"/>/<paramref name="closedB"/> say whether each mesh is a closed,
    /// consistently oriented solid; containment is only tested for those.
    /// </summary>
    public static MeshContactResult Compare(TriangleBvh a, TriangleBvh b, double tolerance, bool closedA, bool closedB, long maxTriangleTests = long.MaxValue)
    {
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be a positive finite number.");

        var crossing = 0;
        var touching = 0;
        var length = 0.0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        long tests = 0;
        var complete = a.VisitCandidatePairs(b, tolerance, (ta, tb) =>
        {
            if (++tests > maxTriangleTests)
                return false;
            var (kind, s0, s1) = Classify(Triangle(a.Mesh, ta), Triangle(b.Mesh, tb), tolerance);
            if (kind == TriangleContact.Touching)
            {
                touching++;
            }
            else if (kind == TriangleContact.Crossing)
            {
                crossing++;
                length += (s1 - s0).Length;
                foreach (var s in (ReadOnlySpan<Vec3>)[s0, s1])
                {
                    minX = Math.Min(minX, s.X);
                    minY = Math.Min(minY, s.Y);
                    minZ = Math.Min(minZ, s.Z);
                    maxX = Math.Max(maxX, s.X);
                    maxY = Math.Max(maxY, s.Y);
                    maxZ = Math.Max(maxZ, s.Z);
                }
            }

            return true;
        });
        tests = Math.Min(tests, maxTriangleTests);

        if (crossing > 0)
        {
            var box = new BoundingBox(new Vector3D(minX, minY, minZ), new Vector3D(maxX, maxY, maxZ));
            return new MeshContactResult(MeshContact.Intersecting, null, crossing, touching, length, box, closedA && closedB, complete, tests);
        }

        // Without crossing surfaces, a closed mesh is either wholly inside the other or wholly outside.
        var containmentChecked = closedA && closedB;
        if (containmentChecked && complete)
        {
            if (BoxInside(a.Bounds, b.Bounds, tolerance) && IsInside(a.Mesh, b))
                return new MeshContactResult(MeshContact.Contained, "a", 0, touching, 0, a.Bounds, true, true, tests);
            if (BoxInside(b.Bounds, a.Bounds, tolerance) && IsInside(b.Mesh, a))
                return new MeshContactResult(MeshContact.Contained, "b", 0, touching, 0, b.Bounds, true, true, tests);
        }

        return new MeshContactResult(touching > 0 ? MeshContact.Touching : MeshContact.Clear, null, 0, touching, 0, null, containmentChecked, complete, tests);
    }

    /// <summary>
    /// Whether <paramref name="inner"/> lies inside the closed mesh of <paramref name="solid"/>: a few
    /// sample points on it (triangle centroids) are tested by ray parity, and the majority decides,
    /// so a point that happens to lie on the other surface does not decide alone.
    /// </summary>
    private static bool IsInside(Mesh inner, TriangleBvh solid)
    {
        var samples = SamplePoints(inner, 5);
        var inside = samples.Count(p => ContainsPoint(solid, p.ToVector3D()));
        return inside * 2 > samples.Count;
    }

    private static List<Vec3> SamplePoints(Mesh mesh, int count)
    {
        var points = new List<Vec3>(count);
        var triangles = mesh.TriangleCount;
        if (triangles == 0)
            return points;
        for (var i = 0; i < count; i++)
        {
            var (p0, p1, p2) = Triangle(mesh, (int)((long)i * triangles / count));
            points.Add((p0 + p1 + p2) * (1.0 / 3));
        }

        return points;
    }

    private static readonly Vector3D[] RayDirections =
    [
        // Deliberately not axis-aligned, so rays rarely run along edges of axis-aligned shapes.
        Normalize(new Vector3D(0.5773, 0.5778, 0.5769)),
        Normalize(new Vector3D(-0.3123, 0.8517, 0.4207)),
        Normalize(new Vector3D(0.7071, -0.1251, -0.6959)),
    ];

    /// <summary>
    /// Point-in-solid for a closed, consistently oriented mesh: three rays in fixed, skewed
    /// directions count surface crossings (odd = inside); the majority of the three decides.
    /// </summary>
    public static bool ContainsPoint(TriangleBvh solid, Vector3D point)
    {
        var votes = 0;
        foreach (var direction in RayDirections)
        {
            if (RayCrossings(solid, point, direction) % 2 == 1)
                votes++;
        }

        return votes >= 2;
    }

    private static int RayCrossings(TriangleBvh solid, Vector3D origin, Vector3D direction)
    {
        var o = Vec3.From(origin);
        var d = Vec3.From(direction);
        var hits = 0;
        solid.VisitRayCandidates(origin, direction, t =>
        {
            var (p0, p1, p2) = Triangle(solid.Mesh, t);
            if (RayHitsTriangle(o, d, p0, p1, p2))
                hits++;
        });
        return hits;
    }

    /// <summary>Möller–Trumbore, strictly in front of the origin.</summary>
    private static bool RayHitsTriangle(Vec3 origin, Vec3 direction, Vec3 p0, Vec3 p1, Vec3 p2)
    {
        var e1 = p1 - p0;
        var e2 = p2 - p0;
        var h = direction.Cross(e2);
        var det = e1.Dot(h);
        var scale = e1.Length * e2.Length;
        if (Math.Abs(det) <= 1e-12 * scale)
            return false;
        var inv = 1 / det;
        var s = origin - p0;
        var u = s.Dot(h) * inv;
        if (u is < 0 or > 1)
            return false;
        var q = s.Cross(e1);
        var v = direction.Dot(q) * inv;
        if (v < 0 || u + v > 1)
            return false;
        return e2.Dot(q) * inv > 1e-9 * Math.Sqrt(scale);
    }

    /// <summary>
    /// Classifies two triangles. Vertex distances to the other triangle's plane below
    /// <paramref name="tolerance"/> count as on the plane, so parallel faces closer than the
    /// tolerance touch, and a penetration shallower than it is not a crossing. For crossing
    /// triangles, also returns the crossing segment.
    /// </summary>
    public static TriangleContact Classify(Vector3D[] first, Vector3D[] second, double tolerance) =>
        Classify((Vec3.From(first[0]), Vec3.From(first[1]), Vec3.From(first[2])), (Vec3.From(second[0]), Vec3.From(second[1]), Vec3.From(second[2])), tolerance).Kind;

    private static (TriangleContact Kind, Vec3 S0, Vec3 S1) Classify((Vec3, Vec3, Vec3) t1, (Vec3, Vec3, Vec3) t2, double tolerance)
    {
        var (a0, a1, a2) = t1;
        var (b0, b1, b2) = t2;
        var n1 = (a1 - a0).Cross(a2 - a0);
        var n2 = (b1 - b0).Cross(b2 - b0);
        var l1 = n1.Length;
        var l2 = n2.Length;
        if (l1 <= 0 || l2 <= 0)
            return (TriangleContact.None, default, default); // degenerate triangles are reported by the geometry checks
        n1 *= 1 / l1;
        n2 *= 1 / l2;

        // Distances of the second triangle's vertices to the first one's plane, and vice versa.
        var db = Snap([(b0 - a0).Dot(n1), (b1 - a0).Dot(n1), (b2 - a0).Dot(n1)], tolerance);
        if (SameStrictSide(db))
            return (TriangleContact.None, default, default);
        var da = Snap([(a0 - b0).Dot(n2), (a1 - b0).Dot(n2), (a2 - b0).Dot(n2)], tolerance);
        if (SameStrictSide(da))
            return (TriangleContact.None, default, default);

        if (db[0] == 0 && db[1] == 0 && db[2] == 0)
            return (CoplanarOverlap([a0, a1, a2], [b0, b1, b2], n1, tolerance) ? TriangleContact.Touching : TriangleContact.None, default, default);

        // Both triangles meet the line where the planes intersect; compare their spans on it.
        var line = n1.Cross(n2);
        if (line.Length <= 1e-12)
        {
            // Nearly parallel planes that are not within the tolerance of each other everywhere:
            // the snapped distances decide, so treat a remaining meeting as touching.
            return (TriangleContact.Touching, default, default);
        }

        line = line.Normalized();
        var spanA = PlaneSpan([a0, a1, a2], da, line);
        var spanB = PlaneSpan([b0, b1, b2], db, line);
        var start = Math.Max(spanA.Min, spanB.Min);
        var end = Math.Min(spanA.Max, spanB.Max);
        if (end < start - tolerance)
            return (TriangleContact.None, default, default);

        // Crossing needs each triangle to straddle the other's plane and a shared span of some
        // length; spans that only meet at a point are an edge or corner contact.
        var straddleA = Straddles(da);
        var straddleB = Straddles(db);
        if (!straddleA || !straddleB || end - start <= tolerance * 1e-3)
            return (TriangleContact.Touching, default, default);

        var s0 = PointAt(spanA, spanB, start);
        var s1 = PointAt(spanA, spanB, end);
        return (TriangleContact.Crossing, s0, s1);
    }

    private readonly record struct Span(double Min, double Max, Vec3 MinPoint, Vec3 MaxPoint, Vec3 Line);

    /// <summary>Where a triangle meets the other plane, projected on the intersection line.</summary>
    private static Span PlaneSpan(Vec3[] p, double[] d, Vec3 line)
    {
        double min = double.MaxValue, max = double.MinValue;
        Vec3 minPoint = default, maxPoint = default;
        void Add(Vec3 point)
        {
            var t = point.Dot(line);
            if (t < min)
            {
                min = t;
                minPoint = point;
            }

            if (t > max)
            {
                max = t;
                maxPoint = point;
            }
        }

        for (var i = 0; i < 3; i++)
        {
            var j = (i + 1) % 3;
            if (d[i] == 0)
                Add(p[i]);
            if (d[i] * d[j] < 0)
                Add(p[i] + (p[j] - p[i]) * (d[i] / (d[i] - d[j])));
        }

        return new Span(min, max, minPoint, maxPoint, line);
    }

    /// <summary>A point of the crossing segment at parameter <paramref name="t"/> on the line (from the span that reaches it).</summary>
    private static Vec3 PointAt(Span a, Span b, double t)
    {
        foreach (var span in (ReadOnlySpan<Span>)[a, b])
        {
            if (span.Max - span.Min > 0 && t >= span.Min && t <= span.Max)
                return span.MinPoint + (span.MaxPoint - span.MinPoint) * ((t - span.Min) / (span.Max - span.Min));
        }

        return a.MinPoint;
    }

    private static double[] Snap(double[] d, double tolerance)
    {
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(d[i]) < tolerance)
                d[i] = 0;
        }

        return d;
    }

    private static bool SameStrictSide(double[] d) => (d[0] > 0 && d[1] > 0 && d[2] > 0) || (d[0] < 0 && d[1] < 0 && d[2] < 0);

    private static bool Straddles(double[] d) => d.Any(x => x > 0) && d.Any(x => x < 0);

    /// <summary>Coplanar triangles: overlap (or touch within the tolerance) in the plane.</summary>
    private static bool CoplanarOverlap(Vec3[] a, Vec3[] b, Vec3 normal, double tolerance)
    {
        // Project on the plane that drops the normal's dominant axis.
        var ax = Math.Abs(normal.X);
        var ay = Math.Abs(normal.Y);
        var az = Math.Abs(normal.Z);
        var drop = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        (double U, double V) P(Vec3 v) => drop switch { 0 => (v.Y, v.Z), 1 => (v.Z, v.X), _ => (v.X, v.Y) };
        var pa = a.Select(P).ToArray();
        var pb = b.Select(P).ToArray();

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                if (SegmentsMeet(pa[i], pa[(i + 1) % 3], pb[j], pb[(j + 1) % 3], tolerance))
                    return true;
            }
        }

        return InTriangle(pa[0], pb, tolerance) || InTriangle(pb[0], pa, tolerance);
    }

    private static bool SegmentsMeet((double U, double V) p, (double U, double V) p2, (double U, double V) q, (double U, double V) q2, double tolerance)
    {
        static double Cross((double U, double V) o, (double U, double V) a, (double U, double V) b) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);
        var d1 = Cross(q, q2, p);
        var d2 = Cross(q, q2, p2);
        var d3 = Cross(p, p2, q);
        var d4 = Cross(p, p2, q2);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return PointSegmentDistance(p, q, q2) <= tolerance || PointSegmentDistance(p2, q, q2) <= tolerance ||
               PointSegmentDistance(q, p, p2) <= tolerance || PointSegmentDistance(q2, p, p2) <= tolerance;
    }

    private static double PointSegmentDistance((double U, double V) p, (double U, double V) a, (double U, double V) b)
    {
        var du = b.U - a.U;
        var dv = b.V - a.V;
        var lengthSquared = du * du + dv * dv;
        var t = lengthSquared > 0 ? Math.Clamp(((p.U - a.U) * du + (p.V - a.V) * dv) / lengthSquared, 0, 1) : 0;
        var x = a.U + t * du - p.U;
        var y = a.V + t * dv - p.V;
        return Math.Sqrt(x * x + y * y);
    }

    private static bool InTriangle((double U, double V) p, (double U, double V)[] t, double tolerance)
    {
        static double Cross((double U, double V) o, (double U, double V) a, (double U, double V) b) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);
        var c0 = Cross(t[0], t[1], p);
        var c1 = Cross(t[1], t[2], p);
        var c2 = Cross(t[2], t[0], p);
        var inside = (c0 >= 0 && c1 >= 0 && c2 >= 0) || (c0 <= 0 && c1 <= 0 && c2 <= 0);
        return inside || PointSegmentDistance(p, t[0], t[1]) <= tolerance || PointSegmentDistance(p, t[1], t[2]) <= tolerance || PointSegmentDistance(p, t[2], t[0]) <= tolerance;
    }

    private static bool BoxInside(BoundingBox inner, BoundingBox outer, double tolerance) =>
        inner.Min.X >= outer.Min.X - tolerance && inner.Min.Y >= outer.Min.Y - tolerance && inner.Min.Z >= outer.Min.Z - tolerance &&
        inner.Max.X <= outer.Max.X + tolerance && inner.Max.Y <= outer.Max.Y + tolerance && inner.Max.Z <= outer.Max.Z + tolerance;

    private static (Vec3, Vec3, Vec3) Triangle(Mesh mesh, int t)
    {
        var idx = mesh.Indices;
        return (Vec3.At(mesh.Positions, idx[t * 3]), Vec3.At(mesh.Positions, idx[t * 3 + 1]), Vec3.At(mesh.Positions, idx[t * 3 + 2]));
    }

    private static Vector3D Normalize(Vector3D v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return new Vector3D(v.X / length, v.Y / length, v.Z / length);
    }
}
