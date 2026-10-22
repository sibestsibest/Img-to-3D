namespace ImageTo3D.Api.Models;

/// <summary>
/// A part-design project derived from a generated model, from an existing model accepted in a
/// reference search, or started empty for hand-built geometry. The source mesh is copied into the
/// project's own storage when the project is created, so it survives job retention and is never
/// modified; parts are separate derived geometry.
/// </summary>
public sealed class PartDesignProject
{
    public Guid Id { get; set; }

    /// <summary>Same owner scheme as generation jobs; projects are only visible to their owner.</summary>
    public required string OwnerKeyHash { get; set; }

    public required string Name { get; set; }

    /// <summary>Where the source came from (see <see cref="DesignSourceKinds"/>).</summary>
    public string SourceKind { get; set; } = DesignSourceKinds.GenerationJob;

    /// <summary>Generation job the source was copied from (provenance only; the job may since have been deleted).</summary>
    public Guid? SourceJobId { get; set; }

    /// <summary>Reference session and model candidate of a reused model (provenance only).</summary>
    public Guid? SourceReferenceSessionId { get; set; }

    public Guid? SourceModelCandidateId { get; set; }

    /// <summary>Licence code and attribution of a reused model, kept with the project for the export package.</summary>
    public string? SourceLicense { get; set; }

    public string? SourceAttribution { get; set; }

    /// <summary>Empty for projects without a source.</summary>
    public required string SourceFileName { get; set; }

    /// <summary>
    /// Millimetres per source unit when the project was created; part geometry is in millimetres at
    /// this scale. Job: the job's scale. GLB: 1000 (metres). STL: 1 (millimetres assumed). Empty: 1.
    /// </summary>
    public double MmPerModelUnit { get; set; }

    public double FitAllowanceMinMm { get; set; } = -0.5;

    public double FitAllowanceMaxMm { get; set; } = 0.5;

    /// <summary>Project-level analysis findings (JSON array of issues).</summary>
    public string IssuesJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<DesignPart> Parts { get; set; } = [];

    public List<PartOperation> Operations { get; set; } = [];
}

public enum DesignPartState
{
    Active,

    /// <summary>Deleted by the user, or created by an operation that was undone. Restorable through history.</summary>
    Removed,

    /// <summary>Replaced by the results of a split; kept for provenance and undo.</summary>
    Superseded,
}

public static class DesignSourceKinds
{
    public const string GenerationJob = "generation_job";

    /// <summary>An uploaded GLB/STL accepted in a reference search.</summary>
    public const string ReferenceModel = "reference_model";

    /// <summary>No source mesh: parts are built from stock shapes.</summary>
    public const string None = "none";
}

public static class PartOrigins
{
    /// <summary>Built from a stock shape with exact sizes (plate, block, rod, tube).</summary>
    public const string Primitive = "primitive";

    public const string Node = "node";
    public const string Component = "component";
    public const string Fragments = "fragments";
    public const string Split = "split";
    public const string Duplicate = "duplicate";
}

public static class ReviewStatuses
{
    public const string NeedsReview = "needs_review";
    public const string UserConfirmed = "user_confirmed";
}

public sealed class DesignPart
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Part this one was derived from (split or duplicate); null for parts from the source analysis.</summary>
    public Guid? ParentPartId { get; set; }

    public required string Origin { get; set; }

    public string? SourceNodeName { get; set; }

    /// <summary>Artifact file holding the immutable part geometry (GLB, metres, Y-up).</summary>
    public required string GeometryFile { get; set; }

    /// <summary>Measured geometry facts (JSON), in millimetres.</summary>
    public required string GeometryReportJson { get; set; }

    public required string SuggestedCategory { get; set; }

    public double SuggestionConfidence { get; set; }

    public string SuggestionReasonsJson { get; set; } = "[]";

    public double EstimatedLengthMm { get; set; }

    public double EstimatedWidthMm { get; set; }

    public double EstimatedThicknessMm { get; set; }

    public double? EstimatedOuterDiameterMm { get; set; }

    public double? EstimatedInnerDiameterMm { get; set; }

    public DateTime CreatedAt { get; set; }

    // Editable state below; everything here is captured by PartSnapshot for undo/redo.

    public int SortOrder { get; set; }

    public required string Name { get; set; }

    public DesignPartState State { get; set; } = DesignPartState.Active;

    public required string Category { get; set; }

    public string ReviewStatus { get; set; } = ReviewStatuses.NeedsReview;

    public string? Material { get; set; }

    public string? ManufacturingMethod { get; set; }

    public int Quantity { get; set; } = 1;

    public double? ThicknessMm { get; set; }

    public double? DiameterMm { get; set; }

    public double? OuterDiameterMm { get; set; }

    public double? InnerDiameterMm { get; set; }

    public double? CutLengthMm { get; set; }

    public double? CutAngleDeg { get; set; }

    public double? FitAllowanceMm { get; set; }

    public double? ConfirmedLengthMm { get; set; }

    public double? ConfirmedWidthMm { get; set; }

    public double? ConfirmedHeightMm { get; set; }

    public string? StockDescription { get; set; }

    public string? StockSource { get; set; }

    public bool NeedsFurtherModelling { get; set; }

    public string? Notes { get; set; }
}

/// <summary>One entry of a project's editing history, reversible through the part snapshots it stores.</summary>
public sealed class PartOperation
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Strictly increasing per project.</summary>
    public int Sequence { get; set; }

    public required string Kind { get; set; }

    public required string Summary { get; set; }

    /// <summary>JSON list of { PartId, Before, After } snapshots; Before is null for parts the operation created.</summary>
    public required string ChangesJson { get; set; }

    public bool IsUndone { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// An immutable export package of a project (drawings, cut list, BOM, STL, manifest), numbered
/// per project. The zip is written once and never regenerated, so a release can always be
/// downloaded exactly as it was handed to the workshop, whatever was edited or undone since.
/// </summary>
public sealed class DesignRelease
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>1, 2, 3... per project; unique together with <see cref="ProjectId"/>.</summary>
    public int Revision { get; set; }

    public string? Note { get; set; }

    /// <summary>Artifact file in the project folder holding the zip.</summary>
    public required string PackageFile { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the zip.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Sequence of the last applied history operation when the release was made (0 if none).</summary>
    public int HistorySequence { get; set; }

    public int PartCount { get; set; }

    public int FileCount { get; set; }

    public int WarningCount { get; set; }

    public DateTime CreatedAt { get; set; }
}
