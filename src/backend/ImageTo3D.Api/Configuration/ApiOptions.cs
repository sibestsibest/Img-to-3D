using System.ComponentModel.DataAnnotations;

namespace ImageTo3D.Api.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root for job artifacts. Must be outside any publicly served directory.</summary>
    [Required]
    public string RootPath { get; set; } = "../../../data/storage";

    [Required]
    public string DatabasePath { get; set; } = "../../../data/imageto3d.db";

    /// <summary>Root for part-design projects. Kept apart from job artifacts so job retention never touches it.</summary>
    [Required]
    public string DesignRootPath { get; set; } = "../../../data/designs";

    /// <summary>Root for reference-search sessions (input photo, thumbnails); never touched by job retention.</summary>
    [Required]
    public string ReferenceRootPath { get; set; } = "../../../data/references";

    /// <summary>Disposable downloads shared by all sessions; can be cleared at any time without losing session data.</summary>
    [Required]
    public string ReferenceCachePath { get; set; } = "../../../data/reference-cache";

    /// <summary>Jobs (rows and files) older than this are deleted.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Artifact folders without a database row are deleted after this grace period.</summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromHours(1);
}

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(1024, 50L * 1024 * 1024)]
    public long MaxBytes { get; set; } = 20L * 1024 * 1024;

    [Range(16, 4096)]
    public int MinDimension { get; set; } = 64;

    [Range(256, 16384)]
    public int MaxDimension { get; set; } = 8192;

    [Range(1, 1_000_000_000)]
    public long MaxPixels { get; set; } = 40_000_000;

    /// <summary>Maximum unfinished (Pending/Processing) jobs per owner.</summary>
    [Range(1, 100)]
    public int MaxActiveJobsPerOwner { get; set; } = 3;

    [Range(1, 1000)]
    public int UploadsPerMinute { get; set; } = 10;

    /// <summary>Extra photos of other sides accepted with one job (0 disables multi-photo jobs).</summary>
    [Range(0, Services.Storage.ArtifactNames.MaxExtraImages)]
    public int MaxExtraImages { get; set; } = Services.Storage.ArtifactNames.MaxExtraImages;
}

public sealed class InferenceClientOptions
{
    public const string SectionName = "InferenceService";

    [Required]
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:5090");

    public string? ApiKey { get; set; }

    /// <summary>Timeout for a single HTTP call to the inference service (not the whole generation).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Overall limit from submission to a final inference status.</summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(40);

    /// <summary>Consecutive failed status polls tolerated before the job is failed.</summary>
    [Range(1, 1000)]
    public int MaxConsecutivePollFailures { get; set; } = 20;
}

public sealed class PartDesignOptions
{
    public const string SectionName = "PartDesign";

    /// <summary>Source meshes above this size are refused (analysis runs inside the request).</summary>
    [Range(1, 20_000_000)]
    public int MaxSourceTriangles { get; set; } = 2_000_000;

    /// <summary>Most parts created by the analysis; smaller pieces beyond it are grouped as fragments.</summary>
    [Range(1, 500)]
    public int MaxAnalysedParts { get; set; } = 64;

    /// <summary>Connected pieces with fewer triangles than this share of the source are grouped as fragments.</summary>
    [Range(0.0, 0.5)]
    public double FragmentTriangleFraction { get; set; } = 0.002;

    [Range(1, 10_000)]
    public int MaxPartsPerProject { get; set; } = 500;

    [Range(1, 1000)]
    public int MaxProjectsPerOwner { get; set; } = 100;

    /// <summary>Stored releases per project; each keeps its own zip, so this bounds disk use.</summary>
    [Range(1, 1000)]
    public int MaxReleasesPerProject { get; set; } = 50;

    /// <summary>Relations per project, including removed ones kept for undo.</summary>
    [Range(1, 10_000)]
    public int MaxRelationsPerProject { get; set; } = 1000;

    /// <summary>Surfaces closer than this count as touching; a penetration shallower than it is not a collision.</summary>
    [Range(0.0001, 1.0)]
    public double ContactToleranceMm { get; set; } = 0.05;

    /// <summary>Triangle-pair tests per assembly check; beyond it the check stops and says it is incomplete.</summary>
    [Range(1000, 1_000_000_000)]
    public long MaxCollisionTriangleTests { get; set; } = 50_000_000;
}

/// <summary>
/// Reference image search. Every source is free and needs no account or key; each can be turned
/// off. The application works with all of them off or unreachable (manual search links remain).
/// </summary>
public sealed class ReferenceSearchOptions
{
    public const string SectionName = "ReferenceSearch";

    public bool WikimediaEnabled { get; set; } = true;

    public bool OpenverseEnabled { get; set; } = true;

    public bool InternetArchiveEnabled { get; set; } = true;

    [Required]
    public Uri WikimediaApiUrl { get; set; } = new("https://commons.wikimedia.org/w/api.php");

    [Required]
    public Uri OpenverseApiUrl { get; set; } = new("https://api.openverse.org/v1/images/");

    [Required]
    public Uri InternetArchiveApiUrl { get; set; } = new("https://archive.org/advancedsearch.php");

    /// <summary>
    /// Sent with every request. Wikimedia's policy asks for a descriptive agent with contact
    /// details; add yours (e.g. "ImageTo3D/0.4 (you@example.com)") when searching a lot.
    /// </summary>
    [Required]
    [MinLength(8)]
    public string UserAgent { get; set; } = "ImageTo3D/0.4 (local reference search; .NET HttpClient)";

    /// <summary>Limit per call to one source, retries included.</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>Extra attempts after a transient failure (network error, 5xx). 429 is never retried.</summary>
    [Range(0, 3)]
    public int MaxRetries { get; set; } = 1;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Results requested from each source (Openverse allows at most 20 without an account).</summary>
    [Range(1, 50)]
    public int ResultsPerSource { get; set; } = 20;

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);

    [Range(1, 2000)]
    public int MaxCandidatesPerSession { get; set; } = 300;

    [Range(1, 1000)]
    public int MaxSessionsPerOwner { get; set; } = 100;

    [Range(1, 1000)]
    public int SearchesPerMinute { get; set; } = 10;

    /// <summary>Remote thumbnails larger than this are not downloaded.</summary>
    [Range(1024, 20L * 1024 * 1024)]
    public long ThumbnailMaxBytes { get; set; } = 4L * 1024 * 1024;

    [Range(64, 2048)]
    public int ThumbnailMaxSide { get; set; } = 640;

    public TimeSpan ThumbnailTimeout { get; set; } = TimeSpan.FromSeconds(10);

    [Range(1, 16)]
    public int DownloadConcurrency { get; set; } = 6;

    /// <summary>dHash bit difference (of 64) at or below which two pictures count as near-duplicates.</summary>
    [Range(0, 20)]
    public int NearDuplicateDistance { get; set; } = 6;
}

/// <summary>
/// Optional local CLIP image/text embeddings for ranking references and model candidates. When
/// the model files are missing or this is turned off, ranking falls back to non-visual signals.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    public bool Enabled { get; set; } = true;

    /// <summary>Folder with the CLIP files (see models/README.md); resolved against the API content root.</summary>
    [Required]
    public string ModelDirectory { get; set; } = "../../../models/clip";

    [Required]
    public string VisionModel { get; set; } = "vision_model_quantized.onnx";

    [Required]
    public string TextModel { get; set; } = "text_model_quantized.onnx";

    [Required]
    public string Vocabulary { get; set; } = "vocab.json";

    [Required]
    public string Merges { get; set; } = "merges.txt";

    /// <summary>CPU threads per inference call; small so ranking never starves the web server.</summary>
    [Range(1, 64)]
    public int IntraOpThreads { get; set; } = 2;

    /// <summary>Mean cosine similarity at or above which pictures are grouped together (variant groups).</summary>
    [Range(0.5, 0.99)]
    public double GroupThreshold { get; set; } = 0.86;
}

/// <summary>Search for existing 3D models. Sketchfab search is anonymous; downloads (which need a token) are not used.</summary>
public sealed class ModelSearchOptions
{
    public const string SectionName = "ModelSearch";

    public bool SketchfabEnabled { get; set; } = true;

    [Required]
    public Uri SketchfabApiUrl { get; set; } = new("https://api.sketchfab.com/v3/search");

    [Range(1, 24)]
    public int ResultsPerSearch { get; set; } = 24;

    [Range(1, 500)]
    public int MaxModelsPerSession { get; set; } = 100;

    /// <summary>Largest GLB/STL accepted for evaluation.</summary>
    [Range(1024, 512L * 1024 * 1024)]
    public long MaxUploadBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Uploaded models above this are refused (evaluation runs inside the request).</summary>
    [Range(1, 20_000_000)]
    public int MaxTriangles { get; set; } = 2_000_000;
}

public sealed class ModelOutputOptions
{
    public const string SectionName = "ModelOutput";

    /// <summary>Initial export size: the longest bounding-box side is scaled to this many millimetres.</summary>
    [Range(1.0, 10_000.0)]
    public double DefaultLongestSideMm { get; set; } = 100;
}
