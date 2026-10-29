"""Diagnose an installation of the SOLIDWORKS MCP server: prints OK / WARN / FAIL per check.

    .venv\\Scripts\\python.exe tools\\doctor.py
    .venv\\Scripts\\python.exe tools\\doctor.py -SwApiDir "D:\\SW2024\\api\\redist"   # as install.ps1 / build.ps1

Where SolidWorks is: -SwApiDir (its api\\redist folder, the same value install.ps1 got), else the environment variable
SWMCP_SW_DIR (the SolidWorks folder itself), else C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS.
"""
import argparse
import glob
import importlib
import importlib.metadata as md
import json
import os
import socket
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SW_DIR = os.environ.get("SWMCP_SW_DIR", r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS")
API_DIR = os.path.join(SW_DIR, "api", "redist")     # install.ps1 / build.ps1 -SwApiDir
EXE = os.path.join(REPO, "execution", "solidworks", "SolidworksExecution", "bin", "Debug", "SolidworksExecution.exe")
# requirements.txt: distribution -> the module the adapter imports (an installed but broken package fails the import)
PACKAGES = {"mcp": "mcp", "httpx": "httpx", "python-dotenv": "dotenv", "ezdxf": "ezdxf", "openpyxl": "openpyxl",
            "numpy": "numpy"}
problems = 0


def report(level, what, detail=""):
    global problems
    if level != "OK":
        problems += 1
    print(f"[{level:4}] {what}" + (f" — {detail}" if detail else ""))


def file_version(path):
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Item '{path}').VersionInfo.FileVersion"],
                             capture_output=True, text=True, timeout=20)
        return out.stdout.strip()
    except Exception:  # noqa: BLE001
        return "?"


def main():
    global SW_DIR, API_DIR
    ap = argparse.ArgumentParser(description="Diagnose an installation of the SOLIDWORKS MCP server.")
    ap.add_argument("-SwApiDir", "--sw-api-dir", dest="sw_api_dir",
                    help=r"SolidWorks API folder <SOLIDWORKS>\api\redist, as passed to install.ps1 / build.ps1")
    args = ap.parse_args()
    if args.sw_api_dir:                       # install.ps1: SolidWorks = two folders above api\redist
        API_DIR = os.path.abspath(args.sw_api_dir)
        SW_DIR = os.path.dirname(os.path.dirname(API_DIR))

    # Python / venv
    v = sys.version_info
    report("OK" if v >= (3, 11) else "FAIL", f"Python {v.major}.{v.minor}", sys.executable)
    if os.path.normcase(sys.prefix) != os.path.normcase(os.path.join(REPO, ".venv")):
        report("WARN", "not running from the project .venv", "run .venv\\Scripts\\python.exe tools\\doctor.py")
    for pkg, module in PACKAGES.items():
        try:
            ver = md.version(pkg)
        except md.PackageNotFoundError:
            report("FAIL", f"package {pkg} missing", "run install.ps1")
            continue
        try:
            importlib.import_module(module)
            report("OK", f"package {pkg} {ver}")
        except Exception as ex:  # noqa: BLE001 — installed but does not import (broken wheel, missing DLL)
            report("FAIL", f"package {pkg} {ver} does not import", f"{type(ex).__name__}: {str(ex)[:120]} — run install.ps1")

    # SolidWorks
    hint = "pass -SwApiDir <SOLIDWORKS>\\api\\redist or set SWMCP_SW_DIR"
    sld = os.path.join(SW_DIR, "SLDWORKS.exe")
    interop = os.path.join(API_DIR, "SolidWorks.Interop.sldworks.dll")
    if os.path.exists(sld):
        report("OK", f"SolidWorks {file_version(sld)}", sld)
    else:
        report("FAIL", "SolidWorks not found", f"{sld} ({hint})")
    if os.path.exists(interop):
        report("OK", f"SolidWorks API {file_version(interop)}", API_DIR)
    else:
        report("FAIL", "SolidWorks API (interop) not found: the exe cannot be built", f"{interop} ({hint})")
    sim = os.path.exists(os.path.join(SW_DIR, "Simulation", "cosworks.dll"))
    report("OK" if sim else "WARN", "SOLIDWORKS Simulation " + ("installed" if sim else "not found (sim_* tools unavailable)"))

    # execution server build
    if not os.path.exists(EXE):
        report("FAIL", "SolidworksExecution.exe not built", "run install.ps1 (or execution\\solidworks\\build.ps1)")
    else:
        exe_t = os.path.getmtime(EXE)
        srcs = glob.glob(os.path.join(REPO, "execution", "solidworks", "SolidworksExecution", "**", "*.cs"), recursive=True)
        newest = max((os.path.getmtime(s) for s in srcs if "\\bin\\" not in s and "\\obj\\" not in s), default=0)
        report("OK" if exe_t >= newest else "WARN", "exe up to date with sources" if exe_t >= newest else
               "exe is OLDER than the C# sources", "" if exe_t >= newest else "rebuild: execution\\solidworks\\build.ps1")
        bin_interop = os.path.join(os.path.dirname(EXE), "SolidWorks.Interop.sldworks.dll")
        if os.path.exists(bin_interop) and os.path.exists(interop):
            a, b = file_version(bin_interop), file_version(interop)
            report("OK" if a == b else "WARN", f"exe built against SW API {a}",
                   "" if a == b else f"installed SW API is {b} — rebuild")

    env_path = os.path.join(REPO, "adapters", "claude", ".env")
    if os.path.exists(env_path):
        txt = open(env_path, encoding="utf-8", errors="replace").read()
        if "EXECUTION_EXE_PATH" in txt and os.path.normcase(EXE) not in os.path.normcase(txt):
            report("WARN", "adapters\\claude\\.env overrides EXECUTION_EXE_PATH", "points away from bin\\Debug — delete the line")

    # mode
    flag = os.path.exists(os.path.join(REPO, "own_instance.flag"))
    report("OK", "mode: " + ("own hidden SolidWorks instance" if flag else "shared (the user's open SolidWorks)"))

    # server
    s = socket.socket()
    s.settimeout(1)
    up = s.connect_ex(("127.0.0.1", 5000)) == 0
    s.close()
    if up:
        try:
            import httpx
            h = httpx.get("http://localhost:5000/health", timeout=10, trust_env=False).json()   # no system proxy (see execution_client)
            report("OK" if h.get("comAttached") else "WARN", "execution server UP",
                   f"comAttached={h.get('comAttached')} sw={h.get('swVersion')} doc={h.get('activeDocument')}")
        except Exception as ex:  # noqa: BLE001
            report("WARN", "port 5000 busy but /health failed", str(ex)[:120])
    else:
        report("OK", "execution server not running (starts automatically on the first tool call)")

    # Claude registration
    cands = glob.glob(os.path.join(os.environ.get("LOCALAPPDATA", ""), r"Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json"))
    cands.append(os.path.join(os.environ.get("APPDATA", ""), r"Claude\claude_desktop_config.json"))
    cfg = next((p for p in cands if os.path.exists(p)), None)
    if not cfg:
        report("WARN", "Claude Desktop config not found", "run tools\\register_claude.py")
    else:
        servers = json.load(open(cfg, encoding="utf-8")).get("mcpServers", {})
        mine = [k for k, v in servers.items() if any("server.py" in a and os.path.normcase(REPO) in os.path.normcase(a)
                                                   for a in v.get("args", []))]
        report("OK" if mine else "WARN", "registered in Claude Desktop as " + (", ".join(mine) if mine else "— not registered"),
               cfg if mine else "run tools\\register_claude.py")

    print(f"\n{'All good.' if not problems else f'{problems} warning(s)/problem(s).'}")


if __name__ == "__main__":
    main()
