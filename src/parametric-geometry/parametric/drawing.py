"""Vector drawings in page millimetres (origin top-left, Y down) written as SVG or PDF.

Layers keep cutting contours apart from everything else: ``cut`` holds only the lines to cut,
``view`` holds projected outlines, ``annotation`` text and dimensions, ``reference`` the 100 mm
print check bar.
"""

from __future__ import annotations

import io
from dataclasses import dataclass, field
from xml.sax.saxutils import escape

from reportlab.pdfgen import canvas

CUT, VIEW, ANNOTATION, REFERENCE = "cut", "view", "annotation", "reference"
LAYERS = (CUT, VIEW, ANNOTATION, REFERENCE)
COLOURS = {CUT: "#d00000", VIEW: "#000000", ANNOTATION: "#1d2330", REFERENCE: "#000000"}
PT_PER_MM = 72 / 25.4

# Pages tried in order of size for 1:1 templates (millimetres).
PAGES = [("A4 portrait", 210, 297), ("A4 landscape", 297, 210), ("A3 portrait", 297, 420), ("A3 landscape", 420, 297)]


@dataclass
class Polyline:
    points: list[tuple[float, float]]
    closed: bool
    layer: str
    stroke: float


@dataclass
class Text:
    text: str
    x: float
    y: float
    size: float
    anchor: str  # start | middle | end
    layer: str
    rotation: float = 0.0  # degrees, counter-clockwise on paper


@dataclass
class Drawing:
    width: float
    height: float
    title: str
    page_name: str = "custom"
    lines: list[Polyline] = field(default_factory=list)
    texts: list[Text] = field(default_factory=list)

    def line(self, points, layer: str = ANNOTATION, stroke: float = 0.18, closed: bool = False) -> None:
        self.lines.append(Polyline([(float(x), float(y)) for x, y in points], closed, layer, stroke))

    def text(self, text: str, x: float, y: float, size: float = 3.0, anchor: str = "start", layer: str = ANNOTATION, rotation: float = 0.0) -> None:
        self.texts.append(Text(text, float(x), float(y), size, anchor, layer, rotation))

    def check_bar(self, x: float, y: float) -> None:
        """100 mm bar with 10 mm ticks: measure it to confirm the print is at 100 %."""
        self.line([(x, y), (x + 100, y)], REFERENCE, 0.3)
        for i in range(11):
            tick = 3 if i % 5 == 0 else 1.5
            self.line([(x + 10 * i, y), (x + 10 * i, y - tick)], REFERENCE, 0.2)
        self.text("100 mm: measure to check the print scale (print at 100 %, never fit to page)", x, y + 4, 2.5, layer=REFERENCE)


def _n(v: float) -> str:
    s = f"{v:.4f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def to_svg(d: Drawing) -> str:
    out = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{_n(d.width)}mm" height="{_n(d.height)}mm" viewBox="0 0 {_n(d.width)} {_n(d.height)}">',
        f"<title>{escape(d.title)}</title>",
    ]
    for layer in LAYERS:
        lines = [p for p in d.lines if p.layer == layer]
        texts = [t for t in d.texts if t.layer == layer]
        if not lines and not texts:
            continue
        out.append(f'<g id="{layer}" fill="none" stroke="{COLOURS[layer]}" stroke-linecap="round" stroke-linejoin="round">')
        for p in lines:
            path = "M " + " L ".join(f"{_n(x)} {_n(y)}" for x, y in p.points) + (" Z" if p.closed else "")
            out.append(f'<path d="{path}" stroke-width="{_n(p.stroke)}"/>')
        for t in texts:
            anchor = {"start": "start", "middle": "middle", "end": "end"}[t.anchor]
            rot = f' transform="rotate({_n(-t.rotation)} {_n(t.x)} {_n(t.y)})"' if t.rotation else ""
            out.append(
                f'<text x="{_n(t.x)}" y="{_n(t.y)}" font-family="Helvetica, Arial, sans-serif" font-size="{_n(t.size)}" '
                f'text-anchor="{anchor}" fill="{COLOURS[layer]}" stroke="none"{rot}>{escape(t.text)}</text>'
            )
        out.append("</g>")
    out.append("</svg>")
    return "\n".join(out) + "\n"


def to_pdf(drawings: list[Drawing]) -> bytes:
    """One page per drawing, each at its own size; reproducible bytes (no dates or random IDs)."""
    buf = io.BytesIO()
    c = canvas.Canvas(buf, invariant=1, pageCompression=1)
    c.setTitle(drawings[0].title if drawings else "")
    c.setCreator("ImageTo3D parametric geometry")
    for d in drawings:
        c.setPageSize((d.width * PT_PER_MM, d.height * PT_PER_MM))
        h = d.height
        for layer in LAYERS:
            colour = COLOURS[layer]
            c.setStrokeColor(colour)
            c.setFillColor(colour)
            for p in (p for p in d.lines if p.layer == layer):
                c.setLineWidth(p.stroke * PT_PER_MM)
                c.setLineCap(1)
                c.setLineJoin(1)
                path = c.beginPath()
                x0, y0 = p.points[0]
                path.moveTo(x0 * PT_PER_MM, (h - y0) * PT_PER_MM)
                for x, y in p.points[1:]:
                    path.lineTo(x * PT_PER_MM, (h - y) * PT_PER_MM)
                if p.closed:
                    path.close()
                c.drawPath(path, stroke=1, fill=0)
            for t in (t for t in d.texts if t.layer == layer):
                c.saveState()
                c.translate(t.x * PT_PER_MM, (h - t.y) * PT_PER_MM)
                if t.rotation:
                    c.rotate(t.rotation)
                c.setFont("Helvetica", t.size * PT_PER_MM)
                draw = {"start": c.drawString, "middle": c.drawCentredString, "end": c.drawRightString}[t.anchor]
                draw(0, 0, t.text)
                c.restoreState()
        c.showPage()
    c.save()
    return buf.getvalue()


def smallest_page(width: float, height: float) -> tuple[str, float, float] | None:
    for name, w, h in PAGES:
        if width <= w + 1e-9 and height <= h + 1e-9:
            return name, w, h
    return None
