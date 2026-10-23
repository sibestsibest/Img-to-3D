using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Services.Processes;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.Parametric;

/// <summary>Whether the parametric worker can be used: <c>ready</c>, <c>missing</c> (not set up), <c>disabled</c> or <c>failed</c>.</summary>
public sealed record ParametricCapability(string Status, string? Reason, string? EngineVersion, JsonElement? Description);

/// <summary>
/// Files of one finished generation in a temporary folder, plus the worker's response. Disposing
/// deletes the folder: copy what must be kept first.
/// </summary>
public sealed class ParametricRun(string directory, JsonElement response) : IDisposable
{
    public JsonElement Response { get; } = response;

    /// <summary>Absolute path of an output listed in the response (e.g. "model.glb"), or null if it is not there.</summary>
    public string? File(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(directory, relativePath));
        return full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && System.IO.File.Exists(full) ? full : null;
    }

    public string Directory => directory;

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start's cleanup of the work root.
        }
    }
}

public interface IParametricWorker
{
    Task<ParametricCapability> GetCapabilityAsync(CancellationToken cancellationToken);

    /// <summary>Runs one generation. Refusals and failures are thrown as <see cref="ApiException"/> with the worker's code and field.</summary>
    Task<ParametricRun> GenerateAsync(string template, JsonElement? parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Calls <c>python -m parametric</c> (src/parametric-geometry) as a child process: the request goes
/// to stdin, files go to a fresh folder under the work root, the response is read from that
/// folder. Only configured paths and generated folder names reach the command line.
/// </summary>
public sealed class ParametricWorker : IParametricWorker, IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly TimeSpan FailureCacheTime = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly ParametricOptions _options;
    private readonly ILogger<ParametricWorker> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _describeLock = new(1, 1);
    private readonly string _workerDirectory;
    private readonly string _python;
    private readonly string _workRoot;
    private ParametricCapability? _capability;
    private DateTimeOffset _capabilityTime;

    public ParametricWorker(IProcessRunner runner, IOptions<ParametricOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<ParametricWorker> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        _time = time;
        _slots = new SemaphoreSlim(_options.MaxConcurrentRuns, _options.MaxConcurrentRuns);
        var root = environment.ContentRootPath;
        _workerDirectory = Path.GetFullPath(_options.WorkerDirectory, root);
        _python = string.IsNullOrWhiteSpace(_options.PythonPath)
            ? Path.Combine(_workerDirectory, ".venv", OperatingSystem.IsWindows() ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python"))
            : Path.GetFullPath(_options.PythonPath, root);
        _workRoot = Path.GetFullPath(_options.WorkRoot, root);
    }

    public async Task<ParametricCapability> GetCapabilityAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return new ParametricCapability("disabled", "Parametric generation is turned off (Parametric:Enabled).", null, null);
        if (!System.IO.File.Exists(Path.Combine(_workerDirectory, "parametric", "__main__.py")))
            return new ParametricCapability("missing", "The parametric geometry worker (src/parametric-geometry) was not found.", null, null);
        if (!System.IO.File.Exists(_python))
            return new ParametricCapability("missing", "The worker's Python environment is not set up; run scripts/setup.ps1 (needs uv) or see src/parametric-geometry/README.md.", null, null);

        await _describeLock.WaitAsync(cancellationToken);
        try
        {
            if (_capability is { } cached && (cached.Status == "ready" || _time.GetUtcNow() - _capabilityTime < FailureCacheTime))
                return cached;
            _capability = await DescribeAsync(cancellationToken);
            _capabilityTime = _time.GetUtcNow();
            return _capability;
        }
        finally
        {
            _describeLock.Release();
        }
    }

    private async Task<ParametricCapability> DescribeAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(Spec(["describe"], null, _options.DescribeTimeout), cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            _logger.LogWarning("Parametric worker describe failed (exit {ExitCode}, timed out {TimedOut}): {Log}", result.ExitCode, result.TimedOut, Tail(result.StandardError));
            return new ParametricCapability("failed", "The parametric geometry worker did not start correctly; see the API log.", null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement.Clone();
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
                return new ParametricCapability("failed", $"The worker speaks schema {root.GetProperty("schemaVersion").GetInt32()}, this API expects {SchemaVersion}.", null, null);
            var version = root.GetProperty("engine").GetProperty("version").GetString();
            return new ParametricCapability("ready", null, version, root);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Parametric worker describe returned unreadable output");
            return new ParametricCapability("failed", "The parametric geometry worker returned an unreadable description.", null, null);
        }
    }

    public async Task<ParametricRun> GenerateAsync(string template, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var capability = await GetCapabilityAsync(cancellationToken);
        if (capability.Status != "ready")
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "geometry_engine_unavailable", capability.Reason ?? "The parametric geometry worker is not available.");

        var request = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["template"] = template,
            ["parameters"] = parameters is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } p ? JsonNode.Parse(p.GetRawText()) : new JsonObject(),
        };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
        if (bytes.Length > _options.MaxRequestBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "request_too_large", $"The parameters are larger than {_options.MaxRequestBytes} bytes.");

        if (!await _slots.WaitAsync(_options.Timeout, cancellationToken))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "geometry_engine_busy", "The geometry engine is busy; try again shortly.");
        var output = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        ParametricRun? run = null;
        try
        {
            System.IO.Directory.CreateDirectory(_workRoot);
            var result = await _runner.RunAsync(Spec(["generate", "--request", "-", "--output", output], bytes, _options.Timeout), cancellationToken);
            run = Interpret(result, output);
            return run;
        }
        finally
        {
            _slots.Release();
            // On success the caller owns (and deletes) the folder; otherwise nothing of it is kept.
            if (run is null && System.IO.Directory.Exists(output))
                new ParametricRun(output, default).Dispose();
        }
    }

    private ParametricRun Interpret(ProcessResult result, string output)
    {
        if (result.TimedOut)
        {
            _logger.LogWarning("Parametric worker timed out after {Timeout}", _options.Timeout);
            throw new ApiException(StatusCodes.Status504GatewayTimeout, "geometry_engine_timeout", $"Generating the geometry took longer than {_options.Timeout.TotalSeconds:0} s and was stopped. Use fewer components or segments.");
        }

        var response = ReadResponse(output, result.StandardOutput);
        if (result.ExitCode == 0 && response is { } ok && ok.TryGetProperty("status", out var status) && status.GetString() == "succeeded")
        {
            _logger.LogInformation("Parametric worker generated {Outputs} files in {Duration} ms", ok.GetProperty("outputs").GetArrayLength(), (int)result.Duration.TotalMilliseconds);
            return new ParametricRun(output, ok);
        }

        if (result.ExitCode == 2 && response is { } refused && refused.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.GetProperty("code").GetString() ?? "invalid_parameter";
            var message = error.GetProperty("message").GetString() ?? "The parameters were refused.";
            var field = error.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (code != "invalid_output_directory")
            {
                var http = code is "geometry_conflict" or "limit_exceeded" ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status400BadRequest;
                throw new ApiException(http, code, message, field);
            }
        }

        _logger.LogError("Parametric worker failed (exit {ExitCode}): {Log}", result.ExitCode, Tail(result.StandardError));
        throw new ApiException(StatusCodes.Status500InternalServerError, "geometry_engine_failed", "The geometry engine failed; see the API log.");
    }

    private static JsonElement? ReadResponse(string output, byte[] stdout)
    {
        var file = Path.Combine(output, "response.json");
        try
        {
            var bytes = System.IO.File.Exists(file) ? System.IO.File.ReadAllBytes(file) : stdout;
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private ProcessSpec Spec(IReadOnlyList<string> command, byte[]? stdin, TimeSpan timeout) => new(
        _python,
        // -E/-s: ignore PYTHON* variables and the user site; -B: no .pyc files next to the sources.
        ["-E", "-s", "-B", "-m", "parametric", .. command],
        _workerDirectory,
        timeout,
        stdin,
        MaxStandardOutputBytes: 4 * 1024 * 1024,
        MaxStandardErrorBytes: _options.MaxLogBytes);

    private static string Tail(string log) => log.Length > 4000 ? log[^4000..] : log;

    public void Dispose()
    {
        _slots.Dispose();
        _describeLock.Dispose();
    }
}
