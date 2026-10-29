"""Register (or update) the 'solidworks' MCP server in Claude Desktop's config, with a timestamped backup.

    .venv\\Scripts\\python.exe tools\\register_claude.py [--name solidworks]

Points Claude at <repo>\\.venv\\Scripts\\python.exe + <repo>\\adapters\\claude\\server.py. Restart Claude afterwards.
"""
import datetime
import glob
import json
import os
import shutil
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def config_path():
    cands = glob.glob(os.path.join(os.environ.get("LOCALAPPDATA", ""),
                                   r"Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json"))
    cands.append(os.path.join(os.environ["APPDATA"], r"Claude\claude_desktop_config.json"))
    for p in cands:
        if os.path.exists(p):
            return p
    return cands[-1]          # create the classic location


def main():
    name = sys.argv[sys.argv.index("--name") + 1] if "--name" in sys.argv else "solidworks"
    path = config_path()
    cfg = {}
    if os.path.exists(path):
        cfg = json.load(open(path, encoding="utf-8"))
        shutil.copy2(path, path + ".bak_" + datetime.datetime.now().strftime("%Y%m%d_%H%M%S"))
    else:
        os.makedirs(os.path.dirname(path), exist_ok=True)
    servers = cfg.setdefault("mcpServers", {})
    servers[name] = {
        "command": os.path.join(REPO, ".venv", "Scripts", "python.exe"),
        "args": [os.path.join(REPO, "adapters", "claude", "server.py")],
        "env": {"PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"},
    }
    json.dump(cfg, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"OK: '{name}' -> {path}")
    print("MCP servers:", ", ".join(servers))
    print("Restart Claude to load the tools.")


if __name__ == "__main__":
    main()
