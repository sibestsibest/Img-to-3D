using System.Buffers.Binary;
using System.Text;

namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Binary STL. STL has no unit field; by convention slicers interpret coordinates as millimetres.
/// The header records the unit we intended so the file is self-describing for humans.
/// </summary>
public static class StlWriter
{
    public static byte[] Write(Mesh mesh, string unitLabel = "mm")
    {
        var validation = MeshValidator.Validate(mesh);
        if (!validation.IsValid)
            throw new InvalidOperationException("Cannot write STL: " + string.Join(" ", validation.Errors));

        var p = mesh.Positions;
        var idx = mesh.Indices;

        // Degenerate triangles are dropped: they carry no geometry and some slicers warn on them.
        var triangles = new List<int>(mesh.TriangleCount);
        for (var t = 0; t < idx.Length; t += 3)
        {
            if (Normal(p, idx[t], idx[t + 1], idx[t + 2], out _, out _, out _))
                triangles.Add(t);
        }

        var output = new byte[84 + triangles.Count * 50];
        var span = output.AsSpan();

        // Must not begin with "solid", or some readers will treat the file as ASCII STL.
        var header = Encoding.ASCII.GetBytes($"ImageTo3D binary STL; units={unitLabel}; AI-generated estimate");
        header.AsSpan(0, Math.Min(header.Length, 80)).CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], (uint)triangles.Count);

        var offset = 84;
        foreach (var t in triangles)
        {
            Normal(p, idx[t], idx[t + 1], idx[t + 2], out var nx, out var ny, out var nz);
            WriteVec(span[offset..], nx, ny, nz);
            for (var k = 0; k < 3; k++)
            {
                var v = idx[t + k] * 3;
                WriteVec(span[(offset + 12 + k * 12)..], p[v], p[v + 1], p[v + 2]);
            }

            // Attribute byte count stays zero (no colour extension).
            offset += 50;
        }

        return output;
    }

    private static void WriteVec(Span<byte> span, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span, x);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], y);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], z);
    }

    private static bool Normal(float[] p, int a, int b, int c, out float nx, out float ny, out float nz)
    {
        a *= 3;
        b *= 3;
        c *= 3;
        double e1x = p[b] - p[a], e1y = p[b + 1] - p[a + 1], e1z = p[b + 2] - p[a + 2];
        double e2x = p[c] - p[a], e2y = p[c + 1] - p[a + 1], e2z = p[c + 2] - p[a + 2];
        var x = e1y * e2z - e1z * e2y;
        var y = e1z * e2x - e1x * e2z;
        var z = e1x * e2y - e1y * e2x;
        var len = Math.Sqrt(x * x + y * y + z * z);
        if (!(len > 0))
        {
            nx = ny = nz = 0;
            return false;
        }

        nx = (float)(x / len);
        ny = (float)(y / len);
        nz = (float)(z / len);
        return true;
    }
}

public static class StlReader
{
    /// <summary>Parses a binary STL into an unwelded triangle soup.</summary>
    public static Mesh ReadBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < 84)
            throw new InvalidDataException("STL is too short.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        if (data.Length != 84 + (long)count * 50)
            throw new InvalidDataException($"Binary STL size mismatch: header declares {count} triangles but file has {data.Length} bytes.");

        var positions = new float[count * 9];
        var indices = new int[count * 3];
        for (var t = 0; t < count; t++)
        {
            var tri = data.Slice(84 + t * 50 + 12, 36);
            for (var k = 0; k < 9; k++)
                positions[t * 9 + k] = BinaryPrimitives.ReadSingleLittleEndian(tri[(k * 4)..]);
            indices[t * 3] = t * 3;
            indices[t * 3 + 1] = t * 3 + 1;
            indices[t * 3 + 2] = t * 3 + 2;
        }

        return new Mesh(positions, indices);
    }
}
