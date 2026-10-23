using System.ComponentModel.DataAnnotations;

namespace ImageTo3D.Api.DTOs;

/// <summary>
/// The fit of a relation. <c>Status</c>: <c>ok</c>, <c>too_tight</c>, <c>too_loose</c>, <c>unknown</c>
/// (a size is missing) or <c>not_applicable</c> (glued, or nested parts that are not round).
/// <c>ClearanceMm</c> is opening minus inserted size; negative means interference. <c>Basis</c> is
/// <c>specified</c> when every size was entered, <c>estimated</c> when one was measured on the model.
/// </summary>
public sealed record FitDto(string Status, double? OpeningMm, double? InsertedMm, double? ClearanceMm, string? Basis);

/// <summary>
/// How two parts are joined. <c>Kind</c>: <c>glued</c>, <c>peg_in_hole</c> (A is the peg),
/// <c>tube_over_rod</c> (A is the tube), <c>nested</c> (B sits inside A) or <c>slot</c> (B is the sheet).
/// </summary>
public sealed record PartRelationDto(
    Guid Id,
    Guid PartAId,
    Guid PartBId,
    string Kind,
    bool AllowOverlap,
    double? OpeningSizeMm,
    string? Note,
    FitDto Fit,
    ValidationDto Validation,
    string Units = "mm");

public sealed class CreateRelationRequest
{
    [Required]
    public Guid? PartAId { get; set; }

    [Required]
    public Guid? PartBId { get; set; }

    [Required]
    [RegularExpression("^(glued|peg_in_hole|tube_over_rod|nested|slot)$", ErrorMessage = "Kind must be 'glued', 'peg_in_hole', 'tube_over_rod', 'nested' or 'slot'.")]
    public string? Kind { get; set; }

    public bool AllowOverlap { get; set; }

    /// <summary>Hole diameter (peg in hole) or slot width (slot), in millimetres.</summary>
    [Range(0.01, 1000.0)]
    public double? OpeningSizeMm { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

/// <summary>Full replacement of a relation's editable fields; the two parts cannot change.</summary>
public sealed class UpdateRelationRequest
{
    [Required]
    [RegularExpression("^(glued|peg_in_hole|tube_over_rod|nested|slot)$", ErrorMessage = "Kind must be 'glued', 'peg_in_hole', 'tube_over_rod', 'nested' or 'slot'.")]
    public string? Kind { get; set; }

    public bool AllowOverlap { get; set; }

    [Range(0.01, 1000.0)]
    public double? OpeningSizeMm { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

/// <summary>
/// A pair of parts whose geometry meets. <c>Contact</c>: <c>intersecting</c> (surfaces cross),
/// <c>contained</c> (one solid wholly inside the other, see <c>ContainedPartId</c>), <c>coincident</c>
/// (the same geometry at the same place, e.g. an unmoved duplicate), <c>touching</c> or, for a glued
/// relation whose parts do not meet, <c>gap</c>. <c>Severity</c> is <c>info</c> when a relation
/// explains it (overlap allowed, nested, glued faces touching).
/// </summary>
public sealed record CollisionDto(
    Guid PartAId,
    Guid PartBId,
    string Contact,
    Guid? ContainedPartId,
    string Code,
    string Severity,
    string Message,
    Guid? RelationId,
    double IntersectionLengthMm,
    int CrossingTriangles,
    BoundsDto? OverlapBounds,
    bool Complete);

/// <summary>
/// Geometry quality of one part. <c>SolidExportReady</c>: closed, manifold, consistently oriented and
/// without degenerate triangles, as solid formats (STL for printing) need. It says nothing about
/// whether the part is the right size.
/// </summary>
public sealed record PartQualityDto(
    Guid PartId,
    string Name,
    string Status,
    bool Closed,
    int BoundaryEdges,
    int NonManifoldEdges,
    int InconsistentEdges,
    int DegenerateTriangles,
    int Components,
    int Triangles,
    double? VolumeMm3,
    bool SolidExportReady,
    int CollisionCount,
    IReadOnlyList<string> IssueCodes);

public sealed record QualityReportDto(
    string Status,
    int PartCount,
    int ValidParts,
    int WarningParts,
    int InvalidParts,
    int SolidExportReadyParts,
    int TotalTriangles,
    double? TotalVolumeMm3,
    IReadOnlyList<PartQualityDto> Parts);

/// <summary>
/// Assembly validation of a project, computed on request from the part geometry: relation and fit
/// checks, collisions between every pair of active parts, and geometry quality. <c>Status</c> is the
/// worst of all (<c>valid</c>, <c>warning</c>, <c>invalid</c>). <c>Complete</c> is false when the
/// collision check stopped at its triangle-test budget.
/// </summary>
public sealed record AssemblyReportDto(
    string Status,
    DateTime CheckedAt,
    int HistorySequence,
    double ContactToleranceMm,
    int PairsChecked,
    long TriangleTests,
    bool Complete,
    IReadOnlyList<PartRelationDto> Relations,
    IReadOnlyList<CollisionDto> Collisions,
    QualityReportDto Quality,
    IReadOnlyList<IssueDto> Issues,
    string Units = "mm",
    string CoordinateSystem = "");

public sealed record ReleasePartRefDto(Guid Id, string Name, string Category, int Quantity);

/// <summary>A part in both releases, with what differs: field names such as <c>name</c>, <c>quantity</c>, <c>geometry</c>, <c>spec.thicknessMm</c>.</summary>
public sealed record ChangedPartDto(Guid Id, string Name, IReadOnlyList<string> Changes);

/// <summary>
/// What changed between two stored releases, read from their manifests. Spec and relation changes
/// can only be compared when both packages carry them (<c>SpecCompared</c>, <c>RelationsCompared</c>).
/// </summary>
public sealed record ReleaseDiffDto(
    int FromRevision,
    int ToRevision,
    IReadOnlyList<ReleasePartRefDto> Added,
    IReadOnlyList<ReleasePartRefDto> Removed,
    IReadOnlyList<ChangedPartDto> Changed,
    int UnchangedCount,
    bool SpecCompared,
    bool RelationsCompared,
    int RelationsAdded,
    int RelationsRemoved,
    int RelationsChanged,
    int WarningsBefore,
    int WarningsAfter,
    IReadOnlyList<string> Notes);
