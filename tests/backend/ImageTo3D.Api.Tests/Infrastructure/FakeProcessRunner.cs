using System.Collections.Concurrent;
using System.Text;
using ImageTo3D.Api.Services.Processes;

namespace ImageTo3D.Api.Tests.Infrastructure;

public enum FakeMeshroomMode
{
    Succeed,
    /// <summary>Only Meshroom's cache holds a mesh (no Publish output).</summary>
    CacheOnly,
    Fail,
    Timeout,
    NoOutput,
    BadObj,
}

/// <summary>
/// Plays meshroom_batch and nvidia-smi; every other executable (the Python worker) really runs.
/// A run writes Meshroom-like cache status files and outputs; with <see cref="Gate"/> set it stays
/// "running" until released or cancelled, so progress and cancellation can be observed.
/// </summary>
public sealed class FakeProcessRunner(IProcessRunner real) : IProcessRunner
{
    public const string Help = "usage: meshroom_batch [-h] [-i ...] [-I ...] [-p PIPELINE] [-o FOLDER] [-s FILE] [--cache FOLDER] [--compute <yes/no>]\n  -i, --input  -p, --pipeline  -o, --output  --cache";

    public FakeMeshroomMode Mode { get; set; } = FakeMeshroomMode.Succeed;

    public string HelpText { get; set; } = Help;

    public int HelpExitCode { get; set; }

    /// <summary>nvidia-smi output line ("name, compute capability"); null = no NVIDIA driver installed.</summary>
    public string? Gpu { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public ConcurrentQueue<ProcessSpec> Calls { get; } = new();

    /// <summary>Photos Meshroom found in its input folder, per run.</summary>
    public ConcurrentQueue<string[]> InputFiles { get; } = new();

    public int CancelledRuns;

    public Task Started => _started.Task;

    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        if (spec.FileName == "nvidia-smi")
        {
            Calls.Enqueue(spec);
            return Gpu is null
                ? throw new System.ComponentModel.Win32Exception(2, "nvidia-smi not found")
                : Result(0, Gpu + "\n");
        }

        if (!Path.GetFileNameWithoutExtension(spec.FileName).Equals("meshroom_batch", StringComparison.OrdinalIgnoreCase))
            return await real.RunAsync(spec, cancellationToken);

        Calls.Enqueue(spec);
        if (spec.Arguments is ["--help"])
            return Result(HelpExitCode, HelpText);

        string Arg(string name) => spec.Arguments[spec.Arguments.ToList().IndexOf(name) + 1];
        var input = Arg("--input");
        var output = Arg("--output");
        var cache = Arg("--cache");
        InputFiles.Enqueue(Directory.GetFiles(input).Select(Path.GetFileName).Order().ToArray()!);

        Status(cache, "CameraInit", "SUCCESS");
        Status(cache, "FeatureExtraction", "SUCCESS");
        Status(cache, "DepthMap", "RUNNING");
        _started.TrySetResult();
        try
        {
            if (Gate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            else
                await Task.Delay(50, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref CancelledRuns);
            throw;
        }

        Status(cache, "DepthMap", "SUCCESS");
        switch (Mode)
        {
            case FakeMeshroomMode.Fail:
                return Result(1, "[info] Starting\n", "Error: SfM reconstruction failed: not enough matched views");
            case FakeMeshroomMode.Timeout:
                return new ProcessResult(-1, Encoding.UTF8.GetBytes("[info] still meshing"), "", true, spec.Timeout);
            case FakeMeshroomMode.NoOutput:
                return Result(0, "[info] done\n");
            case FakeMeshroomMode.BadObj:
                File.WriteAllText(Path.Combine(output, "texturedMesh.obj"), "v 0 0\nf 1 2 3\n");
                return Result(0, "[info] done\n");
            case FakeMeshroomMode.CacheOnly:
                var meshing = Path.Combine(cache, "Meshing", "c0ffee");
                Directory.CreateDirectory(meshing);
                File.WriteAllText(Path.Combine(meshing, "mesh.obj"), CubeObj);
                Status(cache, "Meshing", "SUCCESS");
                return Result(0, "[info] done\n");
            default:
                File.WriteAllText(Path.Combine(output, "texturedMesh.obj"), "mtllib texturedMesh.mtl\n" + CubeObj);
                File.WriteAllText(Path.Combine(output, "texturedMesh.mtl"), "newmtl material_0\nmap_Kd texture_1001.png\n");
                File.WriteAllBytes(Path.Combine(output, "texture_1001.png"), TestImages.Png(64, 64));
                Status(cache, "Texturing", "SUCCESS");
                Status(cache, "Publish", "SUCCESS");
                return Result(0, "[info] Meshroom finished\n");
        }
    }

    /// <summary>A 2 x 1 x 0.5 box in Meshroom units, quads and triangles mixed, with texture indices.</summary>
    public const string CubeObj = """
        v -1 -0.5 -0.25
        v 1 -0.5 -0.25
        v 1 0.5 -0.25
        v -1 0.5 -0.25
        v -1 -0.5 0.25
        v 1 -0.5 0.25
        v 1 0.5 0.25
        v -1 0.5 0.25
        vt 0 0
        f 1/1 3/1 2/1
        f 1/1 4/1 3/1
        f 5/1 6/1 7/1 8/1
        f 1/1 2/1 6/1 5/1
        f 4/1 8/1 7/1 3/1
        f 1/1 5/1 8/1 4/1
        f 2/1 3/1 7/1 6/1

        """;

    private static void Status(string cache, string node, string status)
    {
        var dir = Path.Combine(cache, node, "a1b2c3");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status"), $$"""{"status": "{{status}}", "execMode": "LOCAL"}""");
    }

    private static ProcessResult Result(int exit, string stdout, string stderr = "") => new(exit, Encoding.UTF8.GetBytes(stdout), stderr, false, TimeSpan.FromMilliseconds(5));

    /// <summary>A Meshroom-like installation: meshroom_batch, a few pipeline templates (one not allowed).</summary>
    public static string CreateInstall(string root, params string[] pipelines)
    {
        var install = Path.Combine(root, "Meshroom-2025.1.0");
        var templates = Path.Combine(install, "aliceVision", "share", "meshroom");
        Directory.CreateDirectory(templates);
        File.WriteAllText(Path.Combine(install, OperatingSystem.IsWindows() ? "meshroom_batch.exe" : "meshroom_batch"), "");
        foreach (var p in pipelines.Length > 0 ? pipelines : ["photogrammetry", "photogrammetryDraft", "photogrammetryObject", "cameraTracking"])
            File.WriteAllText(Path.Combine(templates, p + ".mg"), "{}");
        return install;
    }
}
