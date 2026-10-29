"""Which MCP tools are really used — from the adapter's usage log (adapters/claude/usage.jsonl).

    .venv\\Scripts\\python.exe tools\\usage_report.py [days]      (default: all of the log)

Per group: calls, tools used / tools in the group, last use; per tool: calls, errors, median ms; then the tools never
called in that period. The log holds no arguments — tool, group, time, duration, ok — and the groups enabled with
enable_tools."""
import os
import statistics
import sys
import time
from collections import defaultdict
from datetime import datetime

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.environ["SWMCP_TOOLS"] = "all"   # to know every tool and its group
sys.path.insert(0, os.path.join(REPO, "adapters", "claude"))
import server  # noqa: E402,F401  (registers the tools and fills tool_groups.GROUP_OF)
import tool_groups  # noqa: E402


def main():
    days = float(sys.argv[1]) if len(sys.argv) > 1 else None
    since = time.time() - days * 86400 if days else 0
    rows = [r for r in tool_groups.read_usage(tool_groups.USAGE_PATH) if r.get("t", 0) >= since]
    calls = [r for r in rows if r.get("tool")]
    if not calls:
        print(f"no tool calls in {tool_groups.USAGE_PATH}" + (f" for the last {days:g} days" if days else ""))
        return
    per_tool = defaultdict(list)
    for r in calls:
        per_tool[r["tool"]].append(r)
    in_group = defaultdict(set)
    for name, g in tool_groups.GROUP_OF.items():
        in_group[g].add(name)
    stamp = lambda t: datetime.fromtimestamp(t).strftime("%Y-%m-%d %H:%M")  # noqa: E731
    print(f"{len(calls)} calls, {stamp(calls[0]['t'])} … {stamp(calls[-1]['t'])}\n")
    print(f"{'group':12s} {'calls':>6s} {'used':>9s}  last")
    for g in tool_groups.GROUPS:
        rs = [r for r in calls if r.get("group") == g]
        used = {r["tool"] for r in rs}
        print(f"{g:12s} {len(rs):6d} {len(used):4d}/{len(in_group[g]):<4d}  {stamp(rs[-1]['t']) if rs else '—'}")
    print(f"\n{'tool':28s} {'calls':>6s} {'errors':>6s} {'ms (median)':>11s}")
    for name, rs in sorted(per_tool.items(), key=lambda kv: -len(kv[1])):
        print(f"{name:28s} {len(rs):6d} {sum(not r.get('ok') for r in rs):6d} "
              f"{statistics.median(r.get('ms', 0) for r in rs):11.0f}")
    never = sorted(n for n in tool_groups.GROUP_OF if n not in per_tool)
    print(f"\nnever called ({len(never)}):")
    for g in tool_groups.GROUPS:
        names = [n for n in never if tool_groups.GROUP_OF[n] == g]
        if names:
            print(f"  {g}: {', '.join(names)}")
    enabled = [r for r in rows if r.get("enable")]
    if enabled:
        print("\nturned on mid-conversation: " + ", ".join(f"{r['enable']} ({stamp(r['t'])})" for r in enabled))


if __name__ == "__main__":
    main()
