using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Drawing;

/// <summary>
/// Cutting contours are kept apart from everything else so annotations can never be mistaken for
/// (or exported as) cut lines. Reference holds the print-scale verification bar.
/// </summary>
public enum DrawingLayer
{
    Cut,
    Annotation,
    Reference,
}

public enum TextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>Polyline in page millimetres (origin top-left, Y down).</summary>
public sealed record DrawingPolyline(IReadOnlyList<Point2> Points, bool Closed, DrawingLayer Layer, double StrokeMm, string? Role = null);

/// <summary>Text in page millimetres; <paramref name="Position"/> is the baseline anchor point.</summary>
public sealed record DrawingText(string Text, Point2 Position, double SizeMm, TextAnchor Anchor, DrawingLayer Layer, double RotationDeg = 0, string? Role = null);

/// <summary>Page size in whole PDF points (the PDF writer cannot represent fractional page sizes).</summary>
public sealed record PageSize(string Name, int WidthPt, int HeightPt)
{
    public const double MmPerPoint = 25.4 / 72;

    public double WidthMm => WidthPt * MmPerPoint;

    public double HeightMm => HeightPt * MmPerPoint;

    public static readonly PageSize A4Portrait = new("A4 portrait", 595, 842);
    public static readonly PageSize A4Landscape = new("A4 landscape", 842, 595);
    public static readonly PageSize A3Portrait = new("A3 portrait", 842, 1191);
    public static readonly PageSize A3Landscape = new("A3 landscape", 1191, 842);

    public static readonly IReadOnlyList<PageSize> Candidates = [A4Portrait, A4Landscape, A3Portrait, A3Landscape];
}

/// <summary>A vector drawing at 1:1: every coordinate is a real millimetre on paper.</summary>
public sealed class DrawingDocument(PageSize page, string title)
{
    public PageSize Page { get; } = page;

    public string Title { get; } = title;

    public List<DrawingPolyline> Lines { get; } = [];

    public List<DrawingText> Texts { get; } = [];

    public void Line(DrawingLayer layer, double stroke, string? role, params Point2[] points) =>
        Lines.Add(new DrawingPolyline(points, false, layer, stroke, role));

    public void Text(string text, double x, double y, double size, TextAnchor anchor = TextAnchor.Start, DrawingLayer layer = DrawingLayer.Annotation, double rotation = 0, string? role = null) =>
        Texts.Add(new DrawingText(text, new Point2(x, y), size, anchor, layer, rotation, role));
}
