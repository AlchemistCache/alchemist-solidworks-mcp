"""Pictures of the model: one view or a set of views, returned as images (sections in mm).
Registered by registry.register_all(); call plumbing in bridge.py."""
import os
import re
from typing import Literal

from mcp.server.mcpserver import Image

import session
from bridge import dump, fail, call_mm, call_mm_json, with_image
from jobs import background


def register(mcp, tool):
    @tool
    def view_image(view: str = "iso", view_dir: list[float] = [], up: list[float] = [],
                   display: Literal["shaded_edges", "shaded", "hlr", "hlv", "wireframe"] = "shaded_edges",
                   size: int = 640, focus: list[str] = [], focus_box_mm: list[float] = [], isolate: bool = False,
                   hide_components: list[str] = [], section_plane: str = "", section_offset_mm: float = 0.0,
                   section_reverse: bool = False, crop: bool = True, triad: bool = True, render: bool = False,
                   file_path: str = ""):
        """SEE the model: PNG of the active document returned as an image. view: front/back/left/right/top/
        bottom/iso/trimetric/dimetric or '*<named view>'; ANY direction: view_dir = [x,y,z] from the model toward you
        ([1,1,1] = iso, [-1,1,-1] = iso from behind-left, [0,-1,0] = from below), up = the screen's up (default +Y).
        CLOSE-UP of a unit: focus = components to zoom to (isolate=True hides everything else), or focus_box_mm =
        [x1,y1,z1,x2,y2,z2] in model / assembly space — far better than a bigger picture of the whole machine (a unit
        covered by others from that side: isolate=True, or a section). size =
        the LONG side in px (≤2000; image tokens grow with the area); long narrow parts get it too. display
        shaded_edges (default; edges keep light parts visible), shaded, hlr, hlv, wireframe. Optional section through
        a plane (+offset), hidden components. The answer's view_axes say which model axis points right / up / toward
        you; triad draws them in a corner. render=True: a presentation picture — perspective, shadows, ambient
        occlusion, full-scene anti-aliasing, no edge lines (slower; for showing a design, not for checking it).
        Auto-cropped; saved to file_path or a temp file."""
        res = call_mm("view_image", dict(view=view, view_dir=view_dir, up=up, display=display, size=size, focus=focus,
                                      focus_box_mm=focus_box_mm, isolate=isolate, hide_components=hide_components,
                                      section_plane=section_plane, section_offset_mm=section_offset_mm,
                                      section_reverse=section_reverse, crop=crop, triad=triad, render=render or None,
                                      file_path=file_path))
        return with_image(res)

    view_keys = {"view", "view_dir", "up", "display", "size", "focus", "focus_box_mm", "isolate", "hide_components",
                 "section_plane", "section_offset_mm", "section_reverse", "crop", "triad", "render", "file_path"}

    @tool
    def view_images(views: list[str | dict] = ["front", "top", "right", "iso"],
                    display: Literal["shaded_edges", "shaded", "hlr", "hlv", "wireframe"] = "shaded_edges",
                    size: int = 400, focus: list[str] = [], isolate: bool = False, out_dir: str = ""):
        """SEE the model from several sides in one call (default front/top/right/iso, like a drawing's view set):
        one image per view, returned together (up to 8). A view is a name ('top', '*Named') or an object with
        view_image's own parameters — {"view_dir": [-1,1,-1], "focus": [...]}, {"view": "front", "section_plane":
        "Right Plane", "file_path": "..."}. display / size / focus / isolate here are the defaults for every view.
        out_dir keeps the PNGs (default: temp)."""
        items = [v if isinstance(v, dict) else {"view": v} for v in views[:8]]
        for i, it in enumerate(items):
            extra = set(it) - view_keys
            if extra:
                raise fail("BAD_PARAMETER", f"views[{i}] has no parameter {', '.join(sorted(extra))}; "
                                            f"a view takes: {', '.join(sorted(view_keys))}")

        def run():
            images, rows = [], []
            for i, it in enumerate(items):
                args = {"display": display, "size": size, "focus": focus, "isolate": isolate, **it}
                tag = "view_dir" if "view_dir" in it else it.get("view", "iso")
                if out_dir and not it.get("file_path"):
                    args["file_path"] = os.path.join(out_dir, f"view_{i}_{re.sub(r'[^0-9A-Za-z_-]+', '_', str(tag))}.png")
                for k in ("view_dir", "up", "focus_box_mm"):
                    args.setdefault(k, [])
                info = call_mm_json("view_image", args)
                rows.append({"view": tag, "image": info.get("image"), "view_axes": info.get("view_axes"),
                             **({"warning": info["warning"]} if info.get("warning") else {})})
                if info.get("image"):
                    images.append(Image(path=info["image"]))
            return [dump({"views": rows})] + images
        if out_dir:
            os.makedirs(session.full_path("out_dir", out_dir), exist_ok=True)
        return background("view_images", run)
