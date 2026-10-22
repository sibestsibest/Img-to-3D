using ImageTo3D.Core.Reconstruction;

namespace ImageTo3D.Core.Tests;

public class ReconstructionPlannerTests
{
    private static ReconstructionEvidence Evidence(
        bool input = true,
        int sameObject = 0,
        int pendingModels = 0,
        IReadOnlyList<ModelEvidence>? accepted = null,
        string? engine = "TripoSR",
        bool reachable = true,
        int acceptedReferences = 0) =>
        new(input, true, 10, acceptedReferences, sameObject, pendingModels, accepted ?? [], engine, reachable, 5);

    private static ModelEvidence Upload(Guid? id = null, bool? closed = true, double score = 0.7, string scale = "plausible", bool licenceKnown = true, bool? derivatives = true) =>
        new(id ?? Guid.NewGuid(), "Kit part", HasFile: true, GeometryChecked: true, licenceKnown, derivatives, closed, scale, score);

    private static ModelEvidence Listing() => new(Guid.NewGuid(), "Sketchfab listing", HasFile: false, GeometryChecked: false, true, true, null, null, 0.9);

    [Fact]
    public void Routes_AreListedInPreferenceOrder()
    {
        var plan = ReconstructionPlanner.Plan(Evidence());
        Assert.Equal(ReconstructionRoutes.Ordered, plan.Routes.Select(r => r.Route));
    }

    [Fact]
    public void AcceptedUpload_IsReusedAndBlocksGeneration()
    {
        var model = Upload();
        var plan = ReconstructionPlanner.Plan(Evidence(sameObject: 2, accepted: [model], engine: "StableFast3D"));

        Assert.Equal(ReconstructionRoutes.ReuseExistingModel, plan.Recommended);
        Assert.Equal(model.Id, plan.Route(ReconstructionRoutes.ReuseExistingModel).ModelId);
        foreach (var route in new[] { ReconstructionRoutes.Hybrid, ReconstructionRoutes.SingleImage })
        {
            var option = plan.Route(route);
            Assert.False(option.IsAvailable);
            Assert.Contains(option.Reasons, r => r.Code == "existing_model_accepted" && r.Severity == "blocker");
        }

        Assert.True(plan.Route(ReconstructionRoutes.ManualGeometry).IsAvailable);
    }

    [Fact]
    public void ListingOnly_IsNotReusable()
    {
        var plan = ReconstructionPlanner.Plan(Evidence(accepted: [Listing()]));

        var reuse = plan.Route(ReconstructionRoutes.ReuseExistingModel);
        Assert.Equal(RouteAvailability.Unavailable, reuse.Availability);
        Assert.Contains(reuse.Reasons, r => r.Code == "listing_only");
        Assert.Equal(ReconstructionRoutes.SingleImage, plan.Recommended);
    }

    [Fact]
    public void BestReusable_PrefersClosedMeshesThenScore()
    {
        var open = Upload(closed: false, score: 0.95);
        var closedLow = Upload(closed: true, score: 0.5);
        var closedHigh = Upload(closed: true, score: 0.6);

        Assert.Equal(closedHigh.Id, ReconstructionPlanner.BestReusable([open, closedLow, Listing(), closedHigh])!.Id);
        Assert.Null(ReconstructionPlanner.BestReusable([Listing()]));
    }

    [Fact]
    public void Reuse_WarnsAboutOpenMeshesUnitsAndLicences()
    {
        var plan = ReconstructionPlanner.Plan(Evidence(accepted: [Upload(closed: false, scale: "unitless", licenceKnown: false, derivatives: false)]));

        var codes = plan.Route(ReconstructionRoutes.ReuseExistingModel).Reasons.Select(r => r.Code).ToList();
        Assert.Contains("model_open", codes);
        Assert.Contains("model_unitless", codes);
        Assert.Contains("model_licence_unknown", codes);
        Assert.Contains("model_no_derivatives", codes);
        Assert.True(plan.Route(ReconstructionRoutes.ReuseExistingModel).IsAvailable);
    }

    [Fact]
    public void MultiView_IsAlwaysUnsupported()
    {
        foreach (var engine in new[] { "TripoSR", "StableFast3D", "SPAR3D", null })
        {
            var option = ReconstructionPlanner.Plan(Evidence(sameObject: 5, engine: engine)).Route(ReconstructionRoutes.MultiView);
            Assert.Equal(RouteAvailability.Unsupported, option.Availability);
            Assert.Contains(option.Reasons, r => r.Code == "no_multiview_engine");
        }
    }

    [Theory]
    [InlineData("StableFast3D", true)]
    [InlineData("SPAR3D", true)]
    [InlineData("TripoSR", false)]
    [InlineData(null, false)]
    public void Hybrid_NeedsAnEngineThatUsesExtraPhotos(string? engine, bool available)
    {
        var plan = ReconstructionPlanner.Plan(Evidence(sameObject: 2, engine: engine));

        var hybrid = plan.Route(ReconstructionRoutes.Hybrid);
        Assert.Equal(available, hybrid.IsAvailable);
        Assert.Equal(2, hybrid.ExtraPhotos);
        Assert.Equal(available ? ReconstructionRoutes.Hybrid : ReconstructionRoutes.SingleImage, plan.Recommended);
        if (available)
            Assert.Contains(hybrid.Reasons, r => r.Code == "shape_from_one_photo");
        else
            Assert.Contains(hybrid.Reasons, r => r.Code == "engine_ignores_extras");
    }

    [Fact]
    public void Hybrid_NeedsSameObjectViewsAndCapsThem()
    {
        var none = ReconstructionPlanner.Plan(Evidence(sameObject: 0, engine: "SPAR3D", acceptedReferences: 3));
        Assert.Contains(none.Route(ReconstructionRoutes.Hybrid).Reasons, r => r.Code == "no_extra_views");
        Assert.Contains(none.Notes, n => n.Code == "no_same_object");

        var many = ReconstructionPlanner.Plan(Evidence(sameObject: 9, engine: "SPAR3D")).Route(ReconstructionRoutes.Hybrid);
        Assert.Equal(5, many.ExtraPhotos);
        Assert.Contains(many.Reasons, r => r.Code == "extra_views_capped");
    }

    [Fact]
    public void UnreachableEngine_BlocksHybridButOnlyWarnsForSingleImage()
    {
        var plan = ReconstructionPlanner.Plan(Evidence(sameObject: 1, engine: null, reachable: false));

        Assert.Contains(plan.Route(ReconstructionRoutes.Hybrid).Reasons, r => r.Code == "engine_unreachable" && r.Severity == "blocker");
        var single = plan.Route(ReconstructionRoutes.SingleImage);
        Assert.True(single.IsAvailable);
        Assert.Contains(single.Reasons, r => r.Code == "engine_unreachable" && r.Severity == "warning");
    }

    [Fact]
    public void NoPhotoAndNoModel_FallsBackToManualGeometry()
    {
        var plan = ReconstructionPlanner.Plan(Evidence(input: false));

        Assert.Equal(ReconstructionRoutes.ManualGeometry, plan.Recommended);
        Assert.Contains(plan.Route(ReconstructionRoutes.SingleImage).Reasons, r => r.Code == "no_input_image");
        Assert.Contains(plan.Route(ReconstructionRoutes.Hybrid).Reasons, r => r.Code == "no_input_image");
    }

    [Fact]
    public void StateBeforeDecision_ReflectsPendingModels()
    {
        Assert.Equal(ReconstructionStates.ReferenceSearch, ReconstructionPlanner.StateBeforeDecision(Evidence()));
        Assert.Equal(ReconstructionStates.ModelCandidateReview, ReconstructionPlanner.StateBeforeDecision(Evidence(pendingModels: 2)));
        Assert.Contains(ReconstructionPlanner.Plan(Evidence(pendingModels: 2)).Notes, n => n.Code == "models_pending");
    }

    [Fact]
    public void Executable_ExcludesMultiViewAndOnlyGenerationRunsInference()
    {
        Assert.DoesNotContain(ReconstructionRoutes.MultiView, ReconstructionRoutes.Executable);
        Assert.True(ReconstructionRoutes.RunsInference(ReconstructionRoutes.Hybrid));
        Assert.True(ReconstructionRoutes.RunsInference(ReconstructionRoutes.SingleImage));
        Assert.False(ReconstructionRoutes.RunsInference(ReconstructionRoutes.ReuseExistingModel));
        Assert.False(ReconstructionRoutes.RunsInference(ReconstructionRoutes.ManualGeometry));
    }
}
