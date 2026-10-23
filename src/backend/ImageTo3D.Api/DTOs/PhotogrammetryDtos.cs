using System.ComponentModel.DataAnnotations;
using ImageTo3D.Api.Models;

namespace ImageTo3D.Api.DTOs;

/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>blocker</c> (the run cannot start).</param>
/// <param name="PhotoId">The photo the finding is about; null for the whole set.</param>
public sealed record PhotoFindingDto(string Code, string Severity, string Message, Guid? PhotoId);

public sealed record PhotogrammetryPhotoDto(Guid Id, int SortOrder, string OriginalName, string Format, int Width, int Height, long SizeBytes, double Sharpness, bool HasExif, string ThumbnailUrl);

/// <param name="NodesDone">Meshroom nodes finished; Meshroom does not announce the total, so none is shown.</param>
/// <param name="CurrentNode">Node computing now (e.g. FeatureExtraction, DepthMap, Meshing, Texturing).</param>
public sealed record PhotogrammetryProgressDto(int? NodesDone, string? CurrentNode, double? ElapsedSeconds);

public sealed record MeshInspectionDto(int Vertices, int Triangles, bool Closed, int BoundaryEdges, int NonManifoldEdges, int DegenerateTriangles, int Components);

/// <param name="Status"><c>uncalibrated</c> (longest side set to a default; not the real size) or <c>calibrated</c>.</param>
public sealed record PhotogrammetryScaleDto(string Status, string? Axis, double? TargetMm, double? MmPerModelUnit);

public sealed record PhotogrammetryDownloadsDto(bool Glb, bool Stl, bool Zip, bool Log, string? StlError);

public sealed record PhotogrammetryOutputDto(string Path, long Bytes);

/// <param name="Status"><c>draft</c>, <c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Origin">Always <c>photogrammetry</c>: reconstructed from the photos themselves.</param>
public sealed record PhotogrammetryJobDto(
    Guid Id,
    string Name,
    string Status,
    string? Pipeline,
    PhotogrammetryProgressDto Progress,
    string? ErrorCode,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? EngineVersion,
    Guid? SourceReferenceSessionId,
    IReadOnlyList<PhotogrammetryPhotoDto> Photos,
    IReadOnlyList<PhotoFindingDto> Findings,
    bool CanStart,
    MeshInspectionDto? Mesh,
    DimensionsDto? DimensionsMm,
    PhotogrammetryScaleDto Scale,
    PhotogrammetryDownloadsDto Downloads,
    IReadOnlyList<PhotogrammetryOutputDto> Outputs,
    string Origin = "photogrammetry",
    string Units = "mm");

public sealed record PhotogrammetrySummaryDto(Guid Id, string Name, string Status, int PhotoCount, string? Pipeline, DateTime CreatedAt, DateTime? CompletedAt, string Origin = "photogrammetry");

/// <param name="Status">Same values as the engine in <c>GET /api/capabilities</c>.</param>
public sealed record PhotogrammetryEngineDto(string Status, string? Reason, string? Version, IReadOnlyList<string> Pipelines, string? DefaultPipeline, bool Cuda, string? Gpu, IReadOnlyList<string> Notes, int MaxPhotos, long MaxPhotoBytes);

public sealed class CreatePhotogrammetryRequest
{
    [StringLength(100)]
    public string? Name { get; set; }
}

public sealed class StartPhotogrammetryRequest
{
    /// <summary>One of the engine's pipelines; empty = its default (photogrammetry with CUDA, else photogrammetryDraft).</summary>
    [StringLength(48)]
    public string? Pipeline { get; set; }
}

public sealed class PhotogrammetryScaleRequest
{
    /// <summary>X, Y, Z or Longest: the bounding-box side whose real length you measured.</summary>
    [Required]
    [RegularExpression("^(X|Y|Z|Longest)$")]
    public string? Axis { get; set; }

    [Required]
    [Range(0.1, 10000.0)]
    public double? TargetMm { get; set; }
}

public sealed class CreatePhotogrammetryDesignRequest
{
    [Required]
    public Guid? JobId { get; set; }

    [StringLength(100)]
    public string? Name { get; set; }
}
