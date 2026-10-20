using ImageTo3D.Core.Contracts;
using ImageTo3D.Core.Imaging;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Jobs;
using ImageTo3D.Inference.Models;
using ImageTo3D.Inference.Pipeline;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Inference.Api;

public static class InferenceEndpoints
{
    public static void MapInferenceEndpoints(this WebApplication app)
    {
        app.MapGet("/health", (ModelHost models, InferenceJobStore store, IOptions<InferenceOptions> options) =>
        {
            var status = models.State switch
            {
                ModelState.Ready => "ok",
                ModelState.Loading or ModelState.NotLoaded => "loading",
                _ => "unavailable",
            };
            var dto = new InferenceHealthDto(status, models.State.ToString(), models.FailureReason, options.Value.ExecutionProvider, store.QueuedCount, "TripoSR");
            return models.State == ModelState.Ready ? Results.Ok(dto) : Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        var internalApi = app.MapGroup("/internal/jobs").AddEndpointFilter<InternalApiKeyFilter>();

        internalApi.MapPost("/", CreateJob).DisableAntiforgery();

        internalApi.MapGet("/{id:guid}", (Guid id, InferenceJobStore store) =>
            store.Get(id) is { } job ? Results.Ok(job.ToDto()) : NotFound());

        internalApi.MapGet("/{id:guid}/artifacts/{name}", (Guid id, string name, InferenceJobStore store) =>
        {
            // Whitelisted names only: no user-controlled path segments reach the filesystem.
            if (!InferenceArtifactNames.All.Contains(name))
                return NotFound();
            var job = store.Get(id);
            if (job is null || job.Status != InferenceJobStatus.Succeeded)
                return NotFound();
            var path = Path.Combine(job.Directory, name);
            return File.Exists(path) ? Results.File(path, InferenceJob.ContentTypeFor(name)) : NotFound();
        });

        internalApi.MapPost("/{id:guid}/cancel", (Guid id, InferenceJobStore store) =>
        {
            var job = store.Get(id);
            if (job is null)
                return NotFound();
            return job.RequestCancel()
                ? Results.Accepted(value: job.ToDto())
                : Problem(StatusCodes.Status409Conflict, "Job already finished", "The job has already reached a final state.");
        });

        internalApi.MapDelete("/{id:guid}", (Guid id, InferenceJobStore store) =>
        {
            if (!store.Remove(id, out var job) || job is null)
                return NotFound();
            job.RequestCancel();
            try
            {
                if (Directory.Exists(job.Directory))
                    Directory.Delete(job.Directory, recursive: true);
            }
            catch (IOException)
            {
                // Left for the retention cleanup (e.g. a file still being written by a cancelling job).
            }

            return Results.NoContent();
        });
    }

    private static async Task<IResult> CreateJob(
        HttpRequest request,
        InferenceJobStore store,
        InferenceQueue queue,
        IOptions<InferenceOptions> options,
        IHostEnvironment environment,
        ILogger<InferenceJob> logger)
    {
        var o = options.Value;
        if (!request.HasFormContentType)
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "Expected multipart/form-data with an 'image' file.");

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var file = form.Files.GetFile("image");
        if (file is null || file.Length == 0)
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "The 'image' file is required.");
        if (file.Length > o.MaxImageBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "Image too large", $"Images must be at most {o.MaxImageBytes / (1024 * 1024)} MB.");

        int? resolution = null;
        if (form.TryGetValue("resolution", out var resValue))
        {
            if (!int.TryParse(resValue, out var r) || r is < 32 or > 320)
                return Problem(StatusCodes.Status400BadRequest, "Invalid request", "resolution must be an integer between 32 and 320.");
            resolution = r;
        }

        float? ratio = null;
        if (form.TryGetValue("foregroundRatio", out var ratioValue))
        {
            if (!float.TryParse(ratioValue, System.Globalization.CultureInfo.InvariantCulture, out var f) || f is < 0.5f or > 1f)
                return Problem(StatusCodes.Status400BadRequest, "Invalid request", "foregroundRatio must be between 0.5 and 1.0.");
            ratio = f;
        }

        var removeBackground = !form.TryGetValue("removeBackground", out var rb) || !bool.TryParse(rb, out var parsed) || parsed;

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, request.HttpContext.RequestAborted);
        var bytes = buffer.ToArray();
        if (ImageDecoder.DetectFormat(bytes) is null)
            return Problem(StatusCodes.Status400BadRequest, "Unsupported image", "Upload a PNG, JPEG or WebP image.", InferenceErrorCodes.InvalidImage);

        var id = Guid.NewGuid();
        var directory = Path.Combine(Path.GetFullPath(o.WorkDirectory, environment.ContentRootPath), id.ToString("N"));
        var job = new InferenceJob(id, directory, new GenerationRequest(bytes, resolution, ratio, removeBackground));
        store.Add(job);
        if (!queue.TryEnqueue(id))
        {
            store.Remove(id, out _);
            return Problem(StatusCodes.Status503ServiceUnavailable, "Queue full", "The inference queue is full. Try again later.", InferenceErrorCodes.QueueFull);
        }

        logger.LogInformation("Inference job {InferenceJobId} queued ({Bytes} bytes)", id, bytes.Length);
        return Results.Accepted($"/internal/jobs/{id}", job.ToDto());
    }

    private static IResult NotFound() => Problem(StatusCodes.Status404NotFound, "Not found", "Job or artifact not found.");

    private static IResult Problem(int status, string title, string detail, string? code = null)
    {
        var extensions = code is null ? null : new Dictionary<string, object?> { ["code"] = code };
        return Results.Problem(detail: detail, statusCode: status, title: title, extensions: extensions);
    }
}

/// <summary>Requires X-Internal-Api-Key when an API key is configured (constant-time comparison).</summary>
public sealed class InternalApiKeyFilter(IOptions<InferenceOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Internal-Api-Key";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        if (string.IsNullOrEmpty(expected))
            return next(context);

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        var ok = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided), System.Text.Encoding.UTF8.GetBytes(expected));
        return ok
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: "Missing or invalid internal API key."));
    }
}
