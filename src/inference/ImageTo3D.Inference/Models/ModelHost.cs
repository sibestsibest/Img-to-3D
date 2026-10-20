using ImageTo3D.Inference.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Inference.Models;

public enum ModelState
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

/// <summary>Owns the single, process-wide set of ONNX sessions. Loaded once and shared by all jobs.</summary>
public sealed class ModelHost(IOptions<InferenceOptions> options, IHostEnvironment environment, ILogger<ModelHost> logger) : IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private TripoSrModel? _tripoSr;
    private BackgroundRemover? _backgroundRemover;

    public ModelState State { get; private set; } = ModelState.NotLoaded;

    /// <summary>Safe, user-presentable reason for a load failure (no paths).</summary>
    public string? FailureReason { get; private set; }

    public TripoSrModel TripoSr => _tripoSr ?? throw new InvalidOperationException("Models are not loaded.");

    public BackgroundRemover BackgroundRemover => _backgroundRemover ?? throw new InvalidOperationException("Models are not loaded.");

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (State == ModelState.Ready)
            return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (State == ModelState.Ready)
                return;

            State = ModelState.Loading;
            FailureReason = null;
            var o = options.Value;
            var root = Path.GetFullPath(o.ModelDirectory, environment.ContentRootPath);
            var started = DateTime.UtcNow;
            logger.LogInformation("Loading ONNX models with {Provider} execution provider", o.ExecutionProvider);

            await Task.Run(
                () =>
                {
                    var triplane = OnnxSessionFactory.Create(Path.Combine(root, o.TriplaneModel), o, logger);
                    var nerf = OnnxSessionFactory.Create(Path.Combine(root, o.NerfModel), o, logger);
                    _tripoSr = new TripoSrModel(triplane, nerf);
                    _backgroundRemover = new BackgroundRemover(OnnxSessionFactory.Create(Path.Combine(root, o.BackgroundRemovalModel), o, logger));
                },
                cancellationToken);

            State = ModelState.Ready;
            logger.LogInformation("Models loaded in {Seconds:F1}s", (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = ModelState.Failed;
            FailureReason = ex switch
            {
                FileNotFoundException => "Model files are missing. Download them as described in models/README.md.",
                _ => "Model loading failed: " + ex.GetType().Name,
            };
            logger.LogError(ex, "Model loading failed");
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Dispose()
    {
        _tripoSr?.Dispose();
        _backgroundRemover?.Dispose();
        _loadLock.Dispose();
    }
}

public sealed class ModelLoaderService(ModelHost host, IOptions<InferenceOptions> options, ILogger<ModelLoaderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.LoadModelsOnStartup)
            return;
        try
        {
            await host.EnsureLoadedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already logged by ModelHost; /health reports the failure and jobs retry loading.
            logger.LogWarning("Startup model load failed; the service stays up and reports unhealthy");
        }
    }
}
