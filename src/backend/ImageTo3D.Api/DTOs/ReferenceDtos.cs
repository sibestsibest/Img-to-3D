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
    IReadOnlyList<IssueDto> Warnings);

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
    IReadOnlyList<ReferenceCandidateDto> Candidates);

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

public sealed record ClearReferenceCacheDto(int SearchEntries, int Files, long Bytes);
