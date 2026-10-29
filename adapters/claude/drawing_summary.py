"""Text the model reads after analyze_drawing: the advisories and the compact wire JSON.
Pure Python (no MCP, no SolidWorks)."""
import json



# ---------------------------------------------------------------------------
# ADVISORIES — the third home (payload trim phase 5, 2026-09-07)
# ---------------------------------------------------------------------------
# Three channels carry what the model needs, and each costs differently: a tool docstring is paid
# EVERY session whatever the job, a tool_help guide once per job that fetches it, and a payload note
# only when the thing it warns about actually happens. So guidance that applies to ONE flag belongs
# on that flag: `reverse` used to spend a paragraph on "if you see printed_mismatch, do X" in every
# read, whether or not any dimension carried it.
#
# Computed HERE, in the adapter, deliberately NOT in the reader: the saved artifact stays pure DATA
# (the research pool is not polluted, ANALYSIS_VERSION does not move, the draw-dialect contract and
# gate 4 are untouched). Only the ANSWER gains a short advisory block.
#
# A closed set, one line each, emitted ONCE per condition with the places it fired.
def advisories(art) -> str:
    """A short plain-text block naming the conditions this read actually raised. '' when none."""
    out = []
    views = art.get("views") or []

    scaled = [v["vid"] for v in views if v.get("scale_factor") is not None]
    if scaled:
        out.append("view scale_factor @ %s — this view is drawn at a DIFFERENT ratio from the "
                   "sheet; say which factor produced a number before you quote it."
                   % ", ".join(scaled))

    not_read = (art.get("sheet") or {}).get("not_read") or {}
    if not_read:
        out.append("not_read @ sheet (%s) — the reader saw these and did not emit them; a named "
                   "block you cannot account for is evidence that never reached you."
                   % ", ".join(sorted(not_read)))

    ang, lin = [], []
    for k, d in enumerate(art.get("dimensions") or []):
        if d.get("printed_mismatch"):
            (ang if d.get("kind") == "angular" else lin).append("dim[%d]" % k)
    if ang:
        out.append("printed_mismatch/ANGULAR @ %s — the value is a JUDGEMENT, not a reading; "
                   "corroborate it against the geometry or an ellipse ratio before building."
                   % ", ".join(ang))
    if lin:
        out.append("printed_mismatch @ %s — the scale, the arc match or the per-view factor is "
                   "wrong. Reading the flag is free; ignoring it is not." % ", ".join(lin))

    tier_b = ["%s/%s" % (v["vid"], lp.get("id"))
              for v in views for lp in (v.get("loops") or []) if lp.get("tier") == "B"]
    if tier_b:
        out.append("tier:B @ %s — this boundary was WALKED, not chained; check its area against a "
                   "stated length or width before you build on it." % ", ".join(tier_b))

    unpaired = [b for b in (art.get("bend_notes") or []) if b.get("unpaired")]
    if unpaired:
        out.append("unpaired bend x%d — the reader refused to guess, so those bends are NOT built. "
                   "Resolve them from the drawing or declare a gap; never split the difference."
                   % len(unpaired))
    in_loop = [b for b in (art.get("bend_notes") or [])
               if (b.get("bend_line") or {}).get("in_loop")]
    if in_loop:
        out.append("bend_line in_loop x%d — the match landed on OUTLINE geometry, and a bend line "
                   "belongs to no closed contour. Say so." % len(in_loop))

    vg = art.get("view_graph")
    if isinstance(vg, dict) and vg.get("solved") is False:
        out.append("view_graph.solved:false (%s) — the axes did not join; that joining is yours."
                   % vg.get("reason", "reason in the field"))

    if not out:
        return ""
    return "\nADVISORIES (%d) — only what actually fired in THIS read:\n  %s" % (
        len(out), "\n  ".join(out))


def wire_json(obj) -> str:
    """The drawing payload, serialized as tightly as JSON allows.

    `json.dumps` defaults to ', ' and ': ' — two bytes of whitespace per comma and colon, which on
    an artifact made mostly of short numeric arrays is 18,854 B across the 10 samples (11 points of
    payload, measured). Nothing is lost: JSON whitespace is not data.
    """
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
