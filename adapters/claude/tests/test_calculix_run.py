"""calculix.find_ccx (where the solver is looked for, in which order) and calculix.run_ccx (exit code, .dat, output
tails, a solve that does not finish) — with a fake subprocess and fake install folders, no ccx needed."""
import os
import subprocess
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import calculix  # noqa: E402


def _exe(tmp_path, name):
    p = tmp_path / name
    p.write_text("")
    return str(p)


@pytest.fixture
def nowhere(monkeypatch):
    """No SWMCP_CCX, nothing on PATH, no install folders."""
    monkeypatch.setattr(calculix.config, "CCX_PATH", "")
    monkeypatch.setattr(calculix.shutil, "which", lambda name: None)
    monkeypatch.setattr(calculix.glob, "glob", lambda pattern: [])


def test_find_ccx_order(tmp_path, monkeypatch, nowhere):
    assert calculix.find_ccx() is None
    on_path = _exe(tmp_path, "ccx_path.exe")
    monkeypatch.setattr(calculix.shutil, "which", lambda name: on_path if name == "ccx_static.exe" else None)
    assert calculix.find_ccx() == on_path
    configured = _exe(tmp_path, "ccx_configured.exe")
    monkeypatch.setattr(calculix.config, "CCX_PATH", configured)
    assert calculix.find_ccx() == configured                       # SWMCP_CCX first
    monkeypatch.setattr(calculix.config, "CCX_PATH", str(tmp_path / "gone.exe"))
    assert calculix.find_ccx() == on_path                          # a configured path that does not exist is skipped


def test_find_ccx_in_install_folders(tmp_path, monkeypatch, nowhere):
    tools = [_exe(tmp_path, "b_ccx_static.exe"), _exe(tmp_path, "a_ccx_static.exe")]
    seen = []

    def fake_glob(pattern):
        seen.append(pattern)
        return list(tools) if pattern.startswith(r"C:\Tools\CalculiX") else []
    monkeypatch.setattr(calculix.glob, "glob", fake_glob)
    assert calculix.find_ccx() == tools[1]                         # sorted: the first by name
    assert seen[0].startswith(r"C:\Program Files\PrePoMax") and seen[-1] == r"C:\Tools\CalculiX\*\ccx_static.exe"


def test_run_ccx_timeout_is_a_failed_run(tmp_path, monkeypatch):
    deck = str(tmp_path / "job.inp")
    calls = []

    def slow(cmd, **kw):
        calls.append((cmd, kw))
        raise subprocess.TimeoutExpired(cmd, kw["timeout"])
    monkeypatch.setattr(calculix.subprocess, "run", slow)
    assert calculix.run_ccx("ccx.exe", deck, timeout_s=5) == {
        "exit_code": None, "dat": None, "frd": None, "stdout_tail": "ccx did not finish in 5 s", "stderr_tail": ""}
    cmd, kw = calls[0]
    assert cmd == ["ccx.exe", "-i", "job"] and kw["cwd"] == str(tmp_path) and kw["timeout"] == 5
    assert kw["shell"] is False and kw["check"] is False


def test_run_ccx_result(tmp_path, monkeypatch):
    deck = str(tmp_path / "job.inp")

    def solve(cmd, **kw):
        (tmp_path / "job.dat").write_text("")
        (tmp_path / "job.frd").write_text("")
        return subprocess.CompletedProcess(cmd, 0, stdout="o" * 1000 + "x" * 1500, stderr="e" * 300 + "f" * 800)
    monkeypatch.setattr(calculix.subprocess, "run", solve)
    r = calculix.run_ccx("ccx.exe", deck)
    assert r == {"exit_code": 0, "dat": str(tmp_path / "job.dat"), "frd": str(tmp_path / "job.frd"),
                 "stdout_tail": "x" * 1500, "stderr_tail": "f" * 800}

    (tmp_path / "job.dat").unlink()
    (tmp_path / "job.frd").unlink()
    monkeypatch.setattr(calculix.subprocess, "run",
                        lambda cmd, **kw: subprocess.CompletedProcess(cmd, 201, stdout="*ERROR", stderr=""))
    assert calculix.run_ccx("ccx.exe", deck) == {"exit_code": 201, "dat": None, "frd": None, "stdout_tail": "*ERROR",
                                                 "stderr_tail": ""}
