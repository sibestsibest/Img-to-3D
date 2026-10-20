using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services;

public static class JobErrorCodes
{
    public const string InferenceUnavailable = "inference_unavailable";
    public const string InferenceLost = "inference_lost";
    public const string InferenceRejected = "inference_rejected";
    public const string InvalidMesh = "invalid_mesh";
    public const string Timeout = "timeout";
    public const string Internal = "internal_error";
}

/// <summary>
/// Single background worker: submits pending jobs to the inference service one at a time,
/// polls their status, persists every transition, and post-processes the mesh into GLB/STL.
/// </summary>
public sealed class GenerationWorker(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    IInferenceClient inference,
    ArtifactProcessor processor,
    IOptions<InferenceClientOptions> inferenceOptions,
    IOptions<ModelOutputOptions> outputOptions,
    TimeProvider time,
    ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            using var _ = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = jobId });
            try
            {
                await ProcessAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure while processing job");
                await UpdateAsync(jobId, j => Fail(j, JobErrorCodes.Internal, "Generation failed due to an internal error."), CancellationToken.None);
            }
        }
    }

    /// <summary>Re-enqueues unfinished jobs after a restart. Processing jobs resume polling their inference job.</summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unfinished = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in unfinished)
            queue.Enqueue(id);
        if (unfinished.Count > 0)
            logger.LogInformation("Recovered {Count} unfinished jobs", unfinished.Count);
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.IsTerminal)
            return;

        if (job.CancelRequested)
        {
            MarkCancelled(job);
            await SaveAsync(db, job, stoppingToken);
            return;
        }

        if (job.InferenceJobId is null && !await SubmitAsync(db, store, job, stoppingToken))
            return;

        await PollAsync(db, store, job, stoppingToken);
    }

    private async Task<bool> SubmitAsync(AppDbContext db, IArtifactStore store, GenerationJob job, CancellationToken cancellationToken)
    {
        var png = await store.ReadAllAsync(job.Id, ArtifactNames.FileName(ArtifactKind.SourceImage), cancellationToken);
        if (png is null)
        {
            Fail(job, JobErrorCodes.Internal, "The uploaded image is no longer available.");
            await SaveAsync(db, job, cancellationToken);
            return false;
        }

        job.Status = JobStatus.Processing;
        job.StartedAt ??= time.GetUtcNow().UtcDateTime;
        job.Stage = "Submitting";
        await SaveAsync(db, job, cancellationToken);

        try
        {
            var submitted = await inference.SubmitAsync(png, job.Id.ToString("N"), cancellationToken);
            job.InferenceJobId = submitted.Id;
            job.Stage = submitted.Stage;
            await SaveAsync(db, job, cancellationToken);
            logger.LogInformation("Submitted to inference as {InferenceJobId}", submitted.Id);
            return true;
        }
        catch (InferenceUnavailableException ex)
        {
            Fail(job, JobErrorCodes.InferenceUnavailable, ex.Message);
        }
        catch (InferenceRequestException ex)
        {
            Fail(job, ex.Code ?? JobErrorCodes.InferenceRejected, ex.Message);
        }

        await SaveAsync(db, job, cancellationToken);
        return false;
    }

    private async Task PollAsync(AppDbContext db, IArtifactStore store, GenerationJob job, CancellationToken stoppingToken)
    {
        var o = inferenceOptions.Value;
        var inferenceId = job.InferenceJobId!.Value;
        var deadline = (job.StartedAt ?? time.GetUtcNow().UtcDateTime) + o.GenerationTimeout;
        var consecutiveFailures = 0;
        var cancelSent = false;

        while (true)
        {
            await Task.Delay(o.PollInterval, time, stoppingToken);

            // Reload to observe cancellation or deletion requested through the API.
            var entry = db.Entry(job);
            var exists = await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == job.Id, stoppingToken);
            if (!exists)
            {
                entry.State = EntityState.Detached;
                await BestEffortAsync(() => inference.CancelAsync(inferenceId, CancellationToken.None));
                await BestEffortAsync(() => inference.DeleteAsync(inferenceId, CancellationToken.None));
                return;
            }

            await entry.ReloadAsync(stoppingToken);
            if (job.CancelRequested && !cancelSent)
            {
                cancelSent = true;
                await BestEffortAsync(() => inference.CancelAsync(inferenceId, stoppingToken));
            }

            if (time.GetUtcNow().UtcDateTime > deadline)
            {
                await BestEffortAsync(() => inference.CancelAsync(inferenceId, CancellationToken.None));
                Fail(job, JobErrorCodes.Timeout, $"Generation did not finish within {o.GenerationTimeout.TotalMinutes:F0} minutes.");
                await SaveAsync(db, job, stoppingToken);
                return;
            }

            InferenceJobDto? status;
            try
            {
                status = await inference.GetJobAsync(inferenceId, stoppingToken);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is InferenceUnavailableException or InferenceRequestException)
            {
                if (++consecutiveFailures < o.MaxConsecutivePollFailures)
                    continue;
                Fail(job, JobErrorCodes.InferenceUnavailable, "Lost contact with the 3D generation service.");
                await SaveAsync(db, job, stoppingToken);
                return;
            }

            if (status is null)
            {
                Fail(job, JobErrorCodes.InferenceLost, "The generation service restarted and lost this job. Please submit the image again.");
                await SaveAsync(db, job, stoppingToken);
                return;
            }

            if (job.Stage != status.Stage || job.StageProgress != status.StageProgress)
            {
                job.Stage = status.Stage;
                job.StageProgress = status.StageProgress;
                await SaveAsync(db, job, stoppingToken);
            }

            switch (status.Status)
            {
                case InferenceJobStatus.Succeeded:
                    await CompleteAsync(db, store, job, status, stoppingToken);
                    return;
                case InferenceJobStatus.Failed:
                    Fail(job, status.ErrorCode ?? JobErrorCodes.Internal, status.Error ?? "Generation failed.");
                    await SaveAsync(db, job, stoppingToken);
                    await BestEffortAsync(() => inference.DeleteAsync(inferenceId, CancellationToken.None));
                    return;
                case InferenceJobStatus.Cancelled:
                    MarkCancelled(job);
                    await SaveAsync(db, job, stoppingToken);
                    await BestEffortAsync(() => inference.DeleteAsync(inferenceId, CancellationToken.None));
                    return;
            }
        }
    }

    private async Task CompleteAsync(AppDbContext db, IArtifactStore store, GenerationJob job, InferenceJobDto status, CancellationToken cancellationToken)
    {
        var inferenceId = status.Id;
        job.Stage = "Finalizing";
        job.StageProgress = null;
        await SaveAsync(db, job, cancellationToken);

        byte[] rawGlb;
        try
        {
            rawGlb = await inference.DownloadArtifactAsync(inferenceId, InferenceArtifactNames.Mesh, cancellationToken);
            await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.RawMesh), rawGlb, cancellationToken);
            var preprocessed = await inference.DownloadArtifactAsync(inferenceId, InferenceArtifactNames.PreprocessedImage, cancellationToken);
            await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.PreprocessedImage), preprocessed, cancellationToken);
        }
        catch (Exception ex) when (ex is InferenceUnavailableException or InferenceRequestException)
        {
            Fail(job, JobErrorCodes.InferenceUnavailable, "The generated model could not be retrieved from the generation service.");
            await SaveAsync(db, job, cancellationToken);
            return;
        }

        Core.Geometry.Mesh mesh;
        try
        {
            mesh = ArtifactProcessor.LoadRawMesh(rawGlb);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Generated mesh failed validation");
            Fail(job, JobErrorCodes.InvalidMesh, "The generated mesh is empty or invalid.");
            await SaveAsync(db, job, cancellationToken);
            return;
        }

        var modelSize = Core.Geometry.MeshOperations.ComputeBounds(mesh).Size;
        var mmPerUnit = outputOptions.Value.DefaultLongestSideMm / modelSize.MaxComponent;
        var artifacts = processor.Build(rawGlb, mesh, mmPerUnit);

        await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Glb), artifacts.Glb, cancellationToken);
        if (artifacts.Stl is not null)
            await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Stl), artifacts.Stl, cancellationToken);

        job.VertexCount = mesh.VertexCount;
        job.TriangleCount = mesh.TriangleCount;
        job.ModelSizeX = modelSize.X;
        job.ModelSizeY = modelSize.Y;
        job.ModelSizeZ = modelSize.Z;
        job.MmPerModelUnit = mmPerUnit;
        job.ScaleAxis = Core.Geometry.ScaleAxis.Longest;
        job.ScaleTargetMm = outputOptions.Value.DefaultLongestSideMm;
        job.HasGlb = true;
        job.HasStl = artifacts.Stl is not null;
        job.StlError = artifacts.StlError;
        job.Status = JobStatus.Succeeded;
        job.Stage = null;
        job.CompletedAt = time.GetUtcNow().UtcDateTime;
        await SaveAsync(db, job, cancellationToken);
        logger.LogInformation("Job succeeded with {Triangles} triangles", mesh.TriangleCount);

        await BestEffortAsync(() => inference.DeleteAsync(inferenceId, CancellationToken.None));
    }

    private async Task UpdateAsync(Guid jobId, Action<GenerationJob> update, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || job.IsTerminal)
            return;
        update(job);
        await SaveAsync(db, job, cancellationToken);
    }

    private void Fail(GenerationJob job, string code, string message)
    {
        job.Status = JobStatus.Failed;
        job.ErrorCode = code;
        job.ErrorMessage = message.Length > 500 ? message[..500] : message;
        job.StageProgress = null;
        job.CompletedAt = time.GetUtcNow().UtcDateTime;
        logger.LogWarning("Job failed: {ErrorCode}", code);
    }

    private void MarkCancelled(GenerationJob job)
    {
        job.Status = JobStatus.Cancelled;
        job.ErrorCode = InferenceErrorCodes.Cancelled;
        job.ErrorMessage = "Generation was cancelled.";
        job.StageProgress = null;
        job.CompletedAt = time.GetUtcNow().UtcDateTime;
    }

    private async Task SaveAsync(AppDbContext db, GenerationJob job, CancellationToken cancellationToken)
    {
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row was deleted through the API while we were working; drop our changes.
            db.Entry(job).State = EntityState.Detached;
        }
    }

    private async Task BestEffortAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InferenceUnavailableException or InferenceRequestException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Best-effort inference call failed");
        }
    }
}
