"""drawing_summary: the advisories block of analyze_drawing, pinned on synthetic reader
output (no drawing reader, no SolidWorks)."""
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import drawing_summary as ds  # noqa: E402


def test_advisories_name_what_fired():
    assert ds.advisories({}) == ""
    art = {"views": [{"vid": "v0", "scale_factor": 0.5, "loops": [{"id": "L1", "tier": "B"}, {"id": "L2"}]},
                     {"vid": "v1"}],
           "sheet": {"not_read": {"INSERT": {"X": 1}, "HATCH": 2}},
           "dimensions": [{"kind": "angular", "printed_mismatch": True}, {"kind": "linear"},
                          {"kind": "radius", "printed_mismatch": True}],
           "bend_notes": [{"unpaired": "no line"}, {"bend_line": {"in_loop": True}}, {"bend_line": {}}],
           "view_graph": {"solved": False, "reason": "one view"}}
    lines = ds.advisories(art).split("\n")
    assert lines[0] == "" and lines[1] == "ADVISORIES (8) — only what actually fired in THIS read:"
    assert [line.split(" — ")[0] for line in lines[2:]] == [    # one line per condition, with where it fired
        "  view scale_factor @ v0", "  not_read @ sheet (HATCH, INSERT)", "  printed_mismatch/ANGULAR @ dim[0]",
        "  printed_mismatch @ dim[2]", "  tier:B @ v0/L1", "  unpaired bend x1", "  bend_line in_loop x1",
        "  view_graph.solved:false (one view)"]
