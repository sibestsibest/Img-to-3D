using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Processes;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Photogrammetry;

/// <summary>
/// Runs queued photogrammetry jobs one at a time with meshroom_batch. Only generated paths and a
/// pipeline name from the installation's own list reach the command line. While Meshroom runs,
/// progress is read from the status files in its cache folder (Meshroom announces no total, so only
/// finished nodes and the current one are reported) and cancellation is checked; afterwards the
/// output folder is searched for the mesh instead of assuming a layout, the mesh is read back and
/// checked, and the working folder is deleted.
/// </summary>
public sealed class PhotogrammetryWorker(
    PhotogrammetryQueue queue,
    IServiceScopeFactory scopeFactory,
    IMeshroomEngine engine,
    IProcessRunner runner,
    ArtifactProcessor artifacts,
    IOptions<PhotogrammetryOptions> options,
    IOptions<ModelOutputOptions> output,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<PhotogrammetryWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["PhotogrammetryJobId"] = id });
            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Photogrammetry job failed unexpectedly");
                await UpdateAsync(id, j => Fail(j, PhotogrammetryErrorCodes.Internal, "The reconstruction failed unexpectedly; see the API log."), CancellationToken.None);
            }
        }
    }

    /// <summary>Queued jobs are run again; a run interrupted by a restart cannot be resumed and is failed.</summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var job in await db.PhotogrammetryJobs.Where(j => j.Status == PhotogrammetryStatuses.Running).ToListAsync(cancellationToken))
            Fail(job, PhotogrammetryErrorCodes.Interrupted, "The application stopped while Meshroom was running. Start a new reconstruction with the same photos.");
        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in await db.PhotogrammetryJobs.Where(j => j.Status == PhotogrammetryStatuses.Queued).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(cancellationToken))
            queue.Enqueue(id);
    }

    private async Task ProcessAsync(Guid id, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IPhotogrammetryArtifactStore>();
        var job = await db.PhotogrammetryJobs.Include(j => j.Photos).FirstOrDefaultAsync(j => j.Id == id, stoppingToken);
        if (job is null || job.Status != PhotogrammetryStatuses.Queued)
            return;

        var capability = await engine.GetCapabilityAsync(stoppingToken);
        if (capability.Status != "ready" || capability.Executable is null)
        {
            Fail(job, PhotogrammetryErrorCodes.MeshroomUnavailable, capability.Reason ?? "Meshroom is not available.");
            await db.SaveChangesAsync(stoppingToken);
            return;
        }

        var o = options.Value;
        var work = Path.Combine(Path.GetFullPath(o.WorkRoot, environment.ContentRootPath), id.ToString("N"));
        var images = Path.Combine(work, "images");
        var cache = Path.Combine(work, "cache");
        var outputDir = Path.Combine(work, "output");
        try
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(images);
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(outputDir);
            var index = 0;
            foreach (var photo in job.Photos.OrderBy(p => p.SortOrder))
            {
                await using var source = await store.OpenReadAsync(job.Id, photo.FileName, stoppingToken)
                                         ?? throw new InvalidOperationException($"Photo {photo.Id} is missing from storage.");
                await using var target = File.Create(Path.Combine(images, $"photo_{++index:D4}.{photo.Format}"));
                await source.CopyToAsync(target, stoppingToken);
            }

            job.Status = PhotogrammetryStatuses.Running;
            job.StartedAt = time.GetUtcNow().UtcDateTime;
            job.Stage = "Starting";
            job.NodesDone = 0;
            await SaveAsync(db, job, stoppingToken);
            logger.LogInformation("Running Meshroom ({Pipeline}) on {Photos} photos", job.Pipeline, index);

            var outcome = await RunMeshroomAsync(db, job, capability.Executable, images, cache, outputDir, stoppingToken);
            if (outcome is null)
                return; // deleted while running
            await FinishAsync(db, store, job, outcome, cache, outputDir, stoppingToken);
        }
        finally
        {
            if (!o.KeepWorkFiles)
            {
                try
                {
                    if (Directory.Exists(work))
                        Directory.Delete(work, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete the Meshroom work folder {Work}", work);
                }
            }
        }
    }

    private sealed record Outcome(ProcessResult? Result, bool Cancelled);

    /// <summary>Runs Meshroom while polling progress and the cancel flag. Null when the job was deleted.</summary>
    private async Task<Outcome?> RunMeshroomAsync(AppDbContext db, PhotogrammetryJob job, string executable, string images, string cache, string outputDir, CancellationToken stoppingToken)
    {
        var o = options.Value;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var spec = new ProcessSpec(
            executable,
            ["--input", images, "--pipeline", job.Pipeline!, "--output", outputDir, "--cache", cache, "--verbose", "info"],
            Path.GetDirectoryName(executable)!,
            o.Timeout,
            MaxStandardOutputBytes: o.MaxLogBytes,
            MaxStandardErrorBytes: o.MaxLogBytes,
            KeepStandardOutputTail: true);
        var run = runner.RunAsync(spec, stop.Token);

        var cancelled = false;
        var deleted = false;
        var entry = db.Entry(job);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(o.ProgressInterval, time, stoppingToken));
            if (run.IsCompleted)
                break;
            if (!await db.PhotogrammetryJobs.AsNoTracking().AnyAsync(j => j.Id == job.Id, stoppingToken))
            {
                deleted = true;
                await stop.CancelAsync();
                break;
            }

            await entry.ReloadAsync(stoppingToken);
            if (job.CancelRequested)
            {
                cancelled = true;
                await stop.CancelAsync();
                break;
            }

            var (done, current) = ReadProgress(cache);
            if (done != job.NodesDone || current != job.Stage)
            {
                job.NodesDone = done;
                job.Stage = current ?? job.Stage;
                await SaveAsync(db, job, stoppingToken);
            }
        }

        try
        {
            var result = await run;
            return new Outcome(result, false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            if (deleted)
            {
                entry.State = EntityState.Detached;
                logger.LogInformation("Photogrammetry job deleted while running; Meshroom stopped");
                return null;
            }

            return new Outcome(null, cancelled);
        }
    }

    private async Task FinishAsync(AppDbContext db, IPhotogrammetryArtifactStore store, PhotogrammetryJob job, Outcome outcome, string cache, string outputDir, CancellationToken stoppingToken)
    {
        var (done, _) = ReadProgress(cache);
        job.NodesDone = done;
        if (outcome.Result is { } r)
        {
            job.ExitCode = r.ExitCode;
            var log = Encoding.UTF8.GetString(r.StandardOutput) + (r.StandardError.Length > 0 ? "\n--- stderr ---\n" + r.StandardError : "");
            await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.Log, Encoding.UTF8.GetBytes(log), stoppingToken);
        }

        if (outcome.Cancelled || outcome.Result is null)
        {
            job.Status = PhotogrammetryStatuses.Cancelled;
            job.CompletedAt = time.GetUtcNow().UtcDateTime;
            job.Stage = null;
            await SaveAsync(db, job, CancellationToken.None);
            return;
        }

        var result = outcome.Result;
        if (result.TimedOut)
        {
            Fail(job, PhotogrammetryErrorCodes.Timeout, $"Meshroom did not finish within {options.Value.Timeout.TotalHours:0.#} h and was stopped. Use fewer or smaller photos, or the draft pipeline.");
            await SaveAsync(db, job, CancellationToken.None);
            return;
        }

        if (result.ExitCode != 0)
        {
            Fail(job, PhotogrammetryErrorCodes.MeshroomFailed, $"Meshroom stopped with exit code {result.ExitCode}{(job.Stage is { } s && s != "Starting" ? $" during {s}" : "")}. Typical causes: too few overlapping photos, featureless or shiny surfaces, or no CUDA GPU for the full pipeline. The log has details.");
            await SaveAsync(db, job, CancellationToken.None);
            return;
        }

        var meshFile = FindMesh(outputDir, cache);
        if (meshFile is null)
        {
            Fail(job, PhotogrammetryErrorCodes.NoOutputMesh, "Meshroom finished but produced no mesh (OBJ). Usually too few photos could be matched; check the log.");
            await SaveAsync(db, job, CancellationToken.None);
            return;
        }

        Mesh mesh;
        try
        {
            if (new FileInfo(meshFile).Length > options.Value.MaxObjBytes)
                throw new InvalidDataException($"The mesh file is larger than {options.Value.MaxObjBytes / (1024 * 1024)} MB.");
            mesh = ObjReader.Read(await File.ReadAllBytesAsync(meshFile, stoppingToken));
            var validation = MeshValidator.Validate(mesh);
            if (!validation.IsValid)
                throw new InvalidDataException(string.Join(" ", validation.Errors));
        }
        catch (InvalidDataException ex)
        {
            Fail(job, PhotogrammetryErrorCodes.InvalidOutputMesh, "Meshroom's mesh could not be read: " + ex.Message);
            await SaveAsync(db, job, CancellationToken.None);
            return;
        }

        var raw = GlbWriter.Write(mesh, "Photogrammetry reconstruction (Meshroom), unscaled: Meshroom units.");
        await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.RawMesh, raw, stoppingToken);
        var bounds = MeshOperations.ComputeBounds(mesh);
        (job.ModelSizeX, job.ModelSizeY, job.ModelSizeZ) = (bounds.Size.X, bounds.Size.Y, bounds.Size.Z);
        job.VertexCount = mesh.VertexCount;
        job.TriangleCount = mesh.TriangleCount;
        var welded = MeshTopology.Weld(mesh, MeshTopology.DefaultWeldTolerance(mesh));
        var topology = MeshTopology.Analyze(welded);
        job.InspectionJson = JsonSerializer.Serialize(new MeshInspectionDto(mesh.VertexCount, mesh.TriangleCount, topology.IsClosed, topology.BoundaryEdgeCount, topology.NonManifoldEdgeCount, topology.DegenerateTriangleCount, topology.ComponentCount), Json);

        var outputs = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => new PhotogrammetryOutputDto(Path.GetRelativePath(outputDir, f).Replace('\\', '/'), new FileInfo(f).Length))
            .ToList();
        if (!meshFile.StartsWith(outputDir, StringComparison.Ordinal))
            outputs.Add(new PhotogrammetryOutputDto("(cache) " + Path.GetFileName(meshFile), new FileInfo(meshFile).Length));
        job.OutputsJson = JsonSerializer.Serialize(outputs, Json);
        await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.OutputZip, Zip(outputDir, meshFile), stoppingToken);

        // Meshroom's units are arbitrary: start at the default longest side until the user calibrates.
        var longest = bounds.Size.MaxComponent;
        await PhotogrammetryService.ApplyScaleAsync(store, artifacts, job, longest > 0 ? output.Value.DefaultLongestSideMm / longest : 1, stoppingToken);
        job.ScaleStatus = PhotogrammetryScaleStatuses.Uncalibrated;
        job.Status = PhotogrammetryStatuses.Succeeded;
        job.Stage = null;
        job.CompletedAt = time.GetUtcNow().UtcDateTime;
        await SaveAsync(db, job, CancellationToken.None);
        logger.LogInformation("Photogrammetry succeeded: {Triangles} triangles from {Mesh}", mesh.TriangleCount, Path.GetFileName(meshFile));
    }

    /// <summary>
    /// The mesh to read: an OBJ in the output folder (the textured mesh first, then the largest);
    /// failing that, the newest texturedMesh.obj or mesh.obj in the cache.
    /// </summary>
    public static string? FindMesh(string outputDir, string cache)
    {
        static IEnumerable<FileInfo> Objs(string dir) =>
            Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.obj", SearchOption.AllDirectories) : [];

        var published = Objs(outputDir).ToList();
        var textured = published.FirstOrDefault(f => f.Name.Equals("texturedMesh.obj", StringComparison.OrdinalIgnoreCase));
        if (textured is not null)
            return textured.FullName;
        if (published.Count > 0)
            return published.OrderByDescending(f => f.Length).ThenBy(f => f.FullName, StringComparer.Ordinal).First().FullName;

        var cached = Objs(cache).Where(f => f.Name.Equals("texturedMesh.obj", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("mesh.obj", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Name.Equals("texturedMesh.obj", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return cached?.FullName;
    }

    /// <summary>
    /// Finished nodes and the node running now, from Meshroom's cache: every node computes in
    /// cache/&lt;NodeType&gt;/&lt;uid&gt;/ and keeps a JSON "status" file there. Unknown layouts give (0, null).
    /// </summary>
    public static (int Done, string? Current) ReadProgress(string cache)
    {
        if (!Directory.Exists(cache))
            return (0, null);
        var done = 0;
        string? current = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(cache, "status", SearchOption.AllDirectories))
            {
                string? status;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                    status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
                {
                    continue; // being written right now
                }

                if (status == "SUCCESS")
                    done++;
                else if (status == "RUNNING")
                    current = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (done, current);
    }

    private static byte[] Zip(string outputDir, string meshFile)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                zip.CreateEntryFromFile(file, Path.GetRelativePath(outputDir, file).Replace('\\', '/'), CompressionLevel.Optimal);
            if (!meshFile.StartsWith(outputDir, StringComparison.Ordinal))
                zip.CreateEntryFromFile(meshFile, Path.GetFileName(meshFile), CompressionLevel.Optimal);
        }

        return memory.ToArray();
    }

    private void Fail(PhotogrammetryJob job, string code, string message)
    {
        job.Status = PhotogrammetryStatuses.Failed;
        job.ErrorCode = code;
        job.ErrorMessage = message.Length > 1000 ? message[..1000] : message;
        job.Stage = null;
        job.CompletedAt ??= time.GetUtcNow().UtcDateTime;
    }

    private async Task SaveAsync(AppDbContext db, PhotogrammetryJob job, CancellationToken cancellationToken)
    {
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted meanwhile: nothing left to update.
            db.Entry(job).State = EntityState.Detached;
        }
    }

    private async Task UpdateAsync(Guid id, Action<PhotogrammetryJob> change, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PhotogrammetryJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null || PhotogrammetryStatuses.IsTerminal(job.Status))
            return;
        change(job);
        await SaveAsync(db, job, cancellationToken);
    }
}
