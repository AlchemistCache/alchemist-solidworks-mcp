"""face_dimension.resolve on a synthetic part: a 50 × 30 × 10 mm block extruded from a Front-plane rectangle, with a
Ø8 mm through hole cut from a circle — the payload shapes analyze_model returns (faces, feature_map, features, sketch)."""
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import face_dimension as fd  # noqa: E402

FRONT = {"frame": {"origin": [0, 0, 0], "xdir": [1, 0, 0], "ydir": [0, 1, 0]}, "ref": "Front Plane"}


def plane(i, normal, point):
    return {"i": i, "planar": True, "normal": normal, "point": point}


FACES = [
    plane(0, [0, 0, 1], [0.025, 0.015, 0.01]),       # the extrude cap
    plane(1, [0, 0, -1], [0.025, 0.015, 0.0]),       # on the sketch plane
    plane(2, [0, -1, 0], [0.025, 0.0, 0.005]),       # wall from line 0 (y = 0)
    plane(3, [1, 0, 0], [0.05, 0.015, 0.005]),       # wall from line 1 (x = 50)
    plane(4, [-1, 0, 0], [0.0, 0.015, 0.005]),       # wall from line 3 (x = 0)
    {"i": 5, "planar": False, "kind": "cylinder", "axis": [0, 0, 1], "origin": [0.025, 0.015, 0.0], "radius": 0.004,
     "point": [0.029, 0.015, 0.005]},                # the hole
]
FEATURE_MAP = {"map": [
    {"feature": "Boss-Extrude1", "created_faces": [dict(f, planar=True) for f in FACES[:5]]},
    {"feature": "Cut-Extrude1", "created_faces": [{"point": [0.029, 0.015, 0.005], "planar": False}]},
]}
RECIPE = {"features": [
    {"name": "Boss-Extrude1", "type": "Extrusion", "profile_sketch": "Sketch1",
     "dimensions": [{"name": "D1@Boss-Extrude1@Part1.Part", "value_si": 0.01}]},
    {"name": "Cut-Extrude1", "type": "ICE", "profile_sketch": "Sketch2", "dimensions": []},
]}


def line(x1, y1, x2, y2):
    return {"kind": "line", "construction": False, "x1": x1, "y1": y1, "x2": x2, "y2": y2}


SKETCHES = {
    "Sketch1": {"plane": FRONT, "sketch": {"segments": [line(0, 0, 0.05, 0), line(0.05, 0, 0.05, 0.03),
                                                         line(0.05, 0.03, 0, 0.03), line(0, 0.03, 0, 0)]},
                "dimensions": [
                    {"name": "D1@Sketch1@Part1.Part", "type": "linear", "segments": [0]},
                    # a line LENGTH is attached to the line's two end points, not to the line
                    {"name": "D2@Sketch1@Part1.Part", "type": "linear",
                     "points": [{"endpoint_of": [0, 1]}, {"endpoint_of": [1, 2]}]},
                ]},
    "Sketch2": {"plane": FRONT, "sketch": {"segments": [
        {"kind": "arc/circle", "construction": False, "cx": 0.025, "cy": 0.015, "radius": 0.004}]},
                "dimensions": [
                    {"name": "D1@Sketch2@Part1.Part", "type": "diameter", "segments": [0]},
                    {"name": "D2@Sketch2@Part1.Part", "type": "linear", "points": [{"center_of": 0}, {}]},
                ]},
}


def resolve(i, feature_map=FEATURE_MAP, faces=FACES):
    return fd.resolve(FACES[i], feature_map, RECIPE, SKETCHES.__getitem__, faces)


def test_cap_face_is_the_extrude_depth():
    r = resolve(0)
    assert (r["status"], r["role"], r["dimension"]["type"]) == ("dimensioned", "extrude_cap", "depth")
    assert r["dimension"]["name"].startswith("D1@Boss-Extrude1")


def test_face_in_the_sketch_plane_is_not_dimension_driven():
    assert resolve(1)["status"] == "not_dimension_driven"


def test_side_walls_by_segment_and_by_endpoints():
    r = resolve(2)
    assert (r["status"], r["role"], r["segment"]["index"]) == ("dimensioned", "side_wall", 0)
    assert r["dimension"]["name"].startswith("D1@Sketch1")
    r = resolve(3)
    assert r["status"] == "dimensioned" and r["dimension"]["name"].startswith("D2@Sketch1")   # via the end points


def test_undimensioned_wall_offers_the_dimension_at_its_current_size():
    r = resolve(4)
    assert r["status"] == "undimensioned" and r["segment"]["index"] == 3
    assert abs(r["add_dimension"]["value"] - 0.03) < 1e-12


def test_hole_is_the_circle_diameter_with_its_position():
    r = resolve(5)
    assert (r["status"], r["role"], r["feature"]) == ("dimensioned", "cylinder_wall", "Cut-Extrude1")
    assert r["dimension"]["type"] == "diameter"
    assert [d["name"][:2] for d in r["position_dimensions"]] == ["D2"]


def test_nothing_is_guessed():
    assert resolve(0, feature_map={"map": []})["status"] == "unattributed"
    both = {"map": FEATURE_MAP["map"] + [{"feature": "Boss-Extrude2", "created_faces": [dict(FACES[0])]}]}
    r = resolve(0, feature_map=both)
    assert r["status"] == "ambiguous_feature" and set(r["candidates"]) == {"Boss-Extrude1", "Boss-Extrude2"}
    assert fd.resolve({"i": 9, "planar": False}, FEATURE_MAP, RECIPE, SKETCHES.__getitem__)["status"] == "unsupported"


def test_coplanar_faces_are_attributed_by_position():
    """Two pads with coplanar tops: each top belongs to the feature that created a face AT its point."""
    top_a, top_b = plane(0, [0, 0, 1], [0.01, 0.01, 0.01]), plane(1, [0, 0, 1], [0.04, 0.01, 0.01])
    fmap = {"map": [{"feature": "PadA", "created_faces": [dict(top_a)]}, {"feature": "PadB", "created_faces": [dict(top_b)]}]}
    assert fd.creating_features(top_b, fmap, [top_a, top_b]) == ["PadB"]
