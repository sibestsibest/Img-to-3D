using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.References;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ImageTo3D.Api.Controllers;

/// <summary>
/// Reference search: find existing pictures of the object in free public sources before
/// generating geometry, review them (accept or reject) and add your own. Similar-looking results
/// are candidates, not proof of the same object.
/// </summary>
[ApiController]
[Route("api/references")]
[Produces("application/json")]
public sealed class ReferencesController(ReferenceService references) : ControllerBase
{
    /// <summary>The sources the server can search and whether each is enabled. None needs an account or key.</summary>
    [HttpGet("sources")]
    public IReadOnlyList<ReferenceSourceDto> Sources() => references.ListSources();

    /// <summary>
    /// Start a reference search (multipart): an optional "image" (input photo) or "sourceJobId"
    /// (copy the photo of a generation job), plus optional "name" and "query".
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Uploads)]
    [RequestSizeLimit(25L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25L * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ReferenceSessionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(IFormFile? image, [FromForm] string? name, [FromForm] string? query, [FromForm] Guid? sourceJobId, CancellationToken cancellationToken)
    {
        var session = await references.CreateAsync(image, name, query, sourceJobId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = session.Id }, session);
    }

    [HttpGet]
    public Task<PagedResult<ReferenceSessionSummaryDto>> List([FromQuery] ListJobsQuery query, CancellationToken cancellationToken) =>
        references.ListAsync(query.Page, query.PageSize, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReferenceSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ReferenceSessionDto> Get(Guid id, CancellationToken cancellationToken) => references.GetAsync(id, cancellationToken);

    [HttpPut("{id:guid}")]
    [Consumes("application/json")]
    public Task<ReferenceSessionDto> Update(Guid id, [FromBody] UpdateReferenceSessionRequest request, CancellationToken cancellationToken) =>
        references.UpdateAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await references.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Search the enabled sources (or the listed ones) in parallel and add new results. Always 200
    /// when the session exists: each source reports its own status, and failures never fail the search.
    /// </summary>
    [HttpPost("{id:guid}/search")]
    [EnableRateLimiting(RateLimitPolicies.ReferenceSearches)]
    [Consumes("application/json")]
    [ProducesResponseType<ReferenceSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public Task<ReferenceSessionDto> Search(Guid id, [FromBody] SearchReferencesRequest request, CancellationToken cancellationToken) =>
        references.SearchAsync(id, request, cancellationToken);

    /// <summary>Add your own reference picture (multipart "image" plus optional title, author, pageUrl, license, licenseUrl).</summary>
    [HttpPost("{id:guid}/candidates")]
    [EnableRateLimiting(RateLimitPolicies.Uploads)]
    [RequestSizeLimit(25L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25L * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ReferenceSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ReferenceSessionDto> AddCandidate(Guid id, IFormFile? image, [FromForm] AddReferenceImageForm form, CancellationToken cancellationToken) =>
        references.AddManualAsync(id, image, form, cancellationToken);

    /// <summary>Accept or reject a picture and record what it shows (kind, relation to the object, note).</summary>
    [HttpPut("{id:guid}/candidates/{candidateId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType<ReferenceSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ReferenceSessionDto> UpdateCandidate(Guid id, Guid candidateId, [FromBody] UpdateCandidateRequest request, CancellationToken cancellationToken) =>
        references.UpdateCandidateAsync(id, candidateId, request, cancellationToken);

    [HttpDelete("{id:guid}/candidates/{candidateId:guid}")]
    public Task<ReferenceSessionDto> RemoveCandidate(Guid id, Guid candidateId, CancellationToken cancellationToken) =>
        references.RemoveCandidateAsync(id, candidateId, cancellationToken);

    /// <summary>The session's input photo: "thumbnail" (JPEG) or "full" (upright PNG).</summary>
    [HttpGet("{id:guid}/input")]
    public async Task<IActionResult> Input(Guid id, [FromQuery] string size = "thumbnail", CancellationToken cancellationToken = default)
    {
        var thumbnail = size.ToLowerInvariant() switch
        {
            "thumbnail" => true,
            "full" => false,
            _ => throw new Services.ApiException(StatusCodes.Status400BadRequest, "invalid_size", "size must be 'thumbnail' or 'full'."),
        };
        return Image(await references.OpenInputAsync(id, thumbnail, cancellationToken));
    }

    /// <summary>Stored preview of a picture (JPEG, longest side at most ReferenceSearch:ThumbnailMaxSide).</summary>
    [HttpGet("{id:guid}/candidates/{candidateId:guid}/thumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, Guid candidateId, CancellationToken cancellationToken) =>
        Image(await references.OpenThumbnailAsync(id, candidateId, cancellationToken));

    /// <summary>Clear the shared, disposable caches (search responses and downloads). Sessions keep their pictures.</summary>
    [HttpDelete("cache")]
    [ProducesResponseType<ClearReferenceCacheDto>(StatusCodes.Status200OK)]
    public Task<ClearReferenceCacheDto> ClearCache(CancellationToken cancellationToken) => references.ClearCacheAsync(cancellationToken);

    /// <summary>Previews never change for a given ID; files are re-encoded by the server, never served as uploaded.</summary>
    private FileStreamResult Image(ReferenceImageFile file)
    {
        Response.Headers.CacheControl = "private, max-age=86400, immutable";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType);
    }
}
