using System.Diagnostics;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.Imaging;
using ImageTo3D.Inference.Configuration;
using ImageTo3D.Inference.Models;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Inference.Pipeline;

public enum PipelineStage
{
    Queued,
    LoadingModels,
    PreparingImage,
    RemovingBackground,
    EncodingImage,
    EvaluatingDensity,
    ExtractingSurface,
    ColoringVertices,
    Exporting,
}

/// <summary>Progress report. <see cref="Fraction"/> is only set where it is actually measured (density grid chunks).</summary>
public sealed record StageProgress(PipelineStage Stage, double? Fraction = null);

public sealed record GenerationRequest(byte[] ImageBytes, int? Resolution = null, float? ForegroundRatio = null, bool RemoveBackground = true);

public sealed record DensityStats(float Min, float Max, float Mean, double FractionAboveThreshold, float Threshold);

public sealed record MeshStats(int Vertices, int Triangles, double SizeX, double SizeY, double SizeZ);

public sealed record GenerationResult(byte[] Glb, byte[] PreprocessedPng, MeshStats Mesh, DensityStats Density, IReadOnlyDictionary<string, double> StageSeconds);

public sealed class GenerationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// image -> background removal -> re-framing -> TripoSR encoder -> density grid via NeRF
/// decoder -> marching cubes -> per-vertex colour -> GLB. Mirrors run.py / gradio_app.py.
/// </summary>
public sealed class GenerationPipeline(ModelHost models, IOptions<InferenceOptions> options, ILogger<GenerationPipeline> logger)
{
    /// <summary>
    /// TripoSR world space is Z-up with the conditioning camera on +X (azimuth 0 in
    /// get_spherical_cameras). glTF/three.js is Y-up with the default camera on +Z, so map
    /// (x, y, z) -> (y, z, x): up becomes +Y and the photographed side faces +Z.
    /// (tsr.utils.to_gradio_3d_orientation differs by a 180 degree yaw because Gradio's
    /// babylon.js viewer looks from -Z.)
    /// </summary>
    private static readonly double[] ViewerOrientation = [0, 1, 0, 0, 0, 1, 1, 0, 0];

    public async Task<GenerationResult> RunAsync(GenerationRequest request, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var resolution = request.Resolution ?? o.MarchingCubesResolution;
        var ratio = request.ForegroundRatio ?? o.ForegroundRatio;
        if (resolution is < 32 or > 320)
            throw new GenerationException("Resolution must be between 32 and 320.");
        if (ratio is < 0.5f or > 1f)
            throw new GenerationException("Foreground ratio must be between 0.5 and 1.0.");

        var timings = new Dictionary<string, double>();
        var stopwatch = Stopwatch.StartNew();
        void Mark(string name)
        {
            timings[name] = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
        }

        progress.Report(new StageProgress(PipelineStage.LoadingModels));
        await models.EnsureLoadedAsync(cancellationToken);
        Mark("loadModels");

        return await Task.Run(
            () =>
            {
                progress.Report(new StageProgress(PipelineStage.PreparingImage));
                RgbaImage image;
                try
                {
                    image = ImageDecoder.Decode(request.ImageBytes).Image;
                }
                catch (ImageValidationException ex)
                {
                    throw new GenerationException(ex.Message, ex);
                }

                // Official remove_background skips images that already carry transparency.
                if (request.RemoveBackground && !image.HasTransparency())
                {
                    progress.Report(new StageProgress(PipelineStage.RemovingBackground));
                    image = models.BackgroundRemover.RemoveBackground(image, cancellationToken);
                    Mark("removeBackground");
                }

                RgbaImage prepared;
                try
                {
                    prepared = ImagePreprocessor.FillBackground(ImagePreprocessor.ResizeForeground(image, ratio));
                }
                catch (PreprocessingException ex)
                {
                    throw new GenerationException(ex.Message, ex);
                }

                var preprocessedPng = ImageDecoder.EncodePng(prepared);
                var tensor = ImagePreprocessor.ToChwTensor(prepared, TripoSrModel.ImageSize);
                Mark("preprocess");
                cancellationToken.ThrowIfCancellationRequested();

                progress.Report(new StageProgress(PipelineStage.EncodingImage));
                using var triplane = models.TripoSr.Encode(tensor, cancellationToken);
                Mark("encode");

                var density = EvaluateDensityGrid(triplane, resolution, o.ChunkSize, progress, cancellationToken);
                var stats = ComputeStats(density, o.DensityThreshold);
                logger.LogInformation(
                    "Density grid {Res}^3: min {Min:F3} max {Max:F3} mean {Mean:F3}, {Fraction:P2} above threshold {Threshold}",
                    resolution, stats.Min, stats.Max, stats.Mean, stats.FractionAboveThreshold, stats.Threshold);
                Mark("density");

                progress.Report(new StageProgress(PipelineStage.ExtractingSurface));
                var mesh = MarchingCubes.Extract(density, resolution, resolution, resolution, o.DensityThreshold, cancellationToken);
                if (mesh.TriangleCount == 0)
                    throw new GenerationException("The model did not produce a surface for this image. Try a clearer photo of a single object on a plain background.");

                mesh = GridToWorld(mesh, resolution);
                if (o.KeepLargestComponentOnly)
                    mesh = MeshOperations.KeepLargestComponent(mesh);
                Mark("marchingCubes");

                progress.Report(new StageProgress(PipelineStage.ColoringVertices));
                mesh = mesh.WithColors(QueryVertexColors(triplane, mesh, o.ChunkSize, cancellationToken));
                Mark("colors");

                progress.Report(new StageProgress(PipelineStage.Exporting));
                mesh = MeshOperations.Transform(mesh, ViewerOrientation);
                var validation = MeshValidator.Validate(mesh);
                if (!validation.IsValid)
                    throw new GenerationException("Generated mesh failed validation: " + string.Join(" ", validation.Errors));

                var glb = GlbWriter.Write(mesh, "TripoSR output in model units (not metres); Y-up.");
                Mark("export");

                var size = validation.Bounds.Size;
                return new GenerationResult(
                    glb,
                    preprocessedPng,
                    new MeshStats(mesh.VertexCount, mesh.TriangleCount, size.X, size.Y, size.Z),
                    stats,
                    timings);
            },
            cancellationToken);
    }

    private float[] EvaluateDensityGrid(Triplane triplane, int res, int chunkSize, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var total = res * res * res;
        var density = new float[total];
        var xyz = new float[Math.Min(chunkSize, total) * 3];
        var step = 2f * TripoSrModel.Radius / (res - 1);
        var resSq = res * res;

        progress.Report(new StageProgress(PipelineStage.EvaluatingDensity, 0));
        for (var start = 0; start < total; start += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, total - start);
            for (var i = 0; i < count; i++)
            {
                // np.linspace(-R, R, res) over a meshgrid with indexing="ij" (x slowest).
                var l = start + i;
                var x = l / resSq;
                var y = l / res % res;
                var z = l % res;
                xyz[i * 3] = -TripoSrModel.Radius + x * step;
                xyz[i * 3 + 1] = -TripoSrModel.Radius + y * step;
                xyz[i * 3 + 2] = -TripoSrModel.Radius + z * step;
            }

            models.TripoSr.Query(triplane, xyz, count, density.AsSpan(start, count), Span<float>.Empty, cancellationToken);
            progress.Report(new StageProgress(PipelineStage.EvaluatingDensity, (double)(start + count) / total));
        }

        return density;
    }

    private byte[] QueryVertexColors(Triplane triplane, Mesh mesh, int chunkSize, CancellationToken cancellationToken)
    {
        var colors = new byte[mesh.Positions.Length];
        var xyz = new float[Math.Min(chunkSize, mesh.VertexCount) * 3];
        var rgb = new float[xyz.Length];
        for (var start = 0; start < mesh.VertexCount; start += chunkSize)
        {
            var count = Math.Min(chunkSize, mesh.VertexCount - start);
            Array.Copy(mesh.Positions, start * 3, xyz, 0, count * 3);
            models.TripoSr.Query(triplane, xyz, count, Span<float>.Empty, rgb.AsSpan(0, count * 3), cancellationToken);
            for (var i = 0; i < count * 3; i++)
                colors[start * 3 + i] = (byte)Math.Clamp(MathF.Round(rgb[i] * 255f), 0, 255);
        }

        return colors;
    }

    /// <summary>Maps marching-cubes grid indices to TripoSR world coordinates in [-radius, radius].</summary>
    private static Mesh GridToWorld(Mesh mesh, int res)
    {
        var scale = 2.0 * TripoSrModel.Radius / (res - 1);
        return MeshOperations.Transform(mesh, [scale, 0, 0, 0, scale, 0, 0, 0, scale], new Vector3D(-TripoSrModel.Radius, -TripoSrModel.Radius, -TripoSrModel.Radius));
    }

    private static DensityStats ComputeStats(float[] density, float threshold)
    {
        float min = float.MaxValue, max = float.MinValue;
        double sum = 0;
        long above = 0;
        foreach (var d in density)
        {
            min = Math.Min(min, d);
            max = Math.Max(max, d);
            sum += d;
            if (d > threshold)
                above++;
        }

        return new DensityStats(min, max, (float)(sum / density.Length), (double)above / density.Length, threshold);
    }
}
