"""Minimal file logger for the MCP adapter.

Writes to adapter.log next to this module. A FILE (not stdout) because the MCP
server speaks JSON-RPC over stdio — anything on stdout would corrupt the protocol.
Mirrors the C# execution-side ExecLog so the two logs line up per request.

Must NEVER raise — logging failures are swallowed.
"""
import os
import threading
import traceback
from datetime import datetime

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "adapter.log")
MAX_BYTES = 5 * 1024 * 1024        # then adapter.log → adapter.log.1 (one previous file kept)
_lock = threading.Lock()


def write(message: str) -> None:
    try:
        stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
        with _lock:
            if os.path.exists(LOG_PATH) and os.path.getsize(LOG_PATH) > MAX_BYTES:
                os.replace(LOG_PATH, LOG_PATH + ".1")
            with open(LOG_PATH, "a", encoding="utf-8") as fh:
                fh.write(f"{stamp}  {message}\n")
    except Exception:
        # logging must never break a tool call
        pass


def exception(where: str) -> None:
    """The exception being handled, with its traceback — for handlers that turn a failure into answer text (the
    model reads the short text, the developer the traceback here)."""
    write(f"{where}: {traceback.format_exc().rstrip()}")
