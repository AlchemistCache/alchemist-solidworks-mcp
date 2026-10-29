<p align="center"><img src="assets/banner.png" alt="Alchemist for SOLIDWORKS — turns words into parts" width="100%"></p>

<p align="center">
<b>English</b> · <a href="README.ru.md">Русский</a> · <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
<img src="https://img.shields.io/badge/SOLIDWORKS-2024-c8102e" alt="SOLIDWORKS 2024">
<img src="https://img.shields.io/badge/MCP-134%20tools-6b4fbb" alt="134 MCP tools">
<img src="https://img.shields.io/badge/Python-3.11%2B-3776ab" alt="Python 3.11+">
<img src="https://img.shields.io/badge/license-AGPL--3.0-2ea44f" alt="AGPL-3.0">
</p>

# Alchemist for SOLIDWORKS

*Turns words into parts.* An MCP server that lets **Claude** (or any MCP client) work in **SOLIDWORKS**: model parts from a plain-language
request, take big assemblies apart, get sheet metal ready for the laser and the press brake, and check strength in
**SOLIDWORKS Simulation** — while **seeing** the model: every view and every stress map comes back to the chat as a
picture.

<p align="center"><img src="assets/demo.gif" alt="Claude designs a 3D-printable robot arm and checks it" width="100%"></p>
<p align="center"><sub>One conversation, no mouse: Claude models a 3D-printable robot arm down to the screws (17 parts,
64 components), moves it, finds that the shoulder servo would stall and swaps it, checks the printed arm in Simulation
and CalculiX and draws it. Full video: <a href="assets/demo_en.mp4">assets/demo_en.mp4</a></sub></p>

It started as a fork of [SolidPilot](https://github.com/eyfel/mcp-server-solidworks) by Çağatay Bakan and grew from 47
to **134 tools**:
assemblies, sheet metal, weldments, Simulation with a CalculiX cross-check, GOST drawings and bills of materials.

## What you can ask

| Area | Examples |
|---|---|
| **Modelling** | "Make a 120×80×10 plate with four Ø9 holes 10 mm from the edges and 3 mm fillets", "Revolve this sketch around the axis", "Pattern the slot 6 times around the hub", sweeps, lofts, Hole Wizard, configurations, custom properties. Sketches come out **fully defined** with real design dimensions |
| **Assemblies** | "Show the assembly from the front", "Find interferences", "Which parts are closer than 3 mm to each other?", "Mass and centre of gravity of the frame", "Change Distance1 to 25 mm", "Insert a bolt into the plate's hole and pattern it 4× at 30 mm", "Pack and go to D:\copy" |
| **Sheet metal** | "Summary of sheet parts by thickness", "Export flat DXFs named by part number with laser layers", "Make a cut list in Excel", "Check manufacturability: holes near bends, short flanges", "Add an 8 mm hem", "Convert this box to sheet metal" |
| **Strength** | "Check the bracket: fixed on face 4, 500 N down on face 9, AISI 1020" → stress, displacement, factor of safety, where the max is and **a picture of the stress map**; contacts, bolts and welds; hand checks of bolts, welds and beams (EN 1993-1-8); an HTML report; an independent **CalculiX** cross-check on the same mesh |
| **Weldments and BOMs** | "Build a frame from 40×40×2 tube along this sketch", trims, end caps, gussets, weldment cut list, a bill of materials (GOST 2.106 specification in Excel) |
| **Drawings** | views laid out in projection on the sheet at a standard scale, dimensions set off the views (only the features you name, if you like), duplicates removed, sections, hole callouts, BOM table, PDF / DXF export; reading a DXF / DWG drawing to rebuild the part |
| **Supplier models** | "Convert every STEP in this folder to SOLIDWORKS parts" |

## How it works

```
Claude ⇄ MCP ⇄ adapter (Python, adapters/claude) ⇄ HTTP ⇄ SolidworksExecution.exe (C#) ⇄ COM ⇄ SOLIDWORKS
```

- The **adapter** describes the tools to Claude and starts the execution server on the first call.
- **SolidworksExecution.exe** is a background server with no window that drives the SOLIDWORKS API. It is built at
  install time against the API of **your installed** SOLIDWORKS, so it always matches your version.
- By default Claude works in **its own hidden SOLIDWORKS instance** and does not get in your way
  (`install.ps1 -SharedInstance` makes it work in the window you have open).
- Every tool speaks **millimetres and degrees** (Simulation: mm, N, MPa) and answers one JSON object.

## Install

You need Windows, **SOLIDWORKS** (tested with 2024 SP1; SOLIDWORKS Simulation for the strength tools),
**Python 3.11+** and Claude Desktop or Claude Code.

```powershell
git clone https://github.com/AlchemistCache/alchemist-solidworks-mcp.git
cd alchemist-solidworks-mcp
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The installer creates `.venv`, installs the dependencies, builds `SolidworksExecution.exe` (the C# compiler is
downloaded from nuget.org once, ~21 MB) and registers the `solidworks` server in Claude Desktop (backing up the
config). Then **restart Claude**.

Switches: `-Test` runs a smoke test on a live SOLIDWORKS, `-SharedInstance`, `-NoRegister`, `-Dev` (pytest, ruff),
`-SwApiDir <SOLIDWORKS folder>\api\redist` if SOLIDWORKS is not in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`.

**Update:** `git pull`, then run `install.ps1` again (it rebuilds the exe).

## Tools

**134 tools** in groups; the full list with parameters is in [docs/TOOLS.md](docs/TOOLS.md). Groups marked *on request*
are off until you use them, to save context — Claude finds their tools with `tool_help` and runs them through
`execute_batch`, which switches the group on (it then starts on for the next 30 days).

<!-- tools:start -->
| Group | Tools |
|---|---|
| **Documents and files** (16) | `ensure_ready`, `open_new_part`, `open_document`, `export_document`, `verify_state`, `close_document`, `save_document`, `get_selection`, `set_part_material`, `activate_document`, `batch_export_files`, `unreferenced_files`, `run_macro`, `import_step`, `import_step_folder`, `set_document_units` |
| **Sketches** (10) | `create_sketch`, `add_sketch_entity`, `add_sketch_entities`, `sketch_modify`, `add_sketch_constraint`, `add_dimension`, `define_sketch`, `edit_sketch`, `modify_dimension`, `add_sketch_text` |
| **Part features** (13) | `extrude_feature`, `create_rib`, `hole_wizard`, `add_edge_feature`, `add_reference_geometry`, `create_pattern`, `edit_feature`, `list_materials`, `add_plane_angle`, `add_axis_two_points`, `sweep_pipe`, `loft`, `set_body_visibility` |
| **Drawings** (15) · *on request* | `create_drawing`, `add_drawing_view`, `add_flat_pattern_view`, `add_drawing_dimension`, `auto_dimension_drawing`, `auto_center_marks`, `add_hole_callout`, `add_section_view`, `analyze_drawing`, `check_drawing`, `move_drawing_view`, `layout_drawing_views`, `dedupe_drawing_dimensions`, `add_dimension_between`, `insert_bom_table` |
| **Reading and checking the model** (4) | `analyze_model`, `compare_parts`, `compare_assemblies`, `dimension_for_face` |
| **Background jobs and batches** (2) | `job_result`, `execute_batch` |
| **Assemblies** (31) | `list_components`, `delete_components`, `list_open_documents`, `interference_check`, `clearance_check`, `measure_components`, `mass_properties`, `list_references`, `get_custom_properties`, `bom_export`, `bom_gost`, `set_component_state`, `set_appearance`, `move_component`, `replace_component`, `pack_and_go`, `set_custom_properties`, `configurations`, `form_subassembly`, `export_components`, `list_mates`, `edit_mate`, `add_mate_advanced`, `insert_and_mate`, `pattern_components`, `constraint_status`, `open_new_assembly`, `analyze_assembly`, `insert_component`, `add_mate`, `save_body_as_part` |
| **Sheet metal** (10) · *on request* | `sheet_metal_cutlist`, `sheet_metal_hem`, `sheet_metal_miter_flange`, `sheet_metal_cross_break`, `sheet_metal_gusset`, `convert_to_sheet_metal`, `sheet_metal_info`, `export_flat_dxf`, `sheet_metal_dfm`, `sheet_metal_feature` |
| **Strength: SOLIDWORKS Simulation and hand checks** (22) · *on request* | `sim_status`, `sim_create_study`, `sim_delete_study`, `sim_set_material`, `sim_add_fixture`, `sim_add_load`, `sim_contact`, `sim_mesh`, `sim_run`, `sim_results`, `sim_plot`, `sim_image`, `sim_crosscheck`, `sim_connector`, `sim_contact_set`, `sim_mesh_control`, `sim_options`, `sim_copy_study`, `sim_report`, `bolt_check`, `weld_check`, `beam_check` |
| **Weldments** (7) · *on request* | `weldment_profiles`, `create_weldment_profiles`, `weldment_member`, `weldment_cut_list`, `weldment_trim`, `weldment_end_cap`, `weldment_gusset` |
| **Pictures of the model** (2) | `view_image`, `view_images` |
| **Help** (1) | `tool_help` |
| **Tool groups** (1) | `enable_tools` |
<!-- tools:end -->

## Claude skills

`skills/` holds ready workflows: **sheet-assembly-strength** (strength of a sheet part or assembly: fixtures, loads,
contacts, mesh, maps, hand check, report) and **sheet-metal-production** (sheet metal to production: DFM, laser DXF,
cut list, BOM). `install.ps1` installs them for Claude Code (`%USERPROFILE%\.claude\skills`) and puts zips into
`dist\skills` — upload those in Claude Desktop under Settings → Capabilities → Skills.

## Tips

- Start a session with "get SolidWorks ready" (`ensure_ready`) — the first start of the hidden SOLIDWORKS takes up to a minute.
- Face and edge indices for operations come from `analyze_model(faces / edges)` and `analyze_assembly`.
- Long tool descriptions are trimmed (about a third less context per request); the full text and topic help are in
  `tool_help`. Full descriptions always: environment variable `SWMCP_DOCS=full`.
- Tool groups: only the base tools and assemblies are on by default; a group used in the last 30 days switches on by
  itself. Setting: `SWMCP_TOOLS` in `adapters/claude/.env` (`all` — everything).
- Sheet metal in Simulation is meshed as shells: put loads on the large faces of a sheet, not on the thin sides.
- VBA macros (`run_macro`) run only from the project's `macros\` folder.
- Simulation studies live in the document — save the part / assembly to keep them.
- Long operations on big assemblies answer `{"running": true, "job_id": …}` after ~45 s and keep working; Claude
  collects the result with `job_result` (MCP clients cut a call at about 60 s).

## Troubleshooting

| Symptom | What to do |
|---|---|
| Not sure what is wrong | `.venv\Scripts\python.exe tools\doctor.py` — Python and packages, SOLIDWORKS and Simulation, the exe built and fresh, the server, the registration |
| No `solidworks` tools in Claude | restart Claude; check `tools\register_claude.py` |
| `COM_ATTACH_FAILED` | SOLIDWORKS and Claude run with different rights (one "as administrator"), or a dialog / licence prompt is open |
| Build error during install | SOLIDWORKS not in the default folder → `install.ps1 -SwApiDir ...` |
| `SIMULATION_UNAVAILABLE` | no SOLIDWORKS Simulation licence / add-in |
| Logs | `execution\solidworks\SolidworksExecution\bin\Debug\execution.log`, `adapters\claude\adapter.log` |

## Documentation

- [docs/TOOLS.md](docs/TOOLS.md) — every tool with its parameters (generated)
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — layout, adding a tool, tests
- [docs/SOLIDWORKS_API_NOTES.md](docs/SOLIDWORKS_API_NOTES.md) — what the SOLIDWORKS 2024 API really does
- [CONTRIBUTING.md](CONTRIBUTING.md) — conventions and how to contribute
- [docs/UPSTREAM_README.md](docs/UPSTREAM_README.md), [docs/master-architecture.md](docs/master-architecture.md) — the original SolidPilot documentation
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) — code and ideas taken from other forks

## License

AGPL-3.0 (see [LICENSE](LICENSE)), like the original SolidPilot © Çağatay Bakan. SOLIDWORKS is a trademark of
Dassault Systèmes; this project is not affiliated with or endorsed by Dassault Systèmes or Anthropic.
