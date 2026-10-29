# adapters/claude — Architecture

Role: **MCP Adapter / Protocol Bridge (Claude)**

The communication bridge between the AI host (Claude) and the system: translation and routing, no planning logic and no COM.
CAD logic lives here only where it needs no SolidWorks — calculations on data the execution layer returned
(hand checks, CalculiX cross-check, GOST specification, DXF reading, reports).

## The MCP boundary sits at the TOP

This is the key architectural decision: **MCP is the boundary where the AI host meets the system — not an internal transport.**

- **The model calls the tools directly.** The adapter exposes the CAD tools themselves (the list: `docs/TOOLS.md`) —
  modelling, assemblies, sheet metal, SOLIDWORKS Simulation, weldments, drawings. Everything below the adapter is
  reached over plain REST, **not** MCP.
- **Grouped and compacted, to keep the context small.** Tools come in groups (`tool_groups.py`): `base` is always
  on, `assemblies` is on by default; `sheet_metal`, `simulation`, `drawings` and `weldments` start on when used in
  the last 30 days. A tool of a group that is off runs through `execute_batch`, which turns its group on — no client
  has to re-read its tool list mid-conversation (`enable_tools` asks it to, not every client does). Descriptions are trimmed (`tool_docs.py`); the full text and topic help are in
  `tool_help`.
- **No MCP resources.** Reference prose the model needs only sometimes is a `tool_help` topic, paid for only when read.
- **Reading a drawing:** `analyze_drawing` reads a DXF / DWG with the CAD-neutral reader in `cad-planner/drawing`
  and returns `{source, advisories, next, analysis}`; the reading discipline is `tool_help('reading_drawings')`
  (`guides/reading_drawings.md`). The model then builds the part with the ordinary tools (`create_sketch` →
  `add_sketch_entities` → `define_sketch` → `extrude_feature` / `sheet_metal_feature` …).

```
Claude ──MCP: the CAD tools──▶ [adapter] ──REST──▶ SolidworksExecution.exe ──COM──▶ SolidWorks
```

## Responsibilities

- Implement the MCP protocol server (official MCP Python SDK v2 — `MCPServer`, stdio): `server.py` assembles it,
  `registry.py` registers the `tools_<family>.py` modules.
- Translate host tool calls into Execution Layer REST requests — one call path, `session.call`: a UUID4
  `operation_id` per call, the tracked `state_version` (one resync on `INVALID_STATE_VERSION`).
- Run long tools in the background and resolve `execute_batch`'s `$refs` — both in `jobs.py` (`{"running": true,
  "job_id"}`, `BUSY` while a job owns SolidWorks); the tools `job_result` / `execute_batch` are in `tools_core.py`,
  `bridge.py` calls the server and hands the slow calls to `jobs.background`.
- Convert units at the boundary (`units.py`): the model speaks millimetres and degrees, the execution server stays SI.
- Map `ExecutionResponse` → MCP result (`response_mapper.py`): a success is ONE JSON object (`state_version`,
  `document`, the tool's fields, `created` / `removed`); a failure is a `ToolError` reading `CODE | message`.

## Constraints

- No planning logic, no COM; nothing that needs SolidWorks itself.
- Provider-specific shell only — the reusable bridge core (id/version/mapping/client) should be factored out so additional adapters (OpenAI, etc.) reuse it instead of re-implementing.

## Model-facing guidance lives HERE, not in the contracts

The host model only ever sees the **MCP tool definition** (docstring → description, type hints → input schema). It does **not** see `tool-schemas.json`. Therefore tool-usage guidance that prevents the model from sending bad data (enums, units, required params, value ranges) must be encoded in the MCP layer — via `Literal` types and Pydantic `Field` constraints — and `tool-schemas.json` must be kept in sync by a contract test, not hand-maintained in parallel (see `tests/test_schema_contract.py`; it runs in CI on every push).
