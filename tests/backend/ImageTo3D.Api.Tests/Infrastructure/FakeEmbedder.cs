using System.Collections.Concurrent;
using ImageTo3D.Api.Services.Embeddings;
using ImageTo3D.Core.Embeddings;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Api.Tests.Infrastructure;

/// <summary>
/// Deterministic stand-in for CLIP: an image's vector is its mean colour (R, G, B and their
/// complements), so equally coloured pictures are identical and red differs from blue. Texts map
/// to configurable vectors (default: neutral grey). Can be switched off or made to fail.
/// </summary>
public sealed class FakeEmbedder : IImageEmbedder
{
    private int _images;

    public bool Available { get; set; } = true;

    public bool Fail { get; set; }

    public int ImageCalls => _images;

    /// <summary>Exact text → vector (unnormalised is fine); others use <see cref="DefaultText"/>.</summary>
    public ConcurrentDictionary<string, float[]> Texts { get; } = new();

    public float[] DefaultText { get; set; } = [1, 1, 1, 1, 1, 1];

    public string ModelId => "fake-mean-colour";

    public EmbedderStatus Status => new(Available, "Fake", Available ? null : "Fake embedder switched off.");

    public Task<bool> EnsureReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Available);

    public float[] EmbedImage(RgbaImage image, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new InvalidOperationException("fake failure");
        Interlocked.Increment(ref _images);
        double r = 0, g = 0, b = 0;
        var n = image.Width * image.Height;
        for (var i = 0; i < n; i++)
        {
            r += image.Pixels[i * 4];
            g += image.Pixels[i * 4 + 1];
            b += image.Pixels[i * 4 + 2];
        }

        (r, g, b) = (r / n / 255, g / n / 255, b / n / 255);
        return EmbeddingMath.Normalize(new[] { (float)r, (float)g, (float)b, (float)(1 - r), (float)(1 - g), (float)(1 - b) });
    }

    public float[] EmbedText(string text, CancellationToken cancellationToken) =>
        EmbeddingMath.Normalize(Texts.TryGetValue(text, out var v) ? v : DefaultText);
}
