"""Hand checks (hand_calcs.py) against values worked out by hand."""
import math
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import hand_calcs as hc  # noqa: E402


def test_bolt_m8_88():
    r = hc.bolt_check(8, shear_n=702.4, axial_n=156.3, grade="8.8")
    # F_v,Rd = 0.6·800·36.6/1.25 = 14054 N, F_t,Rd = 0.9·800·36.6/1.25 = 21082 N
    assert r["shear_resistance_n"] == pytest.approx(14054, abs=1)
    assert r["tension_resistance_n"] == pytest.approx(21082, abs=1)
    assert r["interaction"] == pytest.approx(702.4 / 14054 + 156.3 / (1.4 * 21082), abs=1e-3)
    assert r["ok"]


def test_bolt_bearing_governs_thin_sheet():
    r = hc.bolt_check(8, shear_n=5000, grade="8.8", plate_t_mm=1.5, plate_fu_mpa=360, e1_mm=15)
    # α_b = min(15/27, 800/360, 1) = 0.5556; F_b,Rd = 2.5·0.5556·360·8·1.5/1.25 = 4800 N
    assert r["bearing_resistance_n"] == pytest.approx(4800, abs=2)
    assert not r["ok"] and r["utilization"] == pytest.approx(5000 / 4800, abs=1e-3)


def test_weld():
    r = hc.weld_check(20000, length_mm=100, leg_mm=5)
    fvw = 360 / math.sqrt(3) / (0.8 * 1.25)
    assert r["throat_mm"] == 3.5
    assert r["f_vw_d_mpa"] == pytest.approx(fvw, abs=0.1)
    assert r["utilization"] == pytest.approx(20000 / (fvw * 3.5 * 100), abs=1e-3)


def test_sections():
    assert hc.section_props("rect", b_mm=10, h_mm=20)["I_mm4"] == pytest.approx(10 * 20 ** 3 / 12, abs=0.1)
    rhs = hc.section_props("rhs", b_mm=50, h_mm=50, t_mm=3)
    assert rhs["I_mm4"] == pytest.approx((50 ** 4 - 44 ** 4) / 12, abs=1)
    # an angle's centroid sits below the middle → c = h − y_c
    ang = hc.section_props("angle", b_mm=40, h_mm=40, t_mm=4)
    assert ang["c_mm"] > 20


def test_beam_cantilever():
    r = hc.beam_check("rect", span_mm=200, load_n=500, b_mm=40, h_mm=5, support="cantilever", fy_mpa=235)
    I, W = 40 * 5 ** 3 / 12, 40 * 5 ** 2 / 6
    assert r["bending_stress_mpa"] == pytest.approx(500 * 200 / W, abs=0.01)
    assert r["deflection_mm"] == pytest.approx(500 * 200 ** 3 / (3 * 210000 * I), abs=1e-3)
    assert not r["ok"]
