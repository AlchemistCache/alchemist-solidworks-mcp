import os
from dotenv import load_dotenv

# Load adapters/claude/.env by EXPLICIT path. A bare load_dotenv() searches the current working
# directory, but the MCP host launches server.py from an arbitrary cwd, so the adapter's own .env
# was silently missed. Anchor it to this file's directory.
load_dotenv(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"))

# Keep the host spelled `localhost`: the C# server registers the HTTP.sys prefix
# `http://localhost:5000/`, which matches on the HOST HEADER, so a request addressed to
# 127.0.0.1 is answered with HTTP 400 by HTTP.sys before it ever reaches our code (measured
# 2026-09-28). Switching to the literal IP would shave the dual-stack connect tax of
# (≈2 s per call on this machine), but only together with a C# prefix change that needs a urlacl.
EXECUTION_BASE_URL = os.getenv("EXECUTION_BASE_URL", "http://localhost:5000")
EXECUTE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/execute"
STATE_ENDPOINT = f"{EXECUTION_BASE_URL}/api/tool/state"
HEALTH_ENDPOINT = f"{EXECUTION_BASE_URL}/health"
ENSURE_ENDPOINT = f"{EXECUTION_BASE_URL}/ensure_ready"
CLOSE_DIALOGS_ENDPOINT = f"{EXECUTION_BASE_URL}/close_dialogs"
HTTP_TIMEOUT = float(os.getenv("HTTP_TIMEOUT", "30"))
# ensure_ready may cold-launch SolidWorks, which can take tens of seconds — give it room.
ENSURE_TIMEOUT = float(os.getenv("ENSURE_TIMEOUT", "120"))
# The CONNECT phase gets its own budget, deliberately NOT derived from the timeouts above.
# httpx applies a connect timeout per resolved address, and whichever wins the race decides the
# EXCEPTION TYPE: beat the OS refusal and you get ConnectError, lose and you get ConnectTimeout.
# `_request_with_autostart` catches only the former, so on a host where a refused local connect is
# slow (measured 2.04 s on this machine) a small HTTP_TIMEOUT would silently
# disable the adapter's auto-start. A fixed floor makes the exception type independent of config.
CONNECT_TIMEOUT = float(os.getenv("CONNECT_TIMEOUT", "10"))

# The repository around this adapter (adapters/claude → repo root): the one place its layout is spelled out.
REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
CAD_PLANNER_DIR = os.path.join(REPO_ROOT, "cad-planner")          # the drawing reader (DXF/DWG)

# Auto-start of the execution server (so the user never has to launch the exe by hand): the standard Debug build
# output by default. Override via .env if needed.
EXECUTION_EXE_PATH = os.getenv("EXECUTION_EXE_PATH", os.path.join(
    REPO_ROOT, "execution", "solidworks", "SolidworksExecution", "bin", "Debug", "SolidworksExecution.exe"))
# How long to wait for a freshly-spawned server to answer /health before giving up.
SERVER_SPAWN_TIMEOUT = float(os.getenv("SERVER_SPAWN_TIMEOUT", "20"))

# ---- adapter behaviour (every SWMCP_* setting is read here, nowhere else) ----
# a tool call not finished after SYNC_WAIT seconds answers {"running": true, "job_id": …} (the MCP client cuts
# calls at ~60 s)
SYNC_WAIT = float(os.getenv("SWMCP_SYNC_WAIT", "45"))
# one tool call over HTTP — a big assembly opens / exports for minutes
LONG_HTTP_TIMEOUT = float(os.getenv("LONG_HTTP_TIMEOUT", "600"))
# tool descriptions: compact (long ones cut at DOC_LIMIT characters, full text via tool_help) | full
DOCS_MODE = os.getenv("SWMCP_DOCS", "compact").lower()
DOC_LIMIT = int(os.getenv("SWMCP_DOC_LIMIT", "500"))
# developer-only options (sim_report engine=solidworks, which automates MS Word)
DEV_TOOLS = os.getenv("SWMCP_DEV_TOOLS") == "1"
# CalculiX solver for sim_crosscheck (else PATH / PrePoMax / bConverged / FreeCAD folders are searched)
CCX_PATH = os.getenv("SWMCP_CCX", "")
# tool groups the model sees (tool_groups.py): auto | all | a comma list (drawings, weldments, assemblies, …)
TOOL_GROUPS = os.getenv("SWMCP_TOOLS", "auto")
# auto keeps a group on while it was used within this many days
USAGE_DAYS = int(os.getenv("SWMCP_USAGE_DAYS", "30"))
# usage log: one line per tool call (no arguments); "off" = none
USAGE_LOG = "" if os.getenv("SWMCP_USAGE_LOG", "").lower() == "off" else (
    os.getenv("SWMCP_USAGE_LOG") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "usage.jsonl"))
