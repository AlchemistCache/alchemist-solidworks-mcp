"""Weldments: GOST profiles, structural members, cut list, trim / extend, end caps, gussets. Units: mm, degrees.
Registered by registry.register_all(); call plumbing in bridge.py."""
from typing import Literal


import session
from bridge import dump, need_xlsx, call_mm, call_mm_json, sheet_reports
from jobs import background


def register(mcp, tool):

    # ------------------------------------------------------------------ weldments
    @tool
    def weldment_profiles(standard: str = "", type: str = "") -> str:
        """Weldment profile library (Tools > Options > File Locations > Weldment Profiles): standards (iso, DIN,
        GOST …) → types → sizes. A size is either a .SLDLFP file in <standard>\\<type>\\ or a configuration of
        <standard>\\<type>.SLDLFP (GOST folder). Use as weldment_member(profile='<standard>/<type>/<size>')."""
        return call_mm("weldment_profiles", dict(standard=standard, type=type))

    @tool
    def create_weldment_profiles(kind: Literal["angle", "channel", "pipe"], sizes: list[str] = [], out_dir: str = "",
                                 overwrite: bool = False) -> str:
        """Weldment profiles the SolidWorks library lacks: GOST equal angles 8509-93 (20x20x3 … 100x100x8), channels
        8240-97 series П (5П … 20П), seamless pipes 8732-78 (25x3 … 159x6) — one .SLDLFP per size in
        <out_dir>/GOST/<type>/ (default %USERPROFILE%/SolidWorksMCP/weldment profiles, searched by weldment_member).
        sizes = subset (e.g. ["40x40x4"], ["10П"]), default all. Section data = reference values — check them."""
        return call_mm("create_weldment_profiles", dict(kind=kind, sizes=sizes, out_dir=out_dir, overwrite=overwrite or None))

    @tool
    def weldment_member(profile: str, sketch: str = "", segments: list[int] = [], size: str = "",
                        angle_deg: float = 0.0, corner: Literal["miter", "butt1", "butt2", "none"] = "miter",
                        groups: list[dict] = []) -> str:
        """Structural members (сварная конструкция) in the ACTIVE part along sketch segments: profile
        '<standard>/<type>/<size>' (weldment_profiles) or a .SLDLFP path (+ size = configuration). One group =
        sketch (+ segments: indices among its non-construction segments, default all), angle_deg (profile rotation),
        corner treatment at group corners. Several groups: groups=[{sketch, segments, angle_deg, corner, mirror}].
        Draw the layout first (create_sketch + lines; 3D frames: several sketches)."""
        return call_mm("weldment_member", dict(profile=profile, sketch=sketch, segments=segments, size=size,
                     angle_deg=angle_deg, corner=corner, groups=groups))

    @tool
    def weldment_cut_list(csv_path: str = "", xlsx_path: str = "", properties: bool = False) -> str:
        """Weldment cut list (ведомость резки профиля) of the ACTIVE part: per item qty, description (profile size),
        length mm, end angles, material, mass; totals per profile (pieces, total length m). csv_path / xlsx_path
        write files; properties=True adds every cut-list property."""
        if not xlsx_path:
            return call_mm("weldment_cut_list", dict(csv_path=csv_path, properties=properties or None))
        xlsx = session.full_path("xlsx_path", xlsx_path)   # the adapter writes it: checked here, before any work

        def run():
            need_xlsx()
            r = call_mm_json("weldment_cut_list", dict(csv_path=csv_path, properties=properties or None))
            r.update(sheet_reports.write_table_xlsx(
                xlsx, "Профиль", f"Ведомость резки профиля — {r.get('document', '')}",
                [("Поз.", "item", 18), ("Кол.", "qty", 7), ("Профиль", "description", 30), ("Длина, мм", "length_mm", 11),
                 ("Угол 1", "angle1_deg", 8), ("Угол 2", "angle2_deg", 8), ("Материал", "material", 22),
                 ("Масса, кг", "mass_kg", 10)], r.get("rows") or [],
                totals=("Итого по профилям", [("Профиль", "description", 30), ("Шт", "pieces", 7),
                                              ("Длина, м", "total_length_m", 11)], r.get("totals") or [])))
            return dump(r)
        return background("weldment_cut_list", run)

    @tool
    def weldment_trim(bodies: list[str], by_bodies: list[str] = [], by_faces: list[int] = [],
                      corner: Literal["trim", "miter", "butt1", "butt2"] = "trim", allow_extension: bool = True,
                      coped: bool = False, gap_mm: float = 0.0) -> str:
        """Trim / extend members (bodies by name, analyze_model(bodies)) to other members or to faces
        (by_faces: face indices). coped = coped cut, gap_mm = weld gap."""
        return call_mm("weldment_trim", dict(bodies=bodies, by_bodies=by_bodies, by_faces=by_faces, corner=corner,
                     allow_extension=allow_extension, coped=coped or None, gap_mm=gap_mm or None))

    @tool
    def weldment_end_cap(face_indices: list[int], thickness_mm: float = 5.0, offset_mm: float = 0.0,
                         chamfer_mm: float = 0.0) -> str:
        """End caps (заглушки) on open member end faces (analyze_model(faces) indices); offset_mm = inset from the
        profile's outer edge, chamfer_mm = corner chamfer."""
        return call_mm("weldment_end_cap", dict(face_indices=face_indices, thickness_mm=thickness_mm, offset_mm=offset_mm,
                     chamfer_mm=chamfer_mm))

    @tool
    def weldment_gusset(face_indices: list[int], thickness_mm: float = 5.0, d1_mm: float = 50.0, d2_mm: float = 50.0,
                        shape: Literal["triangle", "polygon"] = "triangle", d3_mm: float = 10.0, d4_mm: float = 10.0,
                        location: Literal["center", "start", "end"] = "center") -> str:
        """Gusset (косынка) between two planar faces of members meeting at a corner: legs d1/d2 mm, thickness;
        polygon shape uses d3/d4 (chamfer)."""
        return call_mm("weldment_gusset", dict(face_indices=face_indices, thickness_mm=thickness_mm, d1_mm=d1_mm,
                     d2_mm=d2_mm, shape=shape, d3_mm=d3_mm, d4_mm=d4_mm, location=location))
