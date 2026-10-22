using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.PartDesign;

public static class PrimitiveKinds
{
    /// <summary>Flat styrene plate: length x width, thickness along <see cref="PrimitiveSpec.Axis"/> (default Y).</summary>
    public const string Plate = "plate";

    /// <summary>Rectangular block: length along X, height along Y, width along Z.</summary>
    public const string Box = "box";

    /// <summary>Solid round rod: diameter, length along <see cref="PrimitiveSpec.Axis"/> (default X).</summary>
    public const string Rod = "rod";

    /// <summary>Round tube: outer and inner diameter, length along <see cref="PrimitiveSpec.Axis"/> (default X).</summary>
    public const string Tube = "tube";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Plate, Box, Rod, Tube };

    /// <summary>The fabrication category a primitive is made as.</summary>
    public static string Category(string kind) => kind switch
    {
        Plate => PartCategories.Sheet,
        Rod => PartCategories.Rod,
        Tube => PartCategories.Tube,
        _ => PartCategories.Custom3D,
    };
}

/// <summary>
/// A procedural part in design millimetres. Which fields are used depends on <see cref="Kind"/>:
/// plate (length, width, thickness), box (length, width, height), rod (diameter, length),
/// tube (outer and inner diameter, length). <see cref="Center"/> is the centre of the solid.
/// </summary>
public sealed record PrimitiveSpec(
    string Kind,
    double? LengthMm = null,
    double? WidthMm = null,
    double? HeightMm = null,
    double? ThicknessMm = null,
    double? DiameterMm = null,
    double? OuterDiameterMm = null,
    double? InnerDiameterMm = null,
    char Axis = '\0',
    Vector3D Center = default);

public sealed class InvalidPrimitiveException(string message) : ArgumentException(message);

/// <summary>
/// Closed, consistently outward-wound meshes for simple stock shapes (plates, blocks, rods, tubes),
/// so parts that are better built than reconstructed can be added to a design with exact sizes.
/// Round shapes are approximated by a regular polygon whose vertices lie on the nominal circle.
/// </summary>
public static class PrimitiveBuilder
{
    public const double MaxSizeMm = 5000;

    public const int DefaultSegments = 48;

    public static Mesh Build(PrimitiveSpec spec, int segments = DefaultSegments)
    {
        if (!PrimitiveKinds.All.Contains(spec.Kind))
            throw new InvalidPrimitiveException($"Unknown primitive '{spec.Kind}'. Use one of: {string.Join(", ", PrimitiveKinds.All)}.");
        if (!double.IsFinite(spec.Center.X) || !double.IsFinite(spec.Center.Y) || !double.IsFinite(spec.Center.Z) ||
            Math.Abs(spec.Center.X) > 100_000 || Math.Abs(spec.Center.Y) > 100_000 || Math.Abs(spec.Center.Z) > 100_000)
            throw new InvalidPrimitiveException("The position must be finite and within 100 m of the origin.");
        if (segments is < 8 or > 512)
            throw new ArgumentOutOfRangeException(nameof(segments));

        switch (spec.Kind)
        {
            case PrimitiveKinds.Plate:
            {
                var length = Size(spec.LengthMm, "length");
                var width = Size(spec.WidthMm, "width");
                var thickness = Size(spec.ThicknessMm, "thickness");
                // Local Z is the thickness; with the default Y normal the length lies along X and the width along Z.
                return Orient(BoxAt(width, length, thickness), Axis(spec.Axis, 'y'), spec.Center);
            }

            case PrimitiveKinds.Box:
                return Translate(BoxAt(Size(spec.LengthMm, "length"), Size(spec.HeightMm, "height"), Size(spec.WidthMm, "width")), spec.Center);

            case PrimitiveKinds.Rod:
                return Orient(Cylinder(Size(spec.DiameterMm, "diameter") / 2, Size(spec.LengthMm, "length"), segments), Axis(spec.Axis, 'x'), spec.Center);

            default:
            {
                var outer = Size(spec.OuterDiameterMm, "outer diameter");
                var inner = Size(spec.InnerDiameterMm, "inner diameter");
                if (inner >= outer)
                    throw new InvalidPrimitiveException("The inner diameter must be smaller than the outer diameter.");
                if ((outer - inner) / 2 < 0.05)
                    throw new InvalidPrimitiveException("The tube wall must be at least 0.05 mm thick.");
                return Orient(Tube(outer / 2, inner / 2, Size(spec.LengthMm, "length"), segments), Axis(spec.Axis, 'x'), spec.Center);
            }
        }
    }

    private static double Size(double? value, string name)
    {
        if (value is not { } v || !double.IsFinite(v))
            throw new InvalidPrimitiveException($"The {name} is required.");
        if (v < 0.05 || v > MaxSizeMm)
            throw new InvalidPrimitiveException($"The {name} must be between 0.05 and {MaxSizeMm:0} mm.");
        return v;
    }

    private static char Axis(char axis, char fallback)
    {
        var a = axis == '\0' ? fallback : char.ToLowerInvariant(axis);
        return a is 'x' or 'y' or 'z' ? a : throw new InvalidPrimitiveException("The axis must be 'x', 'y' or 'z'.");
    }

    /// <summary>Axis-aligned block centred on the origin, sizes along X, Y and Z.</summary>
    private static Mesh BoxAt(double sx, double sy, double sz)
    {
        var (x, y, z) = ((float)(sx / 2), (float)(sy / 2), (float)(sz / 2));
        float[] p =
        [
            -x, -y, -z, x, -y, -z, x, y, -z, -x, y, -z,
            -x, -y, z, x, -y, z, x, y, z, -x, y, z,
        ];
        int[] i = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 6, 2, 3, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];
        return new Mesh(p, i);
    }

    /// <summary>Solid cylinder around local Z, from z = -length/2 to +length/2.</summary>
    private static Mesh Cylinder(double radius, double length, int n)
    {
        var p = new List<float>((2 * n + 2) * 3);
        var h = (float)(length / 2);
        Ring(p, radius, -h, n); // 0..n-1 bottom
        Ring(p, radius, h, n); // n..2n-1 top
        var cb = 2 * n;
        p.AddRange([0, 0, -h, 0, 0, h]);
        var ct = cb + 1;

        var idx = new List<int>(n * 12);
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            idx.AddRange([i, j, n + j, i, n + j, n + i]); // side, outward
            idx.AddRange([cb, j, i]); // bottom, -Z
            idx.AddRange([ct, n + i, n + j]); // top, +Z
        }

        return new Mesh(p.ToArray(), idx.ToArray());
    }

    /// <summary>Tube around local Z: outer wall outward, inner wall facing the bore, annular ends.</summary>
    private static Mesh Tube(double outer, double inner, double length, int n)
    {
        var p = new List<float>(4 * n * 3);
        var h = (float)(length / 2);
        Ring(p, outer, -h, n); // ob: 0
        Ring(p, outer, h, n); // ot: n
        Ring(p, inner, -h, n); // ib: 2n
        Ring(p, inner, h, n); // it: 3n
        int ob = 0, ot = n, ib = 2 * n, it = 3 * n;

        var idx = new List<int>(n * 24);
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            idx.AddRange([ob + i, ob + j, ot + j, ob + i, ot + j, ot + i]); // outer wall
            idx.AddRange([ib + i, it + j, ib + j, ib + i, it + i, it + j]); // inner wall (faces the axis)
            idx.AddRange([ot + i, ot + j, it + j, ot + i, it + j, it + i]); // top annulus, +Z
            idx.AddRange([ob + i, ib + j, ob + j, ob + i, ib + i, ib + j]); // bottom annulus, -Z
        }

        return new Mesh(p.ToArray(), idx.ToArray());
    }

    private static void Ring(List<float> p, double radius, float z, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            p.AddRange([(float)(radius * Math.Cos(a)), (float)(radius * Math.Sin(a)), z]);
        }
    }

    /// <summary>
    /// Maps local Z (the shape's main axis) onto <paramref name="axis"/> with a cyclic permutation,
    /// which keeps the winding outward, then moves the shape to <paramref name="center"/>.
    /// </summary>
    private static Mesh Orient(Mesh local, char axis, Vector3D center)
    {
        var src = local.Positions;
        var p = new float[src.Length];
        for (var k = 0; k < src.Length; k += 3)
        {
            var (x, y, z) = (src[k], src[k + 1], src[k + 2]);
            (p[k], p[k + 1], p[k + 2]) = axis switch
            {
                'x' => (z, x, y),
                'y' => (y, z, x),
                _ => (x, y, z),
            };
        }

        return Translate(new Mesh(p, local.Indices), center);
    }

    private static Mesh Translate(Mesh mesh, Vector3D t)
    {
        var p = (float[])mesh.Positions.Clone();
        for (var k = 0; k < p.Length; k += 3)
        {
            p[k] = (float)(p[k] + t.X);
            p[k + 1] = (float)(p[k + 1] + t.Y);
            p[k + 2] = (float)(p[k + 2] + t.Z);
        }

        return new Mesh(p, mesh.Indices);
    }
}
