using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.Photogrammetry;
using Microsoft.AspNetCore.Mvc;

namespace ImageTo3D.Api.Controllers;

/// <summary>
/// Multi-photo reconstruction with the optional Meshroom engine: create a photo set, add photos,
/// start, follow, cancel, then calibrate the scale and download. Only the pipeline name (from the
/// installation's own list) reaches Meshroom's command line.
/// </summary>
[ApiController]
[Route("api/photogrammetry")]
[Produces("application/json")]
public sealed class PhotogrammetryController(PhotogrammetryService photogrammetry) : ControllerBase
{
    /// <summary>Whether Meshroom is usable, its pipelines and default, GPU, and the photo limits.</summary>
    [HttpGet("engine")]
    public Task<PhotogrammetryEngineDto> Engine(CancellationToken cancellationToken) => photogrammetry.EngineAsync(cancellationToken);

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<PhotogrammetryJobDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreatePhotogrammetryRequest request, CancellationToken cancellationToken)
    {
        var job = await photogrammetry.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet]
    public Task<PagedResult<PhotogrammetrySummaryDto>> List([FromQuery] ListJobsQuery query, CancellationToken cancellationToken) =>
        photogrammetry.ListAsync(query.Page, query.PageSize, cancellationToken);

    [HttpGet("{id:guid}")]
    public Task<PhotogrammetryJobDto> Get(Guid id, CancellationToken cancellationToken) => photogrammetry.GetAsync(id, cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await photogrammetry.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Adds photos (multipart field <c>photos</c>, JPEG or PNG) to a draft; a batch is refused as a whole if one file is unreadable.</summary>
    [HttpPost("{id:guid}/photos")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(1024L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1024L * 1024 * 1024)]
    public async Task<PhotogrammetryJobDto> AddPhotos(Guid id, CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        return await photogrammetry.AddPhotosAsync(id, form.Files.GetFiles("photos"), cancellationToken);
    }

    [HttpDelete("{id:guid}/photos/{photoId:guid}")]
    public Task<PhotogrammetryJobDto> RemovePhoto(Guid id, Guid photoId, CancellationToken cancellationToken) =>
        photogrammetry.RemovePhotoAsync(id, photoId, cancellationToken);

    [HttpGet("{id:guid}/photos/{photoId:guid}/thumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        var (content, type) = await photogrammetry.ThumbnailAsync(id, photoId, cancellationToken);
        return File(content, type);
    }

    /// <summary>Queues the reconstruction. 409 when a blocker remains (fewer than 3 photos), 503 when Meshroom is not available.</summary>
    [HttpPost("{id:guid}/start")]
    [Consumes("application/json")]
    public Task<PhotogrammetryJobDto> Start(Guid id, [FromBody] StartPhotogrammetryRequest request, CancellationToken cancellationToken) =>
        photogrammetry.StartAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    public Task<PhotogrammetryJobDto> Cancel(Guid id, CancellationToken cancellationToken) => photogrammetry.CancelAsync(id, cancellationToken);

    /// <summary>Sets one measured dimension; the result is rescaled uniformly and marked calibrated.</summary>
    [HttpPut("{id:guid}/scale")]
    [Consumes("application/json")]
    public Task<PhotogrammetryJobDto> Scale(Guid id, [FromBody] PhotogrammetryScaleRequest request, CancellationToken cancellationToken) =>
        photogrammetry.ScaleAsync(id, request, cancellationToken);

    /// <summary>The scaled GLB inline for the viewer.</summary>
    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken cancellationToken)
    {
        var file = await photogrammetry.OpenFileAsync(id, "glb", cancellationToken);
        return File(file.Content, file.ContentType);
    }

    /// <summary><c>glb</c> (metres), <c>stl</c> (mm, Z-up), <c>zip</c> (Meshroom's textured OBJ, MTL and textures) or <c>log</c>.</summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string format = "glb", CancellationToken cancellationToken = default)
    {
        var file = await photogrammetry.OpenFileAsync(id, format, cancellationToken);
        return File(file.Content, file.ContentType, file.DownloadName);
    }
}
