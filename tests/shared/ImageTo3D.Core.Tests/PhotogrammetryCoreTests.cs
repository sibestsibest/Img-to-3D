using System.Text;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.Imaging;
using ImageTo3D.Core.Reconstruction;
using SkiaSharp;

namespace ImageTo3D.Core.Tests;

public class ObjReaderTests
{
    private static Mesh Read(string obj) => ObjReader.Read(Encoding.UTF8.GetBytes(obj));

    [Fact]
    public void ReadsTrianglesAndQuadsWithTextureAndNormalIndices()
    {
        var mesh = Read("""
            # Meshroom-like output
            mtllib texturedMesh.mtl
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            vn 0 0 1
            usemtl material_0
            f 1/1/1 2/1/1 3/1/1 4/1/1
            f 1//1 3//1 4//1
            """);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(3, mesh.TriangleCount); // quad -> 2, triangle -> 1
        Assert.Equal([0, 1, 2, 0, 2, 3, 0, 2, 3], mesh.Indices);
        Assert.Null(mesh.Colors);
    }

    [Fact]
    public void NegativeIndicesAreRelativeToTheLastVertex()
    {
        var mesh = Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\n");
        Assert.Equal([0, 1, 2], mesh.Indices);
    }

    [Fact]
    public void VertexColoursAreKeptWhenEveryVertexHasThem()
    {
        var mesh = Read("v 0 0 0 1 0 0\nv 1 0 0 0 1 0\nv 0 1 0 0 0 0.5\nf 1 2 3\n");
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 128 }, mesh.Colors);
        Assert.Null(Read("v 0 0 0 1 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n").Colors);
    }

    [Theory]
    [InlineData("v 0 0 0\nv 1 0 0\nf 1 2 5\n", "does not exist")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2\n", "at least three")]
    [InlineData("v 0 0\n", "three coordinates")]
    [InlineData("v 0 nan 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n", "not a number")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 0 3\n", "invalid face index")]
    [InlineData("v 0 0 0\n", "no faces")]
    [InlineData("", "no faces")]
    public void BrokenFilesAreRefusedWithTheLine(string obj, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Read(obj));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        var obj = Encoding.UTF8.GetBytes("v 0 0 0\nv 1 0 0\nv 0 1 0\nv 1 1 0\nf 1 2 3\nf 2 4 3\n");
        Assert.Throws<InvalidDataException>(() => ObjReader.Read(obj, maxVertices: 3));
        Assert.Throws<InvalidDataException>(() => ObjReader.Read(obj, maxTriangles: 1));
    }

    [Fact]
    public void RoundTripsAClosedCubeFromTheWriterConventions()
    {
        var sb = new StringBuilder();
        var cube = GeometryTestData.UnitCube();
        for (var i = 0; i < cube.VertexCount; i++)
            sb.Append(FormattableString.Invariant($"v {cube.Positions[i * 3]} {cube.Positions[i * 3 + 1]} {cube.Positions[i * 3 + 2]}\n"));
        for (var t = 0; t < cube.TriangleCount; t++)
            sb.Append($"f {cube.Indices[t * 3] + 1} {cube.Indices[t * 3 + 1] + 1} {cube.Indices[t * 3 + 2] + 1}\n");
        var mesh = Read(sb.ToString());
        Assert.True(MeshTopology.Analyze(MeshTopology.Weld(mesh, 1e-6)).IsClosed);
        Assert.Equal(MeshOperations.SignedVolume(cube), MeshOperations.SignedVolume(mesh), 5);
    }
}

public class PhotoInspectorTests
{
    /// <summary>A checkerboard (sharp) or a smooth gradient (no detail), optionally blurred.</summary>
    internal static byte[] Photo(int w, int h, bool sharp = true, float blur = 0, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int seed = 1)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Gray);
            var rng = new Random(seed);
            using var paint = new SKPaint { IsAntialias = false };
            var cell = Math.Max(4, w / 64);
            for (var y = 0; y < h; y += cell)
            {
                for (var x = 0; x < w; x += cell)
                {
                    var v = (byte)(sharp ? rng.Next(256) : 128 + 64 * x / w);
                    paint.Color = new SKColor(v, v, v);
                    canvas.DrawRect(x, y, cell, cell, paint);
                }
            }
        }

        SKBitmap output = bitmap;
        if (blur > 0)
        {
            output = new SKBitmap(w, h);
            using var canvas = new SKCanvas(output);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
            canvas.DrawBitmap(bitmap, 0, 0, paint);
        }

        using var data = output.Encode(format, 92);
        if (!ReferenceEquals(output, bitmap))
            output.Dispose();
        return data.ToArray();
    }

    private static readonly ImageLimits Limits = new(64, 12000, 60_000_000);

    [Fact]
    public void ReportsSizeFormatAndSharpnessFromAReducedDecode()
    {
        var facts = PhotoInspector.Inspect(Photo(3000, 2000), Limits);
        Assert.Equal((ImageFormatKind.Jpeg, 3000, 2000), (facts.Format, facts.Width, facts.Height));
        Assert.False(facts.HasExif); // Skia writes no EXIF
        Assert.True(facts.Sharpness > PhotoSetCheck.BlurryBelow, facts.Sharpness.ToString());
    }

    [Fact]
    public void BlurLowersTheSharpness()
    {
        var sharp = PhotoInspector.Inspect(Photo(1600, 1200), Limits).Sharpness;
        var blurred = PhotoInspector.Inspect(Photo(1600, 1200, blur: 12), Limits).Sharpness;
        var flat = PhotoInspector.Inspect(Photo(1600, 1200, sharp: false), Limits).Sharpness;
        Assert.True(blurred < sharp * PhotoSetCheck.RelativeBlur, $"{blurred} vs {sharp}");
        Assert.True(flat < PhotoSetCheck.BlurryBelow, flat.ToString());
    }

    [Fact]
    public void RefusesOtherFormatsAndOversizedPhotos()
    {
        Assert.Throws<ImageValidationException>(() => PhotoInspector.Inspect(Photo(200, 200, format: SKEncodedImageFormat.Webp), Limits));
        Assert.Throws<ImageValidationException>(() => PhotoInspector.Inspect(Photo(400, 300), new ImageLimits(64, 300, 1_000_000)));
        Assert.Throws<ImageValidationException>(() => PhotoInspector.Inspect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0], Limits));
        Assert.Equal(ImageFormatKind.Png, PhotoInspector.Inspect(Photo(300, 200, format: SKEncodedImageFormat.Png), Limits).Format);
    }

    [Fact]
    public void DetectsAnExifSegment()
    {
        var jpeg = Photo(200, 200);
        Assert.False(PhotoInspector.HasExifBlock(jpeg));
        // Insert an APP1 "Exif" segment right after SOI.
        byte[] app1 = [0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        var withExif = jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
        Assert.True(PhotoInspector.HasExifBlock(withExif));
    }
}

public class PhotoSetCheckTests
{
    private static PhotoFacts Facts(int w = 4000, int h = 3000, bool exif = true, ulong hash = 0x0F0F_3C3C_5A5A_A5A5, double sharpness = 200) =>
        new(ImageFormatKind.Jpeg, w, h, exif, hash, sharpness);

    private static ulong Hash(int i) => 0x0123_4567_89AB_CDEFUL * (ulong)(i + 3) ^ ((ulong)i << 40);

    [Fact]
    public void TooFewPhotosBlockAndFewPhotosWarn()
    {
        Assert.Contains(PhotoSetCheck.Check([Facts(), Facts(hash: Hash(1))]), f => f.Code == "too_few_photos" && f.Severity == "blocker");
        var five = Enumerable.Range(0, 5).Select(i => Facts(hash: Hash(i))).ToList();
        var findings = PhotoSetCheck.Check(five);
        Assert.DoesNotContain(findings, f => f.Severity == "blocker");
        Assert.Contains(findings, f => f.Code == "few_photos");
        Assert.DoesNotContain(PhotoSetCheck.Check(Enumerable.Range(0, 20).Select(i => Facts(hash: Hash(i))).ToList()), f => f.Code == "few_photos");
    }

    [Fact]
    public void FlagsLowResolutionBlurAndDuplicatesPerPhoto()
    {
        var photos = new List<PhotoFacts>
        {
            Facts(hash: Hash(0)),
            Facts(800, 600, hash: Hash(1)),
            Facts(hash: Hash(2), sharpness: 5),
            Facts(hash: Hash(3), sharpness: 40),
            Facts(hash: Hash(0) ^ 1),
            Facts(hash: Hash(5)),
        };
        var findings = PhotoSetCheck.Check(photos);
        Assert.Contains(findings, f => f is { Code: "low_resolution", Photo: 1 });
        Assert.Contains(findings, f => f is { Code: "blurry", Photo: 2 });
        Assert.Contains(findings, f => f is { Code: "blurrier_than_others", Photo: 3 });
        Assert.Contains(findings, f => f is { Code: "near_duplicate", Photo: 4 } && f.Message.Contains("photo 1"));
        Assert.Contains(findings, f => f.Code == "mixed_sizes");
        Assert.DoesNotContain(findings, f => f.Code == "no_camera_metadata");
    }

    [Fact]
    public void FeaturelessHashesAreNotCalledDuplicates()
    {
        var findings = PhotoSetCheck.Check([Facts(hash: 0), Facts(hash: 0), Facts(hash: 1)]);
        Assert.DoesNotContain(findings, f => f.Code == "near_duplicate");
    }

    [Fact]
    public void MissingExifIsReportedForTheSet()
    {
        var findings = PhotoSetCheck.Check(Enumerable.Range(0, 3).Select(i => Facts(exif: false, hash: Hash(i))).ToList());
        Assert.Contains(findings, f => f is { Code: "no_camera_metadata", Photo: null });
        Assert.Contains(findings, f => f.Code == "surface_limits");
    }
}
