"""Sketches: create / edit a sketch, entities (one or a batch), text, relations, dimensions, modify a dimension.
Units: MILLIMETRES and DEGREES (converted to SI for the server in units.py).
Registered by registry.register_all(); call plumbing in bridge.py."""
from typing import Annotated, Literal

from pydantic import Field

from bridge import call, call_mm


def register(mcp, tool):
    # ---------------------------------------------------------------------------
    # Tool: create_sketch
    # ---------------------------------------------------------------------------
    @tool
    def create_sketch(
        plane: str = "",
        on_face: bool = False,
        face_index: int = -1,
        face_x: float = 0.0,
        face_y: float = 0.0,
        face_z: float = 0.0,
    ) -> str:
        """Create a new sketch on a plane OR on an existing model face (points in mm).
        Default (on_face=False): provide plane (e.g. 'Front Plane', 'Top Plane', or a named ref plane like 'Plane1').
        On a model face (on_face=True), pick the face one of two ways:
          • face_index (PREFERRED) — the integer index of the target face from analyze_model(analysis_type='faces').
            Robust: it selects the face directly, so it works on a revolve end-cap / shaft end where a coordinate
            pick is ambiguous (the flat circular face's centre lies on the revolve axis → FACE_NOT_FOUND).
          • face_x/face_y/face_z — a point (mm) lying ON the target planar face (interior, NOT on an edge).
        Use on_face for features on existing geometry (recess/hub/keyway on a part face, a bore on a shaft end);
        plane names cannot reach model faces. face_index takes priority over face_x/y/z when >= 0."""
        return call("create_sketch", {"plane": plane, "on_face": on_face,
                                      "face_index": face_index if face_index >= 0 else None,   # -1 = not given
                                      "face_x": face_x, "face_y": face_y, "face_z": face_z})

    # ---------------------------------------------------------------------------
    # Tool: add_sketch_entity
    # ---------------------------------------------------------------------------
    @tool
    def add_sketch_entity(
        entity_type: Literal["rectangle", "circle", "line", "arc", "arc_center", "ellipse", "spline", "fillet", "chamfer"],
        x1: float = 0.0,
        y1: float = 0.0,
        x2: float = 0.0,
        y2: float = 0.0,
        xm: float = 0.0,
        ym: float = 0.0,
        cx: float = 0.0,
        cy: float = 0.0,
        radius: float = 0.0,
        vx: float = 0.0,
        vy: float = 0.0,
        distance: float = 0.0,
        direction: float = 0.0,
        points: list[float] = [],
        construction: bool = False,
    ) -> str:
        """Add a sketch entity to the active sketch — coordinates and sizes in mm, in the sketch's own frame.
        entity_type='rectangle':  uses x1,y1 (first corner) and x2,y2 (opposite corner).
        entity_type='circle':     uses cx,cy (center) and radius.
        entity_type='line':       uses x1,y1 (start) and x2,y2 (end).
        entity_type='arc':        uses x1,y1 (start), x2,y2 (end), xm,ym (mid-arc point) — a 3-point arc.
        entity_type='arc_center': uses cx,cy (exact center), x1,y1 (start), x2,y2 (end) and optional
                                  direction. PREFER this over 'arc' whenever the exact centre/radius are
                                  known: it guarantees the radius, and the 3-point fit is unreliable on
                                  shallow or near-collinear arcs.
        entity_type='ellipse':    uses cx,cy (center), x1,y1 (a point on the MAJOR axis), x2,y2 (a point
                                  on the MINOR axis).
        entity_type='spline':     uses points — flat through-points [x1,y1,x2,y2,...] (>= 2 points),
                                  round-tripped exactly.
        entity_type='fillet':     uses vx,vy (vertex to round) and radius.
        entity_type='chamfer':    uses vx,vy (vertex to cut) and distance.
        direction: only for 'arc_center' — sweep sense from start to end. OMIT IT (or pass 0) for the
                   MINOR (<=180°) arc, which a corner round virtually always is; a wrong explicit sign
                   SILENTLY draws the >180° complement. Pass +1 (CCW) or -1 (CW) only for a deliberate
                   major sweep (a round-trip replays analyze's 'dir' verbatim) or a HALF circle (a slot
                   end): one 180° arc with direction, never two quarter arcs — their joint slides along the
                   circle and the sketch can never be fully defined.
        construction: True makes the entity CONSTRUCTION/reference geometry — it guides the profile but
                   adds no edge. Mirrors analyze_model's per-segment 'construction' flag.

        This tool MIRRORS analyze_model 1:1 — every segment it emits (line, arc/circle, ellipse, spline,
        with cx/cy/x1/y1/x2/y2/radius/points/construction, and 'dir' on partial arcs) maps onto an
        entity_type here, so an analyzed sketch rebuilds without dropping or simplifying a curve.
        The answer carries what SolidWorks actually created, READ BACK
        rather than echoed — check the radius/endpoints against your intent before moving on.
        Shared endpoint coordinates close the profile — no coincident constraints by hand. The answer's
        sketch_status says whether the sketch is fully defined (black) or under-defined (blue): define it with
        define_sketch once the shape is drawn, before the feature. All coordinates in mm."""
        return call(
            "add_sketch_entity",
            {
                "entity_type": entity_type,
                "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                "xm": xm, "ym": ym,
                "cx": cx, "cy": cy, "radius": radius,
                "vx": vx, "vy": vy,
                "distance": distance,
                "direction": direction,
                "points": points,
                "construction": construction,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: add_sketch_entities (batch)
    # ---------------------------------------------------------------------------
    @tool
    def add_sketch_entities(segments: list[dict]) -> str:
        """Add MANY sketch entities to the active sketch in ONE call (the batch form of
        add_sketch_entity — prefer this whenever a profile has more than ~3 segments).
        segments: a list of entity records, each record = add_sketch_entity's parameters for one entity
        (mm), e.g.
          [{"entity_type":"line","x1":0,"y1":0,"x2":50,"y2":0},
           {"entity_type":"arc_center","cx":50,"cy":5,"x1":50,"y1":0,"x2":55,"y2":5},
           {"entity_type":"circle","cx":20,"cy":20,"radius":4,"construction":true}]
        Corner rounds / fillet arcs: use arc_center WITHOUT "direction" — the tool then draws the
        MINOR (<=180°) arc deterministically (an explicit wrong sign silently draws the 270°
        complement); pass direction ±1 only for a deliberate major arc or a half circle (a slot end:
        ONE arc, not two quarter arcs).
        Supported entity_type values and their parameters are IDENTICAL to add_sketch_entity
        (rectangle/circle/line/arc/arc_center/ellipse/spline/fillet/chamfer; per-record
        'construction': true supported). Segments are created in list order —
        list a profile's segments in contour order with EXACT shared endpoints (no coincident
        constraints by hand — define_sketch adds relations and dimensions once the shape is drawn).
        NOT transactional: on the first failing record the call fails naming segments[i]
        and how many earlier records were created — those remain in the sketch.
        The answer echoes {segment_count, counts per entity_type} (compact —
        NO per-segment geometry echo; read back with analyze_model(sketch, name=...) if needed).
        Drawn exactly: SolidWorks' snapping and automatic relations are off while the records go in, and a record
        SolidWorks silently did not add (an arc onto existing geometry, a zero-length line) is an error naming it —
        arc_center first retries as the same arc through 3 points.
        All coordinates in mm."""
        return call("add_sketch_entities", {"segments": segments})

    @tool
    def sketch_modify(sketch_name: str, delete_all: bool = False, delete_near: list[list[float]] = [],
                      move_points: list[list[float]] = [], add_segments: list[dict] = [],
                      keep_construction: bool = True) -> str:
        """Change an EXISTING sketch in one call, then rebuild — instead of piling new features over a profile.
        In order: delete_all=True removes every segment (construction lines stay unless keep_construction=False) —
        a REPLACE when add_segments follows; delete_near = [[x, y], …] removes the segment passing each point;
        move_points = [[from_x, from_y, to_x, to_y], …] moves sketch points (a point held by a dimension or relation
        stays — reported in not_moved); add_segments = records exactly like add_sketch_entities. All in mm, SKETCH
        coordinates (analyze_model(sketch, name) shows them). Answers deleted / moved / added, sketch_status and
        features_with_errors (features that fail after the change — fix or delete them)."""
        return call("sketch_modify", {"sketch_name": sketch_name, "delete_all": delete_all or None,
                                      "delete_near": delete_near, "move_points": move_points,
                                      "add_segments": add_segments,
                                      "keep_construction": None if keep_construction else False})

    # ---------------------------------------------------------------------------
    # Tool: add_sketch_constraint
    # ---------------------------------------------------------------------------
    @tool
    def add_sketch_constraint(
        constraint_type: Literal[
            "horizontal", "vertical", "coincident", "parallel",
            "perpendicular", "tangent", "equal", "midpoint",
        ],
        px1: float,
        py1: float,
        px2: float = 0.0,
        py2: float = 0.0,
        entity_type1: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
        entity_type2: Literal["SKETCHSEGMENT", "SKETCHPOINT", "ORIGIN"] = "SKETCHSEGMENT",
    ) -> str:
        """Apply a geometric constraint to sketch entities in the active sketch.
        constraint_type: 'horizontal', 'vertical' — single entity (px1, py1 only).
        constraint_type: 'coincident', 'parallel', 'perpendicular', 'tangent', 'equal', 'midpoint' — requires two entities (px2, py2).
        px1/py1: point on the first entity. px2/py2: point on the second entity (two-entity constraints).
        entity_type1/2: 'SKETCHSEGMENT' (default) or 'SKETCHPOINT' for point-based constraints like coincident/midpoint.
        entity_type2='ORIGIN': the second entity is the sketch origin (px2/py2 ignored) — e.g.
            coincident(corner SKETCHPOINT, ORIGIN) to fix a profile in place.
        All coordinates in mm, in the SKETCH frame (see add_dimension)."""
        return call(
            "add_sketch_constraint",
            {
                "constraint_type": constraint_type,
                "px1": px1, "py1": py1,
                "px2": px2, "py2": py2,
                "entity_type1": entity_type1,
                "entity_type2": entity_type2,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: add_dimension
    # ---------------------------------------------------------------------------
    @tool
    def add_dimension(
        px: float,
        py: float,
        value: Annotated[float, Field(gt=0, description="Dimension value in mm")],
        label_offset_x: float = 0.0,
        label_offset_y: float = -15.0,
        sketch_name: str = "",
        entity_type: Literal["segment", "point"] = "segment",
        to_origin: bool = False,
        orientation: Literal["auto", "horizontal", "vertical"] = "auto",
    ) -> str:
        """Add a driving dimension in a sketch and set it to `value`.

        SKETCH FRAME — px/py (and label_offset_x/y) are 2D coordinates in mm in the sketch's own frame: the
        frame add_sketch_entity draws in and analyze_model(analysis_type='sketch') reports segments in
        (origin + xdir/ydir = the `frame` create_sketch echoes / analyze_model(sketch).plane.frame).
        Pass analyze_model(sketch)'s numbers straight through — e.g. a circle {cx, cy, radius} is
        dimensioned with px=cx+radius, py=cy. Never pre-convert to model XYZ.
        Selection: the sketch entity nearest (px, py) — entity_type='segment' (default: a line ->
            its length, a circle -> its diameter) or 'point' (e.g. a circle's center at (cx, cy)).
        to_origin=True: dimension the picked entity to the SKETCH ORIGIN (a position dimension);
            orientation 'horizontal' / 'vertical' measures along the sketch x / y axis
            (orientation requires to_origin; 'auto' lets SolidWorks choose from the label position).
        sketch_name: dimension an EXISTING sketch — it is opened, dimensioned, closed and the model
            rebuilt. Blank = the active sketch (normal build flow; it stays open).
        Answers {dimension ('D1@Sketch3' — pass it to modify_dimension), sketch, type, requested, effective}."""
        return call("add_dimension", {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
                                      "label_offset_y": label_offset_y, "sketch_name": sketch_name,
                                      "entity_type": entity_type, "to_origin": to_origin, "orientation": orientation})

    @tool
    def define_sketch(sketch_name: str = "", dimensions: list[dict] = [], auto: bool = True,
                      scheme: Literal["baseline", "chain", "ordinate"] = "baseline") -> str:
        """Make a sketch FULLY DEFINED (black in SolidWorks) WITHOUT changing its shape, so its sizes can be edited
        (modify_dimension, a double-click in SolidWorks) and it rebuilds predictably. Three steps:
        1. relations the geometry already satisfies exactly — shared end points coincident, lines along an axis
           horizontal / vertical, a point at the origin on it;
        2. YOUR design dimensions at their current values — dimensions: [{px, py, entity_type?: 'segment'|'point',
           to_origin?, orientation?: 'auto'|'horizontal'|'vertical'}], picked like add_dimension (sketch frame, mm):
           the sizes a designer puts on the drawing (overall width and height, a hole's diameter and its position from
           an edge or the origin, a slot width);
        3. auto=True: SolidWorks' Fully Define Sketch adds what is still missing (dimensions from the sketch origin,
           scheme baseline | chain | ordinate).
        A step that moves the geometry, over-defines the sketch or repeats a size is undone and listed in `skipped`.
        Call it once per sketch, after the shape is drawn and before the feature; on the open sketch, or sketch_name
        (opened and closed again). It never changes a size — add_dimension(value) / modify_dimension do. Answers
        status, free_segments (what is still blue), relations_added, dimensions_added, design_dimensions,
        max_shift_mm, skipped. A profile takes 1-6 s; a sketch of ~100 segments a minute or more (auto)."""
        return call_mm("define_sketch", dict(sketch_name=sketch_name, dimensions=dimensions, auto=auto, scheme=scheme))

    # ---------------------------------------------------------------------------
    # Tool: edit_sketch
    # ---------------------------------------------------------------------------
    @tool
    def edit_sketch(sketch_name: str) -> str:
        """Reopen an existing sketch for editing. Counterpart to create_sketch.
        sketch_name: exact name of the sketch feature in the feature tree (e.g. 'Sketch1').
        Answers with sketch = sketch_name.
        After editing, call extrude_feature (or any feature) to exit the sketch."""
        return call("edit_sketch", {"sketch_name": sketch_name})

    # ---------------------------------------------------------------------------
    # Tool: modify_dimension
    # ---------------------------------------------------------------------------
    @tool
    def modify_dimension(
        name: str,
        value: float,
    ) -> str:
        """Change a named display dimension's value — the UNIVERSAL parametric edit (variant keystone).
        name: the dimension full-name exactly as analyze_model(features) reports it, e.g.
              'D1@Boss-Extrude1@Part.Part' or 'D1@Sketch1'.
        value: the new value — mm for a length/distance, DEGREES for an angle, a plain number for a pattern
              count (the dimension's own kind decides; analyze_model reports it as value_mm, value_deg or
              value with count=true).
        Workflow: analyze_model(analysis_type='features') → read a feature's dimension name + value →
              modify_dimension(name, new_value) → analyze again to confirm. Bumps state_version (geometry
              changes, unlike analyze). The answer carries
              {dimension, requested, effective}: the value read back from SolidWorks AFTER rebuild — verify
              it matches your intent (an equation/relation may have driven it elsewhere). This is how you
              build variants (e.g. resize a boss, widen a slot, change a gear's tooth-spacing)."""
        return call("modify_dimension", {"name": name, "value": value, "units": "mm"})

    # ------------------------------------------------------------------ sketch text (mm)
    @tool
    def add_sketch_text(text: str, x_mm: float = 0.0, y_mm: float = 0.0, height_mm: float = 5.0, font: str = "",
                        bold: bool = False, angle_deg: float = 0.0) -> str:
        """Text in the ACTIVE sketch (sketch coordinates, mm): part marking / engraving. Then cut it
        (extrude_feature cut, small depth) or keep the sketch and export it with the flat pattern
        (export_flat_dxf sketches=True → layer MARK for the laser)."""
        return call_mm("add_sketch_text", dict(text=text, x_mm=x_mm, y_mm=y_mm, height_mm=height_mm, font=font,
                     bold=bold or None, angle_deg=angle_deg or None))
