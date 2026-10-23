using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.PartDesign;
using Microsoft.AspNetCore.Mvc;

namespace ImageTo3D.Api.Controllers;

/// <summary>
/// Part-design projects: a generated model broken into individually editable parts with
/// fabrication properties. Lengths are millimetres; coordinates are the viewer's (Y-up).
/// </summary>
[ApiController]
[Route("api/part-designs")]
[Produces("application/json")]
public sealed class PartDesignsController(PartDesignService designs) : ControllerBase
{
    /// <summary>Create a project from a succeeded generation job: copies its mesh and suggests parts.</summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePartDesignRequest request, CancellationToken cancellationToken)
    {
        var project = await designs.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, project);
    }

    /// <summary>Create an empty project (no source mesh) for parts built from stock shapes.</summary>
    [HttpPost("blank")]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateBlank([FromBody] CreateBlankPartDesignRequest request, CancellationToken cancellationToken)
    {
        var project = await designs.CreateBlankAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, project);
    }

    /// <summary>Add a plate, block, rod or tube with exact millimetre sizes (reversible with undo).</summary>
    [HttpPost("{id:guid}/parts/primitive")]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<PartDesignProjectDto> AddPrimitive(Guid id, [FromBody] AddPrimitiveRequest request, CancellationToken cancellationToken) =>
        designs.AddPrimitiveAsync(id, request, cancellationToken);

    [HttpGet]
    public Task<PagedResult<PartDesignSummaryDto>> List([FromQuery] ListJobsQuery query, CancellationToken cancellationToken) =>
        designs.ListAsync(query.Page, query.PageSize, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PartDesignProjectDto> Get(Guid id, CancellationToken cancellationToken) => designs.GetAsync(id, cancellationToken);

    [HttpPut("{id:guid}")]
    [Consumes("application/json")]
    public Task<PartDesignProjectDto> Update(Guid id, [FromBody] UpdatePartDesignRequest request, CancellationToken cancellationToken) =>
        designs.UpdateProjectAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await designs.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Replace a part's editable fields (name, category, review status, manufacturing spec).</summary>
    [HttpPut("{id:guid}/parts/{partId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PartDesignProjectDto> UpdatePart(Guid id, Guid partId, [FromBody] UpdatePartRequest request, CancellationToken cancellationToken) =>
        designs.UpdatePartAsync(id, partId, request, cancellationToken);

    [HttpPost("{id:guid}/parts/{partId:guid}/duplicate")]
    public Task<PartDesignProjectDto> Duplicate(Guid id, Guid partId, CancellationToken cancellationToken) =>
        designs.DuplicatePartAsync(id, partId, cancellationToken);

    /// <summary>Remove a derived part (reversible with undo). The source mesh is never affected.</summary>
    [HttpDelete("{id:guid}/parts/{partId:guid}")]
    public Task<PartDesignProjectDto> RemovePart(Guid id, Guid partId, CancellationToken cancellationToken) =>
        designs.RemovePartAsync(id, partId, cancellationToken);

    /// <summary>Compute a split without saving it: contours, caps and whether both halves are closed.</summary>
    [HttpPost("{id:guid}/parts/{partId:guid}/split/preview")]
    [Consumes("application/json")]
    public Task<SplitPreviewDto> PreviewSplit(Guid id, Guid partId, [FromBody] SplitRequest request, CancellationToken cancellationToken) =>
        designs.PreviewSplitAsync(id, partId, request, cancellationToken);

    /// <summary>Split a part with a plane into two capped parts; the original is kept as superseded.</summary>
    [HttpPost("{id:guid}/parts/{partId:guid}/split")]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<PartDesignProjectDto> Split(Guid id, Guid partId, [FromBody] SplitRequest request, CancellationToken cancellationToken) =>
        designs.SplitAsync(id, partId, request, cancellationToken);

    /// <summary>Relate two active parts (glued, peg_in_hole, tube_over_rod, nested, slot); reversible with undo.</summary>
    [HttpPost("{id:guid}/relations")]
    [Consumes("application/json")]
    [ProducesResponseType<PartDesignProjectDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<PartDesignProjectDto> AddRelation(Guid id, [FromBody] CreateRelationRequest request, CancellationToken cancellationToken) =>
        designs.AddRelationAsync(id, request, cancellationToken);

    [HttpPut("{id:guid}/relations/{relationId:guid}")]
    [Consumes("application/json")]
    public Task<PartDesignProjectDto> UpdateRelation(Guid id, Guid relationId, [FromBody] UpdateRelationRequest request, CancellationToken cancellationToken) =>
        designs.UpdateRelationAsync(id, relationId, request, cancellationToken);

    [HttpDelete("{id:guid}/relations/{relationId:guid}")]
    public Task<PartDesignProjectDto> RemoveRelation(Guid id, Guid relationId, CancellationToken cancellationToken) =>
        designs.RemoveRelationAsync(id, relationId, cancellationToken);

    /// <summary>Assembly validation computed now: relation fits, collisions between all active parts, and geometry quality.</summary>
    [HttpGet("{id:guid}/assembly")]
    [ProducesResponseType<AssemblyReportDto>(StatusCodes.Status200OK)]
    public async Task<AssemblyReportDto> Assembly(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await designs.AssemblyReportAsync(id, cancellationToken);
    }

    [HttpPost("{id:guid}/undo")]
    public Task<PartDesignProjectDto> Undo(Guid id, CancellationToken cancellationToken) => designs.UndoAsync(id, cancellationToken);

    [HttpPost("{id:guid}/redo")]
    public Task<PartDesignProjectDto> Redo(Guid id, CancellationToken cancellationToken) => designs.RedoAsync(id, cancellationToken);

    /// <summary>What the fabrication drawing of a sheet part contains (page, outline, holes) and the checks run on it.</summary>
    [HttpGet("{id:guid}/parts/{partId:guid}/drawing/report")]
    [ProducesResponseType<DrawingReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<DrawingReportDto> DrawingReport(Guid id, Guid partId, CancellationToken cancellationToken) =>
        designs.DrawingReportAsync(id, partId, cancellationToken);

    /// <summary>1:1 fabrication drawing of a sheet part as SVG (millimetre units), vector PDF or DXF (R12, layer CUT = cutting contours).</summary>
    [HttpGet("{id:guid}/parts/{partId:guid}/drawing")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Drawing(Guid id, Guid partId, [FromQuery] string format = "svg", [FromQuery] bool download = false, CancellationToken cancellationToken = default)
    {
        var file = await designs.RenderDrawingAsync(id, partId, format, cancellationToken);
        // Drawings reflect editable metadata (name, thickness, quantity), so never serve a stale copy.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // The SVG contains user-entered text; even opened directly it can run nothing.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        return download ? File(file.Content, file.ContentType, file.FileName) : File(file.Content, file.ContentType);
    }

    /// <summary>Part geometry as binary STL in millimetres, for printing or CAD.</summary>
    [HttpGet("{id:guid}/parts/{partId:guid}/stl")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PartStl(Guid id, Guid partId, CancellationToken cancellationToken) =>
        Download(await designs.PartStlAsync(id, partId, cancellationToken));

    /// <summary>Bill of materials (CSV, UTF-8), one row per active part.</summary>
    [HttpGet("{id:guid}/export/bom")]
    public async Task<IActionResult> Bom(Guid id, CancellationToken cancellationToken) =>
        Download(await designs.BomCsvAsync(id, cancellationToken));

    /// <summary>Rod and tube cut list (CSV, UTF-8), grouped by stock; estimated values are marked.</summary>
    [HttpGet("{id:guid}/export/cut-list")]
    public async Task<IActionResult> CutList(Guid id, CancellationToken cancellationToken) =>
        Download(await designs.CutListCsvAsync(id, cancellationToken));

    /// <summary>The manifest a draft package would have now: files, parts and warnings.</summary>
    [HttpGet("{id:guid}/export/manifest")]
    [ProducesResponseType<ExportManifestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ExportManifestDto> DraftManifest(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await designs.DraftManifestAsync(id, cancellationToken);
    }

    /// <summary>Draft export package (zip), built now and not stored. Drawings are marked "Draft".</summary>
    [HttpGet("{id:guid}/export/package")]
    [Produces("application/zip")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DraftPackage(Guid id, CancellationToken cancellationToken) =>
        Download(await designs.DraftPackageAsync(id, cancellationToken));

    [HttpGet("{id:guid}/releases")]
    public Task<IReadOnlyList<DesignReleaseDto>> Releases(Guid id, CancellationToken cancellationToken) =>
        designs.ListReleasesAsync(id, cancellationToken);

    /// <summary>Build and store the next numbered release; its package never changes afterwards.</summary>
    [HttpPost("{id:guid}/releases")]
    [Consumes("application/json")]
    [ProducesResponseType<DesignReleaseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRelease(Guid id, [FromBody] CreateReleaseRequest request, CancellationToken cancellationToken)
    {
        var release = await designs.CreateReleaseAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(ReleasePackage), new { id, revision = release.Revision }, release);
    }

    /// <summary>What changed between two stored releases: parts added, removed and changed, and relations.</summary>
    [HttpGet("{id:guid}/releases/compare")]
    [ProducesResponseType<ReleaseDiffDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ReleaseDiffDto> CompareReleases(Guid id, [FromQuery] int from, [FromQuery] int to, CancellationToken cancellationToken) =>
        designs.CompareReleasesAsync(id, from, to, cancellationToken);

    /// <summary>The stored package of a release, byte for byte as it was created.</summary>
    [HttpGet("{id:guid}/releases/{revision:int}/package")]
    [Produces("application/zip")]
    public async Task<IActionResult> ReleasePackage(Guid id, int revision, CancellationToken cancellationToken)
    {
        var (content, fileName) = await designs.OpenReleaseAsync(id, revision, cancellationToken);
        Response.Headers.CacheControl = "private, max-age=86400, immutable";
        return File(content, "application/zip", fileName);
    }

    /// <summary>Exports reflect the current editable state, so they are never cached.</summary>
    private FileContentResult Download(PartDesignService.ExportFile file)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Part geometry for the viewer (GLB, metres). Immutable per part ID, so it is cacheable.</summary>
    [HttpGet("{id:guid}/parts/{partId:guid}/geometry")]
    [Produces("model/gltf-binary")]
    public async Task<IActionResult> Geometry(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var stream = await designs.OpenPartGeometryAsync(id, partId, cancellationToken);
        Response.Headers.CacheControl = "private, max-age=86400, immutable";
        return File(stream, "model/gltf-binary");
    }
}
