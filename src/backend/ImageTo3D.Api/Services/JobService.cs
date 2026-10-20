using System.Text.RegularExpressions;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.Imaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services;

/// <summary>Domain error surfaced to clients as RFC 7807 problem details.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found", "Model job not found.");
}

public sealed record ArtifactFile(Stream Content, string ContentType, string DownloadName);

public sealed partial class JobService(
    AppDbContext db,
    IArtifactStore store,
    JobQueue queue,
    IOwnerAccessor owner,
    IInferenceClient inference,
    ArtifactProcessor processor,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider time,
    ILogger<JobService> logger)
{
    /// <summary>View hints accepted for extra photos (where the camera was relative to the main photo).</summary>
    public static readonly IReadOnlySet<string> ExtraViewHints = new HashSet<string>(StringComparer.Ordinal) { "auto", "front", "back", "left", "right", "top", "bottom" };

    public Task<(JobDto Job, bool Created)> CreateAsync(IFormFile? file, string? idempotencyKey, CancellationToken cancellationToken) =>
        CreateAsync(file, null, null, idempotencyKey, cancellationToken);

    public async Task<(JobDto Job, bool Created)> CreateAsync(
        IFormFile? file,
        IReadOnlyList<IFormFile>? extraFiles,
        IReadOnlyList<string>? extraViews,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var o = uploadOptions.Value;
        var ownerHash = owner.OwnerKeyHash;

        if (idempotencyKey is not null && !IdempotencyKeyPattern().IsMatch(idempotencyKey))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_idempotency_key", "Idempotency-Key must be 8-64 characters of letters, digits or '-'.");

        if (idempotencyKey is not null)
        {
            var existing = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.OwnerKeyHash == ownerHash && j.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
                return (JobDto.From(existing), false);
        }

        if (file is null || file.Length == 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "missing_file", "Select an image file to upload.");
        if (file.Length > o.MaxBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"The image is larger than the {o.MaxBytes / (1024 * 1024)} MB limit.");

        var extras = extraFiles?.Where(f => f.Length > 0).ToList() ?? [];
        var views = extraViews ?? [];
        if (extras.Count > o.MaxExtraImages)
            throw new ApiException(StatusCodes.Status400BadRequest, "too_many_images", $"Upload at most {o.MaxExtraImages} extra photos.");
        if (views.Count > extras.Count)
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_extra_views", "There are more view hints than extra photos.");
        if (views.FirstOrDefault(v => !ExtraViewHints.Contains(v)) is { } badView)
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_extra_views", $"Unknown view '{Truncate(badView)}'. Use one of: {string.Join(", ", ExtraViewHints)}.");
        if (extras.FirstOrDefault(f => f.Length > o.MaxBytes) is not null)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"An extra photo is larger than the {o.MaxBytes / (1024 * 1024)} MB limit.");

        var active = await db.Jobs.CountAsync(j => j.OwnerKeyHash == ownerHash && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing), cancellationToken);
        if (active >= o.MaxActiveJobsPerOwner)
            throw new ApiException(StatusCodes.Status429TooManyRequests, "too_many_active_jobs", $"You already have {active} generations in progress. Wait for one to finish before submitting another.");

        var limits = new ImageLimits(o.MinDimension, o.MaxDimension, o.MaxPixels);
        var bytes = await ReadAsync(file, cancellationToken);
        DecodedImage decoded;
        try
        {
            decoded = ImageDecoder.Decode(bytes, limits);
        }
        catch (ImageValidationException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_image", ex.Message);
        }

        // Extra photos are validated the same way and stored as upright PNGs; only their index names the file.
        var extraPngs = new List<byte[]>(extras.Count);
        for (var i = 0; i < extras.Count; i++)
        {
            try
            {
                extraPngs.Add(ImageDecoder.EncodePng(ImageDecoder.Decode(await ReadAsync(extras[i], cancellationToken), limits).Image));
            }
            catch (ImageValidationException ex)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_image", $"Extra photo {i + 1}: {ex.Message}");
            }
        }

        var extension = decoded.Format switch
        {
            ImageFormatKind.Png => "png",
            ImageFormatKind.Jpeg => "jpg",
            _ => "webp",
        };

        var now = time.GetUtcNow().UtcDateTime;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = ownerHash,
            IdempotencyKey = idempotencyKey,
            OriginalFileName = SanitizeDisplayName(file.FileName, extension),
            OriginalFormat = extension,
            OriginalSizeBytes = bytes.Length,
            ImageWidth = decoded.Image.Width,
            ImageHeight = decoded.Image.Height,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Original, extension), bytes, cancellationToken);
        await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.SourceImage), ImageDecoder.EncodePng(decoded.Image), cancellationToken);
        if (extraPngs.Count > 0)
        {
            for (var i = 0; i < extraPngs.Count; i++)
                await store.SaveAsync(job.Id, ArtifactNames.ExtraImage(i + 1), extraPngs[i], cancellationToken);
            var hints = Enumerable.Range(0, extraPngs.Count).Select(i => i < views.Count ? views[i] : "auto").ToArray();
            await store.SaveAsync(job.Id, ArtifactNames.ExtraViews, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(hints), cancellationToken);
        }

        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Lost a race with an identical concurrent submit: return the winner.
            await store.DeleteJobAsync(job.Id, CancellationToken.None);
            db.ChangeTracker.Clear();
            var winner = await db.Jobs.AsNoTracking().FirstAsync(j => j.OwnerKeyHash == ownerHash && j.IdempotencyKey == idempotencyKey, cancellationToken);
            return (JobDto.From(winner), false);
        }

        queue.Enqueue(job.Id);
        logger.LogInformation("Created job {JobId} ({Format}, {Width}x{Height}, {ExtraCount} extra photos)", job.Id, extension, job.ImageWidth, job.ImageHeight, extraPngs.Count);
        return (JobDto.From(job), true);
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string Truncate(string value) => value.Length > 20 ? value[..20] : value;

    public async Task<JobDto> GetAsync(Guid id, CancellationToken cancellationToken) => JobDto.From(await FindAsync(id, cancellationToken, tracking: false));

    public async Task<PagedResult<JobDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Jobs.AsNoTracking().Where(j => j.OwnerKeyHash == owner.OwnerKeyHash);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(j => j.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<JobDto>(items.Select(JobDto.From).ToList(), page, pageSize, total);
    }

    public async Task<JobDto> ScaleAsync(Guid id, ScaleAxis axis, double targetMm, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken, tracking: true);
        if (job.Status != JobStatus.Succeeded || job.SizeMm is null)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "The model can only be scaled after generation has succeeded.");

        var raw = await store.ReadAllAsync(job.Id, ArtifactNames.FileName(ArtifactKind.RawMesh), cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The generated mesh is no longer available.");

        // Factor is computed against the raw mesh in model units, so repeated scaling never compounds rounding.
        var modelSize = new Vector3D(job.ModelSizeX!.Value, job.ModelSizeY!.Value, job.ModelSizeZ!.Value);
        double mmPerUnit;
        try
        {
            mmPerUnit = ScaleCalculator.ComputeUniformFactor(modelSize, axis, targetMm);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_scale", ex.Message.Split(" (Parameter")[0]);
        }

        var artifacts = processor.Build(raw, ArtifactProcessor.LoadRawMesh(raw), mmPerUnit);
        await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Glb), artifacts.Glb, cancellationToken);
        if (artifacts.Stl is not null)
            await store.SaveAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Stl), artifacts.Stl, cancellationToken);
        else
            await store.DeleteAsync(job.Id, ArtifactNames.FileName(ArtifactKind.Stl), cancellationToken);

        job.MmPerModelUnit = mmPerUnit;
        job.ScaleAxis = axis;
        job.ScaleTargetMm = targetMm;
        job.HasStl = artifacts.Stl is not null;
        job.StlError = artifacts.StlError;
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return JobDto.From(job);
    }

    public async Task<JobDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken, tracking: true);
        if (job.IsTerminal)
            throw new ApiException(StatusCodes.Status409Conflict, "already_finished", "The job has already finished.");

        job.CancelRequested = true;
        job.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        // Stop GPU/CPU work promptly; the worker records the final Cancelled state.
        if (job.InferenceJobId is { } inferenceId)
        {
            try
            {
                await inference.CancelAsync(inferenceId, cancellationToken);
            }
            catch (InferenceUnavailableException)
            {
                // The worker will retry the cancel on its next poll.
            }
        }

        return JobDto.From(job);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken, tracking: true);
        if (!job.IsTerminal && job.InferenceJobId is { } inferenceId)
        {
            try
            {
                await inference.CancelAsync(inferenceId, cancellationToken);
            }
            catch (InferenceUnavailableException)
            {
            }
        }

        db.Jobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteJobAsync(job.Id, cancellationToken);
        logger.LogInformation("Deleted job {JobId}", job.Id);
    }

    public async Task<ArtifactFile> OpenArtifactAsync(Guid id, ArtifactKind kind, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken, tracking: false);
        var isModel = kind is ArtifactKind.Glb or ArtifactKind.Stl;
        if (isModel && job.Status != JobStatus.Succeeded)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "The model is not available until generation has succeeded.");
        if (kind == ArtifactKind.Stl && !job.HasStl)
            throw new ApiException(StatusCodes.Status404NotFound, "stl_unavailable", job.StlError ?? "STL is not available for this model.");

        var stream = await store.OpenReadAsync(job.Id, ArtifactNames.FileName(kind), cancellationToken)
                     ?? throw new ApiException(StatusCodes.Status404NotFound, "artifact_missing", "The requested file is not available.");

        var shortId = job.Id.ToString("N")[..8];
        var size = job.SizeMm is { } s ? $"-{s.MaxComponent:F0}mm" : "";
        return kind switch
        {
            ArtifactKind.Glb => new ArtifactFile(stream, "model/gltf-binary", $"model-{shortId}{size}.glb"),
            ArtifactKind.Stl => new ArtifactFile(stream, "model/stl", $"model-{shortId}{size}.stl"),
            _ => new ArtifactFile(stream, "image/png", $"{kind.ToString().ToLowerInvariant()}-{shortId}.png"),
        };
    }

    private async Task<GenerationJob> FindAsync(Guid id, CancellationToken cancellationToken, bool tracking)
    {
        var query = tracking ? db.Jobs : db.Jobs.AsNoTracking();
        return await query.FirstOrDefaultAsync(j => j.Id == id && j.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
               ?? throw ApiException.NotFound();
    }

    /// <summary>Keeps a short, printable display name; it is never used to build a path.</summary>
    private static string SanitizeDisplayName(string? fileName, string extension)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "");
        name = UnsafeChars().Replace(name, "_").Trim('_', '.', ' ');
        if (name.Length == 0)
            name = "image";
        if (name.Length > 100)
            name = name[..100];
        return $"{name}.{extension}";
    }

    [GeneratedRegex("[^A-Za-z0-9 _.-]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdempotencyKeyPattern();
}
