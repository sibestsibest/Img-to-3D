using System.Collections.Concurrent;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Core.Embeddings;
using ImageTo3D.Core.Imaging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageTo3D.Api.Services.Embeddings;

/// <summary>Whether visual embeddings can be used, and why not when they cannot.</summary>
public sealed record EmbedderStatus(bool Available, string Model, string? Reason);

/// <summary>
/// Joint image/text embedding space (L2-normalised vectors: cosine = dot product). Optional: callers
/// must check <see cref="EnsureReadyAsync"/> and fall back when it returns false.
/// </summary>
public interface IImageEmbedder
{
    /// <summary>Identifies model and weights; part of every cache key so a model change never reuses old vectors.</summary>
    string ModelId { get; }

    EmbedderStatus Status { get; }

    /// <summary>Loads the model on first use. False (with <see cref="Status"/> explaining) when unavailable.</summary>
    Task<bool> EnsureReadyAsync(CancellationToken cancellationToken);

    float[] EmbedImage(RgbaImage image, CancellationToken cancellationToken);

    float[] EmbedText(string text, CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI CLIP ViT-B/32 (MIT), the Transformers.js int8 export (Xenova/clip-vit-base-patch32,
/// revision d15189d), run on the CPU with ONNX Runtime inside the API, so it works with every
/// generation engine. Loaded lazily; text embeddings of prompts are memoised.
/// </summary>
public sealed class ClipEmbedder(IOptions<EmbeddingOptions> options, IHostEnvironment environment, ILogger<ClipEmbedder> logger) : IImageEmbedder, IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, float[]> _textCache = new();
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private ClipTokenizer? _tokenizer;
    private bool _attempted;

    public string ModelId => "clip-vit-b32-q8@d15189d";

    public EmbedderStatus Status { get; private set; } = new(false, "CLIP ViT-B/32 (int8)", "Not loaded yet.");

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_attempted)
            return Status.Available;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_attempted)
                return Status.Available;
            _attempted = true;

            var o = options.Value;
            if (!o.Enabled)
            {
                Status = Status with { Reason = "Turned off in the server configuration (Embeddings:Enabled)." };
                return false;
            }

            var dir = Path.GetFullPath(o.ModelDirectory, environment.ContentRootPath);
            var files = new[] { o.VisionModel, o.TextModel, o.Vocabulary, o.Merges };
            var missing = files.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
            if (missing.Count > 0)
            {
                // No paths in the reason: it is shown to users.
                Status = Status with { Reason = $"CLIP model files are not installed ({string.Join(", ", missing)}). Run scripts/download-models.ps1 -Embeddings." };
                logger.LogInformation("Embeddings unavailable: missing {Files} in {Directory}", missing, dir);
                return false;
            }

            var started = DateTime.UtcNow;
            await Task.Run(
                () =>
                {
                    var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        IntraOpNumThreads = o.IntraOpThreads,
                    };
                    _vision = new InferenceSession(Path.Combine(dir, o.VisionModel), sessionOptions);
                    _text = new InferenceSession(Path.Combine(dir, o.TextModel), sessionOptions);
                    _tokenizer = ClipTokenizer.Load(Path.Combine(dir, o.Vocabulary), Path.Combine(dir, o.Merges));
                    Expect(_vision, "pixel_values", "image_embeds");
                    Expect(_text, "input_ids", "text_embeds");
                },
                cancellationToken);
            Status = Status with { Available = true, Reason = null };
            logger.LogInformation("CLIP embeddings loaded in {Seconds:F1}s", (DateTime.UtcNow - started).TotalSeconds);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the CLIP embedding model");
            Status = Status with { Available = false, Reason = "The CLIP model could not be loaded (see the server log)." };
            DisposeSessions();
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public float[] EmbedImage(RgbaImage image, CancellationToken cancellationToken)
    {
        var session = _vision ?? throw new InvalidOperationException("Embeddings are not available.");
        var input = new DenseTensor<float>(ClipPreprocessor.ToTensor(image), [1, 3, ClipPreprocessor.Size, ClipPreprocessor.Size]);
        return Run(session, NamedOnnxValue.CreateFromTensor("pixel_values", input), "image_embeds", cancellationToken);
    }

    public float[] EmbedText(string text, CancellationToken cancellationToken) => _textCache.GetOrAdd(text, t =>
    {
        var session = _text ?? throw new InvalidOperationException("Embeddings are not available.");
        var ids = _tokenizer!.Encode(t);
        var input = new DenseTensor<long>(ids, [1, ids.Length]);
        if (_textCache.Count > 2000)
            _textCache.Clear(); // prompts repeat; queries are few: a crude bound is enough
        return Run(session, NamedOnnxValue.CreateFromTensor("input_ids", input), "text_embeds", cancellationToken);
    });

    private static float[] Run(InferenceSession session, NamedOnnxValue input, string output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        using var results = session.Run([input], [output], runOptions);
        return EmbeddingMath.Normalize(results[0].AsTensor<float>().ToArray());
    }

    private static void Expect(InferenceSession session, string input, string output)
    {
        if (!session.InputMetadata.ContainsKey(input) || !session.OutputMetadata.ContainsKey(output))
            throw new InvalidDataException($"Unexpected CLIP model interface (needs {input} -> {output}).");
    }

    private void DisposeSessions()
    {
        _vision?.Dispose();
        _text?.Dispose();
        _vision = null;
        _text = null;
    }

    public void Dispose()
    {
        DisposeSessions();
        _loadLock.Dispose();
    }
}
