using System.Text.Json;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.PartDesign;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Core.Reconstruction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.References;

/// <summary>
/// The reconstruction decision of a reference session: which way to get geometry is possible and
/// preferred (<see cref="ReconstructionPlanner"/>), and starting it. Reuse and manual geometry
/// create a part design directly; the generation routes hand the session's photos to the existing
/// job pipeline unchanged. The state of a started generation follows its job.
/// </summary>
public sealed class ReconstructionService(
    AppDbContext db,
    IReferenceArtifactStore store,
    JobService jobs,
    PartDesignService designs,
    IInferenceClient inference,
    IOwnerAccessor owner,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider time,
    ILogger<ReconstructionService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);

    public async Task<ReconstructionDto> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(sessionId, cancellationToken);
        var job = await SyncWithJobAsync(session, cancellationToken);
        var (plan, engine, reachable) = await PlanAsync(session, cancellationToken);
        return ToDto(session, job, plan, engine, reachable);
    }

    public async Task<ReconstructionDto> StartAsync(Guid sessionId, StartReconstructionRequest request, CancellationToken cancellationToken)
    {
        var route = request.Route!;
        if (route == ReconstructionRoutes.MultiView)
            throw new ApiException(StatusCodes.Status409Conflict, "route_unsupported", "Multi-view reconstruction is not supported by any installed engine.");
        if (!ReconstructionRoutes.Executable.Contains(route))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_route", $"Route must be one of: {string.Join(", ", ReconstructionRoutes.Executable)}.");

        var session = await LoadAsync(sessionId, cancellationToken);
        var running = await SyncWithJobAsync(session, cancellationToken);
        if (running is { IsTerminal: false })
            throw new ApiException(StatusCodes.Status409Conflict, "reconstruction_in_progress", "A generation for this search is still running. Wait for it or cancel it first.");

        var (plan, engine, reachable) = await PlanAsync(session, cancellationToken);
        var option = plan.Route(route);
        if (!option.IsAvailable)
        {
            var why = string.Join(" ", option.Reasons.Where(r => r.Severity == "blocker").Select(r => r.Message));
            throw new ApiException(StatusCodes.Status409Conflict, "route_unavailable", why.Length > 0 ? why : "This route is not available for the search.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        GenerationJob? job = null;
        switch (route)
        {
            case ReconstructionRoutes.ReuseExistingModel:
            {
                var model = ReusableModel(session, request.ModelId ?? option.ModelId);
                var bytes = await store.ReadAllAsync(session.Id, ReferenceArtifactNames.ModelFile(model.Id, model.Format!), cancellationToken)
                            ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The uploaded model file is no longer available.");
                var license = LicenseInfo.Normalize(model.LicenseRaw ?? model.License, model.LicenseUrl);
                var attribution = $"\"{model.Title}\" by {model.Author ?? "unknown author"}, {license.Label}" + (model.PageUrl is { } url ? $" ({url})" : "");
                var geometry = model.GeometryJson is null ? null : JsonSerializer.Deserialize<ModelGeometry>(model.GeometryJson, Json);
                var project = await designs.CreateFromModelAsync(
                    new PartDesignService.ReusedModel(bytes, model.Format!, model.Title, session.Id, model.Id, license.Code, attribution, geometry?.ScaleStatus),
                    request.Name ?? session.Name,
                    cancellationToken);
                Record(session, route, ReconstructionStates.Completed, now, designId: project.Id, modelId: model.Id);
                break;
            }

            case ReconstructionRoutes.ManualGeometry:
            {
                // The state stays manual_geometry_required: the project exists, the geometry still has to be built.
                var project = await designs.CreateBlankAsync(new CreateBlankPartDesignRequest { Name = request.Name ?? session.Name }, cancellationToken);
                Record(session, route, route, now, designId: project.Id);
                break;
            }

            default:
            {
                var input = await store.ReadAllAsync(session.Id, ReferenceArtifactNames.InputImage, cancellationToken)
                            ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The input photo is no longer available.");
                var extras = new List<(byte[], string)>();
                var used = new List<Guid>();
                if (route == ReconstructionRoutes.Hybrid)
                {
                    foreach (var candidate in ExtraViews(session).Take(uploadOptions.Value.MaxExtraImages))
                    {
                        var picture = candidate.HasFullImage
                            ? await store.ReadAllAsync(session.Id, ReferenceArtifactNames.FullImage(candidate.Id), cancellationToken)
                            : await store.ReadAllAsync(session.Id, ReferenceArtifactNames.Thumbnail(candidate.Id), cancellationToken);
                        if (picture is null)
                            continue;
                        // The engine finds the side itself; a suggested viewpoint is too uncertain to pass on as a fact.
                        extras.Add((picture, "auto"));
                        used.Add(candidate.Id);
                    }

                    if (extras.Count == 0)
                        throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "None of the accepted pictures is available any more.");
                }

                var created = await jobs.CreateFromImagesAsync(input, session.Name + ".png", extras, cancellationToken);
                job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == created.Id, cancellationToken);
                Record(session, route, route, now, jobId: created.Id, extraIds: used);
                break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reference session {SessionId}: started {Route}", session.Id, route);
        (plan, engine, reachable) = await PlanAsync(session, cancellationToken);
        return ToDto(session, job, plan, engine, reachable);
    }

    private static void Record(ReferenceSession s, string route, string state, DateTime now, Guid? jobId = null, Guid? designId = null, Guid? modelId = null, IReadOnlyList<Guid>? extraIds = null)
    {
        s.ReconstructionRoute = route;
        s.ReconstructionState = state;
        s.ReconstructionStartedAt = now;
        s.ReconstructionJobId = jobId;
        s.ReconstructionDesignId = designId;
        s.ReconstructionModelId = modelId;
        s.ReconstructionExtraIdsJson = extraIds is { Count: > 0 } ? JsonSerializer.Serialize(extraIds, Json) : null;
        s.ReconstructionMessage = null;
        s.UpdatedAt = now;
    }

    private static ModelCandidate ReusableModel(ReferenceSession session, Guid? modelId)
    {
        var model = session.Models.FirstOrDefault(m => m.Id == modelId)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Model candidate not found.");
        if (model.Decision != ReferenceDecisions.Accepted || model.Format is null || model.EvaluationStatus != ModelEvaluationStatuses.GeometryChecked)
            throw new ApiException(StatusCodes.Status409Conflict, "model_not_reusable", "Only an accepted, uploaded model whose geometry was checked can be reused.");
        return model;
    }

    /// <summary>Accepted pictures the user marked as the same object, usable as extra photos, best-ranked first.</summary>
    private static IEnumerable<ReferenceCandidate> ExtraViews(ReferenceSession session) =>
        session.Candidates
            .Where(c => c.Decision == ReferenceDecisions.Accepted && c.Relation == "same_object" && c.DuplicateOfId is null && !c.MatchesInput && (c.HasFullImage || c.HasThumbnail))
            .OrderBy(c => c.RankPosition ?? int.MaxValue)
            .ThenBy(c => c.SortOrder);

    /// <summary>
    /// Brings the stored state up to date with the session's generation job. A job deleted by
    /// retention leaves the last known state; one that vanished while running is reported as failed.
    /// </summary>
    private async Task<GenerationJob?> SyncWithJobAsync(ReferenceSession session, CancellationToken cancellationToken)
    {
        if (session.ReconstructionJobId is not { } jobId)
            return null;
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken);
        var running = session.ReconstructionState == session.ReconstructionRoute;
        (string State, string? Message) next = job switch
        {
            null when running => (ReconstructionStates.Failed, "The generation job no longer exists."),
            null => (session.ReconstructionState!, session.ReconstructionMessage),
            { Status: JobStatus.Succeeded } => (ReconstructionStates.Completed, null),
            { Status: JobStatus.Failed } => (ReconstructionStates.Failed, job.ErrorMessage),
            { Status: JobStatus.Cancelled } => (ReconstructionStates.Cancelled, job.ErrorMessage ?? "The generation was cancelled."),
            _ => (session.ReconstructionRoute!, null),
        };

        if (next.State != session.ReconstructionState || next.Message != session.ReconstructionMessage)
        {
            session.ReconstructionState = next.State;
            session.ReconstructionMessage = next.Message is null ? null : next.Message.Length > 500 ? next.Message[..500] : next.Message;
            await db.SaveChangesAsync(cancellationToken);
        }

        return job;
    }

    private async Task<(ReconstructionPlan Plan, string? Engine, bool Reachable)> PlanAsync(ReferenceSession session, CancellationToken cancellationToken)
    {
        InferenceHealthDto? health;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(HealthTimeout);
            try
            {
                health = await inference.GetHealthAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                health = null;
            }
        }

        var accepted = session.Models
            .Where(m => m.Decision == ReferenceDecisions.Accepted)
            .Select(m =>
            {
                var license = LicenseInfo.Normalize(m.LicenseRaw ?? m.License, m.LicenseUrl);
                var geometry = m.GeometryJson is null ? null : JsonSerializer.Deserialize<ModelGeometry>(m.GeometryJson, Json);
                return new ModelEvidence(
                    m.Id, m.Title, m.Format is not null, m.EvaluationStatus == ModelEvaluationStatuses.GeometryChecked,
                    license.IsKnown, license.Derivatives, geometry?.Closed, geometry?.ScaleStatus, m.Score);
            })
            .ToList();

        var evidence = new ReconstructionEvidence(
            session.HasInputImage,
            session.LastSearchedAt is not null || session.Models.Count > 0,
            session.Candidates.Count,
            session.Candidates.Count(c => c.Decision == ReferenceDecisions.Accepted),
            ExtraViews(session).Count(),
            session.Models.Count(m => m.Decision == ReferenceDecisions.Pending),
            accepted,
            health?.Engine,
            health is not null,
            uploadOptions.Value.MaxExtraImages);
        return (ReconstructionPlanner.Plan(evidence), health?.Engine, health is not null);
    }

    private static ReconstructionDto ToDto(ReferenceSession s, GenerationJob? job, ReconstructionPlan plan, string? engine, bool reachable)
    {
        static RouteReasonDto Reason(RouteReason r) => new(r.Code, r.Severity, r.Message);
        var state = s.ReconstructionState ?? StateBeforeDecision(s);
        return new ReconstructionDto(
            state,
            s.ReconstructionRoute,
            s.ReconstructionStartedAt is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null,
            s.ReconstructionJobId,
            job?.Status.ToString(),
            s.ReconstructionDesignId,
            s.ReconstructionModelId,
            s.ReconstructionExtraIdsJson is { } ids ? JsonSerializer.Deserialize<List<Guid>>(ids, Json) ?? [] : [],
            s.ReconstructionMessage,
            plan.Recommended,
            plan.Routes.Select(r => new RouteOptionDto(r.Route, r.Availability, r.Route == plan.Recommended, r.Reasons.Select(Reason).ToList(), r.ModelId, r.ExtraPhotos)).ToList(),
            plan.Notes.Select(Reason).ToList(),
            engine,
            reachable);
    }

    private static string StateBeforeDecision(ReferenceSession s) =>
        s.Models.Any(m => m.Decision == ReferenceDecisions.Pending) ? ReconstructionStates.ModelCandidateReview : ReconstructionStates.ReferenceSearch;

    private async Task<ReferenceSession> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ReferenceSessions
            .Include(s => s.Candidates)
            .Include(s => s.Models)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Reference search not found.");
}
