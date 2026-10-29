"""pair_check: subassembly-internal pairs, instance grouping, the baseline of intended pairs."""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import pair_check  # noqa: E402


def test_internal_and_group():
    assert pair_check.internal(["Frame-1/Bolt-3", "Frame-1/Plate-1"])
    assert not pair_check.internal(["Frame-1/Bolt-3", "Door-1/Plate-1"])
    assert not pair_check.internal(["Bolt-3", "Plate-1"])
    rows = pair_check.group([{"components": ["Frame-1/Bolt-1", "Plate-1"], "volume_mm3": 12.0},
                             {"components": ["Frame-1/Bolt-2", "Plate-1"], "volume_mm3": 12.04},
                             {"components": ["Frame-1/Bolt-3", "Plate-1"], "volume_mm3": 40.0}], "components", "volume_mm3")
    assert len(rows) == 2 and rows[0]["count"] == 2 and rows[0]["also"] == [["Frame-1/Bolt-2", "Plate-1"]]


def test_baseline_shows_only_new_or_changed(tmp_path, monkeypatch):
    monkeypatch.setattr(pair_check, "BASELINES", str(tmp_path))
    old = [{"components": ["A-1", "B-1"], "volume_mm3": 10.0}, {"components": ["A-1", "C-1"], "volume_mm3": 50.0}]
    assert pair_check.new_only(r"D:\x.SLDASM", old, "interference")[1].startswith("no baseline")
    pair_check.save_baseline(r"D:\x.SLDASM", old, "interference")
    now = old[:1] + [{"components": ["A-1", "C-1"], "volume_mm3": 90.0}, {"components": ["D-1", "B-1"], "volume_mm3": 1.5}]
    fresh, hidden = pair_check.new_only(r"D:\x.SLDASM", now, "interference")
    assert hidden == 1 and [x["components"] for x in fresh] == [["A-1", "C-1"], ["D-1", "B-1"]] and fresh[0]["was"] == 50.0
