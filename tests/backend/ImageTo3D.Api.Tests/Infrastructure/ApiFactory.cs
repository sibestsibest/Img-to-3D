using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Api.Services.Inference;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace ImageTo3D.Api.Tests.Infrastructure;

/// <summary>Isolated API instance: own temp storage, own SQLite file, fake inference client.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Dictionary<string, string?> _settings;

    public ApiFactory(Dictionary<string, string?>? overrides = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        _settings = new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = Path.Combine(Root, "storage"),
            ["Storage:DatabasePath"] = Path.Combine(Root, "test.db"),
            ["InferenceService:PollInterval"] = "00:00:00.020",
            ["InferenceService:GenerationTimeout"] = "00:00:05",
            ["InferenceService:MaxConsecutivePollFailures"] = "3",
            ["Upload:UploadsPerMinute"] = "1000",
            ["Upload:MaxActiveJobsPerOwner"] = "50",
        };
        foreach (var (k, v) in overrides ?? [])
            _settings[k] = v;
    }

    public string Root { get; }

    public FakeInferenceClient Inference { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings)
            builder.UseSetting(k, v);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInferenceClient>();
            services.AddSingleton<IInferenceClient>(Inference);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class TestImages
{
    public static byte[] Png(int w = 128, int h = 96) => Encode(w, h, SKEncodedImageFormat.Png);

    public static byte[] Encode(int w, int h, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.SteelBlue };
            canvas.DrawRect(w / 4f, h / 4f, w / 2f, h / 2f, paint);
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }
}

public static class ApiClientExtensions
{
    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, byte[] bytes, string fileName = "part.png", string contentType = "image/png", string? idempotencyKey = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(file, "image", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/models") { Content = content };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    public static async Task<JobDto> CreateJobAsync(this HttpClient client)
    {
        var response = await client.UploadAsync(TestImages.Png());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDto>(ApiFactory.Json))!;
    }

    public static async Task<JobDto> GetJobAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JobDto>($"/api/models/{id}", ApiFactory.Json))!;

    public static async Task<JobDto> WaitForTerminalAsync(this HttpClient client, Guid id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var job = await client.GetJobAsync(id);
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
                return job;
            await Task.Delay(25);
        }

        throw new TimeoutException("Job did not reach a terminal state.");
    }

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
