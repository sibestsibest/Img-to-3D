using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

/// <summary>Phase 7: connection constraints (fit clearance against the project's limits).</summary>
public class FitCheckTests
{
    private static FitPart Rod(double d, bool specified = true) => new(PartCategories.Rod, new FitSize(d, specified), null, null);

    private static FitPart Tube(double od, double id, bool specified = true) => new(PartCategories.Tube, new FitSize(od, specified), new FitSize(id, specified), null);

    private static FitPart Sheet(double t) => new(PartCategories.Sheet, null, null, new FitSize(t, true));

    [Fact]
    public void TubeOverRod_WithinLimits_IsOk()
    {
        var result = FitCheck.Check(RelationKinds.TubeOverRod, Tube(5, 3.2), Rod(3), null, -0.5, 0.5);

        Assert.Equal("ok", result.Status);
        Assert.Equal(0.2, result.Clearance!.Value, 6);
        Assert.Equal("specified", result.Basis);
        Assert.Contains(result.Issues, i => i.Code == "fit_ok");
    }

    [Theory]
    [InlineData(2.0, "too_tight", "fit_too_tight")]
    [InlineData(4.0, "too_loose", "fit_too_loose")]
    public void PegInHole_OutsideLimits_Warns(double hole, string status, string code)
    {
        var result = FitCheck.Check(RelationKinds.PegInHole, Rod(3), Sheet(1), hole, -0.1, 0.3);

        Assert.Equal(status, result.Status);
        Assert.Contains(result.Issues, i => i.Code == code && i.Severity == Geometry.IssueSeverity.Warning);
    }

    [Fact]
    public void PegInHole_UsesTheBoreOfATube_WhenNoHoleIsGiven()
    {
        var result = FitCheck.Check(RelationKinds.PegInHole, Rod(2.9), Tube(5, 3), null, 0, 0.5);
        Assert.Equal(("ok", 3.0, 2.9), (result.Status, result.OpeningMm!.Value, result.InsertedMm!.Value));
    }

    [Fact]
    public void MissingOpening_IsUnknown()
    {
        var result = FitCheck.Check(RelationKinds.Slot, Sheet(2), Sheet(1), null, 0, 0.3);
        Assert.Equal("unknown", result.Status);
        Assert.Contains(result.Issues, i => i.Code == "fit_unknown");
    }

    [Fact]
    public void Slot_ComparesSlotWidthWithSheetThickness()
    {
        var result = FitCheck.Check(RelationKinds.Slot, Sheet(2), Sheet(1), 1.1, 0, 0.3);
        Assert.Equal(("ok", 0.1), (result.Status, Math.Round(result.Clearance!.Value, 6)));
    }

    [Fact]
    public void EstimatedSizes_AreMarked()
    {
        var result = FitCheck.Check(RelationKinds.Nested, Tube(10, 8, specified: false), Tube(7.8, 6), null, -0.5, 0.5);
        Assert.Equal(("ok", "estimated"), (result.Status, result.Basis));
        Assert.Contains(result.Issues, i => i.Code == "fit_from_estimates");
    }

    [Fact]
    public void WrongCategories_AreReported()
    {
        var result = FitCheck.Check(RelationKinds.TubeOverRod, Rod(3), Sheet(1), null, 0, 0.5);
        Assert.Equal(2, result.Issues.Count(i => i.Code == "relation_category_mismatch"));
    }

    [Fact]
    public void GluedAndNonRoundNested_HaveNoFit()
    {
        Assert.Equal("not_applicable", FitCheck.Check(RelationKinds.Glued, Sheet(1), Sheet(1), null, 0, 0).Status);
        Assert.Equal("not_applicable", FitCheck.Check(RelationKinds.Nested, Sheet(1), Sheet(1), null, 0, 0).Status);
    }

    [Fact]
    public void UnknownKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => FitCheck.Check("welded", Rod(1), Rod(1), null, 0, 0));
    }
}
