using ImageTo3D.Api.Configuration;
using ImageTo3D.Api.Services.Embeddings;
using ImageTo3D.Api.Services.References;
using ImageTo3D.Core.Embeddings;
using ImageTo3D.Core.Imaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Tests;

/// <summary>Runs only when models/clip holds the CLIP ONNX files and samples/ the example images.</summary>
public sealed class RequiresClipFactAttribute : FactAttribute
{
    public RequiresClipFactAttribute()
    {
        if (ClipRealModelTests.FindRepoRoot() is null)
            Skip = "CLIP model files not found in models/clip (run scripts/download-models.ps1 -Embeddings).";
    }
}

[Trait("Category", "RealModel")]
public class ClipRealModelTests
{
    public static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "models", "clip", "vision_model_quantized.onnx"))
                && File.Exists(Path.Combine(dir.FullName, "models", "clip", "text_model_quantized.onnx"))
                && File.Exists(Path.Combine(dir.FullName, "samples", "chair.png")))
                return dir.FullName;
        }

        return null;
    }

    private static async Task<ClipEmbedder> LoadAsync(string root)
    {
        var embedder = new ClipEmbedder(
            Options.Create(new EmbeddingOptions { ModelDirectory = Path.Combine(root, "models", "clip") }),
            new HostingEnvironment { ContentRootPath = root },
            NullLogger<ClipEmbedder>.Instance);
        Assert.True(await embedder.EnsureReadyAsync(default), embedder.Status.Reason);
        return embedder;
    }

    private static RgbaImage Sample(string root, string name) => ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(root, "samples", name))).Image;

    [RequiresClipFact]
    public async Task Embeddings_MatchTheRightCaptionAndImage()
    {
        var root = FindRepoRoot()!;
        using var clip = await LoadAsync(root);
        var chair = clip.EmbedImage(Sample(root, "chair.png"), default);
        var robot = clip.EmbedImage(Sample(root, "robot.png"), default);
        var chairText = clip.EmbedText("a photo of a chair.", default);
        var robotText = clip.EmbedText("a photo of a robot.", default);

        Assert.Equal(512, chair.Length);
        Assert.Equal(1, Math.Sqrt(chair.Sum(v => (double)v * v)), 3); // normalised
        Assert.True(EmbeddingMath.Cosine(chair, chairText) > EmbeddingMath.Cosine(chair, robotText), "chair image should match the chair caption");
        Assert.True(EmbeddingMath.Cosine(robot, robotText) > EmbeddingMath.Cosine(robot, chairText), "robot image should match the robot caption");
        // Typical CLIP ViT-B/32 cosines: matching caption 0.2-0.35, the same picture re-encoded close to 1.
        Assert.InRange(EmbeddingMath.Cosine(chair, chairText), 0.18, 0.4);
        Assert.True(EmbeddingMath.Cosine(chair, robot) < 0.9);
    }

    [RequiresClipFact]
    public async Task Embeddings_AreStableUnderResizingAndJpeg()
    {
        var root = FindRepoRoot()!;
        using var clip = await LoadAsync(root);
        var original = Sample(root, "robot.png");
        var thumbnail = ImageDecoder.Decode(ImageThumbnail.Create(original, 300).Jpeg).Image;

        var similarity = EmbeddingMath.Cosine(clip.EmbedImage(original, default), clip.EmbedImage(thumbnail, default));
        Assert.True(similarity > 0.9, $"similarity {similarity:0.000}");
    }

    [RequiresClipFact]
    public async Task ZeroShot_RecognisesAPhotoOverADrawing()
    {
        var root = FindRepoRoot()!;
        using var clip = await LoadAsync(root);
        var kinds = new PromptSet(
        [
            ("render", EmbeddingMath.Mean([clip.EmbedText("a 3D render of a chair.", default), clip.EmbedText("a CGI rendering of a chair.", default)])),
            ("drawing", EmbeddingMath.Mean([clip.EmbedText("a technical drawing of a chair.", default), clip.EmbedText("a blueprint of a chair.", default)])),
        ]);

        // The TripoSR example chair is a clean product render on white, not a blueprint.
        var result = ReferenceRanker.Classify(clip.EmbedImage(Sample(root, "chair.png"), default), kinds);
        Assert.Equal("render", result.Label);
    }

    [Fact]
    public async Task MissingFiles_AreReportedWithoutPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imageto3d-tests", Guid.NewGuid().ToString("N"));
        using var embedder = new ClipEmbedder(
            Options.Create(new EmbeddingOptions { ModelDirectory = dir }),
            new HostingEnvironment { ContentRootPath = dir },
            NullLogger<ClipEmbedder>.Instance);

        Assert.False(await embedder.EnsureReadyAsync(default));
        Assert.False(embedder.Status.Available);
        Assert.Contains("vision_model_quantized.onnx", embedder.Status.Reason);
        Assert.DoesNotContain(dir, embedder.Status.Reason);
        Assert.Throws<InvalidOperationException>(() => embedder.EmbedText("x", default));
    }

    [Fact]
    public async Task Disabled_IsReportedAndNothingIsLoaded()
    {
        using var embedder = new ClipEmbedder(
            Options.Create(new EmbeddingOptions { Enabled = false }),
            new HostingEnvironment { ContentRootPath = Path.GetTempPath() },
            NullLogger<ClipEmbedder>.Instance);

        Assert.False(await embedder.EnsureReadyAsync(default));
        Assert.Contains("Embeddings:Enabled", embedder.Status.Reason);
    }
}
