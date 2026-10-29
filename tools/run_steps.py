"""Run a JSON scenario directly against the execution server (no Claude needed) — for development and smoke tests.

    .venv\\Scripts\\python.exe tools\\run_steps.py tools\\smoke_test.json [max_chars_per_result]

Scenario = JSON list of steps: ["tool_name", {params}]; the pseudo-step ["ready"] calls ensure_ready.
Placeholders inside strings:
    {OUT}        tools\\out (created; ignored by git)
    {SW_SAMPLES} C:\\Users\\Public\\Documents\\SOLIDWORKS\\SOLIDWORKS 2024\\samples  (env SWMCP_SW_SAMPLES overrides)
    {REPO}       the repository root
Exit code 1 if any step FAILED (unless the step is ["tool", {...}, "expect_fail"]).
"""
import json
import os
import sys
import uuid

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO, "adapters", "claude"))
import execution_client  # noqa: E402
from execution_client import call_tool, ensure_ready, get_state  # noqa: E402

execution_client.HTTP_TIMEOUT = execution_client.LONG_HTTP_TIMEOUT   # dev runs: whole assemblies take a while

OUT = os.path.join(REPO, "tools", "out")
SUBS = {
    "{OUT}": OUT,
    "{SW_SAMPLES}": os.environ.get("SWMCP_SW_SAMPLES",
                                   r"C:\Users\Public\Documents\SOLIDWORKS\SOLIDWORKS 2024\samples"),
    "{REPO}": REPO,
}


def resolve(value, results):
    """"$last.<path>" / "$<n>.<path>" (n = 0-based step) into an earlier step's result_geometry / features."""
    if isinstance(value, str) and value.startswith("$") and "." in value:
        head, path = value[1:].split(".", 1)
        node = results[-1] if head == "last" else results[int(head)]
        for part in path.split("."):
            node = node[int(part)] if isinstance(node, list) else node[part]
        return node
    if isinstance(value, list):
        return [resolve(v, results) for v in value]
    if isinstance(value, dict):
        return {k: resolve(v, results) for k, v in value.items()}
    return value


def main():
    os.makedirs(OUT, exist_ok=True)
    raw = open(sys.argv[1], encoding="utf-8-sig").read()
    for k, v in SUBS.items():
        raw = raw.replace(k, v.replace("\\", "\\\\"))
    limit = int(sys.argv[2]) if len(sys.argv) > 2 else 1500
    failed = 0
    results = []
    for step in json.loads(raw):
        tool, params = step[0], (step[1] if len(step) > 1 else {})
        expect_fail = len(step) > 2 and step[2] == "expect_fail"
        if tool == "ready":
            r = ensure_ready()
            print(f"== ready  comAttached={r.get('comAttached')} sw={r.get('swVersion')} own={r.get('ownInstanceMode')}")
            continue
        try:
            params = resolve(params, results)
        except (KeyError, IndexError, ValueError, TypeError) as ex:
            print(f"== {tool:28s} BAD REF {ex}"); failed += 1; results.append({}); continue
        r = call_tool(tool, str(uuid.uuid4()), get_state(), params)
        status = r.get("status")
        ok = (status == "FAILED") == expect_fail
        failed += 0 if ok else 1
        rg = r.get("result_geometry")
        entry = dict(rg) if isinstance(rg, dict) else {"result": rg}
        entry["features"] = (r.get("cadState") or {}).get("features")
        results.append(entry)
        body = rg
        if body is None:
            body = r.get("error") if status == "FAILED" else (r.get("cadState") or {}).get("features")
        s = json.dumps(body, ensure_ascii=False)
        print(f"== {tool:28s} {status:9s} {'ok' if ok else 'UNEXPECTED'}  {s[:limit]}", flush=True)
    print(f"\n{'ALL OK' if not failed else f'{failed} step(s) failed'}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
