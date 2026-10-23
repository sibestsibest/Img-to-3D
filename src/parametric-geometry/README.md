# Parametric geometry worker

Deterministic, dimension-driven geometry for scratch-building in polystyrene (PS/HIPS) sheet, rod
and tube: the same request always produces byte-identical files. It needs no AI weights, no
Meshroom and no network access. Python 3.11 with the pinned packages in `requirements.txt` is the
only requirement.

The application will call it as a separate process (Phase 2): the request is JSON, the files go to
an output directory, and the response lists them. Nothing but file paths and metadata passes
through JSON.

## Setup

```powershell
cd src\parametric-geometry
uv python install 3.11
uv venv --python 3.11 .venv
uv pip install --python .venv\Scripts\python.exe -r requirements.txt
.venv\Scripts\python.exe -m pytest -q
```

To change a dependency, edit `requirements.in` and regenerate the lock with
`uv pip compile requirements.in -o requirements.txt --python-version 3.11`.

| Package | Version | Licence | Used for |
|---|---|---|---|
| numpy | 2.4.6 | BSD-3-Clause | arrays |
| trimesh | 5.1.1 | MIT | solids, GLB/STL export, plane sections |
| shapely | 2.2.0 | BSD-3-Clause | 2D profiles with holes, projected outlines |
| mapbox-earcut | 2.1.0 | ISC | triangulating profiles for extrusion |
| ezdxf | 1.4.5 | MIT | DXF templates |
| reportlab | 5.1.0 | BSD-3-Clause | PDF pages |
| pytest | 9.1.1 | MIT | tests |

scipy is deliberately not used; `trimesh.section` needs it, so sections use `mesh_plane` instead.

## Usage

```powershell
.venv\Scripts\python.exe -m parametric describe      # templates, parameters, ranges, PS thicknesses
.venv\Scripts\python.exe -m parametric generate --request request.json --output out\ship
```

```json
{
  "schemaVersion": 1,
  "template": "spacecraft",
  "parameters": { "length": 240, "width": 80, "height": 70, "sheet_thickness": 1.0 },
  "exports": ["glb", "stl", "obj", "bom", "templates", "views"]
}
```

`exports` is optional (default: all). The output directory must be new or empty. The worker
writes `response.json` there and prints the same JSON on stdout. Logs go to stderr. Exit codes:
`0` succeeded, `2` refused (the response carries `error.code`, `error.message` and the offending
`error.field`), `1` internal error.

### Output files

| File | Content |
|---|---|
| `model.glb` | one named node per component, **metres** (glTF rule), Y-up, a colour per role |
| `model.stl` | binary, all components, **millimetres**, **Z-up**, as the application's STL export |
| `model.obj` | millimetres, Y-up, one `o` group per component, no materials |
| `bom.csv` | one row per group of identical parts: quantity, material, thickness, sizes, pattern, forming, template file (UTF-8 with BOM; formula-like text is neutralised) |
| `templates/itemNN_<role>.svg` / `.dxf` | 1:1 cutting template of each sheet item; layer `cut` / `CUT` holds only the closed cutting contours |
| `templates/templates.pdf` | all templates, one page each, the smallest A4/A3 page that fits at 1:1 |
| `views/views.svg` / `.pdf` | front, side and top views plus section A-A on A3 at a standard scale (1:1, 1:2, 1:5, …), with overall dimensions |

The response holds `engine`, `origin: "parametric"`, `scale: exact`, the normalised `parameters`,
`derived` values, `dimensions`, per-component `validation` (closed, boundary/non-manifold edges,
degenerate faces, winding, bodies, volume, bounds), `bom`, `outputs` (path, bytes, SHA-256) and
`issues`.

## Conventions

* Millimetres, Y-up, length along X (nose towards +X), width along Z, model on the ground (y = 0).
* PS sheet thicknesses: 0.3, 0.5, 0.8, 1, 1.5, 2 and 3 mm. Every other value is refused
  (`unsupported_thickness`).
* Round parts are regular polygons whose corners lie on the nominal circle (`segments`, default 48).
* Sheet parts are built from their 2D profile, so a cutting template is the profile itself:
  volume = template area × thickness, which the tests check for every flat part.
* Rolled parts (half-shell, tubes, cones) get a **developed** pattern at mid-thickness with a
  forming note, and a `requires_forming` issue. Flat parts get a `flat` pattern.

## Templates

**`spacecraft`**: rectangular upper hull (deck, side walls, end walls, top plate), semicircular
lower hull (rolled half-shell of radius width/2 with two end bulkheads and internal ribs), one
engine housing (rolled tube) with a mount disc and a flared exhaust, armor panels on both sides
(raised, `panel_thickness`), a top frame with recessed windows, a slotted vent panel, rows of hex
bolt heads and antenna rods. `length` is overall (hull + engine + exhaust). `width` and `height`
are the hull's; side armor and top details add to the envelope (`derived.overallSizeMm`). See
`python -m parametric describe` for every parameter, its default and its range.

**`assembly`**: a free list of components: `box`, `chamfered_box` (four edges bevelled), `cylinder`,
`cone`, `frustum`, `sphere`, `hemisphere`, `tube`, `rod`, `extrusion` (polygon with holes), `plate`
(PS sheet with holes) and `shell` (PS sheet rolled to an arc). Each has a `transform`
(`translate`, `rotate_deg`, `scale` > 0). Sheet parts refuse `scale`.

Example:

```json
{"schemaVersion": 1, "template": "assembly", "parameters": {"components": [
  {"name": "base", "shape": "plate", "size_x": 60, "size_y": 40, "thickness": 1.5},
  {"name": "post", "shape": "rod", "diameter": 3, "length": 25, "transform": {"translate": [0, 0, 1.5]}}
]}}
```

## Limitations

* No boolean operations: holes are part of a profile, and recesses and raised panels are separate
  laminated sheets (which is also how they are built in styrene). Curved cut-outs through curved
  surfaces are not possible.
* Bevels exist only as `chamfered_box` (edges along one axis).
* The views draw each component's exact projected outline, but hidden lines are not removed. The
  section is drawn from the cut edges without hatching.
* A flared exhaust uses a radial wall of the sheet thickness. Developed patterns ignore the
  allowance needed for butt or overlap joints, so add it when cutting.
* A template larger than A3 at 1:1 gets a custom-size page and a `template_too_large` warning; the
  DXF is unaffected.
* Only the `spacecraft` and `assembly` templates exist.
