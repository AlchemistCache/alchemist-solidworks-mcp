# Developing Alchemist for SOLIDWORKS

## Layout

```
install.ps1                      installer (venv, dependencies, builds the exe, registers the server in Claude)
adapters/claude/                 the MCP adapter (Python)
  server.py                      builds the MCP server: registration, rules for every tool (errors, background
                                 jobs, one answer form, an unknown parameter is an error), tool groups
  registry.py                    registers every tools_*.py module
  session.py                     the ONLY path to the execution server: operation id, state version, resync
  analysis_reads.py face_compare.py face_dimension.py drawing_summary.py pair_check.py   pure helpers (no MCP, no
                                 SolidWorks): analyze_model payloads, face diffs, face → dimension, analyze_drawing
                                 texts, interference / clearance pairs
  units.py                       units at the boundary: mm / degrees for the model, SI for the server (in and out)
  bridge.py                      calling tools: call — a tool that is SI on the server (units.py), call_raw — the raw
                                 answer, call_mm / call_mm_json — a tool in mm (in the background after SYNC_WAIT), dump, with_image
  jobs.py                        background jobs ({"running": true, "job_id"}, job_result), BUSY, $-references in execute_batch
  verify.py                      verdicts of compare_parts / compare_assemblies (pure logic)
  tools_document.py              documents and files: start SW, new / open / activate / close / save, export, material,
                                 units, STEP import, batch export, macros
  tools_sketch.py                sketches: entities (one or many), text, relations, dimensions, define_sketch
  tools_features.py              part features: extrude / revolve / sweep / loft, rib, Hole Wizard, fillets, patterns,
                                 angled plane, axis by two points, pipe along a polyline, body visibility
  tools_drawing.py               drawings: views and their layout, dimensions, center marks, callouts, sections, BOM
                                 table, duplicate dimensions; check_drawing; analyze_drawing (DXF / DWG)
  tools_analysis.py              analyze_model, compare_parts / assemblies, dimension_for_face
  tools_core.py                  job_result, execute_batch
  tools_assembly.py              assemblies: reading, changing, subassemblies, mates, BOM / GOST specification
  tools_sheet_metal.py           sheet metal: summary, cut list, flat DXF, DFM, features
  tools_simulation.py            Simulation + hand checks (bolt, weld, beam), CalculiX cross-check, HTML report
  tools_weldments.py             weldments, GOST profiles
  tools_images.py                pictures of the model (one view, a view set)
  tool_docs.py                   compact descriptions + tool_help (topic help)
  guides/reading_drawings.md     the discipline for reading a DXF / DWG (rules R1–R23), tool_help('reading_drawings')
  tool_groups.py                 tool groups, turn_on_for (execute_batch runs a tool of a group that is off), enable_tools, usage log
  sheet_dxf.py sheet_reports.py gost_spec.py sim_report.py hand_calcs.py calculix.py   computation without SolidWorks
  execution_client.py            HTTP client of the execution server, starts the exe, timeouts
  response_mapper.py             server answer → ONE JSON object (answer_of) / error fail(code, text) → `CODE | message`
  config.py                      EVERY setting from the environment (.env next to the adapter), see below
  adapter_log.py                 the adapter's log adapter.log (traces of caught errors)
  tests/                         schema contract test, client, errors, bridge / session, pure helpers (pytest)
execution/solidworks/
  build.ps1                      builds the exe with Roslyn against the interop of the INSTALLED SolidWorks
  lib/                           Owin / WebApi / Newtonsoft (vendored)
  contracts/tool-schemas.json    the tool contract (checked against the adapter by a test; gen_contract.py)
  contracts/execution-response.json, state-format.json   the server's answer format (camelCase)
  SolidworksExecution/
    Controllers/ToolController.cs  tool → method routing; routes with no MCP tool (adapter only): model_snapshot
                                   (tree before / after), sim_export_mesh (sim_crosscheck), flat_geometry (sheet_metal_dfm)
    Services/SolidWorksService.*.cs — one partial class, files by subject (like the adapter's modules):
      .Envelope (the Run frame, parameters, error vocabulary)   .Common (shared helpers, listed in its header)
      .cs (documents: open / save / export, units, batch export, macros)   .Import (STEP / IGES)
      .Sketch   .Features  .Features.Geometry (plane / axis, pipe, loft, bodies)  .Features.HoleWizard
      .Analyze  .Analyze.Features (tree)  .Analyze.Topology (faces / edges, feature_map)
      .Drawing  .Drawing.Read  .Drawing.Geometry (reading a drawing)  .Drawing.Bom  .Drawing.Layout
      .Assembly.Build (insert, mates, analyze_assembly)  .Assembly.Manage (reading and changing an assembly)
      .Assembly.Mates (list / edit mates, patterns, insert_and_mate)
      .SheetMetal  .SheetMetal.Features  .SheetMetal.Production (summary, DXF)  .SheetMetal.Flat
      .Simulation  .Simulation.Assemblies (bolts, welds, contacts)  .Simulation.Image  .Simulation.Export
      .Weldments  .Weldments.Profiles   .Images (view_image)
cad-planner/                     the CAD-neutral DXF / DWG reader behind analyze_drawing (no SolidWorks, no MCP)
  drawing/                       dxf_read, contour, curvefit, viewgraph, pairing, vocab, wire, config.json; tests/
  contracts/draw-dialect.schema.json   the `draw` dialect the reader emits (checked against vocab.py by a test)
tools/                           dev scripts: run_steps, smoke_test, run_mcp (+ tools/mcp/*.json), golden (+ golden_*.json),
                                 doctor, gen_contract, gen_tools_doc, register_claude, pack_skills, usage_report;
                                 tools/out — results (not in git)
docs/                            documentation
macros/                          VBA macros for run_macro (not in git)
```

## Adding a tool

1. **C#**: a method in the fitting `Services/SolidWorksService.*.cs`, through the `Run` frame (`SolidWorksService.Envelope.cs`):
   ```csharp
   public ExecutionResponse MyTool(ToolRequest request) => Run(request, mutating: true, p =>
   {
       var d = ActiveDocOrFail();
       double len = PNum(p, "length_mm", 10) / MM;            // input in mm → SI for the API
       if (...) throw new ToolFail("CODE", "a clear message for the model");
       return new JObject { ["feature"] = f.Name };             // the answer's fields (response_mapper.answer_of)
   });
   ```
   `Run` takes care of idempotency, `state_version`, the SolidWorks connection, turning exceptions into errors and
   giving the active document back after read-only tools. Helpers: `PStr/PNum/PInt/PBool/PArr/PStrList`,
   `ActivePartOrFail` / `ActiveAsmOrFail` / `ActiveDrawingOrFail` / `ActivePartOrAsmOrFail`, `PartOrFail(doc, need)`,
   `CheckCreated` (a feature SolidWorks built with an error), `RollForward`, `Comps`, `FindComp`, `SelectByIndices`,
   `FlattenEdges`, `WithPartConfig`, `CaptureView`. Error codes: the vocabulary in the header of `SolidWorksService.Envelope.cs`.
2. **Route**: `case "my_tool": return _service.MyTool(request);` in `ToolController.cs`.
3. **Adapter**: a function with `@tool` inside `register(mcp, tool)` of the fitting `adapters/claude/tools_<area>.py`
   → `call_mm("my_tool", dict(...))` (a new module goes into `registry.MODULES`; `call_mm_json` when Python works on the
   answer). An error is `raise fail("CODE", "text")` (`response_mapper`), never a "FAILED | …" string. The docstring
   must be a string literal (not `"""…""" + X`; a shared tail is the `@doc_append` decorator from `bridge`);
   `test_tool_surface` checks descriptions and routes. The docstring IS the model's manual: what it does, units,
   where indices come from. Keep it short.
   Every millimetre tool gets the long HTTP timeout and the background mode: with no answer within `SWMCP_SYNC_WAIT`
   (45 s) the model gets `{"running": true, "job_id": …}` and collects the result with `job_result`.

   Every tool answers ONE JSON object (`response_mapper.answer_of`, guaranteed by `server._one_form`): `state_version`,
   `document`, `sketch` (while a sketch is open), the tool's fields, `created` / `removed` — what the call added to the
   tree or took away (the C# server diffs the tree before and after every changing call). An error is the text
   `CODE | message`. The exception is the reference tool `tool_help` (it returns a document).
4. **Contract and docs**: `.venv\Scripts\python.exe tools\gen_contract.py`, then `tools\gen_tools_doc.py` (it also
   refreshes the tool tables in both READMEs).
5. **Build and live test**:
   ```powershell
   Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process -Force
   powershell -ExecutionPolicy Bypass -File execution\solidworks\build.ps1
   .venv\Scripts\python.exe tools\run_mcp.py my_scenario.json
   ```
6. **Tests** (what CI runs, `.github/workflows/tests.yml`; `install.ps1 -Dev` installs the packages):
   ```powershell
   .venv\Scripts\python.exe -m ruff check --select F adapters/claude
   .venv\Scripts\python.exe -m ruff check --select F821,F811,F823 adapters tools cad-planner
   .venv\Scripts\python.exe -m pytest -q adapters/claude/tests cad-planner/drawing/tests
   ```
7. A new `.cs` file needs nothing else: both `build.ps1` and the `.csproj` take every `*.cs`.

Every handler (upstream's and ours) goes through `Run` — there is no other frame. A refactoring of an existing tool
is checked against golden answers: before rebuilding, `tools\golden.py record tools\golden_<scenario>.json` (old exe),
after — `golden.py check …` (prints every difference). Scenarios: `golden_upstream.json`, `golden_handlers.json`,
`golden_features.json`. The golden answers live in `tools\out\golden\` (not in git): on a fresh clone `record` first
on a build known to work. State versions are compared as increments, numbers to 6 significant digits.

## Settings (environment or `.env` next to the adapter)

| Variable | Read by | Default | What |
|---|---|---|---|
| `EXECUTION_BASE_URL` | adapter | http://localhost:5000 | the execution server (the name `localhost`, not an IP — see config.py) |
| `EXECUTION_EXE_PATH` | adapter | `bin\Debug\SolidworksExecution.exe` | what to start when the server does not answer |
| `HTTP_TIMEOUT` | adapter | 30 | timeout of an ordinary call, s |
| `LONG_HTTP_TIMEOUT` | adapter | 600 | timeout of long tools (Simulation, import, export), s |
| `CONNECT_TIMEOUT` | adapter | 10 | connecting to the execution server, s |
| `ENSURE_TIMEOUT` | adapter | 120 | `ensure_ready` (starting SolidWorks), s |
| `SERVER_SPAWN_TIMEOUT` | adapter | 20 | starting the execution server's exe, s |
| `SWMCP_SYNC_WAIT` | adapter | 45 | how long to wait before answering `{"running": true, "job_id": …}`, s |
| `SWMCP_DOCS` | adapter | compact | `full` — full tool descriptions |
| `SWMCP_DOC_LIMIT` | adapter | 500 | length of a compact description, characters |
| `SWMCP_DEV_TOOLS` | adapter | — | `1` — developer options (`sim_report engine=solidworks`, automates Word) |
| `SWMCP_CCX` | adapter | — | path to `ccx.exe` (CalculiX) for `sim_crosscheck` |
| `SWMCP_TOOLS` | adapter | auto | tool groups: `auto` (the defaults + those used lately), `all`, or a list: `assemblies,sheet_metal,simulation,drawings,weldments` (`base` always) |
| `SWMCP_USAGE_DAYS` | adapter | 30 | `auto` keeps a group on this many days after its last use |
| `SWMCP_USAGE_LOG` | adapter | `adapters/claude/usage.jsonl` | the usage log (tool, group, time, duration, success — never the arguments); `off` — none |
| `SWMCP_SW_EXE` | C# server | `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe` | what to start for the server's own hidden instance |
| `SWMCP_MACROS_DIR` | C# server | `<repo>\macros` | where `run_macro` takes macros from |
| `SWMCP_PROFILES` | C# server | `%USERPROFILE%\SolidWorksMCP\weldment profiles` | weldment profiles |
| `SWMCP_SW_DIR` | `tools/doctor.py` | `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS` | where to check SolidWorks and Simulation; `-SwApiDir <folder>\api\redist` (as for `install.ps1`) wins |
| `SWMCP_SW_SAMPLES` | `tools/run_steps.py` | `C:\Users\Public\Documents\SOLIDWORKS\SOLIDWORKS 2024\samples` | `{SW_SAMPLES}` in run_steps, run_mcp and golden scenarios |

The adapter reads variables only in `config.py`; the C# server in the three places in the table (from the adapter's
`.env` they reach the exe when the adapter starts it; a running server does not re-read them); the dev scripts the
last two.

## run_steps / run_mcp scenarios

`tools/run_steps.py` runs a JSON scenario straight into the execution server (no Claude, past the adapter, so in the
server's units: upstream tools take metres, see `units.py`). A step is `["tool", {params}]`, `["ready"]` is
`ensure_ready`, a third element `"expect_fail"` marks an expected error. Placeholders: `{OUT}` (tools\out),
`{SW_SAMPLES}` (the SolidWorks 2024 samples, `SWMCP_SW_SAMPLES`), `{REPO}`. Example: `tools/smoke_test.json`
(sheet → DXF → cut list → assembly → interference → Simulation → stress map). `tools/run_mcp.py` takes the same
format through the MCP tools, as Claude calls them (mm, lists, the adapter's answers); scenarios in `tools/mcp/*.json`.

## SolidWorks 2024 API notes

What we found the hard way on a live SolidWorks 2024 is collected in [SOLIDWORKS_API_NOTES.md](SOLIDWORKS_API_NOTES.md).

## Branches and releases

The exe and `bin/` are not kept in git — `install.ps1` / `build.ps1` build them against the installed SolidWorks.
The upstream project is [eyfel/mcp-server-solidworks](https://github.com/eyfel/mcp-server-solidworks); its C# files
have diverged from ours a long time ago.
