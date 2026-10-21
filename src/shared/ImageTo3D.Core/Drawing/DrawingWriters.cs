using System.Globalization;
using System.Security;
using System.Text;
using ImageTo3D.Core.PartDesign;
using SkiaSharp;

namespace ImageTo3D.Core.Drawing;

public static class DrawingStyle
{
    public static string StrokeColour(DrawingLayer layer) => layer switch
    {
        DrawingLayer.Cut => "#000000",
        DrawingLayer.Reference => "#000000",
        _ => "#1f5fbf",
    };

    public static string TextColour(DrawingLayer layer) => layer == DrawingLayer.Annotation ? "#1d2330" : "#000000";
}

/// <summary>
/// SVG with one user unit = 1 mm (width/height in mm, matching viewBox), so it imports at true size
/// into laser/cutter software. Cut contours are in their own group (<c>id="cut"</c>), separate from
/// annotations and the reference bar. All text is XML-escaped.
/// </summary>
public static class SvgDrawingWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string N(double value) => value.ToString("0.####", Invariant);

    public static string Write(DrawingDocument doc)
    {
        var sb = new StringBuilder();
        var w = N(doc.Page.WidthMm);
        var h = N(doc.Page.HeightMm);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}mm\" height=\"{h}mm\" viewBox=\"0 0 {w} {h}\" data-units=\"mm\" data-scale=\"1:1\">\n");
        sb.Append($"<title>{Escape(doc.Title)}</title>\n");
        sb.Append("<desc>Units: millimetres (1 user unit = 1 mm), scale 1:1. Layer 'cut' holds the cutting contours only. Print at 100%, do not fit to page.</desc>\n");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"#ffffff\"/>\n");

        foreach (var layer in new[] { DrawingLayer.Cut, DrawingLayer.Annotation, DrawingLayer.Reference })
        {
            var id = layer.ToString().ToLowerInvariant();
            sb.Append($"<g id=\"{id}\" data-layer=\"{id}\" fill=\"none\" stroke=\"{DrawingStyle.StrokeColour(layer)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\">\n");
            foreach (var line in doc.Lines.Where(l => l.Layer == layer))
            {
                sb.Append("<path d=\"M");
                for (var i = 0; i < line.Points.Count; i++)
                    sb.Append(i == 0 ? "" : " L").Append(' ').Append(N(line.Points[i].X)).Append(' ').Append(N(line.Points[i].Y));
                if (line.Closed)
                    sb.Append(" Z");
                sb.Append($"\" stroke-width=\"{N(line.StrokeMm)}\"");
                if (line.Role is not null)
                    sb.Append($" data-role=\"{Escape(line.Role)}\"");
                sb.Append("/>\n");
            }

            foreach (var text in doc.Texts.Where(t => t.Layer == layer))
            {
                var anchor = text.Anchor switch { TextAnchor.Middle => "middle", TextAnchor.End => "end", _ => "start" };
                sb.Append($"<text x=\"{N(text.Position.X)}\" y=\"{N(text.Position.Y)}\" font-size=\"{N(text.SizeMm)}\" font-family=\"Arial, Helvetica, sans-serif\" text-anchor=\"{anchor}\" fill=\"{DrawingStyle.TextColour(layer)}\" stroke=\"none\"");
                if (text.RotationDeg != 0)
                    sb.Append($" transform=\"rotate({N(text.RotationDeg)} {N(text.Position.X)} {N(text.Position.Y)})\"");
                if (text.Role is not null)
                    sb.Append($" data-role=\"{Escape(text.Role)}\"");
                sb.Append('>').Append(Escape(text.Text)).Append("</text>\n");
            }

            sb.Append("</g>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
}

/// <summary>
/// Vector PDF through SkiaSharp. The page is set in whole points and the canvas is scaled by
/// 72/25.4, so drawing coordinates are millimetres and print at true size. Text is converted to
/// outlines: no font is embedded, and the file looks the same on every machine.
/// </summary>
public static class PdfDrawingWriter
{
    private const float PointsPerMm = 72f / 25.4f;

    public static byte[] Write(DrawingDocument doc)
    {
        using var typeface = SKFontManager.Default.MatchFamily("Arial") ?? SKTypeface.Default;
        using var stream = new SKDynamicMemoryWStream();
        using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = doc.Title, Creator = "ImageTo3D", Producer = "ImageTo3D (SkiaSharp)", RasterDpi = 72 }))
        // RasterDpi must stay 72: Skia rounds the page size in raster units, so any other value makes the
        // MediaBox differ from the requested size (300 dpi gives 594.96 x 841.92 pt for A4). Nothing here is rasterised.
        {
            var canvas = pdf.BeginPage(doc.Page.WidthPt, doc.Page.HeightPt);
            canvas.Scale(PointsPerMm);

            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            foreach (var line in doc.Lines)
            {
                using var builder = new SKPathBuilder();
                builder.MoveTo((float)line.Points[0].X, (float)line.Points[0].Y);
                for (var i = 1; i < line.Points.Count; i++)
                    builder.LineTo((float)line.Points[i].X, (float)line.Points[i].Y);
                if (line.Closed)
                    builder.Close();
                using var path = builder.Detach();
                stroke.StrokeWidth = (float)line.StrokeMm;
                stroke.Color = SKColor.Parse(DrawingStyle.StrokeColour(line.Layer));
                canvas.DrawPath(path, stroke);
            }

            using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (var text in doc.Texts)
            {
                using var font = new SKFont(typeface, (float)text.SizeMm);
                var width = font.MeasureText(text.Text);
                var dx = text.Anchor switch { TextAnchor.Middle => -width / 2, TextAnchor.End => -width, _ => 0f };
                using var glyphs = font.GetTextPath(text.Text, new SKPoint(dx, 0));
                if (glyphs.IsEmpty && !string.IsNullOrWhiteSpace(text.Text))
                    throw new InvalidOperationException("No font is available to draw text; install a TrueType font (for example Arial or DejaVu Sans).");
                fill.Color = SKColor.Parse(DrawingStyle.TextColour(text.Layer));
                canvas.Save();
                canvas.Translate((float)text.Position.X, (float)text.Position.Y);
                if (text.RotationDeg != 0)
                    canvas.RotateDegrees((float)text.RotationDeg);
                canvas.DrawPath(glyphs, fill);
                canvas.Restore();
            }

            pdf.EndPage();
            pdf.Close();
        }

        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
