using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ImageTo3D.Api.Models;
using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.DTOs;

public sealed record DimensionsDto(double X, double Y, double Z);

public sealed record MeshInfoDto(int Vertices, int Triangles);

public sealed record ScaleInfoDto(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ScaleAxis>))] ScaleAxis Axis,
    double TargetMm,
    double MmPerModelUnit);

public sealed record DownloadsDto(bool Glb, bool Stl, string? StlError);

public sealed record JobDto(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))] JobStatus Status,
    string? Stage,
    double? StageProgress,
    string? ErrorCode,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string OriginalFileName,
    int ImageWidth,
    int ImageHeight,
    MeshInfoDto? Mesh,
    DimensionsDto? DimensionsMm,
    ScaleInfoDto? Scale,
    DownloadsDto Downloads,
    string? Engine = null,
    string Origin = GeometryOrigins.AiPredicted)
{
    public static JobDto From(GenerationJob job)
    {
        var size = job.SizeMm;
        return new JobDto(
            job.Id,
            job.Status,
            job.Stage,
            job.StageProgress,
            job.ErrorCode,
            job.ErrorMessage,
            DateTime.SpecifyKind(job.CreatedAt, DateTimeKind.Utc),
            job.StartedAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            job.CompletedAt is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null,
            job.OriginalFileName,
            job.ImageWidth,
            job.ImageHeight,
            job.VertexCount is { } v && job.TriangleCount is { } t ? new MeshInfoDto(v, t) : null,
            size is { } d ? new DimensionsDto(Math.Round(d.X, 3), Math.Round(d.Y, 3), Math.Round(d.Z, 3)) : null,
            job.ScaleAxis is { } axis && job.ScaleTargetMm is { } target && job.MmPerModelUnit is { } f ? new ScaleInfoDto(axis, target, f) : null,
            new DownloadsDto(job.Status == JobStatus.Succeeded && job.HasGlb, job.Status == JobStatus.Succeeded && job.HasStl, job.StlError),
            job.Engine);
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class ScaleRequest
{
    /// <summary>Which bounding-box side to set: X (width), Y (height), Z (depth) or Longest.</summary>
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter<ScaleAxis>))]
    public ScaleAxis? Axis { get; set; }

    /// <summary>Desired length of that side in millimetres.</summary>
    [Required]
    [Range(ScaleCalculator.MinTargetMm, ScaleCalculator.MaxTargetMm)]
    public double? TargetMm { get; set; }
}

public sealed class ListJobsQuery
{
    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}

public sealed record HealthDto(string Status, string Database, InferenceHealthSummaryDto Inference, string Version);

public sealed record InferenceHealthSummaryDto(string Status, string? ModelState, string? Engine = null);
