using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Inference.Pipeline;

/// <summary>
/// Ports of TripoSR's preprocessing (tsr/utils.py resize_foreground, fill_background and
/// ImagePreprocessor.convert_and_resize). Pure functions so they can be unit tested without models.
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>
    /// Crops to the alpha &gt; 0 bounding box, pads to a square, then pads again so the
    /// foreground spans <paramref name="ratio"/> of the side. Matches the official slicing,
    /// including its exclusive upper bound on the bounding box.
    /// </summary>
    public static RgbaImage ResizeForeground(RgbaImage image, float ratio)
    {
        if (ratio is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(ratio));

        int y1 = int.MaxValue, y2 = -1, x1 = int.MaxValue, x2 = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] > 0)
                {
                    y1 = Math.Min(y1, y);
                    y2 = Math.Max(y2, y);
                    x1 = Math.Min(x1, x);
                    x2 = Math.Max(x2, x);
                }
            }
        }

        if (y2 < 0 || y2 - y1 < 1 || x2 - x1 < 1)
            throw new PreprocessingException("No foreground object was detected in the image. Use a photo with a single, clearly visible object.");

        var fgW = x2 - x1;
        var fgH = y2 - y1;
        var size = Math.Max(fgW, fgH);
        var newSize = (int)(size / ratio);
        var offsetX = (size - fgW) / 2 + (newSize - size) / 2;
        var offsetY = (size - fgH) / 2 + (newSize - size) / 2;

        var output = new RgbaImage(newSize, newSize);
        for (var y = 0; y < fgH; y++)
        {
            Buffer.BlockCopy(
                image.Pixels,
                ((y1 + y) * image.Width + x1) * 4,
                output.Pixels,
                ((offsetY + y) * newSize + offsetX) * 4,
                fgW * 4);
        }

        return output;
    }

    /// <summary>Composites RGBA onto 50% grey, as in run.py / gradio_app.py.</summary>
    public static RgbaImage FillBackground(RgbaImage image)
    {
        var output = new RgbaImage(image.Width, image.Height);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var a = image.Pixels[i + 3] / 255f;
            for (var c = 0; c < 3; c++)
            {
                var v = image.Pixels[i + c] / 255f * a + (1 - a) * 0.5f;
                output.Pixels[i + c] = (byte)Math.Clamp((int)(v * 255f), 0, 255); // astype(uint8) truncates
            }

            output.Pixels[i + 3] = 255;
        }

        return output;
    }

    /// <summary>Converts to float [0,1], antialiased-bilinear resizes to size x size and returns CHW layout.</summary>
    public static float[] ToChwTensor(RgbaImage image, int size)
    {
        var hwc = new float[image.Width * image.Height * 3];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            hwc[i * 3] = image.Pixels[i * 4] / 255f;
            hwc[i * 3 + 1] = image.Pixels[i * 4 + 1] / 255f;
            hwc[i * 3 + 2] = image.Pixels[i * 4 + 2] / 255f;
        }

        var resized = Resampler.Resize(hwc, image.Width, image.Height, 3, size, size, ResampleFilter.Bilinear);
        var chw = new float[3 * size * size];
        for (var i = 0; i < size * size; i++)
            for (var c = 0; c < 3; c++)
                chw[c * size * size + i] = Math.Clamp(resized[i * 3 + c], 0f, 1f);
        return chw;
    }
}

public sealed class PreprocessingException(string message) : Exception(message);
