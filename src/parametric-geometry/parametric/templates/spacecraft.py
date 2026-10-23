"""Parameterised spacecraft built the way it would be scratch-built from PS sheet.

Layout (millimetres, Y-up, nose towards +X, width along Z, ground at y = 0)::

            antennas
      ┌──bolts──frame with recesses──vent──┐  top plate      ─ y = height
      │ armor │ armor │ armor │ armor       │  side walls (upper hull, rectangular)
      ├──────────────── deck ───────────────┤               ─ y = width/2 + t
       \___ lower hull: rolled half-shell ___/ + bulkheads, ribs   ─ y = 0
   exhaust ◄ engine housing (rolled tube) at the rear

``length`` is the overall length (hull + engine housing + exhaust). ``width`` and ``height`` are
the hull's; side armor adds its thickness on both sides and details on the top add height
(``derived.overallSizeMm`` gives the real envelope). The lower hull is an exact semicircle of
radius width/2, so ``height`` must leave room for the rectangular upper hull above it.
"""

from __future__ import annotations

import math

from .. import components as parts
from .. import shapes
from ..errors import GEOMETRY_CONFLICT
from ..model import Issue, ParametricModel
from ..params import Param, read, require

NAME = "spacecraft"
DESCRIPTION = "Rectangular upper hull, semicircular lower hull, one engine housing with exhaust, armor panels and details."

PARAMS = [
    Param("length", 240, 40, 2000, description="Overall length: hull, engine housing and exhaust."),
    Param("width", 80, 20, 1000, description="Hull width (also the lower hull diameter)."),
    Param("height", 70, 15, 1000, description="Hull height from the bottom of the lower hull to the top plate."),
    Param("sheet_thickness", 1.0, 0.3, 3, sheet=True, description="PS sheet for the hull structure."),
    Param("panel_thickness", 0.5, 0.3, 3, sheet=True, description="PS sheet for armor panels, frame and vent."),
    Param("engine_diameter", 48, 5, 1000, description="Outside diameter of the engine housing."),
    Param("engine_length", 40, 5, 1000, description="Length of the engine housing."),
    Param("engine_axis_height", 0, 0, 1000, description="Height of the engine axis above the ground; 0 = middle of the hull height."),
    Param("exhaust_length", 20, 2, 1000, description="Length of the exhaust nozzle."),
    Param("exhaust_exit_ratio", 1.2, 1.0, 2.0, unit="", description="Exit diameter of the nozzle / engine diameter."),
    Param("bulkheads", 3, 0, 20, unit="", integer=True, description="Internal ribs inside the lower hull (the two end bulkheads are always built)."),
    Param("rib_depth", 6, 1, 200, description="Radial depth of each internal rib."),
    Param("panel_rows", 2, 0, 10, unit="", integer=True, description="Armor panel rows on each side wall (0 = none)."),
    Param("panel_columns", 4, 0, 30, unit="", integer=True, description="Armor panel columns on each side wall (0 = none)."),
    Param("panel_margin", 4, 0.5, 100, description="Border kept free around panels, frame and bolt rows."),
    Param("panel_gap", 2, 0.3, 100, description="Gap between armor panels and between frame recesses."),
    Param("top_recesses", 2, 0, 10, unit="", integer=True, description="Recessed windows in the top frame (0 = no frame)."),
    Param("vent_slots", 5, 0, 30, unit="", integer=True, description="Slots in the vent panel on the top (0 = no vent)."),
    Param("vent_slot_length", 20, 2, 500, description="Length of each vent slot."),
    Param("vent_slot_width", 2, 0.5, 50, description="Width of each vent slot (also the bar between slots)."),
    Param("bolt_pitch", 12, 0, 200, description="Spacing of the hex bolt heads along both top edges (0 = no bolts)."),
    Param("bolt_diameter", 1.5, 0.3, 20, description="Bolt head size across corners."),
    Param("bolt_height", 0.5, 0.2, 20, description="Bolt head height."),
    Param("antennas", 2, 0, 2, unit="", integer=True, description="Antenna rods on the front of the top frame."),
    Param("antenna_diameter", 1.5, 0.3, 20, description="Antenna rod diameter."),
    Param("antenna_length", 25, 1, 500, description="Antenna rod length."),
    Param("segments", 48, 12, 256, unit="", integer=True, description="Polygon sides of round parts."),
]

X, Y, Z = (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)
NEG_Z = (0.0, 0.0, -1.0)


def build(raw: dict) -> ParametricModel:
    p = read(raw, PARAMS, "parameters.")
    t, tp, seg = p["sheet_thickness"], p["panel_thickness"], p["segments"]
    length, width, height = p["length"], p["width"], p["height"]
    radius = width / 2

    hull_length = length - p["engine_length"] - p["exhaust_length"]
    require(hull_length >= 10 * t and hull_length >= 20, "Engine and exhaust leave too little hull length (at least 20 mm and 10 sheet thicknesses).", "parameters.length", GEOMETRY_CONFLICT)
    wall_height = height - radius - 2 * t  # side wall between the deck and the top plate
    require(wall_height >= 1, f"height must be at least width/2 + 2 x sheet thickness + 1 mm = {radius + 2 * t + 1:g} mm for the rectangular upper hull.", "parameters.height", GEOMETRY_CONFLICT)
    require(radius - t > p["rib_depth"] or p["bulkheads"] == 0, "rib_depth must be smaller than the inner radius of the lower hull.", "parameters.rib_depth", GEOMETRY_CONFLICT)

    exit_d = p["engine_diameter"] * p["exhaust_exit_ratio"]
    max_d = max(p["engine_diameter"], exit_d)
    axis_y = p["engine_axis_height"] or height / 2
    require(max_d <= width, f"The engine and exhaust ({max_d:g} mm) must not be wider than the hull ({width:g} mm).", "parameters.engine_diameter", GEOMETRY_CONFLICT)
    require(axis_y - max_d / 2 >= -1e-9 and axis_y + max_d / 2 <= height + 1e-9, "The engine and exhaust must stay within the hull height; change engine_axis_height or the diameters.", "parameters.engine_axis_height", GEOMETRY_CONFLICT)
    require(p["engine_diameter"] > 4 * t, "engine_diameter must be more than 4 sheet thicknesses.", "parameters.engine_diameter", GEOMETRY_CONFLICT)

    x_front = length / 2
    x_rear = x_front - hull_length
    deck_y = radius + t  # top face of the deck
    top_y = height - t  # bottom face of the top plate
    comps = []
    issues: list[Issue] = []

    # ---- lower hull: rolled half-shell, end bulkheads, ribs ---------------------------------
    # Profile plane (u, v) = (Y, Z), extruded along +X; angles measured from +Y, so π/2..3π/2 is below the axis.
    centre = (x_rear, radius, 0.0)
    comps.append(parts.curved_shell("lower_hull", "hull_shell", radius, t, math.pi / 2, 3 * math.pi / 2, hull_length, centre, Y, Z, seg))
    half_disc = shapes.polygon(shapes.arc(0, 0, radius - t, math.pi / 2, 3 * math.pi / 2, seg // 2))
    comps.append(parts.plate("bulkhead_rear", "bulkhead", half_disc, t, (x_rear, radius, 0.0), Y, Z))
    comps.append(parts.plate("bulkhead_front", "bulkhead", half_disc, t, (x_front - t, radius, 0.0), Y, Z))
    if p["bulkheads"]:
        ri = radius - t
        rib = shapes.polygon(shapes.arc(0, 0, ri, math.pi / 2, 3 * math.pi / 2, seg // 2) + list(reversed(shapes.arc(0, 0, ri - p["rib_depth"], math.pi / 2, 3 * math.pi / 2, seg // 2))))
        inner_length = hull_length - 2 * t
        for i in range(p["bulkheads"]):
            x = x_rear + t + inner_length * (i + 1) / (p["bulkheads"] + 1) - t / 2
            comps.append(parts.plate(f"rib_{i + 1}", "rib", rib, t, (x, radius, 0.0), Y, Z))

    # ---- upper hull: deck, side walls, end walls, top plate ----------------------------------
    # Horizontal plates: u = X, v = -Z so the normal u×v is +Y.
    comps.append(parts.plate("deck", "deck", shapes.polygon(shapes.rect(x_rear, -radius, x_front, radius)), t, (0.0, radius, 0.0), X, NEG_Z))
    comps.append(parts.plate("top_plate", "top_plate", shapes.polygon(shapes.rect(x_rear, -radius, x_front, radius)), t, (0.0, top_y, 0.0), X, NEG_Z))
    wall = shapes.polygon(shapes.rect(x_rear, deck_y, x_front, top_y))
    comps.append(parts.plate("side_wall_right", "side_wall", wall, t, (0.0, 0.0, radius - t), X, Y))
    comps.append(parts.plate("side_wall_left", "side_wall", wall, t, (0.0, 0.0, -radius), X, Y))
    end = shapes.polygon(shapes.rect(deck_y, -radius + t, top_y, radius - t))
    comps.append(parts.plate("end_wall_rear", "end_wall", end, t, (x_rear, 0.0, 0.0), Y, Z))
    comps.append(parts.plate("end_wall_front", "end_wall", end, t, (x_front - t, 0.0, 0.0), Y, Z))

    # ---- armor panels on both side walls (outside, over the whole upper hull side) -----------
    margin, gap = p["panel_margin"], p["panel_gap"]
    rows, cols = p["panel_rows"], p["panel_columns"]
    if rows and cols:
        pw = (hull_length - 2 * margin - (cols - 1) * gap) / cols
        ph = (height - radius - 2 * margin - (rows - 1) * gap) / rows
        require(pw >= 2 and ph >= 2, f"Armor panels would be {pw:.2f} x {ph:.2f} mm; use fewer panels or smaller margins and gaps (minimum 2 mm).", "parameters.panel_columns", GEOMETRY_CONFLICT)
        for side, z in (("right", radius), ("left", -radius - tp)):
            for r in range(rows):
                for c in range(cols):
                    x0 = x_rear + margin + c * (pw + gap)
                    y0 = radius + margin + r * (ph + gap)
                    comps.append(parts.plate(f"armor_{side}_r{r + 1}c{c + 1}", "armor_panel", shapes.polygon(shapes.rect(x0, y0, x0 + pw, y0 + ph)), tp, (0.0, 0.0, z), X, Y))

    # ---- top: frame with recessed windows (front half), vent panel (rear half) ---------------
    x_mid = (x_front + x_rear) / 2
    top_surface = height
    frame_box = (x_mid + gap / 2, -radius + margin, x_front - margin, radius - margin)
    if p["top_recesses"]:
        fx0, fz0, fx1, fz1 = frame_box
        n = p["top_recesses"]
        border = margin
        wx = (fx1 - fx0 - 2 * border - (n - 1) * gap) / n
        wz = fz1 - fz0 - 2 * border
        require(wx >= 2 and wz >= 2, "The top frame is too small for its recesses; reduce top_recesses or panel_margin.", "parameters.top_recesses", GEOMETRY_CONFLICT)
        # Plate coordinates (u, v) = (x, -z).
        holes = [shapes.rect(fx0 + border + i * (wx + gap), -(fz1 - border), fx0 + border + i * (wx + gap) + wx, -(fz0 + border)) for i in range(n)]
        frame = shapes.polygon(shapes.rect(fx0, -fz1, fx1, -fz0), holes)
        comps.append(parts.plate("top_frame", "recess_frame", frame, tp, (0.0, top_surface, 0.0), X, NEG_Z, notes=["Glue onto the top plate; the windows form the recesses."]))

    if p["vent_slots"]:
        n, sl, sw = p["vent_slots"], p["vent_slot_length"], p["vent_slot_width"]
        vx = sl + 2 * sw  # plate length along X
        vz = (2 * n + 1) * sw  # slots and bars of equal width
        avail_x = (x_mid - gap / 2) - (x_rear + margin)
        avail_z = width - 2 * margin
        require(vx <= avail_x and vz <= avail_z, f"The vent panel ({vx:g} x {vz:g} mm) does not fit on the rear half of the top ({avail_x:.2f} x {avail_z:.2f} mm).", "parameters.vent_slots", GEOMETRY_CONFLICT)
        cx = x_rear + margin + avail_x / 2
        slots = [shapes.slot(cx, -(-vz / 2 + sw * (2 * i + 1.5)), sl, sw, seg) for i in range(n)]
        vent = shapes.polygon(shapes.rect(cx - vx / 2, -vz / 2, cx + vx / 2, vz / 2), slots)
        comps.append(parts.plate("vent_panel", "vent_panel", vent, tp, (0.0, top_surface, 0.0), X, NEG_Z))

    if p["bolt_pitch"]:
        require(p["bolt_diameter"] < margin, "bolt_diameter must be smaller than panel_margin (the bolts sit in the free border).", "parameters.bolt_diameter", GEOMETRY_CONFLICT)
        span = hull_length - 2 * margin
        count = int(math.floor(span / p["bolt_pitch"] + 1e-9)) + 1
        start = x_rear + margin + (span - (count - 1) * p["bolt_pitch"]) / 2
        zs = (radius - margin / 2, -radius + margin / 2)
        positions = [(start + i * p["bolt_pitch"], top_surface, z) for z in zs for i in range(count)]
        comps.append(parts.hex_heads("bolt_heads", "bolt", p["bolt_diameter"], p["bolt_height"], positions))

    if p["antennas"]:
        require(p["top_recesses"] > 0, "Antennas stand on the top frame; set top_recesses to at least 1.", "parameters.antennas", GEOMETRY_CONFLICT)
        require(p["antenna_diameter"] < margin, "antenna_diameter must be smaller than panel_margin (the rods stand on the frame border).", "parameters.antenna_diameter", GEOMETRY_CONFLICT)
        fx0, fz0, fx1, fz1 = frame_box
        ax = fx1 - margin / 2
        for i, z in enumerate((fz1 - margin / 2, fz0 + margin / 2)[: p["antennas"]]):
            comps.append(parts.rod(f"antenna_{i + 1}", "antenna", p["antenna_diameter"], p["antenna_length"], "y", (ax, top_surface + tp, z), seg))

    # ---- engine housing, mount disc, exhaust --------------------------------------------------
    d = p["engine_diameter"]
    x_engine = x_rear - p["engine_length"]
    comps.append(parts.rolled_tube("engine_housing", "engine_housing", d, t, p["engine_length"], "x", (x_engine, axis_y, 0.0), seg))
    mount = shapes.polygon(shapes.circle(0, 0, d / 2 - t, seg))
    comps.append(parts.plate("engine_mount", "bulkhead", mount, t, (x_rear - t, axis_y, 0.0), Y, Z, notes=["Sits inside the housing against the rear end wall."]))
    # Nozzle: local +Z onto world -X, starting at the rear end of the housing.
    nozzle_frame = shapes.frame((x_engine, axis_y, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0))  # local X->Z, Y->Y, Z->-X
    comps.append(parts.cone_shell("exhaust", "exhaust", d, exit_d, t, p["exhaust_length"], nozzle_frame, seg))

    for c in comps:
        if c.forming:
            issues.append(Issue("requires_forming", "info", f"{c.name} is rolled from flat sheet: {c.pattern.note}", c.name))

    model = ParametricModel(NAME, p, {}, comps, issues)
    lo, hi = model.bounds()
    model.derived = {
        "hullLengthMm": round(hull_length, 4),
        "lowerHullRadiusMm": radius,
        "upperHullHeightMm": round(height - radius, 4),
        "sideWallHeightMm": round(wall_height, 4),
        "engineAxisHeightMm": axis_y,
        "exhaustExitDiameterMm": round(exit_d, 4),
        "overallSizeMm": [round(float(v), 4) for v in hi - lo],
        "sectionXMm": round(x_rear + hull_length / 2, 4),  # section A-A through the middle of the hull
    }
    return model
