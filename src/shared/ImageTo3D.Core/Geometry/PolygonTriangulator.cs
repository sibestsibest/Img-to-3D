namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Ear-clipping triangulation of a simple 2D polygon with holes. Holes are first joined to the
/// outer boundary by bridge edges (Eberly, "Triangulation by Ear Clipping"), so the output reuses
/// the input vertices only: no vertex is added, which keeps a cap watertight against the walls
/// it closes. Collinear vertices are kept for the same reason.
/// </summary>
public static class PolygonTriangulator
{
    /// <param name="x">X coordinate per vertex index.</param>
    /// <param name="y">Y coordinate per vertex index.</param>
    /// <param name="outer">Outer boundary, counter-clockwise, as vertex indices.</param>
    /// <param name="holes">Hole boundaries, clockwise, strictly inside <paramref name="outer"/>.</param>
    /// <param name="triangles">Receives counter-clockwise triangles as vertex-index triples.</param>
    /// <returns>False with a reason when the polygon cannot be triangulated (self-intersecting or invalid input).</returns>
    public static bool TryTriangulate(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        IReadOnlyList<int> outer,
        IReadOnlyList<IReadOnlyList<int>> holes,
        List<int> triangles,
        out string? error)
    {
        error = null;
        if (outer.Count < 3)
        {
            error = "Outer boundary has fewer than 3 vertices.";
            return false;
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var v in outer)
        {
            minX = Math.Min(minX, x[v]);
            maxX = Math.Max(maxX, x[v]);
            minY = Math.Min(minY, y[v]);
            maxY = Math.Max(maxY, y[v]);
        }

        var extent = Math.Max(maxX - minX, maxY - minY);
        if (!(extent > 0) || !double.IsFinite(extent))
        {
            error = "Polygon has no area.";
            return false;
        }

        var eps = extent * extent * 1e-12;
        if (SignedArea(x, y, outer) <= 0)
        {
            error = "Outer boundary is not counter-clockwise.";
            return false;
        }

        var polygon = new List<int>(outer);
        foreach (var hole in holes.Where(h => h.Count >= 3).OrderByDescending(h => h.Max(v => x[v])))
        {
            if (SignedArea(x, y, hole) >= 0)
            {
                error = "Hole boundary is not clockwise.";
                return false;
            }

            if (!Bridge(x, y, polygon, hole, eps))
            {
                error = "A hole could not be connected to the outer boundary.";
                return false;
            }
        }

        return Clip(x, y, polygon, triangles, eps, out error);
    }

    public static double SignedArea(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<int> loop)
    {
        double area = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            int a = loop[i], b = loop[(i + 1) % loop.Count];
            area += x[a] * y[b] - x[b] * y[a];
        }

        return area / 2;
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool Contains(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<int> loop, double px, double py)
    {
        var inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            double xi = x[loop[i]], yi = y[loop[i]], xj = x[loop[j]], yj = y[loop[j]];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                inside = !inside;
        }

        return inside;
    }

    private static double Cross(IReadOnlyList<double> x, IReadOnlyList<double> y, int a, int b, int c) =>
        (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);

    /// <summary>Splices <paramref name="hole"/> into <paramref name="polygon"/> through a mutually visible vertex pair.</summary>
    private static bool Bridge(IReadOnlyList<double> x, IReadOnlyList<double> y, List<int> polygon, IReadOnlyList<int> hole, double eps)
    {
        var mi = 0;
        for (var i = 1; i < hole.Count; i++)
        {
            if (x[hole[i]] > x[hole[mi]] || (x[hole[i]] == x[hole[mi]] && y[hole[i]] < y[hole[mi]]))
                mi = i;
        }

        var m = hole[mi];
        double mx = x[m], my = y[m];

        // Nearest boundary edge hit by the ray from M towards +x.
        var bestT = double.MaxValue;
        var bestEdge = -1;
        for (var i = 0; i < polygon.Count; i++)
        {
            int a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            if ((y[a] > my) == (y[b] > my) && y[a] != my && y[b] != my)
                continue;
            if (y[a] == y[b])
            {
                if (y[a] != my)
                    continue;
                var t0 = Math.Min(x[a], x[b]) - mx;
                if (t0 >= 0 && t0 < bestT)
                {
                    bestT = t0;
                    bestEdge = i;
                }

                continue;
            }

            var ix = x[a] + (my - y[a]) * (x[b] - x[a]) / (y[b] - y[a]);
            var t = ix - mx;
            if (t >= 0 && t < bestT)
            {
                bestT = t;
                bestEdge = i;
            }
        }

        if (bestEdge < 0)
            return false;

        var ea = polygon[bestEdge];
        var eb = polygon[(bestEdge + 1) % polygon.Count];
        var candidate = x[ea] > x[eb] ? ea : eb;
        var hitX = mx + bestT;

        // If a reflex vertex lies inside triangle (M, I, P), the closest one by angle is visible instead.
        if (!(Math.Abs(x[candidate] - hitX) < 1e-15 && Math.Abs(y[candidate] - my) < 1e-15))
        {
            double px = x[candidate], py = y[candidate];
            var bestScore = double.MinValue;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var r = polygon[i];
                if (r == candidate)
                    continue;
                var prev = polygon[(i - 1 + polygon.Count) % polygon.Count];
                var next = polygon[(i + 1) % polygon.Count];
                if (Cross(x, y, prev, r, next) > eps)
                    continue; // convex: cannot block visibility
                if (!PointInTriangle(mx, my, hitX, my, px, py, x[r], y[r], eps))
                    continue;
                double dx = x[r] - mx, dy = y[r] - my;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance == 0)
                    continue;
                var score = dx / distance; // cosine of the angle to +x
                if (score > bestScore + 1e-12 || (Math.Abs(score - bestScore) <= 1e-12 && distance < bestDistance))
                {
                    bestScore = score;
                    bestDistance = distance;
                    candidate = r;
                }
            }
        }

        // The candidate may appear several times after earlier bridges; use the occurrence whose
        // interior wedge contains M.
        var position = -1;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (polygon[i] != candidate)
                continue;
            var prev = polygon[(i - 1 + polygon.Count) % polygon.Count];
            var next = polygon[(i + 1) % polygon.Count];
            if (position < 0 || InWedge(x, y, prev, candidate, next, mx, my))
                position = i;
        }

        var splice = new List<int>(hole.Count + 2) { m };
        for (var k = 1; k < hole.Count; k++)
            splice.Add(hole[(mi + k) % hole.Count]);
        splice.Add(m);
        splice.Add(candidate);
        polygon.InsertRange(position + 1, splice);
        return true;
    }

    private static bool InWedge(IReadOnlyList<double> x, IReadOnlyList<double> y, int prev, int apex, int next, double px, double py)
    {
        double ax = x[apex], ay = y[apex];
        double c1 = (x[next] - ax) * (py - ay) - (y[next] - ay) * (px - ax); // left of apex->next
        double c2 = (x[prev] - ax) * (py - ay) - (y[prev] - ay) * (px - ax); // right of apex->prev
        var convex = Cross(x, y, prev, apex, next) >= 0;
        return convex ? c1 >= 0 && c2 <= 0 : c1 >= 0 || c2 <= 0;
    }

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py, double eps)
    {
        var d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        var d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
        var d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
        var hasNeg = d1 < -eps || d2 < -eps || d3 < -eps;
        var hasPos = d1 > eps || d2 > eps || d3 > eps;
        return !(hasNeg && hasPos);
    }

    private static bool Clip(IReadOnlyList<double> x, IReadOnlyList<double> y, List<int> polygon, List<int> triangles, double eps, out string? error)
    {
        error = null;
        var n = polygon.Count;
        var prev = new int[n];
        var next = new int[n];
        for (var i = 0; i < n; i++)
        {
            prev[i] = (i - 1 + n) % n;
            next[i] = (i + 1) % n;
        }

        var remaining = n;
        var current = 0;
        var sinceLastEar = 0;
        var produced = new List<int>(3 * (n - 2));
        while (remaining > 3)
        {
            if (IsEar(current))
            {
                produced.AddRange([polygon[prev[current]], polygon[current], polygon[next[current]]]);
                next[prev[current]] = next[current];
                prev[next[current]] = prev[current];
                remaining--;
                current = prev[current];
                sinceLastEar = 0;
                continue;
            }

            current = next[current];
            if (++sinceLastEar > remaining)
            {
                // No ear left: acceptable only if what remains has no area (collinear leftovers).
                double area = 0;
                var s = current;
                do
                {
                    area += x[polygon[s]] * y[polygon[next[s]]] - x[polygon[next[s]]] * y[polygon[s]];
                    s = next[s];
                }
                while (s != current);

                if (Math.Abs(area) / 2 <= eps * remaining)
                {
                    triangles.AddRange(produced);
                    return true;
                }

                error = "Contour self-intersects or overlaps; no valid triangulation exists.";
                return false;
            }
        }

        var last = current;
        if (Cross(x, y, polygon[prev[last]], polygon[last], polygon[next[last]]) > eps)
            produced.AddRange([polygon[prev[last]], polygon[last], polygon[next[last]]]);
        triangles.AddRange(produced);
        return true;

        bool IsEar(int i)
        {
            int a = polygon[prev[i]], b = polygon[i], c = polygon[next[i]];
            if (Cross(x, y, a, b, c) <= eps)
                return false;
            for (var j = next[next[i]]; j != prev[i]; j = next[j])
            {
                var v = polygon[j];
                if (v == a || v == b || v == c)
                    continue;
                if (PointInTriangle(x[a], y[a], x[b], y[b], x[c], y[c], x[v], y[v], eps))
                    return false;
            }

            return true;
        }
    }
}
