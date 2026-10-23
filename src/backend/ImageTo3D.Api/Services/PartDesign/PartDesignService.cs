using System.Text.Json;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Drawing;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// Part-design projects: creation from a generated model (copy of the source, deterministic part
/// analysis), part editing with a reversible history, and plane splitting. All derived geometry is
/// in millimetres in design coordinates (the job's scaled model: Y-up, centred on X/Z, resting on
/// Y = 0) and is written once per part, never modified; edits only change metadata.
/// </summary>
public sealed partial class PartDesignService(
    AppDbContext db,
    IArtifactStore jobStore,
    IDesignArtifactStore store,
    IOwnerAccessor owner,
    IOptions<PartDesignOptions> options,
    TimeProvider time,
    ILogger<PartDesignService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string CoordinateSystem = "millimetres, Y-up, centred on X/Z, resting on Y = 0 (same as the model viewer)";

    // ---- Projects ----

    public async Task<PartDesignProjectDto> CreateAsync(CreatePartDesignRequest request, CancellationToken cancellationToken)
    {
        var ownerHash = owner.OwnerKeyHash;
        await EnsureProjectCapacityAsync(cancellationToken);

        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == request.SourceJobId && j.OwnerKeyHash == ownerHash, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status404NotFound, "source_not_found", "Source model not found.");
        if (job.Status != JobStatus.Succeeded || job.MmPerModelUnit is not { } mmPerUnit)
            throw new ApiException(StatusCodes.Status409Conflict, "not_ready", "A part design can only be created from a successfully generated model.");

        var raw = await jobStore.ReadAllAsync(job.Id, ArtifactNames.FileName(ArtifactKind.RawMesh), cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The generated mesh is no longer available.");

        IReadOnlyList<GlbNodeMesh> nodes;
        try
        {
            nodes = GlbReader.ReadNodes(raw);
        }
        catch (InvalidDataException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "unsupported_source", "The model's mesh cannot be read: " + ex.Message);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var project = new PartDesignProject
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = ownerHash,
            Name = Truncate(request.Name?.Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(job.OriginalFileName), 100),
            SourceKind = DesignSourceKinds.GenerationJob,
            SourceJobId = job.Id,
            SourceFileName = job.OriginalFileName,
            MmPerModelUnit = mmPerUnit,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var issues = new List<GeometryIssue>
        {
            new("scale_estimated", IssueSeverity.Info, $"Dimensions come from the generated model at its current scale (longest side {job.SizeMm?.MaxComponent:0.#} mm). They are estimates until you confirm measurements of the real part."),
        };

        await AnalyseAndSaveAsync(project, nodes, (DesignArtifactNames.SourceMesh, raw), issues, cancellationToken);
        logger.LogInformation("Created part design {ProjectId} from job {JobId} with {PartCount} parts", project.Id, job.Id, project.Parts.Count);
        return ToDto(project);
    }

    /// <summary>An existing model accepted in a reference search, as the reconstruction step hands it over.</summary>
    /// <param name="Format">"glb" (metres by specification) or "stl" (no unit; millimetres assumed, Z-up converted to Y-up).</param>
    public sealed record ReusedModel(byte[] Bytes, string Format, string Title, Guid SessionId, Guid ModelId, string License, string Attribution, string? ScaleStatus);

    /// <summary>
    /// Creates a project from a reused model, the same way as from a generated one (nodes and
    /// connected pieces become parts). The file is kept byte for byte as the immutable source.
    /// </summary>
    public async Task<PartDesignProjectDto> CreateFromModelAsync(ReusedModel model, string? name, CancellationToken cancellationToken)
    {
        await EnsureProjectCapacityAsync(cancellationToken);
        IReadOnlyList<GlbNodeMesh> nodes;
        try
        {
            nodes = model.Format == "glb"
                ? GlbReader.ReadNodes(model.Bytes)
                : [new GlbNodeMesh(0, null, ZUpToYUp(StlReader.ReadBinary(model.Bytes)))];
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or JsonException)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "unsupported_source", "The model's mesh cannot be read: " + ex.Message);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var fileName = Truncate(SafeFileStem(model.Title) + "." + model.Format, 128);
        var project = new PartDesignProject
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = owner.OwnerKeyHash,
            Name = Truncate(Clean(name) ?? model.Title, 100),
            SourceKind = DesignSourceKinds.ReferenceModel,
            SourceReferenceSessionId = model.SessionId,
            SourceModelCandidateId = model.ModelId,
            SourceLicense = Truncate(model.License, 32),
            SourceAttribution = Truncate(model.Attribution, 700),
            SourceFileName = fileName,
            MmPerModelUnit = model.Format == "glb" ? 1000 : 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var issues = new List<GeometryIssue>
        {
            model.Format == "glb"
                ? new("scale_from_file", IssueSeverity.Info, "Dimensions come from the file's declared units (glTF is in metres). They are estimates until you confirm measurements of the real part.")
                : new("scale_assumed_mm", IssueSeverity.Warning, "STL files carry no unit: millimetres were assumed. Check the overall size before fabrication."),
            new("reused_model", IssueSeverity.Info, $"Reused existing model: {model.Attribution}. It may not match the physical object exactly."),
        };
        if (model.ScaleStatus == "suspicious")
            issues.Add(new("scale_suspicious", IssueSeverity.Warning, "The declared size is implausible for a model part; the file may use other units."));

        var sourceFile = model.Format == "glb" ? DesignArtifactNames.SourceMesh : DesignArtifactNames.SourceStl;
        await AnalyseAndSaveAsync(project, nodes, (sourceFile, model.Bytes), issues, cancellationToken);
        logger.LogInformation("Created part design {ProjectId} from reused model {ModelId} with {PartCount} parts", project.Id, model.ModelId, project.Parts.Count);
        return ToDto(project);
    }

    /// <summary>A project without a source mesh, for parts built from stock shapes.</summary>
    public async Task<PartDesignProjectDto> CreateBlankAsync(CreateBlankPartDesignRequest request, CancellationToken cancellationToken)
    {
        await EnsureProjectCapacityAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var project = new PartDesignProject
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = owner.OwnerKeyHash,
            Name = Truncate(Clean(request.Name) ?? "Hand-built design", 100),
            SourceKind = DesignSourceKinds.None,
            SourceFileName = "",
            MmPerModelUnit = 1,
            CreatedAt = now,
            UpdatedAt = now,
            IssuesJson = JsonSerializer.Serialize(new[]
            {
                new IssueDto("no_source", "info", "No source model: add parts from stock shapes with the sizes you want to build.", "analysis"),
            }, Json),
        };

        db.PartDesigns.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created empty part design {ProjectId}", project.Id);
        return ToDto(project);
    }

    /// <summary>Adds a plate, block, rod or tube with exact sizes; reversible like any other edit.</summary>
    public async Task<PartDesignProjectDto> AddPrimitiveAsync(Guid id, AddPrimitiveRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        EnsureCapacity(project, 1);
        var kind = request.Kind!;
        Mesh mesh;
        try
        {
            mesh = PrimitiveBuilder.Build(new PrimitiveSpec(
                kind,
                request.LengthMm,
                request.WidthMm,
                request.HeightMm,
                request.ThicknessMm,
                request.DiameterMm,
                request.OuterDiameterMm,
                request.InnerDiameterMm,
                request.Axis is { Length: 1 } axis ? axis[0] : request.Axis is null ? '\0' : '?',
                request.Center is { Length: 3 } c ? new Vector3D(c[0], c[1], c[2]) : default));
        }
        catch (InvalidPrimitiveException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_primitive", ex.Message);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var files = new List<(string, byte[])>();
        var label = kind switch
        {
            PrimitiveKinds.Plate => $"Plate {request.LengthMm:0.##} x {request.WidthMm:0.##} x {request.ThicknessMm:0.##}",
            PrimitiveKinds.Box => $"Block {request.LengthMm:0.##} x {request.WidthMm:0.##} x {request.HeightMm:0.##}",
            PrimitiveKinds.Rod => $"Rod Ø{request.DiameterMm:0.##} x {request.LengthMm:0.##}",
            _ => $"Tube Ø{request.OuterDiameterMm:0.##}/{request.InnerDiameterMm:0.##} x {request.LengthMm:0.##}",
        };
        var part = CreatePart(project.Id, mesh, Truncate(Clean(request.Name) ?? label, 100), PartOrigins.Primitive, null, null, now, files)
                   ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "no_usable_geometry", "The shape has no usable geometry.");

        // The sizes were entered, not measured: they are the specification of the stock to cut.
        part.Category = PrimitiveKinds.Category(kind);
        part.ReviewStatus = ReviewStatuses.UserConfirmed;
        part.SortOrder = NextSortOrder(project);
        part.Material = Clean(request.Material) ?? (kind == PrimitiveKinds.Box ? null : "Polystyrene (PS)");
        part.ThicknessMm = kind == PrimitiveKinds.Plate ? request.ThicknessMm : null;
        part.DiameterMm = kind == PrimitiveKinds.Rod ? request.DiameterMm : null;
        part.OuterDiameterMm = kind == PrimitiveKinds.Tube ? request.OuterDiameterMm : null;
        part.InnerDiameterMm = kind == PrimitiveKinds.Tube ? request.InnerDiameterMm : null;
        part.CutLengthMm = kind is PrimitiveKinds.Rod or PrimitiveKinds.Tube ? request.LengthMm : null;
        part.NeedsFurtherModelling = kind == PrimitiveKinds.Box;
        project.Parts.Add(part);
        db.DesignParts.Add(part);

        foreach (var (name, content) in files)
            await store.SaveAsync(project.Id, name, content, cancellationToken);
        await RecordAsync(project, "add_primitive", $"Added '{Truncate(part.Name, 60)}'", [new PartChange(part.Id, null, Snapshot(part))], cancellationToken);
        return ToDto(project);
    }

    private async Task EnsureProjectCapacityAsync(CancellationToken cancellationToken)
    {
        var max = options.Value.MaxProjectsPerOwner;
        if (await db.PartDesigns.CountAsync(p => p.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken) >= max)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_projects", $"You already have {max} part-design projects. Delete one first.");
    }

    private static Mesh ZUpToYUp(Mesh mesh)
    {
        var p = (float[])mesh.Positions.Clone();
        for (var i = 0; i < p.Length; i += 3)
            (p[i + 1], p[i + 2]) = (p[i + 2], -p[i + 1]);
        return new Mesh(p, mesh.Indices, mesh.Colors);
    }

    /// <summary>Splits the source into parts, then writes the files and the project (files are removed again if saving fails).</summary>
    private async Task AnalyseAndSaveAsync(PartDesignProject project, IReadOnlyList<GlbNodeMesh> nodes, (string Name, byte[] Content) source, List<GeometryIssue> issues, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var totalTriangles = nodes.Sum(n => (long)n.Mesh.TriangleCount);
        if (totalTriangles == 0)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "no_usable_geometry", "The model has no triangles.");
        if (totalTriangles > o.MaxSourceTriangles)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "source_too_large", $"The model has {totalTriangles:N0} triangles; the limit for part design is {o.MaxSourceTriangles:N0}.");

        var now = project.CreatedAt;
        var baseName = Path.GetFileNameWithoutExtension(project.SourceFileName);
        var mmPerUnit = project.MmPerModelUnit;
        var files = new List<(string Name, byte[] Content)> { source };
        var sort = 0;
        foreach (var piece in Decompose(nodes, mmPerUnit, baseName, o, issues))
        {
            var part = CreatePart(project.Id, piece.Mesh, piece.Name, piece.Origin, piece.NodeName, null, now, files);
            if (part is null)
            {
                issues.Add(new GeometryIssue("ignored_piece", IssueSeverity.Warning, $"'{piece.Name}' has no usable geometry (flat or degenerate) and was skipped."));
                continue;
            }

            part.SortOrder = sort++;
            project.Parts.Add(part);
        }

        if (project.Parts.Count == 0)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "no_usable_geometry", "No usable part could be extracted from the model.");

        project.IssuesJson = JsonSerializer.Serialize(issues.Select(i => IssueDto.From(i, "analysis")), Json);

        foreach (var (name, content) in files)
            await store.SaveAsync(project.Id, name, content, cancellationToken);
        db.PartDesigns.Add(project);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteProjectAsync(project.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<PagedResult<PartDesignSummaryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PartDesigns.AsNoTracking().Where(p => p.OwnerKeyHash == owner.OwnerKeyHash);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PartDesignSummaryDto(p.Id, p.Name, p.SourceJobId, p.SourceKind, p.SourceFileName, p.Parts.Count(x => x.State == DesignPartState.Active), p.CreatedAt, p.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<PartDesignSummaryDto>(items.Select(i => i with { CreatedAt = Utc(i.CreatedAt), UpdatedAt = Utc(i.UpdatedAt) }).ToList(), page, pageSize, total);
    }

    public async Task<PartDesignProjectDto> GetAsync(Guid id, CancellationToken cancellationToken) => ToDto(await LoadAsync(id, cancellationToken));

    public async Task<PartDesignProjectDto> UpdateProjectAsync(Guid id, UpdatePartDesignRequest request, CancellationToken cancellationToken)
    {
        if (request.FitAllowanceMinMm > request.FitAllowanceMaxMm)
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_fit_limits", "The minimum fit allowance must not exceed the maximum.");
        var project = await LoadAsync(id, cancellationToken);
        project.Name = request.Name!.Trim();
        project.FitAllowanceMinMm = request.FitAllowanceMinMm;
        project.FitAllowanceMaxMm = request.FitAllowanceMaxMm;
        project.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(project);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        db.PartDesigns.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteProjectAsync(project.Id, cancellationToken);
        logger.LogInformation("Deleted part design {ProjectId}", project.Id);
    }

    // ---- Parts ----

    public async Task<PartDesignProjectDto> UpdatePartAsync(Guid id, Guid partId, UpdatePartRequest request, CancellationToken cancellationToken)
    {
        if (!PartCategories.All.Contains(request.Category!))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_category", $"Category must be one of: {string.Join(", ", PartCategories.All)}.");
        if (request.ReviewStatus is not (ReviewStatuses.NeedsReview or ReviewStatuses.UserConfirmed))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_review_status", "Review status must be 'needs_review' or 'user_confirmed'.");

        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var before = Snapshot(part);
        var after = before with
        {
            Name = request.Name!.Trim(),
            Category = request.Category!,
            ReviewStatus = request.ReviewStatus,
            Material = Clean(request.Material),
            ManufacturingMethod = Clean(request.ManufacturingMethod),
            Quantity = request.Quantity,
            ThicknessMm = request.ThicknessMm,
            DiameterMm = request.DiameterMm,
            OuterDiameterMm = request.OuterDiameterMm,
            InnerDiameterMm = request.InnerDiameterMm,
            CutLengthMm = request.CutLengthMm,
            CutAngleDeg = request.CutAngleDeg,
            FitAllowanceMm = request.FitAllowanceMm,
            ConfirmedLengthMm = request.ConfirmedLengthMm,
            ConfirmedWidthMm = request.ConfirmedWidthMm,
            ConfirmedHeightMm = request.ConfirmedHeightMm,
            StockDescription = Clean(request.StockDescription),
            StockSource = Clean(request.StockSource),
            NeedsFurtherModelling = request.NeedsFurtherModelling,
            Notes = Clean(request.Notes),
        };

        if (after == before)
            return ToDto(project);

        Apply(part, after);
        await RecordAsync(project, "update", $"Edited '{Truncate(part.Name, 60)}'", [new PartChange(part.Id, before, after)], cancellationToken);
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> DuplicatePartAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var source = ActivePart(project, partId);
        EnsureCapacity(project, 1);

        var copy = new DesignPart
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ParentPartId = source.Id,
            Origin = PartOrigins.Duplicate,
            SourceNodeName = source.SourceNodeName,
            GeometryFile = source.GeometryFile, // geometry is immutable, so sharing the file is safe
            GeometryReportJson = source.GeometryReportJson,
            SuggestedCategory = source.SuggestedCategory,
            SuggestionConfidence = source.SuggestionConfidence,
            SuggestionReasonsJson = source.SuggestionReasonsJson,
            EstimatedLengthMm = source.EstimatedLengthMm,
            EstimatedWidthMm = source.EstimatedWidthMm,
            EstimatedThicknessMm = source.EstimatedThicknessMm,
            EstimatedOuterDiameterMm = source.EstimatedOuterDiameterMm,
            EstimatedInnerDiameterMm = source.EstimatedInnerDiameterMm,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            Name = source.Name,
            Category = source.Category,
        };
        Apply(copy, Snapshot(source) with { Name = Truncate(source.Name + " (copy)", 100), SortOrder = NextSortOrder(project), State = DesignPartState.Active });
        // Explicit Add: a new entity with a preset key reached through a tracked navigation would be treated as existing.
        project.Parts.Add(copy);
        db.DesignParts.Add(copy);

        await RecordAsync(project, "duplicate", $"Duplicated '{Truncate(source.Name, 60)}'", [new PartChange(copy.Id, null, Snapshot(copy))], cancellationToken);
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> RemovePartAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var before = Snapshot(part);
        var after = before with { State = DesignPartState.Removed };
        Apply(part, after);
        await RecordAsync(project, "delete", $"Deleted '{Truncate(part.Name, 60)}'", [new PartChange(part.Id, before, after)], cancellationToken, DetachRelations(project, part.Id));
        return ToDto(project);
    }

    public async Task<SplitPreviewDto> PreviewSplitAsync(Guid id, Guid partId, SplitRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var result = await SplitGeometryAsync(project, part, request, cancellationToken);
        return new SplitPreviewDto(
            result.IsCompleteSolidSplit,
            result.ClosedContourCount,
            result.OpenContourCount,
            Math.Round(result.ContourLength, 4),
            SideDto(result.Below),
            SideDto(result.Above),
            result.Issues.Select(i => IssueDto.From(i, "split")).ToList());
    }

    public async Task<PartDesignProjectDto> SplitAsync(Guid id, Guid partId, SplitRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        EnsureCapacity(project, 2);
        var result = await SplitGeometryAsync(project, part, request, cancellationToken);

        if (result.Below is null || result.Above is null)
            throw new ApiException(StatusCodes.Status400BadRequest, "plane_misses_part", "The cutting plane does not pass through the part.");
        if (!result.IsCompleteSolidSplit && !request.AllowOpenResult)
        {
            var reasons = string.Join(" ", result.Issues.Where(i => i.Severity != IssueSeverity.Info).Select(i => i.Message));
            throw new ApiException(StatusCodes.Status409Conflict, "split_not_closed", "The split does not produce two closed solids. " + reasons + " Set allowOpenResult to keep the open halves anyway.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var files = new List<(string Name, byte[] Content)>();
        var changes = new List<PartChange>();
        var sort = NextSortOrder(project);
        foreach (var (half, label) in new[] { (result.Below, "A"), (result.Above, "B") })
        {
            var child = CreatePart(project.Id, half.Mesh, Truncate($"{part.Name} · {label}", 100), PartOrigins.Split, part.SourceNodeName, part.Id, now, files)
                        ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "split_degenerate", "One side of the split has no usable geometry.");
            child.SortOrder = sort++;
            child.Material = part.Material;
            child.Quantity = part.Quantity;
            project.Parts.Add(child);
            db.DesignParts.Add(child);
            changes.Add(new PartChange(child.Id, null, Snapshot(child)));
        }

        var before = Snapshot(part);
        var after = before with { State = DesignPartState.Superseded };
        Apply(part, after);
        changes.Insert(0, new PartChange(part.Id, before, after));

        foreach (var (name, content) in files)
            await store.SaveAsync(project.Id, name, content, cancellationToken);
        // The halves are new parts: joints of the original no longer say which half they meet, so
        // they are removed with the split (and come back with undo).
        await RecordAsync(project, "split", $"Split '{Truncate(part.Name, 60)}'", changes, cancellationToken, DetachRelations(project, part.Id));
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> UndoAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var operation = project.Operations.Where(o => !o.IsUndone).MaxBy(o => o.Sequence)
                        ?? throw new ApiException(StatusCodes.Status409Conflict, "nothing_to_undo", "There is nothing to undo.");
        foreach (var change in Changes(operation))
        {
            var part = project.Parts.Single(p => p.Id == change.PartId);
            if (change.Before is null)
                part.State = DesignPartState.Removed;
            else
                Apply(part, change.Before);
        }

        foreach (var change in RelationChanges(operation))
        {
            var relation = project.Relations.Single(r => r.Id == change.RelationId);
            if (change.Before is null)
                relation.IsRemoved = true;
            else
                Apply(relation, change.Before);
        }

        operation.IsUndone = true;
        project.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> RedoAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var operation = project.Operations.Where(o => o.IsUndone).MinBy(o => o.Sequence)
                        ?? throw new ApiException(StatusCodes.Status409Conflict, "nothing_to_redo", "There is nothing to redo.");
        foreach (var change in Changes(operation))
            Apply(project.Parts.Single(p => p.Id == change.PartId), change.After);
        foreach (var change in RelationChanges(operation))
            Apply(project.Relations.Single(r => r.Id == change.RelationId), change.After);

        operation.IsUndone = false;
        project.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(project);
    }

    /// <summary>Part geometry for the viewer: GLB in metres, design coordinates.</summary>
    public async Task<Stream> OpenPartGeometryAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var part = await db.DesignParts.AsNoTracking()
                       .Where(p => p.Id == partId && p.ProjectId == id && db.PartDesigns.Any(x => x.Id == id && x.OwnerKeyHash == owner.OwnerKeyHash))
                       .FirstOrDefaultAsync(cancellationToken)
                   ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Part not found.");
        return await store.OpenReadAsync(id, part.GeometryFile, cancellationToken)
               ?? throw new ApiException(StatusCodes.Status404NotFound, "artifact_missing", "The part geometry is not available.");
    }

    // ---- Fabrication drawings ----

    public sealed record DrawingFile(byte[] Content, string ContentType, string FileName);

    /// <summary>Builds the drawing and reports what it contains and every check, without rendering a file.</summary>
    public async Task<DrawingReportDto> DrawingReportAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var (_, part, outline, drawing, thickness) = await BuildDrawingAsync(id, partId, cancellationToken);
        var issues = outline.Issues.Concat(drawing.Issues).Select(i => IssueDto.From(i, "drawing")).ToList();
        if (part.ThicknessMm is { } specified && Math.Abs(specified - outline.MeasuredThicknessMm) > Math.Max(0.2, specified * 0.25))
        {
            issues.Add(new IssueDto("thickness_differs", "info",
                $"The specified thickness ({specified:0.##} mm) differs from the model's ({outline.MeasuredThicknessMm:0.##} mm); the drawing uses the specified value.", "drawing"));
        }

        return new DrawingReportDto(
            drawing.Document.Page.Name,
            Round(drawing.Document.Page.WidthMm),
            Round(drawing.Document.Page.HeightMm),
            "1:1",
            Round(outline.WidthMm),
            Round(outline.HeightMm),
            thickness,
            part.ThicknessMm is null ? "estimated" : "specified",
            Round(outline.MeasuredThicknessMm),
            outline.Outers.Count,
            outline.Holes.Select((h, i) => new DrawingHoleDto($"H{i + 1}", Round(h.Centre.X), Round(h.Centre.Y), h.DiameterMm is { } d ? Round(d) : null, Round(h.WidthMm), Round(h.HeightMm))).ToList(),
            issues,
            DrawingFormats);
    }

    public async Task<DrawingFile> RenderDrawingAsync(Guid id, Guid partId, string format, CancellationToken cancellationToken)
    {
        var kind = format.ToLowerInvariant();
        if (!DrawingFormats.Contains(kind))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_format", "format must be 'svg', 'pdf' or 'dxf'.");

        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var outline = await LoadOutlineAsync(project, part, cancellationToken);
        var name = $"{PartFileStem(part)}-sheet.{kind}";
        if (kind == "dxf")
            return new DrawingFile(Dxf(part, outline, SheetDrawingInfo.Draft), "image/vnd.dxf", name);

        var (drawing, _) = BuildDrawing(project, part, outline, SheetDrawingInfo.Draft);
        return kind == "svg"
            ? new DrawingFile(System.Text.Encoding.UTF8.GetBytes(SvgDrawingWriter.Write(drawing.Document)), "image/svg+xml", name)
            : new DrawingFile(PdfDrawingWriter.Write(drawing.Document), "application/pdf", name);
    }

    private static readonly string[] DrawingFormats = ["svg", "pdf", "dxf"];

    private async Task<(PartDesignProject Project, DesignPart Part, PlanarOutline Outline, SheetDrawing Drawing, double ThicknessMm)> BuildDrawingAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var outline = await LoadOutlineAsync(project, part, cancellationToken);
        var (drawing, thickness) = BuildDrawing(project, part, outline, SheetDrawingInfo.Draft);
        return (project, part, outline, drawing, thickness);
    }

    /// <summary>Cutting outline of a sheet part; refuses other categories and parts that are not flat.</summary>
    private async Task<PlanarOutline> LoadOutlineAsync(PartDesignProject project, DesignPart part, CancellationToken cancellationToken)
    {
        if (part.Category != PartCategories.Sheet)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "drawing_unsupported",
                "Fabrication drawings are available for sheet parts. Set the category to Sheet if this part is cut from flat styrene; rods and tubes are listed in the cut list instead.");
        }

        var glb = await store.ReadAllAsync(project.Id, part.GeometryFile, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The part geometry is not available.");
        var outline = PlanarOutlineExtractor.Extract(MeshOperations.Scale(GlbReader.Read(glb), 1000));
        if (!outline.IsUsable)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "outline_failed",
                "No cutting outline can be made for this part: " + string.Join(" ", outline.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
        }

        return outline;
    }

    private static double SheetThickness(DesignPart part, PlanarOutline outline) => part.ThicknessMm ?? Round(outline.MeasuredThicknessMm);

    private static byte[] Dxf(DesignPart part, PlanarOutline outline, string revision) =>
        System.Text.Encoding.ASCII.GetBytes(DxfWriter.Write(outline, new DxfInfo(part.Name, part.Id, part.Material, SheetThickness(part, outline), part.Quantity, revision)));

    private (SheetDrawing Drawing, double ThicknessMm) BuildDrawing(PartDesignProject project, DesignPart part, PlanarOutline outline, string revision)
    {
        var thickness = SheetThickness(part, outline);
        var confirmed = new[] { ("L", part.ConfirmedLengthMm), ("W", part.ConfirmedWidthMm), ("H", part.ConfirmedHeightMm) }
            .Where(c => c.Item2 is not null)
            .Select(c => $"{c.Item1} {c.Item2:0.##} mm")
            .ToList();
        var note = confirmed.Count > 0
            ? $"Outline from the model at its estimated scale; your measurements: {string.Join(", ", confirmed)}. Compare before cutting."
            : "From the generated model at its estimated scale. Confirm on the real part before cutting.";
        var info = new SheetDrawingInfo(part.Name, part.Id, project.Name, part.Material, thickness, part.ThicknessMm is not null, part.Quantity, note, time.GetUtcNow().UtcDateTime, revision);

        SheetDrawing drawing;
        try
        {
            drawing = SheetDrawingBuilder.Build(outline, info);
        }
        catch (DrawingTooLargeException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "part_too_large_for_page", ex.Message);
        }

        // The checks guard the builder itself: a failure here is a bug, never something to hand to a cutter.
        if (drawing.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error) is { } failed)
        {
            logger.LogError("Drawing check failed for part {PartId}: {Code} {Message}", part.Id, failed.Code, failed.Message);
            throw new InvalidOperationException("Drawing check failed: " + failed.Code);
        }

        return (drawing, thickness);
    }

    /// <summary>Readable and unique per part: the sanitised name plus the first 8 hex digits of the ID.</summary>
    private static string PartFileStem(DesignPart part) => $"{SafeFileStem(part.Name)}-{part.Id.ToString("N")[..8]}";

    private static string SafeFileStem(string name)
    {
        var stem = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (stem.Contains("--", StringComparison.Ordinal))
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        return stem.Length == 0 ? "part" : stem.Length > 40 ? stem[..40] : stem;
    }

    // ---- Analysis ----

    private sealed record Piece(Mesh Mesh, string Name, string Origin, string? NodeName);

    /// <summary>
    /// Nodes, then connected components within each node, in design millimetres. Pieces too small
    /// to matter, or beyond the part limit, are merged into one "fragments" piece.
    /// </summary>
    private static List<Piece> Decompose(IReadOnlyList<GlbNodeMesh> nodes, double mmPerUnit, string baseName, PartDesignOptions o, List<GeometryIssue> issues)
    {
        // Same transform as the job's exported model: centred on X/Z, resting on Y = 0, then scaled.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxZ = double.MinValue;
        foreach (var node in nodes.Where(n => n.Mesh.VertexCount > 0))
        {
            var b = MeshOperations.ComputeBounds(node.Mesh);
            minX = Math.Min(minX, b.Min.X);
            minY = Math.Min(minY, b.Min.Y);
            minZ = Math.Min(minZ, b.Min.Z);
            maxX = Math.Max(maxX, b.Max.X);
            maxZ = Math.Max(maxZ, b.Max.Z);
        }

        var translation = new Vector3D(-(minX + maxX) / 2 * mmPerUnit, -minY * mmPerUnit, -(minZ + maxZ) / 2 * mmPerUnit);
        var candidates = new List<Piece>();
        var nodeNumber = 0;
        foreach (var node in nodes)
        {
            nodeNumber++;
            if (node.Mesh.TriangleCount == 0)
                continue;
            var mm = MeshOperations.Transform(node.Mesh, [mmPerUnit, 0, 0, 0, mmPerUnit, 0, 0, 0, mmPerUnit], translation);
            var welded = MeshTopology.Weld(mm, MeshTopology.DefaultWeldTolerance(mm));
            // Single-node files (TripoSR, SF3D) name their only node generically ("model"); the upload name says more.
            var nodeLabel = nodes.Count == 1 ? Truncate(baseName, 60) : node.Name is { Length: > 0 } name ? Truncate(name, 60) : $"Node {nodeNumber}";
            var components = MeshTopology.ConnectedComponents(welded);
            if (components.Count == 1)
            {
                candidates.Add(new Piece(components[0], nodeLabel, PartOrigins.Node, node.Name));
                continue;
            }

            for (var c = 0; c < components.Count; c++)
                candidates.Add(new Piece(components[c], $"{nodeLabel} · piece {c + 1}", PartOrigins.Component, node.Name));
        }

        var total = candidates.Sum(c => (long)c.Mesh.TriangleCount);
        var ordered = candidates.OrderByDescending(c => c.Mesh.TriangleCount).ToList();
        var kept = new List<Piece>();
        var fragments = new List<Piece>();
        foreach (var piece in ordered)
        {
            var small = piece.Mesh.TriangleCount < total * o.FragmentTriangleFraction;
            if (kept.Count == 0 || (!small && kept.Count < o.MaxAnalysedParts - 1))
                kept.Add(piece);
            else
                fragments.Add(piece);
        }

        if (fragments.Count == 1 && kept.Count < o.MaxAnalysedParts)
        {
            kept.Add(fragments[0]);
        }
        else if (fragments.Count > 1)
        {
            kept.Add(new Piece(Merge(fragments.Select(f => f.Mesh).ToList()), $"Small fragments ({fragments.Count} pieces)", PartOrigins.Fragments, null));
            issues.Add(new GeometryIssue(
                "fragments_grouped",
                IssueSeverity.Info,
                $"{fragments.Count} small or excess pieces were grouped into one part. Generated meshes often contain floating fragments; delete the group if it is noise."));
        }

        return kept;
    }

    private DesignPart? CreatePart(Guid projectId, Mesh mm, string name, string origin, string? nodeName, Guid? parent, DateTime now, List<(string, byte[])> files)
    {
        byte[] glb;
        try
        {
            glb = GlbWriter.Write(MeshOperations.Scale(mm, 0.001), "Part design geometry (derived, not the source). Units: metres.");
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var report = PartAnalyzer.Measure(mm);
        var suggestion = PartAnalyzer.Suggest(mm, report);
        var id = Guid.NewGuid();
        var file = DesignArtifactNames.PartGeometry(id);
        files.Add((file, glb));
        return new DesignPart
        {
            Id = id,
            ProjectId = projectId,
            ParentPartId = parent,
            Origin = origin,
            SourceNodeName = nodeName is null ? null : Truncate(nodeName, 100),
            GeometryFile = file,
            GeometryReportJson = JsonSerializer.Serialize(ReportDto(report), Json),
            SuggestedCategory = suggestion.Category,
            SuggestionConfidence = suggestion.Confidence,
            SuggestionReasonsJson = JsonSerializer.Serialize(suggestion.Reasons, Json),
            EstimatedLengthMm = Round(suggestion.LengthMm),
            EstimatedWidthMm = Round(suggestion.WidthMm),
            EstimatedThicknessMm = Round(suggestion.ThicknessMm),
            EstimatedOuterDiameterMm = suggestion.OuterDiameterMm is { } od ? Round(od) : null,
            EstimatedInnerDiameterMm = suggestion.InnerDiameterMm is { } id2 ? Round(id2) : null,
            CreatedAt = now,
            Name = name,
            // The suggestion is advisory: it seeds the category, but review stays pending until the user confirms.
            Category = suggestion.Category,
            ReviewStatus = ReviewStatuses.NeedsReview,
        };
    }

    private async Task<PlaneSplitResult> SplitGeometryAsync(PartDesignProject project, DesignPart part, SplitRequest request, CancellationToken cancellationToken)
    {
        var point = new Vector3D(request.Point![0], request.Point[1], request.Point[2]);
        var normal = new Vector3D(request.Normal![0], request.Normal[1], request.Normal[2]);
        if (!double.IsFinite(point.X + point.Y + point.Z) || !double.IsFinite(normal.X + normal.Y + normal.Z) ||
            !(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z > 1e-12))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_plane", "The cutting plane needs a finite point and a non-zero normal.");

        var glb = await store.ReadAllAsync(project.Id, part.GeometryFile, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The part geometry is not available.");
        var mm = MeshOperations.Scale(GlbReader.Read(glb), 1000);
        return PlaneSplitter.Split(mm, point, normal);
    }

    // ---- History ----

    private sealed record PartSnapshot(
        int SortOrder,
        string Name,
        DesignPartState State,
        string Category,
        string ReviewStatus,
        string? Material,
        string? ManufacturingMethod,
        int Quantity,
        double? ThicknessMm,
        double? DiameterMm,
        double? OuterDiameterMm,
        double? InnerDiameterMm,
        double? CutLengthMm,
        double? CutAngleDeg,
        double? FitAllowanceMm,
        double? ConfirmedLengthMm,
        double? ConfirmedWidthMm,
        double? ConfirmedHeightMm,
        string? StockDescription,
        string? StockSource,
        bool NeedsFurtherModelling,
        string? Notes);

    private sealed record PartChange(Guid PartId, PartSnapshot? Before, PartSnapshot After);

    private static PartSnapshot Snapshot(DesignPart p) => new(
        p.SortOrder, p.Name, p.State, p.Category, p.ReviewStatus, p.Material, p.ManufacturingMethod, p.Quantity,
        p.ThicknessMm, p.DiameterMm, p.OuterDiameterMm, p.InnerDiameterMm, p.CutLengthMm, p.CutAngleDeg, p.FitAllowanceMm,
        p.ConfirmedLengthMm, p.ConfirmedWidthMm, p.ConfirmedHeightMm, p.StockDescription, p.StockSource, p.NeedsFurtherModelling, p.Notes);

    private static void Apply(DesignPart p, PartSnapshot s)
    {
        p.SortOrder = s.SortOrder;
        p.Name = s.Name;
        p.State = s.State;
        p.Category = s.Category;
        p.ReviewStatus = s.ReviewStatus;
        p.Material = s.Material;
        p.ManufacturingMethod = s.ManufacturingMethod;
        p.Quantity = s.Quantity;
        p.ThicknessMm = s.ThicknessMm;
        p.DiameterMm = s.DiameterMm;
        p.OuterDiameterMm = s.OuterDiameterMm;
        p.InnerDiameterMm = s.InnerDiameterMm;
        p.CutLengthMm = s.CutLengthMm;
        p.CutAngleDeg = s.CutAngleDeg;
        p.FitAllowanceMm = s.FitAllowanceMm;
        p.ConfirmedLengthMm = s.ConfirmedLengthMm;
        p.ConfirmedWidthMm = s.ConfirmedWidthMm;
        p.ConfirmedHeightMm = s.ConfirmedHeightMm;
        p.StockDescription = s.StockDescription;
        p.StockSource = s.StockSource;
        p.NeedsFurtherModelling = s.NeedsFurtherModelling;
        p.Notes = s.Notes;
    }

    private static List<PartChange> Changes(PartOperation operation) =>
        JsonSerializer.Deserialize<List<PartChange>>(operation.ChangesJson, Json) ?? [];

    /// <summary>
    /// Appends an operation. Undone operations are discarded first (a new edit ends the redo
    /// branch); parts that only those operations had created are deleted with their files.
    /// </summary>
    private async Task RecordAsync(PartDesignProject project, string kind, string summary, List<PartChange> changes, CancellationToken cancellationToken, List<RelationChange>? relationChanges = null)
    {
        var discarded = project.Operations.Where(o => o.IsUndone).ToList();
        var orphanFiles = new HashSet<string>(StringComparer.Ordinal);
        var deletedParts = new HashSet<Guid>();
        foreach (var operation in discarded)
        {
            foreach (var change in Changes(operation).Where(c => c.Before is null))
            {
                var part = project.Parts.FirstOrDefault(p => p.Id == change.PartId);
                if (part is null)
                    continue;
                project.Parts.Remove(part);
                db.DesignParts.Remove(part);
                orphanFiles.Add(part.GeometryFile);
                deletedParts.Add(part.Id);
            }

            foreach (var change in RelationChanges(operation).Where(c => c.Before is null))
            {
                if (project.Relations.FirstOrDefault(r => r.Id == change.RelationId) is { } relation)
                    DeleteRelation(project, relation);
            }

            project.Operations.Remove(operation);
            db.PartOperations.Remove(operation);
        }

        // A relation can only outlive its parts as a removed record; drop those that lost a part for good.
        foreach (var relation in project.Relations.Where(r => deletedParts.Contains(r.PartAId) || deletedParts.Contains(r.PartBId)).ToList())
            DeleteRelation(project, relation);

        var now = time.GetUtcNow().UtcDateTime;
        var next = new PartOperation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Sequence = (project.Operations.Count == 0 ? 0 : project.Operations.Max(o => o.Sequence)) + 1,
            Kind = kind,
            Summary = Truncate(summary, 200),
            ChangesJson = JsonSerializer.Serialize(changes, Json),
            RelationChangesJson = JsonSerializer.Serialize(relationChanges ?? [], Json),
            CreatedAt = now,
        };
        project.Operations.Add(next);
        db.PartOperations.Add(next);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        foreach (var file in orphanFiles.Where(f => project.Parts.All(p => p.GeometryFile != f)))
            await store.DeleteAsync(project.Id, file, cancellationToken);
    }

    // ---- Helpers ----

    private async Task<PartDesignProject> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.PartDesigns
            .Include(p => p.Parts)
            .Include(p => p.Operations)
            .Include(p => p.Relations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Part design project not found.");

    private static DesignPart ActivePart(PartDesignProject project, Guid partId)
    {
        var part = project.Parts.FirstOrDefault(p => p.Id == partId)
                   ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Part not found.");
        if (part.State != DesignPartState.Active)
            throw new ApiException(StatusCodes.Status409Conflict, "part_not_active", "This part has been deleted or replaced by a split; undo to restore it.");
        return part;
    }

    private void EnsureCapacity(PartDesignProject project, int adding)
    {
        if (project.Parts.Count + adding > options.Value.MaxPartsPerProject)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_parts", $"A project can hold at most {options.Value.MaxPartsPerProject} parts (including history).");
    }

    private static int NextSortOrder(PartDesignProject project) => project.Parts.Count == 0 ? 0 : project.Parts.Max(p => p.SortOrder) + 1;

    private static Mesh Merge(List<Mesh> meshes)
    {
        var withColors = meshes.All(m => m.Colors is not null);
        var positions = new List<float>();
        var colors = withColors ? new List<byte>() : null;
        var indices = new List<int>();
        foreach (var m in meshes)
        {
            var offset = positions.Count / 3;
            positions.AddRange(m.Positions);
            colors?.AddRange(m.Colors!);
            indices.AddRange(m.Indices.Select(i => i + offset));
        }

        return new Mesh(positions.ToArray(), indices.ToArray(), colors?.ToArray());
    }

    private static GeometryReportDto ReportDto(PartGeometryReport r)
    {
        static Vector3Dto V(Vector3D v) => new(Round(v.X), Round(v.Y), Round(v.Z));
        return new GeometryReportDto(
            r.VertexCount,
            r.TriangleCount,
            r.Topology.IsClosed,
            r.Topology.BoundaryEdgeCount,
            r.Topology.NonManifoldEdgeCount,
            r.Topology.InconsistentlyOrientedEdgeCount,
            r.Topology.DegenerateTriangleCount,
            r.Topology.ComponentCount,
            Round(r.SurfaceArea),
            r.Volume is { } v ? Round(v) : null,
            new BoundsDto(V(r.Bounds.Min), V(r.Bounds.Max), V(r.Bounds.Size)),
            V(r.OrientedBox.Extents),
            r.Issues.Select(i => IssueDto.From(i, "geometry")).ToList());
    }

    private static SplitSideDto? SideDto(PlaneSplitSide? side)
    {
        if (side is null)
            return null;
        var b = MeshOperations.ComputeBounds(side.Mesh);
        static Vector3Dto V(Vector3D v) => new(Round(v.X), Round(v.Y), Round(v.Z));
        double? volume = side.Topology.IsClosed ? Round(MeshOperations.SignedVolume(side.Mesh)) : null;
        return new SplitSideDto(side.Mesh.TriangleCount, side.Capped, side.Topology.IsClosed, Round(side.CapArea), volume, new BoundsDto(V(b.Min), V(b.Max), V(b.Size)));
    }

    private PartDesignProjectDto ToDto(PartDesignProject project)
    {
        var parts = project.Parts
            .Where(p => p.State != DesignPartState.Removed)
            .OrderBy(p => p.SortOrder)
            .Select(p => PartDto(p, project))
            .ToList();
        var history = project.Operations
            .OrderByDescending(o => o.Sequence)
            .Take(50)
            .Select(o => new OperationDto(o.Sequence, o.Kind, o.Summary, o.IsUndone, Utc(o.CreatedAt)))
            .ToList();
        return new PartDesignProjectDto(
            project.Id,
            project.Name,
            project.SourceJobId,
            project.SourceFileName,
            SourceDto(project),
            project.MmPerModelUnit,
            "estimated",
            CoordinateSystem,
            project.FitAllowanceMinMm,
            project.FitAllowanceMaxMm,
            Utc(project.CreatedAt),
            Utc(project.UpdatedAt),
            JsonSerializer.Deserialize<List<IssueDto>>(project.IssuesJson, Json) ?? [],
            parts,
            RelationDtos(project),
            history,
            project.Operations.Any(o => !o.IsUndone),
            project.Operations.Any(o => o.IsUndone));
    }

    private static DesignSourceDto SourceDto(PartDesignProject p) =>
        new(p.SourceKind, p.SourceJobId, p.SourceReferenceSessionId, p.SourceModelCandidateId, p.SourceFileName, p.SourceLicense, p.SourceAttribution);

    private static DesignPartDto PartDto(DesignPart p, PartDesignProject project)
    {
        var geometry = JsonSerializer.Deserialize<GeometryReportDto>(p.GeometryReportJson, Json)!;
        var issues = geometry.Issues.Concat(ManufacturingValidator.Validate(p, project).Select(i => IssueDto.From(i, "manufacturing"))).ToList();
        var status = issues.Any(i => i.Severity == "error") ? "invalid" : issues.Any(i => i.Severity == "warning") ? "warning" : "valid";
        var anyConfirmed = p.ConfirmedLengthMm is not null || p.ConfirmedWidthMm is not null || p.ConfirmedHeightMm is not null;
        return new DesignPartDto(
            p.Id,
            p.ParentPartId,
            p.SortOrder,
            p.Name,
            p.Origin,
            p.SourceNodeName,
            p.State,
            p.Category,
            p.ReviewStatus,
            new SuggestionDto(p.SuggestedCategory, p.SuggestionConfidence, JsonSerializer.Deserialize<List<string>>(p.SuggestionReasonsJson, Json) ?? []),
            new EstimatedDimensionsDto(p.EstimatedLengthMm, p.EstimatedWidthMm, p.EstimatedThicknessMm, p.EstimatedOuterDiameterMm, p.EstimatedInnerDiameterMm),
            new ConfirmedDimensionsDto(p.ConfirmedLengthMm, p.ConfirmedWidthMm, p.ConfirmedHeightMm, anyConfirmed ? ReviewStatuses.UserConfirmed : null),
            new ManufacturingSpecDto(
                p.Material, p.ManufacturingMethod, p.Quantity, p.ThicknessMm, p.DiameterMm, p.OuterDiameterMm, p.InnerDiameterMm,
                p.CutLengthMm, p.CutAngleDeg, p.FitAllowanceMm, p.StockDescription, p.StockSource, p.NeedsFurtherModelling, p.Notes),
            geometry,
            new ValidationDto(status, issues));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static double Round(double value) => Math.Round(value, 4);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
