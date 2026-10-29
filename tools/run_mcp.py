"""Run a scenario through the MCP TOOLS (the adapter, exactly as Claude calls them) against the live execution server —
the model's view: millimetres, lists, the answer texts. tools/run_steps.py talks to the C# server directly (SI).

    .venv\\Scripts\\python.exe tools\\run_mcp.py tools\\mcp\\units.json [max_chars_per_result]

Scenario = JSON list of steps: ["tool_name", {args}] or ["tool_name", {args}, "expect_fail"]; placeholders as in
run_steps ({OUT}, {SW_SAMPLES}, {REPO}). Every tool group is on; background jobs run to the end (no RUNNING handles).
Exit code 1 if a step fails unexpectedly."""
import asyncio
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.environ["SWMCP_TOOLS"] = "all"
os.environ["SWMCP_USAGE_LOG"] = "off"     # test runs are not the user's usage
sys.path.insert(0, os.path.join(REPO, "adapters", "claude"))
sys.path.insert(0, os.path.join(REPO, "tools"))
import execution_client  # noqa: E402
execution_client.HTTP_TIMEOUT = execution_client.LONG_HTTP_TIMEOUT
import server  # noqa: E402
import jobs  # noqa: E402
from run_steps import OUT, SUBS  # noqa: E402


def _subst(v):
    if isinstance(v, str):
        for k, s in SUBS.items():
            v = v.replace(k, s)
        return v
    if isinstance(v, list):
        return [_subst(x) for x in v]
    if isinstance(v, dict):
        return {k: _subst(x) for k, x in v.items()}
    return v


def _text(res):
    items = res if isinstance(res, (list, tuple)) else getattr(res, "content", [res])
    return " ".join(getattr(c, "text", "") or "" for c in items)


async def main(path, limit):
    os.makedirs(OUT, exist_ok=True)
    steps = json.load(open(path, encoding="utf-8"))
    jobs.in_batch.set(True)          # synchronous: a long call returns its result, not RUNNING
    bad = 0
    for step in steps:
        name, args = step[0], _subst(step[1] if len(step) > 1 else {})
        expect_fail = len(step) > 2 and step[2] == "expect_fail"
        try:
            text, ok = _text(await server.mcp.call_tool(name, args)), True
        except Exception as ex:  # noqa: BLE001 — a tool error is the answer here
            text, ok = f"{type(ex).__name__}: {ex}", False
        mark = "ok" if ok != expect_fail else "UNEXPECTED"
        bad += mark != "ok"
        print(f"== {name:24s} {'COMPLETED' if ok else 'FAILED':9s} {mark}  {text[:limit]}")
    print("\nALL OK" if not bad else f"\n{bad} step(s) failed")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 400)))
