using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageTo3D.Core.Embeddings;

/// <summary>
/// Byte-level BPE tokenizer of OpenAI CLIP (simple_tokenizer.py), reading the vocab.json and
/// merges.txt shipped with the model. Text is HTML-unescaped, whitespace-collapsed and lower-cased
/// (ftfy's mojibake repair is not ported: inputs here are typed queries and fixed prompts), split
/// with CLIP's pattern, mapped byte-to-unicode and merged by rank; the last symbol of a word
/// carries "&lt;/w&gt;". Sequences are wrapped in start/end tokens and cut to 77 tokens.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int ContextLength = 77;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly ConcurrentDictionary<string, string[]> _cache = new();
    private static readonly string[] ByteToUnicode = BuildByteToUnicode();

    public ClipTokenizer(Dictionary<string, int> vocab, IEnumerable<string> mergeLines)
    {
        _vocab = vocab;
        _ranks = new Dictionary<(string, string), int>();
        foreach (var line in mergeLines)
        {
            if (line.Length == 0 || line.StartsWith("#version", StringComparison.Ordinal))
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                throw new InvalidDataException("merges.txt has a malformed line.");
            _ranks.TryAdd((line[..space], line[(space + 1)..]), _ranks.Count);
        }

        StartToken = Id("<|startoftext|>");
        EndToken = Id("<|endoftext|>");
    }

    public int StartToken { get; }

    public int EndToken { get; }

    public int VocabularySize => _vocab.Count;

    public static ClipTokenizer Load(string vocabPath, string mergesPath)
    {
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllBytes(vocabPath))
                    ?? throw new InvalidDataException("vocab.json is empty.");
        return new ClipTokenizer(vocab, File.ReadLines(mergesPath));
    }

    /// <summary>Token IDs including start and end tokens, at most <see cref="ContextLength"/> long.</summary>
    public long[] Encode(string text)
    {
        var ids = new List<long> { StartToken };
        foreach (Match m in Pattern().Matches(Clean(text)))
        {
            var bytes = Encoding.UTF8.GetBytes(m.Value);
            var unicode = string.Concat(bytes.Select(b => ByteToUnicode[b]));
            foreach (var piece in Bpe(unicode))
                ids.Add(Id(piece));
        }

        if (ids.Count > ContextLength - 1)
            ids.RemoveRange(ContextLength - 1, ids.Count - (ContextLength - 1));
        ids.Add(EndToken);
        return ids.ToArray();
    }

    private int Id(string token) =>
        _vocab.TryGetValue(token, out var id) ? id : throw new InvalidDataException($"Token '{token}' is not in the vocabulary.");

    private static string Clean(string text)
    {
        text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text));
        return Whitespace().Replace(text, " ").Trim().ToLowerInvariant();
    }

    private string[] Bpe(string token) => _cache.GetOrAdd(token, t =>
    {
        // Symbols are text elements of the byte-mapped string (each byte maps to one char).
        var word = t.Select(c => c.ToString()).ToList();
        word[^1] += "</w>";
        while (word.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var rank) && rank < bestRank)
                    (best, bestRank) = (i, rank);
            }

            if (best < 0)
                break;

            // Merge every occurrence of the best pair, left to right, as the reference does.
            var (first, second) = (word[best], word[best + 1]);
            var merged = new List<string>(word.Count);
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second)
                {
                    merged.Add(first + second);
                    i++;
                }
                else
                {
                    merged.Add(word[i]);
                }
            }

            word = merged;
        }

        return word.ToArray();
    });

    /// <summary>GPT-2/CLIP reversible byte-to-unicode table: printable bytes map to themselves, the rest to 256+.</summary>
    private static string[] BuildByteToUnicode()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToHashSet();
        var table = new string[256];
        var next = 0;
        for (var b = 0; b < 256; b++)
            table[b] = ((char)(printable.Contains(b) ? b : 256 + next++)).ToString();
        return table;
    }

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
