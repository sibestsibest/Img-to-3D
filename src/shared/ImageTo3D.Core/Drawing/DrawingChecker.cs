using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Drawing;

/// <summary>
/// Checks a finished drawing against the outline it was made from: everything on the page, cut
/// contours closed, cut geometry at exactly 1:1, verification bar exactly 100 mm, the critical
/// dimensions present, and no annotation text overlapping other text.
/// </summary>
public static class DrawingChecker
{
    private const double Epsilon = 1e-6;

    public static IReadOnlyList<GeometryIssue> Check(DrawingDocument doc, PlanarOutline outline)
    {
        var issues = new List<GeometryIssue>();
        void Error(string code, string message) => issues.Add(new GeometryIssue(code, IssueSeverity.Error, message));

        var w = doc.Page.WidthMm;
        var h = doc.Page.HeightMm;
        if (doc.Lines.SelectMany(l => l.Points).Any(p => p.X < 0 || p.Y < 0 || p.X > w || p.Y > h || !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            Error("drawing_out_of_bounds", "Some geometry lies outside the page.");

        var cut = doc.Lines.Where(l => l.Layer == DrawingLayer.Cut).ToList();
        if (cut.Count == 0)
            Error("no_cut_contour", "The drawing has no cutting contour.");
        if (cut.Any(l => !l.Closed || l.Points.Count < 3))
            Error("open_cut_contour", "A cutting contour is not closed.");
        if (cut.Count != outline.Outers.Count + outline.Holes.Count)
            Error("contour_count_mismatch", "The number of drawn contours does not match the outline.");

        var outers = cut.Where(l => l.Role == "outline").SelectMany(l => l.Points).ToList();
        if (outers.Count > 0)
        {
            var drawnWidth = outers.Max(p => p.X) - outers.Min(p => p.X);
            var drawnHeight = outers.Max(p => p.Y) - outers.Min(p => p.Y);
            if (Math.Abs(drawnWidth - outline.WidthMm) > Epsilon || Math.Abs(drawnHeight - outline.HeightMm) > Epsilon)
                Error("scale_mismatch", $"The drawn outline is {drawnWidth:0.###} x {drawnHeight:0.###} mm, not the outline's {outline.WidthMm:0.###} x {outline.HeightMm:0.###} mm.");
        }

        var bar = doc.Lines.SingleOrDefault(l => l.Role == "verification-bar");
        if (bar is null)
        {
            Error("missing_verification_bar", "The 100 mm verification bar is missing.");
        }
        else
        {
            var length = Math.Abs(bar.Points[^1].X - bar.Points[0].X);
            if (Math.Abs(length - SheetDrawingBuilder.VerificationBarMm) > Epsilon)
                Error("verification_bar_length", $"The verification bar is {length:0.###} mm instead of 100 mm.");
        }

        foreach (var (role, value) in new[] { ("width", outline.WidthMm), ("height", outline.HeightMm) })
        {
            if (!doc.Texts.Any(t => t.Role == role && t.Text == SheetDrawingBuilder.Mm(value)))
                Error("missing_dimension", $"The {role} dimension is missing or does not match the outline.");
        }

        if (!doc.Texts.Any(t => t.Role == "thickness"))
            Error("missing_dimension", "The thickness dimension is missing.");

        // Approximate text boxes (average glyph advance about 0.55 em) to catch overlaps and clipping.
        var boxes = doc.Texts.Select(Box).ToList();
        var clipped = boxes.Count(b => b.MinX < 0 || b.MinY < 0 || b.MaxX > w || b.MaxY > h);
        if (clipped > 0)
            issues.Add(new GeometryIssue("text_outside_page", IssueSeverity.Warning, $"{clipped} label(s) run past the page edge."));
        var overlaps = 0;
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
                if (boxes[i].Intersects(boxes[j]))
                    overlaps++;
        if (overlaps > 0)
            issues.Add(new GeometryIssue("overlapping_annotations", IssueSeverity.Warning, $"{overlaps} pair(s) of labels overlap; zoom into the SVG to read them."));

        return issues;
    }

    private readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Intersects(Rect o) => MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY;
    }

    private static Rect Box(DrawingText t)
    {
        var length = t.Text.Length * t.SizeMm * 0.55;
        var start = t.Anchor switch
        {
            TextAnchor.Middle => -length / 2,
            TextAnchor.End => -length,
            _ => 0,
        };
        if (Math.Abs(t.RotationDeg) > 45)
            return new Rect(t.Position.X - t.SizeMm, t.Position.Y + start, t.Position.X, t.Position.Y + start + length);
        return new Rect(t.Position.X + start, t.Position.Y - t.SizeMm * 0.8, t.Position.X + start + length, t.Position.Y + t.SizeMm * 0.2);
    }
}
