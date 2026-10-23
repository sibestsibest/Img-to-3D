using System.Globalization;
using System.Text;

namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Minimal Wavefront OBJ reader for reconstruction output (Meshroom's texturedMesh.obj): vertex
/// positions and faces only. Polygons are fan-triangulated; texture coordinates, normals, groups and
/// materials are ignored (the original files stay available as downloads). Negative (relative)
/// indices are supported. Vertex colours ("v x y z r g b") are kept when every vertex has them.
/// </summary>
public static class ObjReader
{
    public static Mesh Read(ReadOnlySpan<byte> data, int maxVertices = 20_000_000, int maxTriangles = 40_000_000)
    {
        var positions = new List<float>();
        var colors = new List<byte>();
        var allColored = true;
        var indices = new List<int>();
        var face = new List<int>(8);
        var lineNumber = 0;

        foreach (var raw in Encoding.UTF8.GetString(data).Split('\n'))
        {
            lineNumber++;
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            if (line.StartsWith("v ") || line.StartsWith("v\t"))
            {
                var parts = Fields(line[2..]);
                if (parts.Count < 3)
                    throw new InvalidDataException($"OBJ line {lineNumber}: a vertex needs three coordinates.");
                for (var k = 0; k < 3; k++)
                    positions.Add(Number(parts[k], lineNumber));
                if (parts.Count >= 6)
                {
                    for (var k = 3; k < 6; k++)
                        colors.Add(ToByte(Number(parts[k], lineNumber)));
                }
                else
                {
                    allColored = false;
                }

                if (positions.Count / 3 > maxVertices)
                    throw new InvalidDataException($"The OBJ has more than {maxVertices:N0} vertices.");
            }
            else if (line.StartsWith("f ") || line.StartsWith("f\t"))
            {
                face.Clear();
                var vertexCount = positions.Count / 3;
                foreach (var token in Fields(line[2..]))
                {
                    var slash = token.IndexOf('/');
                    var head = slash < 0 ? token : token[..slash];
                    if (!int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
                        throw new InvalidDataException($"OBJ line {lineNumber}: invalid face index '{token}'.");
                    var resolved = index > 0 ? index - 1 : vertexCount + index;
                    if (resolved < 0 || resolved >= vertexCount)
                        throw new InvalidDataException($"OBJ line {lineNumber}: face index {index} refers to a vertex that does not exist.");
                    face.Add(resolved);
                }

                if (face.Count < 3)
                    throw new InvalidDataException($"OBJ line {lineNumber}: a face needs at least three vertices.");
                for (var k = 1; k < face.Count - 1; k++)
                    indices.AddRange([face[0], face[k], face[k + 1]]);
                if (indices.Count / 3 > maxTriangles)
                    throw new InvalidDataException($"The OBJ has more than {maxTriangles:N0} triangles.");
            }
        }

        if (positions.Count == 0 || indices.Count == 0)
            throw new InvalidDataException("The OBJ has no faces.");
        return new Mesh(positions.ToArray(), indices.ToArray(), allColored && colors.Count == positions.Count ? colors.ToArray() : null);
    }

    private static List<string> Fields(ReadOnlySpan<char> s) =>
        s.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static float Number(string s, int line)
    {
        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !float.IsFinite(v))
            throw new InvalidDataException($"OBJ line {line}: '{s}' is not a number.");
        return v;
    }

    /// <summary>OBJ colours are 0..1 floats; values above 1 are treated as 0..255.</summary>
    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v <= 1f ? v * 255f : v), 0, 255);
}
