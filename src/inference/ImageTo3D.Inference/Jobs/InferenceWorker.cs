using System.Threading.Channels;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Pipeline;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace ImageTo3D.Inference.Jobs;

/// <summary>Bounded FIFO of job ids. Capacity limits memory held by queued images.</summary>
public sealed class InferenceQueue(IOptions<InferenceOptions> options)
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.MaxQueueLength)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    public bool TryEnqueue(Guid id) => _channel.Writer.TryWrite(id);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Single consumer: exactly one generation runs at a time, which bounds CPU/GPU memory use
/// (the encoder alone holds ~3.3 GB of fp32 weights plus activations).
/// </summary>
public sealed class InferenceWorker(
    InferenceQueue queue,
    InferenceJobStore store,
    GenerationPipeline pipeline,
    IOptions<InferenceOptions> options,
    ILogger<InferenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            var job = store.Get(id);
            if (job is null || !job.TryStart())
                continue;

            using var scope = logger.BeginScope(new Dictionary<string, object> { ["InferenceJobId"] = id });
            await RunJobAsync(job, stoppingToken);

            // Return the large density-grid arrays to the OS between jobs.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private async Task RunJobAsync(InferenceJob job, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(options.Value.JobTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, timeout.Token, stoppingToken);
        var progress = new SynchronousProgress<StageProgress>(job.ReportProgress);

        try
        {
            logger.LogInformation("Inference job started");
            var result = await pipeline.RunAsync(job.TakeRequest(), progress, linked.Token);

            Directory.CreateDirectory(job.Directory);
            await File.WriteAllBytesAsync(Path.Combine(job.Directory, InferenceArtifactNames.Mesh), result.Glb, CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(job.Directory, InferenceArtifactNames.PreprocessedImage), result.PreprocessedPng, CancellationToken.None);

            job.Succeed(new InferenceMeshDto(result.Mesh.Vertices, result.Mesh.Triangles, result.Mesh.SizeX, result.Mesh.SizeY, result.Mesh.SizeZ));
            logger.LogInformation(
                "Inference job succeeded: {Triangles} triangles; stage seconds {Timings}",
                result.Mesh.Triangles,
                string.Join(", ", result.StageSeconds.Select(kv => $"{kv.Key}={kv.Value:F1}")));
        }
        catch (Exception ex) when (IsCancellation(ex, linked.Token))
        {
            if (timeout.IsCancellationRequested && !job.Cancellation.IsCancellationRequested)
            {
                job.Fail(InferenceErrorCodes.Timeout, $"Generation exceeded the {options.Value.JobTimeout.TotalMinutes:F0} minute limit.");
                logger.LogWarning("Inference job timed out");
            }
            else if (stoppingToken.IsCancellationRequested && !job.Cancellation.IsCancellationRequested)
            {
                job.Fail(InferenceErrorCodes.Internal, "The inference service shut down during generation.");
            }
            else
            {
                job.Fail(InferenceErrorCodes.Cancelled, "Generation was cancelled.");
                logger.LogInformation("Inference job cancelled");
            }
        }
        catch (GenerationException ex)
        {
            var code = ex.InnerException switch
            {
                PreprocessingException => InferenceErrorCodes.NoForeground,
                Core.Imaging.ImageValidationException => InferenceErrorCodes.InvalidImage,
                _ when ex.Message.Contains("did not produce a surface", StringComparison.Ordinal) => InferenceErrorCodes.NoSurface,
                _ => InferenceErrorCodes.Internal,
            };
            job.Fail(code, ex.Message);
            logger.LogWarning("Inference job failed: {Code}", code);
        }
        catch (Exception ex) when (IsOutOfMemory(ex))
        {
            job.Fail(InferenceErrorCodes.OutOfMemory, "Not enough memory to run the model. Try a lower mesh resolution, or run on a machine with more RAM/VRAM.");
            logger.LogError(ex, "Inference job ran out of memory");
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex.InnerException is FileNotFoundException)
        {
            job.Fail(InferenceErrorCodes.ModelUnavailable, "The 3D model files are not installed on the inference server.");
            logger.LogError(ex, "Model files missing");
        }
        catch (Exception ex)
        {
            job.Fail(InferenceErrorCodes.Internal, "Generation failed due to an internal error.");
            logger.LogError(ex, "Inference job failed");
        }
    }

    private static bool IsCancellation(Exception ex, CancellationToken token) =>
        token.IsCancellationRequested && (ex is OperationCanceledException || ex is OnnxRuntimeException);

    private static bool IsOutOfMemory(Exception ex) =>
        ex is OutOfMemoryException
        || (ex is OnnxRuntimeException ort && (ort.Message.Contains("allocate", StringComparison.OrdinalIgnoreCase)
                                               || ort.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)));

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>Deletes finished job folders after the retention period, plus orphaned folders from previous runs.</summary>
public sealed class RetentionCleanupService(
    InferenceJobStore store,
    IOptions<InferenceOptions> options,
    IHostEnvironment environment,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        do
        {
            try
            {
                CleanupOnce(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retention cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public int CleanupOnce(DateTimeOffset now)
    {
        var o = options.Value;
        var removed = 0;
        foreach (var job in store.All)
        {
            if (job.IsTerminal && job.CompletedAt is { } done && now - done > o.RetentionPeriod)
            {
                store.Remove(job.Id, out _);
                TryDelete(job.Directory);
                removed++;
            }
        }

        var root = Path.GetFullPath(o.WorkDirectory, environment.ContentRootPath);
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Guid.TryParse(Path.GetFileName(dir), out var id) && store.Get(id) is null
                    && now - Directory.GetLastWriteTimeUtc(dir) > o.RetentionPeriod)
                {
                    TryDelete(dir);
                    removed++;
                }
            }
        }

        if (removed > 0)
            logger.LogInformation("Retention cleanup removed {Count} job folders", removed);
        return removed;
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete job folder");
        }
    }
}
