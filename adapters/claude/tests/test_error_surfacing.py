"""A failed CAD operation must reach the MODEL with its code and message.

Regression: with MCP SDK v2 any exception that is not a ToolError is shown to the model only as
"Error executing tool <name>", so a failed verify_state (NO_ACTIVE_DOCUMENT) looked like an unexplained
crash. response_mapper.CadOperationError is a ToolError (and still a RuntimeError for old handlers).

Run:  python adapters/claude/tests/test_error_surfacing.py   |   pytest ...   (skipped without the adapter deps)
"""
import asyncio
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

FAILED = {"status": "FAILED", "stateVersion": 1,
          "error": {"code": "NO_ACTIVE_DOCUMENT", "message": "No active document found in SolidWorks."}}


def _server():
    """The real server module — skipped without the MCP SDK; any other import error FAILS the test."""
    pytest.importorskip("mcp")
    import server
    return server


def test_map_response_failure_is_a_toolerror_and_a_runtimeerror():
    import response_mapper as rm
    try:
        rm.map_response(FAILED)
    except rm.CadOperationError as ex:
        assert isinstance(ex, RuntimeError)
        assert "NO_ACTIVE_DOCUMENT" in str(ex) and "No active document" in str(ex)
    else:
        raise AssertionError("FAILED must raise")
    try:
        from mcp.server.mcpserver.exceptions import ToolError
    except Exception:  # noqa: BLE001
        print("     (ToolError check skipped: mcp not installed)")
        return
    assert issubclass(rm.CadOperationError, ToolError)


def test_the_model_sees_the_reason_through_the_real_tool_path():
    server = _server()
    import session
    real = session.call_tool
    session.call_tool = lambda *a, **k: FAILED
    try:
        try:
            asyncio.run(server.mcp.call_tool("verify_state", {}))
        except Exception as ex:  # noqa: BLE001
            msg = str(ex)
        else:
            raise AssertionError("a FAILED response must surface as an error")
    finally:
        session.call_tool = real
    assert "NO_ACTIVE_DOCUMENT" in msg, msg


def test_execution_layer_outage_is_explained_not_masked():
    server = _server()

    def down(*a, **k):
        raise server.ExecutionLayerError("solidworks-execution is not running and its exe was not found")
    import session
    real = session.call_tool
    session.call_tool = down
    try:
        try:
            asyncio.run(server.mcp.call_tool("verify_state", {}))
        except Exception as ex:  # noqa: BLE001
            msg = str(ex)
        else:
            raise AssertionError("an outage must surface as an error")
    finally:
        session.call_tool = real
    assert "EXECUTION_LAYER | " in msg and "exe was not found" in msg, msg


def _error_of(server, tool, args):
    try:
        asyncio.run(server.mcp.call_tool(tool, args))
    except Exception as ex:  # noqa: BLE001
        return str(ex)
    raise AssertionError(f"{tool} must fail")


def test_every_failure_kind_reaches_the_model():
    """Not only FAILED responses: an argument of the wrong type, an outage on the raw call path (compare_parts,
    compare_*, dimension_for_face …) and an unexpected crash all carry a code and the reason."""
    server = _server()
    msg = _error_of(server, "add_sketch_entities", {"segments": "[{oops"})   # a list is required, not JSON text
    assert "segments" in msg and "list" in msg, msg
    assert str(server._as_tool_error("t", server.ExecutionLayerError("timed out after 600 s"))).startswith(
        "EXECUTION_LAYER | timed out")
    crash = server._as_tool_error("t", KeyError("volume_mm3"))
    assert str(crash).startswith("INTERNAL | KeyError") and isinstance(crash, __import__("response_mapper").CadOperationError)

    def down(*a, **k):
        raise server.ExecutionLayerError("solidworks-execution is not running")
    import session
    real = session.call_tool
    session.call_tool = down
    try:
        msg = _error_of(server, "dimension_for_face", {"face_index": 0})
    finally:
        session.call_tool = real
    assert "solidworks-execution is not running" in msg, msg


_TESTS = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]

if __name__ == "__main__":
    failed = 0
    for t in _TESTS:
        try:
            t()
            print("ok   -", t.__name__)
        except pytest.skip.Exception as ex:
            print("skip -", t.__name__, "::", ex)
        except Exception as ex:  # noqa: BLE001
            failed += 1
            print("FAIL -", t.__name__, "::", type(ex).__name__, ex)
    print("\n%d/%d passed" % (len(_TESTS) - failed, len(_TESTS)))
    sys.exit(1 if failed else 0)
