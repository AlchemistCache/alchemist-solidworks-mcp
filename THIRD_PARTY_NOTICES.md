# Third-party notices

This repository (Alchemist for SOLIDWORKS) is a fork of **SolidPilot**
(https://github.com/eyfel/mcp-server-solidworks, © Çağatay Bakan, AGPL-3.0) and is distributed under the
same AGPL-3.0 license (see `LICENSE`). Code and techniques adopted from other SolidPilot forks — all AGPL-3.0:

| Source | What | Where here |
|---|---|---|
| Claw256/mcp-server-solidworks [62e7438](https://github.com/Claw256/mcp-server-solidworks/commit/62e7438) — © Fearghal Hayes | `CadOperationError` (ToolError) error surfacing + tests | `adapters/claude/response_mapper.py`, `server.py`, `tests/test_error_surfacing.py` |
| Claw256/mcp-server-solidworks [31deceb](https://github.com/Claw256/mcp-server-solidworks/commit/31deceb) — © Fearghal Hayes | Hole Wizard (`HoleWizard5` arguments, tap drill, size strings, one feature per point) | `SolidWorksService.Features.HoleWizard.cs`, `tools_features.py` `hole_wizard` |
| fafolaktik/mcp-server-solidworks [d2537cd](https://github.com/fafolaktik/mcp-server-solidworks/commit/d2537cd) | sketch-frame fix for `add_dimension` / `add_sketch_constraint`, `dimension_for_face`, dimension attachments in `analyze_model(sketch)` | `SolidWorksService.Sketch.cs`, `SolidWorksService.Analyze.cs`, `adapters/claude/face_dimension.py`, `tools_analysis.py` |
| cyounghabs/mcp-server-solidworks [f8d72ff](https://github.com/cyounghabs/mcp-server-solidworks/commit/f8d72ff) and Bennyco86/q3ds-solidworks-mcp — © Benny Cohen 2026 | SOLIDWORKS Simulation API usage and pitfalls (CreateMesh unit argument, DispatchWrapper entity arrays, AddForce component values, FoS calls) | `SolidWorksService.Simulation.cs` (rewritten) |
| cyounghabs/mcp-server-solidworks [f8d72ff](https://github.com/cyounghabs/mcp-server-solidworks/commit/f8d72ff), [7033506](https://github.com/cyounghabs/mcp-server-solidworks/commit/7033506) | ideas only (own code): views laid out by their measured outlines (`auto_layout.py`), one model dimension kept per drawing (`_dedupe_dimensions`, priority front → top → right → iso) | `SolidWorksService.Drawing.Layout.cs` (`layout_drawing_views`, `dedupe_drawing_dimensions`, `add_dimension_between`) |

An earlier Python server by the same authors was fully re-implemented in C# (`SolidWorksService.Assembly.Manage.cs`, `SheetMetal.Production.cs`, `Images.cs`, `Features.Geometry.cs`).

Ideas taken (no code copied) from [wzyn20051216/solidworks-automation-skill](https://github.com/wzyn20051216/solidworks-automation-skill)
(MIT): the CalculiX cross-check with evidence statuses pass / warn / blocked / failed (`adapters/claude/calculix.py`,
`sim_crosscheck`) and the weldment tool set (`SolidWorksService.Weldments.cs`). Keep its copyright notice with any
code copied from it in the future.
