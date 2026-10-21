using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.References;

/// <summary>One search result as the source described it (licence still unnormalised).</summary>
public sealed record ReferenceHit(
    string ItemId,
    string Title,
    string? PageUrl,
    string? ImageUrl,
    string? ThumbnailUrl,
    string? Author,
    string? License,
    string? LicenseUrl,
    int? Width,
    int? Height);

/// <summary>
/// An independent, free reference-image source. Adapters only call documented public search
/// APIs anonymously: no keys, cookies or scraping. Each one fails on its own without affecting
/// the others.
/// </summary>
public interface IReferenceSource
{
    /// <summary>Stable ID stored on candidates (wikimedia, openverse, internet_archive).</summary>
    string Id { get; }

    string Name { get; }

    string Description { get; }

    bool Enabled { get; }

    /// <summary>True for the hosts this source serves thumbnails from (always HTTPS).</summary>
    bool IsTrustedImageHost(Uri uri);

    Task<IReadOnlyList<ReferenceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

internal static partial class SourceText
{
    /// <summary>Plain text from source fields that may carry HTML (Wikimedia's Artist) or arrays (Internet Archive).</summary>
    public static string? Clean(JsonElement value, int maxLength)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return Clean(text, maxLength);
    }

    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(text, " ")), " ").Trim();
        return text.Length == 0 ? null : text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>Absolute http(s) URL or null: nothing else is stored, so a link can never run script.</summary>
    public static string? Url(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && value!.Length <= 2000 ? uri.AbsoluteUri : null;

    public static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i > 0 ? i : null;

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Wikimedia Commons through the MediaWiki API (file namespace, bitmaps only). Licence from extmetadata.</summary>
public sealed class WikimediaCommonsSource(ReferenceHttp http, IOptions<ReferenceSearchOptions> options) : IReferenceSource
{
    public string Id => "wikimedia";

    public string Name => "Wikimedia Commons";

    public string Description => "Freely licensed photos and drawings. Free, no account; licence and author come with every file.";

    public bool Enabled => options.Value.WikimediaEnabled;

    // The API returns thumbnails on thumb.wikimedia.org (checked 2026-10-21) and originals on upload.wikimedia.org.
    public bool IsTrustedImageHost(Uri uri) => uri.Scheme == "https" && uri.Host is "thumb.wikimedia.org" or "upload.wikimedia.org";

    public async Task<IReadOnlyList<ReferenceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var api = options.Value.WikimediaApiUrl;
        var parameters = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["formatversion"] = "2",
            ["generator"] = "search",
            ["gsrsearch"] = query + " filetype:bitmap",
            ["gsrnamespace"] = "6",
            ["gsrlimit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["prop"] = "imageinfo",
            ["iiprop"] = "url|size|extmetadata",
            ["iiurlwidth"] = options.Value.ThumbnailMaxSide.ToString(CultureInfo.InvariantCulture),
            ["iiextmetadatafilter"] = "LicenseShortName|LicenseUrl|Artist|ObjectName",
        };
        using var doc = await http.GetJsonAsync(SourceUri.Build(api, parameters), u => u.Host == api.Host, cancellationToken);

        var pages = SourceText.Get(SourceText.Get(doc.RootElement, "query"), "pages");
        if (pages.ValueKind != JsonValueKind.Array)
        {
            // No "query" at all is how MediaWiki reports zero results.
            if (SourceText.Get(doc.RootElement, "error").ValueKind == JsonValueKind.Object)
                throw new ReferenceFetchException("bad_response", "Wikimedia reported an error for this query.");
            return [];
        }

        var hits = new List<(int Index, ReferenceHit Hit)>();
        foreach (var page in pages.EnumerateArray())
        {
            var info = SourceText.Get(page, "imageinfo");
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0)
                continue;
            var ii = info[0];
            var meta = SourceText.Get(ii, "extmetadata");
            string? Meta(string key, int max) => SourceText.Clean(SourceText.Get(SourceText.Get(meta, key), "value"), max);

            var pageId = SourceText.Get(page, "pageid");
            if (pageId.ValueKind != JsonValueKind.Number)
                continue;
            var title = SourceText.Clean(SourceText.Get(page, "title"), 300) ?? "Untitled";
            if (title.StartsWith("File:", StringComparison.Ordinal))
                title = title[5..];
            title = Path.GetFileNameWithoutExtension(title) is { Length: > 0 } t ? t.Replace('_', ' ') : title;

            hits.Add((SourceText.Int(SourceText.Get(page, "index")) ?? int.MaxValue, new ReferenceHit(
                pageId.GetRawText(),
                Meta("ObjectName", 300) ?? title,
                SourceText.Url(SourceText.Get(ii, "descriptionurl").GetStringOrNull()),
                SourceText.Url(SourceText.Get(ii, "url").GetStringOrNull()),
                SourceText.Url(SourceText.Get(ii, "thumburl").GetStringOrNull()),
                Meta("Artist", 200),
                Meta("LicenseShortName", 100),
                SourceText.Url(Meta("LicenseUrl", 2000)),
                SourceText.Int(SourceText.Get(ii, "width")),
                SourceText.Int(SourceText.Get(ii, "height")))));
        }

        return hits.OrderBy(h => h.Index).Select(h => h.Hit).ToList();
    }
}

/// <summary>Openverse (WordPress Foundation): openly licensed images aggregated from many providers. Anonymous access is rate limited.</summary>
public sealed class OpenverseSource(ReferenceHttp http, IOptions<ReferenceSearchOptions> options) : IReferenceSource
{
    public string Id => "openverse";

    public string Name => "Openverse";

    public string Description => "Openly licensed images from Flickr, museums and other collections. Free, no account (anonymous requests are rate limited).";

    public bool Enabled => options.Value.OpenverseEnabled;

    // Openverse serves thumbnails itself (/v1/images/{id}/thumb/), so the API host is the only image host.
    public bool IsTrustedImageHost(Uri uri) => uri.Scheme == "https" && uri.Host == options.Value.OpenverseApiUrl.Host;

    public async Task<IReadOnlyList<ReferenceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var api = options.Value.OpenverseApiUrl;
        var parameters = new Dictionary<string, string>
        {
            ["q"] = query,
            ["page_size"] = Math.Min(limit, 20).ToString(CultureInfo.InvariantCulture),
            ["mature"] = "false",
        };
        using var doc = await http.GetJsonAsync(SourceUri.Build(api, parameters), u => u.Host == api.Host, cancellationToken);

        var results = SourceText.Get(doc.RootElement, "results");
        if (results.ValueKind != JsonValueKind.Array)
            throw new ReferenceFetchException("bad_response", "Openverse returned an unexpected response.");

        var hits = new List<ReferenceHit>();
        foreach (var r in results.EnumerateArray())
        {
            if (SourceText.Clean(SourceText.Get(r, "id"), 100) is not { } id)
                continue;
            var license = SourceText.Clean(SourceText.Get(r, "license"), 50);
            var version = SourceText.Clean(SourceText.Get(r, "license_version"), 10);
            hits.Add(new ReferenceHit(
                id,
                SourceText.Clean(SourceText.Get(r, "title"), 300) ?? "Untitled",
                SourceText.Url(SourceText.Get(r, "foreign_landing_url").GetStringOrNull()),
                SourceText.Url(SourceText.Get(r, "url").GetStringOrNull()),
                SourceText.Url(SourceText.Get(r, "thumbnail").GetStringOrNull()),
                SourceText.Clean(SourceText.Get(r, "creator"), 200),
                license is null ? null : version is null ? license : $"{license} {version}",
                SourceText.Url(SourceText.Get(r, "license_url").GetStringOrNull()),
                SourceText.Int(SourceText.Get(r, "width")),
                SourceText.Int(SourceText.Get(r, "height"))));
        }

        return hits;
    }
}

/// <summary>Internet Archive advanced search over image items. Licence metadata is often missing (then "unknown").</summary>
public sealed partial class InternetArchiveSource(ReferenceHttp http, IOptions<ReferenceSearchOptions> options) : IReferenceSource
{
    public string Id => "internet_archive";

    public string Name => "Internet Archive";

    public string Description => "Scanned catalogues, manuals and photo collections. Free, no account; many items state no licence.";

    public bool Enabled => options.Value.InternetArchiveEnabled;

    // /services/img/{id} redirects to the item's storage node (ia800000.us.archive.org).
    public bool IsTrustedImageHost(Uri uri) => uri.Scheme == "https" && (uri.Host == "archive.org" || uri.Host.EndsWith(".archive.org", StringComparison.Ordinal));

    public async Task<IReadOnlyList<ReferenceHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var api = options.Value.InternetArchiveApiUrl;
        var terms = QueryTerms(query);
        if (terms.Length == 0)
            return [];
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("q", $"({terms}) AND mediatype:(image)"),
            new("fl[]", "identifier"),
            new("fl[]", "title"),
            new("fl[]", "creator"),
            new("fl[]", "licenseurl"),
            new("rows", limit.ToString(CultureInfo.InvariantCulture)),
            new("page", "1"),
            new("output", "json"),
        };
        using var doc = await http.GetJsonAsync(SourceUri.Build(api, parameters), u => u.Host == api.Host, cancellationToken);

        var docs = SourceText.Get(SourceText.Get(doc.RootElement, "response"), "docs");
        if (docs.ValueKind != JsonValueKind.Array)
            throw new ReferenceFetchException("bad_response", "Internet Archive returned an unexpected response.");

        var hits = new List<ReferenceHit>();
        foreach (var d in docs.EnumerateArray())
        {
            if (SourceText.Clean(SourceText.Get(d, "identifier"), 100) is not { } id || id.Contains(' '))
                continue;
            var escaped = Uri.EscapeDataString(id);
            var licenseUrl = SourceText.Url(SourceText.Clean(SourceText.Get(d, "licenseurl"), 2000));
            hits.Add(new ReferenceHit(
                id,
                SourceText.Clean(SourceText.Get(d, "title"), 300) ?? id,
                $"https://archive.org/details/{escaped}",
                null,
                $"https://archive.org/services/img/{escaped}",
                SourceText.Clean(SourceText.Get(d, "creator"), 200),
                null,
                licenseUrl,
                null,
                null));
        }

        return hits;
    }

    /// <summary>
    /// The user's words as plain search terms: a word containing query syntax (RX-78-2, "AND", a
    /// colon) is quoted, so it can only match, never change the filter. Backslash escapes are not
    /// used: the Archive's backend fails on them ("Invalid or no response", checked 2026-10-21).
    /// </summary>
    public static string QueryTerms(string query) =>
        string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", "").Replace("\\", ""))
            .Where(w => w.Length > 0)
            .Select(w => LuceneSyntax().IsMatch(w) || w is "AND" or "OR" or "NOT" or "TO" ? $"\"{w}\"" : w));

    [GeneratedRegex(@"[+\-!(){}\[\]^~*?:/&|]")]
    private static partial Regex LuceneSyntax();
}

internal static class SourceUri
{
    public static Uri Build(Uri baseUri, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var query = string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new UriBuilder(baseUri) { Query = query }.Uri;
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
