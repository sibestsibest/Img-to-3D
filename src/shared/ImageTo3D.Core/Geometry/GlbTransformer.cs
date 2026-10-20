using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace ImageTo3D.Core.Geometry;

/// <summary>GLB chunk parsing/assembly shared by the reader and the transformer.</summary>
public static class GlbContainer
{
    private const uint Magic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    public static (JsonNode Json, (int Offset, int Length)? Bin) Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new InvalidDataException("Not a GLB file.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 2)
            throw new InvalidDataException("Unsupported glTF version.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) > data.Length)
            throw new InvalidDataException("GLB is truncated.");

        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkJson || jsonLength < 0 || 20 + jsonLength > data.Length)
            throw new InvalidDataException("GLB JSON chunk missing.");
        var json = JsonNode.Parse(data.Slice(20, jsonLength).ToArray()) ?? throw new InvalidDataException("Empty GLB JSON.");

        (int, int)? bin = null;
        var binHeader = 20 + jsonLength;
        if (binHeader + 8 <= data.Length)
        {
            var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[binHeader..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(binHeader + 4)..]) == ChunkBin && binLength >= 0 && binHeader + 8 + binLength <= data.Length)
                bin = (binHeader + 8, binLength);
        }

        return (json, bin);
    }

    public static byte[] Assemble(JsonNode json, ReadOnlySpan<byte> bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var binPadded = (bin.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + (bin.IsEmpty ? 0 : 8 + binPadded);

        var output = new byte[total];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ChunkJson);
        jsonBytes.CopyTo(span[20..]);
        span.Slice(20 + jsonBytes.Length, jsonPadded - jsonBytes.Length).Fill((byte)' ');
        if (!bin.IsEmpty)
        {
            var binStart = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(span[binStart..], (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(binStart + 4)..], ChunkBin);
            bin.CopyTo(span[(binStart + 8)..]); // padding bytes stay zero
        }

        return output;
    }
}

/// <summary>
/// Applies p' = scale * p + translation to a whole GLB by inserting a new root node, leaving
/// meshes, UVs, textures, normal maps and materials byte-for-byte untouched. Used to scale
/// textured models (e.g. Stable Fast 3D output) without re-encoding them.
/// </summary>
public static class GlbTransformer
{
    public static byte[] ApplyUniformTransform(ReadOnlySpan<byte> glb, double scale, Vector3D translation, string? note = null)
    {
        if (!(scale > 0) || !double.IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be a positive finite number.");

        var (json, binRange) = GlbContainer.Parse(glb);
        var bin = binRange is { } r ? glb.Slice(r.Offset, r.Length) : default;

        var nodes = json["nodes"] as JsonArray ?? new JsonArray();
        json["nodes"] = nodes;
        var scenes = json["scenes"] as JsonArray;
        var sceneIndex = json["scene"]?.GetValue<int>() ?? 0;

        JsonArray roots;
        if (scenes is null || scenes.Count == 0)
        {
            // No scene graph: instance every mesh under the new root.
            roots = new JsonArray();
            var meshCount = json["meshes"]?.AsArray().Count ?? 0;
            for (var i = 0; i < meshCount; i++)
            {
                nodes.Add(new JsonObject { ["mesh"] = i });
                roots.Add(nodes.Count - 1);
            }

            scenes = new JsonArray(new JsonObject());
            json["scenes"] = scenes;
            sceneIndex = 0;
        }
        else
        {
            roots = scenes[sceneIndex]?["nodes"]?.DeepClone() as JsonArray ?? new JsonArray();
        }

        nodes.Add(new JsonObject
        {
            ["name"] = "imageto3d-root",
            ["scale"] = new JsonArray(scale, scale, scale),
            ["translation"] = new JsonArray(translation.X, translation.Y, translation.Z),
            ["children"] = roots,
        });
        scenes[sceneIndex]!["nodes"] = new JsonArray(nodes.Count - 1);
        json["scene"] = sceneIndex;

        if (note is not null)
        {
            var asset = json["asset"] as JsonObject ?? new JsonObject { ["version"] = "2.0" };
            json["asset"] = asset;
            asset["extras"] = new JsonObject { ["note"] = note };
        }

        return GlbContainer.Assemble(json, bin);
    }
}
