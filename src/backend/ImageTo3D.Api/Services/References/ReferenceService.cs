using System.Security.Cryptography;
using System.Text.Json;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Imaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.References;

public sealed record ReferenceImageFile(Stream Content, string ContentType);

/// <summary>Optional details of a manually added reference picture (multipart form fields).</summary>
public sealed class AddReferenceImageForm
{
    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? PageUrl { get; set; }

    /// <summary>One of <see cref="LicenseInfo.Codes"/>; "unknown" when omitted.</summary>
    public string? License { get; set; }

    public string? LicenseUrl { get; set; }
}

/// <summary>
/// Reference-search sessions: free sources searched in parallel, each with its own timeout,
/// retries and cache, so one failing source never fails the search and the session stays usable
/// when all of them are down (manual links and uploads always work). Search results keep their
/// source, author, licence and retrieval time; only small previews are downloaded, from hosts the
/// adapter trusts. Pictures are fingerprinted (SHA-256 and dHash) to flag duplicates.
/// </summary>
public sealed class ReferenceService(
    AppDbContext db,
    IArtifactStore jobStore,
    IReferenceArtifactStore store,
    ReferenceDownloadCache cache,
    ReferenceHttp http,
    IEnumerable<IReferenceSource> sources,
    IOwnerAccessor owner,
    IOptions<ReferenceSearchOptions> options,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider time,
    ILogger<ReferenceService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Remote previews may be small; full uploads use the regular upload limits.</summary>
    private static readonly ImageLimits PreviewLimits = new(16, 8192, 40_000_000);

    private const string UploadSource = "upload";

    private readonly IReadOnlyList<IReferenceSource> _sources = sources.ToList();

    public IReadOnlyList<ReferenceSourceDto> ListSources() =>
        _sources.Select(s => new ReferenceSourceDto(s.Id, s.Name, s.Description, s.Enabled)).ToList();

    // ---- Sessions ----

    public async Task<ReferenceSessionDto> CreateAsync(IFormFile? image, string? name, string? query, Guid? sourceJobId, CancellationToken cancellationToken)
    {
        var ownerHash = owner.OwnerKeyHash;
        var o = options.Value;
        if (await db.ReferenceSessions.CountAsync(s => s.OwnerKeyHash == ownerHash, cancellationToken) >= o.MaxSessionsPerOwner)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_sessions", $"You already have {o.MaxSessionsPerOwner} reference searches. Delete one first.");
        if (image is { Length: > 0 } && sourceJobId is not null)
            throw new ApiException(StatusCodes.Status400BadRequest, "ambiguous_input", "Give either an image or a source job, not both.");

        RgbaImage? input = null;
        string? defaultName = null;
        if (image is { Length: > 0 })
        {
            input = (await DecodeUploadAsync(image, cancellationToken)).Decoded.Image;
            defaultName = Path.GetFileNameWithoutExtension(image.FileName);
        }
        else if (sourceJobId is { } jobId)
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.OwnerKeyHash == ownerHash, cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status404NotFound, "source_not_found", "Source job not found.");
            var png = await jobStore.ReadAllAsync(job.Id, ArtifactNames.FileName(ArtifactKind.SourceImage), cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The job's image is no longer available.");
            input = ImageDecoder.Decode(png, PreviewLimits).Image;
            defaultName = Path.GetFileNameWithoutExtension(job.OriginalFileName);
        }

        var cleanQuery = ReferenceQuery.Clean(query);
        var now = time.GetUtcNow().UtcDateTime;
        var session = new ReferenceSession
        {
            Id = Guid.NewGuid(),
            OwnerKeyHash = ownerHash,
            Name = Truncate(Clean(name) ?? (cleanQuery.Length > 0 ? cleanQuery : null) ?? Clean(defaultName) ?? "Reference search", 100),
            Query = cleanQuery,
            SourceJobId = sourceJobId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (input is not null)
        {
            session.HasInputImage = true;
            session.InputWidth = input.Width;
            session.InputHeight = input.Height;
            session.InputDHash = unchecked((long)ImageHash.DifferenceHash(input));
            await store.SaveAsync(session.Id, ReferenceArtifactNames.InputImage, ImageDecoder.EncodePng(input), cancellationToken);
            await store.SaveAsync(session.Id, ReferenceArtifactNames.InputThumbnail, ImageThumbnail.Create(input, o.ThumbnailMaxSide).Jpeg, cancellationToken);
        }

        db.ReferenceSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteSessionAsync(session.Id, CancellationToken.None);
            throw;
        }

        logger.LogInformation("Created reference session {SessionId} (input image: {HasInput})", session.Id, session.HasInputImage);
        return ToDto(session);
    }

    public async Task<PagedResult<ReferenceSessionSummaryDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.ReferenceSessions.AsNoTracking().Where(s => s.OwnerKeyHash == owner.OwnerKeyHash);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new ReferenceSessionSummaryDto(
                s.Id, s.Name, s.Query, s.HasInputImage, s.Candidates.Count, s.Candidates.Count(c => c.Decision == ReferenceDecisions.Accepted), s.CreatedAt, s.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<ReferenceSessionSummaryDto>(items.Select(i => i with { CreatedAt = Utc(i.CreatedAt), UpdatedAt = Utc(i.UpdatedAt) }).ToList(), page, pageSize, total);
    }

    public async Task<ReferenceSessionDto> GetAsync(Guid id, CancellationToken cancellationToken) => ToDto(await LoadAsync(id, cancellationToken));

    public async Task<ReferenceSessionDto> UpdateAsync(Guid id, UpdateReferenceSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        session.Name = Truncate(request.Name!.Trim(), 100);
        if (request.Query is not null)
            session.Query = ReferenceQuery.Clean(request.Query);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(session);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        db.ReferenceSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteSessionAsync(session.Id, cancellationToken);
        logger.LogInformation("Deleted reference session {SessionId}", session.Id);
    }

    // ---- Search ----

    private sealed record SourceRun(IReferenceSource Source, IReadOnlyList<ReferenceHit>? Hits, bool FromCache, string Status, string? Message);

    public async Task<ReferenceSessionDto> SearchAsync(Guid id, SearchReferencesRequest request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var session = await LoadAsync(id, cancellationToken);
        if (request.Query is not null)
            session.Query = ReferenceQuery.Clean(request.Query);
        var query = session.Query;
        if (query.Length < 2)
            throw new ApiException(StatusCodes.Status400BadRequest, "query_required", "Enter at least two characters to search for (for example the kit or part name).");

        if (request.Sources?.FirstOrDefault(s => _sources.All(x => x.Id != s)) is { } unknown)
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_source", $"Unknown source '{Truncate(unknown, 40)}'. Use one of: {string.Join(", ", _sources.Select(s => s.Id))}.");
        var selected = request.Sources is null ? _sources : _sources.Where(s => request.Sources.Contains(s.Id)).ToList();

        var now = time.GetUtcNow().UtcDateTime;
        var runs = new List<SourceRun>();
        var toFetch = new List<IReferenceSource>();
        foreach (var source in selected)
        {
            if (!source.Enabled)
            {
                runs.Add(new SourceRun(source, null, false, "disabled", "Turned off in the server configuration."));
                continue;
            }

            var entry = await db.ReferenceSearchCache.FindAsync([ReferenceQuery.CacheKey(source.Id, query, o.ResultsPerSource)], cancellationToken);
            if (entry is not null && entry.ExpiresAt > now && Deserialize(entry.HitsJson) is { } cached)
                runs.Add(new SourceRun(source, cached, true, cached.Count == 0 ? "empty" : "ok", null));
            else
                toFetch.Add(source);
        }

        // Network calls run in parallel; the DbContext is only used again once they are all done.
        foreach (var run in await Task.WhenAll(toFetch.Select(s => FetchAsync(s, query, cancellationToken))))
        {
            runs.Add(run);
            if (run.Hits is not null)
                await PutCacheAsync(run.Source.Id, query, run.Hits, now, cancellationToken);
        }

        runs = runs.OrderBy(r => _sources.TakeWhile(s => s != r.Source).Count()).ToList(); // configuration order, not completion order

        // Merge: a hit already in the session (same source and item) is not added again.
        var existing = session.Candidates.ToDictionary(c => (c.Source, c.SourceItemId));
        var nextSort = session.Candidates.Count == 0 ? 0 : session.Candidates.Max(c => c.SortOrder) + 1;
        var downloads = new List<(ReferenceCandidate Candidate, IReferenceSource Source)>();
        var added = new Dictionary<string, int>();
        var full = false;
        foreach (var run in runs.Where(r => r.Hits is not null))
        {
            foreach (var hit in run.Hits!)
            {
                if (existing.TryGetValue((run.Source.Id, hit.ItemId), out var known))
                {
                    if (!known.HasThumbnail && known.DownloadStatus == DownloadStatuses.Failed)
                        downloads.Add((known, run.Source)); // retry a preview that failed last time
                    continue;
                }

                if (session.Candidates.Count >= o.MaxCandidatesPerSession)
                {
                    full = true;
                    break;
                }

                var license = LicenseInfo.Normalize(hit.License, hit.LicenseUrl);
                var candidate = new ReferenceCandidate
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    Origin = ReferenceOrigins.Search,
                    Source = run.Source.Id,
                    SourceItemId = Truncate(hit.ItemId, 100),
                    Title = Truncate(hit.Title, 300),
                    PageUrl = SourceText.Url(hit.PageUrl),
                    ImageUrl = SourceText.Url(hit.ImageUrl),
                    ThumbnailUrl = SourceText.Url(hit.ThumbnailUrl),
                    Author = hit.Author is null ? null : Truncate(hit.Author, 200),
                    License = license.Code,
                    LicenseRaw = hit.License is null ? null : Truncate(hit.License, 100),
                    LicenseUrl = SourceText.Url(hit.LicenseUrl),
                    Width = hit.Width,
                    Height = hit.Height,
                    Query = query,
                    RetrievedAt = now,
                    SortOrder = nextSort++,
                };
                db.ReferenceCandidates.Add(candidate); // EF fix-up also puts it in session.Candidates (the session is tracked)
                existing[(candidate.Source, candidate.SourceItemId)] = candidate;
                downloads.Add((candidate, run.Source));
                added[run.Source.Id] = added.GetValueOrDefault(run.Source.Id) + 1;
            }
        }

        await Parallel.ForEachAsync(
            downloads,
            new ParallelOptions { MaxDegreeOfParallelism = o.DownloadConcurrency, CancellationToken = cancellationToken },
            async (item, token) => await DownloadPreviewAsync(session.Id, item.Candidate, item.Source, token));

        AssignDuplicates(session);

        var outcomes = runs.Select(r => new SourceSearchResultDto(
            r.Source.Id,
            r.Source.Name,
            r.Status,
            r.Hits?.Count ?? 0,
            added.GetValueOrDefault(r.Source.Id),
            r.FromCache,
            full && r.Hits is { Count: > 0 } ? $"The session holds at most {o.MaxCandidatesPerSession} pictures; remove some to add more." : r.Message)).ToList();
        session.LastSearchJson = JsonSerializer.Serialize(outcomes, Json);
        session.LastSearchedAt = now;
        session.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            foreach (var (candidate, _) in downloads.Where(d => db.Entry(d.Candidate).State == EntityState.Added))
                await store.DeleteAsync(session.Id, ReferenceArtifactNames.Thumbnail(candidate.Id), CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Reference search in {SessionId}: {Summary}",
            session.Id,
            string.Join(", ", outcomes.Select(x => $"{x.Source}={x.Status}/{x.Hits}{(x.FromCache ? " (cached)" : "")}")));
        return ToDto(session);
    }

    private async Task<SourceRun> FetchAsync(IReferenceSource source, string query, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(o.SourceTimeout);
        try
        {
            var hits = (await source.SearchAsync(query, o.ResultsPerSource, timeout.Token)).Take(o.ResultsPerSource).ToList();
            return new SourceRun(source, hits, false, hits.Count == 0 ? "empty" : "ok", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SourceRun(source, null, false, "timeout", $"No answer within {o.SourceTimeout.TotalSeconds:0} s.");
        }
        catch (ReferenceFetchException ex)
        {
            logger.LogInformation("Reference source {Source} failed: {Code} {Message}", source.Id, ex.Code, ex.Message);
            return new SourceRun(source, null, false, ex.Code == "rate_limited" ? "rate_limited" : "failed", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reference source {Source} failed unexpectedly", source.Id);
            return new SourceRun(source, null, false, "failed", "Unexpected error while reading this source.");
        }
    }

    private async Task PutCacheAsync(string source, string query, IReadOnlyList<ReferenceHit> hits, DateTime now, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var key = ReferenceQuery.CacheKey(source, query, o.ResultsPerSource);
        var entry = await db.ReferenceSearchCache.FindAsync([key], cancellationToken);
        if (entry is null)
        {
            entry = new ReferenceSearchCacheEntry { Key = key, Source = source, Query = query, HitsJson = "[]" };
            db.ReferenceSearchCache.Add(entry);
        }

        entry.HitsJson = JsonSerializer.Serialize(hits, Json);
        entry.FetchedAt = now;
        entry.ExpiresAt = now + o.CacheTtl;
    }

    private static List<ReferenceHit>? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ReferenceHit>>(json, Json);
        }
        catch (JsonException)
        {
            return null; // unreadable entry: treated as a miss and overwritten
        }
    }

    private async Task DownloadPreviewAsync(Guid sessionId, ReferenceCandidate candidate, IReferenceSource source, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (candidate.ThumbnailUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            SetDownload(candidate, DownloadStatuses.Skipped, "The source offers no preview for this item.");
            return;
        }

        if (!source.IsTrustedImageHost(uri))
        {
            SetDownload(candidate, DownloadStatuses.Skipped, $"Preview not downloaded: {uri.Host} is not a host {source.Name} serves images from.");
            return;
        }

        try
        {
            var bytes = await cache.TryReadAsync(url, cancellationToken);
            if (bytes is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(o.ThumbnailTimeout);
                bytes = await http.DownloadImageAsync(uri, source.IsTrustedImageHost, o.ThumbnailMaxBytes, timeout.Token);
                await cache.WriteAsync(url, bytes, cancellationToken);
            }

            await StorePictureAsync(sessionId, candidate, bytes, ImageDecoder.Decode(bytes, PreviewLimits).Image, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetDownload(candidate, DownloadStatuses.Failed, $"The preview did not arrive within {o.ThumbnailTimeout.TotalSeconds:0} s.");
        }
        catch (ReferenceFetchException ex)
        {
            SetDownload(candidate, DownloadStatuses.Failed, ex.Message);
        }
        catch (ImageValidationException ex)
        {
            SetDownload(candidate, DownloadStatuses.Failed, "The preview is not a usable PNG, JPEG or WebP image: " + ex.Message);
        }
    }

    private async Task StorePictureAsync(Guid sessionId, ReferenceCandidate candidate, byte[] bytes, RgbaImage image, CancellationToken cancellationToken)
    {
        var thumbnail = ImageThumbnail.Create(image, options.Value.ThumbnailMaxSide);
        await store.SaveAsync(sessionId, ReferenceArtifactNames.Thumbnail(candidate.Id), thumbnail.Jpeg, cancellationToken);
        candidate.HasThumbnail = true;
        candidate.ThumbnailWidth = thumbnail.Width;
        candidate.ThumbnailHeight = thumbnail.Height;
        candidate.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        candidate.DHash = unchecked((long)ImageHash.DifferenceHash(image));
        SetDownload(candidate, DownloadStatuses.Downloaded, null);
    }

    private static void SetDownload(ReferenceCandidate candidate, string status, string? error)
    {
        candidate.DownloadStatus = status;
        candidate.DownloadError = error is null ? null : Truncate(error, 300);
    }

    private void AssignDuplicates(ReferenceSession session)
    {
        var ordered = session.Candidates.OrderBy(c => c.SortOrder)
            .Select(c => new ReferenceDeduplicator.Item(c.Id, c.Sha256, c.DHash is { } h ? unchecked((ulong)h) : null))
            .ToList();
        var results = ReferenceDeduplicator.Assign(ordered, session.InputDHash is { } i ? unchecked((ulong)i) : null, options.Value.NearDuplicateDistance);
        foreach (var candidate in session.Candidates)
        {
            var r = results[candidate.Id];
            candidate.DuplicateOfId = r.DuplicateOf;
            candidate.DuplicateKind = r.Kind;
            candidate.MatchesInput = r.MatchesInput;
        }
    }

    // ---- Candidates ----

    /// <summary>Add a picture the user has (a photo, a scan, a saved web image). It is accepted right away.</summary>
    public async Task<ReferenceSessionDto> AddManualAsync(Guid id, IFormFile? image, AddReferenceImageForm form, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        if (session.Candidates.Count >= options.Value.MaxCandidatesPerSession)
            throw new ApiException(StatusCodes.Status409Conflict, "session_full", $"The session holds at most {options.Value.MaxCandidatesPerSession} pictures; remove some first.");

        var license = Clean(form.License) ?? LicenseInfo.Unknown;
        if (!LicenseInfo.Codes.Contains(license))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_license", $"License must be one of: {string.Join(", ", LicenseInfo.Codes)}.");
        var pageUrl = OptionalUrl(form.PageUrl, "page_url");
        var licenseUrl = OptionalUrl(form.LicenseUrl, "license_url");

        if (image is null || image.Length == 0)
            throw new ApiException(StatusCodes.Status400BadRequest, "missing_file", "Select an image file to add.");
        var (bytes, decoded) = await DecodeUploadAsync(image, cancellationToken);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (session.Candidates.Any(c => c.Source == UploadSource && c.SourceItemId == sha))
            throw new ApiException(StatusCodes.Status409Conflict, "already_added", "This exact file is already in the session.");

        var candidate = new ReferenceCandidate
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Origin = ReferenceOrigins.Manual,
            Source = UploadSource,
            SourceItemId = sha,
            Title = Truncate(Clean(form.Title) ?? Clean(Path.GetFileNameWithoutExtension(image.FileName)) ?? "Uploaded picture", 300),
            PageUrl = pageUrl,
            Author = Clean(form.Author) is { } author ? Truncate(author, 200) : null,
            License = license,
            LicenseUrl = licenseUrl,
            Width = decoded.Image.Width,
            Height = decoded.Image.Height,
            RetrievedAt = time.GetUtcNow().UtcDateTime,
            Decision = ReferenceDecisions.Accepted,
            SortOrder = session.Candidates.Count == 0 ? 0 : session.Candidates.Max(c => c.SortOrder) + 1,
            HasFullImage = true,
        };

        await StorePictureAsync(session.Id, candidate, bytes, decoded.Image, cancellationToken);
        await store.SaveAsync(session.Id, ReferenceArtifactNames.FullImage(candidate.Id), ImageDecoder.EncodePng(decoded.Image), cancellationToken);
        db.ReferenceCandidates.Add(candidate); // EF fix-up also puts it in session.Candidates
        AssignDuplicates(session);
        session.UpdatedAt = candidate.RetrievedAt;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteAsync(session.Id, ReferenceArtifactNames.Thumbnail(candidate.Id), CancellationToken.None);
            await store.DeleteAsync(session.Id, ReferenceArtifactNames.FullImage(candidate.Id), CancellationToken.None);
            throw;
        }

        return ToDto(session);
    }

    public async Task<ReferenceSessionDto> UpdateCandidateAsync(Guid id, Guid candidateId, UpdateCandidateRequest request, CancellationToken cancellationToken)
    {
        if (!ReferenceDecisions.All.Contains(request.Decision!))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_decision", "Decision must be 'pending', 'accepted' or 'rejected'.");
        if (request.ImageKind is not null && !ReferenceImageKinds.All.Contains(request.ImageKind))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_image_kind", $"Image kind must be one of: {string.Join(", ", ReferenceImageKinds.All)}.");
        if (request.Relation is not null && !ReferenceRelations.All.Contains(request.Relation))
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_relation", $"Relation must be one of: {string.Join(", ", ReferenceRelations.All)}.");

        var session = await LoadAsync(id, cancellationToken);
        var candidate = session.Candidates.FirstOrDefault(c => c.Id == candidateId)
                        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Reference picture not found.");
        candidate.Decision = request.Decision!;
        candidate.ImageKind = request.ImageKind ?? candidate.ImageKind;
        candidate.Relation = request.Relation ?? candidate.Relation;
        candidate.Note = Clean(request.Note);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(session);
    }

    public async Task<ReferenceSessionDto> RemoveCandidateAsync(Guid id, Guid candidateId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        var candidate = session.Candidates.FirstOrDefault(c => c.Id == candidateId)
                        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Reference picture not found.");
        session.Candidates.Remove(candidate);
        db.ReferenceCandidates.Remove(candidate);
        AssignDuplicates(session);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteAsync(session.Id, ReferenceArtifactNames.Thumbnail(candidate.Id), cancellationToken);
        await store.DeleteAsync(session.Id, ReferenceArtifactNames.FullImage(candidate.Id), cancellationToken);
        return ToDto(session);
    }

    // ---- Files ----

    public async Task<ReferenceImageFile> OpenInputAsync(Guid id, bool thumbnail, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken, includeCandidates: false);
        if (!session.HasInputImage)
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "This session has no input image.");
        return thumbnail
            ? await OpenAsync(session.Id, ReferenceArtifactNames.InputThumbnail, "image/jpeg", cancellationToken)
            : await OpenAsync(session.Id, ReferenceArtifactNames.InputImage, "image/png", cancellationToken);
    }

    public async Task<ReferenceImageFile> OpenThumbnailAsync(Guid id, Guid candidateId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken, includeCandidates: false);
        var exists = await db.ReferenceCandidates.AnyAsync(c => c.SessionId == session.Id && c.Id == candidateId && c.HasThumbnail, cancellationToken);
        if (!exists)
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "No preview for this picture.");
        return await OpenAsync(session.Id, ReferenceArtifactNames.Thumbnail(candidateId), "image/jpeg", cancellationToken);
    }

    private async Task<ReferenceImageFile> OpenAsync(Guid sessionId, string fileName, string contentType, CancellationToken cancellationToken) =>
        new(await store.OpenReadAsync(sessionId, fileName, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", "The file is no longer available."), contentType);

    /// <summary>Drops cached search responses and downloads (shared, disposable). Session data is not touched.</summary>
    public async Task<ClearReferenceCacheDto> ClearCacheAsync(CancellationToken cancellationToken)
    {
        var entries = await db.ReferenceSearchCache.ExecuteDeleteAsync(cancellationToken);
        var (files, bytes) = cache.Clear();
        logger.LogInformation("Cleared reference cache: {Entries} search entries, {Files} files ({Bytes} bytes)", entries, files, bytes);
        return new ClearReferenceCacheDto(entries, files, bytes);
    }

    // ---- Helpers ----

    private async Task<ReferenceSession> LoadAsync(Guid id, CancellationToken cancellationToken, bool includeCandidates = true)
    {
        IQueryable<ReferenceSession> query = db.ReferenceSessions;
        if (includeCandidates)
            query = query.Include(s => s.Candidates);
        return await query.FirstOrDefaultAsync(s => s.Id == id && s.OwnerKeyHash == owner.OwnerKeyHash, cancellationToken)
               ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Reference search not found.");
    }

    private async Task<(byte[] Bytes, DecodedImage Decoded)> DecodeUploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var o = uploadOptions.Value;
        if (file.Length > o.MaxBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "file_too_large", $"The image is larger than the {o.MaxBytes / (1024 * 1024)} MB limit.");
        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        try
        {
            return (bytes, ImageDecoder.Decode(bytes, new ImageLimits(o.MinDimension, o.MaxDimension, o.MaxPixels)));
        }
        catch (ImageValidationException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_image", ex.Message);
        }
    }

    private static string? OptionalUrl(string? value, string field)
    {
        if (Clean(value) is not { } text)
            return null;
        return SourceText.Url(text) ?? throw new ApiException(StatusCodes.Status400BadRequest, "invalid_" + field, "Links must be absolute http:// or https:// addresses.");
    }

    private ReferenceSessionDto ToDto(ReferenceSession s)
    {
        var lastSearch = JsonSerializer.Deserialize<List<SourceSearchResultDto>>(s.LastSearchJson, Json) ?? [];
        return new ReferenceSessionDto(
            s.Id,
            s.Name,
            s.Query,
            s.SourceJobId,
            s.HasInputImage,
            s.InputWidth,
            s.InputHeight,
            Utc(s.CreatedAt),
            Utc(s.UpdatedAt),
            s.LastSearchedAt is { } t ? Utc(t) : null,
            lastSearch,
            ManualSearchLinks.For(s.Query).Select(l => new ManualSearchLinkDto(l.Name, l.Url, l.Kind)).ToList(),
            s.Candidates.OrderBy(c => c.SortOrder).Select(ToDto).ToList());
    }

    private ReferenceCandidateDto ToDto(ReferenceCandidate c)
    {
        // The source's own wording keeps details the code drops (the licence version).
        var license = LicenseInfo.Normalize(c.LicenseRaw ?? c.License, c.LicenseUrl);
        var sourceName = c.Source == UploadSource ? "your upload" : _sources.FirstOrDefault(s => s.Id == c.Source)?.Name ?? c.Source;
        var attribution = c.License == "own_work"
            ? $"\"{c.Title}\", own photo"
            : $"\"{c.Title}\" by {c.Author ?? "unknown author"}, {license.Label}" + (c.Source == UploadSource ? "" : $", via {sourceName}");

        var warnings = new List<IssueDto>();
        if (!license.IsKnown)
            warnings.Add(new IssueDto("licence_unknown", "warning", "No usable licence information: keep this picture for personal reference only and do not redistribute it.", "licence"));
        if (license.CommercialUse == false)
            warnings.Add(new IssueDto("non_commercial", "info", "Non-commercial licence: do not use it in anything that is sold.", "licence"));
        if (license.Derivatives == false)
            warnings.Add(new IssueDto("no_derivatives", "info", "No-derivatives licence: use it to look at; do not publish modified copies.", "licence"));
        if (c.DownloadStatus == DownloadStatuses.Failed)
            warnings.Add(new IssueDto("preview_failed", "warning", c.DownloadError ?? "The preview could not be downloaded.", "download"));
        if (c.DuplicateKind is { } kind)
            warnings.Add(new IssueDto("duplicate", "info", kind == "exact" ? "Same file as another picture in this session." : "Same picture as another one in this session (re-encoded, resized or lightly edited).", "duplicates"));
        if (c.MatchesInput)
            warnings.Add(new IssueDto("matches_input", "info", "Looks like your input photo itself, so it adds no new view.", "duplicates"));

        return new ReferenceCandidateDto(
            c.Id,
            c.Origin,
            c.Source,
            sourceName,
            c.Title,
            c.PageUrl,
            c.ImageUrl,
            c.Author,
            new ReferenceLicenseDto(license.Code, license.Label, c.LicenseUrl, c.LicenseRaw, license.CommercialUse, license.Derivatives, license.AttributionRequired),
            attribution,
            c.Query,
            Utc(c.RetrievedAt),
            c.DownloadStatus,
            c.DownloadError,
            c.HasThumbnail,
            c.ThumbnailWidth,
            c.ThumbnailHeight,
            c.Width,
            c.Height,
            c.DuplicateOfId,
            c.DuplicateKind,
            c.MatchesInput,
            c.Decision,
            c.ImageKind,
            c.Relation,
            c.Note,
            warnings);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
