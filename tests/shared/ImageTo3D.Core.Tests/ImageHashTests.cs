using ImageTo3D.Core.Imaging;
using SkiaSharp;

namespace ImageTo3D.Core.Tests;

public class ImageHashTests
{
    private static RgbaImage Gradient(int w, int h, bool leftToRight)
    {
        var image = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = (byte)(255 * (leftToRight ? x : w - 1 - x) / (w - 1));
                var i = (y * w + x) * 4;
                image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = v;
                image.Pixels[i + 3] = 255;
            }
        }

        return image;
    }

    private static RgbaImage Blocks(int seed, int w = 200, int h = 150)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            for (var i = 0; i < 12; i++)
            {
                using var paint = new SKPaint { Color = new SKColor((byte)random.Next(200), (byte)random.Next(200), (byte)random.Next(200)) };
                canvas.DrawRect(random.Next(0, w - 20), random.Next(0, h - 20), random.Next(20, w / 2), random.Next(20, h / 2), paint);
            }
        }

        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return ImageDecoder.Decode(png.ToArray()).Image;
    }

    private static RgbaImage Reencode(RgbaImage image, int width, int height, int jpegQuality)
    {
        var png = ImageDecoder.EncodePng(image);
        using var bitmap = SKBitmap.Decode(png);
        using var resized = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var jpeg = resized.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        return ImageDecoder.Decode(jpeg.ToArray(), new ImageLimits(16)).Image;
    }

    [Fact]
    public void DifferenceHash_GradientDirectionSetsEveryBitOrNone()
    {
        // Each row compares neighbours left to right: brighter-left sets the bit.
        Assert.Equal(0UL, ImageHash.DifferenceHash(Gradient(90, 80, leftToRight: true)));
        Assert.Equal(ulong.MaxValue, ImageHash.DifferenceHash(Gradient(90, 80, leftToRight: false)));
        Assert.Equal(64, ImageHash.Distance(0, ulong.MaxValue));
    }

    [Fact]
    public void DifferenceHash_SurvivesResizingAndJpegButSeparatesDifferentPictures()
    {
        var original = Blocks(1);
        var hash = ImageHash.DifferenceHash(original);

        Assert.Equal(hash, ImageHash.DifferenceHash(original));
        Assert.InRange(ImageHash.Distance(hash, ImageHash.DifferenceHash(Reencode(original, 120, 90, 70))), 0, 4);
        Assert.InRange(ImageHash.Distance(hash, ImageHash.DifferenceHash(Reencode(original, 400, 300, 85))), 0, 4);

        for (var seed = 2; seed < 8; seed++)
            Assert.True(ImageHash.Distance(hash, ImageHash.DifferenceHash(Blocks(seed))) > 10, $"seed {seed} too close");
    }

    [Fact]
    public void DifferenceHash_TreatsTransparencyAsWhite()
    {
        var transparent = Gradient(90, 80, leftToRight: false);
        var white = new RgbaImage(90, 80);
        Array.Fill(white.Pixels, (byte)255);
        for (var i = 3; i < transparent.Pixels.Length; i += 4)
            transparent.Pixels[i] = 0;

        Assert.Equal(ImageHash.DifferenceHash(white), ImageHash.DifferenceHash(transparent));
    }

    [Fact]
    public void Thumbnail_FitsTheLongestSideAndNeverEnlarges()
    {
        var wide = ImageThumbnail.Create(Blocks(3, 1000, 400), 640);
        Assert.Equal((640, 256), (wide.Width, wide.Height));
        using (var decoded = SKBitmap.Decode(wide.Jpeg))
            Assert.Equal((640, 256), (decoded.Width, decoded.Height));
        Assert.Equal(ImageFormatKind.Jpeg, ImageDecoder.DetectFormat(wide.Jpeg));

        var small = ImageThumbnail.Create(Blocks(3, 120, 300), 640);
        Assert.Equal((120, 300), (small.Width, small.Height));
    }
}
