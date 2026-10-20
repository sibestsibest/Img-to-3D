using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ImageTo3D.Api.Controllers;

[ApiController]
[Route("api/models")]
[Produces("application/json")]
public sealed class ModelsController(JobService jobs) : ControllerBase
{
    /// <summary>Upload an image (multipart field "image") and create a generation job.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Uploads)]
    [RequestSizeLimit(60L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60L * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<JobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(
        IFormFile? image,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var (job, created) = await jobs.CreateAsync(image, idempotencyKey, cancellationToken);
        return created ? AcceptedAtAction(nameof(Get), new { id = job.Id }, job) : Ok(job);
    }

    [HttpGet]
    [ProducesResponseType<PagedResult<JobDto>>(StatusCodes.Status200OK)]
    public Task<PagedResult<JobDto>> List([FromQuery] ListJobsQuery query, CancellationToken cancellationToken) =>
        jobs.ListAsync(query.Page, query.PageSize, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<JobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<JobDto> Get(Guid id, CancellationToken cancellationToken) => jobs.GetAsync(id, cancellationToken);

    /// <summary>GLB for the in-browser viewer (inline, scaled, metres).</summary>
    [HttpGet("{id:guid}/preview")]
    [Produces("model/gltf-binary")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        var file = await jobs.OpenArtifactAsync(id, ArtifactKind.Glb, cancellationToken);
        Response.Headers.CacheControl = "private, no-cache";
        return File(file.Content, file.ContentType, enableRangeProcessing: false);
    }

    /// <summary>Download the generated model as GLB (metres) or binary STL (millimetres, Z-up).</summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string format = "glb", CancellationToken cancellationToken = default)
    {
        var kind = format.ToLowerInvariant() switch
        {
            "glb" => ArtifactKind.Glb,
            "stl" => ArtifactKind.Stl,
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "invalid_format", "format must be 'glb' or 'stl'."),
        };
        var file = await jobs.OpenArtifactAsync(id, kind, cancellationToken);
        return File(file.Content, file.ContentType, file.DownloadName);
    }

    /// <summary>The upright uploaded image ("source") or the background-removed image the model saw ("preprocessed").</summary>
    [HttpGet("{id:guid}/image")]
    [Produces("image/png")]
    public async Task<IActionResult> Image(Guid id, [FromQuery] string kind = "source", CancellationToken cancellationToken = default)
    {
        var artifact = kind.ToLowerInvariant() switch
        {
            "source" => ArtifactKind.SourceImage,
            "preprocessed" => ArtifactKind.PreprocessedImage,
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "invalid_kind", "kind must be 'source' or 'preprocessed'."),
        };
        var file = await jobs.OpenArtifactAsync(id, artifact, cancellationToken);
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(file.Content, file.ContentType);
    }

    /// <summary>Uniformly scale the model so the chosen bounding-box side equals TargetMm.</summary>
    [HttpPut("{id:guid}/scale")]
    [Consumes("application/json")]
    [ProducesResponseType<JobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<JobDto> Scale(Guid id, [FromBody] ScaleRequest request, CancellationToken cancellationToken) =>
        jobs.ScaleAsync(id, request.Axis!.Value, request.TargetMm!.Value, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        Accepted(await jobs.CancelAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await jobs.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
