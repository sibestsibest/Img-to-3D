using System.Text.Json.Serialization;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Inference;
using ImageTo3D.Api.Services.PartDesign;
using ImageTo3D.Api.Services.Embeddings;
using ImageTo3D.Api.Services.References;
using ImageTo3D.Api.Services.Storage;
using ImageTo3D.Core.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<UploadOptions>().Bind(builder.Configuration.GetSection(UploadOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<InferenceClientOptions>().Bind(builder.Configuration.GetSection(InferenceClientOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ModelOutputOptions>().Bind(builder.Configuration.GetSection(ModelOutputOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<PartDesignOptions>().Bind(builder.Configuration.GetSection(PartDesignOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<EmbeddingOptions>().Bind(builder.Configuration.GetSection(EmbeddingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ModelSearchOptions>().Bind(builder.Configuration.GetSection(ModelSearchOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<ReferenceSearchOptions>().Bind(builder.Configuration.GetSection(ReferenceSearchOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    var dbPath = Path.GetFullPath(storage.DatabasePath, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IOwnerAccessor, HttpOwnerAccessor>();
builder.Services.AddSingleton<IArtifactStore, LocalFileArtifactStore>();
builder.Services.AddSingleton<ArtifactProcessor>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<JobService>();
builder.Services.AddSingleton<IDesignArtifactStore, LocalDesignArtifactStore>();
builder.Services.AddScoped<PartDesignService>();
builder.Services.AddSingleton<IReferenceArtifactStore, LocalReferenceArtifactStore>();
builder.Services.AddSingleton<ReferenceDownloadCache>();
builder.Services.AddSingleton<ReferenceHttp>();
builder.Services.AddSingleton<IReferenceSource, WikimediaCommonsSource>();
builder.Services.AddSingleton<IReferenceSource, OpenverseSource>();
builder.Services.AddSingleton<IReferenceSource, InternetArchiveSource>();
builder.Services.AddSingleton<SketchfabSource>();
builder.Services.AddSingleton<IImageEmbedder, ClipEmbedder>();
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<ReconstructionService>();
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.AddHostedService<RetentionCleanupService>();

builder.Services.AddHttpClient<IInferenceClient, InferenceClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<InferenceClientOptions>>().Value;
    http.BaseAddress = o.BaseUrl;
    http.Timeout = o.RequestTimeout;
    if (!string.IsNullOrEmpty(o.ApiKey))
        http.DefaultRequestHeaders.Add("X-Internal-Api-Key", o.ApiKey);
});

// Reference sources: timeouts come from per-call tokens, and redirects are followed by ReferenceHttp
// itself so every hop is checked against the source's trusted hosts.
builder.Services.AddHttpClient(ReferenceHttp.ClientName, (sp, http) =>
    {
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(sp.GetRequiredService<IOptions<ReferenceSearchOptions>>().Value.UserAgent);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddApiProblemDetails();
builder.Services.AddApiRateLimiting();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // EF Core migrations; databases created before migrations existed are baselined first (see README).
    DatabaseInitializer.Initialize(scope.ServiceProvider.GetRequiredService<AppDbContext>(), app.Logger);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<OwnerCookieMiddleware>();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.Run();

public partial class Program;
