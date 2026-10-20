using ImageTo3D.Core.Geometry;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Models;
using ImageTo3D.Inference.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Inference.Tests;

/// <summary>Runs only when the ONNX models are present (set IMAGETO3D_MODEL_DIR or keep them in ./models).</summary>
public sealed class RequiresModelsFactAttribute : FactAttribute
{
    public RequiresModelsFactAttribute()
    {
        if (RealModelSmokeTests.FindModelDirectory() is null)
            Skip = "TripoSR ONNX models not found. Download them (models/README.md) or set IMAGETO3D_MODEL_DIR.";
    }
}

[Trait("Category", "RealModel")]
public class RealModelSmokeTests
{
    public static string? FindModelDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("IMAGETO3D_MODEL_DIR");
        if (!string.IsNullOrEmpty(fromEnv))
            return File.Exists(Path.Combine(fromEnv, "triposr", "triplane.onnx")) ? fromEnv : null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (File.Exists(Path.Combine(candidate, "triposr", "triplane.onnx")) && File.Exists(Path.Combine(candidate, "u2net", "u2net.onnx")))
                return candidate;
        }

        return null;
    }

    [RequiresModelsFact]
    public async Task GeneratesRealMeshFromSampleImage()
    {
        var modelDir = FindModelDirectory()!;
        var sample = Path.Combine(Path.GetDirectoryName(modelDir)!, "samples", "chair.png");
        Assert.True(File.Exists(sample), "samples/chair.png is required for the smoke test.");

        var options = Options.Create(new InferenceOptions { ModelDirectory = modelDir, MarchingCubesResolution = 128 });
        using var host = new ModelHost(options, new Env(modelDir), NullLogger<ModelHost>.Instance);
        var pipeline = new GenerationPipeline(host, options, NullLogger<GenerationPipeline>.Instance);
        var stages = new List<PipelineStage>();

        var result = await pipeline.RunAsync(new GenerationRequest(await File.ReadAllBytesAsync(sample)), new Progress<StageProgress>(p => stages.Add(p.Stage)), CancellationToken.None);

        var mesh = GlbReader.Read(result.Glb);
        var validation = MeshValidator.Validate(mesh);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.True(mesh.TriangleCount > 5_000, $"Expected a substantial mesh, got {mesh.TriangleCount} triangles.");
        Assert.NotNull(mesh.Colors);

        // A chair is taller than it is wide or deep; this also checks the Y-up orientation.
        var size = validation.Bounds.Size;
        Assert.True(size.Y > size.X && size.Y > size.Z, $"Unexpected proportions {size}.");
        Assert.InRange(size.MaxComponent, 0.3, 1.74); // inside the TripoSR radius-0.87 cube

        // Activated density: the official iso-level 25 must cut through the observed range.
        Assert.True(result.Density.Max > 25 && result.Density.Min < 25);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.PreprocessedPng[..4]);
        var stl = StlReader.ReadBinary(StlWriter.Write(mesh));
        Assert.True(MeshValidator.Validate(stl).IsValid);
    }

    [RequiresModelsFact]
    public async Task CancellationStopsARunningGeneration()
    {
        var modelDir = FindModelDirectory()!;
        var sample = Path.Combine(Path.GetDirectoryName(modelDir)!, "samples", "chair.png");
        var options = Options.Create(new InferenceOptions { ModelDirectory = modelDir });
        using var host = new ModelHost(options, new Env(modelDir), NullLogger<ModelHost>.Instance);
        await host.EnsureLoadedAsync(CancellationToken.None);
        var pipeline = new GenerationPipeline(host, options, NullLogger<GenerationPipeline>.Instance);
        using var cts = new CancellationTokenSource();

        var run = pipeline.RunAsync(
            new GenerationRequest(await File.ReadAllBytesAsync(sample)),
            new SyncProgress(p =>
            {
                if (p.Stage == PipelineStage.EncodingImage)
                    cts.CancelAfter(500);
            }),
            cts.Token);

        var ex = await Record.ExceptionAsync(() => run);
        Assert.True(ex is OperationCanceledException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException, $"Unexpected {ex?.GetType()}");
    }

    private sealed class SyncProgress(Action<StageProgress> handler) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => handler(value);
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "smoke";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
