"""Drawings: create a drawing, model / flat-pattern / section views, view placement, dimensions, center marks,
hole callouts, the BOM table; analyze_drawing reads a DXF/DWG for the model to build the part from.
Units: MILLIMETRES (sheet coordinates too) and DEGREES (converted to SI for the server in units.py).
Registered by registry.register_all(); call plumbing in bridge.py."""
import hashlib
import json
import os
import sys
from typing import Annotated, Literal, Optional

from pydantic import Field

from drawing_summary import advisories, wire_json
import session
import adapter_log
from bridge import call, call_raw, dump, call_mm, scratch_dir
from config import CAD_PLANNER_DIR
from response_mapper import fail, failure_of


# ---------------------------------------------------------------------------
# Tool: analyze_drawing  (2026-07-27 — the SHIPPING drawing reader: DXF/DWG)
# ---------------------------------------------------------------------------
# The reader lives in cad-planner/drawing: a drawing is design intent expressed in 2D, read without SolidWorks.
# Imported EAGERLY, at startup, in the MAIN thread -- deliberately, and NOT lazily inside the tool. ezdxf pulls in numpy, and loading numpy's C extensions from an MCP worker
# thread DEADLOCKS on Windows: the tool hung forever with no error and no traceback (diagnosed by
# faulthandler thread dump, 2026-07-27 -- stuck in importlib create_module for numpy._core.
# multiarray). A failure here must still never kill the server, so it degrades to a clean per-call
# error instead.
try:
    if CAD_PLANNER_DIR not in sys.path:
        sys.path.insert(0, CAD_PLANNER_DIR)
    import drawing as _draw
    _DXF_READER_IMPORT_ERROR = None
except Exception as _exc:                     # noqa: BLE001 - startup must survive anything
    _draw = None
    _DXF_READER_IMPORT_ERROR = f"{type(_exc).__name__}: {_exc} (pip install ezdxf)"


def _read_drawing(src: str, save_analysis: bool):
    """The drawing as the reader's artifact + its config: a DWG converted to DXF first, the research artifact
    written beside the source when asked. Raises on the first failure."""
    ext = os.path.splitext(src)[1].lower()
    dxf_path = src
    if ext == ".dwg":
        # DWG is binary; the reader parses DXF only. SolidWorks opens the DWG as a drawing and exports DXF — measured
        # lossless for geometry, dimensions, DIMLFAC, linetypes and notes. The conversion is scratch (%TEMP%).
        dxf_path = os.path.join(scratch_dir("dwg2dxf"), hashlib.sha1(src.encode("utf-8")).hexdigest()[:10] + "-"
                                + os.path.splitext(os.path.basename(src))[0] + ".dxf")
        opened = call_raw("open_document", {"file_path": src})
        if opened.get("status") != "COMPLETED":
            raise failure_of(opened, f"opening the DWG '{src}'")
        try:
            exported = call_raw("export_document", {"formats": ["DXF"], "file_path": dxf_path})
        finally:
            call_raw("close_document", {})  # the import is scratch — discard, never save (even on errors)
        if exported.get("status") != "COMPLETED":
            raise failure_of(exported, "converting the DWG to DXF")
    if _draw is None:
        raise fail("READER_UNAVAILABLE", str(_DXF_READER_IMPORT_ERROR))
    try:
        cfg = _draw.load_config()
        art = _draw.read(dxf_path, cfg)
    except Exception as exc:
        adapter_log.exception("analyze_drawing read")
        raise fail("DXF_READ_FAILED", f"{type(exc).__name__}: {exc}") from exc
    if ext == ".dwg":
        art["source"]["converted_from"] = os.path.basename(src)
    if save_analysis:
        out = os.path.join(os.path.dirname(src), "%s.analysis-v%s.json" % (
            os.path.splitext(os.path.basename(src))[0], art["analysis_version"]))
        try:
            with open(out, "w", encoding="utf-8") as fh:
                json.dump(_draw.wire_encode(art), fh, indent=1, ensure_ascii=False)
        except OSError:                       # the artifact is research-only, never the result
            adapter_log.exception("analyze_drawing artifact write")
    return art, cfg



def register(mcp, tool):
    # ---------------------------------------------------------------------------
    # Tool: create_drawing
    # ---------------------------------------------------------------------------
    @tool
    def create_drawing(model_path: str = "") -> str:
        """Open a new SolidWorks drawing document (A3 sheet, 1:1 scale).
        model_path: not used here (a drawing references its model through its views) — give it to
        add_drawing_view / add_flat_pattern_view.
        Note: add_drawing_view requires the referenced part to be saved to disk."""
        return call("create_drawing", {"model_path": model_path})

    # ---------------------------------------------------------------------------
    # Tool: add_drawing_view
    # ---------------------------------------------------------------------------
    @tool
    def add_drawing_view(
        view_type: Literal["front", "top", "right", "isometric", "back", "bottom", "left"],
        pos_x: float = 100.0,
        pos_y: float = 100.0,
        scale: Annotated[float, Field(
            gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
        model_path: str = "",
        display_mode: Literal["", "hlv", "hlr", "wireframe", "shaded", "shaded_edges", "default"] = "",
    ) -> str:
        """Add a model view to the active drawing sheet.
        view_type: 'front', 'top', 'right', 'isometric', 'back', 'bottom', 'left'.
        pos_x/pos_y: position on the drawing sheet in mm.
        scale: view scale (default 1.0 = 1:1).
        model_path: path to the part file; if omitted, uses the first open part document.
        display_mode: view display style — 'hlv' (Hidden Lines Visible), 'hlr' (Hidden Lines Removed),
            'wireframe', 'shaded', 'shaded_edges', or 'default' (document default). Leave '' to apply the
            DRAFTING-CONVENTION default: orthographic views (front/top/right/back/bottom/left) become HLV
            (hidden lines shown for reference — but never dimension to them), isometric keeps the document
            default. Section views (add_section_view) are separate and show the cut, not hidden lines.
        Requires the part to be saved to disk (in-memory parts cannot be projected)."""
        return call(
            "add_drawing_view",
            {"view_type": view_type, "pos_x": pos_x, "pos_y": pos_y,
                "scale": scale, "model_path": model_path, "display_mode": display_mode},
        )

    # ---------------------------------------------------------------------------
    # Tool: add_flat_pattern_view
    # ---------------------------------------------------------------------------
    @tool
    def add_flat_pattern_view(
        pos_x: float = 100.0,
        pos_y: float = 100.0,
        scale: Annotated[float, Field(
            gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
        model_path: str = "",
        config_name: str = "",
        hide_bend_lines: bool = False,
        flip_view: bool = False,
    ) -> str:
        """Add a FLAT PATTERN view of a SHEET-METAL part to the active drawing — the unfolded blank with
        bend lines/notes. Sheet metal is dimensioned on its FLAT PATTERN (blank size, hole positions,
        bend lines), not on the folded orthographic views, so use this rather than them; then call
        auto_dimension_drawing.

        pos_x/pos_y: position on the drawing sheet in mm.
        scale: view scale (default 1.0 = 1:1).
        model_path: path to the sheet-metal part; if omitted, uses the first open part document.
        config_name: configuration to flatten; if omitted, the part's active configuration is used
            (falling back to 'Default').
        hide_bend_lines: hide the bend lines in the flat pattern (default False).
        flip_view: flip the flat pattern view (default False).

        The part must be sheet metal (have a Flat-Pattern feature) and saved to disk, else returns
        FLAT_PATTERN_VIEW_FAILED. Answers {view_name, config}."""
        return call(
            "add_flat_pattern_view",
            {"pos_x": pos_x, "pos_y": pos_y, "scale": scale, "model_path": model_path,
                "config_name": config_name, "hide_bend_lines": hide_bend_lines,
                "flip_view": flip_view},
        )

    # ---------------------------------------------------------------------------
    # Tool: add_drawing_dimension
    # ---------------------------------------------------------------------------
    @tool
    def add_drawing_dimension(
        px: float,
        py: float,
        value: Annotated[float, Field(gt=0, description="Dimension value in mm")],
        label_offset_x: float = 0.0,
        label_offset_y: float = -15.0,
    ) -> str:
        """Add a smart dimension to a drawing view segment nearest to point (px, py).
        Works the same way as add_dimension but operates in a drawing document context.
        px/py should be on or very near the target segment in drawing sheet coordinates (mm).
        Requires an active drawing document."""
        return call(
            "add_drawing_dimension",
            {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
                "label_offset_y": label_offset_y},
        )

    # ---------------------------------------------------------------------------
    # Tool: auto_dimension_drawing
    # ---------------------------------------------------------------------------
    @tool
    def auto_dimension_drawing(
        all_views: bool = True,
        include_unmarked: bool = False,
        eliminate_duplicates: bool = True,
        view: str = "",
        arrange: bool = True,
        features: list[str] = [],
    ) -> str:
        """Transfer the MODEL's driving dimensions into the active drawing's views (SolidWorks 'Insert
        Model Items > Dimensions'). PREFER this over add_drawing_dimension's coordinate pick — the
        values come from the model's own parametric dimensions and are placed for you. Call it AFTER
        create_drawing + add_drawing_view(s), then verify with check_drawing (dimension_count > 0,
        values matching the model).

        all_views: insert into all drawing views (True) or only the currently selected view (False). Default True.
        view: insert into this view only (its name, e.g. 'Чертежный вид2'). A model dimension already shown elsewhere is
            then shown twice — dedupe_drawing_dimensions keeps one.
        include_unmarked: also insert driving dimensions NOT marked for drawing (i.e. ALL driving dims), not just
            those flagged 'marked for drawing'. More complete but noisier. Default False — if a marked-only pass
            inserts 0 dimensions (the part's dims weren't marked for drawing), re-call with include_unmarked=True.
        eliminate_duplicates: avoid inserting the same model dimension into more than one view. Default True.
        arrange: move the dimensions OFF the geometry (SolidWorks Auto Arrange: linear ones stacked outside the view,
            the shortest nearest; radii / diameters out on leaders) — they come in where they stood in the sketch,
            across the part. Default True.
        features: only the dimensions of these features and of their sketches (names as analyze_model(features) gives
            them) — the sizes the drawing should give; a part of 30 features otherwise brings 80+ dimensions. Checked
            before anything is inserted; names not found come back in features_not_found. Dimensions already on the
            sheet (hand-placed, earlier calls) are neither filtered nor moved.

        SAVE THE DRAWING FIRST: SolidWorks inserts nothing into a drawing that was never saved (inserted_count 0 + warning).
        It may still put one model dimension into several views — dedupe_drawing_dimensions keeps one.

        Answers inserted_count (the number of dimensions placed; 0 is a valid
        outcome). Requires an active drawing with at least one model view. Bumps state_version."""
        return call(
            "auto_dimension_drawing",
            {"all_views": all_views, "include_unmarked": include_unmarked,
                "eliminate_duplicates": eliminate_duplicates, "view": view or None, "arrange": arrange,
                "features": features},
        )

    # ---------------------------------------------------------------------------
    # Tool: auto_center_marks
    # ---------------------------------------------------------------------------
    @tool
    def auto_center_marks(
        include_slots: bool = True,
        extended_lines: bool = True,
    ) -> str:
        """Automatically place center marks (with extended centerlines) on every hole/slot in every
        model view of the active drawing (SolidWorks 'Auto Insert > Center Marks'). This is the robust,
        automatic way to add centerlines to a holed part's drawing — no coordinate picking. Call it
        after the views exist (create_drawing + add_drawing_view), typically alongside
        auto_dimension_drawing, for a difficulty-2-grade drawing of a bracket/flange with holes.

        include_slots: also add slot center marks/centerlines for linear & arc slots (not just round holes). Default True.
        extended_lines: draw the extended centerlines through each hole rather than a small cross. Default True.

        Answers center_marks (total marks placed across all views; 0 if the
        part has no holes/slots). Bumps state_version. Requires an active drawing with model views."""
        return call(
            "auto_center_marks",
            {"include_slots": include_slots, "extended_lines": extended_lines},
        )

    # ---------------------------------------------------------------------------
    # Tool: add_hole_callout
    # ---------------------------------------------------------------------------
    @tool
    def add_hole_callout(px: float, py: float) -> str:
        """Insert a hole callout (e.g. '4× Ø8 THRU') on the hole edge nearest the sheet point (px, py)
        in the active drawing. Coordinate-based selection — same fragile point pick as add_drawing_dimension
        so place px/py right on the hole's projected circular edge. For centerlines
        prefer auto_center_marks (robust/automatic); use this for explicit per-hole callouts where wanted.
        px/py: a point on the target hole's projected circle, in drawing sheet coordinates (mm).
        Requires an active drawing document. Bumps state_version."""
        return call("add_hole_callout", {"px": px, "py": py})

    # ---------------------------------------------------------------------------
    # Tool: add_section_view
    # ---------------------------------------------------------------------------
    @tool
    def add_section_view(
        px: float,
        py: float,
        edge_x: Optional[float] = None,
        edge_y: Optional[float] = None,
        x1: Optional[float] = None,
        y1: Optional[float] = None,
        x2: Optional[float] = None,
        y2: Optional[float] = None,
        label: str = "A",
        flip: bool = False,
        scale: float = 0.0,
    ) -> str:
        """Create a SECTION VIEW (sheet coordinates in mm) that cuts the model so internal/blind features become
        visible, dimensionable
        edges. Use this when a feature's size can't be shown in a projected view — most importantly a BLIND
        POCKET's DEPTH (its floor is a hidden edge in an orthographic view, so auto_dimension_drawing can't
        place the depth).

        Two ways to define the cut (provide ONE pair-set):
        - EDGE mode (edge_x, edge_y): cut collinear with an existing straight edge already projected in
          a view — point at it.
        - LINE mode (x1,y1 → x2,y2): draw the cut line, for cutting THROUGH a feature's interior where
          no edge exists. It must fully cross the view through the feature.
        All coordinates are drawing sheet coordinates in mm (coordinate-based: put the point right on the edge).

        px,py: where to place the section view (mm) — also picks which side the section projects toward.
        label: section label (default 'A' → 'SECTION A-A'). flip: reverse the cut/viewing direction.
        scale: optional section view scale; inherits the parent view's scale if 0/omitted.

        After the section lands, call auto_dimension_drawing or add_drawing_dimension to dimension the
        now-visible depth. Bumps state_version. Requires an active drawing with a parent model view. If it
        returns SECTION_VIEW_FAILED, the drawing state may be wedged by prior failed attempts — a fresh
        server/drawing state clears it."""
        return call("add_section_view", {"px": px, "py": py, "label": label, "flip": flip,
                                          "edge_x": edge_x, "edge_y": edge_y, "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                                          "scale": scale if scale and scale > 0 else None})

    @tool
    def analyze_drawing(file_path: str, save_analysis: bool = False) -> str:
        """Read a 2D technical drawing — **.DXF or .DWG** — completely: every view, contour, dimension and note, for
        you to build the part from with the ordinary tools. A real drawing arrives as DXF/DWG (a .SLDDRW is
        model-linked and nobody ships one), so start here for ANY "build the part from this drawing" job. A .DWG is
        converted automatically. The answer: {source, advisories, next, analysis} — the whole reading in one answer.

        ⮕ Before interpreting a single number, read tool_help('reading_drawings') — the reading discipline
          (projection standard, which face a feature is on, contour gaps, sheet-metal bend arithmetic) and the
          self-checks; a mirrored part passes volume, area and topology alike.

        The analysis — WHAT EVERY FIELD MEANS (the decisions are in tool_help('reading_drawings')):
          sheet — dxf_version, units, **scale_factor** and the sheet box. `scale_factor` is ALREADY
            applied to every size/coordinate/dimension below, so all numbers are TRUE model mm and
            multiplying again builds a half- or double-size part. An ANGLE is never scaled (DIMLFAC is
            a length factor).
            `not_read` counts what the reader saw and did NOT emit, by entity class and — for an
            INSERT — by block name, e.g. {"HATCH": 5, "INSERT": {"WALZRICHTUNG": 1}}. Nothing vanishes
            silently: a named block you cannot account for is evidence that exists on the sheet and
            did not reach you.
          frame — {paper_box, title_block_top, primitives, circles[], note_count}: the border/title
            block cluster, which is never emitted as a view. A candidate is accepted ONLY when it
            CONTAINS every other cluster, so `paper_box: null` means this sheet has no frame and every
            cluster was a view candidate. A cluster in the title-block corner that no dimension points
            at arrives as `role: "frame_item"` (a projection symbol, a weld symbol) — furniture.
          frame_notes[] / notes[] — the sheet's text in reading order, `frame_notes` being the title
            block's. Text INSIDE inserted blocks (SW_NOTE, BIEGETEILE, a company stamp) is included,
            one entry per ROW, nested to any depth. The DWG route splits a row into per-glyph
            fragments that are re-joined left to right, so match on content, not on exact spacing.
          views[] — one per detected view: `vid`, `role`, `paper_box` (sheet coords, for reading the
            LAYOUT), `geom_box`, `size` [w,h] in true mm, `dropped_duplicates`? (exact duplicates the
            DWG route re-emitted), `scale_factor`? — PRESENT ONLY when this view is drawn at a ratio
            DIFFERENT from the sheet's (a detail or section labelled 'A-A 1 : 1' on a 1:10 sheet); its
            geometry and dimensions are already scaled with THAT factor.
            Then `geometry` {lines, arcs, circles, ellipses} in view-local
            true mm with the view's own bottom-left as origin. The EDGE CLASS is carried on every
            primitive as `c`: 'visible' (near-side edge) | 'hidden' (obscured — a real feature seen
            through material) | 'cut_line' (a section's cutting line, not part geometry) | 'center'
            (an axis).
            `lines` is the one array grouped BY that class instead of carrying it per record:
            {"visible": [[index, x1, y1, x2, y2], …], "hidden": […]}. THE FIRST NUMBER IS THE INDEX —
            the one a `seq` entry names — written in rather than implied by position, so ['l', 12, 1]
            is found by looking for the row starting with 12, in whichever group, and never by
            recomputing an offset. Indices run 0..n-1 across all the groups together.
            `ellipses` {cx, cy, rx, ry, rot (+ t1, t2, x1..y2 for an arc)} are PARAMETRIC — a circle
            on a plane tilted by θ projects with ry/rx = cos θ and rx its true radius, so a Ø10 hole
            on a 20° face arrives as rx 5 / ry 4.699, not as 48 line segments. A record carrying
            `n` + `fit` was recovered from an exploded fan of n segments (fit = max deviation, mm);
            one without is a real DXF ellipse.
            The sheet border/title block is NOT a view; a cluster INSIDE a view is that view's feature.
          views[].loops[] — the CLOSED contours, already chained: {id, class, role outer|inner, parent,
            area (mm^2, arc bulges exact), bbox, seq, tier?}. `seq` is [[code, index, dir], …]
            referencing that view's own geometry arrays — the contour IN ORDER, ready to transcribe as
            a sketch path profile. Traverse it as given; consecutive entries share an endpoint exactly.
            `tier` is PRESENT ONLY when it is "B", so the common case costs nothing. Absent = Tier A,
            strict chaining, which stops dead at any junction of three or more and therefore never
            guesses. "B" = planar face traversal, run ONLY on a view whose visible graph has no free
            end — with no dangling edge the figure is a closed planar subdivision and its faces are
            DEFINED by the angular order of the edges at each vertex, so walking it is a reading. It
            appears only where Tier A found nothing.
          views[].open_singles[] — [position, code, index] triples: the SINGLE-segment open chains,
            which are most of them. Nothing is abbreviated away — the id is 'O<position>', the class is
            that primitive's own `c`, the direction is 1 — and a chain whose id/class/direction are not
            all recoverable stays a full record in `open_chains` instead. Read the two together: a
            primitive named here is exactly as unattributed as one in `open_chains`.
          views[].open_chains[] — the MULTI-segment chains belonging to no contour: bend lines, centre
            lines, and (in an ortho view) silhouette fragments the chainer refused to guess through
            a T-junction.
          alignment[] — GRADED view pairs: shares='x' is a vertical projection pair, 'y' a horizontal
            one, and `grade` says how strong the evidence is. 'span' = the two share that axis's span
            edge-for-edge, the strongest. 'mid' = only the midpoints coincide — still a measurement,
            but silhouette ends may genuinely differ, and on a bend-note sheet a mid pair may relate
            the FLAT and the BENT state (two states of the part, not two projections). 'label' = a
            section caption ties the section view `b` to the view `a` carrying its cut line, with
            shares=null, so it survives a cross-scale section.
          view_graph — {solved, views:{vid: [h_axis, v_axis]}} when the pairs pin every view's two
            paper axes onto the part's three. TOPOLOGY ONLY, over ANONYMOUS axes ax0/ax1/ax2: which
            axis is depth and which view is "front" is the projection convention's SIGN, not a field.
            `solved: false` names why, and the joining is yours again.
          dimensions[] — `value` (TRUE), `kind` (linear|aligned|angular|diameter|radius|ordinate),
            `defpts` (the dimension's own reference points, sheet coords) and the owning `view`.
            `printed` is the string the CAD system actually DREW, taken from the dimension's own
            block, with `%%c`→Ø, `%%d`→°, `%%p`→± decoded (the symbol is evidence about what was
            dimensioned); `text` beside it is the raw undecoded override (e.g. '8x <>' = a count).
            The DWG→DXF route corrupts the stored measurement in two whole classes — an ANGULAR value
            comes back as 180+θ, a RADIUS loses its arc side — so the reader ARBITRATES: for radius and
            diameter the printed value wins outright, and for an angle it takes the candidate ≤ 180°.
            `printed_mismatch: true` marks what it could not settle.
            `kind_from: "printed"` marks a dimension whose TYPE the drawn symbol overruled — a plain
            linear dim drawn across a circle with a hand-typed Ø is re-emitted as `kind: "diameter"`.
            `measures` links a dimension to the primitive the reader matched it to.
          bend_notes[] — sheet metal: {dir UP|DOWN, angle_deg, radius, view} plus either `bend_line`
            (the line it annotates, that line's edge class, and `in_loop` — true would mean the match
            landed on outline geometry, which a bend line never is) or `unpaired` with the reason and
            the candidates. Direction comes from the NOTE; the class corroborates it.
          notes[] — other free text on the sheet (e.g. a bare '2 mm' thickness note, 'SECTION C-C'),
            text inside note blocks included.

        file_path: absolute path to the .DXF or .DWG.
        save_analysis (default False): also write `<name>.analysis-v<version>.json` into the drawing's
            folder — a research artifact for cross-part study, versioned by the analyzer; same version
            overwrites. Off by default: it writes into the user's folder, and you never need to read it
            back — this answer already carries everything in it."""
        src = session.full_path("file_path", file_path)
        if not os.path.exists(src):
            raise fail("FILE_NOT_FOUND", src)
        ext = os.path.splitext(src)[1].lower()
        if ext not in (".dxf", ".dwg"):
            raise fail("UNSUPPORTED_TYPE", "analyze_drawing reads .DXF or .DWG. "
                                           "A native .SLDDRW is a test-only path (check_drawing).")
        art, _cfg = _read_drawing(src, save_analysis)
        return dump({"source": {"file": art["source"]["file"], "sha256": art["source"]["sha256"][:12]},
                     "advisories": advisories(art).strip() or None,
                     "next": "read it per tool_help('reading_drawings'), build with the sketch / feature tools",
                     "analysis": json.loads(wire_json(_draw.wire_encode(art)))})

    # ---------------------------------------------------------------------------
    # Tool: check_drawing  (was analyze_slddrw_test until 2026-10-21, analyze_drawing until 2026-07-27)
    # ---------------------------------------------------------------------------
    @tool
    def check_drawing(include_geometry: bool = False, include_relations: bool = False) -> str:
        """Check a NATIVE SolidWorks drawing (.SLDDRW) read through COM — views, dimensions, sections.

        ⚠ NOT the shipping drawing→part path. A .SLDDRW is SLDPRT-linked (its views go blank if the part
          is gone) and nobody ships one — real inputs are DXF/DWG (and PDF). This tool survives for two
          jobs ONLY: (a) checking a drawing YOU produced (do its dimensions match the model?), and
          (b) generating COM ground truth to compare a DXF reader against. For a DXF/DWG job do NOT use
          this tool and do NOT apply its discipline — the fields below (measures, section, normal_axis,
          relations, extent, center_marks) are COM constructs that DO NOT EXIST in a DXF.

        Read-only, does NOT change state. Requires an active NATIVE drawing document. All lengths in mm,
        dimension values value_mm / value_deg. Returns {view_count, dimension_count, views:[…]}, and the
        FIRST view is the drawing SHEET.

        view — {name, type (swDrawingViewTypes_e int), scale, pos [x,y], dimensions[], section?}
        dimension — {name, value_mm | value_deg, diametric?, attached?, anchors?}. `diametric` true means Ø is DATA,
          not a guess. `anchors` are the dim's reference points in MODEL space; an anchor beyond the base
          body's depth signals a feature that extends past it (offset-plane boss/loft), not bad data.
        section (SECTION views only) — {parent_view, cut_normal (the cutting plane's MODEL-space normal,
          i.e. the viewing direction), axis? (that normal snapped to X/Y/Z when axis-aligned), frame
          {origin, xdir, ydir}, label?}. A 2D coord maps to 3D as p = origin + u*xdir + v*ydir.

        include_geometry (default False) — each view's projected 2D geometry as clean primitives:
          {lines[{x1,y1,x2,y2}], curves[{n,x1,y1,xm,ym,x2,y2,cx?,cy?,r?}] (partial arcs as start/mid/end
          + fitted centre), circles[{cx,cy,r}] (FULL circles, true centre+radius), frame{origin,xdir,ydir,
          normal_axis}, extent? }. Source IView.GetPolylines7; heavier payload, so use it when you need
          the SHAPE, not for a dimension check. Two rules that fail silently if broken:
          · `normal_axis` is the view's viewing direction as a SIGNED principal axis. A circle is the
            cross-section of a feature whose axis runs along it, so circles in views with DIFFERENT
            normal_axis are DIFFERENT features — never chain them into one by radius alone.
          · `extent` {axis:[min,max]} is the SERVER-computed model-space span, frame signs applied. Read
            it before any "no material beyond X" claim; never re-derive a span from raw 2D coordinates,
            because a frame's direction components can be negative.

        include_relations (default False; forces include_geometry) — additive deterministic enrichment;
          pass BOTH for a reconstruction. Ids are positional per read: vid 'v<i>', and within a view
          c<i>/a<i>/l<i> for circles/curves/lines. Every relation carries `source` (closed enum) and
          `residual` (max deviation), so "why is this concentric?" is answerable by reading the field.
          · relations — concentric {members,center,radii} (TRUST these centres, never re-derive),
            equal_diameter {members,r,centers} (same Ø at distinct centres = twin bores), tangent.
          · measures — per dimension, the primitive id(s) it measures. `unattached:true` means it
            resolved to NO primitive: a loud gap to close from geometry, never to drop.
          · center_marks / centerlines — which circles carry marks (on:[ids]), which segments run
            through which (through:[ids]). A view that cannot expose its centerlines says so
            ({centerlines_reported, unreadable:true}) instead of guessing."""
        return call("check_drawing", {"include_geometry": include_geometry,
                                             "include_relations": include_relations})

    # ------------------------------------------------------------------ view placement, BOM table (mm)
    @tool
    def move_drawing_view(view: str, x_mm: Optional[float] = None, y_mm: Optional[float] = None,
                          scale: Optional[float] = None) -> str:
        """Move a view of the ACTIVE drawing to x_mm/y_mm on the sheet (view centre) and/or set its scale
        (0.5 = 1:2). Returns the new position."""
        return call_mm("move_drawing_view", dict(view=view, x_mm=x_mm, y_mm=y_mm, scale=scale))

    @tool
    def layout_drawing_views(scale: str = "fit", projection: Literal["auto", "first", "third"] = "auto",
                             gap_mm: float = 15.0, margins_mm: list[float] = [], title_block_mm: list[float] = [],
                             arrange: bool = True) -> str:
        """Arrange the views of the ACTIVE sheet: front view as the main one, the others in projection relation to it
        (first angle — GOST/ISO E: top view below, left view on the right; third angle — top above, right view on the
        right; auto = the sheet's own setting), lined up on the views' own points; isometric, sections, details and
        flat patterns in the free cells of that block, then a column on its right (a section's projection link is
        removed — it stands by its label). Each view's box counts its dimension texts and, for a labelled view, the
        label SolidWorks keeps above it (~28 mm). No overlaps, inside the frame (margins_mm [left, bottom, right, top], default GOST 2.301: 20, 5, 5, 5), clear of the title
        block (title_block_mm [w, h] in the bottom right corner, default 185 × 55) and of the sheet's own notes and
        signs (technical requirements, roughness). scale: "fit" = the largest standard scale (GOST 2.302) at which it
        all fits — set as the SHEET's scale (the title block shows it, the views use it); "keep"; or a ratio "1:2".
        Tables are not avoided. arrange (default True) sets the dimensions off the geometry again after a scale
        change (Auto Arrange). Returns each view's box, the scale and fits / overlaps."""
        return call_mm("layout_drawing_views", dict(scale=scale, projection=projection, gap_mm=gap_mm,
                     margins_mm=margins_mm, title_block_mm=title_block_mm, arrange=arrange))

    @tool
    def dedupe_drawing_dimensions(dry_run: bool = False, arrange: bool = True) -> str:
        """Remove repeated dimensions on the ACTIVE drawing: auto_dimension_drawing puts one model dimension into every
        view that can show it, while a drawing gives each size once. Each model dimension shown in several views is
        kept in the best one — front, top, left / right, section, detail, the rest; isometric last — and its copies
        are deleted from the drawing (the model is not touched). Also one size given twice in a view by two sketches
        (same kind, value and measured points — the profile's 170 and the holes' 170): the first stays. Dimensions
        made in the drawing are left alone. arrange (default True) closes up the stacks afterwards. dry_run lists
        what would go."""
        return call_mm("dedupe_drawing_dimensions", dict(dry_run=dry_run or None, arrange=arrange))

    @tool
    def add_dimension_between(x1_mm: float, y1_mm: float, x2_mm: float, y2_mm: float, view: str = "",
                              orientation: Literal["auto", "horizontal", "vertical"] = "auto",
                              text_x_mm: Optional[float] = None, text_y_mm: Optional[float] = None) -> str:
        """Drawing dimension BETWEEN two entities of one view (edge – edge, hole circle – edge, vertex – vertex …):
        a point ON each. With `view` the points (and text) are in that view's own frame, model mm — exactly the
        coordinates check_drawing(include_geometry) lists for its lines / circles; without it they are sheet mm.
        orientation horizontal / vertical measures along the sheet's X / Y; auto lets SolidWorks choose (parallel
        edges → their distance, two circles → centre distance). Text default: 12 mm above (right of, for vertical)
        the pair. Returns the value in mm (deg) and the sheet points used."""
        return call_mm("add_dimension_between", dict(x1_mm=x1_mm, y1_mm=y1_mm, x2_mm=x2_mm, y2_mm=y2_mm, view=view or None,
                     orientation=orientation, text_x_mm=text_x_mm, text_y_mm=text_y_mm))

    @tool
    def insert_bom_table(view: str = "", bom_type: Literal["top_level", "parts_only", "indented"] = "top_level",
                         anchor: Literal["top_right", "top_left", "bottom_right", "bottom_left"] = "top_right",
                         x_mm: Optional[float] = None, y_mm: Optional[float] = None, template: str = "",
                         config: str = "", balloons: bool = False) -> str:
        """BOM table on the ACTIVE drawing for the assembly shown in `view` (default: the first assembly view):
        at the sheet's table anchor (anchor) or at x_mm/y_mm; template default = SolidWorks bom-standard.
        balloons=True adds auto-balloons to that view, numbered like the BOM. Returns header + first rows."""
        return call_mm("insert_bom_table", dict(view=view, bom_type=bom_type, anchor=anchor, x_mm=x_mm, y_mm=y_mm,
                     template=template, config=config, balloons=balloons or None))
