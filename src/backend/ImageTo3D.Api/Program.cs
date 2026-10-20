using System.Text.Json.Serialization;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Data;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Inference;
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

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddApiProblemDetails();
builder.Services.AddApiRateLimiting();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Schema is created on first run (no migrations yet; see README).
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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
