namespace ImageTo3D.Core.Geometry;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A geometry or manufacturing finding with a stable machine-readable code.</summary>
public sealed record GeometryIssue(string Code, IssueSeverity Severity, string Message);

/// <param name="Mesh">The half, compacted, with caps when <paramref name="Capped"/> is true.</param>
/// <param name="CapArea">Area of the cap triangles added on the cut (0 when not capped).</param>
public sealed record PlaneSplitSide(Mesh Mesh, MeshTopologyReport Topology, bool Capped, int CapLoopCount, double CapArea);

/// <param name="Below">Side where (p - point) · normal &lt; 0; null when nothing lies there.</param>
/// <param name="Above">Side where (p - point) · normal &gt; 0; null when nothing lies there.</param>
public sealed record PlaneSplitResult(
    PlaneSplitSide? Below,
    PlaneSplitSide? Above,
    int ClosedContourCount,
    int OpenContourCount,
    double ContourLength,
    IReadOnlyList<GeometryIssue> Issues)
{
    /// <summary>The plane crossed the mesh and both halves are closed solids.</summary>
    /// <summary>
    /// Closed cut contours as 3D points on the plane, oriented counter-clockwise when viewed from
    /// the normal's side for outer boundaries and clockwise for holes (the cross-section of the part).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Vector3D>> Contours { get; init; } = [];

    public bool IsCompleteSolidSplit =>
        Below is { Capped: true, Topology.IsClosed: true } && Above is { Capped: true, Topology.IsClosed: true };
}

/// <summary>
/// Splits a triangle mesh with a plane into two meshes and closes each cut with a planar cap.
/// Triangles crossing the plane are split exactly at the intersection; vertices within a small
/// tolerance of the plane are treated as lying on it, so a cut through existing vertices does not
/// create slivers. Caps are built from the cut contours with nesting resolved by orientation and
/// containment (outer boundaries with holes, islands inside holes), then triangulated without adding
/// vertices so the result is watertight. Caps are only added when every contour is closed and the
/// nesting is consistent; otherwise the halves are returned open, with issues explaining why.
/// </summary>
public static class PlaneSplitter
{
    private const double OnPlaneRelativeTolerance = 1e-7;

    public static PlaneSplitResult Split(Mesh mesh, Vector3D point, Vector3D normal)
    {
        var n = Vec3.From(normal);
        var origin = Vec3.From(point);
        if (!n.IsFinite || !origin.IsFinite || !(n.Length > 0))
            throw new ArgumentException("The cutting plane needs a finite point and a non-zero normal.");
        n = n.Normalized();

        var issues = new List<GeometryIssue>();
        var welded = MeshTopology.Weld(mesh, MeshTopology.DefaultWeldTolerance(mesh));
        if (welded.TriangleCount == 0)
            throw new ArgumentException("The mesh has no triangles.", nameof(mesh));

        var size = MeshOperations.ComputeBounds(welded).Size;
        var diagonal = Math.Sqrt(size.X * size.X + size.Y * size.Y + size.Z * size.Z);
        var onPlane = diagonal * OnPlaneRelativeTolerance;

        var vertexCount = welded.VertexCount;
        var positions = new List<double>(vertexCount * 3 + 64);
        for (var i = 0; i < welded.Positions.Length; i++)
            positions.Add(welded.Positions[i]);
        var colors = welded.Colors is null ? null : new List<byte>(welded.Colors);

        var side = new int[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var d = (Vec3.At(welded.Positions, v) - origin).Dot(n);
            side[v] = d < -onPlane ? -1 : d > onPlane ? 1 : 0;
        }

        // Cut points are keyed by undirected edge so both triangles on an edge share one vertex.
        var cutPoints = new Dictionary<long, int>();
        var isOnCut = new HashSet<int>();
        for (var v = 0; v < vertexCount; v++)
        {
            if (side[v] == 0)
                isOnCut.Add(v);
        }

        int CutPoint(int a, int b)
        {
            var key = (long)Math.Min(a, b) * vertexCount + Math.Max(a, b);
            if (cutPoints.TryGetValue(key, out var existing))
                return existing;
            var pa = Vec3.At(welded.Positions, a);
            var pb = Vec3.At(welded.Positions, b);
            var da = (pa - origin).Dot(n);
            var db = (pb - origin).Dot(n);
            var t = da / (da - db);
            var p = pa + (pb - pa) * t;
            var index = positions.Count / 3;
            positions.AddRange([p.X, p.Y, p.Z]);
            if (colors is not null)
            {
                for (var k = 0; k < 3; k++)
                    colors.Add((byte)Math.Round(welded.Colors![a * 3 + k] + (welded.Colors[b * 3 + k] - welded.Colors[a * 3 + k]) * t));
            }

            cutPoints[key] = index;
            isOnCut.Add(index);
            return index;
        }

        var below = new List<int>();
        var above = new List<int>();
        var idx = welded.Indices;
        for (var t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            int sa = side[a], sb = side[b], sc = side[c];
            var hasNeg = sa < 0 || sb < 0 || sc < 0;
            var hasPos = sa > 0 || sb > 0 || sc > 0;

            if (!hasNeg && !hasPos)
            {
                // Face lying in the plane belongs to the solid behind it (opposite its normal).
                var faceNormal = (Vec3.At(welded.Positions, b) - Vec3.At(welded.Positions, a)).Cross(Vec3.At(welded.Positions, c) - Vec3.At(welded.Positions, a));
                (faceNormal.Dot(n) > 0 ? below : above).AddRange([a, b, c]);
                continue;
            }

            if (!hasPos)
            {
                below.AddRange([a, b, c]);
                continue;
            }

            if (!hasNeg)
            {
                above.AddRange([a, b, c]);
                continue;
            }

            // Rotate so the first vertex is the odd one out, preserving winding.
            Span<int> v = [a, b, c];
            Span<int> s = [sa, sb, sc];
            var zeroAt = s.IndexOf(0);
            if (zeroAt >= 0)
            {
                // One vertex on the plane, the other two on opposite sides: split the far edge only.
                int o = v[zeroAt], p = v[(zeroAt + 1) % 3], q = v[(zeroAt + 2) % 3];
                var x = CutPoint(p, q);
                (side[p] < 0 ? below : above).AddRange([o, p, x]);
                (side[q] < 0 ? below : above).AddRange([o, x, q]);
                continue;
            }

            var lone = s[0] == s[1] ? 2 : s[0] == s[2] ? 1 : 0;
            int l = v[lone], m1 = v[(lone + 1) % 3], m2 = v[(lone + 2) % 3];
            var x1 = CutPoint(l, m1);
            var x2 = CutPoint(l, m2);
            var loneList = side[l] < 0 ? below : above;
            var pairList = side[l] < 0 ? above : below;
            loneList.AddRange([l, x1, x2]);
            pairList.AddRange([x1, m1, m2]);
            pairList.AddRange([x1, m2, x2]);
        }

        if (below.Count == 0 || above.Count == 0)
        {
            issues.Add(new GeometryIssue("plane_misses_part", IssueSeverity.Error, "The cutting plane does not pass through the part; nothing would be split."));
            return new PlaneSplitResult(null, null, 0, 0, 0, issues);
        }

        var belowSide = BuildSide(below, n, positions, colors, isOnCut, issues, "below", out var closedLoops, out var openChains, out var contourLength, out var belowLoops);
        var aboveSide = BuildSide(above, -n, positions, colors, isOnCut, issues, "above", out _, out _, out _, out _);
        // The below side's cap loops face +normal: outer boundaries counter-clockwise, holes clockwise.
        var contours = belowLoops
            .Select(loop => (IReadOnlyList<Vector3D>)loop.Select(v => new Vector3D(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2])).ToList())
            .ToList();

        if (openChains > 0)
        {
            issues.Add(new GeometryIssue(
                "cut_contour_open",
                IssueSeverity.Error,
                $"{openChains} cut contour(s) are not closed because the part's surface has holes or gaps where the plane crosses it. The halves were not capped and are not closed solids."));
        }

        foreach (var (half, name) in new[] { (belowSide, "below"), (aboveSide, "above") })
        {
            if (half.Capped && !half.Topology.IsClosed)
            {
                issues.Add(new GeometryIssue(
                    "half_not_closed",
                    IssueSeverity.Warning,
                    $"The {name} half is not a closed solid ({half.Topology.BoundaryEdgeCount} open edges, {half.Topology.NonManifoldEdgeCount} non-manifold edges); the source part was probably not closed."));
            }
        }

        return new PlaneSplitResult(belowSide, aboveSide, closedLoops, openChains, contourLength, issues) { Contours = contours };
    }

    private static PlaneSplitSide BuildSide(
        List<int> triangles,
        Vec3 capNormal,
        List<double> positions,
        List<byte>? colors,
        HashSet<int> isOnCut,
        List<GeometryIssue> issues,
        string name,
        out int closedLoops,
        out int openChains,
        out double contourLength,
        out List<List<int>> closedLoopVertices)
    {
        closedLoops = 0;
        openChains = 0;
        contourLength = 0;

        // Boundary edges of this half that lie on the cut; the cap uses them reversed.
        var undirected = new Dictionary<(int, int), int>();
        for (var t = 0; t < triangles.Count; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                undirected[key] = undirected.GetValueOrDefault(key) + 1;
            }
        }

        var capNext = new Dictionary<int, int>();
        var branching = false;
        for (var t = 0; t < triangles.Count; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                if (!isOnCut.Contains(a) || !isOnCut.Contains(b) || undirected[a < b ? (a, b) : (b, a)] != 1)
                    continue;
                if (!capNext.TryAdd(b, a))
                    branching = true;
            }
        }

        var loops = new List<List<int>>();
        closedLoopVertices = loops;
        var visited = new HashSet<int>();
        var hasIncoming = new HashSet<int>(capNext.Values);
        // Open chains start where nothing leads in; closed loops are what remains.
        foreach (var start in capNext.Keys.Where(k => !hasIncoming.Contains(k)).Concat(capNext.Keys).ToList())
        {
            if (visited.Contains(start))
                continue;
            var loop = new List<int>();
            var current = start;
            var closed = false;
            while (true)
            {
                visited.Add(current);
                loop.Add(current);
                if (!capNext.TryGetValue(current, out var following))
                    break;
                if (following == start)
                {
                    closed = true;
                    break;
                }

                if (visited.Contains(following))
                    break;
                current = following;
            }

            for (var i = 0; i + 1 < loop.Count + (closed ? 1 : 0); i++)
                contourLength += Distance(positions, loop[i], loop[(i + 1) % loop.Count]);

            if (closed && loop.Count >= 3)
            {
                loops.Add(loop);
                closedLoops++;
            }
            else
            {
                openChains++;
            }
        }

        var capped = false;
        var capArea = 0.0;
        var all = new List<int>(triangles);
        if (branching)
        {
            issues.Add(new GeometryIssue("cut_contour_branching", IssueSeverity.Error, $"The cut contour on the {name} side touches itself (the plane passes through a pinch point); the cut was not capped."));
        }
        else if (openChains == 0 && loops.Count > 0)
        {
            var cap = new List<int>();
            if (TryCap(loops, capNormal, positions, cap, out var reason))
            {
                all.AddRange(cap);
                capped = true;
                for (var t = 0; t < cap.Count; t += 3)
                    capArea += TriangleArea(positions, cap[t], cap[t + 1], cap[t + 2]);
            }
            else
            {
                issues.Add(new GeometryIssue("cap_failed", IssueSeverity.Error, $"The cut on the {name} side could not be capped: {reason}"));
            }
        }

        var mesh = ToMesh(all, positions, colors);
        return new PlaneSplitSide(mesh, MeshTopology.Analyze(mesh), capped, capped ? loops.Count : 0, capArea);
    }

    /// <summary>Groups loops into outer boundaries and holes by orientation and containment, then triangulates each.</summary>
    private static bool TryCap(List<List<int>> loops, Vec3 capNormal, List<double> positions, List<int> cap, out string? reason)
    {
        reason = null;

        // 2D basis with u x v = capNormal: outer boundaries are counter-clockwise, holes clockwise.
        var helper = Math.Abs(capNormal.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        var u = capNormal.Cross(helper).Normalized();
        var v = capNormal.Cross(u);

        var x = new Dictionary<int, double>();
        var y = new Dictionary<int, double>();
        foreach (var loop in loops)
        {
            foreach (var vertex in loop)
            {
                var p = new Vec3(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);
                x[vertex] = p.Dot(u);
                y[vertex] = p.Dot(v);
            }
        }

        // Re-index into dense arrays for the triangulator.
        var dense = new Dictionary<int, int>();
        var xs = new List<double>();
        var ys = new List<double>();
        var localLoops = loops.Select(loop => loop.Select(vertex =>
        {
            if (!dense.TryGetValue(vertex, out var d))
            {
                d = xs.Count;
                dense[vertex] = d;
                xs.Add(x[vertex]);
                ys.Add(y[vertex]);
            }

            return d;
        }).ToList()).ToList();
        var global = dense.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();

        var areas = localLoops.Select(l => PolygonTriangulator.SignedArea(xs, ys, l)).ToList();
        var outers = Enumerable.Range(0, localLoops.Count).Where(i => areas[i] > 0).ToList();
        var holes = Enumerable.Range(0, localLoops.Count).Where(i => areas[i] < 0).ToList();
        if (outers.Count == 0)
        {
            reason = "every contour runs the wrong way; the part's faces may point inwards.";
            return false;
        }

        // Nesting depth must alternate: outer boundaries at even depth, holes at odd depth.
        int Depth(int loop)
        {
            var probe = localLoops[loop][0];
            return Enumerable.Range(0, localLoops.Count).Count(other =>
                other != loop && Math.Abs(areas[other]) > Math.Abs(areas[loop]) && PolygonTriangulator.Contains(xs, ys, localLoops[other], xs[probe], ys[probe]));
        }

        foreach (var i in outers)
        {
            if (Depth(i) % 2 != 0)
            {
                reason = "contours overlap (the part may contain intersecting or nested shells).";
                return false;
            }
        }

        var holesOf = outers.ToDictionary(i => i, _ => new List<IReadOnlyList<int>>());
        foreach (var h in holes)
        {
            if (Depth(h) % 2 != 1)
            {
                reason = "a hole contour is not inside any outer contour.";
                return false;
            }

            var probe = localLoops[h][0];
            var owner = outers
                .Where(o => areas[o] > -areas[h] && PolygonTriangulator.Contains(xs, ys, localLoops[o], xs[probe], ys[probe]))
                .OrderBy(o => areas[o])
                .FirstOrDefault(-1);
            if (owner < 0)
            {
                reason = "a hole contour is not inside any outer contour.";
                return false;
            }

            holesOf[owner].Add(localLoops[h]);
        }

        var local = new List<int>();
        foreach (var o in outers)
        {
            if (!PolygonTriangulator.TryTriangulate(xs, ys, localLoops[o], holesOf[o], local, out var error))
            {
                reason = error;
                return false;
            }
        }

        cap.AddRange(local.Select(i => global[i]));
        return true;
    }

    private static Mesh ToMesh(List<int> triangles, List<double> positions, List<byte>? colors)
    {
        var remap = new Dictionary<int, int>();
        var outPositions = new List<float>();
        var outColors = colors is null ? null : new List<byte>();
        var outIndices = new int[triangles.Count];
        for (var i = 0; i < triangles.Count; i++)
        {
            var v = triangles[i];
            if (!remap.TryGetValue(v, out var mapped))
            {
                mapped = outPositions.Count / 3;
                remap[v] = mapped;
                outPositions.AddRange([(float)positions[v * 3], (float)positions[v * 3 + 1], (float)positions[v * 3 + 2]]);
                outColors?.AddRange([colors![v * 3], colors[v * 3 + 1], colors[v * 3 + 2]]);
            }

            outIndices[i] = mapped;
        }

        return new Mesh(outPositions.ToArray(), outIndices, outColors?.ToArray());
    }

    private static double Distance(List<double> p, int a, int b)
    {
        double dx = p[a * 3] - p[b * 3], dy = p[a * 3 + 1] - p[b * 3 + 1], dz = p[a * 3 + 2] - p[b * 3 + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double TriangleArea(List<double> p, int a, int b, int c)
    {
        var pa = new Vec3(p[a * 3], p[a * 3 + 1], p[a * 3 + 2]);
        var pb = new Vec3(p[b * 3], p[b * 3 + 1], p[b * 3 + 2]);
        var pc = new Vec3(p[c * 3], p[c * 3 + 1], p[c * 3 + 2]);
        return (pb - pa).Cross(pc - pa).Length / 2;
    }
}
