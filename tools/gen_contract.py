"""Generate execution/solidworks/contracts/tool-schemas.json entries from the adapter's MCP schemas (keeps
tests/test_schema_contract.py green after adding or changing a tool).

    .venv\\Scripts\\python.exe tools\\gen_contract.py

Generated entries ("[mm/deg/kg] …") are regenerated on every run and missing tools are added; the hand-written
upstream entries are kept as they are — edit those by hand. Entries of removed tools are deleted by hand too: the
contract lists exactly the MCP tools (test_schema_contract), internal server routes such as model_snapshot are not in it.
"""
import asyncio
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.environ["SWMCP_DOCS"] = "full"   # the generated files carry the full descriptions
os.environ["SWMCP_TOOLS"] = "all"   # and every tool group, also the ones that are off by default
os.environ["SWMCP_USAGE_LOG"] = "off"
sys.path.insert(0, os.path.join(REPO, "adapters", "claude"))
import server  # noqa: E402

PATH = os.path.join(REPO, "execution", "solidworks", "contracts", "tool-schemas.json")
GENERATED = "[mm/deg/kg]"   # the description prefix of a generated entry


def main():
    raw = open(PATH, encoding="utf-8").read()
    contract = json.loads(raw)
    second = raw.split("\n")[1]
    indent = len(second) - len(second.lstrip(" "))
    added, refreshed = [], []
    for t in asyncio.run(server.mcp.list_tools()):
        # upstream entries are hand-written; the millimetre tools' entries are generated — refresh them every run
        if t.name in contract and not contract[t.name].get("description", "").startswith(GENERATED):
            continue
        props = (t.input_schema or {}).get("properties", {})
        req = set((t.input_schema or {}).get("required", []))
        params = {}
        for k, v in props.items():
            typ = v.get("type") or "/".join(x.get("type", "?") for x in v.get("anyOf", [])) or "any"
            if "enum" in v:
                typ += ": " + " | ".join(repr(e) for e in v["enum"])
            default = "required" if k in req else "optional, default " + json.dumps(v.get("default"), ensure_ascii=False)
            params[k] = f"{typ} ({default})"
        (refreshed if t.name in contract else added).append(t.name)
        contract[t.name] = {
            "description": GENERATED + " " + " ".join((t.description or "").split()),
            "input": {"operation_id": "string (required, unique idempotency key)",
                      "state_version": "integer (required, must match current execution state)",
                      "params": params},
            "output": "-> execution-response.json; payload in result_geometry (JSON object)",
        }
    out = json.dumps(contract, ensure_ascii=raw.count("\\u") > 20, indent=indent)
    open(PATH, "w", encoding="utf-8", newline="\n").write(out + ("\n" if raw.endswith("\n") else ""))
    print(f"added {len(added)}: {added}; refreshed {len(refreshed)} generated entries")


if __name__ == "__main__":
    main()
