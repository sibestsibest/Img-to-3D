namespace ImageTo3D.Core.Imaging;

/// <summary>8-bit, non-premultiplied RGBA image stored row-major.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        pixels ??= new byte[checked(width * height * 4)];
        if (pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match dimensions.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>True when at least one pixel is not fully opaque.</summary>
    public bool HasTransparency()
    {
        for (var i = 3; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] < 255)
                return true;
        }

        return false;
    }
}
