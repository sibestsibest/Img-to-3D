using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Text.Json;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Parametric;

namespace ImageTo3D.Api.Tests.Infrastructure;

/// <summary>
/// Stands in for the Python worker: a successful run hands over a copy of a real worker output
/// (Fixtures/parametric, an "assembly" of plate, shell, rod, tube and block), so no Python is needed.
/// </summary>
public sealed class FakeParametricWorker : IParametricWorker
{
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "parametric");

    public ParametricCapability Capability { get; set; } = new("ready", null, "0.1.0", Describe());

    /// <summary>Thrown instead of generating (e.g. a refusal with a field), when set.</summary>
    public ApiException? Failure { get; set; }

    public ConcurrentQueue<(string Template, string? Parameters)> Requests { get; } = new();

    public List<string> RunDirectories { get; } = [];

    public Task<ParametricCapability> GetCapabilityAsync(CancellationToken cancellationToken) => Task.FromResult(Capability);

    public Task<ParametricRun> GenerateAsync(string template, JsonElement? parameters, CancellationToken cancellationToken)
    {
        Requests.Enqueue((template, parameters?.GetRawText()));
        if (Capability.Status != "ready")
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "geometry_engine_unavailable", Capability.Reason ?? "unavailable");
        if (Failure is not null)
            throw Failure;

        var dir = Path.Combine(Path.GetTempPath(), "imageto3d-tests", "parametric-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir, Path.GetRelativePath(FixtureDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        lock (RunDirectories)
            RunDirectories.Add(dir);
        using var response = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "response.json")));
        return Task.FromResult(new ParametricRun(dir, response.RootElement.Clone()));
    }

    private static JsonElement Describe()
    {
        using var doc = JsonDocument.Parse("""
            {"schemaVersion":1,"engine":{"name":"Parametric","version":"0.1.0"},"origin":"parametric","units":"mm","upAxis":"y",
             "sheetThicknessesMm":[0.3,0.5,0.8,1.0,1.5,2.0,3.0],"exports":["glb","stl","obj","bom","templates","views"],
             "templates":[{"name":"assembly","description":"Compound model","parameters":[{"name":"segments","type":"integer","default":48,"minimum":12,"maximum":256,"unit":"","description":""}]}]}
            """);
        return doc.RootElement.Clone();
    }
}
