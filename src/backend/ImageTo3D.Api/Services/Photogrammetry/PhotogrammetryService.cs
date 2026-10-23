using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.Imaging;
using ImageTo3D.Core.Reconstruction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Photogrammetry;

/// <summary>In-memory signal of photogrammetry jobs to run; the database is the source of truth.</summary>
public sealed class PhotogrammetryQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>A photo handed over by another feature (a reference search's pictures).</summary>
public sealed record PhotoInput(byte[] Bytes, string Name);

/// <summary>
/// Photo sets and runs of the Meshroom photogrammetry engine: a job collects photos as a draft,
/// is checked (count, resolution, blur, duplicates, camera data), then runs once in the background
/// (<see cref="PhotogrammetryWorker"/>). Photos are stored unchanged so Meshroom gets their EXIF data.
/// </summary>
public sealed class PhotogrammetryService(
    AppDbContext db,
    IPhotogrammetryArtifactStore store,
    IMeshroomEngine engine,
    PhotogrammetryQueue queue,
    ArtifactProcessor artifacts,
    IOwnerAccessor owner,
    IOptions<PhotogrammetryOptions> options,
    TimeProvider time,
    ILogger<PhotogrammetryService> logger)
{
    public const int ThumbnailSide = 320;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PhotogrammetryEngineDto> EngineAsync(CancellationToken cancellationToken)
    {
        var c = await engine.GetCapabilityAsync(cancellationToken);
        var o = options.Value;
        return new PhotogrammetryEngineDto(c.Status, c.Reason, c.Version, c.Pipelines, c.DefaultPipeline, c.Cuda, c.Gpu, c.Notes, o.MaxPhotos, o.MaxPhotoBytes);
    }

    public async Task<PhotogrammetryJobDto> CreateAsync(CreatePhotogrammetryRequest request, CancellationToken cancellationToken)
    {
        var job = await NewJobAsync(request.Name, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(job);
    }

    private async Task<PhotogrammetryJob> NewJobAsync(string? name, Guid? sessionId, CancellationToken cancellationToken)
    {
        var max = options.Value.MaxJobsPerOwner;
        if (await db.PhotogrammetryJobs.CountAsync(j => j.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken) >= max)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_jobs", $"You already have {max} photogrammetry jobs. Delete one first.");
        var now = time.GetUtcNow().UtcDateTime;
        var job = new PhotogrammetryJob
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = owner.OwnerKeyHash,
            Name = Truncate(string.IsNullOrWhiteSpace(name) ? "Photo set" : name.Trim(), 100),
            SourceReferenceSessionId = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PhotogrammetryJobs.Add(job);
        return job;
    }

    public async Task<PagedResult<PhotogrammetrySummaryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PhotogrammetryJobs.AsNoTracking().Where(j => j.OwnerKeyHash == owner.OwnerKeyHash);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(j => j.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(j => new PhotogrammetrySummaryDto(j.Id, j.Name, j.Status, j.Photos.Count, j.Pipeline, j.CreatedAt, j.CompletedAt, GeometryOrigins.Photogrammetry))
            .ToListAsync(cancellationToken);
        return new PagedResult<PhotogrammetrySummaryDto>(items.Select(i => i with { CreatedAt = Utc(i.CreatedAt), CompletedAt = i.CompletedAt is { } c ? Utc(c) : null }).ToList(), page, pageSize, total);
    }

    public async Task<PhotogrammetryJobDto> GetAsync(Guid id, CancellationToken cancellationToken) => ToDto(await LoadAsync(id, cancellationToken));

    /// <summary>Adds photos to a draft. The whole batch is checked first; one unreadable file refuses the batch.</summary>
    public async Task<PhotogrammetryJobDto> AddPhotosAsync(Guid id, IReadOnlyList<IFormFile> files, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (files.Count == 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "missing_file", "Choose at least one photo.");
        var inputs = new List<PhotoInput>();
        var o = options.Value;
        foreach (var file in files)
        {
            if (file.Length > o.MaxPhotoBytes)
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"{Display(file.FileName)} is larger than {o.MaxPhotoBytes / (1024 * 1024)} MB.");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            inputs.Add(new PhotoInput(memory.ToArray(), file.FileName));
        }

        await AddPhotosCoreAsync(job, inputs, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(job);
    }

    private async Task AddPhotosCoreAsync(PhotogrammetryJob job, IReadOnlyList<PhotoInput> inputs, CancellationToken cancellationToken)
    {
        if (job.Status != PhotogrammetryStatuses.Draft)
            throw new ApiException(StatusCodes.Status409Conflict, "not_draft", "Photos can only be changed before the reconstruction starts.");
        var o = options.Value;
        if (job.Photos.Count + inputs.Count > o.MaxPhotos)
            throw new ApiException(StatusCodes.Status400BadRequest, "too_many_photos", $"A photo set holds at most {o.MaxPhotos} photos.");

        var limits = new ImageLimits(64, o.MaxPhotoDimension, o.MaxPhotoPixels);
        var checkedPhotos = new List<(PhotoInput Input, PhotoFacts Facts, RgbaImage Preview)>();
        foreach (var input in inputs)
        {
            try
            {
                var facts = PhotoInspector.Inspect(input.Bytes, limits, out var preview);
                checkedPhotos.Add((input, facts, preview));
            }
            catch (ImageValidationException ex)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_photo", $"{Display(input.Name)}: {ex.Message}");
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var order = job.Photos.Count == 0 ? 0 : job.Photos.Max(p => p.SortOrder) + 1;
        foreach (var (input, facts, preview) in checkedPhotos)
        {
            var photo = new PhotogrammetryPhoto
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                SortOrder = order++,
                FileName = "",
                OriginalName = Truncate(Display(input.Name), 128),
                Format = facts.Format == ImageFormatKind.Png ? "png" : "jpg",
                Width = facts.Width,
                Height = facts.Height,
                SizeBytes = input.Bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(input.Bytes)),
                DHash = unchecked((long)facts.DHash),
                Sharpness = Math.Round(facts.Sharpness, 3),
                HasExif = facts.HasExif,
                CreatedAt = now,
            };
            photo.FileName = PhotogrammetryArtifactNames.Photo(photo.Id, photo.Format);
            await store.SaveAsync(job.Id, photo.FileName, input.Bytes, cancellationToken);
            await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.Thumbnail(photo.Id), ImageThumbnail.Create(preview, ThumbnailSide).Jpeg, cancellationToken);
            job.Photos.Add(photo);
            db.PhotogrammetryPhotos.Add(photo);
        }

        job.UpdatedAt = now;
    }

    public async Task<PhotogrammetryJobDto> RemovePhotoAsync(Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (job.Status != PhotogrammetryStatuses.Draft)
            throw new ApiException(StatusCodes.Status409Conflict, "not_draft", "Photos can only be changed before the reconstruction starts.");
        var photo = job.Photos.FirstOrDefault(p => p.Id == photoId) ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Photo not found.");
        job.Photos.Remove(photo);
        db.PhotogrammetryPhotos.Remove(photo);
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteAsync(job.Id, photo.FileName, cancellationToken);
        await store.DeleteAsync(job.Id, PhotogrammetryArtifactNames.Thumbnail(photo.Id), cancellationToken);
        return ToDto(job);
    }

    public async Task<PhotogrammetryJobDto> StartAsync(Guid id, StartPhotogrammetryRequest request, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        await StartCoreAsync(job, request.Pipeline, cancellationToken);
        return ToDto(job);
    }

    private async Task StartCoreAsync(PhotogrammetryJob job, string? pipeline, CancellationToken cancellationToken)
    {
        if (job.Status != PhotogrammetryStatuses.Draft)
            throw new ApiException(StatusCodes.Status409Conflict, "not_draft", "This photo set has already been started.");
        var blocker = Findings(job).FirstOrDefault(f => f.Severity == "blocker");
        if (blocker is not null)
            throw new ApiException(StatusCodes.Status409Conflict, "photo_set_blocked", blocker.Message);
        var c = await engine.GetCapabilityAsync(cancellationToken);
        if (c.Status != "ready")
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "meshroom_unavailable", c.Reason ?? "Meshroom is not available.");
        var chosen = string.IsNullOrWhiteSpace(pipeline) ? c.DefaultPipeline! : pipeline;
        if (!c.Pipelines.Contains(chosen, StringComparer.Ordinal))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_pipeline", $"Use one of: {string.Join(", ", c.Pipelines)}.", "pipeline");

        job.Status = PhotogrammetryStatuses.Queued;
        job.Pipeline = chosen;
        job.EngineVersion = c.Version is null ? null : Truncate(c.Version, 32);
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        queue.Enqueue(job.Id);
        logger.LogInformation("Queued photogrammetry job {JobId} with {Photos} photos, pipeline {Pipeline}", job.Id, job.Photos.Count, chosen);
    }

    /// <summary>Starts a job from pictures another feature collected (the reference search's multi-view route).</summary>
    public async Task<Guid> CreateAndStartAsync(string name, IReadOnlyList<PhotoInput> photos, Guid sessionId, CancellationToken cancellationToken)
    {
        var job = await NewJobAsync(name, sessionId, cancellationToken);
        await AddPhotosCoreAsync(job, photos, cancellationToken);
        await StartCoreAsync(job, null, cancellationToken);
        return job.Id;
    }

    public async Task<PhotogrammetryJobDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (PhotogrammetryStatuses.IsTerminal(job.Status) || job.Status == PhotogrammetryStatuses.Draft)
            throw new ApiException(StatusCodes.Status409Conflict, "not_running", "Only a queued or running reconstruction can be cancelled.");
        var now = time.GetUtcNow().UtcDateTime;
        if (job.Status == PhotogrammetryStatuses.Queued)
        {
            job.Status = PhotogrammetryStatuses.Cancelled;
            job.CompletedAt = now;
        }

        // A running job is stopped by the worker, which checks this flag every few seconds.
        job.CancelRequested = true;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(job);
    }

    /// <summary>Deletes the job and all its files; a running Meshroom process is killed by the worker.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        db.PhotogrammetryJobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteJobAsync(job.Id, cancellationToken);
    }

    public async Task<(byte[] Content, string ContentType)> ThumbnailAsync(Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (job.Photos.All(p => p.Id != photoId))
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Photo not found.");
        var bytes = await store.ReadAllAsync(job.Id, PhotogrammetryArtifactNames.Thumbnail(photoId), cancellationToken)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Thumbnail not found.");
        return (bytes, "image/jpeg");
    }

    /// <summary><c>glb</c>, <c>stl</c>, <c>zip</c> (Meshroom's own output files) or <c>log</c>.</summary>
    public async Task<ArtifactFile> OpenFileAsync(Guid id, string format, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        var (file, type, suffix) = format switch
        {
            "glb" => (PhotogrammetryArtifactNames.Glb, "model/gltf-binary", ".glb"),
            "stl" => (PhotogrammetryArtifactNames.Stl, "model/stl", ".stl"),
            "zip" => (PhotogrammetryArtifactNames.OutputZip, "application/zip", "-meshroom.zip"),
            "log" => (PhotogrammetryArtifactNames.Log, "text/plain; charset=utf-8", "-meshroom.log"),
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "invalid_format", "Use glb, stl, zip or log."),
        };
        if (format != "log" && job.Status != PhotogrammetryStatuses.Succeeded)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "The reconstruction has not succeeded.");
        var stream = await store.OpenReadAsync(job.Id, file, cancellationToken)
                     ?? throw new ApiException(StatusCodes.Status404NotFound, format == "stl" ? "stl_unavailable" : "not_found", format == "stl" ? job.StlError ?? "No STL." : "The file is not available.");
        return new ArtifactFile(stream, type, SafeStem(job.Name) + suffix);
    }

    /// <summary>Sets the real length of one bounding-box side, measured on the object; rebuilds GLB and STL.</summary>
    public async Task<PhotogrammetryJobDto> ScaleAsync(Guid id, PhotogrammetryScaleRequest request, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (job.Status != PhotogrammetryStatuses.Succeeded)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "The reconstruction has not succeeded.");
        var axis = Enum.Parse<ScaleAxis>(request.Axis!);
        var size = new Vector3D(job.ModelSizeX ?? 0, job.ModelSizeY ?? 0, job.ModelSizeZ ?? 0);
        double factor;
        try
        {
            factor = ScaleCalculator.ComputeUniformFactor(size, axis, request.TargetMm!.Value);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "zero_dimension", ex.Message, "axis");
        }

        await ApplyScaleAsync(job, factor, cancellationToken);
        job.ScaleStatus = PhotogrammetryScaleStatuses.Calibrated;
        job.ScaleAxis = axis.ToString();
        job.ScaleTargetMm = request.TargetMm;
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(job);
    }

    private Task ApplyScaleAsync(PhotogrammetryJob job, double mmPerUnit, CancellationToken cancellationToken) =>
        ApplyScaleAsync(store, artifacts, job, mmPerUnit, cancellationToken);

    /// <summary>Writes model.glb and model.stl at <paramref name="mmPerUnit"/> from the stored raw mesh (also used by the worker).</summary>
    internal static async Task ApplyScaleAsync(IPhotogrammetryArtifactStore store, ArtifactProcessor artifacts, PhotogrammetryJob job, double mmPerUnit, CancellationToken cancellationToken)
    {
        var raw = await store.ReadAllAsync(job.Id, PhotogrammetryArtifactNames.RawMesh, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The reconstructed mesh is no longer available.");
        var built = artifacts.Build(raw, ArtifactProcessor.LoadRawMesh(raw), mmPerUnit, "Photogrammetry reconstruction (Meshroom)");
        await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.Glb, built.Glb, cancellationToken);
        if (built.Stl is { } stl)
            await store.SaveAsync(job.Id, PhotogrammetryArtifactNames.Stl, stl, cancellationToken);
        else
            await store.DeleteAsync(job.Id, PhotogrammetryArtifactNames.Stl, cancellationToken);
        job.HasStl = built.Stl is not null;
        job.StlError = built.StlError;
        job.MmPerModelUnit = mmPerUnit;
    }

    /// <summary>Raw mesh in millimetres for a part design (the job's current scale).</summary>
    public async Task<(byte[] RawGlb, double MmPerUnit, PhotogrammetryJob Job)> ResultForDesignAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        if (job.Status != PhotogrammetryStatuses.Succeeded || job.MmPerModelUnit is not { } mm)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "The reconstruction has not succeeded.");
        var raw = await store.ReadAllAsync(job.Id, PhotogrammetryArtifactNames.RawMesh, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The reconstructed mesh is no longer available.");
        return (raw, mm, job);
    }

    private async Task<PhotogrammetryJob> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.PhotogrammetryJobs.Include(j => j.Photos).FirstOrDefaultAsync(j => j.Id == id && j.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Photogrammetry job not found.");

    private static IReadOnlyList<PhotoFindingDto> Findings(PhotogrammetryJob job)
    {
        var photos = job.Photos.OrderBy(p => p.SortOrder).ToList();
        var facts = photos.Select(p => new PhotoFacts(p.Format == "png" ? ImageFormatKind.Png : ImageFormatKind.Jpeg, p.Width, p.Height, p.HasExif, unchecked((ulong)p.DHash), p.Sharpness)).ToList();
        return PhotoSetCheck.Check(facts).Select(f => new PhotoFindingDto(f.Code, f.Severity, f.Message, f.Photo is { } i ? photos[i].Id : null)).ToList();
    }

    private PhotogrammetryJobDto ToDto(PhotogrammetryJob job)
    {
        var photos = job.Photos.OrderBy(p => p.SortOrder).ToList();
        var findings = Findings(job);
        var now = time.GetUtcNow().UtcDateTime;
        double? elapsed = job.StartedAt is { } s ? Math.Round(((job.CompletedAt ?? now) - s).TotalSeconds, 1) : null;
        var inspection = job.InspectionJson is null ? null : JsonSerializer.Deserialize<MeshInspectionDto>(job.InspectionJson, Json);
        DimensionsDto? dims = job.MmPerModelUnit is { } mm && job.ModelSizeX is { } x
            ? new DimensionsDto(Math.Round(x * mm, 3), Math.Round(job.ModelSizeY!.Value * mm, 3), Math.Round(job.ModelSizeZ!.Value * mm, 3))
            : null;
        var succeeded = job.Status == PhotogrammetryStatuses.Succeeded;
        var ran = job.StartedAt is not null && PhotogrammetryStatuses.IsTerminal(job.Status);
        return new PhotogrammetryJobDto(
            job.Id,
            job.Name,
            job.Status,
            job.Pipeline,
            new PhotogrammetryProgressDto(job.NodesDone, job.Status == PhotogrammetryStatuses.Running ? job.Stage : null, elapsed),
            job.ErrorCode,
            job.ErrorMessage,
            Utc(job.CreatedAt),
            job.StartedAt is { } st ? Utc(st) : null,
            job.CompletedAt is { } ct ? Utc(ct) : null,
            job.EngineVersion,
            job.SourceReferenceSessionId,
            photos.Select(p => new PhotogrammetryPhotoDto(p.Id, p.SortOrder, p.OriginalName, p.Format, p.Width, p.Height, p.SizeBytes, p.Sharpness, p.HasExif,
                $"/api/photogrammetry/{job.Id}/photos/{p.Id}/thumbnail")).ToList(),
            findings,
            job.Status == PhotogrammetryStatuses.Draft && findings.All(f => f.Severity != "blocker"),
            inspection,
            dims,
            new PhotogrammetryScaleDto(job.ScaleStatus, job.ScaleAxis, job.ScaleTargetMm, job.MmPerModelUnit),
            new PhotogrammetryDownloadsDto(succeeded, succeeded && job.HasStl, succeeded, ran, job.StlError),
            JsonSerializer.Deserialize<List<PhotogrammetryOutputDto>>(job.OutputsJson, Json) ?? []);
    }

    private static string Display(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        return string.IsNullOrWhiteSpace(name) ? "photo" : name;
    }

    private static string SafeStem(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var stem = new string(chars).Trim('-');
        return stem.Length == 0 ? "photogrammetry" : stem.Length > 60 ? stem[..60] : stem;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
