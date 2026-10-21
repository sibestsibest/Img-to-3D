using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Api.Services.References;

public sealed record ManualSearchLink(string Name, string Url, string Kind);

/// <summary>
/// Browser search links for sources that offer no permitted automated access (general image
/// search engines, shops, fan wikis). They are only links: nothing is fetched from them.
/// </summary>
public static class ManualSearchLinks
{
    private static readonly (string Name, string Template, string Kind)[] Templates =
    [
        ("Google Images", "https://www.google.com/search?tbm=isch&q={0}", "images"),
        ("Bing Images", "https://www.bing.com/images/search?q={0}", "images"),
        ("DuckDuckGo Images", "https://duckduckgo.com/?ia=images&iax=images&q={0}", "images"),
        ("Wikimedia Commons", "https://commons.wikimedia.org/w/index.php?title=Special:MediaSearch&type=image&search={0}", "images"),
        ("Openverse", "https://openverse.org/search/image?q={0}", "images"),
        ("Gundam Wiki", "https://gundam.fandom.com/wiki/Special:Search?query={0}", "reference"),
        ("HobbyLink Japan", "https://www.hlj.com/search/?q={0}", "shop"),
        ("Sketchfab (3D models)", "https://sketchfab.com/search?type=models&q={0}", "models"),
        ("Printables (3D models)", "https://www.printables.com/search/models?q={0}", "models"),
    ];

    public static IReadOnlyList<ManualSearchLink> For(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var q = Uri.EscapeDataString(query.Trim());
        return Templates.Select(t => new ManualSearchLink(t.Name, string.Format(System.Globalization.CultureInfo.InvariantCulture, t.Template, q), t.Kind)).ToList();
    }
}

public static partial class ReferenceQuery
{
    public const int MaxLength = 200;

    /// <summary>Trimmed, single-spaced, control characters removed. Empty when nothing searchable remains.</summary>
    public static string Clean(string? query)
    {
        var text = Spaces().Replace(new string((query ?? "").Where(c => !char.IsControl(c)).ToArray()), " ").Trim();
        return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();
    }

    /// <summary>Cache key: the same words in any case or spacing hit the same entry.</summary>
    public static string CacheKey(string source, string query, int limit) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}\n{Clean(query).ToLowerInvariant()}\n{limit}")));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Groups candidates showing the same picture. Candidates are taken in discovery order; each
/// one is compared with the earlier ones that are not themselves duplicates. Equal SHA-256 is an
/// exact duplicate (the same file); a dHash within the threshold is a near duplicate (the same
/// picture re-encoded, resized or lightly edited). Different photos of the same object are NOT
/// duplicates and are not detected here: that needs similarity ranking (phase 5).
/// </summary>
public static class ReferenceDeduplicator
{
    public sealed record Item(Guid Id, string? Sha256, ulong? DHash);

    public sealed record Result(Guid? DuplicateOf, string? Kind, bool MatchesInput);

    public static IReadOnlyDictionary<Guid, Result> Assign(IReadOnlyList<Item> ordered, ulong? inputHash, int threshold)
    {
        var results = new Dictionary<Guid, Result>();
        var canonical = new List<Item>();
        foreach (var item in ordered)
        {
            Guid? of = null;
            string? kind = null;
            if (item.Sha256 is { } sha && canonical.FirstOrDefault(c => c.Sha256 == sha) is { } same)
                (of, kind) = (same.Id, "exact");
            else if (item.DHash is { } hash && canonical.FirstOrDefault(c => c.DHash is { } h && ImageHash.Distance(h, hash) <= threshold) is { } near)
                (of, kind) = (near.Id, "near");

            if (of is null)
                canonical.Add(item);
            var matchesInput = item.DHash is { } d && inputHash is { } input && ImageHash.Distance(d, input) <= threshold;
            results[item.Id] = new Result(of, kind, matchesInput);
        }

        return results;
    }
}
