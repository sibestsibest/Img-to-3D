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

    public DateTime? RankedAt { get; set; }

    /// <summary>"embeddings" (visual and query similarity used) or "basic" (only non-visual signals).</summary>
    public string? RankingMode { get; set; }

    /// <summary>Why ranking is basic or partial (e.g. model not installed); null when nothing is missing.</summary>
    public string? RankingNote { get; set; }

    /// <summary>Suggested viewpoint of the input photo (so references showing other sides can be preferred).</summary>
    public string? InputViewpoint { get; set; }

    public double? InputViewpointConfidence { get; set; }

    /// <summary>Outcome of the last 3D model search per source (JSON array).</summary>
    public string LastModelSearchJson { get; set; } = "[]";

    // ---- Reconstruction decision (see ReconstructionPlanner); null until a route is started ----

    /// <summary>Route the user started (reuse_existing_model, hybrid_reconstruction, ...).</summary>
    public string? ReconstructionRoute { get; set; }

    /// <summary>Last known state; for generation routes it follows the job and is kept when the job is deleted by retention.</summary>
    public string? ReconstructionState { get; set; }

    public DateTime? ReconstructionStartedAt { get; set; }

    /// <summary>Generation job started for this session (no foreign key: jobs are deleted by retention).</summary>
    public Guid? ReconstructionJobId { get; set; }

    /// <summary>Part design created from an accepted model, or the empty one for manual geometry.</summary>
    public Guid? ReconstructionDesignId { get; set; }

    /// <summary>Model candidate that was reused.</summary>
    public Guid? ReconstructionModelId { get; set; }

    /// <summary>Reference candidates sent as extra photos (JSON array of IDs).</summary>
    public string? ReconstructionExtraIdsJson { get; set; }

    /// <summary>Why the generation failed or was cancelled, copied from the job.</summary>
    public string? ReconstructionMessage { get; set; }

    public List<ReferenceCandidate> Candidates { get; set; } = [];

    public List<ModelCandidate> Models { get; set; } = [];
}

/// <summary>Suggested viewpoints (zero-shot, advisory). "uncertain" when no class is clearly ahead.</summary>
public static class Viewpoints
{
    public const string Uncertain = "uncertain";

    public static readonly IReadOnlyList<string> All = ["front", "back", "side", "top", "three_quarter"];
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

    // ---- Ranking (recomputed as a whole; advisory, never a claim of identity) ----

    /// <summary>1 = best. Null until ranked.</summary>
    public int? RankPosition { get; set; }

    /// <summary>0..1 weighted mix of the available signals (see <c>ReferenceRanker</c>).</summary>
    public double? RankScore { get; set; }

    /// <summary>0..1: how much evidence the score rests on (lower without embeddings or an input photo).</summary>
    public double? RankConfidence { get; set; }

    /// <summary>JSON array of { code, message, effect } explaining the score.</summary>
    public string? RankReasonsJson { get; set; }

    /// <summary>Visual group (pictures whose embeddings are close: the same or closely related object); 1-based.</summary>
    public int? GroupIndex { get; set; }

    /// <summary>CLIP cosine between the query and the picture.</summary>
    public double? QuerySimilarity { get; set; }

    /// <summary>CLIP cosine between the input photo and the picture.</summary>
    public double? InputSimilarity { get; set; }

    public string? SuggestedViewpoint { get; set; }

    public double? ViewpointConfidence { get; set; }

    public string? SuggestedKind { get; set; }

    public double? KindConfidence { get; set; }

    public string? SuggestedRelation { get; set; }

    public double? RelationConfidence { get; set; }
}

public static class ModelEvaluationStatuses
{
    /// <summary>Judged from the listing only (thumbnail, licence, face count); the mesh itself was not checked.</summary>
    public const string MetadataOnly = "metadata_only";

    /// <summary>The uploaded mesh was read and checked.</summary>
    public const string GeometryChecked = "geometry_checked";
}

/// <summary>
/// An existing 3D model that might be reused instead of generating one: a search result (listing
/// only; nothing is downloaded) or a GLB/STL the user uploaded (checked geometrically).
/// </summary>
public sealed class ModelCandidate
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public required string Origin { get; set; }

    /// <summary>"sketchfab" or "upload".</summary>
    public required string Source { get; set; }

    /// <summary>Sketchfab UID, or SHA-256 of the uploaded file.</summary>
    public required string SourceItemId { get; set; }

    public required string Title { get; set; }

    public string? PageUrl { get; set; }

    public string? Author { get; set; }

    public string License { get; set; } = "unknown";

    public string? LicenseRaw { get; set; }

    public string? LicenseUrl { get; set; }

    public string? Query { get; set; }

    public DateTime RetrievedAt { get; set; }

    /// <summary>Whether the source offers a download (search results only).</summary>
    public bool? IsDownloadable { get; set; }

    public int? FaceCount { get; set; }

    public int? VertexCount { get; set; }

    /// <summary>Size of the GLB download as listed, or of the uploaded file.</summary>
    public long? FileBytes { get; set; }

    public string? ThumbnailUrl { get; set; }

    public bool HasThumbnail { get; set; }

    public string DownloadStatus { get; set; } = DownloadStatuses.Skipped;

    public string? DownloadError { get; set; }

    /// <summary>Upload only: "glb" or "stl"; the file is kept in the session folder.</summary>
    public string? Format { get; set; }

    public string EvaluationStatus { get; set; } = ModelEvaluationStatuses.MetadataOnly;

    public double? Score { get; set; }

    public double? Confidence { get; set; }

    /// <summary>JSON array of { code, message, effect }.</summary>
    public string ReasonsJson { get; set; } = "[]";

    /// <summary>Upload only: JSON geometry report (triangles, components, closed, size, scale status).</summary>
    public string? GeometryJson { get; set; }

    /// <summary>Best CLIP cosine of the preview against the input photo and accepted references.</summary>
    public double? VisualSimilarity { get; set; }

    public string Decision { get; set; } = ReferenceDecisions.Pending;

    public string? Note { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>Disposable cache of image embeddings keyed by model and SHA-256 of the exact image bytes embedded.</summary>
public sealed class ImageEmbeddingCacheEntry
{
    public required string Model { get; set; }

    public required string ImageSha256 { get; set; }

    public required byte[] Vector { get; set; }

    public DateTime CreatedAt { get; set; }
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
