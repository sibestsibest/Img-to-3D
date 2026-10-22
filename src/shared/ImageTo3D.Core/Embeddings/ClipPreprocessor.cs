using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Core.Embeddings;

/// <summary>
/// CLIP image preprocessing as in the model's preprocessor_config.json: shortest side resized to
/// 224 with bicubic filtering, centre crop 224 x 224, scaled to [0, 1] and normalised with CLIP's
/// mean and standard deviation, laid out as CHW. Transparent pixels are composited onto white
/// first (the reference simply drops alpha, which exposes arbitrary hidden colours).
/// </summary>
public static class ClipPreprocessor
{
    public const int Size = 224;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static float[] ToTensor(RgbaImage image)
    {
        var (w, h) = (image.Width, image.Height);
        var (rw, rh) = w <= h ? (Size, Math.Max(Size, (int)((long)Size * h / w))) : (Math.Max(Size, (int)((long)Size * w / h)), Size);

        var rgb = new float[w * h * 3];
        var p = image.Pixels;
        for (var i = 0; i < w * h; i++)
        {
            var a = p[i * 4 + 3] / 255f;
            for (var c = 0; c < 3; c++)
                rgb[i * 3 + c] = p[i * 4 + c] * a + 255f * (1 - a);
        }

        var resized = Resampler.Resize(rgb, w, h, 3, rw, rh, ResampleFilter.Bicubic);
        var top = (rh - Size) / 2;
        var left = (rw - Size) / 2;
        var tensor = new float[3 * Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var src = ((y + top) * rw + x + left) * 3;
                for (var c = 0; c < 3; c++)
                {
                    // Clamp like PIL's 8-bit result before rescaling (bicubic can overshoot).
                    var v = Math.Clamp(resized[src + c], 0f, 255f) / 255f;
                    tensor[c * Size * Size + y * Size + x] = (v - Mean[c]) / Std[c];
                }
            }
        }

        return tensor;
    }
}

/// <summary>Vector helpers for embeddings: all vectors are L2-normalised so cosine is a dot product.</summary>
public static class EmbeddingMath
{
    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (var x in v)
            sum += x * x;
        var norm = Math.Sqrt(sum);
        var result = v.ToArray();
        if (norm > 0)
        {
            for (var i = 0; i < result.Length; i++)
                result[i] = (float)(result[i] / norm);
        }

        return result;
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors differ in length.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>Mean of several (prompt) embeddings, re-normalised: CLIP's prompt-ensembling.</summary>
    public static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var sum = new float[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
                sum[i] += v[i];
        }

        return Normalize(sum);
    }

    public static double[] Softmax(IReadOnlyList<double> logits, double scale)
    {
        var max = logits.Max() * scale;
        var exp = logits.Select(l => Math.Exp(l * scale - max)).ToArray();
        var total = exp.Sum();
        return exp.Select(e => e / total).ToArray();
    }

    /// <summary>
    /// Average-linkage agglomerative clustering on cosine similarity: clusters are merged while the
    /// mean pairwise similarity between them is at least <paramref name="threshold"/>. Returns a
    /// cluster index per item; clusters are numbered in order of their first item.
    /// </summary>
    public static int[] Cluster(IReadOnlyList<float[]> vectors, double threshold)
    {
        var n = vectors.Count;
        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        var active = Enumerable.Repeat(true, n).ToArray();
        var sim = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
                sim[i, j] = sim[j, i] = Cosine(vectors[i], vectors[j]);
        }

        while (true)
        {
            var (bi, bj, best) = (-1, -1, double.NegativeInfinity);
            for (var i = 0; i < n; i++)
            {
                if (!active[i])
                    continue;
                for (var j = i + 1; j < n; j++)
                {
                    if (active[j] && sim[i, j] > best)
                        (bi, bj, best) = (i, j, sim[i, j]);
                }
            }

            if (bi < 0 || best < threshold)
                break;

            // Lance-Williams update for average linkage.
            var (si, sj) = (members[bi].Count, members[bj].Count);
            for (var k = 0; k < n; k++)
            {
                if (active[k] && k != bi && k != bj)
                    sim[bi, k] = sim[k, bi] = (si * sim[bi, k] + sj * sim[bj, k]) / (si + sj);
            }

            members[bi].AddRange(members[bj]);
            active[bj] = false;
        }

        var labels = new int[n];
        var next = 0;
        foreach (var cluster in Enumerable.Range(0, n).Where(i => active[i]).Select(i => members[i]).OrderBy(m => m.Min()))
        {
            foreach (var item in cluster)
                labels[item] = next;
            next++;
        }

        return labels;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException("Embedding blob has an invalid length.");
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
