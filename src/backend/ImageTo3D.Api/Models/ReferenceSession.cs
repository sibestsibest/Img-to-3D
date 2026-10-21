namespace ImageTo3D.Api.Models;

/// <summary>
/// A reference search for one object: an optional input photo, the query, and the reference
/// images found or added by hand. Kept apart from generation jobs (no foreign key, own storage
/// root), so it can run before any generation and outlives job retention.
/// </summary>
public sealed class ReferenceSession
{
    public Guid Id { get; set; }

    /// <summary>Same owner scheme as generation jobs; sessions are only visible to their owner.</summary>
    public required string OwnerKeyHash { get; set; }

    public required string Name { get; set; }

    /// <summary>Search text entered by the user (there is no automatic image analysis yet).</summary>
    public string Query { get; set; } = "";

    /// <summary>Generation job the input photo was copied from (provenance only; the job may since have been deleted).</summary>
    public Guid? SourceJobId { get; set; }

    public bool HasInputImage { get; set; }

    public int? InputWidth { get; set; }

    public int? InputHeight { get; set; }

    /// <summary>Difference hash of the input photo, to flag search results that are the input itself.</summary>
    public long? InputDHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastSearchedAt { get; set; }

    /// <summary>Outcome of the last search per source (JSON array), shown so failures are visible.</summary>
    public string LastSearchJson { get; set; } = "[]";

    public List<ReferenceCandidate> Candidates { get; set; } = [];
}

public static class ReferenceOrigins
{
    public const string Search = "search";
    public const string Manual = "manual";
}

public static class ReferenceDecisions
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pending, Accepted, Rejected };
}

public static class DownloadStatuses
{
    public const string Downloaded = "downloaded";
    public const string Failed = "failed";

    /// <summary>No thumbnail offered by the source, or its address is not one the adapter trusts.</summary>
    public const string Skipped = "skipped";
}

/// <summary>What the picture is, as judged by the user. Automatic classification comes with embeddings (phase 5).</summary>
public static class ReferenceImageKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "unknown", "photo", "render", "drawing" };
}

/// <summary>How the picture relates to the object being modelled. A similar look is not proof of the same object.</summary>
public static class ReferenceRelations
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "unknown", "same_object", "variant", "similar_different" };
}

public sealed class ReferenceCandidate
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public required string Origin { get; set; }

    /// <summary>Adapter ID (wikimedia, openverse, internet_archive) or "upload".</summary>
    public required string Source { get; set; }

    /// <summary>The item's ID at the source; unique per session and source so repeated searches add no copies.</summary>
    public required string SourceItemId { get; set; }

    public required string Title { get; set; }

    /// <summary>Web page describing the item (not an image).</summary>
    public string? PageUrl { get; set; }

    /// <summary>Direct address of the full image, when the source gives one.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Remote thumbnail address the preview was downloaded from.</summary>
    public string? ThumbnailUrl { get; set; }

    public string? Author { get; set; }

    /// <summary>Normalised licence code (see <c>LicenseInfo</c>); "unknown" when missing or unrecognised.</summary>
    public string License { get; set; } = "unknown";

    /// <summary>Licence as the source stated it, kept for traceability.</summary>
    public string? LicenseRaw { get; set; }

    public string? LicenseUrl { get; set; }

    /// <summary>Size of the full image as reported by the source.</summary>
    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Search text that found it (null for manual additions).</summary>
    public string? Query { get; set; }

    public DateTime RetrievedAt { get; set; }

    public string DownloadStatus { get; set; } = DownloadStatuses.Skipped;

    public string? DownloadError { get; set; }

    public bool HasThumbnail { get; set; }

    public int? ThumbnailWidth { get; set; }

    public int? ThumbnailHeight { get; set; }

    /// <summary>SHA-256 of the downloaded (or uploaded) bytes: equal values are the same file.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Difference hash of the picture (64 bits stored as a signed integer).</summary>
    public long? DHash { get; set; }

    /// <summary>Earlier candidate in this session showing the same picture; recomputed after every change.</summary>
    public Guid? DuplicateOfId { get; set; }

    /// <summary>"exact" (same file) or "near" (same picture, different encoding or size).</summary>
    public string? DuplicateKind { get; set; }

    /// <summary>The picture looks like the session's input photo itself (dHash within the near-duplicate distance).</summary>
    public bool MatchesInput { get; set; }

    public string Decision { get; set; } = ReferenceDecisions.Pending;

    public string ImageKind { get; set; } = "unknown";

    public string Relation { get; set; } = "unknown";

    public string? Note { get; set; }

    /// <summary>Order of discovery within the session (manual additions included).</summary>
    public int SortOrder { get; set; }

    /// <summary>Upload only: an upright full-size PNG is kept next to the thumbnail.</summary>
    public bool HasFullImage { get; set; }
}

/// <summary>
/// Disposable cache of raw search responses, keyed by source and normalised query. Shared by all
/// owners (it holds public search results only) and safe to clear at any time.
/// </summary>
public sealed class ReferenceSearchCacheEntry
{
    /// <summary>SHA-256 (hex) of source, normalised query and result limit.</summary>
    public required string Key { get; set; }

    public required string Source { get; set; }

    public required string Query { get; set; }

    /// <summary>JSON array of the parsed hits.</summary>
    public required string HitsJson { get; set; }

    public DateTime FetchedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
