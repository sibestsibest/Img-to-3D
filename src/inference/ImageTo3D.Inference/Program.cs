using System.Net;
using ImageTo3D.Core.Hosting;
using ImageTo3D.Inference.Api;
using ImageTo3D.Inference.Cli;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Jobs;
using ImageTo3D.Inference.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

if (args.Length > 0 && args[0] == "generate")
{
    var cliBuilder = Host.CreateApplicationBuilder(args[1..]);
    GenerateCommand.AddPipelineServices(cliBuilder.Services, cliBuilder.Configuration);
    using var cliHost = cliBuilder.Build();
    return await GenerateCommand.RunAsync(args, cliHost.Services);
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

GenerateCommand.AddPipelineServices(builder.Services, builder.Configuration);
builder.Services.AddSingleton<InferenceJobStore>();
builder.Services.AddSingleton<InferenceQueue>();
builder.Services.AddHostedService<ModelLoaderService>();
builder.Services.AddHostedService<InferenceWorker>();
builder.Services.AddSingleton<RetentionCleanupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanupService>());
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 60L * 1024 * 1024);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.MapInferenceEndpoints();

// Refuse to expose the unauthenticated internal API beyond loopback.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    var exposed = addresses.Where(a => !IsLoopback(a)).ToArray();
    if (exposed.Length > 0 && string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<InferenceOptions>>().Value.ApiKey))
    {
        app.Logger.LogCritical("Inference service is bound to a non-loopback address without Inference:ApiKey; shutting down");
        app.Lifetime.StopApplication();
    }
});

app.Run();
return 0;

static bool IsLoopback(string address)
{
    if (!Uri.TryCreate(address.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"), UriKind.Absolute, out var uri))
        return false;
    return uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}

public partial class Program;
