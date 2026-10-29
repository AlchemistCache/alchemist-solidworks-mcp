"""analyze_model's answers as data: the payload items, the JSON object of a JSON mode, key=value lists as numbers.
Pure Python (no MCP, no SolidWorks)."""
import json

from response_mapper import fail, failure_of



def analysis_items(response: dict) -> list:
    """Return the `cadState.features` list an analyze_model call carries its payload in.

    Payload shapes (C#-owned): the JSON modes (features, faces, edges, feature_map, sketch, analyze_assembly) = ONE
    item holding a JSON object; 'mass_properties'/'geometry' modes = 'key=value' strings. A FAILED response raises
    (map_response's error discipline)."""
    if response.get("status") != "COMPLETED":
        raise failure_of(response)
    return (response.get("cadState") or {}).get("features") or []


def analysis_payload(response: dict) -> dict:
    """The JSON object of a JSON analysis mode (see analysis_items); no or unreadable payload raises."""
    items = analysis_items(response)
    if not items:
        raise fail("NO_PAYLOAD", "the analysis returned no payload")
    try:
        return json.loads(items[0])
    except (ValueError, TypeError) as ex:
        raise fail("PAYLOAD_UNPARSEABLE", str(ex)) from ex


def kv_dict(items: list) -> dict:
    """Parse ['volume=4,22503E-06', 'faces=12', ...] into a dict with real numbers.

    Numeric values may use a COMMA decimal separator (localized SolidWorks formatting);
    normalize to float, and to int for plain integer counts."""
    out = {}
    for item in items:
        if not isinstance(item, str) or "=" not in item:
            continue
        key, _, raw = item.partition("=")
        raw = raw.strip()
        try:
            num = float(raw.replace(",", "."))
            is_plain_int = raw.isdigit() or (raw.startswith("-") and raw[1:].isdigit())
            out[key.strip()] = int(num) if is_plain_int else num
        except ValueError:
            out[key.strip()] = raw
    return out

