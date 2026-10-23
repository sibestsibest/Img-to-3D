using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Parametric;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// Projects generated from template parameters by the parametric geometry worker. The worker's
/// GLB holds one node per component; each becomes one part with the component's exact category,
/// sheet thickness and material. The worker's own files (1:1 cutting templates, orthographic
/// views, BOM, STL/OBJ) are kept with the project as generated.
/// </summary>
public sealed partial class PartDesignService
{
    /// <summary>kind -> (stored file, worker output, content type, download suffix, description)</summary>
    private static readonly (string Kind, string Stored, string Output, string ContentType, string Suffix, string Description)[] ParametricFiles =
    [
        ("templates-pdf", "ptemplates.pdf", "templates/templates.pdf", "application/pdf", "templates.pdf", "1:1 cutting templates of every sheet part (print at 100 %)"),
        ("views-pdf", "pviews.pdf", "views/views.pdf", "application/pdf", "views.pdf", "Front, side, top and section views with overall dimensions"),
        ("views-svg", "pviews.svg", "views/views.svg", "image/svg+xml", "views.svg", "Orthographic views (SVG)"),
        ("bom", "pbom.csv", "bom.csv", "text/csv", "bom.csv", "Bill of materials, identical parts grouped"),
        ("stl", "pmodel.stl", "model.stl", "model/stl", "model.stl", "Whole model, millimetres, Z-up"),
        ("obj", "pmodel.obj", "model.obj", "model/obj", "model.obj", "Whole model, millimetres, Y-up, one group per component"),
        ("package", "pfiles.zip", "", "application/zip", "parametric.zip", "Every generated file: per-part SVG and DXF templates, views, BOM, GLB/STL/OBJ, response"),
    ];

    public async Task<JsonElement> ParametricTemplatesAsync(CancellationToken cancellationToken)
    {
        var capability = await parametric.GetCapabilityAsync(cancellationToken);
        return capability is { Status: "ready", Description: { } description }
            ? description
            : throw new ApiException(StatusCodes.Status503ServiceUnavailable, "geometry_engine_unavailable", capability.Reason ?? "The parametric geometry worker is not available.");
    }

    public async Task<PartDesignProjectDto> CreateParametricAsync(CreateParametricDesignRequest request, CancellationToken cancellationToken)
    {
        await EnsureProjectCapacityAsync(cancellationToken);
        using var run = await parametric.GenerateAsync(request.Template!, request.Parameters, cancellationToken);
        var response = run.Response;

        var glbPath = run.File("model.glb") ?? throw new ApiException(StatusCodes.Status500InternalServerError, "geometry_engine_failed", "The geometry engine produced no model.");
        var glb = await File.ReadAllBytesAsync(glbPath, cancellationToken);
        IReadOnlyList<GlbNodeMesh> nodes;
        try
        {
            nodes = GlbReader.ReadNodes(glb);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError(ex, "The parametric worker's GLB cannot be read");
            throw new ApiException(StatusCodes.Status500InternalServerError, "geometry_engine_failed", "The geometry engine produced an unreadable model.");
        }

        var o = options.Value;
        if (nodes.Sum(n => (long)n.Mesh.TriangleCount) > o.MaxSourceTriangles)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "source_too_large", $"The model has more than {o.MaxSourceTriangles:N0} triangles; use fewer components or segments.");
        if (nodes.Count > o.MaxPartsPerProject)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "too_many_parts", $"The model has {nodes.Count} components; a project holds at most {o.MaxPartsPerProject} parts.");

        var template = response.GetProperty("template").GetString()!;
        var engine = response.GetProperty("engine");
        var version = $"{engine.GetProperty("version").GetString()}";
        var parameters = response.GetProperty("parameters");
        var components = response.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c, StringComparer.Ordinal);

        var now = time.GetUtcNow().UtcDateTime;
        var project = new PartDesignProject
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = owner.OwnerKeyHash,
            Name = Truncate(Clean(request.Name) ?? DefaultParametricName(template, parameters), 100),
            SourceKind = DesignSourceKinds.Parametric,
            SourceFileName = Truncate(SafeFileStem(template) + ".glb", 128),
            // The GLB is in metres; parts are written in millimetres.
            MmPerModelUnit = 1000,
            ParametricTemplate = Truncate(template, 32),
            ParametricParametersJson = parameters.GetRawText(),
            ParametricEngineVersion = Truncate(version, 16),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var issues = new List<IssueDto>
        {
            new("parametric_exact", "info", $"Generated from template '{template}' by {engine.GetProperty("name").GetString()} {version}: sizes are exactly the entered parameters (nothing is estimated or AI-predicted).", "analysis"),
            new("parametric_files_fixed", "info", "The cutting templates, views and BOM of the Parametric card show the model as generated; edits made here afterwards are not in them. Create a variant to regenerate.", "analysis"),
        };
        foreach (var issue in response.GetProperty("issues").EnumerateArray())
            issues.Add(new IssueDto(issue.GetProperty("code").GetString()!, issue.GetProperty("severity").GetString()!, issue.GetProperty("message").GetString()!, "parametric"));
        project.IssuesJson = JsonSerializer.Serialize(issues, Json);

        var files = new List<(string Name, byte[] Content)> { (DesignArtifactNames.SourceMesh, glb) };
        var sort = 0;
        foreach (var node in nodes)
        {
            var name = node.Name ?? $"part_{sort + 1}";
            var part = CreatePart(project.Id, MeshOperations.Scale(node.Mesh, 1000), Truncate(name, 100), PartOrigins.Parametric, name, null, now, files)
                       ?? throw new ApiException(StatusCodes.Status500InternalServerError, "geometry_engine_failed", $"Component '{name}' has no usable geometry.");
            if (components.TryGetValue(name, out var component))
                ApplyComponent(part, component);
            part.SortOrder = sort++;
            project.Parts.Add(part);
        }

        var kept = new List<string>();
        foreach (var file in ParametricFiles)
        {
            if (file.Kind == "package")
                files.Add((file.Stored, ZipDirectory(run.Directory, now)));
            else if (run.File(file.Output) is { } path)
                files.Add((file.Stored, await File.ReadAllBytesAsync(path, cancellationToken)));
            else
                continue;
            kept.Add(file.Kind);
        }

        project.ParametricFilesJson = JsonSerializer.Serialize(kept, Json);

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

        logger.LogInformation("Created parametric part design {ProjectId} from template {Template} with {PartCount} parts", project.Id, template, project.Parts.Count);
        return ToDto(project);
    }

    public async Task<DrawingFile> OpenParametricFileAsync(Guid id, string kind, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        if (project.SourceKind != DesignSourceKinds.Parametric)
            throw new ApiException(StatusCodes.Status409Conflict, "not_parametric", "This project was not generated from parameters.");
        var file = ParametricFiles.FirstOrDefault(f => f.Kind == kind);
        if (file.Kind is null)
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"Unknown file '{kind}'. Use one of: {string.Join(", ", ParametricFiles.Select(f => f.Kind))}.");
        var content = await store.ReadAllAsync(id, file.Stored, cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "This file was not generated for the project.");
        return new DrawingFile(content, file.ContentType, $"{SafeFileStem(project.Name)}-{file.Suffix}");
    }

    /// <summary>Exact specification from the generated component; the parameters were entered, so no review is pending.</summary>
    private static void ApplyComponent(DesignPart part, JsonElement c)
    {
        var category = c.GetProperty("category").GetString();
        part.Category = category is not null && PartCategories.All.Contains(category) ? category : PartCategories.Custom3D;
        part.ReviewStatus = ReviewStatuses.UserConfirmed;
        part.Material = c.GetProperty("material").GetString() is { Length: > 0 } m ? Truncate(m, 100) : null;
        part.ThicknessMm = Number(c, "thicknessMm");
        var size = c.TryGetProperty("sizeMm", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        if (part.Category == PartCategories.Rod)
        {
            part.DiameterMm = Number(size, "diameter");
            part.CutLengthMm = Number(size, "length");
        }
        else if (part.Category == PartCategories.Tube)
        {
            part.OuterDiameterMm = Number(size, "outerDiameter");
            part.InnerDiameterMm = Number(size, "innerDiameter");
            part.CutLengthMm = Number(size, "length");
        }

        var notes = new List<string>();
        var quantity = c.GetProperty("quantity").GetInt32();
        if (quantity > 1)
        {
            // The part's geometry already holds every piece; the quantity stays 1 so nothing is counted twice.
            part.StockDescription = Truncate($"{quantity} identical pieces in this part", 200);
            notes.Add($"Contains {quantity} identical pieces.");
        }

        if (c.TryGetProperty("forming", out var forming) && forming.GetString() is { } f)
            notes.Add(f == "cone_roll" ? "Cut flat, then roll into a cone (developed template in the parametric files)." : "Cut flat, then roll (developed template in the parametric files).");
        if (notes.Count > 0)
            part.Notes = Truncate(string.Join(" ", notes), 1000);
    }

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string DefaultParametricName(string template, JsonElement parameters)
    {
        var title = template.Length == 0 ? "Parametric" : char.ToUpperInvariant(template[0]) + template[1..].Replace('_', ' ');
        if (Number(parameters, "length") is { } l && Number(parameters, "width") is { } w && Number(parameters, "height") is { } h)
            return string.Create(CultureInfo.InvariantCulture, $"{title} {l:0.##} x {w:0.##} x {h:0.##} mm");
        return title;
    }

    /// <summary>All files of the run, sorted, with one fixed timestamp (the same run always zips the same way).</summary>
    private static byte[] ZipDirectory(string directory, DateTime timestamp)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(Path.GetRelativePath(directory, path).Replace('\\', '/'), CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
                using var target = entry.Open();
                using var source = File.OpenRead(path);
                source.CopyTo(target);
            }
        }

        return memory.ToArray();
    }

    private static ParametricInfoDto? ParametricInfo(PartDesignProject project)
    {
        if (project.SourceKind != DesignSourceKinds.Parametric || project.ParametricParametersJson is null)
            return null;
        using var parameters = JsonDocument.Parse(project.ParametricParametersJson);
        var kept = JsonSerializer.Deserialize<List<string>>(project.ParametricFilesJson ?? "[]", Json) ?? [];
        var files = ParametricFiles
            .Where(f => kept.Contains(f.Kind))
            .Select(f => new ParametricFileDto(f.Kind, f.Description, $"/api/part-designs/{project.Id}/parametric/files/{f.Kind}"))
            .ToList();
        return new ParametricInfoDto(project.ParametricTemplate ?? "", project.ParametricEngineVersion ?? "", parameters.RootElement.Clone(), files);
    }
}
