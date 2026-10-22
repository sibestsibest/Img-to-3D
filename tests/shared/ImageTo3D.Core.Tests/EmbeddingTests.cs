using ImageTo3D.Core.Embeddings;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Core.Tests;

/// <summary>Runs only when models/clip (vocab.json, merges.txt) is present: the real CLIP vocabulary.</summary>
public sealed class RequiresClipVocabularyFactAttribute : FactAttribute
{
    public RequiresClipVocabularyFactAttribute()
    {
        if (ClipTokenizerTests.FindClipDirectory() is null)
            Skip = "models/clip not found (run scripts/download-models.ps1 -Embeddings).";
    }
}

public class ClipTokenizerTests
{
    public static string? FindClipDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models", "clip");
            if (File.Exists(Path.Combine(candidate, "vocab.json")) && File.Exists(Path.Combine(candidate, "merges.txt")))
                return candidate;
        }

        return null;
    }

    private static ClipTokenizer Real()
    {
        var dir = FindClipDirectory()!;
        return ClipTokenizer.Load(Path.Combine(dir, "vocab.json"), Path.Combine(dir, "merges.txt"));
    }

    /// <summary>Tiny vocabulary: merges "l o" then "lo w&lt;/w&gt;" turn "low" into one token.</summary>
    private static ClipTokenizer Tiny() => new(
        new Dictionary<string, int>
        {
            ["l"] = 0, ["o"] = 1, ["w"] = 2, ["w</w>"] = 3, ["lo"] = 4, ["low</w>"] = 5, ["o</w>"] = 6, ["!</w>"] = 7,
            ["<|startoftext|>"] = 100, ["<|endoftext|>"] = 101,
        },
        ["#version: 0.2", "l o", "lo w</w>"]);

    [Fact]
    public void Bpe_AppliesMergesByRankAndMarksWordEnds()
    {
        var t = Tiny();
        Assert.Equal([100, 5, 101], t.Encode("low"));
        Assert.Equal([100, 5, 5, 7, 101], t.Encode("  LOW\tlow! ")); // lower-cased, whitespace collapsed, punctuation split
        Assert.Equal([100, 4, 6, 101], t.Encode("loo")); // "l o" merges; "o</w>" stays
    }

    [Fact]
    public void Encode_UnknownSymbolsAreReported()
    {
        Assert.Throws<InvalidDataException>(() => Tiny().Encode("zz"));
    }

    [RequiresClipVocabularyFact]
    public void Real_MatchesReferenceTokenIds()
    {
        var t = Real();
        Assert.Equal(49408, t.VocabularySize);
        // Reference ids from vocab.json: a</w>=320, photo</w>=1125, of</w>=539, cat</w>=2368.
        Assert.Equal([49406, 320, 1125, 539, 320, 2368, 49407], t.Encode("a photo of a cat"));
        Assert.Equal(t.Encode("a photo of a cat"), t.Encode("  A   Photo of a CAT "));
        Assert.Equal(t.Encode("a &amp; b"), t.Encode("a & b")); // HTML entities unescaped
        Assert.Equal(26087, t.Encode("gundam")[1]);
    }

    [RequiresClipVocabularyFact]
    public void Real_SplitsHyphenatedNamesAndDigitsAndCapsLength()
    {
        var t = Real();
        var ids = t.Encode("RX-78-2 Gundam");
        Assert.Equal(49406, ids[0]);
        Assert.Equal(49407, ids[^1]);
        Assert.True(ids.Length > 5); // rx, -, 7, 8, -, 2, gundam: digits are single tokens in CLIP
        var longText = string.Join(' ', Enumerable.Repeat("gundam", 200));
        var capped = t.Encode(longText);
        Assert.Equal(ClipTokenizer.ContextLength, capped.Length);
        Assert.Equal(49407, capped[^1]);
    }
}

public class ClipPreprocessorTests
{
    private static RgbaImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var image = new RgbaImage(w, h);
        for (var i = 0; i < w * h; i++)
            (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2], image.Pixels[i * 4 + 3]) = (r, g, b, a);
        return image;
    }

    [Fact]
    public void Tensor_IsChwNormalisedWithClipStatistics()
    {
        var tensor = ClipPreprocessor.ToTensor(Solid(300, 300, 255, 255, 255));
        Assert.Equal(3 * 224 * 224, tensor.Length);
        Assert.Equal((1 - 0.48145466f) / 0.26862954f, tensor[0], 3);
        Assert.Equal((1 - 0.4578275f) / 0.26130258f, tensor[224 * 224], 3);
        Assert.Equal((1 - 0.40821073f) / 0.27577711f, tensor[2 * 224 * 224 + 500], 3);
    }

    [Fact]
    public void Transparent_PixelsBecomeWhite()
    {
        Assert.Equal(ClipPreprocessor.ToTensor(Solid(64, 64, 255, 255, 255)), ClipPreprocessor.ToTensor(Solid(64, 64, 10, 200, 30, 0)));
    }

    [Fact]
    public void WideImages_AreCentreCropped()
    {
        // Three vertical bands (red, green, blue): only the green middle survives the centre crop.
        var image = new RgbaImage(672, 224);
        for (var y = 0; y < 224; y++)
        {
            for (var x = 0; x < 672; x++)
            {
                var i = (y * 672 + x) * 4;
                image.Pixels[i + x / 224] = 255;
                image.Pixels[i + 3] = 255;
            }
        }

        var tensor = ClipPreprocessor.ToTensor(image);
        var centre = 112 * 224 + 112;
        Assert.True(tensor[224 * 224 + centre] > 1.5, "green channel high in the middle");
        Assert.True(tensor[centre] < -1.5, "red channel low in the middle");
    }
}

public class EmbeddingMathTests
{
    [Fact]
    public void Bicubic_KeepsConstantsAndIsIdentityAtTheSameSize()
    {
        var src = Enumerable.Range(0, 30 * 20).Select(i => (float)(i % 7)).ToArray();
        Assert.Equal(src, Resampler.Resize(src, 30, 20, 1, 30, 20, ResampleFilter.Bicubic));
        var constant = Enumerable.Repeat(42f, 100 * 80).ToArray();
        Assert.All(Resampler.Resize(constant, 100, 80, 1, 37, 23, ResampleFilter.Bicubic), v => Assert.Equal(42f, v, 3));
    }

    [Fact]
    public void Cosine_AndNormalisation()
    {
        var a = EmbeddingMath.Normalize(new float[] { 3, 4 });
        Assert.Equal(1, Math.Sqrt(a[0] * a[0] + a[1] * a[1]), 6);
        Assert.Equal(1, EmbeddingMath.Cosine(a, new float[] { 6, 8 }), 6);
        Assert.Equal(0, EmbeddingMath.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 }), 6);
        Assert.Equal(a, EmbeddingMath.FromBytes(EmbeddingMath.ToBytes(a)));
    }

    [Fact]
    public void Softmax_SumsToOneAndFavoursTheLargest()
    {
        var p = EmbeddingMath.Softmax([0.30, 0.25, 0.20], 100);
        Assert.Equal(1, p.Sum(), 9);
        Assert.True(p[0] > 0.99);
    }

    [Fact]
    public void Cluster_GroupsCloseVectorsAndKeepsOutliersApart()
    {
        float[][] vectors =
        [
            [1, 0.05f, 0], [0.98f, 0.1f, 0], [0, 1, 0.02f], [1, 0, 0.03f], [0.02f, 0.97f, 0.1f], [0, 0, 1],
        ];
        var labels = EmbeddingMath.Cluster(vectors, 0.9);
        Assert.Equal([0, 0, 1, 0, 1, 2], labels);
        Assert.Equal([0, 1, 2], EmbeddingMath.Cluster(vectors.Take(3).ToList(), 0.9999)); // nothing merges
    }
}
