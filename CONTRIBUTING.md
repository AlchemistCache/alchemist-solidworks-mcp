# Contributing Guide

Thanks for contributing to Alchemist for SOLIDWORKS. This document summarizes how to set up the development environment, the architectural rules, and the flow for adding a new capability.

Alchemist for SOLIDWORKS is an MCP server that lets Claude model, assemble, check and draw in SolidWorks. For an overview, see [README.md](README.md).

---

## Architecture and invariants

```
Claude ⇄ MCP ⇄ adapter (Python, adapters/claude) ⇄ HTTP ⇄ SolidworksExecution.exe (C#) ⇄ COM ⇄ SOLIDWORKS
```

The system has two layers and one library, and preserving these boundaries is the foundation of the project:

| Part | Directory | Responsibility |
|---|---|---|
| `adapters/claude` | `adapters/claude/` | MCP protocol bridge (Python, MCP SDK v2): the tools the model calls, units, one answer form. No COM. |
| `execution/solidworks` | `execution/solidworks/` | The only layer that touches SolidWorks COM (C#, .NET 4.8). |
| `cad-planner` | `cad-planner/` | The CAD-neutral DXF/DWG drawing reader behind `analyze_drawing` (`drawing/`, contract `contracts/draw-dialect.schema.json`). No COM, no MCP. |

Rules that must never be violated:

- Only the execution layer (`Services/SolidWorksService.*.cs`, one partial class) touches COM.
- **MCP is the top boundary** (between the client and the adapter), not an internal transport. The adapter reaches the execution layer over plain REST (`session.call`).
- The drawing reader is **CAD-neutral**: it knows DXF/DWG geometry, not SolidWorks; the model builds the part from its output with the ordinary tools.
- The execution layer and the drawing reader have no knowledge of which client is calling them.
- The adapter layer is provider-specific. Supporting a new AI client (OpenClaw, OpenAI, a local LLM, etc.) means only adding a new adapter that reuses the shared bridge core.

---

## Development environment

### Requirements
- Windows and **SolidWorks 2024 SP1** (this fork's target; the build compiles against the INSTALLED SolidWorks interop).
- **.NET Framework 4.8** (runtime). No Visual Studio needed: `execution/solidworks/build.ps1` downloads the Roslyn compiler once.
- **Python 3.11+** and the official **MCP Python SDK v2** (`mcp>=2.0.0`) for the MCP adapter.

One command sets everything up — the `.venv`, the adapter's dependencies, the exe build and the registration in
Claude; `-Dev` adds the test tools (pytest, ruff):

```
powershell -ExecutionPolicy Bypass -File install.ps1 -Dev
```

`.venv\Scripts\python.exe tools\doctor.py` checks an installation (SolidWorks, exe, port, Python packages).

### Building and running the execution layer

Build:

```
powershell -ExecutionPolicy Bypass -File execution\solidworks\build.ps1
```

(Stop the running server first. The csproj compiles `**\*.cs` as well, so Visual Studio needs no file list either.)

Restart (headless, `http://localhost:5000`):

```
Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process execution\solidworks\SolidworksExecution\bin\Debug\SolidworksExecution.exe -WindowStyle Hidden
```

The server runs headless and must be running while SolidWorks is open (the COM connection is established lazily, on the first tool call). C#-side errors and per-request traces are written to `execution\solidworks\SolidworksExecution\bin\Debug\execution.log`.

### Running the adapter

The MCP client starts it (`install.ps1` registers `.venv\Scripts\python.exe adapters\claude\server.py` in Claude).
It does not hot-reload: after changing a tool, reconnect the MCP server. Either side may restart — the adapter
resyncs its `state_version` once from `GET /api/tool/state` when the server answers `INVALID_STATE_VERSION`
(`adapters/claude/session.py`).

---

## Adding a new capability

A new capability is a new tool, or a new mode of an existing one; the model calls the tools directly.

### A new tool

1. **Contract** — `tools/gen_contract.py` adds the tool to `execution/solidworks/contracts/tool-schemas.json` from the
   adapter's schema (step 3); `tools/gen_tools_doc.py` regenerates `docs/TOOLS.md`.
2. **Execution** — add a `case "tool_name":` in `ToolController.cs` and implement it in the matching `SolidWorksService.<Family>.cs` **partial class** under `Services/` (one class split by tool family; every handler uses the `Run(request, mutating, p => …)` envelope in `SolidWorksService.Envelope.cs` with `ToolFail(code, message)` — see docs/DEVELOPMENT.md). **Verify any SolidWorks COM API signature by reflecting the interop assembly first — never invent a method name or argument list.** The real API frequently differs from what looks plausible (for example, the model-item insertion API lives on `IDrawingDoc`, not `IView`; `GetLines3` returns empty while `GetPolylines7` is the working geometry getter). Decode unknown return shapes empirically against a live document before writing the parser.
3. **Adapter** — add the MCP tool with `@tool` inside `register(mcp, tool)` of the matching `adapters/claude/tools_<family>.py` (a new module goes into `registry.MODULES`; `server.py` only assembles the server). Model-facing guidance lives here (the client never sees `tool-schemas.json`): use `Literal` enums for discriminators, Pydantic `Field` constraints for units and ranges, and real required parameters.
4. **Verify** — run the tests (see below), then validate against live SolidWorks (`tools/run_steps.py` scenario;
   for a change to an existing handler, `tools/golden.py` before/after).

---

## Conventions

- **Units:** every MCP tool takes and returns millimetres and degrees. The execution server stays SI (meters, radians); a tool whose C# side is SI lists its length parameters in `adapters/claude/units.py` (`INPUT_LENGTHS`), and its answer is converted there by key (`response_to_mm`) — the test `test_every_length_is_millimetres` checks the table against the schemas. New tools take `_mm` parameters on both sides. Angles in degrees at the adapter boundary. `tool_help('units')` is the model-facing summary.
- **HTTP semantics:** all CAD results return HTTP 200; `FAILED` and `DUPLICATE` are domain states, not transport errors. HTTP 400 is only for malformed requests or unknown tool names.
- **state_version:** every request is checked with strict equality; a mismatch returns `FAILED` with `INVALID_STATE_VERSION`. The adapter takes the version from every response (a mutating tool that failed after changing the model advances it too) and resyncs once on a mismatch. Wire format: `execution/solidworks/contracts/execution-response.json`.
- **Array parameters:** every list is a real `list[...]` / `list[dict]` parameter (MCP SDK v2 passes them correctly) — no JSON text in a string parameter.
- **C# handlers:** every handler is `Run(request, mutating, p => …)`; read parameters only with `PStr / PNum / PInt / PBool / PArr / PStrList` (one rule for "not given" and one error text), check the document kind with `Active{Part,Asm,Drawing}OrFail` / `PartOrFail(doc, need)`, fail with `ToolFail(code, message)` from the vocabulary in the header of `SolidWorksService.Envelope.cs`. Serialization is camelCase and null values are omitted.
- **swconst enums:** use the named constants, not magic numbers; the interop DLL is copied next to the exe, so an enum used as a type (`((swFeatureError_e)code).ToString()`) loads fine.

---

## Testing

- **All offline suites** (what CI runs, `.github/workflows/tests.yml`):

  ```
  .venv\Scripts\python.exe -m ruff check --select F adapters/claude
  .venv\Scripts\python.exe -m ruff check --select F821,F811,F823 adapters tools cad-planner
  .venv\Scripts\python.exe -m pytest -q adapters/claude/tests cad-planner/drawing/tests
  ```

- **Contract test:** catches any tool or parameter drift between the adapter's tools and `tool-schemas.json`, and
  checks that every function of the drawing package the adapter calls is actually exported by it (a renamed function
  would otherwise fail only when `analyze_drawing` runs). It is part of the adapter suite above
  (`adapters/claude/tests/test_schema_contract.py`).

- **Drawing reader suite:** `cad-planner/drawing/tests/` checks the `draw` dialect against
  `cad-planner/contracts/draw-dialect.schema.json` and replays frozen fixtures (contour chaining, bend-note pairing) —
  fully offline, no DXF library needed.

- **Golden responses (C# refactors):** `tools/golden.py record tools/golden_<scenario>.json` with the old exe, rebuild,
  then `golden.py check …` prints every changed response (state-version deltas, floats to 6 significant digits).
  Baselines live in `tools/out/golden/` (not in git): on a fresh clone, record them first from a known-good build.

- **Live testing (manual by design):** tools are verified against live SolidWorks, with the GUI open, case by case. A cohesive batch of tools is chosen together and tested as a batch. When a tool fails, inspect `execution.log` and report the expected result, the API response, and a hypothesis. For drawing tools, the exported **PDF is the ground truth** — some interop counters under-report (e.g. inserted annotations / center marks), so confirm a drawing change by exporting and reading the PDF rather than trusting an in-band count alone.

The C# layer is not built in CI — it requires the licensed SolidWorks interop assemblies; C# behavior is verified live instead.

---

## License of contributions

Alchemist for SOLIDWORKS is free software under the [GNU AGPL-3.0](LICENSE), like the SolidPilot project it is forked from.
By submitting a contribution (a pull request, a patch or any other work) you agree that it is licensed under the same
AGPL-3.0 terms (inbound = outbound), and you confirm that you have the right to contribute it. Please sign off your
commits (`git commit -s`, the [Developer Certificate of Origin](https://developercertificate.org/)).

---

## Commits and Pull Requests

- Write meaningful, focused commits; do not mix several unrelated changes in one commit.
- If you change the behavior of a tool or feature, keep the relevant contract and tests up to date.
- In the Pull Request description, state what you changed, why, and how you verified it.
- Sign off your commits (`git commit -s`).

For questions and discussions, feel free to open an issue.
