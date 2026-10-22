using System.Security.Cryptography;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Embeddings;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Api.Services.References;

/// <summary>
/// Ranking: embeddings of every stored preview (cached by the SHA-256 of the exact bytes), the
/// input photo and the query, then <see cref="ReferenceRanker"/> for pictures and
/// <see cref="ModelEvaluator"/> for model candidates. Without the embedding model everything is
/// still ranked from non-visual signals and the session says why.
/// </summary>
public sealed partial class ReferenceService
{
    private static readonly (string Label, string[] Templates)[] ViewpointTemplates =
    [
        ("front", ["a photo of the front of a {0}.", "a front view of a {0}."]),
        ("back", ["a photo of the back of a {0}.", "a rear view of a {0}."]),
        ("side", ["a side view of a {0}.", "a profile view of a {0}."]),
        ("top", ["a top-down view of a {0}.", "a photo of a {0} seen from above."]),
        ("three_quarter", ["a three-quarter view of a {0}."]),
    ];

    private static readonly (string Label, string[] Templates)[] KindTemplates =
    [
        ("photo", ["a photo of a {0}.", "a real photograph of a {0}."]),
        ("render", ["a 3D render of a {0}.", "a CGI rendering of a {0}."]),
        ("drawing", ["a technical drawing of a {0}.", "a blueprint of a {0}.", "a line art sketch of a {0}."]),
    ];

    /// <summary>Re-rank pictures and models, using the current accepted pictures as extra evidence.</summary>
    public async Task<ReferenceSessionDto> RankAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(id, cancellationToken);
        await RankSessionAsync(session, cancellationToken);
        session.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(session);
    }

    private sealed record EmbeddingContext(
        Dictionary<Guid, float[]> Pictures,
        Dictionary<Guid, float[]> Models,
        float[]? Input,
        float[]? Query,
        PromptSet? Viewpoints,
        PromptSet? Kinds);

    /// <summary>Recomputes every ranking field of the session (pictures and models). Never throws for missing or failing embeddings.</summary>
    private async Task RankSessionAsync(ReferenceSession session, CancellationToken cancellationToken)
    {
        var context = new EmbeddingContext([], [], null, null, null, null);
        string mode = "basic";
        string? note;
        if (await embedder.EnsureReadyAsync(cancellationToken))
        {
            try
            {
                context = await EmbedSessionAsync(session, cancellationToken);
                mode = "embeddings";
                var withoutPreview = session.Candidates.Count(c => !context.Pictures.ContainsKey(c.Id));
                note = withoutPreview > 0 ? $"{withoutPreview} picture(s) without a preview were ranked without visual signals." : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Visual ranking failed for reference session {SessionId}", session.Id);
                context = new EmbeddingContext([], [], null, null, null, null);
                note = "Visual ranking failed (see the server log); ranked without embeddings.";
            }
        }
        else
        {
            note = embedder.Status.Reason;
        }

        var inputView = context.Input is { } inputVector && context.Viewpoints is { } views ? ReferenceRanker.Classify(inputVector, views) : null;
        var items = session.Candidates.Select(c => new RankItem(
            c.Id,
            c.SortOrder,
            context.Pictures.GetValueOrDefault(c.Id),
            MinSide(c),
            LicenseTone(LicenseInfo.Normalize(c.LicenseRaw ?? c.License, c.LicenseUrl)),
            c.Source,
            c.DuplicateOfId is not null,
            c.DuplicateOfId,
            c.MatchesInput,
            c.Decision == ReferenceDecisions.Accepted)).ToList();
        var results = ReferenceRanker.Rank(items, new RankContext(context.Query, context.Input, inputView, context.Viewpoints, context.Kinds, embeddingOptions.Value.GroupThreshold));

        foreach (var c in session.Candidates)
        {
            var r = results[c.Id];
            c.RankPosition = r.Position;
            c.RankScore = r.Score;
            c.RankConfidence = r.Confidence;
            c.RankReasonsJson = JsonSerializer.Serialize(r.Reasons, Json);
            c.GroupIndex = r.Group;
            c.QuerySimilarity = r.QuerySimilarity;
            c.InputSimilarity = r.InputSimilarity;
            (c.SuggestedViewpoint, c.ViewpointConfidence) = (r.Viewpoint?.Label, r.Viewpoint?.Confidence);
            (c.SuggestedKind, c.KindConfidence) = (r.Kind?.Label, r.Kind?.Confidence);
            (c.SuggestedRelation, c.RelationConfidence) = (r.Relation?.Label, r.Relation?.Confidence);
        }

        EvaluateModels(session, context);

        session.RankedAt = time.GetUtcNow().UtcDateTime;
        session.RankingMode = mode;
        session.RankingNote = note is null ? null : Truncate(note, 300);
        (session.InputViewpoint, session.InputViewpointConfidence) = (inputView?.Label, inputView?.Confidence);
    }

    private void EvaluateModels(ReferenceSession session, EmbeddingContext context)
    {
        // Visual evidence of what the object looks like: the input photo and accepted pictures.
        var evidence = session.Candidates
            .Where(c => c.Decision == ReferenceDecisions.Accepted && context.Pictures.ContainsKey(c.Id))
            .Select(c => context.Pictures[c.Id])
            .ToList();
        if (context.Input is { } input)
            evidence.Add(input);

        foreach (var m in session.Models)
        {
            var preview = context.Models.GetValueOrDefault(m.Id);
            double? visual = preview is not null && evidence.Count > 0 ? evidence.Max(e => EmbeddingMath.Cosine(preview, e)) : null;
            double? query = preview is not null && context.Query is { } q ? EmbeddingMath.Cosine(preview, q) : null;
            var license = LicenseInfo.Normalize(m.LicenseRaw ?? m.License, m.LicenseUrl);
            var geometry = m.GeometryJson is null ? null : JsonSerializer.Deserialize<ModelGeometry>(m.GeometryJson, Json);
            var evaluation = ModelEvaluator.Evaluate(new ModelFacts(
                m.Origin == ReferenceOrigins.Manual,
                m.IsDownloadable,
                m.FaceCount,
                license.Derivatives,
                license.IsKnown,
                visual,
                query,
                geometry));
            m.Score = evaluation.Score;
            m.Confidence = evaluation.Confidence;
            m.ReasonsJson = JsonSerializer.Serialize(evaluation.Reasons, Json);
            m.VisualSimilarity = visual is { } v ? Math.Round(v, 4) : null;
        }
    }

    private async Task<EmbeddingContext> EmbedSessionAsync(ReferenceSession session, CancellationToken cancellationToken)
    {
        var fresh = new Dictionary<string, float[]>();
        async Task<float[]?> Embed(string fileName)
        {
            var bytes = await store.ReadAllAsync(session.Id, fileName, cancellationToken);
            return bytes is null ? null : await EmbedCachedAsync(bytes, fresh, cancellationToken);
        }

        var pictures = new Dictionary<Guid, float[]>();
        foreach (var c in session.Candidates.Where(c => c.HasThumbnail))
        {
            if (await Embed(ReferenceArtifactNames.Thumbnail(c.Id)) is { } v)
                pictures[c.Id] = v;
        }

        var models = new Dictionary<Guid, float[]>();
        foreach (var m in session.Models.Where(m => m.HasThumbnail))
        {
            if (await Embed(ReferenceArtifactNames.ModelThumbnail(m.Id)) is { } v)
                models[m.Id] = v;
        }

        var input = session.HasInputImage ? await Embed(ReferenceArtifactNames.InputThumbnail) : null;
        var subject = Subject(session.Query);
        var query = session.Query.Length >= 2 ? embedder.EmbedText($"a photo of a {subject}.", cancellationToken) : null;
        return new EmbeddingContext(pictures, models, input, query, Prompts(subject, ViewpointTemplates, cancellationToken), Prompts(subject, KindTemplates, cancellationToken));
    }

    /// <summary>Embedding of an image file, from the cache when the same bytes were embedded before by the same model.</summary>
    private async Task<float[]> EmbedCachedAsync(byte[] bytes, Dictionary<string, float[]> fresh, CancellationToken cancellationToken)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (fresh.TryGetValue(sha, out var known))
            return known;
        if (await db.ImageEmbeddingCache.FindAsync([embedder.ModelId, sha], cancellationToken) is { } cached)
            return fresh[sha] = EmbeddingMath.FromBytes(cached.Vector);

        var vector = embedder.EmbedImage(ImageDecoder.Decode(bytes, PreviewLimits).Image, cancellationToken);
        db.ImageEmbeddingCache.Add(new ImageEmbeddingCacheEntry
        {
            Model = embedder.ModelId,
            ImageSha256 = sha,
            Vector = EmbeddingMath.ToBytes(vector),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
        return fresh[sha] = vector;
    }

    private PromptSet Prompts(string subject, (string Label, string[] Templates)[] templates, CancellationToken cancellationToken) =>
        new(templates.Select(t => (t.Label, EmbeddingMath.Mean(t.Templates.Select(p => embedder.EmbedText(string.Format(System.Globalization.CultureInfo.InvariantCulture, p, subject), cancellationToken)).ToList()))).ToList());

    /// <summary>What the prompts talk about: the query (shortened) or a neutral word.</summary>
    private static string Subject(string query) => query.Length >= 2 ? (query.Length <= 60 ? query : query[..60]) : "object";

    /// <summary>Short side of the full image when the source reported it, else of the stored preview.</summary>
    private static int? MinSide(ReferenceCandidate c) =>
        c.Width is { } w && c.Height is { } h ? Math.Min(w, h)
        : c.ThumbnailWidth is { } tw && c.ThumbnailHeight is { } th ? Math.Min(tw, th)
        : null;

    private static string LicenseTone(LicenseInfo license) =>
        !license.IsKnown ? "unknown" : license.CommercialUse == false || license.Derivatives == false ? "restricted" : "free";
}
