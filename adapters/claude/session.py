"""The adapter's ONE path to the execution server.

Every operation on the execution server (/api/tool/execute) — from the tools (tools_*.py, through bridge.call / call_mm /
call_mm_json / call_raw) — goes through call(): a fresh
operation id, the tracked state_version, and ONE resync + retry when the server answers INVALID_STATE_VERSION (it was
restarted after a rebuild and its counter reset). The version lives here, under a lock: tools run on up to 40 MCP
worker threads plus the background-job pool.

Transport errors (server down, timeout, BUSY) propagate as execution_client.ExecutionLayerError.
"""
import os
import threading
import uuid

from execution_client import call_tool, get_state
from response_mapper import fail

# Parameters that name a file or folder. A RELATIVE path would be resolved against the execution server's working
# directory (not the user's), so the file lands somewhere unexpected — ask for a full path instead.
PATH_PARAMS = {"file_path", "out_dir", "out_path", "model_path", "csv_path", "xlsx_path",
               "template_path", "assembly_path", "artifact_path", "new_file", "old_file", "target_dir", "folder",
               "keep_dxf_dir", "files"}


def given(params: dict) -> dict:
    """ONE rule for every call: None, "", [] and {} mean "not given" and are not sent — the execution server's P*
    helpers treat an absent parameter as its default. (False / 0 are values: a tool that means "default" by them
    passes `x or None`.)"""
    return {k: v for k, v in (params or {}).items() if v is not None and v != "" and v != [] and v != {}}


def full_path(key: str, value: str) -> str:
    """A file/folder parameter as a normalized FULL path; a relative one is refused (it would be resolved against
    some process's working folder, not the user's)."""
    if isinstance(value, str) and value and not os.path.isabs(value):
        raise fail("BAD_PARAMETER", f"{key} must be a full path (e.g. D:\\Work\\part.SLDPRT), got '{value}'")
    return os.path.normpath(value) if value else value


def check_paths(params: dict) -> None:
    for key in PATH_PARAMS.intersection(params or {}):
        for v in params[key] if isinstance(params[key], list) else [params[key]]:
            full_path(key, v)

_lock = threading.Lock()
_state_version = 0


def state_version() -> int:
    with _lock:
        return _state_version


def set_state_version(sv) -> None:
    global _state_version
    if sv is None:
        return
    with _lock:
        _state_version = sv


def resync() -> int:
    """Take the authoritative version from GET /state (after many sub-operations outside call(), or a mismatch)."""
    try:
        set_state_version(get_state())
    except Exception:  # noqa: BLE001 — keep the last known value; the next call() resyncs again if needed
        pass
    return state_version()


def is_state_mismatch(response: dict) -> bool:
    return response.get("status") == "FAILED" and (response.get("error") or {}).get("code") == "INVALID_STATE_VERSION"


def call(tool: str, params: dict) -> dict:
    """One tool call; returns the raw ExecutionResponse dict (COMPLETED / FAILED / DUPLICATE) and records its
    state_version."""
    params = given(params)
    check_paths(params)
    response = call_tool(tool, str(uuid.uuid4()), state_version(), params)
    if is_state_mismatch(response):
        response = call_tool(tool, str(uuid.uuid4()), resync(), params)
    set_state_version(response.get("stateVersion"))
    return response
