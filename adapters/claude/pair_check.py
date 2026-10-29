"""Shaping interference / clearance pair lists: drop pairs inside one subassembly, merge pairs that differ only by
instance numbers, and a per-assembly baseline of intended pairs (only new ones are shown after it)."""
import hashlib
import json
import os
import re

BASELINES = os.path.join(os.path.expanduser("~"), ".swmcp", "baselines")
_INSTANCE = re.compile(r"-\d+$")


def internal(names: list) -> bool:
    """Both components inside the same top-level subassembly ('Frame-1/Bolt-3' and 'Frame-1/Plate-1')."""
    tops = [n.split("/", 1)[0] for n in names if "/" in n]
    return len(tops) == len(names) == 2 and tops[0] == tops[1]


def _kind(name: str) -> str:
    """'Frame-1/Bolt M6-12' → 'Frame/Bolt M6' (every path segment without its instance number)."""
    return "/".join(_INSTANCE.sub("", seg) for seg in name.split("/"))


def group(items: list, names_key: str, value_key: str) -> list:
    """Pairs of the same component kinds with the same value (rounded) become one row with count."""
    rows, index = [], {}
    for x in items:
        key = (tuple(sorted(_kind(n) for n in x[names_key])), round(float(x.get(value_key) or 0), 1))
        if key in index:
            row = index[key]
            row["count"] = row.get("count", 1) + 1
            if len(row.setdefault("also", [])) < 3:
                row["also"].append(x[names_key])
        else:
            row = dict(x)
            index[key] = row
            rows.append(row)
    return rows


def _path(asm_path: str, kind: str) -> str:
    tag = hashlib.sha1((asm_path or "unsaved").lower().encode("utf-8")).hexdigest()[:12]
    name = re.sub(r"[^0-9A-Za-zА-Яа-я_-]+", "_", os.path.splitext(os.path.basename(asm_path or "unsaved"))[0])[:40]
    return os.path.join(BASELINES, f"{kind}_{name}_{tag}.json")


def _key(x: dict) -> str:
    return "|".join(sorted(x.get("components") or x.get("pair") or []))


def save_baseline(asm_path: str, items: list, kind: str) -> dict:
    os.makedirs(BASELINES, exist_ok=True)
    path = _path(asm_path, kind)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"assembly": asm_path, "pairs": {_key(x): x.get("volume_mm3", x.get("gap_mm")) for x in items}},
                  fh, ensure_ascii=False, indent=0)
    return {"pairs": len(items), "file": path}


def new_only(asm_path: str, items: list, kind: str):
    """(the pairs not in the baseline or changed by more than 20 % / 1 mm³, how many known pairs were hidden)."""
    path = _path(asm_path, kind)
    if not os.path.exists(path):
        return items, f"no baseline yet for this assembly — run with baseline='save' first ({path})"
    with open(path, encoding="utf-8") as fh:
        known = json.load(fh).get("pairs") or {}
    fresh, hidden = [], 0
    for x in items:
        k, v = _key(x), float(x.get("volume_mm3", x.get("gap_mm")) or 0)
        old = known.get(k)
        if old is not None and abs(v - float(old)) <= max(1.0, 0.2 * abs(float(old))):
            hidden += 1
            continue
        if old is not None:
            x = dict(x, was=old)
        fresh.append(x)
    return fresh, hidden
