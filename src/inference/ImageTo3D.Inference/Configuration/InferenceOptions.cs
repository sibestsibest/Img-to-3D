using System.ComponentModel.DataAnnotations;

namespace ImageTo3D.Inference.Configuration;

public sealed class InferenceOptions
{
    public const string SectionName = "Inference";

    /// <summary>Directory containing the ONNX models (see models/README.md). Relative paths resolve against the content root.</summary>
    [Required]
    public string ModelDirectory { get; set; } = "../../../models";

    [Required]
    public string TriplaneModel { get; set; } = "triposr/triplane.onnx";

    [Required]
    public string NerfModel { get; set; } = "triposr/nerf.onnx";

    [Required]
    public string BackgroundRemovalModel { get; set; } = "u2net/u2net.onnx";

    /// <summary>Scratch directory for per-job inputs and outputs.</summary>
    [Required]
    public string WorkDirectory { get; set; } = "data/inference";

    /// <summary>"Cpu" or "Cuda". CUDA requires building with -p:OrtFlavor=Cuda and a matching CUDA/cuDNN install.</summary>
    [RegularExpression("^(Cpu|Cuda)$")]
    public string ExecutionProvider { get; set; } = "Cpu";

    public int CudaDeviceId { get; set; }

    /// <summary>ONNX Runtime intra-op threads; 0 lets ORT choose.</summary>
    [Range(0, 256)]
    public int IntraOpThreads { get; set; }

    /// <summary>Marching cubes grid resolution (official default 256).</summary>
    [Range(32, 320)]
    public int MarchingCubesResolution { get; set; } = 256;

    /// <summary>Iso-level on the activated density (official TripoSR extract_mesh default 25.0).</summary>
    [Range(0.0, 1000.0)]
    public float DensityThreshold { get; set; } = 25f;

    /// <summary>Fraction of the image the foreground occupies after re-framing (official default 0.85).</summary>
    [Range(0.5, 1.0)]
    public float ForegroundRatio { get; set; } = 0.85f;

    /// <summary>Points per NeRF query; lower values reduce memory use.</summary>
    [Range(1024, 4_194_304)]
    public int ChunkSize { get; set; } = 262_144;

    /// <summary>Drop disconnected fragments and keep only the largest connected component.</summary>
    public bool KeepLargestComponentOnly { get; set; }

    [Range(1, 1000)]
    public int MaxQueueLength { get; set; } = 8;

    [Range(1, 50 * 1024 * 1024)]
    public long MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Finished job folders older than this are deleted.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Optional shared secret required in the X-Internal-Api-Key header. Required when not bound to loopback.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Load models in the background at startup (false = load on first use; used by tests).</summary>
    public bool LoadModelsOnStartup { get; set; } = true;
}
