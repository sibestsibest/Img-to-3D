using System.ComponentModel.DataAnnotations;

namespace ImageTo3D.Api.DTOs;

/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>blocker</c> (the route cannot start because of it).</param>
public sealed record RouteReasonDto(string Code, string Severity, string Message);

/// <summary>
/// One way to get geometry. <c>Availability</c>: <c>available</c>, <c>unavailable</c> (not with
/// what the search has now; see the blockers) or <c>unsupported</c> (no installed engine can do it).
/// </summary>
public sealed record RouteOptionDto(
    string Route,
    string Availability,
    bool Recommended,
    IReadOnlyList<RouteReasonDto> Reasons,
    Guid? ModelId,
    int ExtraPhotos);

/// <summary>
/// Reconstruction decision of a reference search. <c>State</c> is <c>reference_search</c> or
/// <c>model_candidate_review</c> before anything starts; while a generation runs it is the route
/// (<c>single_image_reconstruction</c>, <c>hybrid_reconstruction</c>); after starting manual
/// geometry it is <c>manual_geometry_required</c>; otherwise <c>completed</c>, <c>failed</c> or
/// <c>cancelled</c>. Routes are listed in preference order.
/// </summary>
public sealed record ReconstructionDto(
    string State,
    string? Route,
    DateTime? StartedAt,
    Guid? JobId,
    string? JobStatus,
    Guid? DesignId,
    Guid? ModelId,
    IReadOnlyList<Guid> ExtraReferenceIds,
    string? Message,
    string Recommended,
    IReadOnlyList<RouteOptionDto> Routes,
    IReadOnlyList<RouteReasonDto> Notes,
    string? Engine,
    bool EngineReachable);

public sealed class StartReconstructionRequest
{
    /// <summary><c>reuse_existing_model</c>, <c>hybrid_reconstruction</c>, <c>single_image_reconstruction</c> or <c>manual_geometry_required</c>.</summary>
    [Required]
    [StringLength(32)]
    public string? Route { get; set; }

    /// <summary>Reuse only: which accepted model (default: the one the plan names).</summary>
    public Guid? ModelId { get; set; }

    /// <summary>Name of the part design created by reuse or manual geometry (default: the search name).</summary>
    [StringLength(100)]
    public string? Name { get; set; }
}
