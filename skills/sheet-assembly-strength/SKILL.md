---
name: sheet-assembly-strength
description: Strength check of a sheet-metal part or an assembly of sheets (brackets, housings, frames) in SOLIDWORKS Simulation through the solidworks MCP server (Alchemist for SOLIDWORKS) — setup, fixtures and loads, contacts and bolts, mesh, solve, stress maps, factor of safety, hand cross-check and an HTML report. Use when the user asks to check strength / посчитать на прочность / проверить кронштейн / запас прочности of a SolidWorks part or assembly.
---

# Strength of a sheet-metal assembly

You work through the tools of the `solidworks` MCP server. Units of the `sim_*` tools: N, MPa, mm. If a tool's
description is cut short — `tool_help('<tool>')`; the overall order — `tool_help('simulation')`.

## 0. Input
1. `ensure_ready`, then `open_document` (do **not save** the user's model unless asked).
2. Find out from the user (or from the model) **what is being checked**: how the part is held (bolts to a wall, welded
   to a frame), what the load is (mass of equipment → force = m·9.81 N; pressure; self-weight), what factor of safety
   is required (1.5 by default for static loads, 2–3 for shocks / vibration). Never invent a load silently — state the
   assumption.
3. Look at the model: `view_images`, `sheet_metal_info` (thicknesses), `mass_properties`.

## 1. Study
- `sim_create_study(name, study_type="static")` (no "." or "," in the name).
- `sim_status(details=True)` → materials. Parts without a material — `sim_set_material` (steel: "Plain Carbon Steel" /
  "AISI 1020", stainless "AISI 304", aluminium "6061-T6 (SS)"; a localized SolidWorks uses localized names).

## 2. Fixtures and loads
- Face indices: `analyze_model(faces, near='[x,y,z]')` for a part, `analyze_assembly(faces, component)` for an assembly.
- **Sheet = shells.** Put loads and fixtures on the **large faces of a sheet**. A thin side face gets nothing in a
  shell model (the result is zeros).
- Bolted to a wall: `sim_add_fixture` on the cylindrical faces of the holes or on the contact face (fixed is
  conservative; more realistic — `roller` on the contact face + fixed on the holes).
- Load: `sim_add_load(load_type="force", faces=[…], value=N, direction=[0,-1,0])`; pressure in MPa;
  `load_type="gravity"` — self-weight.

## 3. Assemblies: contacts and connectors
- By default parts are **bonded**. For bolted joints — `sim_contact(global_contact="no_penetration")` or
  `sim_contact_set` on a pair of faces (friction 0.15–0.2).
- `sim_connector(connector_type="bolt", head=[…hole edge / face…], nut=[…], diameter_mm=8, preload_n=…)` — **only
  between solid bodies**: on sheet-metal shells the SW 2024 API creates no bolt (InvalidMesh). For sheets: keep a
  bonded contact over the bolt's pad, or model the joint separately with solid bodies.
- Welding sheets: `sim_connector(connector_type="edge_weld", face1, face2, edges)`. Overlapping shells are bonded by
  SolidWorks itself — the weld then gets no load.

## 4. Mesh and solve
- `sim_mesh()` (default size); on holes and bends — `sim_mesh_control(entities, element_size_mm≈t)`.
- `sim_run()` — if the answer is `"running": true`, wait with `job_result(job_id)`, do not call again.
- The result: von Mises stress (MPa) with the location of the max, displacement (mm), factor of safety (for shells —
  the material's yield / σmax), connector forces. When the max sits on a fixture or a loaded face, `max_on` says so
  and `max_away_from_boundary` gives the stress away from it — judge the part by that.

## 5. Check the result (always)
- `sim_image(result="stress", clip_percentile=99)` and `sim_image(result="displacement", deform_scale=-1)` — see where
  the max is. A peak at a sharp corner of a fixture or at a point load is a **singularity**: judge the stress next to
  it, not one point.
- By hand: `beam_check` (a cantilever / beam of the same section and length), `bolt_check` (shear / axial from the
  connectors in `sim_results`), `weld_check`. More than 2× off the FEA — look for a mistake in fixtures or loads.
- Mesh: when in doubt, solve a copy (`sim_copy_study`) with `sim_mesh(element_size_mm` halved`)` or
  `sim_options(adaptive="h")` and compare.

## 6. Answer the user
- `sim_report(required_fos=…, hand_checks=[…], notes=[…], compare_studies=[…])` → an HTML report (verdict, results,
  maps, mesh sensitivity, hand checks, inputs). Give the path.
- Briefly: σmax and where, max displacement, factor of safety vs the required one, **assumptions** (fixtures, loads,
  contacts), what is singular, what to change (thickness, a hem, a `sheet_metal_gusset` rib, a gusset plate).
- Another load case — `sim_copy_study` and change only the load.
