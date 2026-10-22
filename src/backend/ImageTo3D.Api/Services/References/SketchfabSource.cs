using System.Globalization;
using System.Text.Json;
using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.References;

/// <summary>A 3D model listing as the source described it.</summary>
public sealed record ModelHit(
    string ItemId,
    string Title,
    string? PageUrl,
    string? Author,
    string? License,
    bool? IsDownloadable,
    int? FaceCount,
    int? VertexCount,
    long? GlbBytes,
    string? ThumbnailUrl);

/// <summary>
/// Sketchfab Data API v3 model search, anonymous (no token, no account). Only listings are read:
/// downloading a model needs a personal API token and is left to the user, who can then upload
/// the file here for a geometric check. Age-restricted listings are skipped.
/// </summary>
public sealed class SketchfabSource(ReferenceHttp http, IOptions<ModelSearchOptions> options)
{
    public string Id => "sketchfab";

    public string Name => "Sketchfab";

    public bool Enabled => options.Value.SketchfabEnabled;

    public bool IsTrustedImageHost(Uri uri) => uri.Scheme == "https" && uri.Host == "media.sketchfab.com";

    public async Task<IReadOnlyList<ModelHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var api = options.Value.SketchfabApiUrl;
        var parameters = new Dictionary<string, string>
        {
            ["type"] = "models",
            ["q"] = query,
            ["count"] = Math.Min(limit, 24).ToString(CultureInfo.InvariantCulture),
        };
        using var doc = await http.GetJsonAsync(SourceUri.Build(api, parameters), u => u.Host == api.Host, cancellationToken);

        var results = SourceText.Get(doc.RootElement, "results");
        if (results.ValueKind != JsonValueKind.Array)
            throw new ReferenceFetchException("bad_response", "Sketchfab returned an unexpected response.");

        var hits = new List<ModelHit>();
        foreach (var r in results.EnumerateArray())
        {
            if (SourceText.Get(r, "isAgeRestricted").ValueKind == JsonValueKind.True)
                continue;
            if (SourceText.Clean(SourceText.Get(r, "uid"), 64) is not { } uid)
                continue;

            var user = SourceText.Get(r, "user");
            var glb = SourceText.Get(SourceText.Get(r, "archives"), "glb");
            var size = SourceText.Get(glb, "size");
            hits.Add(new ModelHit(
                uid,
                SourceText.Clean(SourceText.Get(r, "name"), 300) ?? "Untitled",
                SourceText.Url(SourceText.Get(r, "viewerUrl").GetStringOrNull()),
                SourceText.Clean(SourceText.Get(user, "displayName"), 200) ?? SourceText.Clean(SourceText.Get(user, "username"), 200),
                SourceText.Clean(SourceText.Get(SourceText.Get(r, "license"), "label"), 100),
                SourceText.Get(r, "isDownloadable").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null },
                SourceText.Int(SourceText.Get(r, "faceCount")),
                SourceText.Int(SourceText.Get(r, "vertexCount")),
                size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : null,
                PickThumbnail(SourceText.Get(SourceText.Get(r, "thumbnails"), "images"))));
        }

        return hits;
    }

    /// <summary>The smallest thumbnail at least 256 px wide (they come in several sizes), else the largest.</summary>
    private static string? PickThumbnail(JsonElement images)
    {
        if (images.ValueKind != JsonValueKind.Array)
            return null;
        var all = images.EnumerateArray()
            .Select(i => (Width: SourceText.Int(SourceText.Get(i, "width")) ?? 0, Url: SourceText.Url(SourceText.Get(i, "url").GetStringOrNull())))
            .Where(i => i.Url is not null)
            .ToList();
        return (all.Where(i => i.Width >= 256).OrderBy(i => i.Width).FirstOrDefault().Url) ?? all.OrderByDescending(i => i.Width).FirstOrDefault().Url;
    }
}
