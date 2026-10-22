using ImageTo3D.Api.Models;
using ImageTo3D.Core.Embeddings;

namespace ImageTo3D.Api.Services.References;

/// <summary>One contribution to a score: <c>effect</c> is "+", "-" or "0" (informational).</summary>
public sealed record RankReason(string Code, string Message, string Effect);

/// <summary>A zero-shot suggestion: the best label and its probability; "uncertain" when no label is clearly ahead.</summary>
public sealed record ZeroShot(string Label, double Confidence);

/// <summary>Text prompts per label, already embedded (each label's prompts averaged: CLIP prompt ensembling).</summary>
public sealed record PromptSet(IReadOnlyList<(string Label, float[] Embedding)> Labels);

public sealed record RankItem(
    Guid Id,
    int SortOrder,
    float[]? Embedding,
    int? MinSide,
    string LicenseTone,
    string Source,
    bool IsDuplicate,
    Guid? DuplicateOf,
    bool MatchesInput,
    bool Accepted);

public sealed record RankContext(
    float[]? Query,
    float[]? Input,
    ZeroShot? InputViewpoint,
    PromptSet? Viewpoints,
    PromptSet? Kinds,
    double GroupThreshold);

public sealed record RankResult(
    int Position,
    double Score,
    double Confidence,
    int? Group,
    double? QuerySimilarity,
    double? InputSimilarity,
    ZeroShot? Viewpoint,
    ZeroShot? Kind,
    ZeroShot? Relation,
    IReadOnlyList<RankReason> Reasons);

/// <summary>
/// Ranks reference pictures from the evidence available. Every signal is mapped to 0..1 and
/// weighted; missing signals are left out (weights re-normalised), so a session without embeddings
/// or without an input photo is still ranked, with lower confidence. The calibration constants are
/// heuristics chosen for CLIP ViT-B/32 cosines (query match ≈ 0.18–0.32, same object ≈ 0.8–0.95),
/// not measured accuracies. A high similarity is never treated as proof of the same object: the
/// relation is only suggested, with a capped confidence.
/// </summary>
public static class ReferenceRanker
{
    /// <summary>CLIP's learned logit scale (exp(4.6052) ≈ 100) used for zero-shot softmax.</summary>
    public const double LogitScale = 100;

    private const double Uncertain = 0.4;

    private static readonly Dictionary<string, double> SourceReliability = new()
    {
        ["upload"] = 1.0,
        ["wikimedia"] = 0.9,
        ["openverse"] = 0.8,
        ["internet_archive"] = 0.6,
    };

    public static double QueryScore(double cosine) => Math.Clamp((cosine - 0.18) / 0.14, 0, 1);

    public static double VisualScore(double cosine) => Math.Clamp((cosine - 0.6) / 0.3, 0, 1);

    public static double ResolutionScore(int minSide) => Math.Clamp(Math.Log2(minSide / 200.0) / 2.3, 0, 1);

    public static ZeroShot Classify(float[] image, PromptSet prompts)
    {
        var probabilities = EmbeddingMath.Softmax(prompts.Labels.Select(l => EmbeddingMath.Cosine(image, l.Embedding)).ToList(), LogitScale);
        var best = Array.IndexOf(probabilities, probabilities.Max());
        return new ZeroShot(probabilities[best] >= Uncertain ? prompts.Labels[best].Label : Viewpoints.Uncertain, Math.Round(probabilities[best], 3));
    }

    /// <summary>Suggested relation to the input photo from visual similarity alone (confidence capped at 0.5).</summary>
    public static ZeroShot SuggestRelation(double inputCosine) => inputCosine switch
    {
        >= 0.9 => new ZeroShot("same_object", 0.5),
        >= 0.8 => new ZeroShot("variant", 0.4),
        _ => new ZeroShot("similar_different", 0.4),
    };

    public static IReadOnlyDictionary<Guid, RankResult> Rank(IReadOnlyList<RankItem> items, RankContext context)
    {
        var accepted = items.Where(i => i.Accepted && i.Embedding is not null).ToList();
        var scored = new List<(RankItem Item, double Score, double Confidence, double? Q, double? I, ZeroShot? View, ZeroShot? Kind, ZeroShot? Relation, List<RankReason> Reasons)>();

        foreach (var item in items)
        {
            var reasons = new List<RankReason>();
            double weighted = 0, weights = 0;
            void Add(double weight, double value)
            {
                weighted += weight * value;
                weights += weight;
            }

            double? q = null, sim = null;
            ZeroShot? view = null, kind = null, relation = null;
            var e = item.Embedding;
            if (e is not null)
            {
                if (context.Query is { } query)
                {
                    q = EmbeddingMath.Cosine(e, query);
                    var v = QueryScore(q.Value);
                    Add(0.30, v);
                    if (v >= 0.6)
                        reasons.Add(new RankReason("query_match", $"Strong match for the search text (CLIP {q:0.00}).", "+"));
                    else if (v <= 0.2)
                        reasons.Add(new RankReason("query_weak", $"Weak match for the search text (CLIP {q:0.00}).", "-"));
                }

                if (context.Input is { } input)
                {
                    sim = EmbeddingMath.Cosine(e, input);
                    var v = VisualScore(sim.Value);
                    Add(0.25, v);
                    relation = SuggestRelation(sim.Value);
                    if (v >= 0.6)
                        reasons.Add(new RankReason("looks_like_input", $"Looks like your photo (similarity {sim:0.00}).", "+"));
                    else if (v <= 0.2)
                        reasons.Add(new RankReason("unlike_input", $"Looks different from your photo (similarity {sim:0.00}).", "-"));
                }

                var others = accepted.Where(a => a.Id != item.Id).ToList();
                if (others.Count > 0)
                {
                    var best = others.Max(a => EmbeddingMath.Cosine(e, a.Embedding!));
                    var v = VisualScore(best);
                    Add(0.15, v);
                    if (v >= 0.6)
                        reasons.Add(new RankReason("like_accepted", $"Similar to pictures you accepted ({best:0.00}).", "+"));
                }

                if (context.Viewpoints is { } viewpoints)
                    view = Classify(e, viewpoints);
                if (context.Kinds is { } kinds)
                    kind = Classify(e, kinds);

                // A different side only helps when the picture plausibly shows the same kind of object.
                if (view is { Label: not Viewpoints.Uncertain } && context.InputViewpoint is { Label: not Viewpoints.Uncertain } inputView && sim is { } s && VisualScore(s) > 0.3)
                {
                    var differs = view.Label != inputView.Label;
                    Add(0.10, differs ? 1 : 0.2);
                    reasons.Add(differs
                        ? new RankReason("new_view", $"Probably shows the {Label(view.Label)} ({view.Confidence:P0}), a side your photo ({Label(inputView.Label)}) does not show.", "+")
                        : new RankReason("same_view", $"Probably the same side as your photo ({Label(view.Label)}).", "0"));
                }
            }

            if (item.MinSide is { } side)
            {
                Add(0.10, ResolutionScore(side));
                if (side < 400)
                    reasons.Add(new RankReason("low_resolution", $"Low resolution ({side} px on the short side).", "-"));
                else if (side >= 1000)
                    reasons.Add(new RankReason("high_resolution", $"High resolution ({side} px on the short side).", "+"));
            }

            Add(0.05, item.LicenseTone switch { "free" => 1, "restricted" => 0.6, _ => 0.25 });
            if (item.LicenseTone == "unknown")
                reasons.Add(new RankReason("licence_unknown", "Licence unknown: personal reference only.", "-"));
            else if (item.LicenseTone == "restricted")
                reasons.Add(new RankReason("licence_restricted", "Licence restricts commercial use or changes.", "-"));
            Add(0.05, SourceReliability.GetValueOrDefault(item.Source, 0.6));

            var score = weights > 0 ? weighted / weights : 0;
            if (item.IsDuplicate)
            {
                score *= 0.3;
                reasons.Add(new RankReason("duplicate", "Copy of another picture in this session.", "-"));
            }

            if (item.MatchesInput)
            {
                score *= 0.5;
                reasons.Add(new RankReason("is_input", "This is your input photo itself: it adds no new view.", "-"));
            }

            var confidence = 0.25;
            if (e is not null)
                confidence += 0.35 + (context.Query is not null ? 0.2 : 0) + (context.Input is not null ? 0.2 : 0);
            else
                reasons.Add(new RankReason("no_visual", "Not compared visually (no preview or no embedding model).", "0"));

            scored.Add((item, score, Math.Min(confidence, 1), q, sim, view, kind, relation, reasons));
        }

        var groups = Groups(items, context.GroupThreshold);
        var ordered = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Item.SortOrder).ToList();

        // Groups are numbered by their best-ranked member, so group 1 holds the top picture.
        var groupNumbers = new Dictionary<int, int>();
        foreach (var s in ordered)
        {
            if (groups.TryGetValue(s.Item.Id, out var g) && !groupNumbers.ContainsKey(g))
                groupNumbers[g] = groupNumbers.Count + 1;
        }

        return ordered.Select((s, index) => (s, index)).ToDictionary(
            x => x.s.Item.Id,
            x => new RankResult(
                x.index + 1,
                Math.Round(x.s.Score, 4),
                Math.Round(x.s.Confidence, 2),
                groups.TryGetValue(x.s.Item.Id, out var g) ? groupNumbers[g] : null,
                x.s.Q is { } q ? Math.Round(q, 4) : null,
                x.s.I is { } i ? Math.Round(i, 4) : null,
                x.s.View,
                x.s.Kind,
                x.s.Relation,
                x.s.Reasons));
    }

    /// <summary>Visual groups over pictures with embeddings; duplicates join their original's group.</summary>
    private static Dictionary<Guid, int> Groups(IReadOnlyList<RankItem> items, double threshold)
    {
        var originals = items.Where(i => i.Embedding is not null && !i.IsDuplicate).OrderBy(i => i.SortOrder).ToList();
        var result = new Dictionary<Guid, int>();
        if (originals.Count == 0)
            return result;
        var labels = EmbeddingMath.Cluster(originals.Select(o => o.Embedding!).ToList(), threshold);
        for (var i = 0; i < originals.Count; i++)
            result[originals[i].Id] = labels[i];
        foreach (var d in items.Where(i => i.IsDuplicate && i.DuplicateOf is { } of && result.ContainsKey(of)))
            result[d.Id] = result[d.DuplicateOf!.Value];
        return result;
    }

    private static string Label(string viewpoint) => viewpoint.Replace('_', '-');
}

/// <summary>Facts about a model candidate, from its listing and (for uploads) its geometry.</summary>
public sealed record ModelFacts(
    bool Uploaded,
    bool? IsDownloadable,
    int? FaceCount,
    bool? Derivatives,
    bool LicenseKnown,
    double? VisualCosine,
    double? QueryCosine,
    ModelGeometry? Geometry);

public sealed record ModelGeometry(
    int Triangles,
    int Components,
    bool Closed,
    int BoundaryEdges,
    int NonManifoldEdges,
    int DegenerateTriangles,
    double SizeXMm,
    double SizeYMm,
    double SizeZMm,
    string ScaleStatus);

public sealed record ModelEvaluation(double Score, double Confidence, IReadOnlyList<RankReason> Reasons);

/// <summary>
/// Scores an existing model for reuse: does it look like the object, can it be obtained and
/// modified, and is the mesh workable. The name is deliberately not a signal. Listing-only
/// candidates get a low confidence: their mesh was never checked.
/// </summary>
public static class ModelEvaluator
{
    public static ModelEvaluation Evaluate(ModelFacts f)
    {
        var reasons = new List<RankReason>();
        double weighted = 0, weights = 0;
        void Add(double weight, double value)
        {
            weighted += weight * value;
            weights += weight;
        }

        if (f.VisualCosine is { } visual)
        {
            var v = ReferenceRanker.VisualScore(visual);
            Add(0.35, v);
            reasons.Add(v >= 0.6
                ? new RankReason("looks_like_object", $"Preview looks like your photo or accepted pictures ({visual:0.00}).", "+")
                : v <= 0.2
                    ? new RankReason("looks_different", $"Preview looks different from your photo ({visual:0.00}).", "-")
                    : new RankReason("looks_somewhat", $"Preview is somewhat similar ({visual:0.00}).", "0"));
        }

        if (f.QueryCosine is { } query)
        {
            var v = ReferenceRanker.QueryScore(query);
            Add(0.15, v);
            if (v <= 0.2)
                reasons.Add(new RankReason("query_weak", $"Preview matches the search text weakly (CLIP {query:0.00}).", "-"));
        }

        if (!f.Uploaded)
        {
            Add(0.15, f.IsDownloadable == true ? 1 : 0);
            reasons.Add(f.IsDownloadable == true
                ? new RankReason("downloadable", "Offered for download by the source (a free account may be needed).", "+")
                : new RankReason("not_downloadable", "Not downloadable from the source.", "-"));
        }

        Add(0.15, f.Derivatives switch { true => 1, false => 0, _ => 0.3 });
        if (f.Derivatives == false)
            reasons.Add(new RankReason("no_derivatives", "Licence does not allow modified versions.", "-"));
        else if (!f.LicenseKnown)
            reasons.Add(new RankReason("licence_unknown", "Licence unknown or not a standard open licence: check it before reuse.", "-"));

        var faces = f.Geometry?.Triangles ?? f.FaceCount;
        if (faces is { } n)
        {
            var v = n < 300 ? 0.3 : n <= 150_000 ? 1 : Math.Clamp(1 - Math.Log10(n / 150_000.0), 0.2, 1);
            Add(0.10, v);
            if (n < 300)
                reasons.Add(new RankReason("very_coarse", $"Very few faces ({n:N0}): probably too coarse.", "-"));
            else if (n > 150_000)
                reasons.Add(new RankReason("dense_mesh", $"Dense mesh ({n:N0} faces): simplify before editing.", "-"));
        }

        if (f.Geometry is { } g)
        {
            var quality = g.Closed ? 1.0 : 0.4;
            if (g.Closed)
                reasons.Add(new RankReason("closed", "Closed surface (watertight).", "+"));
            else
                reasons.Add(new RankReason("open", $"Open or non-manifold surface ({g.BoundaryEdges:N0} boundary, {g.NonManifoldEdges:N0} non-manifold edges): needs repair before printing or splitting.", "-"));
            if (g.DegenerateTriangles > g.Triangles / 100)
            {
                quality *= 0.7;
                reasons.Add(new RankReason("degenerate", $"{g.DegenerateTriangles:N0} degenerate faces.", "-"));
            }

            if (g.Components > 200)
            {
                quality *= 0.8;
                reasons.Add(new RankReason("fragmented", $"{g.Components:N0} separate pieces.", "-"));
            }
            else if (g.Components > 1)
            {
                reasons.Add(new RankReason("components", $"{g.Components:N0} separate pieces (can become parts).", "0"));
            }

            Add(0.20, quality);
            reasons.Add(g.ScaleStatus switch
            {
                "plausible" => new RankReason("scale_plausible", $"Declared size {g.SizeXMm:0.#} × {g.SizeYMm:0.#} × {g.SizeZMm:0.#} mm looks plausible; still confirm it.", "0"),
                "unitless" => new RankReason("scale_unitless", "STL has no units: millimetres assumed; confirm the size.", "0"),
                _ => new RankReason("scale_suspicious", $"Declared size {g.SizeXMm:0.#} × {g.SizeYMm:0.#} × {g.SizeZMm:0.#} mm looks wrong for a model part: rescale it.", "-"),
            });
        }

        var confidence = (f.Geometry is not null ? 0.5 : 0.2) + (f.VisualCosine is not null ? 0.25 : 0);
        if (f.VisualCosine is null)
            reasons.Add(new RankReason("no_visual", "Not compared visually (no preview or no embedding model).", "0"));
        if (f.Geometry is null)
            reasons.Add(new RankReason("listing_only", "Judged from the listing only: the mesh itself has not been checked.", "0"));

        return new ModelEvaluation(Math.Round(weights > 0 ? weighted / weights : 0, 4), Math.Round(Math.Min(confidence, 1), 2), reasons);
    }
}
