using SkiaSharp;

namespace ImageTo3D.Core.Imaging;

/// <param name="Width">Stored width in pixels (before EXIF orientation).</param>
/// <param name="HasExif">JPEG with an EXIF block (camera and focal length for photogrammetry); always false for PNG.</param>
/// <param name="Sharpness">Variance of the Laplacian of the luminance at <see cref="PhotoInspector.AnalysisSide"/> px: higher is sharper.</param>
public sealed record PhotoFacts(ImageFormatKind Format, int Width, int Height, bool HasExif, ulong DHash, double Sharpness);

/// <summary>
/// Facts about one photo of a photogrammetry set, from a reduced decode (the JPEG decoder scales
/// while decoding), so a 50-megapixel photo never needs its full pixel buffer. The file itself is
/// kept unchanged: re-encoding would drop the EXIF data the reconstruction uses.
/// </summary>
public static class PhotoInspector
{
    /// <summary>Longest side the photo is analysed at, so sharpness values are comparable between cameras.</summary>
    public const int AnalysisSide = 1024;

    public static PhotoFacts Inspect(byte[] data, ImageLimits limits) => Inspect(data, limits, out _);

    /// <param name="preview">The photo at <see cref="AnalysisSide"/> px (for thumbnails); not EXIF-rotated.</param>
    public static PhotoFacts Inspect(byte[] data, ImageLimits limits, out RgbaImage preview)
    {
        var format = ImageDecoder.DetectFormat(data);
        if (format is not (ImageFormatKind.Jpeg or ImageFormatKind.Png))
            throw new ImageValidationException("Photogrammetry needs JPEG or PNG photos.");

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new ImageValidationException("The file could not be decoded as an image.");
        var expected = format == ImageFormatKind.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        if (codec.EncodedFormat != expected)
            throw new ImageValidationException("Image content does not match its file signature.");

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width < limits.MinDimension || height < limits.MinDimension)
            throw new ImageValidationException($"Photo is too small. Minimum size is {limits.MinDimension}x{limits.MinDimension} pixels.");
        if (width > limits.MaxDimension || height > limits.MaxDimension || (long)width * height > limits.MaxPixels)
            throw new ImageValidationException($"Photo is too large. Maximum size is {limits.MaxDimension} pixels per side and {limits.MaxPixels:N0} pixels in total.");

        var scale = Math.Min(1f, AnalysisSide / (float)Math.Max(width, height));
        var size = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new ImageValidationException(result == SKCodecResult.IncompleteInput ? "The photo file is truncated." : "The photo data is corrupt.");

        // The codec only scales by fixed factors; bring the analysis image to AnalysisSide exactly.
        using var analysis = Math.Max(size.Width, size.Height) > AnalysisSide
            ? bitmap.Resize(Fit(size.Width, size.Height), new SKSamplingOptions(SKFilterMode.Linear))
            : bitmap.Copy();
        var image = new RgbaImage(analysis.Width, analysis.Height, analysis.GetPixelSpan().ToArray());
        preview = image;
        return new PhotoFacts(format.Value, width, height, format == ImageFormatKind.Jpeg && HasExifBlock(data), ImageHash.DifferenceHash(image), LaplacianVariance(image));
    }

    private static SKImageInfo Fit(int w, int h)
    {
        var f = AnalysisSide / (double)Math.Max(w, h);
        return new SKImageInfo(Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)), SKColorType.Rgba8888, SKAlphaType.Unpremul);
    }

    /// <summary>Variance of the 4-neighbour Laplacian of the luminance (0..255): a standard focus measure.</summary>
    public static double LaplacianVariance(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        if (w < 3 || h < 3)
            return 0;
        var gray = new double[w * h];
        var p = image.Pixels;
        for (var i = 0; i < w * h; i++)
            gray[i] = 0.299 * p[i * 4] + 0.587 * p[i * 4 + 1] + 0.114 * p[i * 4 + 2];

        double sum = 0, sumSq = 0;
        var n = 0;
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var lap = gray[i - 1] + gray[i + 1] + gray[i - w] + gray[i + w] - 4 * gray[i];
                sum += lap;
                sumSq += lap * lap;
                n++;
            }
        }

        var mean = sum / n;
        return Math.Max(0, sumSq / n - mean * mean);
    }

    /// <summary>Walks the JPEG markers before the image data looking for an APP1 "Exif" segment.</summary>
    public static bool HasExifBlock(ReadOnlySpan<byte> jpeg)
    {
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker is 0xDA or 0xD9)
                return false; // start of scan / end of image: no more metadata
            var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (length < 2)
                return false;
            if (marker == 0xE1 && pos + 10 <= jpeg.Length && jpeg.Slice(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                return true;
            pos += 2 + length;
        }

        return false;
    }
}
