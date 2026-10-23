using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Services.Processes;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageTo3D.Api.Tests;

/// <summary>Runs only when the worker's virtual environment exists (scripts/setup.ps1 with uv).</summary>
public sealed class RequiresParametricWorkerFactAttribute : FactAttribute
{
    public RequiresParametricWorkerFactAttribute()
    {
        if (ParametricRealWorkerTests.Python() is null)
            Skip = "src/parametric-geometry/.venv not found (run scripts/setup.ps1 with uv installed).";
    }
}

/// <summary>The real process runner and the real Python worker, end to end through the API.</summary>
[Trait("Category", "ParametricWorker")]
public class ParametricRealWorkerTests
{
    public static string? Python()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var venv = Path.Combine(dir.FullName, "src", "parametric-geometry", ".venv");
            var python = OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");
            if (File.Exists(python))
                return python;
        }

        return null;
    }

    private static ProcessSpec Script(string code, TimeSpan timeout, byte[]? stdin = null, int maxErr = 64 * 1024) =>
        new(Python()!, ["-I", "-c", code], Path.GetTempPath(), timeout, stdin, MaxStandardErrorBytes: maxErr);

    [RequiresParametricWorkerFact]
    public async Task Runner_PassesStdinAndCapturesOutputAndExitCode()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var result = await runner.RunAsync(Script("import sys; d=sys.stdin.buffer.read(); sys.stdout.write(str(len(d))); sys.stderr.write('E'*300000); sys.exit(3)", TimeSpan.FromSeconds(30), Encoding.UTF8.GetBytes(new string('x', 200_000)), maxErr: 1000), default);
        Assert.Equal((3, false), (result.ExitCode, result.TimedOut));
        Assert.Equal("200000", Encoding.UTF8.GetString(result.StandardOutput));
        Assert.Equal(new string('E', 1000), result.StandardError);
    }

    [RequiresParametricWorkerFact]
    public async Task Runner_KillsAProcessThatRunsTooLong()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var watch = Stopwatch.StartNew();
        var result = await runner.RunAsync(Script("import time; time.sleep(60)", TimeSpan.FromSeconds(1)), default);
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [RequiresParametricWorkerFact]
    public async Task Runner_CancellationKillsAndThrows()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Script("import time; time.sleep(60)", TimeSpan.FromMinutes(1)), cts.Token));
    }

    [RequiresParametricWorkerFact]
    public async Task Spacecraft_IsGeneratedByTheRealWorker()
    {
        using var factory = new ApiFactory(realParametricWorker: true);
        var client = factory.CreateClient();

        var caps = await client.GetFromJsonAsync<CapabilitiesDto>("/api/capabilities", ApiFactory.Json);
        Assert.Equal("ready", caps!.Engines.Single(e => e.Id == "parametric").Status);
        var templates = await client.GetFromJsonAsync<JsonElement>("/api/part-designs/parametric/templates");
        Assert.Contains(templates.GetProperty("templates").EnumerateArray(), t => t.GetProperty("name").GetString() == "spacecraft");

        var response = await client.PostAsJsonAsync("/api/part-designs/parametric", new { template = "spacecraft", parameters = new { length = 200, sheet_thickness = 1.5 } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var project = (await response.Content.ReadFromJsonAsync<PartDesignProjectDto>(ApiFactory.Json))!;

        Assert.Equal("Spacecraft 200 x 80 x 70 mm", project.Name);
        Assert.Equal(36, project.Parts.Count);
        Assert.All(project.Parts, p => Assert.True(p.Geometry.Closed, p.Name));
        Assert.All(project.Parts.Where(p => p.Category == "sheet" && p.Name.StartsWith("side_wall")), p => Assert.Equal(1.5, p.Spec.ThicknessMm));
        Assert.Equal(200, project.Parts.Max(p => p.Geometry.Bounds.Max.X) - project.Parts.Min(p => p.Geometry.Bounds.Min.X), 3);
        // 2 rows of floor((140 - 8) / 12) + 1 = 12 bolts on a 140 mm hull.
        Assert.Equal("24 identical pieces in this part", project.Parts.Single(p => p.Name == "bolt_heads").Spec.StockDescription);
        Assert.Contains(project.Issues, i => i.Code == "requires_forming" && i.Source == "parametric");

        var pdf = await client.GetByteArrayAsync($"/api/part-designs/{project.Id}/parametric/files/templates-pdf");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(factory.Root, "parametric-work")));

        var refused = await client.PostAsJsonAsync("/api/part-designs/parametric", new { template = "spacecraft", parameters = new { sheet_thickness = 0.7 } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("unsupported_thickness", "parameters.sheet_thickness"), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
    }
}
