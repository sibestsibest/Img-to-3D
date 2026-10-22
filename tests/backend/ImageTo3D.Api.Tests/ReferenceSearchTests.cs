using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Tests.Infrastructure;
using SkiaSharp;

namespace ImageTo3D.Api.Tests;

public class ReferenceSearchTests
{
    internal const string WikimediaApi = "commons.wikimedia.org/w/api.php";
    internal const string OpenverseApi = "api.openverse.org/v1/images/";
    internal const string ArchiveApi = "archive.org/advancedsearch.php";

    /// <summary>A distinct, deterministic picture per seed (grey blocks at seeded positions).</summary>
    public static byte[] Picture(int seed, int w = 240, int h = 180, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            for (var i = 0; i < 12; i++)
            {
                var grey = (byte)random.Next(0, 200);
                using var paint = new SKPaint { Color = new SKColor(grey, grey, (byte)(grey + 40)) };
                canvas.DrawRect(random.Next(0, w - 20), random.Next(0, h - 20), random.Next(20, w / 2), random.Next(20, h / 2), paint);
            }
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    internal static string WikimediaJson(string thumbA = "https://thumb.wikimedia.org/thumb/a.png", string thumbB = "https://upload.wikimedia.org/thumb/b.png") => """
        {"batchcomplete":true,"query":{"pages":[
          {"pageid":101,"ns":6,"title":"File:RX-78-2_front_view.jpg","index":2,"imageinfo":[{
            "url":"https://upload.wikimedia.org/full/b.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:RX-78-2_front_view.jpg",
            "thumburl":"THUMB_B","width":3000,"height":2000,
            "extmetadata":{"LicenseShortName":{"value":"CC BY-SA 4.0"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by-sa/4.0"},
                           "Artist":{"value":"<a href=\"//commons.wikimedia.org/wiki/User:Jane\">Jane &amp; Co</a>"}}}]},
          {"pageid":100,"ns":6,"title":"File:Gunpla box art.png","index":1,"imageinfo":[{
            "url":"https://upload.wikimedia.org/full/a.png","descriptionurl":"https://commons.wikimedia.org/wiki/File:Gunpla_box_art.png",
            "thumburl":"THUMB_A","width":800,"height":600,
            "extmetadata":{"LicenseShortName":{"value":"Public domain"}}}]}
        ]}}
        """.Replace("THUMB_A", thumbA).Replace("THUMB_B", thumbB);

    private const string OpenverseJson = """
        {"result_count":1,"page_count":1,"results":[{"id":"ov-1","title":"Gundam statue","foreign_landing_url":"https://www.flickr.com/photos/x/1",
          "url":"https://live.staticflickr.com/1.jpg","creator":"Bob","license":"by-nc","license_version":"2.0",
          "license_url":"https://creativecommons.org/licenses/by-nc/2.0/","thumbnail":"https://api.openverse.org/v1/images/ov-1/thumb/","width":1024,"height":768}]}
        """;

    private const string ArchiveJson = """
        {"responseHeader":{"status":0},"response":{"numFound":1,"start":0,"docs":[{"identifier":"gundam-catalog-1985","title":["Gundam catalog 1985"],"creator":"Bandai"}]}}
        """;

    /// <summary>All three sources answer with one distinct picture per result.</summary>
    private static void ServeAll(FakeReferenceWeb web)
    {
        web.OnJson(WikimediaApi, WikimediaJson());
        web.OnImage("thumb.wikimedia.org/thumb/a.png", Picture(1));
        web.OnImage("upload.wikimedia.org/thumb/b.png", Picture(2));
        web.OnJson(OpenverseApi, OpenverseJson);
        web.OnImage("api.openverse.org/v1/images/ov-1/thumb/", Picture(3, format: SKEncodedImageFormat.Jpeg), "image/jpeg");
        web.OnJson(ArchiveApi, ArchiveJson);
        web.OnRedirect("archive.org/services/img/gundam-catalog-1985", "https://ia800100.us.archive.org/12/items/gundam-catalog-1985/__ia_thumb.jpg");
        web.OnImage("ia800100.us.archive.org/12/items/gundam-catalog-1985/__ia_thumb.jpg", Picture(4, format: SKEncodedImageFormat.Jpeg), "image/jpeg");
    }

    internal static async Task<ReferenceSessionDto> CreateSessionAsync(HttpClient client, string? query = "rx-78-2 gundam", byte[]? image = null, Guid? sourceJobId = null)
    {
        using var form = new MultipartFormDataContent();
        if (query is not null)
            form.Add(new StringContent(query), "query");
        if (sourceJobId is { } jobId)
            form.Add(new StringContent(jobId.ToString()), "sourceJobId");
        if (image is not null)
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "image", "input.png");
        }

        if (!form.Any())
            form.Add(new StringContent("Untitled test"), "name"); // a multipart body needs at least one part

        var response = await client.PostAsync("/api/references", form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReferenceSessionDto>(ApiFactory.Json))!;
    }

    internal static async Task<ReferenceSessionDto> SearchAsync(HttpClient client, Guid id, object? body = null)
    {
        var response = await client.PostAsJsonAsync($"/api/references/{id}/search", body ?? new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReferenceSessionDto>(ApiFactory.Json))!;
    }

    internal static async Task<ReferenceSessionDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReferenceSessionDto>(ApiFactory.Json))!;
    }

    private static SourceSearchResultDto Outcome(ReferenceSessionDto session, string source) => session.LastSearch.Single(s => s.Source == source);

    [Fact]
    public async Task Sources_AreAllFreeWithoutAccount()
    {
        using var factory = new ApiFactory();
        var sources = (await factory.CreateClient().GetFromJsonAsync<List<ReferenceSourceDto>>("/api/references/sources", ApiFactory.Json))!;

        Assert.Equal(["wikimedia", "openverse", "internet_archive"], sources.Select(s => s.Id));
        Assert.All(sources, s => Assert.True(s.Enabled));
        Assert.All(sources, s => Assert.Equal("free_no_account", s.Access));
    }

    [Fact]
    public async Task Search_StoresResultsWithSourceAuthorLicenceAndPreviews()
    {
        using var factory = new ApiFactory();
        ServeAll(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id);

        Assert.All(session.LastSearch, s => Assert.Equal("ok", s.Status));
        Assert.Equal(4, session.Candidates.Count);
        // Wikimedia results keep the API's ranking ("index"), not the page order.
        Assert.Equal(["100", "101"], session.Candidates.Where(c => c.Source == "wikimedia").Select(c => c.Title == "Gunpla box art" ? "100" : "101"));

        var wiki = session.Candidates.Single(c => c.Title == "RX-78-2 front view");
        Assert.Equal("search", wiki.Origin);
        Assert.Equal("Wikimedia Commons", wiki.SourceName);
        Assert.Equal("https://commons.wikimedia.org/wiki/File:RX-78-2_front_view.jpg", wiki.PageUrl);
        Assert.Equal("https://upload.wikimedia.org/full/b.jpg", wiki.ImageUrl);
        Assert.Equal("Jane & Co", wiki.Author); // HTML removed, entities decoded
        Assert.Equal("cc-by-sa", wiki.License.Code);
        Assert.Equal("CC BY-SA 4.0", wiki.License.Label);
        Assert.True(wiki.License.CommercialUse);
        Assert.True(wiki.License.AttributionRequired);
        Assert.Equal("\"RX-78-2 front view\" by Jane & Co, CC BY-SA 4.0, via Wikimedia Commons", wiki.Attribution);
        Assert.Equal("rx-78-2 gundam", wiki.Query);
        Assert.Equal(3000, wiki.Width);
        Assert.Equal("downloaded", wiki.DownloadStatus);
        Assert.Equal("pending", wiki.Decision);
        Assert.Empty(wiki.Warnings);

        var openverse = session.Candidates.Single(c => c.Source == "openverse");
        Assert.Equal("cc-by-nc", openverse.License.Code);
        Assert.False(openverse.License.CommercialUse);
        Assert.Contains(openverse.Warnings, w => w.Code == "non_commercial");

        // Internet Archive states no licence: unknown, flagged for personal reference only. Its
        // preview redirects to a storage node, which is an archive.org host and so allowed.
        var archive = session.Candidates.Single(c => c.Source == "internet_archive");
        Assert.Equal("unknown", archive.License.Code);
        Assert.Null(archive.License.CommercialUse);
        Assert.Contains(archive.Warnings, w => w.Code == "licence_unknown" && w.Severity == "warning");
        Assert.Equal("https://archive.org/details/gundam-catalog-1985", archive.PageUrl);
        Assert.Null(archive.ImageUrl); // only a web page is known, not a direct image address
        Assert.Equal("downloaded", archive.DownloadStatus);

        var thumbnail = await client.GetAsync($"/api/references/{session.Id}/candidates/{wiki.Id}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType!.MediaType);
        using (var decoded = SKBitmap.Decode(await thumbnail.Content.ReadAsByteArrayAsync()))
            Assert.Equal(240, decoded.Width); // never enlarged beyond the source

        Assert.Contains(session.ManualLinks, l => l.Name == "Google Images" && l.Url.EndsWith("q=rx-78-2%20gundam", StringComparison.Ordinal));

        // The Archive gets the hyphenated word quoted, never backslash-escaped.
        var archiveCall = factory.Web.Requests.Single(r => r.RequestUri!.AbsolutePath == "/advancedsearch.php");
        Assert.Contains("q=(\"rx-78-2\" gundam) AND mediatype:(image)", Uri.UnescapeDataString(archiveCall.RequestUri!.Query));
        // Previews ask for decodable formats but accept anything (Openverse rejects a bare image/*).
        var preview = factory.Web.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/thumb/", StringComparison.Ordinal));
        Assert.Contains(preview.Headers.Accept, a => a.MediaType == "*/*");

        // Anonymous, key-free access: an identifying User-Agent, never credentials.
        Assert.All(factory.Web.Requests, r => Assert.Null(r.Headers.Authorization));
        Assert.All(factory.Web.Requests, r => Assert.Contains("ImageTo3D", r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task Search_RepeatedQueryUsesCacheAndAddsNoCopies_UntilCacheIsCleared()
    {
        using var factory = new ApiFactory();
        ServeAll(factory.Web);
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        await SearchAsync(client, id);
        var again = await SearchAsync(client, id, new { query = "  RX-78-2   Gundam " }); // same words, other case and spacing

        Assert.Equal(4, again.Candidates.Count);
        Assert.All(again.LastSearch, s => Assert.True(s.FromCache));
        Assert.All(again.LastSearch, s => Assert.Equal(0, s.Added));
        Assert.Equal(1, factory.Web.Count(WikimediaApi));
        Assert.Equal(1, factory.Web.Count("thumb.wikimedia.org/thumb/a.png"));

        var cleared = await (await client.DeleteAsync("/api/references/cache")).Content.ReadFromJsonAsync<ClearReferenceCacheDto>(ApiFactory.Json);
        Assert.Equal(3, cleared!.SearchEntries);
        Assert.Equal(4, cleared.Files);

        var afterClear = await SearchAsync(client, id);
        Assert.Equal(2, factory.Web.Count(WikimediaApi));
        Assert.All(afterClear.LastSearch, s => Assert.False(s.FromCache));
        Assert.Equal(4, afterClear.Candidates.Count);
        // Session previews are kept apart from the disposable cache.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/references/{id}/candidates/{afterClear.Candidates[0].Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Search_FailingSourcesAreIsolatedWithBoundedRetries()
    {
        using var factory = new ApiFactory(new() { ["ReferenceSearch:SourceTimeout"] = "00:00:00.300" });
        factory.Web.OnStatus(WikimediaApi, HttpStatusCode.ServiceUnavailable);
        factory.Web.OnStatus(OpenverseApi, HttpStatusCode.TooManyRequests);
        factory.Web.OnHang(ArchiveApi);
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        var session = await SearchAsync(client, id);

        Assert.Equal("failed", Outcome(session, "wikimedia").Status);
        Assert.Equal(2, factory.Web.Count(WikimediaApi)); // one retry after a 5xx (MaxRetries = 1)
        Assert.Equal("rate_limited", Outcome(session, "openverse").Status);
        Assert.Equal(1, factory.Web.Count(OpenverseApi)); // 429 is never retried
        Assert.Equal("timeout", Outcome(session, "internet_archive").Status);
        Assert.Empty(session.Candidates);
        Assert.NotEmpty(session.ManualLinks); // still a way forward with every source down

        // Failures are not cached: the next search asks again, and a recovered source works.
        factory.Web.OnJson(WikimediaApi, WikimediaJson());
        factory.Web.OnImage("thumb.wikimedia.org/thumb/a.png", Picture(1));
        factory.Web.OnImage("upload.wikimedia.org/thumb/b.png", Picture(2));
        var retried = await SearchAsync(client, id, new { sources = new[] { "wikimedia" } });
        Assert.Equal("ok", Outcome(retried, "wikimedia").Status);
        Assert.Single(retried.LastSearch);
        Assert.Equal(2, retried.Candidates.Count);
    }

    [Fact]
    public async Task Search_EmptyResultsDisabledSourcesAndBadAnswers()
    {
        using var factory = new ApiFactory(new() { ["ReferenceSearch:InternetArchiveEnabled"] = "false" });
        factory.Web.OnJson(WikimediaApi, """{"batchcomplete":true}""");
        factory.Web.OnJson(OpenverseApi, "<html>not json</html>");
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id);

        Assert.Equal("empty", Outcome(session, "wikimedia").Status);
        Assert.Equal("failed", Outcome(session, "openverse").Status);
        Assert.Equal("disabled", Outcome(session, "internet_archive").Status);
        Assert.Equal(0, factory.Web.Count(ArchiveApi));
        Assert.Empty(session.Candidates);
    }

    [Fact]
    public async Task Search_RejectsMissingQueryAndUnknownSources()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client, query: null)).Id;

        var noQuery = await client.PostAsJsonAsync($"/api/references/{id}/search", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noQuery.StatusCode);
        Assert.Equal("query_required", (await noQuery.ProblemAsync()).GetProperty("code").GetString());

        var unknown = await client.PostAsJsonAsync($"/api/references/{id}/search", new { query = "zaku", sources = new[] { "google" } });
        Assert.Equal("unknown_source", (await unknown.ProblemAsync()).GetProperty("code").GetString());
        Assert.Empty(factory.Web.Requests);
    }

    [Fact]
    public async Task Duplicates_ExactAndNearCopiesAndTheInputPhotoAreFlagged()
    {
        using var factory = new ApiFactory();
        var picture = Picture(7);
        factory.Web.OnJson(WikimediaApi, WikimediaJson());
        factory.Web.OnImage("thumb.wikimedia.org/thumb/a.png", picture);
        factory.Web.OnImage("upload.wikimedia.org/thumb/b.png", Picture(8));
        factory.Web.OnJson(OpenverseApi, OpenverseJson);
        factory.Web.OnImage("api.openverse.org/v1/images/ov-1/thumb/", picture); // same file again
        factory.Web.OnJson(ArchiveApi, ArchiveJson);
        factory.Web.OnRedirect("archive.org/services/img/gundam-catalog-1985", "https://ia800100.us.archive.org/t.jpg");
        using (var original = SKBitmap.Decode(picture))
        using (var smaller = original.Resize(new SKImageInfo(160, 120), new SKSamplingOptions(SKFilterMode.Linear)))
        using (var jpeg = smaller.Encode(SKEncodedImageFormat.Jpeg, 70))
            factory.Web.OnImage("ia800100.us.archive.org/t.jpg", jpeg.ToArray(), "image/jpeg"); // re-encoded, resized copy

        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client, image: picture)).Id);

        var first = session.Candidates.Single(c => c.Title == "Gunpla box art");
        var other = session.Candidates.Single(c => c.Title == "RX-78-2 front view");
        var exact = session.Candidates.Single(c => c.Source == "openverse");
        var near = session.Candidates.Single(c => c.Source == "internet_archive");

        Assert.Null(first.DuplicateOfId);
        Assert.Null(other.DuplicateOfId);
        Assert.Equal((first.Id, "exact"), (exact.DuplicateOfId!.Value, exact.DuplicateKind));
        Assert.Equal((first.Id, "near"), (near.DuplicateOfId!.Value, near.DuplicateKind));
        Assert.Contains(near.Warnings, w => w.Code == "duplicate");
        Assert.True(first.MatchesInput);
        Assert.False(other.MatchesInput);

        // Removing the original promotes the next copy; the rest now point at it.
        var after = await ReadAsync(await client.DeleteAsync($"/api/references/{session.Id}/candidates/{first.Id}"));
        Assert.Null(after.Candidates.Single(c => c.Id == exact.Id).DuplicateOfId);
        Assert.Equal(exact.Id, after.Candidates.Single(c => c.Id == near.Id).DuplicateOfId);
    }

    [Fact]
    public async Task Previews_OnlyComeFromTrustedHostsWithinSizeLimits()
    {
        using var factory = new ApiFactory(new() { ["ReferenceSearch:ThumbnailMaxBytes"] = "20000" });
        factory.Web.OnJson(WikimediaApi, WikimediaJson(thumbA: "https://evil.example.com/a.png", thumbB: "http://upload.wikimedia.org/thumb/b.png"));
        factory.Web.OnImage("evil.example.com/a.png", Picture(1));
        factory.Web.OnJson(OpenverseApi, OpenverseJson);
        factory.Web.OnImage("api.openverse.org/v1/images/ov-1/thumb/", new byte[30_000]); // larger than the 20 kB limit here
        factory.Web.OnJson(ArchiveApi, ArchiveJson);
        // A redirect towards the cloud metadata address must not be followed.
        factory.Web.OnRedirect("archive.org/services/img/gundam-catalog-1985", "http://169.254.169.254/latest/meta-data/");

        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id);

        var untrusted = session.Candidates.Single(c => c.Title == "Gunpla box art");
        Assert.Equal("skipped", untrusted.DownloadStatus);
        Assert.Contains("evil.example.com", untrusted.DownloadError);
        Assert.Equal("skipped", session.Candidates.Single(c => c.Title == "RX-78-2 front view").DownloadStatus); // plain http is not trusted
        Assert.Equal("failed", session.Candidates.Single(c => c.Source == "openverse").DownloadStatus);
        var redirected = session.Candidates.Single(c => c.Source == "internet_archive");
        Assert.Equal("failed", redirected.DownloadStatus);
        Assert.Contains("169.254.169.254", redirected.DownloadError);
        Assert.Contains(redirected.Warnings, w => w.Code == "preview_failed");

        Assert.Equal(0, factory.Web.Count("evil.example.com/a.png"));
        Assert.DoesNotContain(factory.Web.Requests, r => r.RequestUri!.Host == "169.254.169.254" || r.RequestUri.Scheme == "http");
        Assert.All(session.Candidates, c => Assert.False(c.HasThumbnail));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/references/{session.Id}/candidates/{untrusted.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Review_AcceptRejectAndDescribeCandidates()
    {
        using var factory = new ApiFactory();
        ServeAll(factory.Web);
        var client = factory.CreateClient();
        var session = await SearchAsync(client, (await CreateSessionAsync(client)).Id);
        var target = session.Candidates[0];

        var updated = await ReadAsync(await client.PutAsJsonAsync($"/api/references/{session.Id}/candidates/{target.Id}",
            new { decision = "accepted", imageKind = "render", relation = "variant", note = "  MG version  " }));
        var c = updated.Candidates.Single(x => x.Id == target.Id);
        Assert.Equal(("accepted", "render", "variant", "MG version"), (c.Decision, c.ImageKind, c.Relation, c.Note));

        var reopened = (await client.GetFromJsonAsync<ReferenceSessionDto>($"/api/references/{session.Id}", ApiFactory.Json))!;
        Assert.Equal("accepted", reopened.Candidates.Single(x => x.Id == target.Id).Decision);
        var summary = (await client.GetFromJsonAsync<PagedResult<ReferenceSessionSummaryDto>>("/api/references", ApiFactory.Json))!.Items.Single();
        Assert.Equal((4, 1), (summary.CandidateCount, summary.AcceptedCount));

        var bad = await client.PutAsJsonAsync($"/api/references/{session.Id}/candidates/{target.Id}", new { decision = "maybe" });
        Assert.Equal("invalid_decision", (await bad.ProblemAsync()).GetProperty("code").GetString());
        var badRelation = await client.PutAsJsonAsync($"/api/references/{session.Id}/candidates/{target.Id}", new { decision = "rejected", relation = "identical" });
        Assert.Equal("invalid_relation", (await badRelation.ProblemAsync()).GetProperty("code").GetString());

        var removed = await ReadAsync(await client.DeleteAsync($"/api/references/{session.Id}/candidates/{target.Id}"));
        Assert.Equal(3, removed.Candidates.Count);
        Assert.False(File.Exists(Path.Combine(factory.Root, "references", session.Id.ToString("N"), $"c{target.Id:N}.jpg")));
    }

    private static async Task<HttpResponseMessage> AddPictureAsync(HttpClient client, Guid id, byte[] image, Dictionary<string, string>? fields = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "my-side-view.png");
        foreach (var (k, v) in fields ?? [])
            form.Add(new StringContent(v), k);
        return await client.PostAsync($"/api/references/{id}/candidates", form);
    }

    [Fact]
    public async Task ManualPictures_AreAcceptedWithTheirLicenceAndValidated()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = (await CreateSessionAsync(client)).Id;

        var session = await ReadAsync(await AddPictureAsync(client, id, Picture(5), new()
        {
            ["author"] = "Me",
            ["license"] = "cc-by",
            ["pageUrl"] = "https://example.com/my-build",
        }));
        var added = Assert.Single(session.Candidates);
        Assert.Equal(("manual", "upload", "accepted"), (added.Origin, added.Source, added.Decision));
        Assert.Equal("my-side-view", added.Title);
        Assert.Equal("cc-by", added.License.Code);
        Assert.Equal("\"my-side-view\" by Me, CC BY", added.Attribution);
        Assert.True(added.HasThumbnail);
        Assert.True(File.Exists(Path.Combine(factory.Root, "references", id.ToString("N"), $"c{added.Id:N}full.png")));

        var again = await AddPictureAsync(client, id, Picture(5));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var script = await AddPictureAsync(client, id, Picture(6), new() { ["pageUrl"] = "javascript:alert(1)" });
        Assert.Equal("invalid_page_url", (await script.ProblemAsync()).GetProperty("code").GetString());
        var licence = await AddPictureAsync(client, id, Picture(6), new() { ["license"] = "free-ish" });
        Assert.Equal("invalid_license", (await licence.ProblemAsync()).GetProperty("code").GetString());
        var notImage = await AddPictureAsync(client, id, "plain text"u8.ToArray());
        Assert.Equal("invalid_image", (await notImage.ProblemAsync()).GetProperty("code").GetString());

        var unknown = await ReadAsync(await AddPictureAsync(client, id, Picture(6)));
        Assert.Contains(unknown.Candidates.Single(c => c.Id != added.Id).Warnings, w => w.Code == "licence_unknown");
    }

    [Fact]
    public async Task Session_FromJobKeepsItsOwnCopyOfThePhoto()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var job = await client.WaitForTerminalAsync((await client.CreateJobAsync()).Id);
        var session = await CreateSessionAsync(client, query: null, sourceJobId: job.Id);

        Assert.Equal(job.Id, session.SourceJobId);
        Assert.True(session.HasInputImage);
        Assert.Equal("part", session.Name); // from the job's file name
        Assert.Equal((128, 96), (session.InputWidth!.Value, session.InputHeight!.Value));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/models/{job.Id}")).StatusCode);
        var full = await client.GetAsync($"/api/references/{session.Id}/input?size=full");
        Assert.Equal("image/png", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/references/{session.Id}/input")).StatusCode);

        using var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "sourceJobId" } };
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/references", form)).StatusCode);
    }

    [Fact]
    public async Task Sessions_AreIsolatedPerOwnerAndDeletedWithTheirFiles()
    {
        using var factory = new ApiFactory();
        ServeAll(factory.Web);
        var owner = factory.CreateClient();
        var session = await SearchAsync(owner, (await CreateSessionAsync(owner, image: Picture(9))).Id);
        var candidate = session.Candidates[0].Id;

        var stranger = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/references/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/references/{session.Id}/input")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/references/{session.Id}/candidates/{candidate}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/references/{session.Id}/search", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/references/{session.Id}")).StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<PagedResult<ReferenceSessionSummaryDto>>("/api/references", ApiFactory.Json))!.Items);

        var folder = Path.Combine(factory.Root, "references", session.Id.ToString("N"));
        Assert.True(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/references/{session.Id}")).StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/references/{session.Id}")).StatusCode);
    }
}
