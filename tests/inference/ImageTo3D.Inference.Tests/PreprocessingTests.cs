using ImageTo3D.Core.Imaging;
using ImageTo3D.Inference.Pipeline;

namespace ImageTo3D.Inference.Tests;

public class PreprocessingTests
{
    [Fact]
    public void ResizeForeground_CentresObjectAtRequestedRatio()
    {
        // 100x60 canvas with an opaque 20x10 block at (10, 5).
        var image = new RgbaImage(100, 60);
        Paint(image, 10, 5, 20, 10, 200);

        var result = ImagePreprocessor.ResizeForeground(image, 0.8f);

        // Official slicing keeps max-min pixels: fg = 19x9; square side 19; new side int(19 / 0.8) = 23.
        Assert.Equal(23, result.Width);
        Assert.Equal(23, result.Height);
        var (x1, y1, x2, y2) = OpaqueBounds(result);
        Assert.Equal(19, x2 - x1 + 1);
        Assert.Equal(9, y2 - y1 + 1);
        Assert.InRange(x1, 1, 3);   // horizontally centred
        Assert.InRange(y1, 6, 8);   // vertically centred within the square
    }

    [Fact]
    public void ResizeForeground_WithoutForeground_Throws()
    {
        Assert.Throws<PreprocessingException>(() => ImagePreprocessor.ResizeForeground(new RgbaImage(32, 32), 0.85f));
    }

    [Fact]
    public void FillBackground_CompositesOntoHalfGrey()
    {
        var image = new RgbaImage(2, 1);
        image.Pixels[0] = 255; // opaque red
        image.Pixels[3] = 255;
        // pixel 2 fully transparent

        var result = ImagePreprocessor.FillBackground(image);

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.Pixels[..4]);
        Assert.Equal(new byte[] { 127, 127, 127, 255 }, result.Pixels[4..]); // int(0.5 * 255)
    }

    [Fact]
    public void ToChwTensor_IsPlanarAndInUnitRange()
    {
        var image = new RgbaImage(4, 4);
        for (var i = 0; i < 16; i++)
        {
            image.Pixels[i * 4] = 255;
            image.Pixels[i * 4 + 1] = 0;
            image.Pixels[i * 4 + 2] = 51;
            image.Pixels[i * 4 + 3] = 255;
        }

        var tensor = ImagePreprocessor.ToChwTensor(image, 8);

        Assert.Equal(3 * 8 * 8, tensor.Length);
        Assert.All(tensor[..64], v => Assert.Equal(1f, v, 4));
        Assert.All(tensor[64..128], v => Assert.Equal(0f, v, 4));
        Assert.All(tensor[128..], v => Assert.Equal(0.2f, v, 4));
    }

    private static void Paint(RgbaImage image, int x, int y, int w, int h, byte value)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
            {
                var o = (yy * image.Width + xx) * 4;
                image.Pixels[o] = image.Pixels[o + 1] = image.Pixels[o + 2] = value;
                image.Pixels[o + 3] = 255;
            }
    }

    private static (int X1, int Y1, int X2, int Y2) OpaqueBounds(RgbaImage image)
    {
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image.Pixels[(y * image.Width + x) * 4 + 3] > 0)
                {
                    x1 = Math.Min(x1, x);
                    y1 = Math.Min(y1, y);
                    x2 = Math.Max(x2, x);
                    y2 = Math.Max(y2, y);
                }

        return (x1, y1, x2, y2);
    }
}
