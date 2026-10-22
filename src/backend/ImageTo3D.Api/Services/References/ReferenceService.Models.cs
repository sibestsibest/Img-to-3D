using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Api.Services.References;

public sealed record ModelFile(Stream Content, string ContentType, string FileName);

/// <summary>
/// Existing 3D models: Sketchfab listings (anonymous search; nothing is downloaded but the
/// preview) and GLB/STL files the user uploads, which are read and checked. Every candidate is
/// scored by <see cref="ModelEvaluator"/>; the name is never a signal.
/// </summary>
public sealed partial class ReferenceService
{
    private const string ModelUploadSource = "upload";

    /// <summary>Embedding availability (loading the model on first call) and the sources.</summary>
    public async Task<ReferenceCapabilitiesDto> CapabilitiesAsync(CancellationToken cancellationToken)
    {
        await embedder.EnsureReadyAsync(cancellationToken);
        var status = embedder.Status;
        return new ReferenceCapabilitiesDto(
            new EmbeddingStatusDto(status.Available, status.Model, status.Reason),
            ListSources(),
            [new ReferenceSourceDto(sketchfab.Id, sketchfab.Name, "3D model listings (licence, face count, preview). Search is free and anonymous; downloading needs a free Sketchfab account, so download the file yourself and upload it here.", sketchfab.Enabled)]);
    }

    public async Task<ReferenceSessionDto> SearchModelsAsync(Guid id, SearchReferencesRequest request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var mo = modelOptions.Value;
        var session = await LoadAsync(id, cancellationToken);
        if (request.Query is not null)
            session.Query = ReferenceQuery.Clean(request.Query);
        var query = session.Query;
        if (query.Length < 2)
            throw new ApiException(StatusCodes.Status400BadRequest, "query_required", "Enter at least two characters to search for (for example the kit or part name).");

        var now = time.GetUtcNow().UtcDateTime;
        IReadOnlyList<ModelHit>? hits = null;
        string status;
        string? message = null;
        var fromCache = false;
        if (!sketchfab.Enabled)
        {
            status = "disabled";
            message = "Turned off in the server configuration.";
        }
        else
        {
            var key = ReferenceQuery.CacheKey(sketchfab.Id, query, mo.ResultsPerSearch);
            var entry = await db.ReferenceSearchCache.FindAsync([key], cancellationToken);
            if (entry is not null && entry.ExpiresAt > now && DeserializeModels(entry.HitsJson) is { } cached)
            {
                (hits, fromCache) = (cached, true);
                status = cached.Count == 0 ? "empty" : "ok";
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(o.SourceTimeout);
                try
                {
                    hits = (await sketchfab.SearchAsync(query, mo.ResultsPerSearch, timeout.Token)).Take(mo.ResultsPerSearch).ToList();
                    status = hits.Count == 0 ? "empty" : "ok";
                    if (entry is null)
                    {
                        entry = new ReferenceSearchCacheEntry { Key = key, Source = sketchfab.Id, Query = query, HitsJson = "[]" };
                        db.ReferenceSearchCache.Add(entry);
                    }

                    (entry.HitsJson, entry.FetchedAt, entry.ExpiresAt) = (JsonSerializer.Serialize(hits, Json), now, now + o.CacheTtl);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    (status, message) = ("timeout", $"No answer within {o.SourceTimeout.TotalSeconds:0} s.");
                }
                catch (ReferenceFetchException ex)
                {
                    (status, message) = (ex.Code == "rate_limited" ? "rate_limited" : "failed", ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Model source {Source} failed unexpectedly", sketchfab.Id);
                    (status, message) = ("failed", "Unexpected error while reading this source.");
                }
            }
        }

        var added = 0;
        var known = session.Models.Select(m => (m.Source, m.SourceItemId)).ToHashSet();
        var nextSort = session.Models.Count == 0 ? 0 : session.Models.Max(m => m.SortOrder) + 1;
        var fresh = new List<ModelCandidate>();
        foreach (var hit in hits ?? [])
        {
            if (known.Contains((sketchfab.Id, hit.ItemId)))
                continue;
            if (session.Models.Count >= mo.MaxModelsPerSession)
            {
                message = $"The session holds at most {mo.MaxModelsPerSession} models; remove some to add more.";
                break;
            }

            var license = LicenseInfo.Normalize(hit.License);
            var model = new ModelCandidate
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                Origin = ReferenceOrigins.Search,
                Source = sketchfab.Id,
                SourceItemId = Truncate(hit.ItemId, 100),
                Title = Truncate(hit.Title, 300),
                PageUrl = SourceText.Url(hit.PageUrl),
                Author = hit.Author is null ? null : Truncate(hit.Author, 200),
                License = license.Code,
                LicenseRaw = hit.License is null ? null : Truncate(hit.License, 100),
                Query = query,
                RetrievedAt = now,
                IsDownloadable = hit.IsDownloadable,
                FaceCount = hit.FaceCount,
                VertexCount = hit.VertexCount,
                FileBytes = hit.GlbBytes,
                ThumbnailUrl = SourceText.Url(hit.ThumbnailUrl),
                SortOrder = nextSort++,
            };
            db.ModelCandidates.Add(model); // EF fix-up also puts it in session.Models
            known.Add((model.Source, model.SourceItemId));
            fresh.Add(model);
            added++;
        }

        await Parallel.ForEachAsync(
            fresh,
            new ParallelOptions { MaxDegreeOfParallelism = o.DownloadConcurrency, CancellationToken = cancellationToken },
            async (model, token) =>
            {
                var preview = await FetchPreviewAsync(model.ThumbnailUrl, sketchfab.IsTrustedImageHost, sketchfab.Name, token);
                (model.DownloadStatus, model.DownloadError) = (preview.Status, preview.Error is null ? null : Truncate(preview.Error, 300));
                if (preview.Image is not null)
                {
                    await store.SaveAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(model.Id), ImageThumbnail.Create(preview.Image, o.ThumbnailMaxSide).Jpeg, token);
                    model.HasThumbnail = true;
                }
            });

        await RankSessionAsync(session, cancellationToken);
        session.LastModelSearchJson = JsonSerializer.Serialize(new[] { new SourceSearchResultDto(sketchfab.Id, sketchfab.Name, status, hits?.Count ?? 0, added, fromCache, message) }, Json);
        session.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            foreach (var model in fresh)
                await store.DeleteAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(model.Id), CancellationToken.None);
            throw;
        }

        logger.LogInformation("Model search in {SessionId}: {Status}, {Hits} hits, {Added} added", session.Id, status, hits?.Count ?? 0, added);
        return ToDto(session);
    }

    private static List<ModelHit>? DeserializeModels(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ModelHit>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Upload a GLB or binary STL to have its geometry checked against the session (licence and source as entered).</summary>
    public async Task<ReferenceSessionDto> AddModelAsync(Guid id, IFormFile? file, AddReferenceImageForm form, CancellationToken cancellationToken)
    {
        var mo = modelOptions.Value;
        var session = await LoadAsync(id, cancellationToken);
        if (session.Models.Count >= mo.MaxModelsPerSession)
            throw new ApiException(StatusCodes.Status409Conflict, "session_full", $"The session holds at most {mo.MaxModelsPerSession} models; remove some first.");
        var license = Clean(form.License) ?? LicenseInfo.Unknown;
        if (!LicenseInfo.Codes.Contains(license))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_license", $"License must be one of: {string.Join(", ", LicenseInfo.Codes)}.");
        var pageUrl = OptionalUrl(form.PageUrl, "page_url");
        var licenseUrl = OptionalUrl(form.LicenseUrl, "license_url");
        if (file is null || file.Length == 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "missing_file", "Select a GLB or STL file to add.");
        if (file.Length > mo.MaxUploadBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"The model is larger than the {mo.MaxUploadBytes / (1024 * 1024)} MB limit.");

        byte[] bytes;
        await using (var input = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await input.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (session.Models.Any(m => m.Source == ModelUploadSource && m.SourceItemId == sha))
            throw new ApiException(StatusCodes.Status409Conflict, "already_added", "This exact file is already in the session.");

        var (format, mesh) = ReadModel(bytes, mo.MaxTriangles);
        var geometry = AnalyseModel(mesh, format);
        var model = new ModelCandidate
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Origin = ReferenceOrigins.Manual,
            Source = ModelUploadSource,
            SourceItemId = sha,
            Title = Truncate(Clean(form.Title) ?? Clean(Path.GetFileNameWithoutExtension(file.FileName)) ?? "Uploaded model", 300),
            PageUrl = pageUrl,
            Author = Clean(form.Author) is { } author ? Truncate(author, 200) : null,
            License = license,
            LicenseUrl = licenseUrl,
            RetrievedAt = time.GetUtcNow().UtcDateTime,
            FaceCount = geometry.Triangles,
            VertexCount = mesh.VertexCount,
            FileBytes = bytes.Length,
            Format = format,
            EvaluationStatus = ModelEvaluationStatuses.GeometryChecked,
            GeometryJson = JsonSerializer.Serialize(geometry, Json),
            SortOrder = session.Models.Count == 0 ? 0 : session.Models.Max(m => m.SortOrder) + 1,
        };

        // A shaded render stands in for a photo so the model can be compared visually.
        var render = MeshPreviewRenderer.Render(format == "stl" ? ZUpToYUp(mesh) : mesh, 384, yawDegrees: 25, pitchDegrees: 15);
        await store.SaveAsync(session.Id, ReferenceArtifactNames.ModelFile(model.Id, format), bytes, cancellationToken);
        await store.SaveAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(model.Id), ImageThumbnail.Create(render, options.Value.ThumbnailMaxSide).Jpeg, cancellationToken);
        (model.HasThumbnail, model.DownloadStatus) = (true, DownloadStatuses.Downloaded);
        db.ModelCandidates.Add(model);
        await RankSessionAsync(session, cancellationToken);
        session.UpdatedAt = model.RetrievedAt;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteAsync(session.Id, ReferenceArtifactNames.ModelFile(model.Id, format), CancellationToken.None);
            await store.DeleteAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(model.Id), CancellationToken.None);
            throw;
        }

        logger.LogInformation("Added model {ModelId} ({Format}, {Triangles} triangles) to reference session {SessionId}", model.Id, format, geometry.Triangles, session.Id);
        return ToDto(session);
    }

    /// <summary>Format from the content (never the file name): GLB magic or a size-consistent binary STL.</summary>
    private static (string Format, Mesh Mesh) ReadModel(byte[] bytes, int maxTriangles)
    {
        string format;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67)
            format = "glb";
        else if (bytes.Length >= 84 && bytes.Length == 84 + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)) * 50)
            format = "stl";
        else if (bytes.Length >= 5 && System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Equals("solid", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status400BadRequest, "unsupported_model", "ASCII STL is not supported yet. Export a binary STL or a GLB.");
        else
            throw new ApiException(StatusCodes.Status400BadRequest, "unsupported_model", "Upload a binary GLB or a binary STL file (OBJ, separate glTF + bin and ASCII STL are not supported yet).");

        if (format == "stl" && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)) > maxTriangles)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "model_too_large", $"The model has more than {maxTriangles:N0} triangles.");

        Mesh mesh;
        try
        {
            mesh = format == "glb" ? GlbReader.Read(bytes) : StlReader.ReadBinary(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or System.Text.Json.JsonException)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "unreadable_model", "The file could not be read as a " + format.ToUpperInvariant() + " mesh: " + ex.Message);
        }

        if (mesh.TriangleCount > maxTriangles)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "model_too_large", $"The model has {mesh.TriangleCount:N0} triangles; the limit is {maxTriangles:N0}.");
        var validation = MeshValidator.Validate(mesh);
        if (!validation.IsValid)
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_mesh", "The mesh is not usable: " + string.Join(" ", validation.Errors));
        return (format, mesh);
    }

    /// <summary>
    /// Topology on the welded mesh (exporters split vertices along seams) and the declared size:
    /// glTF is in metres by specification, STL has no unit (millimetres assumed).
    /// </summary>
    private static ModelGeometry AnalyseModel(Mesh mesh, string format)
    {
        var welded = MeshTopology.Weld(mesh, MeshTopology.DefaultWeldTolerance(mesh));
        var report = MeshTopology.Analyze(welded);
        var size = MeshOperations.ComputeBounds(mesh).Size;
        var mm = format == "glb" ? 1000.0 : 1.0;
        var (x, y, z) = (size.X * mm, size.Y * mm, size.Z * mm);
        var longest = Math.Max(x, Math.Max(y, z));
        var scale = format == "stl" ? "unitless" : longest is >= 5 and <= 3000 ? "plausible" : "suspicious";
        return new ModelGeometry(
            mesh.TriangleCount,
            report.ComponentCount,
            report.IsClosed,
            report.BoundaryEdgeCount,
            report.NonManifoldEdgeCount,
            report.DegenerateTriangleCount,
            Math.Round(x, 2),
            Math.Round(y, 2),
            Math.Round(z, 2),
            scale);
    }

    private static Mesh ZUpToYUp(Mesh mesh)
    {
        var p = (float[])mesh.Positions.Clone();
        for (var i = 0; i < p.Length; i += 3)
            (p[i + 1], p[i + 2]) = (p[i + 2], -p[i + 1]);
        return new Mesh(p, mesh.Indices, mesh.Colors);
    }

    public async Task<ReferenceSessionDto> UpdateModelAsync(Guid id, Guid modelId, UpdateModelRequest request, CancellationToken cancellationToken)
    {
        if (!ReferenceDecisions.All.Contains(request.Decision!))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_decision", "Decision must be 'pending', 'accepted' or 'rejected'.");
        var session = await LoadAsync(id, cancellationToken);
        var model = session.Models.FirstOrDefault(m => m.Id == modelId)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Model candidate not found.");
        model.Decision = request.Decision!;
        model.Note = Clean(request.Note);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(session);
    }

    public async Task<ReferenceSessionDto> RemoveModelAsync(Guid id, Guid modelId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        var model = session.Models.FirstOrDefault(m => m.Id == modelId)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Model candidate not found.");
        session.Models.Remove(model);
        db.ModelCandidates.Remove(model);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(model.Id), cancellationToken);
        if (model.Format is { } format)
            await store.DeleteAsync(session.Id, ReferenceArtifactNames.ModelFile(model.Id, format), cancellationToken);
        return ToDto(session);
    }

    public async Task<ReferenceImageFile> OpenModelThumbnailAsync(Guid id, Guid modelId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken, includeCandidates: false);
        if (!db.ModelCandidates.Any(m => m.SessionId == session.Id && m.Id == modelId && m.HasThumbnail))
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "No preview for this model.");
        return await OpenAsync(session.Id, ReferenceArtifactNames.ModelThumbnail(modelId), "image/jpeg", cancellationToken);
    }

    /// <summary>The uploaded file of a model candidate, as uploaded.</summary>
    public async Task<ModelFile> OpenModelFileAsync(Guid id, Guid modelId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken, includeCandidates: false);
        var model = db.ModelCandidates.FirstOrDefault(m => m.SessionId == session.Id && m.Id == modelId && m.Format != null)
                    ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "No uploaded file for this model.");
        var stream = await store.OpenReadAsync(session.Id, ReferenceArtifactNames.ModelFile(model.Id, model.Format!), cancellationToken)
                     ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The file is no longer available.");
        return new ModelFile(stream, model.Format == "glb" ? "model/gltf-binary" : "model/stl", $"model-{model.Id.ToString("N")[..8]}.{model.Format}");
    }

    private ModelCandidateDto ToDto(ModelCandidate m)
    {
        var license = LicenseInfo.Normalize(m.LicenseRaw ?? m.License, m.LicenseUrl);
        var sourceName = m.Source == ModelUploadSource ? "your upload" : sketchfab.Name;
        var warnings = new List<IssueDto>();
        if (!license.IsKnown)
            warnings.Add(new IssueDto("licence_unknown", "warning", "Licence unknown or not a standard open licence: read the source's terms before reusing this model.", "licence"));
        if (license.Derivatives == false)
            warnings.Add(new IssueDto("no_derivatives", "warning", "No-derivatives licence: it may not be modified or split into parts.", "licence"));
        if (license.CommercialUse == false)
            warnings.Add(new IssueDto("non_commercial", "info", "Non-commercial licence.", "licence"));
        if (m.DownloadStatus == DownloadStatuses.Failed)
            warnings.Add(new IssueDto("preview_failed", "warning", m.DownloadError ?? "The preview could not be downloaded.", "download"));

        return new ModelCandidateDto(
            m.Id,
            m.Origin,
            m.Source,
            sourceName,
            m.Title,
            m.PageUrl,
            m.Author,
            new ReferenceLicenseDto(license.Code, license.Label, m.LicenseUrl, m.LicenseRaw, license.CommercialUse, license.Derivatives, license.AttributionRequired),
            $"\"{m.Title}\" by {m.Author ?? "unknown author"}, {license.Label}" + (m.Source == ModelUploadSource ? "" : $", via {sourceName}"),
            m.Query,
            Utc(m.RetrievedAt),
            m.IsDownloadable,
            m.FaceCount,
            m.VertexCount,
            m.FileBytes,
            m.HasThumbnail,
            m.DownloadStatus,
            m.Format,
            m.EvaluationStatus,
            m.Score,
            m.Confidence,
            m.VisualSimilarity,
            Reasons(m.ReasonsJson),
            m.GeometryJson is null ? null : JsonSerializer.Deserialize<ModelGeometryDto>(m.GeometryJson, Json),
            m.Decision,
            m.Note,
            warnings);
    }
}
