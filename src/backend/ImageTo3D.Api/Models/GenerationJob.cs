using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Models;

public enum JobStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed class GenerationJob
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the anonymous owner cookie; jobs are only visible to their owner.</summary>
    public required string OwnerKeyHash { get; set; }

    /// <summary>Client-supplied Idempotency-Key, unique per owner, to absorb accidental double submits.</summary>
    public string? IdempotencyKey { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public string? Stage { get; set; }

    public double? StageProgress { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public bool CancelRequested { get; set; }

    /// <summary>Sanitised display name only; never used to build file paths.</summary>
    public required string OriginalFileName { get; set; }

    public required string OriginalFormat { get; set; }

    public long OriginalSizeBytes { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public Guid? InferenceJobId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int? VertexCount { get; set; }

    public int? TriangleCount { get; set; }

    /// <summary>Bounding box of the raw mesh in TripoSR model units (Y-up).</summary>
    public double? ModelSizeX { get; set; }

    public double? ModelSizeY { get; set; }

    public double? ModelSizeZ { get; set; }

    /// <summary>Millimetres per model unit applied to the exported artifacts.</summary>
    public double? MmPerModelUnit { get; set; }

    public ScaleAxis? ScaleAxis { get; set; }

    public double? ScaleTargetMm { get; set; }

    public bool HasGlb { get; set; }

    public bool HasStl { get; set; }

    public string? StlError { get; set; }

    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    public Vector3D? SizeMm => ModelSizeX is { } x && ModelSizeY is { } y && ModelSizeZ is { } z && MmPerModelUnit is { } f
        ? new Vector3D(x * f, y * f, z * f)
        : null;
}
