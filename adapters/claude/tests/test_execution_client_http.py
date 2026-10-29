"""Regression gates for the local-HTTP path of execution_client (issue #10 / PR #11).

Two silent failure modes, both of which only showed up on a live machine:

1. httpx `trust_env=True` routes http://localhost:5000 through the Windows system proxy
   (HKCU Internet Settings — Clash/V2Ray and friends), because urllib's
   getproxies_registry() never reports ProxyOverride and httpx therefore has no bypass
   list to honour. A proxied /health hangs or answers 502, _server_is_up() reads that as
   "server is down", and the adapter spawns a SECOND SolidworksExecution.exe that then
   collides on the HTTP.sys prefix.

2. The probe swallowed every failure silently, which is why (1) was invisible in
   adapter.log. A proxy answering 502 raises NOTHING, so logging only the exception path
   would still hide half of the original bug.

Neither is reproducible on a machine without a system proxy, so both are guarded
structurally instead: the HTTP client is stubbed and never touches the network.

Runnable two ways:
  - standalone:  python tests/test_execution_client_http.py   (exits non-zero on failure)
  - pytest:      pytest tests/test_execution_client_http.py
"""
import os
import sys

import httpx

_HERE = os.path.dirname(os.path.abspath(__file__))
_ADAPTER_DIR = os.path.dirname(_HERE)

# Import execution_client as a module (it lives one dir up from tests/).
sys.path.insert(0, _ADAPTER_DIR)
import execution_client as ec  # noqa: E402


class _FakeResponse:
    def __init__(self, status_code, body=None):
        self.status_code = status_code
        self._body = body if body is not None else {}
        self.text = str(self._body)

    def json(self):
        return self._body


class _FakeClient:
    """Stands in for httpx.Client — answers GET from the stub's script, POST from its `posts` map (url end → body)."""

    def __init__(self, stub):
        self._stub = stub

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def get(self, url):
        outcome = self._stub.next_outcome()
        if isinstance(outcome, Exception):
            raise outcome
        if isinstance(outcome, dict):   # a 200 with that JSON body
            return _FakeResponse(200, outcome)
        return _FakeResponse(outcome)

    def post(self, url, **kwargs):
        self._stub.posted.append(url)
        body = next(b for end, b in self._stub.posts.items() if url.endswith(end))
        return _FakeResponse(200, body)


class _StubHttp:
    """Patch execution_client's HTTP client + logger, and script the /health outcomes.

    Each outcome is an int status code, a dict (200 with that JSON body) or an Exception instance
    to raise; the last one repeats once the script runs dry. Captured log lines land in `.lines`;
    POSTs answer from `posts` and are recorded in `.posted`; a server spawn lands in `.spawned`.
    """

    def __init__(self, *outcomes, posts=None):
        self._queue = list(outcomes)
        self.lines = []
        self.posts = posts or {}
        self.posted = []
        self.spawned = []

    def __enter__(self):
        self._saved = (ec._http_client, ec._log, ec._health_fail_logged, ec.subprocess.Popen, ec.time.sleep)
        ec._http_client = lambda **kwargs: _FakeClient(self)
        ec._log = self.lines.append
        ec._health_fail_logged = False  # every scenario starts from a clean outage state
        ec.subprocess.Popen = lambda *a, **k: self.spawned.append(a)
        ec.time.sleep = lambda s: None
        return self

    def __exit__(self, *exc):
        ec._http_client, ec._log, ec._health_fail_logged, ec.subprocess.Popen, ec.time.sleep = self._saved
        return False

    def next_outcome(self):
        return self._queue.pop(0) if len(self._queue) > 1 else self._queue[0]


def find_trust_env_drift():
    """_http_client must hand out clients that ignore env/registry proxies."""
    errors = []
    with ec._http_client() as client:
        if client.trust_env is not False:
            errors.append(
                f"_http_client() built a client with trust_env={client.trust_env!r} — "
                f"localhost traffic goes through the Windows system proxy again (issue #10)"
            )
    with ec._http_client(timeout=7.0) as client:
        if client.timeout.read != 7.0:
            errors.append("_http_client() dropped an explicit timeout kwarg")
    return errors


def find_connect_floor_drift():
    """The CONNECT phase must not inherit the caller's timeout.

    Which exception a down server raises is a race between the connect budget and the OS's
    refusal — ConnectError if the refusal wins, ConnectTimeout if it does not — and
    `_request_with_autostart` catches only the first. Tying the connect phase to the caller's
    number therefore makes auto-start depend on whatever HTTP_TIMEOUT someone put in .env.
    Measured on this host: a refused local connect takes 2.04 s, so a 2 s caller timeout
    already lands on the wrong side (KNOWN-LIMITATIONS #31).
    """
    errors = []
    if ec.CONNECT_TIMEOUT < 5.0:
        errors.append(
            f"CONNECT_TIMEOUT is {ec.CONNECT_TIMEOUT}s — too tight a floor to outlast a slow "
            f"local refusal (2.04s measured, and that is one host's number, not a law)"
        )
    # The probe's own 2s is the tightest caller in the module: if any caller's number reached
    # the connect phase, this is the one that would break first.
    for caller_timeout in (2.0, 3.0, 30.0, 120.0):
        with ec._http_client(timeout=caller_timeout) as client:
            if client.timeout.connect != ec.CONNECT_TIMEOUT:
                errors.append(
                    f"_http_client(timeout={caller_timeout}) gave the connect phase "
                    f"{client.timeout.connect}s instead of the {ec.CONNECT_TIMEOUT}s floor — "
                    f"auto-start becomes config-dependent again"
                )
            if client.timeout.read != caller_timeout:
                errors.append(
                    f"_http_client(timeout={caller_timeout}) changed the READ timeout to "
                    f"{client.timeout.read}s — only the connect phase may be overridden"
                )
    # An explicit httpx.Timeout is the caller saying exactly what it wants; don't rewrite it.
    explicit = httpx.Timeout(4.0, connect=1.5)
    with ec._http_client(timeout=explicit) as client:
        if client.timeout.connect != 1.5:
            errors.append("_http_client() overrode an explicit httpx.Timeout the caller passed in")
    return errors


def find_bare_client_calls():
    """Every call site must go through _http_client, not httpx.Client directly.

    A request added later as `httpx.Client(...)` inherits trust_env=True and silently
    reintroduces issue #10 — on proxied machines only, which is exactly the kind of
    regression no behavioural test on a clean box can catch.
    """
    errors = []
    allowed = "return httpx.Client(**kwargs)"  # the one inside _http_client itself
    with open(ec.__file__, encoding="utf-8") as fh:
        for n, line in enumerate(fh, 1):
            if "httpx.Client(" in line and line.strip() != allowed:
                errors.append(
                    f"execution_client.py:{n} constructs httpx.Client directly — use "
                    f"_http_client() so trust_env stays off"
                )
    return errors


def find_probe_gaps():
    """_server_is_up must report — and log — both shapes of failure."""
    errors = []

    with _StubHttp(200) as stub:
        if not ec._server_is_up():
            errors.append("a 200 /health must read as 'server is up'")
        if stub.lines:
            errors.append(f"a healthy probe must stay silent, but logged: {stub.lines}")

    with _StubHttp(ConnectionRefusedError("nothing listening")) as stub:
        if ec._server_is_up():
            errors.append("a raising /health must read as 'server is down'")
        if not any("probe failed" in ln for ln in stub.lines):
            errors.append(f"a /health that raised logged nothing: {stub.lines}")

    with _StubHttp(502) as stub:
        if ec._server_is_up():
            errors.append("HTTP 502 from /health must read as 'server is down'")
        if not any("502" in ln for ln in stub.lines):
            errors.append(
                "a 502 /health — the system proxy answering in place of the local server, "
                "the exact symptom in issue #10 — logged nothing, so the non-200 branch is "
                f"silent again. logged: {stub.lines}"
            )
    return errors


def find_log_spam():
    """One line per outage: not one per poll, but not zero on the next outage either."""
    errors = []
    # Five failing polls (as _ensure_server_up would make), a recovery, then a fresh failure.
    with _StubHttp(502, 502, 502, 502, 502, 200, 502) as stub:
        results = [ec._server_is_up() for _ in range(7)]
    if results[5] is not True:
        errors.append("the scripted 200 did not read as 'server is up' — stub script drifted")
    if len(stub.lines) != 2:
        errors.append(
            f"expected exactly 2 log lines (one per outage), got {len(stub.lines)}: "
            f"{stub.lines}. Five failing polls must log once, and the flag must reset on "
            f"recovery so the NEXT outage is still visible."
        )
    return errors


_STUCK_ON_DIALOG = {"status": "UP", "busy": "open_document", "busySeconds": 612,
                    "dialogs": ["Новый документ SOLIDWORKS"]}


def find_second_server_spawns():
    """A server that takes the connection but does not answer /health is busy, not down.

    2026-10-28: an import waited 8 h on a SolidWorks dialog; the old server's /health queued
    behind it, the probe read the silence as "down", and every ensure_ready started a second
    SolidworksExecution.exe that died on the taken port.
    """
    errors = []
    with _StubHttp(httpx.ReadTimeout("timed out")) as stub:
        if ec._server_state() != "stuck":
            errors.append("a /health that connects but does not answer must read as 'stuck'")
        try:
            ec._ensure_server_up()
            errors.append("_ensure_server_up() on a stuck server must raise, not return")
        except ec.ExecutionLayerError as ex:
            if "second server" not in str(ex):
                errors.append(f"the stuck-server error does not say why no server was started: {ex}")
        if stub.spawned:
            errors.append("a stuck server got a second SolidworksExecution.exe spawned next to it")
    with _StubHttp(ConnectionRefusedError("nothing listening")) as stub:
        if ec._server_state() != "down":
            errors.append("a refused /health must still read as 'down' (auto-start must keep working)")
    return errors


def find_dialog_blind_spots():
    """A call stuck on a SolidWorks dialog is named — in a timeout, a running job and ensure_ready."""
    errors = []
    with _StubHttp(_STUCK_ON_DIALOG):
        note = ec.busy_note()
        for part in ("open_document", "612 s", "Новый документ SOLIDWORKS", "close_dialogs=True"):
            if part not in note:
                errors.append(f"busy_note() lacks {part!r}: {note!r}")
        try:
            ec.ensure_ready()
            errors.append("ensure_ready() queued behind a call stuck on a dialog instead of answering BUSY")
        except ec.ExecutionLayerError as ex:
            if not str(ex).startswith("BUSY |") or "Новый документ SOLIDWORKS" not in str(ex):
                errors.append(f"ensure_ready() on a stuck server must answer 'BUSY | …' naming the dialog: {ex}")
    with _StubHttp({"status": "UP", "busy": "analyze_model", "busySeconds": 50, "dialogs": []}):
        if ec.busy_note(dialogs_only=True):
            errors.append("busy_note(dialogs_only=True) spoke up for a long call without a dialog")
        if "analyze_model" not in ec.busy_note():
            errors.append("busy_note() did not name the call the server is busy with")
    with _StubHttp({"status": "UP", "dialogs": ["Microsoft Visual Basic: Run-time error"]}):
        # idle, but the dialog is still open: SolidWorks waits on it, the next call would too
        note = ec.busy_note(dialogs_only=True)
        if "SolidWorks shows «Microsoft Visual Basic: Run-time error»" not in note:
            errors.append(f"busy_note() of an idle server with a dialog open does not name it: {note!r}")
        try:
            ec.ensure_ready()
            errors.append("ensure_ready() went into SolidWorks while it showed a dialog")
        except ec.ExecutionLayerError as ex:
            if not str(ex).startswith("BUSY |"):
                errors.append(f"ensure_ready() with a dialog open must answer 'BUSY | …': {ex}")
    with _StubHttp({"status": "UP", "comAttached": True}):
        if ec.busy_note():
            errors.append("busy_note() on an idle server must be empty")
    with _StubHttp(httpx.ReadTimeout("timed out")):
        if ec.busy_note():
            errors.append("busy_note() must stay empty when /health does not answer")
    return errors


def find_close_dialogs_gaps():
    """ensure_ready(close_dialogs=True) closes the dialogs first and reports them."""
    errors = []
    posts = {"/close_dialogs": {"closed": ["Новый документ SOLIDWORKS [cancel]"], "ownInstance": True},
             "/ensure_ready": {"status": "UP", "comAttached": True, "stateVersion": 7}}
    with _StubHttp({"status": "UP", "comAttached": True}, posts=posts) as stub:
        try:
            body = ec.ensure_ready(close_dialogs=True)
        except ec.ExecutionLayerError as ex:
            return [f"ensure_ready(close_dialogs=True) failed: {ex}"]
        if not stub.posted or not stub.posted[0].endswith("/close_dialogs"):
            errors.append(f"ensure_ready(close_dialogs=True) did not POST /close_dialogs first: {stub.posted}")
        if body.get("dialogsClosed") != ["Новый документ SOLIDWORKS [cancel]"]:
            errors.append(f"the closed dialogs are not in the answer: {body}")
    with _StubHttp({"status": "UP", "comAttached": True}, posts=posts) as stub:
        ec.ensure_ready()
        if any(u.endswith("/close_dialogs") for u in stub.posted):
            errors.append("ensure_ready() without close_dialogs closed dialogs")
    # live 2026-10-28: the import whose template picker was cancelled asked again at once — one call closes both
    idle, asking = {"status": "UP", "comAttached": True}, {"status": "UP", "dialogs": ["Новый документ SOLIDWORKS"]}
    with _StubHttp(idle, asking, idle, posts=posts) as stub:
        try:
            ec.ensure_ready(close_dialogs=True)
        except ec.ExecutionLayerError as ex:
            errors.append(f"a dialog that came back after the first close was left to the caller: {ex}")
        if sum(u.endswith("/close_dialogs") for u in stub.posted) != 2:
            errors.append(f"expected two close rounds for a dialog that came back: {stub.posted}")
    return errors


def find_all():
    return (find_trust_env_drift() + find_connect_floor_drift() + find_bare_client_calls()
            + find_probe_gaps() + find_log_spam() + find_second_server_spawns() + find_dialog_blind_spots()
            + find_close_dialogs_gaps())


def test_stuck_server_gets_no_second_server():
    """pytest entry point — a server that does not answer /health is busy, not down."""
    errors = find_second_server_spawns()
    assert not errors, "second-server regression:\n  - " + "\n  - ".join(errors)


def test_dialog_that_holds_solidworks_is_named_and_closable():
    """pytest entry point — busy_note / ensure_ready name the dialog; close_dialogs closes it."""
    errors = find_dialog_blind_spots() + find_close_dialogs_gaps()
    assert not errors, "stuck-dialog regression:\n  - " + "\n  - ".join(errors)


def test_trust_env_off():
    """pytest entry point — clients never inherit the system proxy."""
    errors = find_trust_env_drift() + find_bare_client_calls()
    assert not errors, "proxy-bypass regression:\n  - " + "\n  - ".join(errors)


def test_connect_phase_has_its_own_floor():
    """pytest entry point — auto-start must not depend on the caller's timeout."""
    errors = find_connect_floor_drift()
    assert not errors, "connect-timeout regression:\n  - " + "\n  - ".join(errors)


def test_health_probe_reports_and_logs_both_failure_shapes():
    """pytest entry point — hang AND 502 are both caught and both logged."""
    errors = find_probe_gaps() + find_log_spam()
    assert not errors, "/health probe regression:\n  - " + "\n  - ".join(errors)


if __name__ == "__main__":
    errs = find_all()
    if errs:
        print("EXECUTION-CLIENT HTTP REGRESSION:")
        for e in errs:
            print("  -", e)
        sys.exit(1)
    print("OK - trust_env off at every call site; connect phase floored at %gs independently of "
          "the caller; /health reports and logs both failure shapes (raise + non-200), once per "
          "outage" % ec.CONNECT_TIMEOUT)
    sys.exit(0)
