using ImageTo3D.Api.Models;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// Cross-field manufacturing checks. Out-of-range single values are rejected at the API boundary;
/// these rules report combinations that are inconsistent or incomplete for the chosen category,
/// so a user can save work in progress and see what is still missing.
/// </summary>
public static class ManufacturingValidator
{
    public static IReadOnlyList<GeometryIssue> Validate(DesignPart part, PartDesignProject project)
    {
        var issues = new List<GeometryIssue>();
        void Warn(string code, string message) => issues.Add(new GeometryIssue(code, IssueSeverity.Warning, message));

        switch (part.Category)
        {
            case PartCategories.NeedsReview:
                Warn("category_needs_review", "Choose how this part will be made (sheet, rod, tube, stock part or custom 3D).");
                break;
            case PartCategories.Sheet:
                if (part.ThicknessMm is null)
                    Warn("missing_thickness", "Set the sheet thickness.");
                else if (part.ThicknessMm > 10)
                    Warn("unusual_thickness", $"{part.ThicknessMm:0.##} mm is unusually thick for styrene sheet; check the value.");
                break;
            case PartCategories.Rod:
                if (part.DiameterMm is null)
                    Warn("missing_diameter", "Set the rod diameter.");
                if (part.CutLengthMm is null)
                    Warn("missing_cut_length", "Set the cut length.");
                break;
            case PartCategories.Tube:
                if (part.OuterDiameterMm is null)
                    Warn("missing_outer_diameter", "Set the tube outer diameter.");
                if (part.InnerDiameterMm is null)
                    Warn("missing_inner_diameter", "Set the tube inner diameter.");
                if (part.CutLengthMm is null)
                    Warn("missing_cut_length", "Set the cut length.");
                if (part.OuterDiameterMm is { } od && part.InnerDiameterMm is { } id && id >= od)
                    issues.Add(new GeometryIssue("tube_inner_not_less_than_outer", IssueSeverity.Error, $"Inner diameter ({id:0.##} mm) must be smaller than outer diameter ({od:0.##} mm)."));
                break;
            case PartCategories.StockPart:
                if (string.IsNullOrWhiteSpace(part.StockDescription))
                    Warn("missing_stock_description", "Describe the commercial part (type, size, maker).");
                break;
            case PartCategories.Custom3D:
                if (part.NeedsFurtherModelling)
                    issues.Add(new GeometryIssue("needs_modelling", IssueSeverity.Info, "Marked as needing further manual modelling or 3D printing."));
                break;
        }

        if (part.FitAllowanceMm is { } fit && (fit < project.FitAllowanceMinMm || fit > project.FitAllowanceMaxMm))
            Warn("fit_allowance_out_of_range", $"Fit allowance {fit:0.###} mm is outside the project limits ({project.FitAllowanceMinMm:0.###} to {project.FitAllowanceMaxMm:0.###} mm).");

        if (part.Category is not (PartCategories.Rod or PartCategories.Tube) && part.CutAngleDeg is not null)
            issues.Add(new GeometryIssue("cut_angle_unused", IssueSeverity.Info, "Cut angle only applies to rods and tubes."));

        if (part.ConfirmedLengthMm is { } confirmed && part.EstimatedLengthMm > 0)
        {
            var ratio = confirmed / part.EstimatedLengthMm;
            if (ratio is < 0.75 or > 1.33)
            {
                issues.Add(new GeometryIssue(
                    "confirmed_differs_from_estimate",
                    IssueSeverity.Info,
                    $"Confirmed length differs from the model estimate by {Math.Abs(ratio - 1):P0}; the model scale may need adjusting."));
            }
        }

        return issues;
    }
}
