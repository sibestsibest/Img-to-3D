using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.PartDesign;

/// <summary>How two parts are joined. Part A and part B have a role that depends on the kind.</summary>
public static class RelationKinds
{
    /// <summary>A and B are glued face to face; their surfaces should touch.</summary>
    public const string Glued = "glued";

    /// <summary>A (a rod or tube) is pushed into a hole in B; the hole is the opening size, or B's bore if B is a tube.</summary>
    public const string PegInHole = "peg_in_hole";

    /// <summary>A (a tube) slides over B (a rod or a smaller tube).</summary>
    public const string TubeOverRod = "tube_over_rod";

    /// <summary>B sits inside A (e.g. telescoping tubes); the overlap is intended.</summary>
    public const string Nested = "nested";

    /// <summary>B (a sheet) slots into a slot in A; the slot width is the opening size.</summary>
    public const string Slot = "slot";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Glued, PegInHole, TubeOverRod, Nested, Slot };
}

/// <summary>A size used in a fit, and whether the user specified it or it comes from the model.</summary>
public readonly record struct FitSize(double Value, bool Specified);

/// <summary>The sizes of one part that fits can use; null when unknown.</summary>
/// <param name="Outer">Rod diameter or tube outer diameter.</param>
/// <param name="Inner">Tube inner diameter.</param>
/// <param name="Thickness">Sheet thickness.</param>
public sealed record FitPart(string Category, FitSize? Outer, FitSize? Inner, FitSize? Thickness);

/// <param name="Clearance">Opening minus the inserted size (negative = interference), or null when not computed.</param>
/// <param name="Status">"ok", "too_tight", "too_loose", "unknown" or "not_applicable".</param>
/// <param name="Basis">"specified" when every size was entered by the user, "estimated" when one comes from the model.</param>
public sealed record FitResult(
    string Status,
    double? OpeningMm,
    double? InsertedMm,
    double? Clearance,
    string? Basis,
    IReadOnlyList<GeometryIssue> Issues);

/// <summary>
/// Connection constraints for a relation between two parts: whether the parts' categories suit the
/// kind of joint, and the clearance of the fit against the project's allowed range (fit allowance).
/// Sizes are millimetres; the user's values are used before the model's estimates.
/// </summary>
public static class FitCheck
{
    public static FitResult Check(string kind, FitPart a, FitPart b, double? openingMm, double minClearanceMm, double maxClearanceMm)
    {
        var issues = new List<GeometryIssue>();
        void Mismatch(string message) => issues.Add(new GeometryIssue("relation_category_mismatch", IssueSeverity.Warning, message));
        static bool Round(FitPart p) => p.Category is PartCategories.Rod or PartCategories.Tube;

        FitSize? opening = null, inserted = null;
        string? openingLabel = null;
        switch (kind)
        {
            case RelationKinds.Glued:
                return new FitResult("not_applicable", null, null, null, null, issues);

            case RelationKinds.PegInHole:
                if (!Round(a))
                    Mismatch("For a peg in a hole, part A is the peg and should be a rod or tube.");
                inserted = a.Outer;
                opening = openingMm is { } hole ? new FitSize(hole, true) : b.Category == PartCategories.Tube ? b.Inner : null;
                openingLabel = "hole";
                break;

            case RelationKinds.TubeOverRod:
                if (a.Category != PartCategories.Tube)
                    Mismatch("For a tube over a rod, part A should be the tube.");
                if (!Round(b))
                    Mismatch("For a tube over a rod, part B should be a rod or a smaller tube.");
                opening = a.Inner;
                inserted = b.Outer;
                openingLabel = "tube bore";
                break;

            case RelationKinds.Nested:
                // Only round parts have a clearance to check; other nested parts only need the overlap allowed.
                if (a.Category != PartCategories.Tube || !Round(b))
                    return new FitResult("not_applicable", null, null, null, null, issues);
                opening = a.Inner;
                inserted = b.Outer;
                openingLabel = "outer tube bore";
                break;

            case RelationKinds.Slot:
                if (b.Category != PartCategories.Sheet)
                    Mismatch("For a slot, part B is the sheet that goes into it.");
                opening = openingMm is { } width ? new FitSize(width, true) : null;
                inserted = b.Thickness;
                openingLabel = "slot width";
                break;

            default:
                throw new ArgumentException($"Unknown relation kind '{kind}'.", nameof(kind));
        }

        if (opening is not { } o || inserted is not { } i)
        {
            var missing = opening is null ? $"the {openingLabel}" : "the size of the inserted part";
            issues.Add(new GeometryIssue("fit_unknown", IssueSeverity.Warning, $"The fit cannot be checked: {missing} is not known. Enter it to check the clearance."));
            return new FitResult("unknown", opening?.Value, inserted?.Value, null, null, issues);
        }

        var clearance = Math.Round(o.Value - i.Value, 4);
        var basis = o.Specified && i.Specified ? "specified" : "estimated";
        if (basis == "estimated")
            issues.Add(new GeometryIssue("fit_from_estimates", IssueSeverity.Info, "Part of this fit uses sizes measured on the model; enter the real sizes to rely on it."));

        string status;
        if (clearance < minClearanceMm)
        {
            status = "too_tight";
            var what = clearance < 0 ? $"{-clearance:0.###} mm interference" : $"{clearance:0.###} mm clearance";
            issues.Add(new GeometryIssue("fit_too_tight", IssueSeverity.Warning,
                $"Too tight: {what} ({openingLabel} {o.Value:0.###} mm, part {i.Value:0.###} mm); the project allows {minClearanceMm:0.###} to {maxClearanceMm:0.###} mm."));
        }
        else if (clearance > maxClearanceMm)
        {
            status = "too_loose";
            issues.Add(new GeometryIssue("fit_too_loose", IssueSeverity.Warning,
                $"Too loose: {clearance:0.###} mm clearance ({openingLabel} {o.Value:0.###} mm, part {i.Value:0.###} mm); the project allows {minClearanceMm:0.###} to {maxClearanceMm:0.###} mm."));
        }
        else
        {
            status = "ok";
            issues.Add(new GeometryIssue("fit_ok", IssueSeverity.Info, $"Clearance {clearance:0.###} mm is within the project limits."));
        }

        return new FitResult(status, o.Value, i.Value, clearance, basis, issues);
    }
}
