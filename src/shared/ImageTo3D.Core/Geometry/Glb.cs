using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Minimal glTF 2.0 binary (GLB) writer: one mesh, one primitive with POSITION, NORMAL,
/// optional COLOR_0 and uint32 indices. glTF positions are defined in metres.
/// </summary>
public static class GlbWriter
{
    private const uint Magic = 0x46546C67; // "glTF"
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    public static byte[] Write(Mesh mesh, string? generatorNote = null)
    {
        var validation = MeshValidator.Validate(mesh);
        if (!validation.IsValid)
            throw new InvalidOperationException("Cannot write GLB: " + string.Join(" ", validation.Errors));

        var normals = MeshOperations.ComputeVertexNormals(mesh);
        var vertexCount = mesh.VertexCount;

        using var bin = new MemoryStream();
        var positionsOffset = AppendFloats(bin, mesh.Positions);
        var normalsOffset = AppendFloats(bin, normals);

        long colorsOffset = -1;
        if (mesh.Colors is not null)
        {
            // COLOR_0 is linear per the glTF spec; mesh colours are sRGB, so convert.
            // Unsigned short keeps precision in dark tones after linearisation.
            colorsOffset = bin.Position;
            Span<byte> buffer = stackalloc byte[8];
            for (var v = 0; v < vertexCount; v++)
            {
                for (var k = 0; k < 3; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer[(k * 2)..], ColorSpace.SrgbByteToLinearUShort(mesh.Colors[v * 3 + k]));
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], ushort.MaxValue);
                bin.Write(buffer);
            }
        }

        var indicesOffset = bin.Position;
        Span<byte> intBuffer = stackalloc byte[4];
        foreach (var index in mesh.Indices)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)index);
            bin.Write(intBuffer);
        }

        Pad(bin, 0);
        var binLength = bin.Length;

        var bounds = validation.Bounds;
        var bufferViews = new JsonArray
        {
            BufferView(positionsOffset, vertexCount * 12L, 34962),
            BufferView(normalsOffset, vertexCount * 12L, 34962),
        };
        var accessors = new JsonArray
        {
            new JsonObject
            {
                ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = vertexCount, ["type"] = "VEC3",
                ["min"] = new JsonArray((float)bounds.Min.X, (float)bounds.Min.Y, (float)bounds.Min.Z),
                ["max"] = new JsonArray((float)bounds.Max.X, (float)bounds.Max.Y, (float)bounds.Max.Z),
            },
            new JsonObject { ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = vertexCount, ["type"] = "VEC3" },
        };
        var attributes = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1 };

        if (colorsOffset >= 0)
        {
            bufferViews.Add(BufferView(colorsOffset, vertexCount * 8L, 34962));
            accessors.Add(new JsonObject
            {
                ["bufferView"] = bufferViews.Count - 1, ["componentType"] = 5123, ["normalized"] = true,
                ["count"] = vertexCount, ["type"] = "VEC4",
            });
            attributes["COLOR_0"] = accessors.Count - 1;
        }

        bufferViews.Add(BufferView(indicesOffset, mesh.Indices.Length * 4L, 34963));
        accessors.Add(new JsonObject
        {
            ["bufferView"] = bufferViews.Count - 1, ["componentType"] = 5125, ["count"] = mesh.Indices.Length, ["type"] = "SCALAR",
        });
        var indicesAccessor = accessors.Count - 1;

        var asset = new JsonObject { ["version"] = "2.0", ["generator"] = "ImageTo3D" };
        if (generatorNote is not null)
            asset["extras"] = new JsonObject { ["note"] = generatorNote };

        var root = new JsonObject
        {
            ["asset"] = asset,
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0, ["name"] = "model" }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "model",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes, ["indices"] = indicesAccessor, ["material"] = 0, ["mode"] = 4,
                }),
            }),
            ["materials"] = new JsonArray(new JsonObject
            {
                ["name"] = "default",
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorFactor"] = new JsonArray(1, 1, 1, 1), ["metallicFactor"] = 0, ["roughnessFactor"] = 0.8,
                },
                ["doubleSided"] = false,
            }),
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binLength }),
        };

        var jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        var jsonPadded = (jsonBytes.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + 8 + binLength;

        var output = new byte[total];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ChunkJson);
        jsonBytes.CopyTo(span[20..]);
        span.Slice(20 + jsonBytes.Length, jsonPadded - jsonBytes.Length).Fill((byte)' ');
        var binStart = 20 + jsonPadded;
        BinaryPrimitives.WriteUInt32LittleEndian(span[binStart..], (uint)binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(binStart + 4)..], ChunkBin);
        bin.GetBuffer().AsSpan(0, (int)binLength).CopyTo(span[(binStart + 8)..]);
        return output;
    }

    private static long AppendFloats(MemoryStream stream, float[] values)
    {
        var offset = stream.Position;
        Span<byte> buffer = stackalloc byte[4];
        foreach (var v in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer, v);
            stream.Write(buffer);
        }

        return offset;
    }

    private static JsonObject BufferView(long offset, long length, int target) =>
        new() { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length, ["target"] = target };

    private static void Pad(Stream stream, byte value)
    {
        while (stream.Position % 4 != 0)
            stream.WriteByte(value);
    }
}

/// <param name="NodeIndex">glTF node index, or -1 when the file has no scene graph.</param>
/// <param name="Name">Node name, else mesh name; null when neither is set.</param>
public sealed record GlbNodeMesh(int NodeIndex, string? Name, Mesh Mesh);

/// <summary>
/// Reads triangle geometry from a GLB file. Supports the subset used by common exporters:
/// triangle-list primitives with float POSITION, optional COLOR_0 (float or normalized
/// ubyte/ushort) and optional ubyte/ushort/uint indices. Node transforms are ignored.
/// </summary>
public static class GlbReader
{
    public static Mesh Read(ReadOnlySpan<byte> data)
    {
        var (json, binRange) = GlbContainer.Parse(data);
        var bin = binRange is { } r ? data.Slice(r.Offset, r.Length) : default;
        return Build(json, bin, MeshInstances(json));
    }

    /// <summary>
    /// One mesh per scene node that references a mesh (world transform applied), in scene-graph
    /// order. Files without a scene graph yield one entry per mesh with <see cref="GlbNodeMesh.NodeIndex"/> -1.
    /// </summary>
    public static IReadOnlyList<GlbNodeMesh> ReadNodes(ReadOnlySpan<byte> data)
    {
        var (json, binRange) = GlbContainer.Parse(data);
        var bin = binRange is { } r ? data.Slice(r.Offset, r.Length) : default;
        var result = new List<GlbNodeMesh>();
        foreach (var instance in MeshInstances(json))
        {
            var name = instance.Node >= 0
                ? json["nodes"]?[instance.Node]?["name"]?.GetValue<string>()
                : null;
            name ??= json["meshes"]?[instance.Mesh]?["name"]?.GetValue<string>();
            result.Add(new GlbNodeMesh(instance.Node, name, Build(json, bin, [instance])));
        }

        return result;
    }

    private static Mesh Build(JsonNode json, ReadOnlySpan<byte> bin, List<(int Node, int Mesh, Matrix4x4 World)> instances)
    {
        var positions = new List<float>();
        var colors = new List<byte>();
        var indices = new List<int>();
        var allHaveColors = true;

        foreach (var (_, meshIndex, world) in instances)
        {
            var meshNode = json["meshes"]?[meshIndex] ?? throw new InvalidDataException($"Mesh {meshIndex} missing.");
            var flip = world.GetDeterminant() < 0;
            foreach (var primitive in meshNode["primitives"]?.AsArray() ?? [])
            {
                var mode = primitive?["mode"]?.GetValue<int>() ?? 4;
                if (mode != 4)
                    throw new InvalidDataException("Only triangle-list primitives are supported.");
                var attributes = primitive!["attributes"] ?? throw new InvalidDataException("Primitive has no attributes.");
                var positionAccessor = attributes["POSITION"]?.GetValue<int>() ?? throw new InvalidDataException("Primitive has no POSITION.");

                var baseVertex = positions.Count / 3;
                var primitivePositions = ReadAccessor(json, bin, positionAccessor, out var posComponents);
                if (posComponents != 3)
                    throw new InvalidDataException("POSITION must be VEC3.");
                for (var i = 0; i < primitivePositions.Length; i += 3)
                {
                    var p = Vector3.Transform(new Vector3(primitivePositions[i], primitivePositions[i + 1], primitivePositions[i + 2]), world);
                    positions.Add(p.X);
                    positions.Add(p.Y);
                    positions.Add(p.Z);
                }

                var vertexCount = primitivePositions.Length / 3;

                if (attributes["COLOR_0"] is { } colorNode)
                {
                    var c = ReadAccessor(json, bin, colorNode.GetValue<int>(), out var colorComponents);
                    for (var v = 0; v < vertexCount; v++)
                        for (var k = 0; k < 3; k++)
                            colors.Add(ColorSpace.LinearToSrgbByte(c[v * colorComponents + k]));
                }
                else
                {
                    allHaveColors = false;
                }

                var primitiveIndices = primitive["indices"] is { } indicesNode
                    ? ReadAccessor(json, bin, indicesNode.GetValue<int>(), out _).Select(i => (int)i).ToArray()
                    : Enumerable.Range(0, vertexCount).ToArray();
                for (var t = 0; t + 2 < primitiveIndices.Length; t += 3)
                {
                    // A mirroring node transform reverses orientation; swap to keep faces outward.
                    indices.Add(baseVertex + primitiveIndices[t]);
                    indices.Add(baseVertex + primitiveIndices[flip ? t + 2 : t + 1]);
                    indices.Add(baseVertex + primitiveIndices[flip ? t + 1 : t + 2]);
                }
            }
        }

        return new Mesh(positions.ToArray(), indices.ToArray(), allHaveColors && colors.Count == positions.Count ? colors.ToArray() : null);
    }

    /// <summary>
    /// Mesh references with their world transforms, walking the default scene graph. Files without
    /// scenes/nodes fall back to every mesh with an identity transform.
    /// </summary>
    private static List<(int Node, int Mesh, Matrix4x4 World)> MeshInstances(JsonNode json)
    {
        var result = new List<(int, int, Matrix4x4)>();
        var nodes = json["nodes"]?.AsArray();
        var sceneIndex = json["scene"]?.GetValue<int>() ?? 0;
        var roots = json["scenes"]?[sceneIndex]?["nodes"]?.AsArray();

        if (nodes is null || roots is null)
        {
            var count = json["meshes"]?.AsArray().Count ?? 0;
            for (var i = 0; i < count; i++)
                result.Add((-1, i, Matrix4x4.Identity));
            return result;
        }

        var visiting = new HashSet<int>();
        void Visit(int index, Matrix4x4 parent)
        {
            if (!visiting.Add(index))
                throw new InvalidDataException("GLB node graph contains a cycle.");
            var node = nodes[index] ?? throw new InvalidDataException($"Node {index} missing.");
            var world = LocalMatrix(node) * parent; // System.Numerics uses row vectors: child first, then parent
            if (node["mesh"] is { } mesh)
                result.Add((index, mesh.GetValue<int>(), world));
            foreach (var child in node["children"]?.AsArray() ?? [])
                Visit(child!.GetValue<int>(), world);
            visiting.Remove(index);
        }

        foreach (var root in roots)
            Visit(root!.GetValue<int>(), Matrix4x4.Identity);
        return result;
    }

    private static Matrix4x4 LocalMatrix(JsonNode node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            // glTF stores column-major matrices for column vectors; that is exactly the
            // row-major layout System.Numerics expects for its row-vector convention.
            var v = m.Select(x => (float)x!.GetValue<double>()).ToArray();
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        var scale = node["scale"] is JsonArray s ? new Vector3(F(s, 0), F(s, 1), F(s, 2)) : Vector3.One;
        var rotation = node["rotation"] is JsonArray r ? new Quaternion(F(r, 0), F(r, 1), F(r, 2), F(r, 3)) : Quaternion.Identity;
        var translation = node["translation"] is JsonArray t ? new Vector3(F(t, 0), F(t, 1), F(t, 2)) : Vector3.Zero;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

        static float F(JsonArray a, int i) => (float)a[i]!.GetValue<double>();
    }

    private static float[] ReadAccessor(JsonNode json, ReadOnlySpan<byte> bin, int accessorIndex, out int components)
    {
        var accessor = json["accessors"]?[accessorIndex] ?? throw new InvalidDataException($"Accessor {accessorIndex} missing.");
        var count = accessor["count"]!.GetValue<int>();
        var componentType = accessor["componentType"]!.GetValue<int>();
        var normalized = accessor["normalized"]?.GetValue<bool>() ?? false;
        components = accessor["type"]!.GetValue<string>() switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            var t => throw new InvalidDataException($"Unsupported accessor type {t}."),
        };
        var componentSize = componentType switch
        {
            5126 or 5125 => 4,
            5123 or 5122 => 2,
            5121 or 5120 => 1,
            _ => throw new InvalidDataException($"Unsupported component type {componentType}."),
        };

        var viewIndex = accessor["bufferView"]?.GetValue<int>() ?? throw new InvalidDataException("Sparse/empty accessors are not supported.");
        var view = json["bufferViews"]![viewIndex]!;
        if ((view["buffer"]?.GetValue<int>() ?? 0) != 0)
            throw new InvalidDataException("Only the embedded GLB buffer is supported.");
        var viewOffset = view["byteOffset"]?.GetValue<long>() ?? 0;
        var viewLength = view["byteLength"]!.GetValue<long>();
        var accessorOffset = accessor["byteOffset"]?.GetValue<long>() ?? 0;
        var elementSize = componentSize * components;
        var stride = view["byteStride"]?.GetValue<int>() ?? elementSize;

        if (count > 0 && accessorOffset + (long)(count - 1) * stride + elementSize > viewLength)
            throw new InvalidDataException("Accessor exceeds its buffer view.");
        if (viewOffset + viewLength > bin.Length)
            throw new InvalidDataException("Buffer view exceeds the binary chunk.");

        var result = new float[count * components];
        for (var i = 0; i < count; i++)
        {
            var element = bin.Slice((int)(viewOffset + accessorOffset + (long)i * stride), elementSize);
            for (var k = 0; k < components; k++)
            {
                var c = element[(k * componentSize)..];
                result[i * components + k] = componentType switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(c),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(c),
                    5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(c) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(c),
                    5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(c) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(c),
                    5121 => normalized ? c[0] / 255f : c[0],
                    _ => normalized ? Math.Max((sbyte)c[0] / 127f, -1f) : (sbyte)c[0],
                };
            }
        }

        return result;
    }
}

public static class ColorSpace
{
    public static ushort SrgbByteToLinearUShort(byte srgb)
    {
        var c = srgb / 255.0;
        var linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return (ushort)Math.Round(linear * 65535.0);
    }

    public static byte LinearToSrgbByte(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var srgb = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255.0);
    }
}
