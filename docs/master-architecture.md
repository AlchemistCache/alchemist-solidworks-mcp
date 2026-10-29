## REPOSITORY ARCHITECTURE

> **Note (Alchemist for SOLIDWORKS, 2026-10-27):** this is the original SolidPilot design document, kept as history.
> This fork removed the Feature Graph IR path — the compiler (`compiler/solidworks`), the recipe,
> `submit_feature_graph` / `rebuild_from_ir` and the IR schema — on 2026-10-27: it went unused, and the sketches it
> built carried no dimensions. Here the model calls the CAD tools directly
> (Claude ⇄ MCP ⇄ adapter ⇄ REST ⇄ execution ⇄ COM ⇄ SolidWorks; see [CONTRIBUTING.md](../CONTRIBUTING.md)), and
> `cad-planner/` holds only the DXF/DWG drawing reader. The rest of this document describes upstream's design.

mcp-server-solidworks (public name: **SolidPilot**) is an AI-driven CAD automation system for SolidWorks.

The defining idea: the AI works at the **CAD feature level**, not the tool level. The host model expresses *intent* as a CAD-neutral **Feature Graph IR**; a deterministic compiler lowers that IR into concrete SolidWorks operations. This solves the core economic problem — the SolidWorks API has thousands of methods, and exposing each as a flat tool explodes context size and token cost.

---

## SYSTEM DATA FLOW (DECIDED)

```
                 User
                  │
                  ▼
        AI Planner / Intent Layer            ← Phase 1: the host model (Claude) itself
          (cad-planner, CAD-neutral)
                  │   emits
                  ▼
          Feature Graph IR                   ← cad-planner/contracts/feature-graph.schema.json
                  │
   ══════════════ MCP BOUNDARY ══════════════  ← host submits IR via ONE tool: submit_feature_graph(graph)
                  │
                  ▼
      Deterministic CAD Compiler             ← compiler/solidworks (NO LLM, NO MCP)
        (+ Reference Resolver)
                  │   REST
                  ▼
          Execution Layer                    ← execution/solidworks (C#, only layer touching COM)
                  │   COM
                  ▼
             SolidWorks
```

**MCP sits at the TOP** — it is the boundary where the AI host meets the system, exposing the feature-level interface. Everything **below** the IR is deterministic internal machinery reached over plain REST; MCP is never used as an internal transport.

---

## LAYERS

### 1. cad-planner (CAD-neutral) — AI Planning / Intent Layer
Converts user intent into a CAD-neutral **Feature Graph IR**. Operates at the feature level, never the tool level.
- **Phase 1:** realized as the host model (Claude) + the IR schema + a planning prompt. No separate service.
- **Phase 2:** optional dedicated/local-LLM planner for near-zero cost. Same IR contract.
- Validates **structure** only (types, params, node references). NOT geometry.
- Does NOT touch SolidWorks, does NOT execute, does NOT emit raw tool sequences.

### 2. compiler/solidworks (SolidWorks-specific) — Deterministic CAD Compiler
Lowers the IR into ordered execution-layer tool calls. **Deterministic — no LLM, no MCP.**
- **Reference Resolver:** resolves semantic refs (`top_face`, `center`) to concrete selectors/coords against live B-rep state — the hardest, make-or-break module.
- Performs **geometric** validation (face exists, fit, selector uniqueness).
- Maps low-level failures back up to feature-level errors so recovery isn't lost.
- A different CAD backend = a different compiler; the IR and cad-planner stay unchanged.

### 3. execution/solidworks (SolidWorks-specific) — Execution Layer / Truth Engine
C# .NET 4.8 REST + COM. The **only** layer allowed to touch SolidWorks COM. Deterministic execution, authoritative CAD state, idempotency + `state_version`. NO AI, NO planning. Adapter- and compiler-agnostic.

### 4. adapters/* — MCP Protocol Bridge
Exposes the system to a specific AI host over MCP.
- **Target:** one high-level tool `submit_feature_graph(graph)` (+ a few reads).
- **Phase 1:** still exposes the low-level tools directly (the full list: `docs/TOOLS.md`) so the working path runs while the IR vocabulary grows; the modelling tools collapse into the IR tool once the vocabulary and resolver are ready.
- Every MCP tool speaks millimetres and degrees (`adapters/claude/units.py` converts at the boundary; the execution layer and the IR stay SI) and answers ONE JSON object (`state_version`, `document`, the tool's fields, `created` / `removed`); a failure is `CODE | message`.
- In this fork (Alchemist for SOLIDWORKS) the direct tools — modelling, assemblies, sheet metal, SOLIDWORKS Simulation, weldments, drawings for production — are the whole, permanent surface; the IR door was removed (see the note at the top).
- `adapters/claude/` is the current implementation. Additional adapters reuse a shared bridge core.

---

## CONTRACTS

| Contract | Location | Owner | Nature |
|---|---|---|---|
| `feature-graph.schema.json` | `cad-planner/contracts/` | Planner | CAD-NEUTRAL IR **+ capability registry** (single artifact) |
| `tool-schemas.json` | `execution/solidworks/contracts/` | Execution | the MCP tool surface (checked against the adapter by `test_schema_contract`); internal server routes such as `model_snapshot` are not tools and are not in it |
| `state-format.json` | `execution/solidworks/contracts/` | Execution | CadState structure |
| `execution-response.json` | `execution/solidworks/contracts/` | Execution | the server's wire format: COMPLETED / FAILED / DUPLICATE (the adapter turns it into the one JSON answer) |

The capability registry **is** the IR schema — the Planner validates intent against that one file; there is no separate discovery protocol.

---

## VALIDATION SPLIT (DECIDED)

- **Plan-time (Planner / IR schema):** structural + schema validation only — registered types, required params present, node references structurally resolvable, units correct.
- **Compile/exec-time (Compiler + Resolver + Execution):** geometric validity — referenced face actually exists, hole fits, selector resolves to exactly one entity.

The Planner cannot validate geometry; it has no live B-rep state. Do not conflate the two.

---

## ARCHITECTURAL INVARIANTS

- The AI works at feature level; only the compiler knows tools; only execution knows COM.
- MCP is the **top** boundary (host ↔ IR), never an internal transport.
- The IR is **CAD-neutral**; SolidWorks specifics live only in compiler + execution. The schema has no backend-specific escape hatch: what the neutral vocabulary cannot express is recorded as a `VOCABULARY_GAP` (recipe rule C5) and done with the direct tools instead.
- Execution and Planner have zero knowledge of which adapter/host is calling.
- Adding a CAD backend = new compiler + execution; the IR and cad-planner are unchanged.
- The compiler is the "second planner" — but **deterministic**. What we forbid is a second *LLM* re-doing the host's reasoning.

---

## DESIGN GOALS

- Deterministic, reproducible CAD execution.
- Low token cost: one LLM call (intent → IR) per request; everything below is deterministic.
- AI-model-agnostic and (eventually) CAD-system-agnostic.
- Strict separation of intent vs implementation.

> **Build discipline:** the IR vocabulary and reference model are DRAFT and must be **derived from real production designs, not invented ahead of them.** See the [README](../README.md) for the current status and roadmap.
