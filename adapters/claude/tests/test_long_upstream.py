"""A slow UPSTREAM tool (export of a big assembly) answers {"running": true, "job_id": …} after SYNC_WAIT like the
millimetre tools, and job_result delivers its answer — instead of the MCP client cutting the call at ~60 s."""
import asyncio
import json
import os
import sys
import time

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)


def _text(res):
    return next((c.text for c in res.content if getattr(c, "type", "") == "text"), "")


def test_slow_export_runs_as_a_job(monkeypatch):
    import pytest
    pytest.importorskip("mcp")
    import server
    import jobs
    import session

    def slow(tool, op, sv, params):
        time.sleep(0.4)
        return {"status": "COMPLETED", "stateVersion": sv, "cadState": {"activeDocument": "big.SLDASM"}}

    monkeypatch.setattr(session, "call_tool", slow)
    monkeypatch.setattr(jobs, "SYNC_WAIT", 0.05)
    jobs.jobs.clear()
    first = _text(asyncio.run(server.mcp.call_tool("export_document", {"file_path": r"C:\x\big.step"})))
    assert '"running":true' in first and '"job_id"' in first, first
    job_id = json.loads(first)["job_id"]
    done = json.loads(_text(asyncio.run(server.mcp.call_tool("job_result", {"job_id": job_id, "wait_s": 5}))))
    assert done["document"] == "big.SLDASM" and "job_seconds" in done and "state_version" in done, done
    jobs.jobs.clear()
