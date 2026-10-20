using System.Net;
using System.Net.Http.Json;
using ImageTo3D.Api.Tests.Infrastructure;
using SkiaSharp;

namespace ImageTo3D.Api.Tests;

public class UploadValidationTests
{
    [Fact]
    public async Task UnsupportedFormat_Returns400WithCode()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var gif = "GIF89a"u8.ToArray().Concat(new byte[200]).ToArray();
        var response = await client.UploadAsync(gif, "part.gif", "image/gif");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal("invalid_image", problem.GetProperty("code").GetString());
        Assert.Equal(0, factory.Inference.Submissions);
    }

    [Fact]
    public async Task ContentTypeIsIgnored_SignatureDecides()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        // Text pretending to be PNG via the client-declared content type and file name.
        var response = await client.UploadAsync("not really an image"u8.ToArray(), "evil.png", "image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CorruptImageWithValidSignature_Returns400()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var png = TestImages.Png(300, 300);
        var response = await client.UploadAsync(png[..(png.Length / 3)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingFile_Returns400()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        using var content = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        var response = await client.PostAsync("/api/models", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("missing_file", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TooLarge_Returns413()
    {
        using var factory = new ApiFactory(new() { ["Upload:MaxBytes"] = "2048" });
        var client = factory.CreateClient();

        var response = await client.UploadAsync(TestImages.Encode(400, 400, SKEncodedImageFormat.Png).Concat(new byte[4096]).ToArray());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TooSmallDimensions_Returns400()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.UploadAsync(TestImages.Png(16, 16));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("too small", (await response.ProblemAsync()).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task SupportedFormats_Accepted(SKEncodedImageFormat format, string contentType)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.UploadAsync(TestImages.Encode(128, 128, format), "part", contentType);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task FileNameIsSanitised()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.UploadAsync(TestImages.Png(), "../../etc/<script>passwd.png");
        var job = await response.Content.ReadFromJsonAsync<ImageTo3D.Api.DTOs.JobDto>(ApiFactory.Json);

        Assert.DoesNotContain("/", job!.OriginalFileName);
        Assert.DoesNotContain("<", job.OriginalFileName);
        Assert.EndsWith(".png", job.OriginalFileName);
    }

    [Fact]
    public async Task InvalidIdempotencyKey_Returns400()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.UploadAsync(TestImages.Png(), idempotencyKey: "bad key!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
