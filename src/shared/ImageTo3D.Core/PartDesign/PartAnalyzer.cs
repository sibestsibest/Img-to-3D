using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.PartDesign;

/// <summary>Fabrication categories. <see cref="NeedsReview"/> means the evidence does not support a suggestion.</summary>
public static class PartCategories
{
    public const string Sheet = "sheet";
    public const string Rod = "rod";
    public const string Tube = "tube";
    public const string StockPart = "stock_part";
    public const string Custom3D = "custom_3d";
    public const string NeedsReview = "needs_review";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Sheet, Rod, Tube, StockPart, Custom3D, NeedsReview };
}

/// <summary>Measured facts about one part, in the mesh's units (millimetres for design parts).</summary>
/// <param name="Volume">Only set for a closed, outward-facing mesh.</param>
public sealed record PartGeometryReport(
    int VertexCount,
    int TriangleCount,
    BoundingBox Bounds,
    OrientedBox OrientedBox,
    MeshTopologyReport Topology,
    double SurfaceArea,
    double? Volume,
    IReadOnlyList<GeometryIssue> Issues)
{
    public IssueSeverity? WorstSeverity => Issues.Count == 0 ? null : Issues.Max(i => i.Severity);
}

/// <param name="Category">One of <see cref="PartCategories"/>; never <see cref="PartCategories.StockPart"/> (that needs a human).</param>
/// <param name="Confidence">0..1, a heuristic score, not a probability.</param>
public sealed record PartSuggestion(
    string Category,
    double Confidence,
    IReadOnlyList<string> Reasons,
    double LengthMm,
    double WidthMm,
    double ThicknessMm,
    double? OuterDiameterMm,
    double? InnerDiameterMm);

/// <summary>
/// Deterministic first-pass analysis. Measurements come from the mesh; the category is advisory
/// and based on simple shape ratios. Dimensions are only as good as the model's scale, which for a
/// generated model is an estimate.
/// </summary>
public static class PartAnalyzer
{
    public const double SuspiciouslySmallMm = 0.5;
    public const double SuspiciouslyLargeMm = 2000;

    public static PartGeometryReport Measure(Mesh mesh)
    {
        var issues = new List<GeometryIssue>();
        var validation = MeshValidator.Validate(mesh);
        foreach (var error in validation.Errors)
            issues.Add(new GeometryIssue("invalid_geometry", IssueSeverity.Error, error));

        var bounds = MeshOperations.ComputeBounds(mesh);
        if (!validation.Errors.Any(e => e.Contains("indices", StringComparison.Ordinal) || e.Contains("non-finite", StringComparison.Ordinal)) && mesh.TriangleCount > 0)
        {
            var topology = MeshTopology.Analyze(mesh);
            var area = MeshTopology.SurfaceArea(mesh);
            double? volume = null;
            if (topology.IsClosed)
            {
                var signed = MeshOperations.SignedVolume(mesh);
                if (signed > 0)
                    volume = signed;
                else
                    issues.Add(new GeometryIssue("inverted_normals", IssueSeverity.Warning, "The surface is closed but faces point inwards (negative volume)."));
            }

            if (topology.DegenerateTriangleCount > 0)
                issues.Add(new GeometryIssue("degenerate_triangles", IssueSeverity.Warning, $"{topology.DegenerateTriangleCount} zero-area triangle(s)."));
            if (topology.BoundaryEdgeCount > 0)
                issues.Add(new GeometryIssue("open_surface", IssueSeverity.Warning, $"The surface is not closed ({topology.BoundaryEdgeCount} open edges); volume and solid exports are not reliable."));
            if (topology.NonManifoldEdgeCount > 0)
                issues.Add(new GeometryIssue("non_manifold", IssueSeverity.Warning, $"{topology.NonManifoldEdgeCount} edge(s) are shared by more than two triangles."));
            if (topology.InconsistentlyOrientedEdgeCount > 0)
                issues.Add(new GeometryIssue("inconsistent_orientation", IssueSeverity.Warning, $"{topology.InconsistentlyOrientedEdgeCount} edge(s) have neighbouring faces with opposite winding."));

            var longest = bounds.Size.MaxComponent;
            if (longest < SuspiciouslySmallMm)
                issues.Add(new GeometryIssue("suspicious_scale", IssueSeverity.Warning, $"The part is only {longest:0.###} mm long; check the model scale."));
            else if (longest > SuspiciouslyLargeMm)
                issues.Add(new GeometryIssue("suspicious_scale", IssueSeverity.Warning, $"The part is {longest:0} mm long; check the model scale."));

            return new PartGeometryReport(mesh.VertexCount, mesh.TriangleCount, bounds, OrientedBounds.Compute(mesh), topology, area, volume, issues);
        }

        return new PartGeometryReport(mesh.VertexCount, mesh.TriangleCount, bounds, OrientedBounds.Compute(mesh), new MeshTopologyReport(0, 0, 0, 0, 0), 0, null, issues);
    }

    public static PartSuggestion Suggest(Mesh mesh, PartGeometryReport report)
    {
        var e = report.OrientedBox.Extents;
        double length = e.X, width = e.Y, thickness = e.Z;
        var reasons = new List<string>();

        if (report.Issues.Any(i => i.Severity == IssueSeverity.Error) || mesh.TriangleCount < 4 || !(length > 0))
        {
            reasons.Add("The geometry is invalid or too small to classify.");
            return new PartSuggestion(PartCategories.NeedsReview, 0, reasons, length, width, thickness, null, null);
        }

        var closed = report.Topology.IsClosed && report.Volume is not null;
        if (!closed)
            reasons.Add("The surface is not a closed solid, so the suggestion is uncertain.");

        var flatness = width > 0 ? thickness / width : 1;
        var slenderness = width / length;
        var roundness = width > 0 ? thickness / width : 0;

        // Sheet: thin in one direction and dominated by two parallel faces.
        if (flatness <= 0.2)
        {
            var planar = PlanarAreaFraction(mesh, report.OrientedBox.Axes[2]);
            if (planar >= 0.7)
            {
                reasons.Add($"Thin: thickness is {flatness:P0} of width.");
                reasons.Add($"{planar:P0} of the surface faces the thin direction (flat faces).");
                return Finish(PartCategories.Sheet, 0.55 + 0.35 * Math.Min(1, (planar - 0.7) / 0.3), null, null);
            }

            reasons.Add($"Thin ({flatness:P0} of width) but only {planar:P0} of the surface is flat; possibly curved or ribbed.");
        }

        // Rod or tube: long, with a compact cross-section.
        if (slenderness <= 0.35 && roundness >= 0.75)
        {
            reasons.Add($"Long and slender: width is {slenderness:P0} of length with a compact cross-section.");
            var outer = (width + thickness) / 2;
            if (closed)
            {
                var fill = report.Volume!.Value / (length * width * thickness);
                var hollow = HollowFraction(mesh, report.OrientedBox, out var innerDiameter);
                if (fill < 0.65 && hollow >= 0.15)
                {
                    reasons.Add($"Fills {fill:P0} of its box and {hollow:P0} of mid-length vertices lie near the axis: hollow.");
                    return Finish(PartCategories.Tube, 0.6, outer, innerDiameter);
                }

                if (fill is >= 0.7 and <= 0.86)
                {
                    reasons.Add($"Fills {fill:P0} of its box (a round section fills about 79%).");
                    return Finish(PartCategories.Rod, 0.7, outer, null);
                }

                if (fill > 0.86)
                {
                    reasons.Add($"Fills {fill:P0} of its box: square or rectangular section.");
                    return Finish(PartCategories.Rod, 0.6, null, null);
                }

                reasons.Add($"Fills only {fill:P0} of its box; the section is irregular.");
            }
            else
            {
                return Finish(PartCategories.Rod, 0.35, outer, null);
            }
        }

        reasons.Add("No simple stock shape (sheet, rod, tube) matches; likely needs custom modelling or printing.");
        return Finish(PartCategories.Custom3D, closed ? 0.5 : 0.3, null, null);

        PartSuggestion Finish(string category, double confidence, double? outerDiameter, double? innerDiameter)
        {
            if (!closed)
                confidence = Math.Min(confidence, 0.35);
            confidence = Math.Round(confidence, 2);
            // Low confidence means the evidence is insufficient: say so instead of guessing.
            var final = confidence < 0.5 ? PartCategories.NeedsReview : category;
            if (final != category)
                reasons.Add($"Closest match is '{category}', but confidence {confidence:0.00} is too low to suggest it.");
            return new PartSuggestion(final, confidence, reasons, length, width, thickness, outerDiameter, innerDiameter);
        }
    }

    /// <summary>Share of surface area whose normal is (anti)parallel to <paramref name="axis"/>.</summary>
    public static double PlanarAreaFraction(Mesh mesh, Vector3D axis)
    {
        var a = Vec3.From(axis);
        var p = mesh.Positions;
        double total = 0, aligned = 0;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var pa = Vec3.At(p, mesh.Indices[t]);
            var cross = (Vec3.At(p, mesh.Indices[t + 1]) - pa).Cross(Vec3.At(p, mesh.Indices[t + 2]) - pa);
            var area = cross.Length / 2;
            total += area;
            if (area > 0 && Math.Abs(cross.Normalized().Dot(a)) > 0.95)
                aligned += area;
        }

        return total > 0 ? aligned / total : 0;
    }

    /// <summary>
    /// Share of the side-wall area whose normal points towards the long axis (an inner wall), plus an
    /// area-weighted inner diameter estimate. Independent of how vertices are distributed.
    /// </summary>
    private static double HollowFraction(Mesh mesh, OrientedBox box, out double? innerDiameter)
    {
        var axis = Vec3.From(box.Axes[0]);
        var center = Vec3.From(box.Center);
        var p = mesh.Positions;
        double sideArea = 0, inwardArea = 0, inwardRadius = 0;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var pa = Vec3.At(p, mesh.Indices[t]);
            var pb = Vec3.At(p, mesh.Indices[t + 1]);
            var pc = Vec3.At(p, mesh.Indices[t + 2]);
            var cross = (pb - pa).Cross(pc - pa);
            var area = cross.Length / 2;
            if (!(area > 0))
                continue;
            var normal = cross.Normalized();
            if (Math.Abs(normal.Dot(axis)) > 0.5)
                continue; // end face, not a side wall
            var d = (pa + pb + pc) * (1.0 / 3) - center;
            var radial = d - axis * d.Dot(axis);
            var r = radial.Length;
            if (!(r > 0))
                continue;
            sideArea += area;
            if (normal.Dot(radial * (1 / r)) < 0)
            {
                inwardArea += area;
                inwardRadius += r * area;
            }
        }

        innerDiameter = inwardArea > 0 ? 2 * inwardRadius / inwardArea : null;
        return sideArea > 0 ? inwardArea / sideArea : 0;
    }
}
