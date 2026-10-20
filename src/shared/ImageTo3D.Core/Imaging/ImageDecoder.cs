using SkiaSharp;

namespace ImageTo3D.Core.Imaging;

public enum ImageFormatKind
{
    Png,
    Jpeg,
    WebP,
}

public sealed class ImageValidationException(string message) : Exception(message);

public sealed record ImageLimits(int MinDimension = 64, int MaxDimension = 8192, long MaxPixels = 40_000_000);

public sealed record DecodedImage(RgbaImage Image, ImageFormatKind Format, int OriginalWidth, int OriginalHeight);

/// <summary>
/// Validates and decodes uploaded images. The format is detected from the file signature
/// (never the file name or client-supplied content type), dimensions are checked before
/// pixels are allocated, and EXIF orientation is applied so the result is upright.
/// </summary>
public static class ImageDecoder
{
    public static ImageFormatKind? DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ImageFormatKind.Png;
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ImageFormatKind.Jpeg;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return ImageFormatKind.WebP;
        return null;
    }

    public static DecodedImage Decode(byte[] data, ImageLimits? limits = null)
    {
        limits ??= new ImageLimits();

        var format = DetectFormat(data) ?? throw new ImageValidationException("Unsupported image format. Upload a PNG, JPEG or WebP image.");

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new ImageValidationException("The file could not be decoded as an image.");

        var expected = format switch
        {
            ImageFormatKind.Png => SKEncodedImageFormat.Png,
            ImageFormatKind.Jpeg => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Webp,
        };
        if (codec.EncodedFormat != expected)
            throw new ImageValidationException("Image content does not match its file signature.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width < limits.MinDimension || height < limits.MinDimension)
            throw new ImageValidationException($"Image is too small. Minimum size is {limits.MinDimension}x{limits.MinDimension} pixels.");
        if (width > limits.MaxDimension || height > limits.MaxDimension || (long)width * height > limits.MaxPixels)
            throw new ImageValidationException($"Image is too large. Maximum size is {limits.MaxDimension} pixels per side and {limits.MaxPixels:N0} pixels in total.");

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var raw = new SKBitmap(info);
        var result = codec.GetPixels(info, raw.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new ImageValidationException("The image data is corrupt or incomplete.");
        if (result == SKCodecResult.IncompleteInput)
            throw new ImageValidationException("The image file is truncated.");

        using var oriented = ApplyOrigin(raw, codec.EncodedOrigin);
        return new DecodedImage(ToRgba(oriented), format, width, height);
    }

    public static byte[] EncodePng(RgbaImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed.");
        return encoded.ToArray();
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return src.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;

        // Pixel-exact remap (no filtering) so orientation never blurs the image.
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var srcPixels = src.GetPixelSpan();
        var dstPixels = new byte[w * h * 4];
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var (dx, dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (src.Width - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (src.Width - 1 - x, src.Height - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, src.Height - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (src.Height - 1 - y, x),
                    SKEncodedOrigin.RightBottom => (src.Height - 1 - y, src.Width - 1 - x),
                    SKEncodedOrigin.LeftBottom => (y, src.Width - 1 - x),
                    _ => (x, y),
                };
                srcPixels.Slice((y * src.Width + x) * 4, 4).CopyTo(dstPixels.AsSpan((dy * w + dx) * 4, 4));
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(dstPixels, 0, dst.GetPixels(), dstPixels.Length);
        return dst;
    }

    private static RgbaImage ToRgba(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan().ToArray();
        return new RgbaImage(bitmap.Width, bitmap.Height, pixels);
    }
}
