import config
import os
import subprocess
import threading
import time
import httpx
from datetime import datetime, timedelta, timezone
from config import (
    EXECUTE_ENDPOINT,
    STATE_ENDPOINT,
    HEALTH_ENDPOINT,
    ENSURE_ENDPOINT,
    CLOSE_DIALOGS_ENDPOINT,
    HTTP_TIMEOUT,
    ENSURE_TIMEOUT,
    CONNECT_TIMEOUT,
    EXECUTION_EXE_PATH,
    SERVER_SPAWN_TIMEOUT,
)
from adapter_log import write as _log


class ExecutionLayerError(Exception):
    """Raised when the execution layer returns an unexpected HTTP error."""
    pass


# Serializes auto-start so two concurrent tool calls hitting a down server don't each
# spawn a duplicate exe.
_spawn_lock = threading.Lock()
_health_fail_logged = False

# Set by jobs.py: returns a "BUSY | …" message while a background job still runs in SolidWorks, else None.
BUSY_CHECK = None

# Every tool call may take minutes on a big assembly (open, export, interference, a mesh) — one timeout for all of
# them; health / state requests keep the short HTTP_TIMEOUT. The MCP client itself waits ~60 s: anything slower
# answers {"running": true, "job_id": …} from the adapter (jobs.background) long before this.
LONG_HTTP_TIMEOUT = config.LONG_HTTP_TIMEOUT


def _http_client(**kwargs) -> httpx.Client:
    """Talk to the local execution server: no system proxy, and a CONNECT phase of its own.

    1. `trust_env=False` — httpx trust_env=True reads HKCU Internet Settings and will send
       http://localhost:5000 through a LAN proxy (Clash/V2Ray), which hangs or returns
       HTTP 502. Windows ProxyOverride (localhost / <local>) is not honoured by httpx, so
       we never inherit env/registry proxies here.

    2. The connect phase gets CONNECT_TIMEOUT, not the caller's number. Which exception a
       down server raises is a RACE between that budget and the OS's refusal: win it and
       httpx raises ConnectError, lose it and ConnectTimeout. `_request_with_autostart`
       catches only ConnectError, so with a small caller timeout the auto-start would
       silently never fire. Splitting the phases makes the exception type a property of the
       code instead of a property of someone's .env.
    """
    kwargs.setdefault("trust_env", False)
    timeout = kwargs.get("timeout")
    if isinstance(timeout, (int, float)):
        # A caller passing its own httpx.Timeout has said exactly what it wants — leave it.
        kwargs["timeout"] = httpx.Timeout(timeout, connect=CONNECT_TIMEOUT)
    return httpx.Client(**kwargs)


def _log_health_failure(detail: str) -> None:
    """Log the first failure of an outage, then stay quiet until /health answers again.

    _ensure_server_up polls the probe twice a second, so an unconditional log would bury
    adapter.log under one line per poll.
    """
    global _health_fail_logged
    if not _health_fail_logged:
        _log(f"!! /health probe failed: {detail}")
        _health_fail_logged = True


def _server_state() -> str:
    """Cheap liveness probe: "up" if /health answers 200; "stuck" if the server took the
    connection but did not answer in time — a server whose /health queues behind a call that
    waits on SolidWorks (servers before 2026-10-28; newer ones answer `busy` at once). That one
    is NOT down: a second exe only collides on the port (every 30 min for 8 h that night).
    "down" otherwise.

    Both failure shapes are logged (once per outage), because they are the two halves of
    the same bug: a system proxy that hijacks the localhost route either hangs — which
    surfaces as an exception — or answers 502 itself, which raises nothing at all.
    """
    global _health_fail_logged
    try:
        with _http_client(timeout=2.0) as client:
            status = client.get(HEALTH_ENDPOINT).status_code
    except httpx.ReadTimeout as ex:
        _log_health_failure(f"{type(ex).__name__}: {ex} (connected, no answer)")
        return "stuck"
    except Exception as ex:
        _log_health_failure(f"{type(ex).__name__}: {ex}")
        return "down"
    if status != 200:
        _log_health_failure(f"HTTP {status} from {HEALTH_ENDPOINT}")
        return "down"
    _health_fail_logged = False
    return "up"


def _server_is_up() -> bool:
    """True if /health answers 200 (see _server_state)."""
    return _server_state() == "up"


def _ensure_server_up() -> None:
    """Start the execution-server exe if it isn't already answering /health.

    Guarded against duplicate spawns: re-checks /health inside the lock, spawns at most
    one headless/detached process, then polls /health until it's up. The server is meant
    to outlive this call (it persists while SolidWorks is open), so it is launched detached.
    A server that is running but stuck is not replaced (see _server_state).
    Raises ExecutionLayerError if it can't be brought up (missing exe, or never came up).
    """
    with _spawn_lock:
        state = _server_state()
        if state == "up":
            return
        if state == "stuck":
            _log("!! execution server is running but does not answer /health — no second server started")
            raise ExecutionLayerError(
                "solidworks-execution is running but does not answer: a call in it waits on SolidWorks (a dialog "
                "nobody answers in the hidden instance?). Close that dialog, or end SolidworksExecution.exe — "
                "a second server is not started (it would only collide on the port).")
        if not os.path.isfile(EXECUTION_EXE_PATH):
            _log(f"!! cannot auto-start server — exe not found at {EXECUTION_EXE_PATH}")
            raise ExecutionLayerError(
                "solidworks-execution is not running and its exe was not found at "
                f"{EXECUTION_EXE_PATH}. Build the server or set EXECUTION_EXE_PATH."
            )
        _log(f"-> auto-starting execution server: {EXECUTION_EXE_PATH}")
        # DETACHED_PROCESS | CREATE_NO_WINDOW: survive the adapter, no console window flash.
        creationflags = 0x00000008 | 0x08000000
        try:
            subprocess.Popen(
                [EXECUTION_EXE_PATH],
                cwd=os.path.dirname(EXECUTION_EXE_PATH),
                creationflags=creationflags,
                close_fds=True,
            )
        except Exception as ex:
            _log(f"!! server spawn failed: {ex}")
            raise ExecutionLayerError(f"Failed to start solidworks-execution: {ex}")

        deadline = time.monotonic() + SERVER_SPAWN_TIMEOUT
        while time.monotonic() < deadline:
            if _server_is_up():
                _log("<- execution server is up")
                return
            time.sleep(0.5)
        _log("!! execution server did not answer /health within timeout")
        raise ExecutionLayerError(
            f"Started solidworks-execution but it did not answer {HEALTH_ENDPOINT} "
            f"within {SERVER_SPAWN_TIMEOUT}s."
        )


def _request_with_autostart(do_request, label: str):
    """Run an httpx request; on ConnectError, auto-start the server once and retry.

    Makes every tool call self-heal the "server is down" case transparently. Timeouts are
    NOT caught here (they propagate to each caller's own timeout handling).
    """
    try:
        return do_request()
    except httpx.ConnectError:
        _log(f"<- {label} CONNECT_ERROR — attempting server auto-start")
        _ensure_server_up()  # raises ExecutionLayerError if it can't bring the server up
        try:
            return do_request()
        except httpx.ConnectError:
            _log(f"<- {label} CONNECT_ERROR after auto-start")
            raise ExecutionLayerError(
                "Cannot connect to solidworks-execution even after auto-start. "
                f"Is {HEALTH_ENDPOINT} reachable?"
            )


def get_health() -> dict:
    """GET /health — server status + COM attach state (does not touch state_version)."""
    try:
        with _http_client(timeout=HTTP_TIMEOUT) as client:
            response = client.get(HEALTH_ENDPOINT)
    except httpx.ConnectError:
        _log("<- health CONNECT_ERROR (server down?)")
        raise ExecutionLayerError(
            f"Cannot connect to solidworks-execution. Is the server running on {HEALTH_ENDPOINT}?"
        )
    except httpx.TimeoutException:
        _log("<- health TIMEOUT")
        raise ExecutionLayerError(f"Health request timed out after {HTTP_TIMEOUT}s.")
    if response.status_code != 200:
        raise ExecutionLayerError(f"Unexpected HTTP {response.status_code} from /health: {response.text}")
    body = _json_body(response, "/health")
    _log(f"<- health status={body.get('status')} comAttached={body.get('comAttached')} sv={body.get('stateVersion')}")
    return body


def _health_now() -> dict:
    """/health as it is right now ({} when it does not answer) — a server busy with a call answers at once with busy,
    busySeconds and the dialogs its own SolidWorks shows."""
    try:
        with _http_client(timeout=3.0) as client:
            body = client.get(HEALTH_ENDPOINT).json()
    except Exception:
        return {}
    return body if isinstance(body, dict) else {}


def busy_note(dialogs_only: bool = False) -> str:
    """What the server is busy with, as a sentence for an error or a running answer: the tool, for how long, and the
    dialogs SolidWorks shows — nobody can answer them in the hidden instance, SolidWorks waits on them for good. ""
    when the server is idle without a dialog or does not answer (and, dialogs_only, when SolidWorks shows none)."""
    body = _health_now()
    dialogs = body.get("dialogs") or []
    busy = body.get("busy")
    if not dialogs and (dialogs_only or not busy):
        return ""
    note = f"SolidWorks is still in {busy} ({body.get('busySeconds') or 0:.0f} s)" if busy else "SolidWorks"
    if not dialogs:
        return note + "."
    return (note + (" and shows" if busy else " shows") + f" «{'» «'.join(dialogs)}» — nobody can answer that in its "
            "hidden instance: ensure_ready(close_dialogs=True) closes it (Cancel / No first).")


def dismiss_dialogs() -> list:
    """POST /close_dialogs — the server closes the dialogs its own hidden SolidWorks shows (Cancel / No first); off its
    STA thread, so it works while a call waits on one. Returns what each said."""
    try:
        with _http_client(timeout=HTTP_TIMEOUT) as client:
            response = client.post(CLOSE_DIALOGS_ENDPOINT)
    except httpx.HTTPError as ex:
        raise ExecutionLayerError(f"close_dialogs: {type(ex).__name__}: {ex}")
    if response.status_code == 404:
        raise ExecutionLayerError("this execution server has no /close_dialogs — rebuild and restart it")
    if response.status_code != 200:
        raise ExecutionLayerError(f"Unexpected HTTP {response.status_code} from /close_dialogs: {response.text}")
    closed = list(_json_body(response, "/close_dialogs").get("closed") or [])
    _log(f"<- close_dialogs closed={closed}")
    return closed


def get_state() -> int:
    """
    GET /api/tool/state — fetch the current authoritative state_version.

    Used to resync after a desync (e.g. execution server restarted on rebuild).
    Read-only on the server: no state_version check, no increment.
    """
    _log("-> get_state (resync)")

    def _do():
        with _http_client(timeout=HTTP_TIMEOUT) as client:
            return client.get(STATE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "get_state")
    except httpx.TimeoutException:
        _log("<- get_state TIMEOUT")
        raise ExecutionLayerError(
            f"Resync request to solidworks-execution timed out after {HTTP_TIMEOUT}s."
        )

    if response.status_code != 200:
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /state: {response.text}"
        )

    sv = int(_json_body(response, "/state").get("stateVersion", 0))
    _log(f"<- get_state sv={sv}")
    return sv


def _json_body(response, where: str) -> dict:
    """The server's JSON answer — anything else (HTTP.sys's HTML error page, a crash text) is an execution-layer
    error naming the HTTP status and the start of the body, never a JSONDecodeError blamed on the tool's arguments."""
    try:
        body = response.json()
    except ValueError:
        raise ExecutionLayerError(f"HTTP {response.status_code} from {where} is not JSON: {response.text[:200]!r}")
    if not isinstance(body, dict):
        raise ExecutionLayerError(f"HTTP {response.status_code} from {where}: unexpected answer {str(body)[:200]!r}")
    return body


def call_tool(tool_name: str, operation_id: str, state_version: int, params: dict) -> dict:
    """
    POST /api/tool/execute on the solidworks-execution layer.

    Returns the parsed JSON response body on HTTP 200.
    Raises ExecutionLayerError on HTTP 400 or unexpected status codes.
    """
    payload = {
        "operationId": operation_id,
        "tool": tool_name,
        "stateVersion": state_version,
        "params": params,
    }

    busy = BUSY_CHECK() if BUSY_CHECK else None
    if busy:
        _log(f"-> {tool_name} refused: {busy}")
        raise ExecutionLayerError(busy)
    _log(f"-> {tool_name} op={operation_id} sv={state_version}")

    timeout = LONG_HTTP_TIMEOUT
    # the server skips the request if it only reaches the STA thread after we stopped waiting (queued behind a
    # long job) — otherwise it would still change the model after we reported a timeout
    payload["deadlineUtc"] = (datetime.now(timezone.utc) + timedelta(seconds=max(1.0, timeout - 2))).strftime(
        "%Y-%m-%dT%H:%M:%S.%fZ")

    def _do():
        with _http_client(timeout=timeout) as client:
            return client.post(EXECUTE_ENDPOINT, json=payload)

    try:
        response = _request_with_autostart(_do, tool_name)
    except httpx.TimeoutException:
        _log(f"<- {tool_name} TIMEOUT after {timeout}s")
        note = busy_note()
        raise ExecutionLayerError(
            f"Request to solidworks-execution timed out after {timeout}s." + (f" {note}" if note else "")
        )

    if response.status_code == 400:
        body = _json_body(response, tool_name)
        _log(f"<- {tool_name} HTTP_400 {body.get('error', '')}")
        raise ExecutionLayerError(f"Bad request: {body.get('error', response.text)}")

    if response.status_code != 200:
        _log(f"<- {tool_name} HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from execution layer: {response.text}"
        )

    body = _json_body(response, tool_name)
    err = (body.get("error") or {}).get("code")
    _log(f"<- {tool_name} {body.get('status')}{(' ' + err) if err else ''} sv={body.get('stateVersion')}")
    return body


def ensure_ready(close_dialogs: bool = False) -> dict:
    """POST /ensure_ready — bring the whole stack up and report readiness.

    Two layers: (1) make sure the execution server itself is running (auto-spawn it if
    down — a down server can't answer /ensure_ready); (2) the server attaches to SolidWorks,
    launching it via COM if it's closed. Uses a long timeout because a cold SolidWorks launch
    can take tens of seconds. Returns the parsed readiness dict; does NOT open any document.

    While the server's own SolidWorks shows a dialog this answers BUSY naming it (SolidWorks
    waits on it, /ensure_ready would too); close_dialogs=True first closes those dialogs, a few
    rounds — the call one held goes on and may ask again (a cancelled import asked for a template
    twice, 2026-10-28) — dialogsClosed in the answer.
    """
    _log("-> ensure_ready")
    # A down server can't answer /ensure_ready, so spawn it up front (idempotent / guarded).
    if not _server_is_up():
        _ensure_server_up()
    closed = []
    if close_dialogs:
        for _ in range(5):
            closed += dismiss_dialogs()
            time.sleep(1.5)
            if not _health_now().get("dialogs"):
                break
    if _health_now().get("dialogs"):
        note = busy_note()
        if note:
            _log(f"<- ensure_ready BUSY {note}")
            raise ExecutionLayerError("BUSY | " + note + (f" Closed already: «{'» «'.join(closed)}»." if closed else ""))

    def _do():
        with _http_client(timeout=ENSURE_TIMEOUT) as client:
            return client.post(ENSURE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "ensure_ready")
    except httpx.TimeoutException:
        _log("<- ensure_ready TIMEOUT")
        raise ExecutionLayerError(
            f"ensure_ready timed out after {ENSURE_TIMEOUT}s (a SolidWorks cold launch can be slow)."
        )

    if response.status_code != 200:
        _log(f"<- ensure_ready HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /ensure_ready: {response.text}"
        )

    body = _json_body(response, "/ensure_ready")
    _log(
        f"<- ensure_ready comAttached={body.get('comAttached')} "
        f"swLaunched={body.get('swLaunched')} doc={body.get('activeDocument')}"
    )
    if closed:
        body["dialogsClosed"] = closed
    return body
