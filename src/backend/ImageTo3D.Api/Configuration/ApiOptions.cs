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
}

public sealed class ModelOutputOptions
{
    public const string SectionName = "ModelOutput";

    /// <summary>Initial export size: the longest bounding-box side is scaled to this many millimetres.</summary>
    [Range(1.0, 10_000.0)]
    public double DefaultLongestSideMm { get; set; } = 100;
}
