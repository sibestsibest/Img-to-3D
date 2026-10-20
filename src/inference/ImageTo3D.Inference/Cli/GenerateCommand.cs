using ImageTo3D.Core.Geometry;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Models;
using ImageTo3D.Inference.Pipeline;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Inference.Cli;

/// <summary>
/// Offline generation without the HTTP layer:
/// dotnet run --project src/inference/ImageTo3D.Inference -- generate &lt;image&gt; &lt;outputDir&gt; [resolution]
/// </summary>
public static class GenerateCommand
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        // Positional arguments only; "--Section:Key=value" pairs are configuration overrides.
        args = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: generate <image> <outputDir> [resolution] [--Inference:Key=value ...]");
            return 2;
        }

        var imagePath = Path.GetFullPath(args[1]);
        var outputDir = Path.GetFullPath(args[2]);
        int? resolution = args.Length > 3 ? int.Parse(args[3]) : null;
        Directory.CreateDirectory(outputDir);

        var pipeline = services.GetRequiredService<GenerationPipeline>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("generate");
        var progress = new Progress<StageProgress>(p =>
        {
            if (p.Fraction is null || p.Fraction is 0 or >= 1 || (int)(p.Fraction * 100) % 10 == 0)
                logger.LogInformation("Stage {Stage}{Fraction}", p.Stage, p.Fraction is { } f ? $" {f:P0}" : "");
        });

        var result = await pipeline.RunAsync(new GenerationRequest(await File.ReadAllBytesAsync(imagePath)), progress, CancellationToken.None);

        await File.WriteAllBytesAsync(Path.Combine(outputDir, "input.png"), result.PreprocessedPng);
        await File.WriteAllBytesAsync(Path.Combine(outputDir, "mesh.glb"), result.Glb);

        // Same conversion the API performs: Y-up model units -> Z-up STL, longest side 100 mm.
        var mesh = GlbReader.Read(result.Glb);
        var factor = 100.0 / MeshOperations.ComputeBounds(mesh).Size.MaxComponent;
        var stl = StlWriter.Write(MeshOperations.YUpToZUp(MeshOperations.Scale(MeshOperations.CenterOnGround(mesh), factor)));
        await File.WriteAllBytesAsync(Path.Combine(outputDir, "mesh_100mm.stl"), stl);

        foreach (var yaw in new[] { 0, 90, 45 })
        {
            var preview = MeshPreviewRenderer.Render(mesh, 512, yaw, yaw == 45 ? 20 : 0);
            await File.WriteAllBytesAsync(Path.Combine(outputDir, $"preview_{yaw}.png"), Core.Imaging.ImageDecoder.EncodePng(preview));
        }

        logger.LogInformation("Mesh: {Vertices} vertices, {Triangles} triangles, size {X:F3} x {Y:F3} x {Z:F3} model units",
            result.Mesh.Vertices, result.Mesh.Triangles, result.Mesh.SizeX, result.Mesh.SizeY, result.Mesh.SizeZ);
        logger.LogInformation("Density: {@Density}", result.Density);
        logger.LogInformation("Timings (s): {Timings}", string.Join(", ", result.StageSeconds.Select(kv => $"{kv.Key}={kv.Value:F1}")));
        logger.LogInformation("Wrote input.png, mesh.glb, mesh_100mm.stl to {Dir}", outputDir);
        return 0;
    }

    public static void AddPipelineServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InferenceOptions>().Bind(configuration.GetSection(InferenceOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ModelHost>();
        services.AddSingleton<GenerationPipeline>();
    }
}
