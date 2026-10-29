---
name: sheet-metal-production
description: Preparing the sheet-metal parts of a SolidWorks assembly for production through the solidworks MCP server (Alchemist for SOLIDWORKS) — summary by thickness, design-for-manufacturing check (holes near bends, short flanges, radii), flat DXF for laser cutting with layers and names by part number, a cut list in Excel, a bill of materials. Use when the user asks to prepare sheet metal for laser cutting / bending, выгрузить DXF, сделать ведомость раскроя, проверить технологичность листа.
---

# Sheet metal to production

Tools of the `solidworks` MCP server; sizes in mm. A cut-short tool description — `tool_help('<name>')`,
an overview — `tool_help('sheet_metal')`. Do **not save** the user's model unless asked.

1. `ensure_ready` → `open_document(assembly or part)`.
2. `sheet_metal_info()` — how many sheet-metal parts, thicknesses, bend radii, K-factor. Note parts without a material
   or with different radii at one thickness.
3. `sheet_metal_dfm()` — manufacturability: hole ↔ bend (< 2.5·t + R), hole ↔ edge (< t), holes smaller than t,
   short flanges (< 4·t), different radii. Pass the user's shop limits as parameters (`min_flange_mm` etc.) if given.
   List the findings per part with their `at` coordinates (mm, on the flat pattern).
4. DXF: `export_flat_dxf(out_dir=…, name_by="designation", layers=True, note=True)` — one file per part + configuration,
   layers CUT_OUTER / CUT_INNER / BEND / MARK, a label on NOTE (not cut). Part marking can be added first: a sketch on
   a face + `add_sketch_text`, exported with `sketches=True` → layer MARK.
   If the answer is `"running": true` — `job_result(job_id)`.
5. Cut list: `sheet_metal_cutlist(xlsx_path=…, assembly_qty=N)` — blanks, areas, cut length, bends, mass; totals per
   thickness + material.
6. If a bill of materials is needed — `bom_gost(xlsx_path=…)` (GOST 2.106) or `bom_export`; parts without a part number
   come with a warning — offer to fill it in (`set_custom_properties`).
7. Answer: where the DXF and Excel files are, the number of parts and sheets per thickness, the mass, the DFM findings
   and what to fix.
