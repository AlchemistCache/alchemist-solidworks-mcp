"""Tests never touch the developer's environment: the adapter and usage logs go to a temp folder (adapter.log next to
the code is what users read when something goes wrong; usage.jsonl decides their tool groups), every tool group is on,
and a local .env does not change timeouts or modes under test."""
import os
import sys
import tempfile

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

# before config.py is imported: load_dotenv must not override what the tests expect
for _k in [k for k in os.environ if k.startswith("SWMCP_") or k in ("CONNECT_TIMEOUT", "HTTP_TIMEOUT",
                                                                          "LONG_HTTP_TIMEOUT", "ENSURE_TIMEOUT")]:
    del os.environ[_k]
import dotenv  # noqa: E402
dotenv.load_dotenv = lambda *a, **k: False
_TMP = tempfile.mkdtemp(prefix="swmcp_test_")
os.environ["SWMCP_TOOLS"] = "all"                                  # tests see every tool group
os.environ["SWMCP_USAGE_LOG"] = os.path.join(_TMP, "usage.jsonl")  # never the user's usage log

import adapter_log  # noqa: E402
adapter_log.LOG_PATH = os.path.join(_TMP, "adapter.log")
