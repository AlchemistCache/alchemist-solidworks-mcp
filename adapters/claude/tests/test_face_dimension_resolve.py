"""face_dimension.resolve pinned whole — every status, with its fields in the order the model reads them — on the
synthetic block + hole of test_face_dimension.py."""
import copy
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.dirname(_HERE), _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import face_dimension as fd  # noqa: E402
from test_face_dimension import FACES, FEATURE_MAP, RECIPE, SKETCHES, line, plane  # noqa: E402

BOSS = {"face_kind": "plane", "feature": "Boss-Extrude1", "sketch": "Sketch1"}
WALL0 = {"kind": "line", "construction": False, "x1": 0, "y1": 0, "x2": 0.05, "y2": 0, "index": 0}
D1 = {"name": "D1@Sketch1@Part1.Part", "type": "linear", "segments": [0]}


def run(i=None, face=None, fmap=FEATURE_MAP, recipe=RECIPE, sketches=SKETCHES, faces=FACES):
    return fd.resolve(FACES[i] if face is None else face, fmap, recipe, sketches.__getitem__, faces)


def pin(result, expected):
    assert result == expected
    assert list(result) == list(expected)


def test_cap_and_sketch_plane():
    pin(run(0), {"face_index": 0, **BOSS, "role": "extrude_cap", "status": "dimensioned",
                 "dimension": {"name": "D1@Boss-Extrude1@Part1.Part", "value_si": 0.01, "type": "depth"}})
    pin(run(1), {"face_index": 1, **BOSS, "status": "not_dimension_driven", "role": "sketch_plane",
                 "reason": "the face lies in the sketch plane — positioned by the plane"})


def test_cap_without_one_depth():
    rec = copy.deepcopy(RECIPE)
    rec["features"][0]["dimensions"] = [{"name": "D1"}, {"name": "D2"}]
    pin(run(0, recipe=rec), {"face_index": 0, **BOSS, "role": "extrude_cap", "status": "multiple_dimensions",
                             "candidates": [{"name": "D1"}, {"name": "D2"}],
                             "reason": "Boss-Extrude1 carries 2 dimensions"})
    rec["features"][0]["dimensions"] = []
    pin(run(0, recipe=rec), {"face_index": 0, **BOSS, "role": "extrude_cap", "status": "undimensioned",
                             "candidates": [], "reason": "Boss-Extrude1 carries 0 dimensions"})


def test_walls():
    pin(run(2), {"face_index": 2, **BOSS, "role": "side_wall", "segment": WALL0, "status": "dimensioned", "dimension": D1})
    pin(run(4), {"face_index": 4, **BOSS, "role": "side_wall",
                 "segment": {"kind": "line", "construction": False, "x1": 0, "y1": 0.03, "x2": 0, "y2": 0, "index": 3},
                 "status": "undimensioned",
                 "add_dimension": {"px": 0.0, "py": 0.015, "value": 0.03, "label_offset_x": 0.0, "label_offset_y": -0.008},
                 "reason": "no driving dimension is attached to segment 3 of Sketch1"})


def test_hole_with_position():
    pin(run(5), {"face_index": 5, "face_kind": "cylinder", "feature": "Cut-Extrude1", "sketch": "Sketch2",
                 "role": "cylinder_wall",
                 "segment": {"kind": "arc/circle", "construction": False, "cx": 0.025, "cy": 0.015, "radius": 0.004,
                             "index": 0},
                 "position_dimensions": [{"name": "D2@Sketch2@Part1.Part", "type": "linear",
                                          "points": [{"center_of": 0}, {}]}],
                 "status": "dimensioned", "dimension": {"name": "D1@Sketch2@Part1.Part", "type": "diameter", "segments": [0]}})


def test_feature_without_sketch_or_frame():
    pin(run(0, recipe={"features": []}), {"face_index": 0, "face_kind": "plane", "feature": "Boss-Extrude1",
                                          "status": "unattributed", "reason": "Boss-Extrude1 is not in the recipe"})
    rec = copy.deepcopy(RECIPE)
    rec["features"][0].pop("profile_sketch")
    pin(run(0, recipe=rec), {"face_index": 0, "face_kind": "plane", "feature": "Boss-Extrude1",
                             "status": "no_profile_sketch",
                             "feature_dimensions": [{"name": "D1@Boss-Extrude1@Part1.Part", "value_si": 0.01}],
                             "reason": "Boss-Extrude1 (Extrusion) has no profile sketch"})
    sk = copy.deepcopy(SKETCHES)
    sk["Sketch1"]["plane"] = {}
    pin(run(2, sketches=sk), {"face_index": 2, **BOSS, "status": "unsupported",
                              "reason": "Sketch1 reported no sketch frame"})


def test_orientations_it_does_not_handle():
    tilted = plane(7, [0, 0.6, 0.8], [0.025, 0.015, 0.01])
    fmap = {"map": [{"feature": "Boss-Extrude1", "created_faces": [dict(tilted)]}]}
    pin(run(face=tilted, fmap=fmap, faces=[tilted]),
        {"face_index": 7, **BOSS, "status": "unsupported",
         "reason": "face is neither parallel nor perpendicular to the sketch plane"})
    cyl = {"i": 8, "planar": False, "kind": "cylinder", "axis": [1, 0, 0], "origin": [0.0, 0.015, 0.005],
           "radius": 0.004, "point": [0.01, 0.019, 0.005]}
    fmap = {"map": [{"feature": "Cut-Extrude1", "created_faces": [{"point": [0.01, 0.019, 0.005], "planar": False}]}]}
    pin(run(face=cyl, fmap=fmap, faces=[cyl]),
        {"face_index": 8, "face_kind": "cylinder", "feature": "Cut-Extrude1", "sketch": "Sketch2",
         "status": "unsupported", "reason": "cylinder axis is not normal to the sketch"})


def test_segment_must_be_exactly_one():
    sk = copy.deepcopy(SKETCHES)
    sk["Sketch1"]["sketch"]["segments"].append(line(0.05, 0.01, 0.05, 0.02))
    pin(run(3, sketches=sk), {"face_index": 3, **BOSS, "role": "side_wall", "status": "ambiguous_segment",
                              "candidates": [1, 4], "reason": "2 segments of Sketch1 sweep into this face"})
    sk = copy.deepcopy(SKETCHES)
    sk["Sketch1"]["sketch"]["segments"][1]["construction"] = True
    pin(run(3, sketches=sk), {"face_index": 3, **BOSS, "role": "side_wall", "status": "no_segment",
                              "candidates": [], "reason": "0 segments of Sketch1 sweep into this face"})


def test_driving_and_other_dimensions():
    angular = {"name": "D4@Sketch1", "type": "angular", "segments": [0, 1]}
    sk = copy.deepcopy(SKETCHES)
    sk["Sketch1"]["dimensions"] += [{"name": "D3@Sketch1", "type": "horizontal", "segments": [0]}, angular]
    pin(run(2, sketches=sk), {"face_index": 2, **BOSS, "role": "side_wall", "segment": WALL0,
                              "other_dimensions": [angular], "status": "multiple_dimensions",
                              "candidates": [D1, {"name": "D3@Sketch1", "type": "horizontal", "segments": [0]}],
                              "reason": "2 dimensions are attached to segment 0"})
    sk["Sketch1"]["dimensions"] = [D1, angular]
    pin(run(2, sketches=sk), {"face_index": 2, **BOSS, "role": "side_wall", "segment": WALL0,
                              "other_dimensions": [angular], "status": "dimensioned", "dimension": D1})
