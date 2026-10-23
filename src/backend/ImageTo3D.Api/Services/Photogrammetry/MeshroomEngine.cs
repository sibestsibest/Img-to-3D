using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Services.Processes;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Photogrammetry;

/// <param name="Status"><c>ready</c>, <c>missing</c> (not configured or not found), <c>disabled</c> or <c>failed</c> (found but unusable).</param>
/// <param name="Executable">Full path of meshroom_batch (internal: never sent to clients).</param>
/// <param name="Pipelines">Pipeline templates of the installation that this application allows.</param>
/// <param name="DefaultPipeline">photogrammetry with a CUDA GPU, otherwise photogrammetryDraft (CPU only).</param>
public sealed record MeshroomCapability(
    string Status,
    string? Reason,
    string? Version,
    string? Executable,
    IReadOnlyList<string> Pipelines,
    string? DefaultPipeline,
    bool Cuda,
    string? Gpu,
    IReadOnlyList<string> Notes);

public interface IMeshroomEngine
{
    Task<MeshroomCapability> GetCapabilityAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Finds and checks a local Meshroom installation without assuming its layout: the meshroom_batch
/// executable is searched in the configured folder, the pipeline templates (.mg files) actually
/// installed are listed, and the CLI's own --help must offer the options this application uses.
/// A working check is cached; a failed one is retried after a minute.
/// </summary>
public sealed partial class MeshroomEngine(IProcessRunner runner, IOptions<PhotogrammetryOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<MeshroomEngine> logger) : IMeshroomEngine, IDisposable
{
    /// <summary>Templates this application runs (whole-object reconstruction); others (camera tracking, HDR, ...) are not offered.</summary>
    public static readonly IReadOnlyList<string> AllowedPipelines = ["photogrammetry", "photogrammetryDraft", "photogrammetryObject", "photogrammetryObjectTurntable"];

    /// <summary>CLI options used by <see cref="PhotogrammetryWorker"/>; an installation whose --help lacks one is refused.</summary>
    public static readonly IReadOnlyList<string> RequiredOptions = ["--input", "--pipeline", "--output", "--cache"];

    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MeshroomCapability? _cached;
    private DateTimeOffset _checkedAt;

    public async Task<MeshroomCapability> GetCapabilityAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled)
            return Missing("disabled", "Photogrammetry is turned off (Photogrammetry:Enabled).");
        if (string.IsNullOrWhiteSpace(o.MeshroomPath))
            return Missing("missing", "Meshroom is not configured: install Meshroom 2025.1 or newer and set Photogrammetry:MeshroomPath to its folder.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } c && (c.Status == "ready" || time.GetUtcNow() - _checkedAt < RetryFailedAfter))
                return c;
            _cached = await CheckAsync(Path.GetFullPath(o.MeshroomPath, environment.ContentRootPath), cancellationToken);
            _checkedAt = time.GetUtcNow();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static MeshroomCapability Missing(string status, string reason) => new(status, reason, null, null, [], null, false, null, []);

    private async Task<MeshroomCapability> CheckAsync(string configured, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(configured);
        if (executable is null)
            return Missing("missing", $"meshroom_batch was not found in the configured Meshroom folder ({Path.GetFileName(configured.TrimEnd('\\', '/'))}).");

        var help = await runner.RunAsync(new ProcessSpec(executable, ["--help"], Path.GetDirectoryName(executable)!, TimeSpan.FromSeconds(90)), cancellationToken);
        var text = Encoding.UTF8.GetString(help.StandardOutput) + help.StandardError;
        if (help.TimedOut || help.ExitCode != 0)
        {
            logger.LogWarning("meshroom_batch --help failed (exit {ExitCode}, timed out {TimedOut}): {Log}", help.ExitCode, help.TimedOut, Tail(text));
            return Missing("failed", "meshroom_batch did not start correctly (see the API log).") with { Executable = executable };
        }

        var lacking = RequiredOptions.Where(opt => !text.Contains(opt, StringComparison.Ordinal)).ToList();
        if (lacking.Count > 0)
            return Missing("failed", $"This Meshroom version's command line lacks {string.Join(", ", lacking)}; Meshroom 2025.1 or newer is needed.") with { Executable = executable };

        var notes = new List<string>();
        var root = File.Exists(configured) ? Path.GetDirectoryName(configured)! : configured;
        var installed = FindPipelines(root);
        IReadOnlyList<string> pipelines;
        if (installed.Count > 0)
        {
            pipelines = AllowedPipelines.Where(p => installed.Contains(p)).ToList();
        }
        else
        {
            pipelines = ["photogrammetry", "photogrammetryDraft"];
            notes.Add("The installation's pipeline files were not found; photogrammetry and photogrammetryDraft are assumed to exist.");
        }

        if (pipelines.Count == 0)
            return Missing("failed", "None of the photogrammetry pipelines (photogrammetry, photogrammetryDraft, photogrammetryObject) is installed.") with { Executable = executable };

        var (cuda, gpu) = await DetectCudaAsync(cancellationToken);
        var preferred = cuda ? "photogrammetry" : "photogrammetryDraft";
        var defaultPipeline = pipelines.Contains(preferred) ? preferred : pipelines[0];
        if (!cuda)
            notes.Add("No CUDA GPU detected: the full pipeline's depth maps need one, so the CPU-only draft pipeline is the default (coarser).");

        var version = VersionPattern().Match(executable) is { Success: true } m ? m.Value : null;
        logger.LogInformation("Meshroom {Version} ready: {Pipelines}, default {Default}, CUDA {Cuda}", version ?? "(version unknown)", string.Join(", ", pipelines), defaultPipeline, cuda);
        return new MeshroomCapability("ready", null, version, executable, pipelines, defaultPipeline, cuda, gpu, notes);
    }

    /// <summary>The configured file, or meshroom_batch in the folder or up to three levels below it.</summary>
    public static string? FindExecutable(string configured)
    {
        if (File.Exists(configured))
            return Path.GetFileNameWithoutExtension(configured).Equals("meshroom_batch", StringComparison.OrdinalIgnoreCase) ? configured : null;
        if (!Directory.Exists(configured))
            return null;
        string[] names = OperatingSystem.IsWindows() ? ["meshroom_batch.exe", "meshroom_batch.bat", "meshroom_batch.cmd"] : ["meshroom_batch"];
        foreach (var dir in Walk(configured, 3))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Names of the .mg pipeline templates installed (searched up to six levels deep).</summary>
    public static HashSet<string> FindPipelines(string root)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Walk(root, 6))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.mg"))
                    found.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    private static IEnumerable<string> Walk(string root, int depth)
    {
        var level = new List<string> { root };
        for (var d = 0; d <= depth && level.Count > 0; d++)
        {
            foreach (var dir in level)
                yield return dir;
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            level = next;
        }
    }

    /// <summary>nvidia-smi reports the GPU and its compute capability; Meshroom 2025 needs CUDA compute capability 5.0 or newer.</summary>
    private async Task<(bool Cuda, string? Gpu)> DetectCudaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec("nvidia-smi", ["--query-gpu=name,compute_cap", "--format=csv,noheader"], Path.GetTempPath(), TimeSpan.FromSeconds(15)), cancellationToken);
            if (result.TimedOut || result.ExitCode != 0)
                return (false, null);
            var line = Encoding.UTF8.GetString(result.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var parts = line?.Split(',', StringSplitOptions.TrimEntries);
            if (parts is not { Length: 2 } || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var capability))
                return (false, parts?[0]);
            return (capability >= 5.0, parts[0]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, null); // nvidia-smi not installed: no NVIDIA GPU or driver
        }
    }

    private static string Tail(string text) => text.Length > 2000 ? text[^2000..] : text;

    [GeneratedRegex(@"20\d\d\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    public void Dispose() => _lock.Dispose();
}
