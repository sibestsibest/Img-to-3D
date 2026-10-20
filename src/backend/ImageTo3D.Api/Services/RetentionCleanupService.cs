using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services;

/// <summary>
/// Periodically deletes finished jobs older than the retention period (rows and files) and
/// artifact folders that have no database row (e.g. an upload interrupted mid-way).
/// Unfinished jobs are never deleted by retention.
/// </summary>
public sealed class RetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    TimeProvider time,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, time);
        do
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<(int Jobs, int Orphans)> CleanupOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var now = time.GetUtcNow().UtcDateTime;
        var o = options.Value;

        var cutoff = now - o.RetentionPeriod;
        var expired = await db.Jobs
            .Where(j => j.CreatedAt < cutoff && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))
            .ToListAsync(cancellationToken);
        foreach (var job in expired)
        {
            await store.DeleteJobAsync(job.Id, cancellationToken);
            db.Jobs.Remove(job);
        }

        await db.SaveChangesAsync(cancellationToken);

        var orphans = 0;
        var known = (await db.Jobs.Select(j => j.Id).ToListAsync(cancellationToken)).ToHashSet();
        await foreach (var (jobId, modified) in store.ListJobsAsync(cancellationToken))
        {
            if (!known.Contains(jobId) && now - modified > o.OrphanGracePeriod)
            {
                await store.DeleteJobAsync(jobId, cancellationToken);
                orphans++;
            }
        }

        if (expired.Count > 0 || orphans > 0)
            logger.LogInformation("Retention cleanup removed {Jobs} expired jobs and {Orphans} orphaned folders", expired.Count, orphans);
        return (expired.Count, orphans);
    }
}
