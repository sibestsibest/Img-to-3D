using System.Numerics;
using SkiaSharp;

namespace ImageTo3D.Core.Imaging;

/// <summary>
/// Perceptual fingerprint for duplicate detection. The difference hash (dHash) shrinks the image
/// to 9 x 8 grey levels and records whether each pixel is brighter than its right neighbour, so
/// re-encoding, resizing and small colour shifts change few of the 64 bits while different
/// pictures differ in about half of them. Transparent pixels are composited onto white first,
/// matching how the thumbnails are shown.
/// </summary>
public static class ImageHash
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;

    /// <summary>Grey levels (of 255) a cell must exceed its right neighbour by to set its bit.</summary>
    private const double FlatMargin = 2.0;

    public static ulong DifferenceHash(RgbaImage image)
    {
        var grey = BoxShrink(Luminance(image), image.Width, image.Height, HashWidth, HashHeight);
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < HashHeight; y++)
        {
            for (var x = 0; x < HashWidth - 1; x++)
            {
                // A small margin keeps flat areas (equal neighbours) at 0 instead of letting
                // compression noise decide the bit.
                if (grey[y * HashWidth + x] > grey[y * HashWidth + x + 1] + FlatMargin)
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Rec. 601 luma of each pixel after compositing onto white.</summary>
    private static double[] Luminance(RgbaImage image)
    {
        var p = image.Pixels;
        var luma = new double[image.Width * image.Height];
        for (var i = 0; i < luma.Length; i++)
        {
            var a = p[i * 4 + 3] / 255.0;
            double Channel(int c) => p[i * 4 + c] * a + 255 * (1 - a);
            luma[i] = 0.299 * Channel(0) + 0.587 * Channel(1) + 0.114 * Channel(2);
        }

        return luma;
    }

    /// <summary>Area average: every source pixel contributes to the cells it overlaps, by overlap.</summary>
    private static double[] BoxShrink(double[] src, int width, int height, int dstWidth, int dstHeight)
    {
        var dst = new double[dstWidth * dstHeight];
        var sx = (double)width / dstWidth;
        var sy = (double)height / dstHeight;
        for (var dy = 0; dy < dstHeight; dy++)
        {
            var y0 = dy * sy;
            var y1 = y0 + sy;
            for (var dx = 0; dx < dstWidth; dx++)
            {
                var x0 = dx * sx;
                var x1 = x0 + sx;
                double sum = 0, weight = 0;
                for (var y = (int)y0; y < Math.Min(height, (int)Math.Ceiling(y1)); y++)
                {
                    var wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    for (var x = (int)x0; x < Math.Min(width, (int)Math.Ceiling(x1)); x++)
                    {
                        var w = wy * (Math.Min(x + 1, x1) - Math.Max(x, x0));
                        sum += src[y * width + x] * w;
                        weight += w;
                    }
                }

                dst[dy * dstWidth + dx] = weight > 0 ? sum / weight : 0;
            }
        }

        return dst;
    }
}

/// <summary>Small JPEG previews of reference images (never larger than the source, transparency flattened onto white).</summary>
public static class ImageThumbnail
{
    public sealed record Result(byte[] Jpeg, int Width, int Height);

    public static Result Create(RgbaImage image, int maxSide, int quality = 85)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var source = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, source.GetPixels(), image.Pixels.Length);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Could not allocate the thumbnail surface.");
        surface.Canvas.Clear(SKColors.White);
        using (var sourceImage = SKImage.FromBitmap(source))
            surface.Canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)); // mipmaps avoid aliasing on large reductions
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new InvalidOperationException("JPEG encoding failed.");
        return new Result(encoded.ToArray(), width, height);
    }
}
