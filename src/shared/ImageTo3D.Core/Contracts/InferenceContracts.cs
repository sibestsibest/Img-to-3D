using System.Text.Json.Serialization;

namespace ImageTo3D.Core.Contracts;

// Wire contract between the ASP.NET Core API and the inference service (/internal/*).

[JsonConverter(typeof(JsonStringEnumConverter<InferenceJobStatus>))]
public enum InferenceJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public static class InferenceErrorCodes
{
    public const string InvalidImage = "invalid_image";
    public const string NoForeground = "no_foreground";
    public const string NoSurface = "no_surface";
    public const string OutOfMemory = "out_of_memory";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string ModelUnavailable = "model_unavailable";
    public const string QueueFull = "queue_full";
    public const string Internal = "internal_error";
}

public static class InferenceArtifactNames
{
    public const string Mesh = "mesh.glb";
    public const string PreprocessedImage = "input.png";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Mesh, PreprocessedImage };
}

public sealed record InferenceArtifactDto(string Name, string ContentType, long SizeBytes);

/// <summary>Mesh statistics in TripoSR model units (Y-up).</summary>
public sealed record InferenceMeshDto(int Vertices, int Triangles, double SizeX, double SizeY, double SizeZ);

public sealed record InferenceJobDto(
    Guid Id,
    InferenceJobStatus Status,
    string Stage,
    double? StageProgress,
    string? ErrorCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<InferenceArtifactDto> Artifacts,
    InferenceMeshDto? Mesh);

/// <summary>Health of an inference engine. <see cref="Engine"/> names the model family (e.g. "TripoSR", "StableFast3D").</summary>
public sealed record InferenceHealthDto(string Status, string ModelState, string? Reason, string ExecutionProvider, int QueuedJobs, string? Engine = null);
