using Microsoft.ML.OnnxRuntime;

namespace ImageTo3D.Inference.Models;

/// <summary>
/// TripoSR split into two ONNX graphs (community export of stabilityai/TripoSR, see models/README.md):
/// triplane.onnx: image[B,3,512,512] in [0,1] -> triplane[B,3,C,64,64]
/// nerf.onnx: (triplane[1,3,C,64,64], xyz[K,3] in [-radius, radius]) -> (density[K] activated, color[K,3])
/// </summary>
public sealed class TripoSrModel : IDisposable
{
    public const int ImageSize = 512;

    /// <summary>Renderer radius from the TripoSR config (renderer.radius).</summary>
    public const float Radius = 0.87f;

    private readonly InferenceSession _triplane;
    private readonly InferenceSession _nerf;

    public TripoSrModel(InferenceSession triplane, InferenceSession nerf)
    {
        _triplane = triplane;
        _nerf = nerf;
        RequireNames(triplane.InputMetadata.Keys, "image");
        RequireNames(triplane.OutputMetadata.Keys, "triplane");
        RequireNames(nerf.InputMetadata.Keys, "triplane", "xyz");
        RequireNames(nerf.OutputMetadata.Keys, "density", "color");
    }

    /// <summary>Runs the image encoder. <paramref name="chwImage"/> is [3,512,512] RGB in [0,1].</summary>
    public Triplane Encode(float[] chwImage, CancellationToken cancellationToken)
    {
        if (chwImage.Length != 3 * ImageSize * ImageSize)
            throw new ArgumentException("Image tensor must be 3x512x512.", nameof(chwImage));

        using var runOptions = new RunOptions();
        using var _ = OnnxSessionFactory.LinkTermination(runOptions, cancellationToken);
        using var input = OrtValue.CreateTensorValueFromMemory(chwImage, [1, 3, ImageSize, ImageSize]);
        using var outputs = _triplane.Run(runOptions, ["image"], [input], ["triplane"]);
        cancellationToken.ThrowIfCancellationRequested();

        var shape = outputs[0].GetTensorTypeAndShape().Shape;
        if (shape.Length != 5 || shape[0] != 1 || shape[1] != 3)
            throw new InvalidOperationException($"Unexpected triplane shape [{string.Join(",", shape)}].");
        return new Triplane(outputs[0].GetTensorDataAsSpan<float>().ToArray(), shape);
    }

    /// <summary>Queries density (and optionally colour) at <paramref name="pointCount"/> xyz points.</summary>
    public void Query(Triplane triplane, float[] xyz, int pointCount, Span<float> density, Span<float> color, CancellationToken cancellationToken)
    {
        var wantDensity = !density.IsEmpty;
        var wantColor = !color.IsEmpty;
        string[] outputNames = wantDensity && wantColor ? ["density", "color"] : wantDensity ? ["density"] : ["color"];

        using var runOptions = new RunOptions();
        using var _ = OnnxSessionFactory.LinkTermination(runOptions, cancellationToken);
        using var xyzValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, xyz.AsMemory(0, pointCount * 3), [pointCount, 3]);
        using var outputs = _nerf.Run(runOptions, ["triplane", "xyz"], [triplane.Value, xyzValue], outputNames);
        cancellationToken.ThrowIfCancellationRequested();

        var index = 0;
        if (wantDensity)
        {
            var d = outputs[index++].GetTensorDataAsSpan<float>();
            if (d.Length != pointCount)
                throw new InvalidOperationException($"Density output has {d.Length} values for {pointCount} points.");
            d.CopyTo(density);
        }

        if (wantColor)
        {
            var c = outputs[index].GetTensorDataAsSpan<float>();
            if (c.Length != pointCount * 3)
                throw new InvalidOperationException($"Color output has {c.Length} values for {pointCount} points; expected RGB.");
            c.CopyTo(color);
        }
    }

    public void Dispose()
    {
        _triplane.Dispose();
        _nerf.Dispose();
    }

    private static void RequireNames(IEnumerable<string> actual, params string[] expected)
    {
        var missing = expected.Except(actual).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"ONNX model is missing expected tensors: {string.Join(", ", missing)}. Is this the brodatech/triposr-onnx export?");
    }
}

/// <summary>Triplane scene code kept as a reusable ORT tensor for repeated NeRF queries.</summary>
public sealed class Triplane : IDisposable
{
    private readonly float[] _data;

    public Triplane(float[] data, long[] shape)
    {
        _data = data;
        Shape = shape;
        Value = OrtValue.CreateTensorValueFromMemory(_data, shape);
    }

    public long[] Shape { get; }

    public OrtValue Value { get; }

    public void Dispose() => Value.Dispose();
}
