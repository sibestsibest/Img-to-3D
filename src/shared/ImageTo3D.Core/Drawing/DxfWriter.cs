using System.Globalization;
using System.Text;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Drawing;

/// <param name="Revision">Shown in the annotation text, for example "Rev 3" or "Draft".</param>
public sealed record DxfInfo(string PartName, Guid PartId, string? Material, double ThicknessMm, int Quantity, string Revision);

/// <summary>
/// ASCII DXF (R12, AC1009) of a flat part's cutting outline in model space, 1 drawing unit = 1 mm,
/// origin at the lower-left corner of the outline (X along the longest side, Y up). Layer CUT holds
/// only the closed cutting contours (outer boundaries and holes); layer ANNOTATION holds hole
/// labels and a short description, and can be switched off in the cutter software.
/// R12 is the most widely imported DXF version (laser, vinyl cutters, CAD).
/// </summary>
public static class DxfWriter
{
    public const string CutLayer = "CUT";
    public const string AnnotationLayer = "ANNOTATION";

    private const double TextHeight = 2.5;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Write(PlanarOutline outline, DxfInfo info)
    {
        if (!outline.IsUsable)
            throw new InvalidOperationException("The outline is not usable for a DXF export.");

        var labelGap = TextHeight * 2;
        var minY = -labelGap * 4;
        var dxf = new StringBuilder();

        void Pair(int code, string value) => dxf.Append(code.ToString(Invariant)).Append('\n').Append(value).Append('\n');
        void Num(int code, double value) => Pair(code, value.ToString("0.######", Invariant));

        // ---- Header: version and units ----
        Pair(0, "SECTION");
        Pair(2, "HEADER");
        Pair(9, "$ACADVER");
        Pair(1, "AC1009");
        Pair(9, "$INSUNITS");
        Pair(70, "4"); // millimetres
        Pair(9, "$MEASUREMENT");
        Pair(70, "1"); // metric
        Pair(9, "$EXTMIN");
        Num(10, 0);
        Num(20, minY);
        Pair(9, "$EXTMAX");
        Num(10, outline.WidthMm);
        Num(20, outline.HeightMm);
        Pair(0, "ENDSEC");

        // ---- Tables: line type and the two layers ----
        Pair(0, "SECTION");
        Pair(2, "TABLES");
        Pair(0, "TABLE");
        Pair(2, "LTYPE");
        Pair(70, "1");
        Pair(0, "LTYPE");
        Pair(2, "CONTINUOUS");
        Pair(70, "0");
        Pair(3, "Solid line");
        Pair(72, "65");
        Pair(73, "0");
        Num(40, 0);
        Pair(0, "ENDTAB");
        Pair(0, "TABLE");
        Pair(2, "LAYER");
        Pair(70, "2");
        foreach (var (name, colour) in new[] { (CutLayer, 7), (AnnotationLayer, 5) })
        {
            Pair(0, "LAYER");
            Pair(2, name);
            Pair(70, "0");
            Pair(62, colour.ToString(Invariant));
            Pair(6, "CONTINUOUS");
        }

        Pair(0, "ENDTAB");
        Pair(0, "ENDSEC");

        // ---- Entities ----
        Pair(0, "SECTION");
        Pair(2, "ENTITIES");

        void Polyline(IReadOnlyList<Point2> points)
        {
            Pair(0, "POLYLINE");
            Pair(8, CutLayer);
            Pair(66, "1");
            Num(10, 0);
            Num(20, 0);
            Num(30, 0);
            Pair(70, "1"); // closed
            foreach (var p in points)
            {
                Pair(0, "VERTEX");
                Pair(8, CutLayer);
                Num(10, p.X);
                Num(20, p.Y);
                Num(30, 0);
            }

            Pair(0, "SEQEND");
            Pair(8, CutLayer);
        }

        void Text(string value, double x, double y, double height)
        {
            Pair(0, "TEXT");
            Pair(8, AnnotationLayer);
            Num(10, x);
            Num(20, y);
            Num(30, 0);
            Num(40, height);
            Pair(1, Encode(value));
        }

        foreach (var loop in outline.Outers)
            Polyline(loop.Points);
        for (var i = 0; i < outline.Holes.Count; i++)
        {
            var hole = outline.Holes[i];
            Polyline(hole.Loop.Points);
            Text($"H{i + 1}", hole.Centre.X + hole.WidthMm / 2 + 0.5, hole.Centre.Y, Math.Min(TextHeight, Math.Max(0.5, hole.HeightMm)));
        }

        // Description under the part, outside the cut area.
        var material = string.IsNullOrWhiteSpace(info.Material) ? "material not specified" : info.Material;
        Text($"{info.PartName} ({info.PartId.ToString("N")[..8]}) - {info.Revision}", 0, -labelGap, TextHeight);
        Text($"{material}, thickness {info.ThicknessMm.ToString("0.##", Invariant)} mm, quantity {info.Quantity.ToString(Invariant)}", 0, -labelGap * 2, TextHeight);
        Text($"Units mm, scale 1:1. {outline.WidthMm.ToString("0.00", Invariant)} x {outline.HeightMm.ToString("0.00", Invariant)} mm. Layer CUT = cutting contours only.", 0, -labelGap * 3, TextHeight);

        Pair(0, "ENDSEC");
        Pair(0, "EOF");
        return dxf.ToString();
    }

    /// <summary>
    /// DXF text values are single lines of the drawing code page; control characters become spaces
    /// and anything outside printable ASCII uses the \U+XXXX escape that AutoCAD-compatible readers decode.
    /// User text cannot start an escape or a %% control code of its own.
    /// </summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length);
        var previous = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F)
                sb.Append(' ');
            else if (rune.Value == '\\')
                sb.Append('/');
            else if (rune.Value == '%' && previous == '%')
                sb.Append(' ');
            else if (rune.Value < 0x7F)
                sb.Append((char)rune.Value);
            else if (rune.IsBmp)
                sb.Append("\\U+").Append(rune.Value.ToString("X4", Invariant));
            else
                sb.Append('?');
            previous = rune.Value;
        }

        return sb.ToString();
    }
}
