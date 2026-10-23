using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageTo3D.Api.Models;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.DTOs;

public sealed record IssueDto(string Code, string Severity, string Message, string Source)
{
    public static IssueDto From(GeometryIssue issue, string source) =>
        new(issue.Code, issue.Severity.ToString().ToLowerInvariant(), issue.Message, source);
}

/// <summary>Overall status: <c>valid</c>, <c>warning</c> or <c>invalid</c> (worst issue wins).</summary>
public sealed record ValidationDto(string Status, IReadOnlyList<IssueDto> Issues);

public sealed record Vector3Dto(double X, double Y, double Z);

public sealed record BoundsDto(Vector3Dto Min, Vector3Dto Max, Vector3Dto Size);

/// <summary>Measured geometry of a part in millimetres (design coordinates: Y-up, centred on X/Z, resting on Y = 0).</summary>
public sealed record GeometryReportDto(
    int Vertices,
    int Triangles,
    bool Closed,
    int BoundaryEdges,
    int NonManifoldEdges,
    int InconsistentEdges,
    int DegenerateTriangles,
    int Components,
    double SurfaceAreaMm2,
    double? VolumeMm3,
    BoundsDto Bounds,
    Vector3Dto OrientedExtentsMm,
    IReadOnlyList<IssueDto> Issues);

public sealed record SuggestionDto(string Category, double Confidence, IReadOnlyList<string> Reasons);

/// <summary>Dimensions measured on the model. Always <c>estimated</c>: the model's scale is itself an estimate.</summary>
public sealed record EstimatedDimensionsDto(double LengthMm, double WidthMm, double ThicknessMm, double? OuterDiameterMm, double? InnerDiameterMm, string Status = "estimated");

/// <summary>Dimensions the user measured on the physical part; <c>Status</c> is null until at least one is set.</summary>
public sealed record ConfirmedDimensionsDto(double? LengthMm, double? WidthMm, double? HeightMm, string? Status);

public sealed record ManufacturingSpecDto(
    string? Material,
    string? ManufacturingMethod,
    int Quantity,
    double? ThicknessMm,
    double? DiameterMm,
    double? OuterDiameterMm,
    double? InnerDiameterMm,
    double? CutLengthMm,
    double? CutAngleDeg,
    double? FitAllowanceMm,
    string? StockDescription,
    string? StockSource,
    bool NeedsFurtherModelling,
    string? Notes);

public sealed record DesignPartDto(
    Guid Id,
    Guid? ParentPartId,
    int SortOrder,
    string Name,
    string Origin,
    string? SourceNodeName,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DesignPartState>))] DesignPartState State,
    string Category,
    string ReviewStatus,
    SuggestionDto Suggestion,
    EstimatedDimensionsDto Estimated,
    ConfirmedDimensionsDto Confirmed,
    ManufacturingSpecDto Spec,
    GeometryReportDto Geometry,
    ValidationDto Validation,
    string Units = "mm");

public sealed record OperationDto(int Sequence, string Kind, string Summary, bool Undone, DateTime CreatedAt);

/// <summary>
/// Provenance of a project's source. <c>Kind</c>: <c>generation_job</c>, <c>reference_model</c>
/// (an accepted existing model, with its licence and attribution) or <c>none</c> (built by hand).
/// </summary>
public sealed record DesignSourceDto(
    string Kind,
    Guid? JobId,
    Guid? ReferenceSessionId,
    Guid? ModelCandidateId,
    string FileName,
    string? License,
    string? Attribution);

public sealed record PartDesignProjectDto(
    Guid Id,
    string Name,
    Guid? SourceJobId,
    string SourceFileName,
    DesignSourceDto Source,
    double MmPerModelUnit,
    string ScaleStatus,
    string CoordinateSystem,
    double FitAllowanceMinMm,
    double FitAllowanceMaxMm,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<DesignPartDto> Parts,
    IReadOnlyList<PartRelationDto> Relations,
    IReadOnlyList<OperationDto> History,
    bool CanUndo,
    bool CanRedo,
    string Units = "mm");

public sealed record PartDesignSummaryDto(Guid Id, string Name, Guid? SourceJobId, string SourceKind, string SourceFileName, int PartCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed class CreatePartDesignRequest
{
    [Required]
    public Guid? SourceJobId { get; set; }

    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }
}

/// <summary>An empty project, for parts built from stock shapes.</summary>
public sealed class CreateBlankPartDesignRequest
{
    [StringLength(100)]
    public string? Name { get; set; }
}

/// <summary>
/// A stock shape in design millimetres (Y-up). <c>Kind</c>: <c>plate</c> (length, width,
/// thickness; axis = thickness direction, default y), <c>box</c> (length X, height Y, width Z),
/// <c>rod</c> (diameter, length; axis = length direction, default x) or <c>tube</c> (outer and
/// inner diameter, length). <c>Center</c> is the centre of the shape, default the origin.
/// </summary>
public sealed class AddPrimitiveRequest
{
    [Required]
    [RegularExpression("^(plate|box|rod|tube)$", ErrorMessage = "Kind must be 'plate', 'box', 'rod' or 'tube'.")]
    public string? Kind { get; set; }

    [RegularExpression("^mm$", ErrorMessage = "Units must be 'mm'.")]
    public string Units { get; set; } = "mm";

    [StringLength(100)]
    public string? Name { get; set; }

    [StringLength(100)]
    public string? Material { get; set; }

    [Range(0.05, 5000.0)]
    public double? LengthMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? WidthMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? HeightMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? ThicknessMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? DiameterMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? OuterDiameterMm { get; set; }

    [Range(0.05, 5000.0)]
    public double? InnerDiameterMm { get; set; }

    [RegularExpression("^[xyz]$", ErrorMessage = "Axis must be 'x', 'y' or 'z'.")]
    public string? Axis { get; set; }

    [MinLength(3)]
    [MaxLength(3)]
    public double[]? Center { get; set; }
}

public sealed class UpdatePartDesignRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [Range(-10.0, 10.0)]
    public double FitAllowanceMinMm { get; set; } = -0.5;

    [Range(-10.0, 10.0)]
    public double FitAllowanceMaxMm { get; set; } = 0.5;
}

/// <summary>Full replacement of a part's editable fields. All lengths are millimetres.</summary>
public sealed class UpdatePartRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [Required]
    public string? Category { get; set; }

    /// <summary><c>needs_review</c> or <c>user_confirmed</c>.</summary>
    [Required]
    public string? ReviewStatus { get; set; }

    /// <summary>Only millimetres are supported; anything else is rejected rather than converted silently.</summary>
    [RegularExpression("^mm$", ErrorMessage = "Units must be 'mm'.")]
    public string Units { get; set; } = "mm";

    [StringLength(100)]
    public string? Material { get; set; }

    [StringLength(100)]
    public string? ManufacturingMethod { get; set; }

    [Range(1, 10_000)]
    public int Quantity { get; set; } = 1;

    [Range(0.01, 1000.0)]
    public double? ThicknessMm { get; set; }

    [Range(0.01, 1000.0)]
    public double? DiameterMm { get; set; }

    [Range(0.01, 1000.0)]
    public double? OuterDiameterMm { get; set; }

    [Range(0.0, 1000.0)]
    public double? InnerDiameterMm { get; set; }

    [Range(0.01, 10_000.0)]
    public double? CutLengthMm { get; set; }

    /// <summary>0 is a square cut; the angle is measured from square.</summary>
    [Range(-89.0, 89.0)]
    public double? CutAngleDeg { get; set; }

    [Range(-10.0, 10.0)]
    public double? FitAllowanceMm { get; set; }

    [Range(0.01, 10_000.0)]
    public double? ConfirmedLengthMm { get; set; }

    [Range(0.01, 10_000.0)]
    public double? ConfirmedWidthMm { get; set; }

    [Range(0.01, 10_000.0)]
    public double? ConfirmedHeightMm { get; set; }

    [StringLength(200)]
    public string? StockDescription { get; set; }

    [StringLength(200)]
    public string? StockSource { get; set; }

    public bool NeedsFurtherModelling { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>Cutting plane in design coordinates (millimetres, Y-up).</summary>
public sealed class SplitRequest
{
    [Required]
    [MinLength(3)]
    [MaxLength(3)]
    public double[]? Point { get; set; }

    [Required]
    [MinLength(3)]
    [MaxLength(3)]
    public double[]? Normal { get; set; }

    /// <summary>Apply the split even when the halves are not closed solids (they are then marked invalid).</summary>
    public bool AllowOpenResult { get; set; }
}

public sealed record DrawingHoleDto(string Label, double CentreXMm, double CentreYMm, double? DiameterMm, double WidthMm, double HeightMm);

/// <summary>What a fabrication drawing will contain, and every check run on it. Lengths in millimetres.</summary>
public sealed record DrawingReportDto(
    string Page,
    double PageWidthMm,
    double PageHeightMm,
    string Scale,
    double OutlineWidthMm,
    double OutlineHeightMm,
    double ThicknessMm,
    string ThicknessStatus,
    double MeasuredThicknessMm,
    int OutlineCount,
    IReadOnlyList<DrawingHoleDto> Holes,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<string> Formats,
    string Units = "mm");

public sealed record SplitSideDto(int Triangles, bool Capped, bool Closed, double CapAreaMm2, double? VolumeMm3, BoundsDto Bounds);

public sealed record SplitPreviewDto(
    bool CompleteSolidSplit,
    int ClosedContours,
    int OpenContours,
    double ContourLengthMm,
    SplitSideDto? Below,
    SplitSideDto? Above,
    IReadOnlyList<IssueDto> Issues);

/// <summary>One file inside an export package; <c>PartId</c> is null for project-level files (BOM, cut list, README).</summary>
public sealed record ManifestFileDto(string Path, string Kind, Guid? PartId, long Bytes, string Sha256);

/// <summary>
/// A part in a package. <c>Spec</c> and <c>GeometrySha256</c> (SHA-256 of its STL) let two releases be
/// compared field by field; packages made before they existed have them null.
/// </summary>
public sealed record ManifestPartDto(
    int Item,
    Guid Id,
    string Name,
    string Category,
    string ReviewStatus,
    string ValidationStatus,
    int Quantity,
    IReadOnlyList<string> Files,
    ManufacturingSpecDto? Spec = null,
    string? GeometrySha256 = null);

/// <summary>Something the workshop should know before using the package; <c>PartId</c> is null for project-level notes.</summary>
public sealed record ManifestWarningDto(string Code, string Severity, string Message, Guid? PartId);

public sealed record ManifestProjectDto(
    Guid Id,
    string Name,
    Guid? SourceJobId,
    string SourceFileName,
    DesignSourceDto Source,
    double MmPerModelUnit,
    string ScaleStatus,
    int HistorySequence,
    DateTime UpdatedAt);

/// <summary>
/// manifest.json of an export package. <c>Kind</c> is <c>draft</c> (made on demand, not stored) or
/// <c>release</c> (stored and numbered, see <c>Revision</c>). Lengths are millimetres.
/// </summary>
public sealed record ExportManifestDto(
    int SchemaVersion,
    string Generator,
    string Kind,
    int? Revision,
    string? Note,
    DateTime CreatedAt,
    string Units,
    string CoordinateSystem,
    ManifestProjectDto Project,
    IReadOnlyList<ManifestPartDto> Parts,
    IReadOnlyList<ManifestFileDto> Files,
    IReadOnlyList<ManifestWarningDto> Warnings,
    ManifestAssemblyDto? Assembly = null);

/// <summary>
/// Summary of <c>metadata/assembly.json</c> (relations, fits, collisions) and <c>metadata/quality.json</c>
/// (geometry quality per part) in the package; null in packages made before assembly checks existed.
/// </summary>
public sealed record ManifestAssemblyDto(
    string Status,
    int RelationCount,
    int CollisionCount,
    bool CollisionCheckComplete,
    string QualityStatus,
    int SolidExportReadyParts);

public sealed record DesignReleaseDto(
    Guid Id,
    int Revision,
    string? Note,
    long SizeBytes,
    string Sha256,
    int HistorySequence,
    int PartCount,
    int FileCount,
    int WarningCount,
    DateTime CreatedAt);

public sealed class CreateReleaseRequest
{
    [StringLength(500)]
    public string? Note { get; set; }
}
