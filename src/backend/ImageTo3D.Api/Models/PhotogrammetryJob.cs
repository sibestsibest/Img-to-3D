namespace ImageTo3D.Api.Models;

public static class PhotogrammetryStatuses
{
    /// <summary>Collecting photos; nothing runs yet.</summary>
    public const string Draft = "draft";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled;

    public static bool IsActive(string status) => status is Queued or Running;
}

public static class PhotogrammetryErrorCodes
{
    public const string MeshroomUnavailable = "meshroom_unavailable";
    public const string MeshroomFailed = "meshroom_failed";
    public const string Timeout = "timeout";
    public const string NoOutputMesh = "no_output_mesh";
    public const string InvalidOutputMesh = "invalid_output_mesh";
    public const string Interrupted = "interrupted";
    public const string Internal = "internal_error";
}

/// <summary>Scale of a photogrammetry result: Meshroom's units are arbitrary until one dimension is entered.</summary>
public static class PhotogrammetryScaleStatuses
{
    /// <summary>Longest side set to the default size; not the real size.</summary>
    public const string Uncalibrated = "uncalibrated";

    /// <summary>One dimension was entered from a measurement of the real object.</summary>
    public const string Calibrated = "calibrated";
}

/// <summary>
/// A multi-photo reconstruction with Meshroom: a photo set (draft), then one background run.
/// Kept apart from generation jobs (different inputs, hours of runtime) and never removed by job
/// retention; the work folder Meshroom uses is deleted after the run.
/// </summary>
public sealed class PhotogrammetryJob
{
    public Guid Id { get; set; }

    public required string OwnerKeyHash { get; set; }

    public required string Name { get; set; }

    public string Status { get; set; } = PhotogrammetryStatuses.Draft;

    /// <summary>Meshroom pipeline template the run uses (e.g. photogrammetry, photogrammetryDraft).</summary>
    public string? Pipeline { get; set; }

    /// <summary>Meshroom node currently computing (from its cache status files), e.g. DepthMap.</summary>
    public string? Stage { get; set; }

    /// <summary>Meshroom nodes finished so far (the total is not known in advance).</summary>
    public int? NodesDone { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public bool CancelRequested { get; set; }

    public int? ExitCode { get; set; }

    public string? EngineVersion { get; set; }

    /// <summary>Reference search the photos came from (multi-view route), if any.</summary>
    public Guid? SourceReferenceSessionId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int? VertexCount { get; set; }

    public int? TriangleCount { get; set; }

    /// <summary>Bounding box of the mesh in Meshroom's own (arbitrary) units.</summary>
    public double? ModelSizeX { get; set; }

    public double? ModelSizeY { get; set; }

    public double? ModelSizeZ { get; set; }

    /// <summary>Mesh checks as JSON (closed, boundary and non-manifold edges, components, ...).</summary>
    public string? InspectionJson { get; set; }

    /// <summary>Files Meshroom produced: JSON list of { path, bytes } relative to its output folder.</summary>
    public string OutputsJson { get; set; } = "[]";

    public string ScaleStatus { get; set; } = PhotogrammetryScaleStatuses.Uncalibrated;

    public double? MmPerModelUnit { get; set; }

    public string? ScaleAxis { get; set; }

    public double? ScaleTargetMm { get; set; }

    public bool HasStl { get; set; }

    public string? StlError { get; set; }

    public List<PhotogrammetryPhoto> Photos { get; set; } = [];
}

/// <summary>One photo of a set, stored byte for byte (EXIF kept), with the facts used for warnings.</summary>
public sealed class PhotogrammetryPhoto
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Stored file name (generated; the original name is display only).</summary>
    public required string FileName { get; set; }

    public required string OriginalName { get; set; }

    public required string Format { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long SizeBytes { get; set; }

    public required string Sha256 { get; set; }

    /// <summary>64-bit difference hash (stored as a signed value).</summary>
    public long DHash { get; set; }

    public double Sharpness { get; set; }

    public bool HasExif { get; set; }

    public DateTime CreatedAt { get; set; }
}
