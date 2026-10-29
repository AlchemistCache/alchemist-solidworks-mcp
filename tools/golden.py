"""Golden responses of the execution server — guard for refactorings that must not change what a tool returns.

    .venv\\Scripts\\python.exe tools\\golden.py record tools\\golden_upstream.json   # with the OLD build
    .venv\\Scripts\\python.exe tools\\golden.py check  tools\\golden_upstream.json   # with the NEW build

A scenario is the run_steps format (["tool", {params}], ["ready"]); placeholders {OUT} {SW_SAMPLES} {REPO}. Each
response is normalized (operation ids, times, numbered new-document titles like «Деталь17» / «Part3») and stored in
tools/out/golden/<scenario>.json; `check` prints every difference and exits 1 if there is one."""
import json
import os
import re
import sys
import uuid

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO, "adapters", "claude"))
sys.path.insert(0, os.path.join(REPO, "tools"))
import execution_client  # noqa: E402
from execution_client import call_tool, ensure_ready, get_state  # noqa: E402
from run_steps import SUBS, OUT, resolve  # noqa: E402

execution_client.HTTP_TIMEOUT = execution_client.LONG_HTTP_TIMEOUT
_TITLE = re.compile(r"(Деталь|Сборка|Чертеж|Part|Assem|Draw)\d+")
_DROP = {"operationId", "serverTimeUtc", "seconds", "solve_seconds"}


def normalize(obj):
    if isinstance(obj, dict):
        return {k: normalize(v) for k, v in obj.items() if k not in _DROP}
    if isinstance(obj, list):
        return [normalize(v) for v in obj]
    if isinstance(obj, str):
        return _TITLE.sub(lambda m: m.group(1) + "#", obj)
    if isinstance(obj, float):
        return float(f"{obj:.6g}")   # significant digits: volumes in m³ are ~1e-5
    return obj


def run(scenario):
    raw = open(scenario, encoding="utf-8-sig").read()
    for k, v in SUBS.items():
        raw = raw.replace(k, v.replace("\\", "\\\\"))
    out, results = [], []
    for step in json.loads(raw):
        tool, params = step[0], (step[1] if len(step) > 1 else {})
        if tool == "ready":
            ensure_ready()
            continue
        r = call_tool(tool, str(uuid.uuid4()), get_state(), resolve(params, results))
        rg = r.get("result_geometry")
        results.append(dict(rg) if isinstance(rg, dict) else {"result": rg})
        out.append({"tool": tool, "response": normalize(r)})
        print(f"== {tool:24s} {r.get('status')}", flush=True)
    return out


def relative(entries):
    """state_version as the step's change (+1 / 0) instead of the absolute counter, which depends on the server's past."""
    prev, out = None, []
    for e in entries:
        e = json.loads(json.dumps(e))
        r = e["response"]
        sv = r.get("stateVersion")
        if isinstance(sv, int):
            r["stateVersion"] = 0 if prev is None else sv - prev
            if isinstance(r.get("cadState"), dict) and isinstance(r["cadState"].get("stateVersion"), int):
                r["cadState"]["stateVersion"] = r["stateVersion"]
            prev = sv
        out.append(e)
    return out


def diff(a, b, path=""):
    if type(a) is not type(b):
        return [f"{path}: {a!r} != {b!r}"]
    if isinstance(a, dict):
        res = []
        for k in sorted(set(a) | set(b)):
            res += diff(a.get(k), b.get(k), f"{path}.{k}")
        return res
    if isinstance(a, list):
        if len(a) != len(b):
            return [f"{path}: {len(a)} items != {len(b)}"]
        return [d for i, (x, y) in enumerate(zip(a, b)) for d in diff(x, y, f"{path}[{i}]")]
    return [] if a == b else [f"{path}: {a!r} != {b!r}"]


def main():
    mode, scenario = sys.argv[1], sys.argv[2]
    store = os.path.join(OUT, "golden", os.path.splitext(os.path.basename(scenario))[0] + ".json")
    got = run(scenario)
    if mode == "record":
        os.makedirs(os.path.dirname(store), exist_ok=True)
        json.dump(got, open(store, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"recorded {len(got)} responses → {store}")
        return
    json.dump(got, open(store.replace(".json", ".got.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    want = json.load(open(store, encoding="utf-8"))
    problems = diff(relative(want), relative(got))
    for p in problems:
        print("DIFF", p)
    print("GOLDEN OK" if not problems else f"{len(problems)} difference(s)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
