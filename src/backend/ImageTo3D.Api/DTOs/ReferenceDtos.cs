using System.ComponentModel.DataAnnotations;

namespace ImageTo3D.Api.DTOs;

/// <summary>A source the server can search: all are free and need no account.</summary>
public sealed record ReferenceSourceDto(string Id, string Name, string Description, bool Enabled, string Access = "free_no_account");

/// <summary>
/// Licence of a reference picture. Permissions are null when the licence is unknown, in which
/// case the picture is for personal reference only.
/// </summary>
public sealed record ReferenceLicenseDto(string Code, string Label, string? Url, string? Raw, bool? CommercialUse, bool? Derivatives, bool? AttributionRequired);

public sealed record ReferenceCandidateDto(
    Guid Id,
    string Origin,
    string Source,
    string SourceName,
    string Title,
    string? PageUrl,
    string? ImageUrl,
    string? Author,
    ReferenceLicenseDto License,
    string Attribution,
    string? Query,
    DateTime RetrievedAt,
    string DownloadStatus,
    string? DownloadError,
    bool HasThumbnail,
    int? ThumbnailWidth,
    int? ThumbnailHeight,
    int? Width,
    int? Height,
    Guid? DuplicateOfId,
    string? DuplicateKind,
    bool MatchesInput,
    string Decision,
    string ImageKind,
    string Relation,
    string? Note,
    IReadOnlyList<IssueDto> Warnings,
    CandidateRankingDto Ranking);

/// <summary>
/// Outcome of one source in a search: <c>ok</c>, <c>empty</c>, <c>failed</c>, <c>timeout</c>,
/// <c>rate_limited</c> or <c>disabled</c>. A failed source never fails the search.
/// </summary>
public sealed record SourceSearchResultDto(string Source, string Name, string Status, int Hits, int Added, bool FromCache, string? Message);

public sealed record ManualSearchLinkDto(string Name, string Url, string Kind);

public sealed record ReferenceSessionDto(
    Guid Id,
    string Name,
    string Query,
    Guid? SourceJobId,
    bool HasInputImage,
    int? InputWidth,
    int? InputHeight,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSearchedAt,
    IReadOnlyList<SourceSearchResultDto> LastSearch,
    IReadOnlyList<ManualSearchLinkDto> ManualLinks,
    IReadOnlyList<ReferenceCandidateDto> Candidates,
    SessionRankingDto Ranking,
    IReadOnlyList<SourceSearchResultDto> LastModelSearch,
    IReadOnlyList<ModelCandidateDto> Models);

public sealed record ReferenceSessionSummaryDto(
    Guid Id,
    string Name,
    string Query,
    bool HasInputImage,
    int CandidateCount,
    int AcceptedCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class SearchReferencesRequest
{
    /// <summary>Replaces the session's query when given.</summary>
    [StringLength(200)]
    public string? Query { get; set; }

    /// <summary>Source IDs to search; all enabled sources when omitted.</summary>
    [MaxLength(10)]
    public List<string>? Sources { get; set; }
}

public sealed class UpdateReferenceSessionRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(200)]
    public string? Query { get; set; }
}

public sealed class UpdateCandidateRequest
{
    /// <summary>pending, accepted or rejected.</summary>
    [Required]
    public string? Decision { get; set; }

    /// <summary>unknown, photo, render or drawing.</summary>
    public string? ImageKind { get; set; }

    /// <summary>unknown, same_object, variant or similar_different.</summary>
    public string? Relation { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed record ClearReferenceCacheDto(int SearchEntries, int Files, long Bytes, int Embeddings);

/// <summary>Advisory label with its probability (zero-shot) or capped confidence; "uncertain" when no label is clearly ahead.</summary>
public sealed record ZeroShotDto(string Label, double Confidence);

/// <summary>One contribution to a score: <c>effect</c> is "+", "-" or "0" (informational).</summary>
public sealed record RankReasonDto(string Code, string Message, string Effect);

/// <summary>
/// Ranking of one picture. All values are advisory: <c>score</c> mixes the available signals,
/// <c>confidence</c> says how much evidence it rests on, and the suggestions never assert identity.
/// </summary>
public sealed record CandidateRankingDto(
    int? Position,
    double? Score,
    double? Confidence,
    int? Group,
    double? QuerySimilarity,
    double? InputSimilarity,
    ZeroShotDto? Viewpoint,
    ZeroShotDto? Kind,
    ZeroShotDto? Relation,
    IReadOnlyList<RankReasonDto> Reasons);

/// <summary><c>mode</c>: "embeddings" or "basic" (no visual signals; <c>note</c> says why). Null before the first ranking.</summary>
public sealed record SessionRankingDto(string? Mode, DateTime? RankedAt, string? Note, ZeroShotDto? InputViewpoint, string EmbeddingModel);

public sealed record ModelGeometryDto(
    int Triangles,
    int Components,
    bool Closed,
    int BoundaryEdges,
    int NonManifoldEdges,
    int DegenerateTriangles,
    double SizeXMm,
    double SizeYMm,
    double SizeZMm,
    string ScaleStatus);

/// <summary>
/// An existing 3D model. <c>evaluationStatus</c> is <c>metadata_only</c> (listing and preview
/// only; the mesh was not checked) or <c>geometry_checked</c> (uploaded file read and analysed).
/// </summary>
public sealed record ModelCandidateDto(
    Guid Id,
    string Origin,
    string Source,
    string SourceName,
    string Title,
    string? PageUrl,
    string? Author,
    ReferenceLicenseDto License,
    string Attribution,
    string? Query,
    DateTime RetrievedAt,
    bool? IsDownloadable,
    int? FaceCount,
    int? VertexCount,
    long? FileBytes,
    bool HasThumbnail,
    string DownloadStatus,
    string? Format,
    string EvaluationStatus,
    double? Score,
    double? Confidence,
    double? VisualSimilarity,
    IReadOnlyList<RankReasonDto> Reasons,
    ModelGeometryDto? Geometry,
    string Decision,
    string? Note,
    IReadOnlyList<IssueDto> Warnings);

public sealed class UpdateModelRequest
{
    /// <summary>pending, accepted or rejected.</summary>
    [Required]
    public string? Decision { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed record EmbeddingStatusDto(bool Available, string Model, string? Reason);

/// <summary>What the server can do for reference search right now.</summary>
public sealed record ReferenceCapabilitiesDto(EmbeddingStatusDto Embeddings, IReadOnlyList<ReferenceSourceDto> ImageSources, IReadOnlyList<ReferenceSourceDto> ModelSources);
