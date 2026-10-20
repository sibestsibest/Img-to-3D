namespace ImageTo3D.Core.Imaging;

public enum ResampleFilter
{
    /// <summary>Triangle filter widened on downscale; equals PIL BILINEAR and torch interpolate(bilinear, antialias=True).</summary>
    Bilinear,

    /// <summary>Lanczos-3; equals PIL LANCZOS.</summary>
    Lanczos,
}

/// <summary>
/// Separable convolution resampler over float planes using the same support and
/// centring rules as Pillow's ImagingResample, so results match the Python reference
/// preprocessing closely.
/// </summary>
public static class Resampler
{
    /// <summary>Resizes an interleaved float image with <paramref name="channels"/> channels.</summary>
    public static float[] Resize(float[] src, int srcWidth, int srcHeight, int channels, int dstWidth, int dstHeight, ResampleFilter filter)
    {
        if (src.Length != srcWidth * srcHeight * channels)
            throw new ArgumentException("Source size mismatch.", nameof(src));

        var horizontal = ComputeWeights(srcWidth, dstWidth, filter);
        var tmp = new float[dstWidth * srcHeight * channels];
        for (var y = 0; y < srcHeight; y++)
        {
            for (var x = 0; x < dstWidth; x++)
            {
                var (start, weights) = horizontal[x];
                for (var c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += weights[k] * src[(y * srcWidth + start + k) * channels + c];
                    tmp[(y * dstWidth + x) * channels + c] = (float)sum;
                }
            }
        }

        var vertical = ComputeWeights(srcHeight, dstHeight, filter);
        var dst = new float[dstWidth * dstHeight * channels];
        for (var y = 0; y < dstHeight; y++)
        {
            var (start, weights) = vertical[y];
            for (var x = 0; x < dstWidth; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += weights[k] * tmp[((start + k) * dstWidth + x) * channels + c];
                    dst[(y * dstWidth + x) * channels + c] = (float)sum;
                }
            }
        }

        return dst;
    }

    private static (int Start, double[] Weights)[] ComputeWeights(int inSize, int outSize, ResampleFilter filter)
    {
        var support = filter == ResampleFilter.Bilinear ? 1.0 : 3.0;
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var radius = support * filterScale;
        var result = new (int, double[])[outSize];

        for (var i = 0; i < outSize; i++)
        {
            var center = (i + 0.5) * scale;
            var min = Math.Max((int)Math.Floor(center - radius + 0.5), 0);
            var max = Math.Min((int)Math.Floor(center + radius + 0.5), inSize);
            var weights = new double[Math.Max(max - min, 1)];
            double total = 0;
            for (var j = 0; j < max - min; j++)
            {
                var w = Kernel((j + min - center + 0.5) / filterScale, filter);
                weights[j] = w;
                total += w;
            }

            if (total != 0)
            {
                for (var j = 0; j < weights.Length; j++)
                    weights[j] /= total;
            }
            else
            {
                weights[0] = 1;
            }

            result[i] = (min, weights);
        }

        return result;
    }

    private static double Kernel(double x, ResampleFilter filter)
    {
        x = Math.Abs(x);
        if (filter == ResampleFilter.Bilinear)
            return x < 1 ? 1 - x : 0;

        if (x >= 3)
            return 0;
        return Sinc(x) * Sinc(x / 3);
    }

    private static double Sinc(double x)
    {
        if (x == 0)
            return 1;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }
}
