using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Drawing;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;
using Microsoft.EntityFrameworkCore;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// Fabrication exports: per-part STL, BOM and rod/tube cut list (CSV), and export packages (zip with
/// drawings, DXF, STL, CSVs and manifest.json). A draft package is built on demand; a release is
/// built once, numbered, stored and served unchanged from then on.
/// </summary>
public sealed partial class PartDesignService
{
    public const int ManifestSchemaVersion = 1;

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

    /// <summary>Part geometry as binary STL in millimetres (design coordinates).</summary>
    public async Task<ExportFile> PartStlAsync(Guid id, Guid partId, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var part = ActivePart(project, partId);
        var mesh = await LoadPartMeshAsync(project, part, cancellationToken);
        try
        {
            return new ExportFile(StlWriter.Write(mesh), "model/stl", $"{PartFileStem(part)}.stl");
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "stl_failed", "The part geometry cannot be written as STL: " + ex.Message);
        }
    }

    public async Task<ExportFile> BomCsvAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        return new ExportFile(Bom(project, ExportParts(project)).ToBytes(), "text/csv; charset=utf-8", $"{SafeFileStem(project.Name)}-bom.csv");
    }

    public async Task<ExportFile> CutListCsvAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        return new ExportFile(CutList(ExportParts(project)).ToBytes(), "text/csv; charset=utf-8", $"{SafeFileStem(project.Name)}-cut-list.csv");
    }

    /// <summary>What a draft package would contain right now (builds it, without returning the files).</summary>
    public async Task<ExportManifestDto> DraftManifestAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        return (await BuildPackageAsync(project, null, null, time.GetUtcNow().UtcDateTime, cancellationToken)).Manifest;
    }

    public async Task<ExportFile> DraftPackageAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var (zip, _) = await BuildPackageAsync(project, null, null, now, cancellationToken);
        return new ExportFile(zip, "application/zip", $"{SafeFileStem(project.Name)}-draft-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip");
    }

    // ---- Releases ----

    public async Task<IReadOnlyList<DesignReleaseDto>> ListReleasesAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureProjectAsync(id, cancellationToken);
        var releases = await db.DesignReleases.AsNoTracking()
            .Where(r => r.ProjectId == id)
            .OrderByDescending(r => r.Revision)
            .ToListAsync(cancellationToken);
        return releases.Select(ReleaseDto).ToList();
    }

    /// <summary>Builds the next numbered package and stores it; it is never rebuilt afterwards.</summary>
    public async Task<DesignReleaseDto> CreateReleaseAsync(Guid id, CreateReleaseRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var existing = db.DesignReleases.Where(r => r.ProjectId == id);
        if (await existing.CountAsync(cancellationToken) >= options.Value.MaxReleasesPerProject)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_releases", $"A project can keep at most {options.Value.MaxReleasesPerProject} releases.");

        var revision = (await existing.MaxAsync(r => (int?)r.Revision, cancellationToken) ?? 0) + 1;
        var now = time.GetUtcNow().UtcDateTime;
        var note = Clean(request.Note);
        var (zip, manifest) = await BuildPackageAsync(project, revision, note, now, cancellationToken);

        var release = new DesignRelease
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Revision = revision,
            Note = note,
            SizeBytes = zip.Length,
            Sha256 = Sha256(zip),
            HistorySequence = manifest.Project.HistorySequence,
            PartCount = manifest.Parts.Count,
            FileCount = manifest.Files.Count,
            WarningCount = manifest.Warnings.Count,
            CreatedAt = now,
            PackageFile = "",
        };
        release.PackageFile = DesignArtifactNames.ReleasePackage(release.Id);

        await store.SaveAsync(project.Id, release.PackageFile, zip, cancellationToken);
        db.DesignReleases.Add(release);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request took this revision number first.
            await store.DeleteAsync(project.Id, release.PackageFile, CancellationToken.None);
            throw new ApiException(StatusCodes.Status409Conflict, "release_conflict", "Another release was created at the same time; try again.");
        }

        logger.LogInformation("Created release {Revision} of part design {ProjectId} ({Bytes} bytes)", revision, project.Id, zip.Length);
        return ReleaseDto(release);
    }

    public async Task<(Stream Content, string FileName)> OpenReleaseAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        var project = await db.PartDesigns.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Part design project not found.");
        var release = await db.DesignReleases.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == id && r.Revision == revision, cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Release not found.");
        var stream = await store.OpenReadAsync(id, release.PackageFile, cancellationToken)
                     ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The release package is not available.");
        return (stream, $"{SafeFileStem(project.Name)}-rev{revision}.zip");
    }

    // ---- Package ----

    private sealed record ExportPart(int Item, DesignPart Part, ValidationDto Validation);

    private List<ExportPart> ExportParts(PartDesignProject project) =>
        project.Parts
            .Where(p => p.State == DesignPartState.Active)
            .OrderBy(p => p.SortOrder)
            .Select((p, i) => new ExportPart(i + 1, p, PartDto(p, project).Validation))
            .ToList();

    /// <summary>
    /// Builds every file in memory, then the manifest (with a SHA-256 per file) and the zip. Parts with
    /// warnings are included and listed in the manifest; a part with a validation error blocks the
    /// package, since its files would be wrong.
    /// </summary>
    private async Task<(byte[] Zip, ExportManifestDto Manifest)> BuildPackageAsync(PartDesignProject project, int? revision, string? note, DateTime createdAt, CancellationToken cancellationToken)
    {
        var parts = ExportParts(project);
        if (parts.Count == 0)
            throw new ApiException(StatusCodes.Status409Conflict, "nothing_to_export", "The project has no parts to export.");
        var invalid = parts.Where(p => p.Validation.Status == "invalid").ToList();
        if (invalid.Count > 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "export_blocked",
                "Fix the errors on these parts before exporting: " + string.Join(", ", invalid.Select(p => $"'{Truncate(p.Part.Name, 40)}'")) + ".");
        }

        var label = revision is { } r ? $"Rev {r}" : SheetDrawingInfo.Draft;
        var files = new List<(string Path, string Kind, Guid? PartId, byte[] Content)>();
        var warnings = new List<ManifestWarningDto>();
        var meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        var digits = Math.Max(2, parts.Count.ToString(CultureInfo.InvariantCulture).Length);

        foreach (var (item, part, validation) in parts)
        {
            foreach (var issue in validation.Issues.Where(i => i.Severity is "warning" or "error"))
                warnings.Add(new ManifestWarningDto(issue.Code, issue.Severity, issue.Message, part.Id));
            if (part.ReviewStatus != ReviewStatuses.UserConfirmed)
                warnings.Add(new ManifestWarningDto("not_reviewed", "warning", "The part has not been confirmed by the user.", part.Id));

            if (!meshes.TryGetValue(part.GeometryFile, out var mesh))
                meshes[part.GeometryFile] = mesh = await LoadPartMeshAsync(project, part, cancellationToken);

            var stem = $"{item.ToString(new string('0', digits), CultureInfo.InvariantCulture)}-{PartFileStem(part)}";
            try
            {
                files.Add(($"stl/{stem}.stl", "stl", part.Id, StlWriter.Write(mesh)));
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add(new ManifestWarningDto("stl_failed", "warning", "No STL: " + ex.Message, part.Id));
            }

            if (part.Category != PartCategories.Sheet)
                continue;

            var outline = PlanarOutlineExtractor.Extract(mesh);
            if (!outline.IsUsable)
            {
                warnings.Add(new ManifestWarningDto("outline_failed", "warning",
                    "No drawing or DXF: " + string.Join(" ", outline.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)), part.Id));
                continue;
            }

            files.Add(($"drawings/{stem}.dxf", "drawing-dxf", part.Id, Dxf(part, outline, label)));
            try
            {
                var (drawing, _) = BuildDrawing(project, part, outline, label);
                files.Add(($"drawings/{stem}.pdf", "drawing-pdf", part.Id, PdfDrawingWriter.Write(drawing.Document)));
                files.Add(($"drawings/{stem}.svg", "drawing-svg", part.Id, Encoding.UTF8.GetBytes(SvgDrawingWriter.Write(drawing.Document))));
            }
            catch (ApiException ex)
            {
                warnings.Add(new ManifestWarningDto(ex.Code, "warning", "No PDF/SVG drawing (the DXF is included): " + ex.Message, part.Id));
            }
        }

        // Assembly and quality: relations, fits, collisions between the parts, geometry quality.
        var assembly = await BuildAssemblyAsync(project, meshes, cancellationToken);
        foreach (var relation in assembly.Relations)
        {
            foreach (var issue in relation.Validation.Issues.Where(i => i.Severity is "warning" or "error"))
                warnings.Add(new ManifestWarningDto(issue.Code, issue.Severity, issue.Message, relation.PartAId));
        }

        foreach (var collision in assembly.Collisions.Where(c => c.Severity is "warning" or "error"))
            warnings.Add(new ManifestWarningDto(collision.Code, collision.Severity, collision.Message, collision.PartAId));
        foreach (var issue in assembly.Issues.Where(i => i.Severity is "warning" or "error"))
            warnings.Add(new ManifestWarningDto(issue.Code, issue.Severity, issue.Message, null));
        files.Add((AssemblyFile, "assembly", null, JsonSerializer.SerializeToUtf8Bytes(assembly with { Quality = assembly.Quality with { Parts = [] } }, ManifestJson)));
        files.Add(("metadata/quality.json", "quality", null, JsonSerializer.SerializeToUtf8Bytes(assembly.Quality, ManifestJson)));

        files.Add(("bom.csv", "bom", null, Bom(project, parts).ToBytes()));
        files.Add(("cut-list.csv", "cut-list", null, CutList(parts).ToBytes()));
        files.Add(("README.txt", "readme", null, Encoding.UTF8.GetBytes(Readme(project, label, note, createdAt, warnings.Count))));

        var manifest = new ExportManifestDto(
            ManifestSchemaVersion,
            "ImageTo3D",
            revision is null ? "draft" : "release",
            revision,
            note,
            Utc(createdAt),
            "mm",
            CoordinateSystem,
            new ManifestProjectDto(project.Id, project.Name, project.SourceJobId, project.SourceFileName, SourceDto(project), project.MmPerModelUnit, "estimated", HistorySequence(project), Utc(project.UpdatedAt)),
            parts.Select(p => new ManifestPartDto(p.Item, p.Part.Id, p.Part.Name, p.Part.Category, p.Part.ReviewStatus, p.Validation.Status, p.Part.Quantity,
                files.Where(f => f.PartId == p.Part.Id).Select(f => f.Path).ToList(),
                PartDto(p.Part, project).Spec,
                files.Where(f => f.PartId == p.Part.Id && f.Kind == "stl").Select(f => Sha256(f.Content)).FirstOrDefault())).ToList(),
            files.Select(f => new ManifestFileDto(f.Path, f.Kind, f.PartId, f.Content.LongLength, Sha256(f.Content))).ToList(),
            warnings,
            new ManifestAssemblyDto(assembly.Status, assembly.Relations.Count, assembly.Collisions.Count(c => c.Severity != "info"), assembly.Complete,
                assembly.Quality.Status, assembly.Quality.SolidExportReadyParts));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Zip timestamps are local times without a zone; the manifest holds the exact UTC time.
            var stamp = new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Unspecified), TimeSpan.Zero);
            void Add(string path, byte[] content)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                using var stream = entry.Open();
                stream.Write(content);
            }

            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson));
            foreach (var file in files)
                Add(file.Path, file.Content);
        }

        return (buffer.ToArray(), manifest);
    }

    private static CsvTable Bom(PartDesignProject project, List<ExportPart> parts)
    {
        var table = new CsvTable(
            "item", "part_id", "name", "category", "review_status", "validation_status", "quantity", "material", "manufacturing_method",
            "thickness_mm", "diameter_mm", "outer_diameter_mm", "inner_diameter_mm", "cut_length_mm", "cut_angle_deg", "fit_allowance_mm",
            "estimated_length_mm", "estimated_width_mm", "estimated_thickness_mm", "confirmed_length_mm", "confirmed_width_mm", "confirmed_height_mm",
            "stock_description", "stock_source", "needs_further_modelling", "issues", "notes");
        foreach (var (item, p, validation) in parts)
        {
            table.Add(
                CsvTable.Int(item), p.Id.ToString(), CsvTable.Text(p.Name), p.Category, p.ReviewStatus, validation.Status, CsvTable.Int(p.Quantity),
                CsvTable.Text(p.Material), CsvTable.Text(p.ManufacturingMethod),
                CsvTable.Num(p.ThicknessMm), CsvTable.Num(p.DiameterMm), CsvTable.Num(p.OuterDiameterMm), CsvTable.Num(p.InnerDiameterMm),
                CsvTable.Num(p.CutLengthMm), CsvTable.Num(p.CutAngleDeg), CsvTable.Num(p.FitAllowanceMm),
                CsvTable.Num(p.EstimatedLengthMm), CsvTable.Num(p.EstimatedWidthMm), CsvTable.Num(p.EstimatedThicknessMm),
                CsvTable.Num(p.ConfirmedLengthMm), CsvTable.Num(p.ConfirmedWidthMm), CsvTable.Num(p.ConfirmedHeightMm),
                CsvTable.Text(p.StockDescription), CsvTable.Text(p.StockSource), CsvTable.Bool(p.NeedsFurtherModelling),
                string.Join(' ', validation.Issues.Where(i => i.Severity is "warning" or "error").Select(i => i.Code).Distinct()),
                CsvTable.Text(p.Notes));
        }

        return table;
    }

    private sealed record CutLine(
        string Category, string? Material, string Profile, double? OuterDiameter, double? InnerDiameter, string SizeStatus,
        double Length, string LengthStatus, double Angle, double? FitAllowance);

    /// <summary>
    /// Rod and tube pieces grouped by stock (category, material, section) and identical cut; one row
    /// per distinct cut with the summed quantity. Missing values fall back to the model estimate and
    /// are marked "estimated" so nobody cuts from a guess unknowingly.
    /// </summary>
    private static CsvTable CutList(List<ExportPart> parts)
    {
        var table = new CsvTable(
            "stock_group", "category", "material", "profile", "outer_diameter_mm", "inner_diameter_mm", "size_status",
            "cut_length_mm", "length_status", "cut_angle_deg", "fit_allowance_mm", "quantity", "total_length_mm", "parts");

        var lines = parts
            .Where(p => p.Part.Category is PartCategories.Rod or PartCategories.Tube)
            .Select(p => (Line: CutLineFor(p.Part), p.Part))
            .GroupBy(x => x.Line)
            .Select(g => (g.Key, Quantity: g.Sum(x => x.Part.Quantity), Names: string.Join("; ", g.Select(x => x.Part.Name))))
            .OrderBy(x => x.Key.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Material ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.OuterDiameter ?? double.MaxValue)
            .ThenBy(x => x.Key.InnerDiameter ?? 0)
            .ThenBy(x => x.Key.Profile, StringComparer.Ordinal)
            .ThenByDescending(x => x.Key.Length)
            .ToList();

        var groups = new Dictionary<(string, string, string), int>();
        foreach (var (line, quantity, names) in lines)
        {
            var key = (line.Category, (line.Material ?? "").ToUpperInvariant(), line.Profile);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = groups.Count + 1;
            table.Add(
                $"S{group}", line.Category, CsvTable.Text(line.Material), CsvTable.Text(line.Profile),
                CsvTable.Num(line.OuterDiameter), CsvTable.Num(line.InnerDiameter), line.SizeStatus,
                CsvTable.Num(line.Length), line.LengthStatus, CsvTable.Num(line.Angle), CsvTable.Num(line.FitAllowance),
                CsvTable.Int(quantity), CsvTable.Num(Math.Round(line.Length * quantity, 2)), CsvTable.Text(names));
        }

        return table;
    }

    private static CutLine CutLineFor(DesignPart p)
    {
        static double? R(double? v) => v is { } x ? Math.Round(x, 2) : null;
        double? outer, inner = null;
        string sizeStatus, profile;
        if (p.Category == PartCategories.Tube)
        {
            var specified = p.OuterDiameterMm is not null && p.InnerDiameterMm is not null;
            outer = R(p.OuterDiameterMm ?? p.EstimatedOuterDiameterMm);
            inner = R(p.InnerDiameterMm ?? p.EstimatedInnerDiameterMm);
            sizeStatus = specified ? "specified" : outer is null ? "missing" : "estimated";
            profile = outer is null ? "tube, size unknown" : $"tube OD {CsvTable.Num(outer)} / ID {(inner is null ? "?" : CsvTable.Num(inner))}";
        }
        else if (p.DiameterMm is not null || p.EstimatedOuterDiameterMm is not null)
        {
            outer = R(p.DiameterMm ?? p.EstimatedOuterDiameterMm);
            sizeStatus = p.DiameterMm is not null ? "specified" : "estimated";
            profile = $"round rod D {CsvTable.Num(outer)}";
        }
        else
        {
            // Square or rectangular section: no diameter, so describe the measured section.
            outer = null;
            sizeStatus = "estimated";
            profile = $"rod section {CsvTable.Num(Math.Round(p.EstimatedWidthMm, 2))} x {CsvTable.Num(Math.Round(p.EstimatedThicknessMm, 2))}";
        }

        var length = Math.Round(p.CutLengthMm ?? p.EstimatedLengthMm, 2);
        return new CutLine(p.Category, p.Material?.Trim(), profile, outer, inner, sizeStatus, length, p.CutLengthMm is null ? "estimated" : "specified",
            Math.Round(p.CutAngleDeg ?? 0, 2), R(p.FitAllowanceMm));
    }

    private static string Readme(PartDesignProject project, string label, string? note, DateTime createdAt, int warningCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{project.Name} - fabrication package, {label}");
        var from = project.SourceKind == DesignSourceKinds.None ? "" : $" from '{project.SourceFileName}'";
        sb.AppendLine($"Generated {createdAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC by ImageTo3D{from}.");
        if (project.SourceAttribution is { } attribution)
            sb.AppendLine("Source model: " + attribution + ". Respect its licence when sharing anything made from it.");
        if (note is not null)
            sb.AppendLine("Note: " + note);
        sb.AppendLine();
        sb.AppendLine("Contents");
        sb.AppendLine("  manifest.json  every file with its SHA-256, the parts and all warnings");
        sb.AppendLine("  bom.csv        bill of materials, one row per part");
        sb.AppendLine("  cut-list.csv   rod and tube cuts grouped by stock");
        sb.AppendLine("  drawings/      sheet parts: PDF and SVG at 1:1, DXF (layer CUT = cutting contours)");
        sb.AppendLine("  stl/           every part as STL, millimetres");
        sb.AppendLine("  metadata/      assembly.json (relations, fits, collisions), quality.json (geometry quality per part)");
        sb.AppendLine();
        sb.AppendLine("Before cutting");
        sb.AppendLine("  - Print PDFs at 100% / actual size, never 'fit to page'; check the 100 mm bar with a ruler.");
        sb.AppendLine(project.SourceKind switch
        {
            DesignSourceKinds.ReferenceModel => "  - All lengths are millimetres. Dimensions come from a reused model at its file's declared scale;",
            DesignSourceKinds.None => "  - All lengths are millimetres. Parts built from stock shapes have the sizes entered; others are estimates;",
            _ => "  - All lengths are millimetres. Dimensions come from an AI-generated model at an estimated scale;",
        });
        sb.AppendLine("    values marked 'estimated' must be confirmed on the real part.");
        sb.AppendLine(warningCount == 0 ? "  - No warnings." : $"  - {warningCount} warning(s): see manifest.json.");
        return sb.ToString();
    }

    // ---- Helpers ----

    private async Task<Mesh> LoadPartMeshAsync(PartDesignProject project, DesignPart part, CancellationToken cancellationToken)
    {
        var glb = await store.ReadAllAsync(project.Id, part.GeometryFile, cancellationToken)
                  ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", $"The geometry of '{Truncate(part.Name, 40)}' is not available.");
        return MeshOperations.Scale(GlbReader.Read(glb), 1000);
    }

    private async Task EnsureProjectAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.PartDesigns.AnyAsync(p => p.Id == id && p.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken))
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Part design project not found.");
    }

    private static int HistorySequence(PartDesignProject project) =>
        project.Operations.Where(o => !o.IsUndone).Select(o => o.Sequence).DefaultIfEmpty(0).Max();

    private static DesignReleaseDto ReleaseDto(DesignRelease r) =>
        new(r.Id, r.Revision, r.Note, r.SizeBytes, r.Sha256, r.HistorySequence, r.PartCount, r.FileCount, r.WarningCount, Utc(r.CreatedAt));

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
