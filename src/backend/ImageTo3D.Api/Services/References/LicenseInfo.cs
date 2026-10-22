using System.Text.RegularExpressions;

namespace ImageTo3D.Api.Services.References;

/// <summary>
/// A licence reduced to what matters for using a reference picture. Permissions are null when
/// the licence is unknown: nothing may be assumed then.
/// </summary>
public sealed partial record LicenseInfo(string Code, string Label, bool? CommercialUse, bool? Derivatives, bool? AttributionRequired)
{
    public const string Unknown = "unknown";

    public bool IsKnown => Code != Unknown;

    /// <summary>Codes a user may pick for a manually added picture.</summary>
    public static readonly IReadOnlyList<string> Codes =
        ["unknown", "cc0", "public_domain", "cc-by", "cc-by-sa", "cc-by-nd", "cc-by-nc", "cc-by-nc-sa", "cc-by-nc-nd", "gfdl", "own_work"];

    /// <summary>
    /// Maps what a source states (short name such as "CC BY-SA 4.0", Openverse's "by-sa", or a
    /// licence URL) to a code. Anything not recognised is <c>unknown</c>; the raw text is kept
    /// separately so nothing is lost.
    /// </summary>
    public static LicenseInfo Normalize(string? raw, string? url = null)
    {
        var text = (raw ?? "").Trim().ToLowerInvariant();
        var link = (url ?? "").Trim().ToLowerInvariant();

        // Long names (Sketchfab: "CC Attribution-NonCommercial-ShareAlike") become short terms.
        text = text.Replace("attribution", "by").Replace("non-commercial", "nc").Replace("noncommercial", "nc")
            .Replace("share-alike", "sa").Replace("sharealike", "sa").Replace("noderivatives", "nd").Replace("noderivs", "nd");

        if (text is "own_work" or "own work")
            return new LicenseInfo("own_work", "Own photo", true, true, false);
        if (text.StartsWith("cc0", StringComparison.Ordinal) || text == "cc-zero" || link.Contains("publicdomain/zero"))
            return new LicenseInfo("cc0", "CC0 (public domain dedication)", true, true, false);
        if (text is "pdm" or "pd" or "public_domain" || text.StartsWith("public domain", StringComparison.Ordinal) || text.StartsWith("pd-", StringComparison.Ordinal) || link.Contains("publicdomain/mark"))
            return new LicenseInfo("public_domain", "Public domain", true, true, false);
        if (text.StartsWith("gfdl", StringComparison.Ordinal) || link.Contains("gnu.org/licenses/fdl") || link.Contains("gnu.org/copyleft/fdl"))
            return new LicenseInfo("gfdl", "GNU FDL", true, true, true);

        var fromUrl = CcUrl().Match(link) is { Success: true } m ? m.Groups[1].Value.Replace('/', ' ') : "";
        if ((CreativeCommons(text) ?? CreativeCommons(fromUrl)) is { } cc)
            return cc;

        if (text is "" or "unknown" && link == "")
            return new LicenseInfo(Unknown, "Licence unknown", null, null, null);
        return new LicenseInfo(Unknown, "Licence not recognised", null, null, null);
    }

    private static LicenseInfo? CreativeCommons(string text)
    {
        var m = CcText().Match(text);
        if (!m.Success)
            return null;

        var parts = m.Groups["terms"].Value.Split(['-', ' ', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "by" || parts.Skip(1).Any(p => p is not ("nc" or "sa" or "nd")))
            return null;
        var nc = parts.Contains("nc");
        var sa = parts.Contains("sa");
        var nd = parts.Contains("nd");
        if (sa && nd)
            return null;

        var code = "cc-by" + (nc ? "-nc" : "") + (sa ? "-sa" : "") + (nd ? "-nd" : "");
        var version = m.Groups["version"].Success ? " " + m.Groups["version"].Value : "";
        var label = "CC " + string.Join('-', code[3..].Split('-').Select(p => p.ToUpperInvariant())) + version;
        return new LicenseInfo(code, label, !nc, !nd, true);
    }

    // "cc by-sa 4.0", "cc-by-nc-2.0", "by-sa" (Openverse), "by 3.0"
    [GeneratedRegex(@"^(?:cc[\s\-_]*)?(?<terms>by(?:[\s\-_]+(?:nc|sa|nd))*)(?:[\s\-_]+(?<version>\d(?:\.\d)?))?(?:[\s\-_]+[a-z]{2})?$")]
    private static partial Regex CcText();

    // https://creativecommons.org/licenses/by-sa/4.0/
    [GeneratedRegex(@"creativecommons\.org/licenses/([a-z\-]+(?:/\d(?:\.\d)?)?)")]
    private static partial Regex CcUrl();
}
