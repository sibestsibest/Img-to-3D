using ImageTo3D.Api.Services.References;
using ImageTo3D.Core.Embeddings;

namespace ImageTo3D.Api.Tests;

public class ReferenceRankerTests
{
    private static float[] V(params float[] v) => EmbeddingMath.Normalize(v);

    private static RankItem Item(int order, float[]? e, int? side = 1200, string licence = "free", string source = "wikimedia", bool duplicate = false, bool input = false, bool accepted = false) =>
        new(Guid.NewGuid(), order, e, side, licence, source, duplicate, null, input, accepted);

    private static PromptSet Views() => new([("front", V(1, 0, 0, 0)), ("back", V(0, 1, 0, 0)), ("side", V(0, 0, 1, 0))]);

    [Fact]
    public void Rank_PrefersVisualAndQueryMatchesAndExplainsWhy()
    {
        var query = V(1, 0, 0, 0);
        var good = Item(0, V(1, 0.2f, 0, 0));
        var poor = Item(1, V(0, 0, 0, 1));
        var results = ReferenceRanker.Rank([poor, good], new RankContext(query, query, null, null, null, 0.86));

        Assert.Equal(1, results[good.Id].Position);
        Assert.Equal(2, results[poor.Id].Position);
        Assert.Contains(results[good.Id].Reasons, r => r.Code == "query_match" && r.Effect == "+");
        Assert.Contains(results[good.Id].Reasons, r => r.Code == "looks_like_input");
        Assert.Contains(results[poor.Id].Reasons, r => r.Code == "query_weak" && r.Effect == "-");
        Assert.Equal(1.0, results[good.Id].Confidence); // embeddings, query and input photo all present
    }

    [Fact]
    public void Rank_WithoutEmbeddings_UsesOtherSignalsWithLowConfidence()
    {
        var sharp = Item(0, null, side: 2000, licence: "free");
        var blurry = Item(1, null, side: 200, licence: "unknown");
        var results = ReferenceRanker.Rank([blurry, sharp], new RankContext(null, null, null, null, null, 0.86));

        Assert.Equal(1, results[sharp.Id].Position);
        Assert.Equal(0.25, results[sharp.Id].Confidence);
        Assert.Contains(results[blurry.Id].Reasons, r => r.Code == "low_resolution");
        Assert.Contains(results[blurry.Id].Reasons, r => r.Code == "licence_unknown");
        Assert.Contains(results[sharp.Id].Reasons, r => r.Code == "no_visual");
        Assert.Null(results[sharp.Id].Group);
        Assert.Null(results[sharp.Id].Viewpoint);
    }

    [Fact]
    public void Rank_PenalisesDuplicatesAndTheInputPhotoItself()
    {
        var e = V(1, 0, 0);
        var original = Item(0, e);
        var copy = Item(1, e, duplicate: true) with { DuplicateOf = original.Id };
        var self = Item(2, e, input: true);
        var results = ReferenceRanker.Rank([original, copy, self], new RankContext(null, e, null, null, null, 0.86));

        Assert.Equal(1, results[original.Id].Position);
        Assert.True(results[copy.Id].Score < results[self.Id].Score);
        Assert.Contains(results[copy.Id].Reasons, r => r.Code == "duplicate");
        Assert.Contains(results[self.Id].Reasons, r => r.Code == "is_input");
        Assert.Equal(results[original.Id].Group, results[copy.Id].Group); // a copy joins its original's group
    }

    [Fact]
    public void Rank_GroupsSimilarPicturesAndNumbersGroupsByBestMember()
    {
        var query = V(0, 1, 0);
        var a1 = Item(0, V(1, 0.1f, 0));
        var b1 = Item(1, V(0.1f, 1, 0));
        var a2 = Item(2, V(1, 0.15f, 0.05f));
        var results = ReferenceRanker.Rank([a1, b1, a2], new RankContext(query, null, null, null, null, 0.9));

        Assert.Equal(results[a1.Id].Group, results[a2.Id].Group);
        Assert.NotEqual(results[a1.Id].Group, results[b1.Id].Group);
        Assert.Equal(1, results[b1.Id].Group); // b1 matches the query best, so its group is first
    }

    [Fact]
    public void Rank_RewardsAViewTheInputPhotoDoesNotShow()
    {
        var input = V(1, 0.9f, 0, 0);
        var back = Item(0, V(0.9f, 1, 0, 0));
        var front = Item(1, V(1, 0.8f, 0, 0));
        var context = new RankContext(null, input, new ZeroShot("front", 0.8), Views(), null, 0.86);
        var results = ReferenceRanker.Rank([front, back], context);

        Assert.Equal("back", results[back.Id].Viewpoint!.Label);
        Assert.Contains(results[back.Id].Reasons, r => r.Code == "new_view" && r.Effect == "+");
        Assert.Contains(results[front.Id].Reasons, r => r.Code == "same_view");
    }

    [Fact]
    public void Classify_IsUncertainWhenNoLabelIsClearlyAhead()
    {
        var result = ReferenceRanker.Classify(V(1, 1, 1, 0), Views());
        Assert.Equal("uncertain", result.Label);
        Assert.True(result.Confidence < 0.4);
        Assert.Equal("front", ReferenceRanker.Classify(V(1, 0.1f, 0.1f, 0), Views()).Label);
    }

    [Fact]
    public void Relation_IsOnlyASuggestionWithCappedConfidence()
    {
        Assert.Equal(new ZeroShot("same_object", 0.5), ReferenceRanker.SuggestRelation(0.99));
        Assert.Equal("variant", ReferenceRanker.SuggestRelation(0.85).Label);
        Assert.Equal("similar_different", ReferenceRanker.SuggestRelation(0.5).Label);
        Assert.True(new[] { 0.99, 0.85, 0.5 }.All(c => ReferenceRanker.SuggestRelation(c).Confidence <= 0.5));
    }

    [Fact]
    public void Rank_SimilarToAcceptedPicturesCounts()
    {
        var accepted = Item(0, V(0, 0, 1), accepted: true);
        var like = Item(1, V(0, 0.1f, 1));
        var unlike = Item(2, V(1, 0, 0));
        var results = ReferenceRanker.Rank([accepted, like, unlike], new RankContext(null, null, null, null, null, 0.86));

        Assert.Contains(results[like.Id].Reasons, r => r.Code == "like_accepted");
        Assert.True(results[like.Id].Score > results[unlike.Id].Score);
        Assert.DoesNotContain(results[accepted.Id].Reasons, r => r.Code == "like_accepted"); // not compared with itself
    }
}

public class ModelEvaluatorTests
{
    private static ModelGeometry Geometry(bool closed = true, int triangles = 5000, int components = 1, string scale = "plausible", int degenerate = 0) =>
        new(triangles, components, closed, closed ? 0 : 40, 0, degenerate, 100, 200, 50, scale);

    [Fact]
    public void ListingOnly_HasLowConfidenceAndSaysSo()
    {
        var e = ModelEvaluator.Evaluate(new ModelFacts(false, true, 20_000, true, true, null, null, null));
        Assert.Equal(0.2, e.Confidence);
        Assert.Contains(e.Reasons, r => r.Code == "listing_only");
        Assert.Contains(e.Reasons, r => r.Code == "downloadable" && r.Effect == "+");
    }

    [Fact]
    public void NotDownloadableOrNoDerivatives_LowersTheScore()
    {
        var open = ModelEvaluator.Evaluate(new ModelFacts(false, true, 20_000, true, true, 0.85, null, null));
        var locked = ModelEvaluator.Evaluate(new ModelFacts(false, false, 20_000, false, true, 0.85, null, null));
        Assert.True(locked.Score < open.Score);
        Assert.Contains(locked.Reasons, r => r.Code == "not_downloadable");
        Assert.Contains(locked.Reasons, r => r.Code == "no_derivatives");
    }

    [Fact]
    public void FaceCount_FlagsCoarseAndDenseMeshes()
    {
        Assert.Contains(ModelEvaluator.Evaluate(new ModelFacts(false, true, 120, true, true, null, null, null)).Reasons, r => r.Code == "very_coarse");
        Assert.Contains(ModelEvaluator.Evaluate(new ModelFacts(false, true, 2_000_000, true, true, null, null, null)).Reasons, r => r.Code == "dense_mesh");
    }

    [Fact]
    public void Geometry_ClosedBeatsOpenAndScaleIsReported()
    {
        var closed = ModelEvaluator.Evaluate(new ModelFacts(true, null, null, true, true, 0.85, null, Geometry()));
        var open = ModelEvaluator.Evaluate(new ModelFacts(true, null, null, true, true, 0.85, null, Geometry(closed: false, degenerate: 500, components: 400)));
        Assert.True(closed.Score > open.Score);
        Assert.Equal(0.75, closed.Confidence);
        Assert.Contains(closed.Reasons, r => r.Code == "closed");
        Assert.Contains(open.Reasons, r => r.Code == "open");
        Assert.Contains(open.Reasons, r => r.Code == "degenerate");
        Assert.Contains(open.Reasons, r => r.Code == "fragmented");
        Assert.Contains(ModelEvaluator.Evaluate(new ModelFacts(true, null, null, null, false, null, null, Geometry(scale: "unitless"))).Reasons, r => r.Code == "scale_unitless");
        Assert.Contains(ModelEvaluator.Evaluate(new ModelFacts(true, null, null, null, false, null, null, Geometry(scale: "suspicious"))).Reasons, r => r.Code == "scale_suspicious");
    }

    [Fact]
    public void UnknownLicence_IsFlagged()
    {
        Assert.Contains(ModelEvaluator.Evaluate(new ModelFacts(false, true, 5000, null, false, null, null, null)).Reasons, r => r.Code == "licence_unknown");
    }
}
