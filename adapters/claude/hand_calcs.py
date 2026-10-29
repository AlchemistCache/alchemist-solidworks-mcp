"""Hand checks that run without SolidWorks — an independent cross-check of FEA results (bolt forces from
sim_results → bolt_check; a weld line → weld_check; a bracket / frame member → beam_check).

Eurocode 3 (EN 1993-1-8) simplified rules; units N, mm, MPa. Every result carries its formula inputs so the
numbers can be checked by hand. Not a substitute for a design code review."""
import math

# ISO 898-1 / EN 1993-1-8 table 3.1: grade -> (f_yb, f_ub) MPa
BOLT_GRADES = {"4.6": (240, 400), "4.8": (320, 400), "5.6": (300, 500), "5.8": (400, 500), "6.8": (480, 600),
               "8.8": (640, 800), "10.9": (900, 1000), "A2-70": (450, 700), "A4-70": (450, 700), "A4-80": (600, 800)}
# ISO 898-1 tensile stress area A_s, mm² (coarse thread)
TENSILE_AREA = {3: 5.03, 4: 8.78, 5: 14.2, 6: 20.1, 8: 36.6, 10: 58.0, 12: 84.3, 14: 115, 16: 157, 18: 192,
                20: 245, 22: 303, 24: 353, 27: 459, 30: 561, 36: 817}
GAMMA_M2 = 1.25


def _r(x, n=3):
    return round(x, n) if isinstance(x, float) else x


def bolt_check(diameter_mm: float, shear_n: float = 0.0, axial_n: float = 0.0, grade: str = "8.8",
               shear_planes: int = 1, threads_in_shear: bool = True, plate_t_mm: float = 0.0,
               plate_fu_mpa: float = 360.0, e1_mm: float = 0.0, gamma_m2: float = GAMMA_M2) -> dict:
    """EN 1993-1-8 table 3.4: shear F_v,Rd = α_v·f_ub·A/γ_M2 per plane (α_v 0.6; 0.5 for 4.8/5.8/6.8/10.9 with
    threads in the shear plane), tension F_t,Rd = 0.9·f_ub·A_s/γ_M2, interaction F_v/F_v,Rd + F_t/(1.4·F_t,Rd) ≤ 1;
    bearing F_b,Rd = k1·α_b·f_u·d·t/γ_M2 (k1 = 2.5, α_b = min(e1/3d0, f_ub/f_u, 1)) when plate_t_mm is given."""
    if grade not in BOLT_GRADES:
        raise ValueError(f"grade: one of {', '.join(BOLT_GRADES)}")
    fyb, fub = BOLT_GRADES[grade]
    d = float(diameter_mm)
    As = TENSILE_AREA.get(int(round(d))) or 0.78 * math.pi * d * d / 4
    A = As if threads_in_shear else math.pi * d * d / 4
    av = 0.5 if threads_in_shear and grade in ("4.8", "5.8", "6.8", "10.9") else 0.6
    FvRd = av * fub * A / gamma_m2 * max(1, shear_planes)
    FtRd = 0.9 * fub * As / gamma_m2
    v, t = abs(shear_n), max(0.0, axial_n)
    res = {"grade": grade, "f_ub_mpa": fub, "A_s_mm2": _r(As, 1), "shear_resistance_n": _r(FvRd, 0),
           "tension_resistance_n": _r(FtRd, 0), "shear_util": _r(v / FvRd), "tension_util": _r(t / FtRd),
           "interaction": _r(v / FvRd + t / (1.4 * FtRd))}
    checks = [res["shear_util"], res["tension_util"], res["interaction"]]
    if plate_t_mm > 0:
        d0 = d + (1 if d <= 14 else 2)
        ab = min((e1_mm / (3 * d0)) if e1_mm > 0 else 1.0, fub / plate_fu_mpa, 1.0)
        FbRd = 2.5 * ab * plate_fu_mpa * d * plate_t_mm / gamma_m2
        res.update(bearing_resistance_n=_r(FbRd, 0), bearing_util=_r(v / FbRd), alpha_b=_r(ab))
        checks.append(res["bearing_util"])
    res["utilization"] = _r(max(checks))
    res["ok"] = res["utilization"] <= 1.0
    return res


def weld_check(force_n: float, length_mm: float, throat_mm: float = 0.0, leg_mm: float = 0.0,
               fu_mpa: float = 360.0, beta_w: float = 0.8, gamma_m2: float = GAMMA_M2, sides: int = 1) -> dict:
    """Fillet weld, EN 1993-1-8 4.5.3.3 simplified method: f_vw,d = f_u/√3/(β_w·γ_M2), F_w,Rd = f_vw,d·a per mm;
    utilization = F / (F_w,Rd·L·sides). a = throat (or 0.7 × leg). β_w: 0.8 S235, 0.85 S275, 0.9 S355."""
    a = throat_mm or 0.7 * leg_mm
    if a <= 0 or length_mm <= 0:
        raise ValueError("throat_mm (or leg_mm) and length_mm must be > 0")
    fvw = fu_mpa / math.sqrt(3) / (beta_w * gamma_m2)
    FwRd = fvw * a * length_mm * max(1, sides)
    tau = abs(force_n) / (a * length_mm * max(1, sides))
    return {"throat_mm": _r(a, 2), "f_vw_d_mpa": _r(fvw, 1), "weld_stress_mpa": _r(tau, 2),
            "resistance_n": _r(FwRd, 0), "utilization": _r(abs(force_n) / FwRd), "ok": abs(force_n) <= FwRd,
            "min_throat_mm": _r(abs(force_n) / (fvw * length_mm * max(1, sides)), 2)}


def section_props(section: str, b_mm: float = 0.0, h_mm: float = 0.0, t_mm: float = 0.0, d_mm: float = 0.0) -> dict:
    A, I, c = _section(section, b_mm, h_mm, t_mm, d_mm)
    return {"A_mm2": _r(A, 1), "I_mm4": _r(I, 1), "W_mm3": _r(I / c, 2), "c_mm": _r(c, 2)}


def _section(section, b_mm, h_mm, t_mm, d_mm):
    """Bending about the horizontal axis (load acts along h). rect b×h; rhs b×h×t (hollow); pipe d×t; round d;
    channel (U) b flange × h web × t, open side up; angle h×b×t (L, vertical leg h)."""
    if section == "rect":
        A, I, c = b_mm * h_mm, b_mm * h_mm ** 3 / 12, h_mm / 2
    elif section == "rhs":
        bi, hi = b_mm - 2 * t_mm, h_mm - 2 * t_mm
        A, I, c = b_mm * h_mm - bi * hi, (b_mm * h_mm ** 3 - bi * hi ** 3) / 12, h_mm / 2
    elif section == "pipe":
        di = d_mm - 2 * t_mm
        A, I, c = math.pi / 4 * (d_mm ** 2 - di ** 2), math.pi / 64 * (d_mm ** 4 - di ** 4), d_mm / 2
    elif section == "round":
        A, I, c = math.pi / 4 * d_mm ** 2, math.pi / 64 * d_mm ** 4, d_mm / 2
    elif section in ("channel", "angle"):
        # composite of rectangles: (area, centroid y, own I)
        if section == "channel":
            parts = [(b_mm * t_mm, t_mm / 2, b_mm * t_mm ** 3 / 12)] + \
                    [(t_mm * (h_mm - t_mm), t_mm + (h_mm - t_mm) / 2, t_mm * (h_mm - t_mm) ** 3 / 12)] * 2
        else:
            parts = [((b_mm - t_mm) * t_mm, t_mm / 2, (b_mm - t_mm) * t_mm ** 3 / 12),
                     (t_mm * h_mm, h_mm / 2, t_mm * h_mm ** 3 / 12)]
        A = sum(p[0] for p in parts)
        yc = sum(p[0] * p[1] for p in parts) / A
        I = sum(p[2] + p[0] * (p[1] - yc) ** 2 for p in parts)
        c = max(yc, h_mm - yc)
    else:
        raise ValueError("section: rect | rhs | pipe | round | channel | angle")
    return A, I, c


def beam_check(section: str, span_mm: float, load_n: float = 0.0, udl_n_per_mm: float = 0.0,
               support: str = "cantilever", b_mm: float = 0.0, h_mm: float = 0.0, t_mm: float = 0.0,
               d_mm: float = 0.0, E_mpa: float = 210000.0, fy_mpa: float = 235.0, deflection_limit: float = 200.0) -> dict:
    """Elastic beam: cantilever (point load at the free end / UDL) or simply_supported (point load mid-span /
    UDL). σ = M/W, f = classic formulas; utilization vs f_y and span/deflection_limit."""
    sp = section_props(section, b_mm, h_mm, t_mm, d_mm)
    _, I, c = _section(section, b_mm, h_mm, t_mm, d_mm)
    L, P, q, W = span_mm, load_n, udl_n_per_mm, I / c
    if support == "cantilever":
        M = P * L + q * L * L / 2
        f = P * L ** 3 / (3 * E_mpa * I) + q * L ** 4 / (8 * E_mpa * I)
        V = P + q * L
    elif support == "simply_supported":
        M = P * L / 4 + q * L * L / 8
        f = P * L ** 3 / (48 * E_mpa * I) + 5 * q * L ** 4 / (384 * E_mpa * I)
        V = P / 2 + q * L / 2
    else:
        raise ValueError("support: cantilever | simply_supported")
    sigma = M / W
    f_allow = L / deflection_limit
    return {**sp, "max_moment_nmm": _r(M, 0), "max_shear_n": _r(V, 1), "bending_stress_mpa": _r(sigma, 2),
            "deflection_mm": _r(f, 3), "deflection_limit_mm": _r(f_allow, 2), "stress_util": _r(sigma / fy_mpa),
            "deflection_util": _r(f / f_allow), "fos_yield": _r(fy_mpa / sigma, 2) if sigma > 0 else None,
            "ok": sigma <= fy_mpa and f <= f_allow}
