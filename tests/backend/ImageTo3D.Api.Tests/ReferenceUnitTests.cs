using System.Net;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Services.References;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Tests;

public class LicenseInfoTests
{
    [Theory]
    [InlineData("CC BY-SA 4.0", null, "cc-by-sa", "CC BY-SA 4.0", true, true)]
    [InlineData("CC BY 2.0", null, "cc-by", "CC BY 2.0", true, true)]
    [InlineData("cc-by-nc-nd-3.0", null, "cc-by-nc-nd", "CC BY-NC-ND 3.0", false, false)]
    [InlineData("by-nc-sa 2.0", null, "cc-by-nc-sa", "CC BY-NC-SA 2.0", false, true)] // Openverse wording
    [InlineData("CC BY-SA 3.0 de", null, "cc-by-sa", "CC BY-SA 3.0", true, true)] // jurisdiction port
    [InlineData(null, "https://creativecommons.org/licenses/by-nd/4.0/", "cc-by-nd", "CC BY-ND 4.0", true, false)]
    [InlineData("cc0", null, "cc0", "CC0 (public domain dedication)", true, true)]
    [InlineData(null, "https://creativecommons.org/publicdomain/zero/1.0/", "cc0", "CC0 (public domain dedication)", true, true)]
    [InlineData("Public domain", null, "public_domain", "Public domain", true, true)]
    [InlineData("pdm", null, "public_domain", "Public domain", true, true)]
    [InlineData("GFDL", null, "gfdl", "GNU FDL", true, true)]
    public void Normalize_RecognisesCommonWordings(string? raw, string? url, string code, string label, bool commercial, bool derivatives)
    {
        var license = LicenseInfo.Normalize(raw, url);
        Assert.Equal(code, license.Code);
        Assert.Equal(label, license.Label);
        Assert.Equal(commercial, license.CommercialUse);
        Assert.Equal(derivatives, license.Derivatives);
        Assert.True(license.IsKnown);
    }

    [Theory]
    [InlineData(null, null, "Licence unknown")]
    [InlineData("", "", "Licence unknown")]
    [InlineData("All rights reserved", null, "Licence not recognised")]
    [InlineData("by-sa-nd", null, "Licence not recognised")] // contradictory terms
    [InlineData(null, "https://example.com/terms", "Licence not recognised")]
    public void Normalize_MissingOrInvalidLicencesAreUnknownWithNoPermissions(string? raw, string? url, string label)
    {
        var license = LicenseInfo.Normalize(raw, url);
        Assert.Equal("unknown", license.Code);
        Assert.Equal(label, license.Label);
        Assert.False(license.IsKnown);
        Assert.Null(license.CommercialUse);
        Assert.Null(license.Derivatives);
        Assert.Null(license.AttributionRequired);
    }

    [Fact]
    public void Normalize_AcceptsEveryCodeOfferedForManualPictures()
    {
        foreach (var code in LicenseInfo.Codes)
            Assert.Equal(code, LicenseInfo.Normalize(code).Code);
    }
}

public class ReferenceSearchSupportTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    [Fact]
    public void Deduplicator_GroupsExactAndNearCopiesUnderTheFirstOriginal()
    {
        var results = ReferenceDeduplicator.Assign(
        [
            new(A, "aa", 0b1111_0000),
            new(B, "bb", 0xFFFF_0000_0000_0000),
            new(C, "aa", 0xFFFF_FFFF_FFFF_FFFF), // same file as A, whatever its hash
            new(D, "dd", 0b1111_0011),           // 2 bits from A
        ], inputHash: 0xFFFF_0000_0000_0001, threshold: 2);

        Assert.Equal(new ReferenceDeduplicator.Result(null, null, false), results[A]);
        Assert.Equal(new ReferenceDeduplicator.Result(null, null, true), results[B]); // 1 bit from the input photo
        Assert.Equal(new ReferenceDeduplicator.Result(A, "exact", false), results[C]);
        Assert.Equal(new ReferenceDeduplicator.Result(A, "near", false), results[D]);
    }

    [Fact]
    public void Deduplicator_NeverPointsAtADuplicateAndIgnoresMissingFingerprints()
    {
        var results = ReferenceDeduplicator.Assign(
        [
            new(A, null, null),
            new(B, "bb", 0b0000),
            new(C, "cc", 0b0011), // near B
            new(D, "dd", 0b0111), // 3 bits from B, 1 from C: C is a duplicate, so D is compared with B only
        ], inputHash: null, threshold: 2);

        Assert.Null(results[A].DuplicateOf);
        Assert.Equal(B, results[C].DuplicateOf);
        Assert.Null(results[D].DuplicateOf);
        Assert.All(results.Values, r => Assert.False(r.MatchesInput));
    }

    [Fact]
    public void Query_IsCleanedAndCacheKeysIgnoreCaseAndSpacing()
    {
        Assert.Equal("RX-78-2 Gundam", ReferenceQuery.Clean("  RX-78-2 \t\n Gundam\u0007 "));
        Assert.Equal(200, ReferenceQuery.Clean(new string('x', 500)).Length);
        Assert.Equal("", ReferenceQuery.Clean(null));
        Assert.Equal(ReferenceQuery.CacheKey("wikimedia", "rx-78-2  gundam", 20), ReferenceQuery.CacheKey("wikimedia", "RX-78-2 Gundam", 20));
        Assert.NotEqual(ReferenceQuery.CacheKey("wikimedia", "zaku", 20), ReferenceQuery.CacheKey("openverse", "zaku", 20));
        Assert.NotEqual(ReferenceQuery.CacheKey("wikimedia", "zaku", 20), ReferenceQuery.CacheKey("wikimedia", "zaku", 10));
    }

    [Theory]
    [InlineData("rx-78-2 gundam", "\"rx-78-2\" gundam")]
    [InlineData("zaku AND OR", "zaku \"AND\" \"OR\"")]
    [InlineData("title:\"x\" back\\slash", "\"title:x\" backslash")]
    [InlineData("\"\" \\", "")]
    public void ArchiveQuery_QuotesSyntaxInsteadOfEscaping(string query, string expected) =>
        Assert.Equal(expected, InternetArchiveSource.QueryTerms(query));

    [Fact]
    public void ManualLinks_EncodeTheQueryAndAreOnlyHttps()
    {
        var links = ManualSearchLinks.For("MG Zaku II & ver 2.0");
        Assert.Contains(links, l => l.Kind == "images");
        Assert.Contains(links, l => l.Kind == "models");
        Assert.All(links, l => Assert.StartsWith("https://", l.Url));
        Assert.All(links, l => Assert.Contains("MG%20Zaku%20II%20%26%20ver%202.0", l.Url));
        Assert.Empty(ManualSearchLinks.For("   "));
    }
}

public class ReferenceHttpTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ReferenceHttp Create(FakeReferenceWeb web, int retries = 1) =>
        new(new Factory(web), Options.Create(new ReferenceSearchOptions { MaxRetries = retries, RetryDelay = TimeSpan.FromMilliseconds(1) }), NullLogger<ReferenceHttp>.Instance);

    private static bool Anywhere(Uri _) => true;

    [Fact]
    public async Task GetJson_CancellationIsPropagatedNotReportedAsAFailure()
    {
        var web = new FakeReferenceWeb();
        web.OnHang("api.test/search");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(web).GetJsonAsync(new Uri("https://api.test/search"), Anywhere, cts.Token));
        Assert.Equal(1, web.Count("api.test/search"));
    }

    [Fact]
    public async Task GetJson_RetriesAreBounded()
    {
        var web = new FakeReferenceWeb();
        web.OnStatus("api.test/search", HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<ReferenceFetchException>(() => Create(web, retries: 3).GetJsonAsync(new Uri("https://api.test/search"), Anywhere, default));
        Assert.Equal("unavailable", error.Code);
        Assert.Equal(4, web.Count("api.test/search"));
    }

    [Fact]
    public async Task GetJson_ClientErrorsAreNotRetried()
    {
        var web = new FakeReferenceWeb(); // unknown route: 404

        var error = await Assert.ThrowsAsync<ReferenceFetchException>(() => Create(web).GetJsonAsync(new Uri("https://api.test/missing"), Anywhere, default));
        Assert.Equal("bad_response", error.Code);
        Assert.Equal(1, web.Count("api.test/missing"));
    }

    [Fact]
    public async Task Download_RedirectLoopsAreCutOff()
    {
        var web = new FakeReferenceWeb();
        web.OnRedirect("img.test/a", "https://img.test/b");
        web.OnRedirect("img.test/b", "https://img.test/a");

        var error = await Assert.ThrowsAsync<ReferenceFetchException>(() => Create(web).DownloadImageAsync(new Uri("https://img.test/a"), Anywhere, 1000, default));
        Assert.Equal("bad_response", error.Code);
        Assert.Equal(4, web.Requests.Count); // first request plus at most 3 redirects
    }

    [Fact]
    public async Task Download_RequiresAnImageContentType()
    {
        var web = new FakeReferenceWeb();
        web.OnJson("img.test/a", "{}");

        var error = await Assert.ThrowsAsync<ReferenceFetchException>(() => Create(web).DownloadImageAsync(new Uri("https://img.test/a"), Anywhere, 1000, default));
        Assert.Equal("not_an_image", error.Code);
    }
}
