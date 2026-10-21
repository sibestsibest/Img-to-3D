using System.Globalization;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Drawing;

/// <param name="ThicknessSpecified">True when the thickness was entered by the user, false when it is the model estimate.</param>
/// <param name="DimensionsNote">What the drawn dimensions are based on (estimated model scale or confirmed measurements).</param>
/// <param name="Revision">"Draft" for a drawing made on demand, "Rev N" for one inside release N of the project.</param>
public sealed record SheetDrawingInfo(
    string PartName,
    Guid PartId,
    string ProjectName,
    string? Material,
    double ThicknessMm,
    bool ThicknessSpecified,
    int Quantity,
    string DimensionsNote,
    DateTime GeneratedUtc,
    string Revision = SheetDrawingInfo.Draft)
{
    public const string Draft = "Draft";
}

public sealed record SheetDrawing(DrawingDocument Document, IReadOnlyList<GeometryIssue> Issues);

public sealed class DrawingTooLargeException(string message) : Exception(message);

/// <summary>
/// Lays out a 1:1 fabrication sheet for a flat part: the cutting outline (top view) with overall and
/// hole dimensions, an edge view with the thickness, a hole table, a 100 mm verification bar and a
/// title block. Picks the smallest A4/A3 page that holds the part at full size; never scales down.
/// </summary>
public static class SheetDrawingBuilder
{
    public const double VerificationBarMm = 100;

    private const double Margin = 10;
    private const double DimSpace = 16;
    private const double TextSize = 3.2;
    private const double SmallText = 2.5;
    private const double TitleHeight = 40;
    private const double CutStroke = 0.2;
    private const double ThinStroke = 0.13;
    private const int MaxHoleRows = 20;
    private const double MinTitleWidth = 185; // fits A4 portrait (595 pt = 209.9 mm) with margins

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Mm(double value) => value.ToString("0.00", Invariant);

    public static SheetDrawing Build(PlanarOutline outline, SheetDrawingInfo info)
    {
        if (!outline.IsUsable)
            throw new InvalidOperationException("The outline is not usable for a drawing.");

        double w = outline.WidthMm, h = outline.HeightMm, t = info.ThicknessMm;
        var holeRows = Math.Min(outline.Holes.Count, MaxHoleRows) + (outline.Holes.Count > MaxHoleRows ? 1 : 0);
        var holeTable = outline.Holes.Count > 0 ? 7 + holeRows * 4.5 : 0;
        var notes = outline.Issues.Where(i => i.Severity != IssueSeverity.Info).ToList();
        var notesHeight = notes.Count * 4.5;

        var needWidth = Margin + DimSpace + w + 26 + Margin;
        var needHeight = Margin + 8 + h + 12 + Math.Max(t, 0.5) + DimSpace + notesHeight + 25 + holeTable + 4 + TitleHeight + Margin;
        var page = PageSize.Candidates.FirstOrDefault(p => p.WidthMm >= Math.Max(needWidth, MinTitleWidth + 2 * Margin) && p.HeightMm >= needHeight)
                   ?? throw new DrawingTooLargeException(
                       $"The part ({Mm(w)} x {Mm(h)} mm) does not fit on an A3 sheet at 1:1. Split it into smaller parts; tiled printing is not supported yet.");

        var doc = new DrawingDocument(page, $"{info.PartName} - cutting sheet 1:1");
        var ox = Margin + DimSpace;
        var oy = Margin + 8;
        Point2 P(double x, double y) => new(ox + x, oy + (h - y)); // outline (Y up) to page (Y down)

        // ---- Top view: cutting outline ----
        doc.Text("TOP VIEW - cutting outline (1:1)", Margin, Margin + 3.5, TextSize);
        foreach (var loop in outline.Outers)
            doc.Lines.Add(new DrawingPolyline(loop.Points.Select(p => P(p.X, p.Y)).ToList(), true, DrawingLayer.Cut, CutStroke, "outline"));
        for (var i = 0; i < outline.Holes.Count; i++)
        {
            var hole = outline.Holes[i];
            doc.Lines.Add(new DrawingPolyline(hole.Loop.Points.Select(p => P(p.X, p.Y)).ToList(), true, DrawingLayer.Cut, CutStroke, "hole"));
            var c = P(hole.Centre.X, hole.Centre.Y);
            var mark = Math.Min(2, Math.Min(hole.WidthMm, hole.HeightMm) / 4);
            doc.Line(DrawingLayer.Annotation, ThinStroke, "centre", new Point2(c.X - mark, c.Y), new Point2(c.X + mark, c.Y));
            doc.Line(DrawingLayer.Annotation, ThinStroke, "centre", new Point2(c.X, c.Y - mark), new Point2(c.X, c.Y + mark));
            doc.Text($"H{i + 1}", c.X + mark + 0.6, c.Y - 0.6, SmallText, role: "hole-label");
        }

        // Overall height, to the left of the outline.
        VerticalDimension(doc, P(0, 0).Y, P(0, h).Y, ox, ox - 9, h, "height");

        // ---- Edge view: thickness ----
        var sy = oy + h + 12;
        doc.Text("EDGE VIEW - thickness", Margin, sy - 3, TextSize);
        var side = new List<Point2> { new(ox, sy), new(ox + w, sy), new(ox + w, sy + t), new(ox, sy + t) };
        doc.Lines.Add(new DrawingPolyline(side, true, DrawingLayer.Annotation, ThinStroke, "edge-view"));
        VerticalDimension(doc, sy, sy + t, ox + w, ox + w + 8, t, "thickness", textLeft: false);

        // Overall width, under the edge view.
        var widthY = sy + Math.Max(t, 0.5) + 10;
        HorizontalDimension(doc, ox, ox + w, sy + t, widthY, w, "width");

        var y = widthY + 6;
        foreach (var note in notes)
        {
            doc.Text("NOTE: " + note.Message, Margin, y, SmallText, role: "note");
            y += 4.5;
        }

        // ---- Verification bar: 100 mm with 10 mm ticks ----
        y += 6;
        var bx = Margin;
        doc.Line(DrawingLayer.Reference, 0.35, "verification-bar", new Point2(bx, y), new Point2(bx + VerificationBarMm, y));
        for (var k = 0; k <= 10; k++)
        {
            var tick = k % 5 == 0 ? 3 : 1.5;
            doc.Line(DrawingLayer.Reference, ThinStroke, "verification-tick", new Point2(bx + k * 10, y - tick), new Point2(bx + k * 10, y));
        }

        doc.Text("0", bx, y + 3.5, SmallText, TextAnchor.Middle, DrawingLayer.Reference);
        doc.Text("100 mm", bx + VerificationBarMm, y + 3.5, SmallText, TextAnchor.Middle, DrawingLayer.Reference);
        doc.Text("Print at 100% (actual size), never 'fit to page'. Check that this bar measures exactly 100 mm.", bx, y + 8, SmallText, layer: DrawingLayer.Reference, role: "print-instruction");
        y += 15;

        // ---- Hole table ----
        if (outline.Holes.Count > 0)
        {
            doc.Text("HOLES (centre from lower-left corner of the outline)", Margin, y, SmallText, role: "hole-table");
            y += 4.5;
            for (var i = 0; i < Math.Min(outline.Holes.Count, MaxHoleRows); i++)
            {
                var hole = outline.Holes[i];
                var size = hole.DiameterMm is { } d ? $"diameter {Mm(d)}" : $"{Mm(hole.WidthMm)} x {Mm(hole.HeightMm)}";
                doc.Text($"H{i + 1}: {size} mm, centre X {Mm(hole.Centre.X)} Y {Mm(hole.Centre.Y)}", Margin + 2, y, SmallText, role: "hole-row");
                y += 4.5;
            }

            if (outline.Holes.Count > MaxHoleRows)
            {
                doc.Text($"... and {outline.Holes.Count - MaxHoleRows} more holes (see the SVG for exact geometry)", Margin + 2, y, SmallText, role: "hole-row");
                y += 4.5;
            }
        }

        TitleBlock(doc, page, outline, info);
        return new SheetDrawing(doc, DrawingChecker.Check(doc, outline));
    }

    private static void TitleBlock(DrawingDocument doc, PageSize page, PlanarOutline outline, SheetDrawingInfo info)
    {
        var left = Margin;
        var right = page.WidthMm - Margin;
        var top = page.HeightMm - Margin - TitleHeight;
        var bottom = page.HeightMm - Margin;
        var mid = left + (right - left) * 0.6;
        doc.Lines.Add(new DrawingPolyline([new(left, top), new(right, top), new(right, bottom), new(left, bottom)], true, DrawingLayer.Annotation, 0.25, "title-block"));
        for (var r = 1; r < 5; r++)
            doc.Line(DrawingLayer.Annotation, ThinStroke, "title-block", new Point2(left, top + r * 8), new Point2(right, top + r * 8));
        doc.Line(DrawingLayer.Annotation, ThinStroke, "title-block", new Point2(mid, top), new Point2(mid, top + 32));

        void Cell(double x, int row, string label, string value, string? role = null)
        {
            doc.Text(label, x + 1.5, top + row * 8 + 3, 1.8, layer: DrawingLayer.Annotation);
            doc.Text(value, x + 1.5, top + row * 8 + 6.8, 3, layer: DrawingLayer.Annotation, role: role);
        }

        Cell(left, 0, "PART", Trim(info.PartName, 70), "part-name");
        Cell(mid, 0, "PART ID", info.PartId.ToString("N")[..8]);
        Cell(left, 1, "MATERIAL", Trim(info.Material ?? "not specified", 70), "material");
        Cell(mid, 1, "THICKNESS", $"{Mm(info.ThicknessMm)} mm ({(info.ThicknessSpecified ? "specified" : "estimated from model")})", "thickness-spec");
        Cell(left, 2, "QUANTITY", info.Quantity.ToString(Invariant), "quantity");
        Cell(mid, 2, "SCALE / UNITS", "1:1 / millimetres", "scale");
        Cell(left, 3, "OUTLINE", $"{Mm(outline.WidthMm)} x {Mm(outline.HeightMm)} mm, {outline.Holes.Count} hole(s), area {Mm(outline.Outers.Sum(o => o.Area) + outline.Holes.Sum(x => x.Loop.Area))} mm2");
        Cell(mid, 3, "REVISION", Trim(info.Revision, 30), "revision");
        Cell(left, 4, "DIMENSIONS", Trim(info.DimensionsNote, 110), "dimensions-note");
        // Above the title block, so it never runs into a cell.
        doc.Text($"{Trim(info.ProjectName, 50)} - generated {info.GeneratedUtc.ToString("yyyy-MM-dd HH:mm", Invariant)} UTC - ImageTo3D", right, top - 1.5, 1.8, TextAnchor.End);
    }

    private static void HorizontalDimension(DrawingDocument doc, double x1, double x2, double featureY, double dimY, double value, string role)
    {
        const double gap = 1, over = 2;
        doc.Line(DrawingLayer.Annotation, ThinStroke, "extension", new Point2(x1, featureY + gap), new Point2(x1, dimY + over));
        doc.Line(DrawingLayer.Annotation, ThinStroke, "extension", new Point2(x2, featureY + gap), new Point2(x2, dimY + over));
        doc.Line(DrawingLayer.Annotation, ThinStroke, "dimension", new Point2(x1, dimY), new Point2(x2, dimY));
        Arrow(doc, new Point2(x1, dimY), 1, 0);
        Arrow(doc, new Point2(x2, dimY), -1, 0);
        doc.Text(Mm(value), (x1 + x2) / 2, dimY - 1.2, TextSize, TextAnchor.Middle, role: role);
    }

    private static void VerticalDimension(DrawingDocument doc, double y1, double y2, double featureX, double dimX, double value, string role, bool textLeft = true)
    {
        const double gap = 1, over = 2;
        var dir = Math.Sign(dimX - featureX);
        doc.Line(DrawingLayer.Annotation, ThinStroke, "extension", new Point2(featureX + dir * gap, y1), new Point2(dimX + dir * over, y1));
        doc.Line(DrawingLayer.Annotation, ThinStroke, "extension", new Point2(featureX + dir * gap, y2), new Point2(dimX + dir * over, y2));
        doc.Line(DrawingLayer.Annotation, ThinStroke, "dimension", new Point2(dimX, y1), new Point2(dimX, y2));
        // Arrows point outwards from the gap for short dimensions (the thickness of a sheet).
        var inside = Math.Abs(y2 - y1) > 6;
        Arrow(doc, new Point2(dimX, Math.Min(y1, y2)), 0, inside ? 1 : -1);
        Arrow(doc, new Point2(dimX, Math.Max(y1, y2)), 0, inside ? -1 : 1);
        if (textLeft)
            doc.Text(Mm(value), dimX - 1.2, (y1 + y2) / 2, TextSize, TextAnchor.Middle, rotation: -90, role: role);
        else
            doc.Text(Mm(value), dimX + 1.5, (y1 + y2) / 2 + TextSize / 2.5, TextSize, role: role);
    }

    /// <summary>Open arrowhead at <paramref name="tip"/>; its barbs extend along (dx, dy), so it points the opposite way.</summary>
    private static void Arrow(DrawingDocument doc, Point2 tip, double dx, double dy)
    {
        const double length = 2.5, spread = 0.8;
        var back = new Point2(tip.X + dx * length, tip.Y + dy * length);
        doc.Line(DrawingLayer.Annotation, ThinStroke, "arrow",
            new Point2(back.X - dy * spread, back.Y - dx * spread), tip, new Point2(back.X + dy * spread, back.Y + dx * spread));
    }

    private static string Trim(string value, int max) => value.Length > max ? value[..(max - 1)] + "…" : value;
}
