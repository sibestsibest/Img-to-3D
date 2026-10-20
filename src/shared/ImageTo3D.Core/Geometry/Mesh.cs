namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Indexed triangle mesh. Positions are packed xyz triples, indices are counter-clockwise
/// triangles (outward-facing), colors are optional packed sRGB triples (one per vertex).
/// </summary>
public sealed class Mesh
{
    public Mesh(float[] positions, int[] indices, byte[]? colors = null)
    {
        if (positions.Length % 3 != 0)
            throw new ArgumentException("Positions length must be a multiple of 3.", nameof(positions));
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Indices length must be a multiple of 3.", nameof(indices));
        if (colors is not null && colors.Length != positions.Length)
            throw new ArgumentException("Colors must contain one RGB triple per vertex.", nameof(colors));

        Positions = positions;
        Indices = indices;
        Colors = colors;
    }

    public float[] Positions { get; }

    public int[] Indices { get; }

    public byte[]? Colors { get; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    public Mesh WithColors(byte[]? colors) => new(Positions, Indices, colors);

    public Mesh Clone() => new((float[])Positions.Clone(), (int[])Indices.Clone(), (byte[]?)Colors?.Clone());
}

public readonly record struct Vector3D(double X, double Y, double Z)
{
    public double this[int axis] => axis switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public double MaxComponent => Math.Max(X, Math.Max(Y, Z));
}

public readonly record struct BoundingBox(Vector3D Min, Vector3D Max)
{
    public Vector3D Size => new(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);

    public Vector3D Center => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);
}
