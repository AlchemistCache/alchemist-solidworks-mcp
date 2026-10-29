"""Compact tool descriptions and tool_help (tool_docs.py)."""
import json
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import tool_docs  # noqa: E402


def test_cut_keeps_sentences():
    text = "First sentence. Use e.g. analyze_model for indices. " + "x " * 400
    out = tool_docs._cut(text, 60)
    assert out.endswith("indices.") and len(out) <= 60
    assert tool_docs._cut("short", 60) == "short"


def test_help_topics(monkeypatch):
    # own dicts: the server's (filled at import, used by other tests) stay as they are
    monkeypatch.setattr(tool_docs, "FULL", {"export_flat_dxf": "Flat-pattern DXF straight from the model. Long text.",
                                            "view_image": "SEE the model: PNG."})
    monkeypatch.setattr(tool_docs, "PARAMS", {"export_flat_dxf": {"properties": {"out_dir": {"type": "string",
                                                                                          "default": ""}},
                                                                  "required": []}})
    idx = json.loads(tool_docs.help_text(""))
    assert "sheet_metal" in idx["guides"] and idx["tools"]["view_image"] == "SEE the model: PNG."
    full = json.loads(tool_docs.help_text("export_flat_dxf"))
    assert full["parameters"][0]["name"] == "out_dir"
    assert "sheet_metal_dfm" in tool_docs.help_text("sheet_metal")
    hits = json.loads(tool_docs.help_text("dxf"))
    assert "export_flat_dxf" in hits["tools"] and "sheet_metal" in hits["guides"]
