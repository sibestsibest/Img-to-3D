using ImageTo3D.Core.Imaging;
using SkiaSharp;

namespace ImageTo3D.Core.Tests;

public class ImagingTests
{
    [Fact]
    public void DetectFormat_UsesSignatureNotExtension()
    {
        Assert.Equal(ImageFormatKind.Png, ImageDecoder.DetectFormat(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.Equal(ImageFormatKind.Jpeg, ImageDecoder.DetectFormat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(ImageFormatKind.WebP, ImageDecoder.DetectFormat("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ImageDecoder.DetectFormat("GIF89a........"u8));
        Assert.Null(ImageDecoder.DetectFormat("glTF"u8));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Decode_AcceptsSupportedFormats(SKEncodedImageFormat format)
    {
        var data = Encode(120, 80, format);

        var decoded = ImageDecoder.Decode(data);

        Assert.Equal(120, decoded.Image.Width);
        Assert.Equal(80, decoded.Image.Height);
    }

    [Fact]
    public void Decode_RejectsUnsupportedFormat()
    {
        var gif = "GIF89a"u8.ToArray().Concat(new byte[100]).ToArray();
        var ex = Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(gif));
        Assert.Contains("Unsupported", ex.Message);
    }

    [Fact]
    public void Decode_RejectsCorruptPayloadWithValidSignature()
    {
        var fake = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[200]).ToArray();
        Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(fake));
    }

    [Fact]
    public void Decode_RejectsTruncatedImage()
    {
        var png = Encode(300, 300, SKEncodedImageFormat.Png, noise: true);
        Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(png[..(png.Length / 2)]));
    }

    [Fact]
    public void Decode_EnforcesDimensionLimits()
    {
        var small = Encode(32, 32, SKEncodedImageFormat.Png);
        var large = Encode(300, 300, SKEncodedImageFormat.Png);

        Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(small));
        Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(large, new ImageLimits(MaxDimension: 256)));
        Assert.Throws<ImageValidationException>(() => ImageDecoder.Decode(large, new ImageLimits(MaxPixels: 50_000)));
    }

    [Fact]
    public void EncodePng_RoundTripsPixels()
    {
        var image = new RgbaImage(64, 64);
        image.Pixels[0] = 10;
        image.Pixels[1] = 20;
        image.Pixels[2] = 30;
        image.Pixels[3] = 128;

        var decoded = ImageDecoder.Decode(ImageDecoder.EncodePng(image));

        Assert.Equal(image.Pixels[..4], decoded.Image.Pixels[..4]);
    }

    [Fact]
    public void Resampler_ConstantImageStaysConstant()
    {
        var src = Enumerable.Repeat(0.25f, 37 * 23 * 3).ToArray();

        var dst = Resampler.Resize(src, 37, 23, 3, 512, 512, ResampleFilter.Bilinear);

        Assert.All(dst, v => Assert.Equal(0.25f, v, 5));
        Assert.All(Resampler.Resize(src, 37, 23, 3, 10, 7, ResampleFilter.Lanczos), v => Assert.Equal(0.25f, v, 4));
    }

    [Fact]
    public void Resampler_BilinearHalvingAveragesNeighbours()
    {
        // Antialiased 2x bilinear downscale (Pillow semantics): the triangle filter is widened to
        // radius 2, taps are clipped at the border and the remaining weights renormalised.
        float[] src = [0, 1, 0, 1];

        var dst = Resampler.Resize(src, 4, 1, 1, 2, 1, ResampleFilter.Bilinear);

        Assert.Equal(0.75f / 1.75f, dst[0], 4); // taps 0..2 with weights 0.75, 0.75, 0.25
        Assert.Equal(1.00f / 1.75f, dst[1], 4); // taps 1..3 with weights 0.25, 0.75, 0.75
    }

    private static byte[] Encode(int w, int h, SKEncodedImageFormat format, bool noise = false)
    {
        using var bitmap = new SKBitmap(w, h);
        var random = new Random(42);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bitmap.SetPixel(x, y, noise ? new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)) : new SKColor((byte)x, (byte)y, 100));
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }
}
