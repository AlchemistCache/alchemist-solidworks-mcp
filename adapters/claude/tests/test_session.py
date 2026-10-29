"""session.call: the ONE call path — tracked state_version, one resync + retry when the server was restarted."""
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import session  # noqa: E402

MISMATCH = {"status": "FAILED", "error": {"code": "INVALID_STATE_VERSION", "message": "stale"}}


def test_mismatch_resyncs_and_retries_once_with_a_new_operation(monkeypatch):
    sent = []

    def call_tool(tool, op, sv, params):
        sent.append((op, sv))
        return MISMATCH if len(sent) == 1 else {"status": "COMPLETED", "stateVersion": sv + 1}

    monkeypatch.setattr(session, "call_tool", call_tool)
    monkeypatch.setattr(session, "get_state", lambda: 40)
    session.set_state_version(7)
    r = session.call("extrude_feature", {})
    assert r["status"] == "COMPLETED" and session.state_version() == 41
    assert [sv for _, sv in sent] == [7, 40] and sent[0][0] != sent[1][0]


def test_unreachable_state_endpoint_keeps_the_last_version(monkeypatch):
    monkeypatch.setattr(session, "call_tool", lambda tool, op, sv, params: {"status": "COMPLETED", "stateVersion": 12})
    session.set_state_version(3)
    assert session.call("verify_state", {})["stateVersion"] == 12 and session.state_version() == 12

    def down():
        raise OSError("connection refused")
    monkeypatch.setattr(session, "get_state", down)
    assert session.resync() == 12          # the last known value stays


def test_relative_paths_are_refused_before_the_call(monkeypatch):
    sent = []
    monkeypatch.setattr(session, "call_tool", lambda *a: sent.append(a) or {"status": "COMPLETED", "stateVersion": 1})
    try:
        session.call("save_document", {"file_path": "part.SLDPRT"})
    except __import__("response_mapper").CadOperationError as ex:
        assert str(ex).startswith("BAD_PARAMETER | file_path must be a full path")
    else:
        raise AssertionError("a relative path must be refused")
    assert not sent
    session.call("batch_export_files", {"files": [r"D:\a.SLDPRT"], "out_dir": r"D:\out", "format": "step"})
    assert len(sent) == 1
