using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.PartDesign;

public readonly record struct Point2(double X, double Y);

/// <param name="Points">Closed polygon (last point not repeated). Outer boundaries counter-clockwise, holes clockwise.</param>
public sealed record OutlineLoop(IReadOnlyList<Point2> Points, double Area, double Perimeter)
{
    public (Point2 Min, Point2 Max) Bounds =>
        (new Point2(Points.Min(p => p.X), Points.Min(p => p.Y)), new Point2(Points.Max(p => p.X), Points.Max(p => p.Y)));
}

/// <param name="DiameterMm">Set when the hole is round within <see cref="PlanarOutlineExtractor.RoundTolerance"/>.</param>
public sealed record OutlineHole(OutlineLoop Loop, Point2 Centre, double? DiameterMm, double WidthMm, double HeightMm);

/// <summary>
/// Cutting outline of a flat part in its own 2D frame, in millimetres. The origin is the lower-left
/// corner of the outline's bounding box; X runs along the part's longest side.
/// </summary>
public sealed record PlanarOutline(
    IReadOnlyList<OutlineLoop> Outers,
    IReadOnlyList<OutlineHole> Holes,
    double WidthMm,
    double HeightMm,
    double MeasuredThicknessMm,
    double PlanarFraction,
    IReadOnlyList<GeometryIssue> Issues)
{
    public bool IsUsable => Outers.Count > 0 && Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>
/// Extracts the cutting outline of a sheet part from the cross-section at mid-thickness (the same
/// exact contour code as plane splitting), simplified within a tolerance. Sections at a quarter and
/// three quarters of the thickness detect edges that are not perpendicular to the faces. Refuses
/// parts that are not flat: an orthographic outline of a curved part is not a cutting pattern.
/// </summary>
public static class PlanarOutlineExtractor
{
    public const double DefaultToleranceMm = 0.05;
    public const double MinPlanarFraction = 0.6;
    public const double RoundTolerance = 0.03;

    public static PlanarOutline Extract(Mesh mm, double toleranceMm = DefaultToleranceMm)
    {
        var issues = new List<GeometryIssue>();
        var box = OrientedBounds.Compute(mm);
        var thickness = box.Extents.Z;
        var n = box.Axes[2];
        var planar = PartAnalyzer.PlanarAreaFraction(mm, n);

        if (planar < MinPlanarFraction)
        {
            issues.Add(new GeometryIssue(
                "not_planar",
                IssueSeverity.Error,
                $"Only {planar:P0} of the surface is flat and parallel to the thin direction. A flat cutting outline would be wrong for this part: it needs a developed pattern, segmented construction, forming over a mould, or custom 3D modelling."));
            return new PlanarOutline([], [], 0, 0, thickness, planar, issues);
        }

        var u = Vec3.From(box.Axes[0]);
        var nv = Vec3.From(n);
        var v = nv.Cross(u); // u x v = n, so loops counter-clockwise about n stay counter-clockwise in 2D
        var centre = Vec3.From(box.Center);

        var mid = Section(mm, centre, nv, u, v, out var openMid);
        if (openMid > 0 || mid.Count == 0)
        {
            issues.Add(new GeometryIssue("outline_open", IssueSeverity.Error, "The part's surface is not closed where the outline is taken, so no reliable cutting outline exists."));
            return new PlanarOutline([], [], 0, 0, thickness, planar, issues);
        }

        var simplified = mid.Select(loop => Simplify(loop, toleranceMm)).Where(l => l.Count >= 3).ToList();
        var outers = simplified.Where(l => SignedArea(l) > 0).ToList();
        var holes = simplified.Where(l => SignedArea(l) < 0).ToList();
        if (outers.Count == 0)
        {
            issues.Add(new GeometryIssue("outline_inverted", IssueSeverity.Error, "The outline runs the wrong way; the part's faces may point inwards."));
            return new PlanarOutline([], [], 0, 0, thickness, planar, issues);
        }

        if (outers.Count > 1)
            issues.Add(new GeometryIssue("multiple_outlines", IssueSeverity.Warning, $"The part has {outers.Count} separate outlines; each is drawn and must be cut separately."));

        // Edges that are not square to the faces (bevels, chamfers, tapers) change the section area through the thickness.
        var midArea = simplified.Sum(SignedArea);
        foreach (var fraction in new[] { -0.25, 0.25 })
        {
            var other = Section(mm, centre + nv * (thickness * fraction), nv, u, v, out var open);
            var area = other.Sum(SignedArea);
            if (open > 0 || midArea <= 0 || Math.Abs(area - midArea) / midArea > 0.02)
            {
                issues.Add(new GeometryIssue(
                    "edges_not_perpendicular",
                    IssueSeverity.Warning,
                    "The outline changes through the thickness (bevelled, tapered or rounded edges). The drawing shows the mid-thickness outline; finish edges by hand."));
                break;
            }
        }

        // Origin at the lower-left corner of everything drawn.
        var minX = simplified.SelectMany(l => l).Min(p => p.X);
        var minY = simplified.SelectMany(l => l).Min(p => p.Y);
        List<Point2> Shift(List<Point2> loop) => loop.Select(p => new Point2(p.X - minX, p.Y - minY)).ToList();

        var outerLoops = outers.Select(l => ToLoop(Shift(l))).ToList();
        var holeList = holes.Select(l => Hole(ToLoop(Shift(l)))).ToList();
        var width = outerLoops.Max(l => l.Bounds.Max.X);
        var height = outerLoops.Max(l => l.Bounds.Max.Y);
        return new PlanarOutline(outerLoops, holeList, width, height, thickness, planar, issues);
    }

    private static List<List<Point2>> Section(Mesh mesh, Vec3 point, Vec3 normal, Vec3 u, Vec3 v, out int openContours)
    {
        var result = PlaneSplitter.Split(mesh, point.ToVector3D(), normal.ToVector3D());
        openContours = result.OpenContourCount + (result.Below is null || result.Above is null ? 1 : 0);
        return result.Contours
            .Select(loop => loop.Select(p =>
            {
                var q = Vec3.From(p);
                return new Point2(q.Dot(u), q.Dot(v));
            }).ToList())
            .ToList();
    }

    /// <summary>Douglas-Peucker on a closed polygon: every removed point lies within <paramref name="tolerance"/> of the result.</summary>
    public static List<Point2> Simplify(IReadOnlyList<Point2> loop, double tolerance)
    {
        if (loop.Count < 4 || !(tolerance > 0))
            return [.. loop];

        // Split at the two mutually farthest points so both halves are open polylines.
        var a = 0;
        var b = 0;
        var best = -1.0;
        for (var i = 0; i < loop.Count; i++)
        {
            var d = Dist2(loop[0], loop[i]);
            if (d > best)
            {
                best = d;
                b = i;
            }
        }

        best = -1;
        for (var i = 0; i < loop.Count; i++)
        {
            var d = Dist2(loop[b], loop[i]);
            if (d > best)
            {
                best = d;
                a = i;
            }
        }

        if (a > b)
            (a, b) = (b, a);
        var keep = new bool[loop.Count];
        keep[a] = keep[b] = true;
        Mark(loop, a, b, tolerance, keep);
        Mark(loop, b, a + loop.Count, tolerance, keep);
        var result = new List<Point2>();
        for (var i = 0; i < loop.Count; i++)
        {
            if (keep[i])
                result.Add(loop[i]);
        }

        return result;
    }

    private static void Mark(IReadOnlyList<Point2> loop, int start, int end, double tolerance, bool[] keep)
    {
        var stack = new Stack<(int, int)>();
        stack.Push((start, end));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            if (e - s < 2)
                continue;
            var ps = loop[s % loop.Count];
            var pe = loop[e % loop.Count];
            var farthest = -1;
            var max = tolerance;
            for (var i = s + 1; i < e; i++)
            {
                var d = SegmentDistance(loop[i % loop.Count], ps, pe);
                if (d > max)
                {
                    max = d;
                    farthest = i;
                }
            }

            if (farthest < 0)
                continue;
            keep[farthest % loop.Count] = true;
            stack.Push((s, farthest));
            stack.Push((farthest, e));
        }

    }

    public static double SegmentDistance(Point2 p, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var length2 = dx * dx + dy * dy;
        var t = length2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0, 1) : 0;
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    public static double SignedArea(IReadOnlyList<Point2> loop)
    {
        double area = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            area += p.X * q.Y - q.X * p.Y;
        }

        return area / 2;
    }

    private static OutlineLoop ToLoop(List<Point2> points)
    {
        double perimeter = 0;
        for (var i = 0; i < points.Count; i++)
            perimeter += Math.Sqrt(Dist2(points[i], points[(i + 1) % points.Count]));
        return new OutlineLoop(points, SignedArea(points), perimeter);
    }

    private static OutlineHole Hole(OutlineLoop loop)
    {
        var (min, max) = loop.Bounds;
        // Area centroid of the polygon.
        double cx = 0, cy = 0, a = 0;
        var p = loop.Points;
        for (var i = 0; i < p.Count; i++)
        {
            var q = p[(i + 1) % p.Count];
            var cross = p[i].X * q.Y - q.X * p[i].Y;
            a += cross;
            cx += (p[i].X + q.X) * cross;
            cy += (p[i].Y + q.Y) * cross;
        }

        var centre = Math.Abs(a) > 1e-12 ? new Point2(cx / (3 * a), cy / (3 * a)) : new Point2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        var radii = p.Select(pt => Math.Sqrt(Dist2(pt, centre))).ToList();
        var mean = radii.Average();
        var round = p.Count >= 8 && mean > 0 && radii.All(r => Math.Abs(r - mean) <= mean * RoundTolerance);
        return new OutlineHole(loop, centre, round ? 2 * mean : null, max.X - min.X, max.Y - min.Y);
    }

    private static double Dist2(Point2 a, Point2 b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}
