using System.Text;
using System.Text.Json;
using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Services;
using ImageTo3D.Api.Services.Parametric;
using ImageTo3D.Api.Services.Processes;
using ImageTo3D.Api.Tests.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Tests;

/// <summary>How the API drives the worker process, with process execution replaced by a script of results.</summary>
public sealed class ParametricWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "imageto3d-tests", "worker-" + Guid.NewGuid().ToString("N"));
    private int _instances;

    private sealed class Runner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Func<ProcessSpec, ProcessResult> Describe { get; set; } = _ => Ok(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"engine":{"name":"Parametric","version":"0.1.0"},"templates":[]}"""));

        public Func<ProcessSpec, ProcessResult> Generate { get; set; } = spec =>
        {
            var output = spec.Arguments[^1];
            foreach (var file in Directory.EnumerateFiles(FakeParametricWorker.FixtureDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(output, Path.GetRelativePath(FakeParametricWorker.FixtureDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            return Ok([]);
        };

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            return Task.FromResult(spec.Arguments.Contains("describe") ? Describe(spec) : Generate(spec));
        }
    }

    private static ProcessResult Ok(byte[] stdout) => new(0, stdout, "", false, TimeSpan.FromMilliseconds(5));

    private static ProcessResult Refused(string output, string code, string field)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "response.json"), JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", outputs = Array.Empty<object>(), error = new { code, message = "No.", field } }));
        return new ProcessResult(2, [], "refused", false, TimeSpan.Zero);
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private (ParametricWorker Worker, Runner Runner) Create(bool withPython = true, bool withWorker = true, Action<ParametricOptions>? configure = null)
    {
        var root = Path.Combine(_root, (++_instances).ToString());
        Directory.CreateDirectory(root);
        var workerDir = Path.Combine(root, "worker");
        if (withWorker)
        {
            Directory.CreateDirectory(Path.Combine(workerDir, "parametric"));
            File.WriteAllText(Path.Combine(workerDir, "parametric", "__main__.py"), "");
        }

        var python = Path.Combine(root, "python.exe");
        if (withPython)
            File.WriteAllText(python, "");
        var options = new ParametricOptions { WorkerDirectory = workerDir, PythonPath = python, WorkRoot = Path.Combine(_root, "work") };
        configure?.Invoke(options);
        var runner = new Runner();
        return (new ParametricWorker(runner, Options.Create(options), new Env(_root), TimeProvider.System, NullLogger<ParametricWorker>.Instance), runner);
    }

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Capability_ReportsMissingSetupWithoutStartingAProcess()
    {
        var (noPython, r1) = Create(withPython: false);
        Assert.Equal("missing", (await noPython.GetCapabilityAsync(default)).Status);
        var (noWorker, r2) = Create(withWorker: false);
        Assert.Equal("missing", (await noWorker.GetCapabilityAsync(default)).Status);
        var (disabled, r3) = Create(configure: o => o.Enabled = false);
        Assert.Equal("disabled", (await disabled.GetCapabilityAsync(default)).Status);
        Assert.Empty(r1.Calls.Concat(r2.Calls).Concat(r3.Calls));

        var ex = await Assert.ThrowsAsync<ApiException>(() => noPython.GenerateAsync("spacecraft", null, default));
        Assert.Equal((503, "geometry_engine_unavailable"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Capability_IsReadyAfterDescribeAndCached()
    {
        var (worker, runner) = Create();
        var a = await worker.GetCapabilityAsync(default);
        var b = await worker.GetCapabilityAsync(default);
        Assert.Equal(("ready", "0.1.0"), (a.Status, a.EngineVersion));
        Assert.Same(a, b);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-E", "-s", "-B", "-m", "parametric", "describe"], call.Arguments);
        Assert.Equal(Path.Combine(_root, "1", "worker"), call.WorkingDirectory);
    }

    [Fact]
    public async Task Capability_FailsOnABrokenWorker()
    {
        var (worker, runner) = Create();
        runner.Describe = _ => new ProcessResult(1, [], "ModuleNotFoundError: trimesh", false, TimeSpan.Zero);
        Assert.Equal("failed", (await worker.GetCapabilityAsync(default)).Status);
        runner.Describe = _ => Ok(Encoding.UTF8.GetBytes("""{"schemaVersion":2,"engine":{"version":"9"}}"""));
        var (other, otherRunner) = Create();
        otherRunner.Describe = runner.Describe;
        var cap = await other.GetCapabilityAsync(default);
        Assert.Equal("failed", cap.Status);
        Assert.Contains("schema 2", cap.Reason);
    }

    [Fact]
    public async Task Generate_SendsTheRequestOnStdinAndHandsOverTheFiles()
    {
        var (worker, runner) = Create();
        string dir;
        using (var run = await worker.GenerateAsync("spacecraft", Params("""{"length":200}"""), default))
        {
            dir = run.Directory;
            Assert.Equal("succeeded", run.Response.GetProperty("status").GetString());
            Assert.NotNull(run.File("model.glb"));
            Assert.Null(run.File("../../1/python.exe"));
            Assert.StartsWith(Path.Combine(_root, "work"), dir);

            var call = runner.Calls.Last();
            Assert.Equal(["-E", "-s", "-B", "-m", "parametric", "generate", "--request", "-", "--output", dir], call.Arguments);
            var request = JsonDocument.Parse(call.StandardInput!).RootElement;
            Assert.Equal(1, request.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("spacecraft", request.GetProperty("template").GetString());
            Assert.Equal(200, request.GetProperty("parameters").GetProperty("length").GetInt32());
        }

        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData("geometry_conflict", 422)]
    [InlineData("limit_exceeded", 422)]
    [InlineData("unsupported_thickness", 400)]
    [InlineData("invalid_parameter", 400)]
    [InlineData("unknown_template", 400)]
    public async Task Generate_RefusalsKeepCodeAndField(string code, int status)
    {
        var (worker, runner) = Create();
        runner.Generate = spec => Refused(spec.Arguments[^1], code, "parameters.height");
        var ex = await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("spacecraft", null, default));
        Assert.Equal((status, code, "parameters.height", "No."), (ex.Status, ex.Code, ex.Field, ex.Message));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work")));
    }

    [Fact]
    public async Task Generate_CrashTimeoutAndGarbageAreReportedWithoutDetails()
    {
        var (worker, runner) = Create();
        runner.Generate = _ => new ProcessResult(1, [], "Traceback (most recent call last): secret path C:\\x", false, TimeSpan.Zero);
        var crash = await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("spacecraft", null, default));
        Assert.Equal((500, "geometry_engine_failed"), (crash.Status, crash.Code));
        Assert.DoesNotContain("Traceback", crash.Message);

        runner.Generate = _ => new ProcessResult(-1, [], "", true, TimeSpan.FromMinutes(2));
        var timeout = await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("spacecraft", null, default));
        Assert.Equal((504, "geometry_engine_timeout"), (timeout.Status, timeout.Code));

        runner.Generate = _ => Ok(Encoding.UTF8.GetBytes("not json"));
        var garbage = await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("spacecraft", null, default));
        Assert.Equal(500, garbage.Status);

        // A refusal of the output folder is the API's fault, not the user's.
        runner.Generate = spec => Refused(spec.Arguments[^1], "invalid_output_directory", "output");
        Assert.Equal(500, (await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("spacecraft", null, default))).Status);

        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work")));
    }

    [Fact]
    public async Task Generate_RefusesOversizedRequestsBeforeStarting()
    {
        var (worker, runner) = Create(configure: o => o.MaxRequestBytes = 2000);
        var big = Params("{\"components\":[" + string.Join(",", Enumerable.Repeat("{\"name\":\"abcdefghij\"}", 200)) + "]}");
        var ex = await Assert.ThrowsAsync<ApiException>(() => worker.GenerateAsync("assembly", big, default));
        Assert.Equal((413, "request_too_large"), (ex.Status, ex.Code));
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("generate"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
