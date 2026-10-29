"""Call plumbing shared by the tool modules (tools_*.py) — its public API:
    call(tool, params)                      a tool whose server side is SI (units.py converts) → the answer, with the
                                            transport-error check that catches a call which changed the model anyway
    call_raw(tool, params)                  → the raw ExecutionResponse dict (session.call), SI
    call_mm(tool, params)                   a millimetre tool → the answer, in the background after SYNC_WAIT
    call_mm_json(tool, params)              → the server's payload as a dict, for tools that post-process it in Python
    dump(obj)                               compact JSON text; with_image(res) adds the picture a result points at
Every answer is ONE JSON object (response_mapper.answer_of): state_version, document, the tool's fields, created /
removed. Also: scratch folders, small helpers of the tool modules and the helper modules loaded once at start.
Background jobs and execute_batch's $refs: jobs.py. Units everywhere: MILLIMETRES, DEGREES, KILOGRAMS, N, MPa."""
import json
import os
import re
import shutil
import tempfile
import time

from mcp.server.mcpserver import Image
from execution_client import ExecutionLayerError
from response_mapper import (CadOperationError, answer, dump, execution_layer_error, fail, failure_of,  # noqa: F401 (CadOperationError: re-exported)
                             map_response)
import session
import units
from jobs import background
import calculix  # noqa: F401  (helper modules: loaded once here, at start — numpy/ezdxf must not
import hand_calcs  # noqa: F401  #  be imported first inside a pool thread; re-exported to tools_*)
import sheet_dxf  # noqa: F401
import sim_report as sim_report_html  # noqa: F401  (`sim_report` is also a tool name)
try:                      # XLSX writers need openpyxl (requirements.txt); older installs: a readable error instead
    import gost_spec
    import sheet_reports
except ImportError as _ex:
    gost_spec = sheet_reports = None
    _XLSX_MISSING = f"{_ex} — re-run install.ps1 (pip install -r adapters/claude/requirements.txt)"


def call_mm(tool: str, params: dict) -> str:
    return background(tool, lambda: _call_mm_sync(tool, params))


def with_image(res: str):
    """A finished result that carries an image path is returned with the image itself."""
    try:
        info = json.loads(res[res.index("{"):])
        if isinstance(info, dict) and info.get("image"):
            return [res, Image(path=info["image"])]
    except (ValueError, OSError):
        pass
    return res


def _call_checked(tool: str, params: dict) -> dict:
    """A COMPLETED answer, or the tool's error. (session.call sends a fresh operation id every time, so a DUPLICATE
    cannot come back here.)"""
    try:
        r = session.call(tool, params)
    except ExecutionLayerError as ex:   # timeout / server down / busy: say so instead of "Error executing tool"
        raise execution_layer_error(ex) from ex
    if r.get("status") != "COMPLETED":
        raise failure_of(r)
    return r


def _call_mm_sync(tool: str, params: dict) -> str:
    return answer(_call_checked(tool, params))


def call_mm_json(tool: str, params: dict) -> dict:
    """C# payload as a dict, for tools that post-process it in Python (XLSX, DXF layers, DFM)."""
    return _call_checked(tool, params).get("result_geometry") or {}


SCRATCH = os.path.join(tempfile.gettempdir(), "SolidWorksMCP")   # img (C# view/sim images), dwg2dxf, dfm, ccx


def scratch_dir(kind: str, unique: bool = False) -> str:
    """%TEMP%\\SolidWorksMCP\\<kind> (a fresh sub-folder when unique) — every scratch file of the adapter lives here and
    cleanup_temp() removes what is older than two days."""
    base = os.path.join(SCRATCH, kind)
    os.makedirs(base, exist_ok=True)
    return tempfile.mkdtemp(dir=base) if unique else base


def cleanup_temp(max_age_h: float = 48.0):
    """Remove scratch older than max_age_h (images, DWG→DXF conversions, DFM / CalculiX work folders) — at server
    start, not on import (tests import the adapter)."""
    now = time.time()
    old = lambda p: now - os.path.getmtime(p) > max_age_h * 3600   # noqa: E731
    for kind in os.listdir(SCRATCH) if os.path.isdir(SCRATCH) else []:
        base = os.path.join(SCRATCH, kind)
        for name in os.listdir(base) if os.path.isdir(base) else []:
            path = os.path.join(base, name)
            try:
                if old(path):
                    shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)
            except OSError:
                pass
    tmp = tempfile.gettempdir()   # work folders of older versions: %TEMP%\solidpilot_ccx_* / solidpilot_dfm_*
    for name in os.listdir(tmp):
        path = os.path.join(tmp, name)
        if re.match(r"^solidpilot_(ccx|dfm)_", name) and os.path.isdir(path) and old(path):
            shutil.rmtree(path, ignore_errors=True)


def need_xlsx():
    if sheet_reports is None:
        raise fail("MISSING_DEPENDENCY", _XLSX_MISSING)


def doc_append(text: str):
    """Append shared text to a tool docstring — a string expression like (docstring + X) is not a docstring."""
    def deco(fn):
        fn.__doc__ = (fn.__doc__ or "").rstrip() + " " + text
        return fn
    return deco


def calc(fn, *args, **kw) -> str:
    """Pure-Python check: bad input becomes a readable tool error instead of 'Error executing tool'."""
    try:
        return dump(fn(*args, **kw))
    except (ValueError, ZeroDivisionError) as ex:
        raise fail("BAD_PARAMETER", str(ex)) from ex


def flat_note(f: dict) -> str:
    parts = [f.get("designation") or f.get("part") or "", f.get("name") or ""]
    if f.get("thickness_mm"):
        parts.append(f"t={f['thickness_mm']:g} мм")
    parts.append(f"{f.get('qty', 1)} шт")
    if f.get("material"):
        parts.append(f["material"])
    return " | ".join(x for x in parts if x)




# ---------------------------------------------------------------- SI tools (units.py), tree check on a transport error
# The raw ExecutionResponse of one call (the state_version and its resync live in session.py).
call_raw = session.call


def call(tool_name: str, params: dict) -> str:
    """Send a tool call to the execution layer — the model's millimetres in, SI to the server, millimetres back
    (units.py).

    session.call resyncs a stale state_version (the server was restarted after a rebuild) and retries once. The
    server reads the tree before and after every mutating call (created / removed in the answer), also when the call
    FAILED after it had already changed the model — that is answered as done, with the error as a warning, instead
    of inviting a duplicate. Only a transport error / timeout needs the adapter's own snapshot: the operation may
    still have run.
    """
    before = _model_snapshot() if tool_name in _MUTATING else None
    try:
        response = units.response_to_mm(tool_name, session.call(tool_name, units.params_to_si(tool_name, params)))
    except Exception as exc:
        diff = _model_diff(before, _model_snapshot()) if before is not None else None
        if diff:
            return dump({**diff, "warning": f"{type(exc).__name__}: {exc} — BUT the model changed: the operation did "
                                            "take effect. Do NOT repeat the call (it would create a duplicate '-2')."})
        if isinstance(exc, ExecutionLayerError):  # say WHAT failed, not "Error executing tool"
            raise execution_layer_error(exc) from exc
        raise
    if response.get("status") == "FAILED" and (response.get("created") or response.get("removed")):
        err = response.get("error") or {}
        return answer(response, warning=f"the tool reported {err.get('code')} | {err.get('message')} — BUT the model "
                                        "changed (created / removed): the operation did take effect. Do NOT repeat "
                                        "the call (it would create a duplicate '-2').")
    return map_response(response)


# --- a transport error on these may still have changed the model: a tree snapshot before, compared on error ---
_MUTATING = {"insert_component", "edit_feature", "extrude_feature", "add_mate", "create_pattern",
             "sheet_metal_feature", "add_edge_feature", "create_rib", "add_reference_geometry",
             "save_body_as_part", "create_sketch", "open_new_part", "open_new_assembly", "hole_wizard"}


def _model_snapshot():
    """The active document's tree names (features, '<name> (mate)', '<name> (component)') — the execution server's
    internal model_snapshot route (not an MCP tool), the same read the server diffs for created / removed. Any
    error → None (no check)."""
    try:
        r = call_raw("model_snapshot", {})
        if r.get("status") != "COMPLETED":
            return None
        g = r.get("result_geometry") or {}
        return {"doc": g.get("doc"), "items": g.get("items") or []}
    except Exception:
        return None


def _model_diff(a, b):
    """created / removed between two snapshots (or the document switch); None when nothing changed."""
    if not a or not b:
        return None
    if a["doc"] != b["doc"]:
        return {"document_changed": f"{a['doc']} → {b['doc']}"}
    before, after = set(a["items"]), set(b["items"])
    diff = {"created": [x for x in b["items"] if x not in before][:30],
            "removed": [x for x in a["items"] if x not in after][:30]}
    return {k: v for k, v in diff.items() if v} or None
