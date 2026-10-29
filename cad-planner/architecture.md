# cad-planner — Architecture

Role: **the DXF/DWG drawing reader (CAD-neutral)**

This folder holds only the reader behind the `analyze_drawing` tool (drawings group) and its contract:

```
cad-planner/
  drawing/                             the reader (Python package) and its tests
  contracts/draw-dialect.schema.json   the `draw` dialect it emits
```

## What it does

It reads a technical drawing completely and hands the model the whole reading. It parses DXF; a DWG is first
exported to DXF by SolidWorks — the adapter does that, not this folder.

```
DXF/DWG ─▶ dxf_read (`draw` dialect) ─▶ contour (closed loops, open chains) ─▶ wire (compact form) ─▶ the model
```

- `dxf_read` — DXF → `draw` dialect (needs ezdxf); the only module that touches DXF.
- `contour`, `curvefit`, `viewgraph`, `pairing` — loops and chains, ellipses from exploded curve fans, view frames
  and alignment, bend notes paired with bend lines. Pure geometry, no dependencies.
- `vocab` — the dialect's vocabulary; `wire` — the compact form the model reads; `config.json` — drafting and
  manufacturing conventions (projection standard, K-factor, bend-note conventions, tolerances).

`analyze_drawing` returns `{source, advisories, next, analysis}`. How to read it — projection, faces, sheet metal,
the silent mistakes — is the guide `tool_help('reading_drawings')` (`adapters/claude/guides/reading_drawings.md`).
The model then builds the part with the ordinary tools: `create_sketch` → `add_sketch_entities` → `define_sketch`
(fully defined, design dimensions) → `extrude_feature` / `sheet_metal_feature` …

## Constraints

- No SolidWorks, no COM, no MCP: it knows drawing geometry, not CAD tools. The adapter imports it; the execution
  layer never sees it.
- It reads and reports; it does not decide how the part is built.

## Contract and tests

- `contracts/draw-dialect.schema.json` — what the model is told the dialect contains.
- `drawing/tests/test_draw_contract.py` — the schema against `vocab.py` in both directions, plus goldens on frozen
  `draw` fixtures (chaining, bend pairing, view graph, curve fit). Offline, no ezdxf needed; runs in CI.
