using System.Text.Json.Nodes;
using ImageTo3D.Core.Geometry;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;

namespace ImageTo3D.Core.Tests;

public class GlbTransformTests
{
    [Fact]
    public void Transformer_ScalesAndTranslatesViaRootNode()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());

        var transformed = GlbTransformer.ApplyUniformTransform(glb, 0.5, new Vector3D(10, 0, -2));

        var bounds = MeshOperations.ComputeBounds(GlbReader.Read(transformed));
        Assert.Equal(new Vector3D(10, 0, -2), Round(bounds.Min));
        Assert.Equal(new Vector3D(10.5, 0.5, -1.5), Round(bounds.Max));
        Assert.True(MeshOperations.SignedVolume(GlbReader.Read(transformed)) > 0);
    }

    [Fact]
    public void Transformer_KeepsBinaryChunkAndMaterialsUntouched()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());
        var (beforeJson, beforeBin) = GlbContainer.Parse(glb);

        var transformed = GlbTransformer.ApplyUniformTransform(glb, 2, default, "note");
        var (afterJson, afterBin) = GlbContainer.Parse(transformed);

        // Texture images, UVs and normal maps live in the BIN chunk and the material/texture JSON;
        // both must be preserved exactly.
        Assert.Equal(glb.AsSpan(beforeBin!.Value.Offset, beforeBin.Value.Length).ToArray(), transformed.AsSpan(afterBin!.Value.Offset, beforeBin.Value.Length).ToArray());
        Assert.Equal(beforeJson["materials"]!.ToJsonString(), afterJson["materials"]!.ToJsonString());
        Assert.Equal(beforeJson["accessors"]!.ToJsonString(), afterJson["accessors"]!.ToJsonString());
        Assert.Equal("note", afterJson["asset"]!["extras"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void Transformer_OutputIsValidGltf()
    {
        var transformed = GlbTransformer.ApplyUniformTransform(GlbWriter.Write(GeometryTestData.UnitCube()), 0.001, new Vector3D(1, 2, 3));

        var model = ModelRoot.ParseGLB(new ArraySegment<byte>(transformed));

        var root = Assert.Single(model.DefaultScene.VisualChildren);
        Assert.Equal("imageto3d-root", root.Name);
        Assert.Single(root.VisualChildren);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Transformer_RejectsInvalidScale(double scale)
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());
        Assert.Throws<ArgumentOutOfRangeException>(() => GlbTransformer.ApplyUniformTransform(glb, scale, default));
    }

    [Fact]
    public void Reader_AppliesNodeMatrixAndTrs()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());
        var (json, bin) = GlbContainer.Parse(glb);

        // Parent: translate +5 in X via column-major matrix; child: TRS scale 2 + rotate 90 deg about Y.
        var half = (float)Math.Sqrt(0.5);
        json["nodes"] = new JsonArray(
            new JsonObject { ["matrix"] = new JsonArray(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 0, 0, 1), ["children"] = new JsonArray(1) },
            new JsonObject { ["mesh"] = 0, ["scale"] = new JsonArray(2, 2, 2), ["rotation"] = new JsonArray(0, half, 0, half) });
        json["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
        var rebuilt = GlbContainer.Assemble(json, glb.AsSpan(bin!.Value.Offset, bin.Value.Length));

        var mesh = GlbReader.Read(rebuilt);

        var bounds = MeshOperations.ComputeBounds(mesh);
        // Cube [0,1]^3 scaled to [0,2]^3, rotated +90 deg about Y: x' = z, z' = -x  => x in [0,2], z in [-2,0]; then +5 in X.
        Assert.Equal(new Vector3D(5, 0, -2), Round(bounds.Min));
        Assert.Equal(new Vector3D(7, 2, 0), Round(bounds.Max));
        Assert.Equal(8.0, MeshOperations.SignedVolume(mesh), 4);
    }

    [Fact]
    public void Reader_FlipsWindingForMirroringNodes()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());
        var (json, bin) = GlbContainer.Parse(glb);
        json["nodes"]![0]!["scale"] = new JsonArray(-1, 1, 1);
        var rebuilt = GlbContainer.Assemble(json, glb.AsSpan(bin!.Value.Offset, bin.Value.Length));

        Assert.True(MeshOperations.SignedVolume(GlbReader.Read(rebuilt)) > 0);
    }

    private static Vector3D Round(Vector3D v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));
}
