using ImageTo3D.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace ImageTo3D.Inference.Models;

/// <summary>
/// U²-Net salient-object segmentation, reproducing rembg's default "u2net" session:
/// LANCZOS resize to 320x320, divide by the image maximum, ImageNet mean/std, min-max
/// normalise the first output, LANCZOS resize the mask back, then a "naive cutout"
/// (PIL composite onto transparent black, which scales RGB and alpha by the mask).
/// </summary>
public sealed class BackgroundRemover(InferenceSession session) : IDisposable
{
    private const int Size = 320;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly string _inputName = session.InputMetadata.Keys.First();
    private readonly string _outputName = session.OutputMetadata.Keys.First();

    public RgbaImage RemoveBackground(RgbaImage image, CancellationToken cancellationToken)
    {
        var w = image.Width;
        var h = image.Height;

        var rgb = new float[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = image.Pixels[i * 4];
            rgb[i * 3 + 1] = image.Pixels[i * 4 + 1];
            rgb[i * 3 + 2] = image.Pixels[i * 4 + 2];
        }

        var resized = Resampler.Resize(rgb, w, h, 3, Size, Size, ResampleFilter.Lanczos);
        var max = 1e-6f;
        for (var i = 0; i < resized.Length; i++)
        {
            resized[i] = ToByte(resized[i]); // PIL resize output is uint8
            max = Math.Max(max, resized[i]);
        }

        var input = new float[3 * Size * Size];
        for (var i = 0; i < Size * Size; i++)
            for (var c = 0; c < 3; c++)
                input[c * Size * Size + i] = (resized[i * 3 + c] / max - Mean[c]) / Std[c];

        float[] prediction;
        using (var runOptions = new RunOptions())
        using (OnnxSessionFactory.LinkTermination(runOptions, cancellationToken))
        using (var inputValue = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]))
        {
            using var outputs = session.Run(runOptions, [_inputName], [inputValue], [_outputName]);
            cancellationToken.ThrowIfCancellationRequested();
            prediction = outputs[0].GetTensorDataAsSpan<float>()[..(Size * Size)].ToArray();
        }

        var min = prediction.Min();
        var range = prediction.Max() - min;
        var mask8 = new float[Size * Size];
        for (var i = 0; i < mask8.Length; i++)
            mask8[i] = MathF.Floor(range > 0 ? (prediction[i] - min) / range * 255f : 0f); // astype(uint8) truncates

        var mask = Resampler.Resize(mask8, Size, Size, 1, w, h, ResampleFilter.Lanczos);

        var output = new RgbaImage(w, h);
        for (var i = 0; i < w * h; i++)
        {
            var m = ToByte(mask[i]) / 255f;
            output.Pixels[i * 4] = ToByte(image.Pixels[i * 4] * m);
            output.Pixels[i * 4 + 1] = ToByte(image.Pixels[i * 4 + 1] * m);
            output.Pixels[i * 4 + 2] = ToByte(image.Pixels[i * 4 + 2] * m);
            output.Pixels[i * 4 + 3] = ToByte(255f * m);
        }

        return output;
    }

    public void Dispose() => session.Dispose();

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v), 0, 255);
}
