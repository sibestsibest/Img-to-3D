namespace ImageTo3D.Core.Reconstruction;

/// <summary>Ways to obtain the geometry of an object, in order of preference.</summary>
public static class ReconstructionRoutes
{
    /// <summary>Use an existing model the user accepted (uploaded file, geometry checked); no inference runs.</summary>
    public const string ReuseExistingModel = "reuse_existing_model";

    /// <summary>Geometry from several photos with the optional Meshroom photogrammetry engine (unsupported when it is not installed).</summary>
    public const string MultiView = "multi_view_reconstruction";

    /// <summary>
    /// The existing single-image engine with accepted same-object references sent as extra photos.
    /// The shape still comes from the main photo; the extra photos only colour the sides it cannot see.
    /// </summary>
    public const string Hybrid = "hybrid_reconstruction";

    /// <summary>The existing single-image engine on the input photo alone.</summary>
    public const string SingleImage = "single_image_reconstruction";

    /// <summary>No usable source: an empty part design where stock shapes (plates, rods, tubes, blocks) are built by hand.</summary>
    public const string ManualGeometry = "manual_geometry_required";

    /// <summary>Preference order (master principle: retrieval first, reuse second, reconstruction third).</summary>
    public static readonly IReadOnlyList<string> Ordered = [ReuseExistingModel, MultiView, Hybrid, SingleImage, ManualGeometry];

    /// <summary>Routes that can be started (multi-view only while the photogrammetry engine is ready).</summary>
    public static readonly IReadOnlySet<string> Executable = new HashSet<string>(StringComparer.Ordinal) { ReuseExistingModel, MultiView, Hybrid, SingleImage, ManualGeometry };

    public static bool RunsInference(string route) => route is Hybrid or SingleImage;

    /// <summary>Routes that start a photogrammetry job (Meshroom) instead of an inference job.</summary>
    public static bool RunsPhotogrammetry(string route) => route is MultiView;
}

/// <summary>Where a reference session stands on the way to geometry.</summary>
public static class ReconstructionStates
{
    public const string ReferenceSearch = "reference_search";
    public const string ModelCandidateReview = "model_candidate_review";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    // While a route runs, the state is the route itself (e.g. single_image_reconstruction).
}

public static class RouteAvailability
{
    public const string Available = "available";

    /// <summary>Possible in principle, but not with what the session has now (the reasons say what is missing).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Not something the installed software can do at all.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>An accepted model candidate, as the planner sees it.</summary>
/// <param name="HasFile">The file is stored (an upload); search listings have no file.</param>
/// <param name="DerivativesAllowed">False for no-derivatives licences; null when unknown.</param>
/// <param name="ScaleStatus">"plausible", "suspicious" or "unitless" (STL); null when not checked.</param>
public sealed record ModelEvidence(
    Guid Id,
    string Title,
    bool HasFile,
    bool GeometryChecked,
    bool LicenseKnown,
    bool? DerivativesAllowed,
    bool? Closed,
    string? ScaleStatus,
    double? Score);

/// <param name="UsableSameObjectViews">Accepted pictures the user marked as the same object, with an image, not duplicates and not the input itself.</param>
/// <param name="Engine">Engine name reported by the inference service health (null when unknown).</param>
/// <param name="PhotogrammetryReady">Meshroom is installed and usable; <paramref name="PhotogrammetryReason"/> says why not otherwise.</param>
public sealed record ReconstructionEvidence(
    bool HasInputImage,
    bool Searched,
    int ReferenceCount,
    int AcceptedReferences,
    int UsableSameObjectViews,
    int PendingModels,
    IReadOnlyList<ModelEvidence> AcceptedModels,
    string? Engine,
    bool EngineReachable,
    int MaxExtraPhotos,
    bool PhotogrammetryReady = false,
    string? PhotogrammetryReason = null);

/// <param name="Severity">"info", "warning" or "blocker" (the route cannot start because of it).</param>
public sealed record RouteReason(string Code, string Severity, string Message);

/// <param name="ModelId">Reuse only: the model that would be used.</param>
/// <param name="ExtraPhotos">Hybrid only: how many references would be sent as extra photos.</param>
public sealed record RouteOption(string Route, string Availability, IReadOnlyList<RouteReason> Reasons, Guid? ModelId = null, int ExtraPhotos = 0)
{
    public bool IsAvailable => Availability == RouteAvailability.Available;
}

/// <param name="Recommended">First available route in preference order (manual geometry is always available).</param>
public sealed record ReconstructionPlan(string Recommended, IReadOnlyList<RouteOption> Routes, IReadOnlyList<RouteReason> Notes)
{
    public RouteOption Route(string route) => Routes.Single(r => r.Route == route);
}

/// <summary>
/// Deterministic choice of how to get geometry for a reference session. It never claims more than
/// the evidence supports: similarity is not identity, one photo cannot recover hidden surfaces, and
/// photogrammetry (several photos) runs only when Meshroom is installed.
/// </summary>
public static class ReconstructionPlanner
{
    /// <summary>Engines that colour hidden sides from extra photos (the Stability AI engines); TripoSR ignores them.</summary>
    public static bool UsesExtraPhotos(string? engine) => engine is "StableFast3D" or "SPAR3D";

    /// <summary>State while nothing has been started: still collecting references, or models wait for review.</summary>
    public static string StateBeforeDecision(ReconstructionEvidence e) =>
        e.PendingModels > 0 ? ReconstructionStates.ModelCandidateReview : ReconstructionStates.ReferenceSearch;

    /// <summary>The accepted model reuse would take: files before listings, checked geometry, closed meshes, then score.</summary>
    public static ModelEvidence? BestReusable(IReadOnlyList<ModelEvidence> accepted) =>
        accepted.Where(m => m.HasFile && m.GeometryChecked)
            .OrderByDescending(m => m.Closed == true)
            .ThenByDescending(m => m.Score ?? -1)
            .ThenBy(m => m.Id)
            .FirstOrDefault();

    public static ReconstructionPlan Plan(ReconstructionEvidence e)
    {
        var reuse = Reuse(e);
        var routes = new List<RouteOption>
        {
            reuse,
            MultiView(e, reuse.IsAvailable),
            Hybrid(e, reuse.IsAvailable),
            Single(e, reuse.IsAvailable),
            new(ReconstructionRoutes.ManualGeometry, RouteAvailability.Available,
            [
                new("manual_always", "info", "Creates an empty part design: add plates, rods, tubes and blocks with exact millimetre sizes. Nothing is generated or guessed."),
            ]),
        };

        var notes = new List<RouteReason>();
        if (!e.Searched && e.ReferenceCount == 0)
            notes.Add(new("not_searched", "info", "No reference search yet: search first, an existing model may make generation unnecessary."));
        if (e.PendingModels > 0)
            notes.Add(new("models_pending", "info", $"{e.PendingModels} model candidate(s) still pending review; accepting a suitable one avoids generating."));
        if (e.AcceptedReferences > 0 && e.UsableSameObjectViews == 0)
            notes.Add(new("no_same_object", "info", "Accepted pictures are only used as extra photos when you mark them as showing the same object."));

        var recommended = routes.First(r => r.IsAvailable).Route;
        return new ReconstructionPlan(recommended, routes, notes);
    }

    private static RouteOption Reuse(ReconstructionEvidence e)
    {
        if (e.AcceptedModels.Count == 0)
            return new(ReconstructionRoutes.ReuseExistingModel, RouteAvailability.Unavailable, [new("no_accepted_model", "blocker", "No existing model has been accepted.")]);

        var best = BestReusable(e.AcceptedModels);
        if (best is null)
        {
            return new(ReconstructionRoutes.ReuseExistingModel, RouteAvailability.Unavailable,
            [
                new("listing_only", "blocker", "The accepted models are search listings without a file. Download one from its page (where its licence allows) and upload it to have its geometry checked."),
            ]);
        }

        var reasons = new List<RouteReason> { new("accepted_model", "info", $"Accepted model \"{best.Title}\" is reused as is; no AI generation runs.") };
        if (best.Closed == false)
            reasons.Add(new("model_open", "warning", "The model is not a closed mesh: parts split from it may not be watertight."));
        if (best.ScaleStatus == "unitless")
            reasons.Add(new("model_unitless", "warning", "STL files carry no unit; millimetres are assumed. Check the size before fabrication."));
        else if (best.ScaleStatus == "suspicious")
            reasons.Add(new("model_scale_suspicious", "warning", "The file's declared size is implausible for a model kit part; check its units."));
        if (!best.LicenseKnown)
            reasons.Add(new("model_licence_unknown", "warning", "Licence unknown: keep the result for personal use and read the source's terms."));
        if (best.DerivativesAllowed == false)
            reasons.Add(new("model_no_derivatives", "warning", "No-derivatives licence: splitting it into parts is a modification; do not share the results."));
        return new(ReconstructionRoutes.ReuseExistingModel, RouteAvailability.Available, reasons, best.Id);
    }

    /// <summary>
    /// Reference pictures are previews of at most 640 px, so far more views are asked for than a
    /// set of full-resolution photos needs (the Photogrammetry page takes those).
    /// </summary>
    public const int MinReferenceViewsForPhotogrammetry = 8;

    private static RouteOption MultiView(ReconstructionEvidence e, bool reuseAvailable)
    {
        if (!e.PhotogrammetryReady)
        {
            return new(ReconstructionRoutes.MultiView, RouteAvailability.Unsupported,
            [
                new("no_multiview_engine", "blocker",
                    (e.PhotogrammetryReason ?? "The Meshroom photogrammetry engine is not installed.") +
                    " With the Stable Fast 3D or SPAR3D engine, extra photos colour the hidden sides (hybrid), but the shape still comes from one photo."),
            ]);
        }

        var views = (e.HasInputImage ? 1 : 0) + e.UsableSameObjectViews;
        var blockers = new List<RouteReason>();
        if (views < MinReferenceViewsForPhotogrammetry)
            blockers.Add(new("too_few_views", "blocker", $"{views} usable picture(s) of the same object (input photo plus accepted same-object pictures); at least {MinReferenceViewsForPhotogrammetry} are needed from a reference search. For your own photos use the Photogrammetry page."));
        if (reuseAvailable)
            blockers.Add(ExistingModelBlocker);
        if (blockers.Count > 0)
            return new(ReconstructionRoutes.MultiView, RouteAvailability.Unavailable, blockers, ExtraPhotos: views);

        return new(ReconstructionRoutes.MultiView, RouteAvailability.Available,
        [
            new("multi_view_photos", "info", $"{views} pictures go to Meshroom, which reconstructs the shape from the views themselves (nothing is guessed by an AI)."),
            new("reference_previews_small", "warning", "Reference pictures are previews of at most 640 px taken by different cameras: the reconstruction may fail or be coarse. Your own full-resolution photos on the Photogrammetry page work much better."),
            new("scale_unknown", "warning", "Photogrammetry does not know the real size: calibrate the scale with one known dimension afterwards."),
        ], ExtraPhotos: views);
    }

    private static RouteOption Hybrid(ReconstructionEvidence e, bool reuseAvailable)
    {
        var blockers = new List<RouteReason>();
        if (!e.HasInputImage)
            blockers.Add(new("no_input_image", "blocker", "The session has no input photo to generate from."));
        if (e.UsableSameObjectViews == 0)
            blockers.Add(new("no_extra_views", "blocker", "No accepted picture is marked as the same object (duplicates and copies of the input are not counted)."));
        if (!e.EngineReachable)
            blockers.Add(new("engine_unreachable", "blocker", "The inference service is not reachable, so it is unknown whether its engine uses extra photos."));
        else if (!UsesExtraPhotos(e.Engine))
            blockers.Add(new("engine_ignores_extras", "blocker", $"The connected engine ({e.Engine ?? "unknown"}) ignores extra photos; only Stable Fast 3D and SPAR3D use them."));
        if (reuseAvailable)
            blockers.Add(ExistingModelBlocker);

        var count = Math.Min(e.UsableSameObjectViews, e.MaxExtraPhotos);
        if (blockers.Count > 0)
            return new(ReconstructionRoutes.Hybrid, RouteAvailability.Unavailable, blockers, ExtraPhotos: count);

        var reasons = new List<RouteReason>
        {
            new("extra_views", "info", $"{count} accepted same-object picture(s) are sent as extra photos to colour the sides the input photo cannot see."),
            new("shape_from_one_photo", "warning", "The shape still comes from the input photo alone; hidden surfaces are guessed, not recovered."),
        };
        if (e.UsableSameObjectViews > e.MaxExtraPhotos)
            reasons.Add(new("extra_views_capped", "info", $"Only the {e.MaxExtraPhotos} best-ranked of {e.UsableSameObjectViews} pictures are sent."));
        return new(ReconstructionRoutes.Hybrid, RouteAvailability.Available, reasons, ExtraPhotos: count);
    }

    private static RouteOption Single(ReconstructionEvidence e, bool reuseAvailable)
    {
        var blockers = new List<RouteReason>();
        if (!e.HasInputImage)
            blockers.Add(new("no_input_image", "blocker", "The session has no input photo to generate from."));
        if (reuseAvailable)
            blockers.Add(ExistingModelBlocker);
        if (blockers.Count > 0)
            return new(ReconstructionRoutes.SingleImage, RouteAvailability.Unavailable, blockers);

        var reasons = new List<RouteReason>
        {
            new("single_photo", "warning", "Generated from one photo: hidden surfaces are guessed and the scale is an estimate."),
        };
        if (!e.EngineReachable)
            reasons.Add(new("engine_unreachable", "warning", "The inference service is not reachable right now; the job fails if it stays down."));
        return new(ReconstructionRoutes.SingleImage, RouteAvailability.Available, reasons);
    }

    private static readonly RouteReason ExistingModelBlocker =
        new("existing_model_accepted", "blocker", "An accepted existing model can be reused, so no heavy inference is run. Reject that model first to generate a new one.");
}
