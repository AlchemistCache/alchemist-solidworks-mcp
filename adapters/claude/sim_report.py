"""Strength-calculation report as a self-contained HTML page (no MS Word — SolidWorks' own GenerateReport
automates Word, which hung on SW 2024 after the first report of a session).

build_html() takes what the tools already return: sim_status(details) → study setup, sim_results → numbers,
sim_image → PNG files next to the report. Russian, print-friendly."""
import datetime
import html
import os

CSS = """
body{font:14px/1.45 'Segoe UI',Arial,sans-serif;color:#1d232b;margin:24px auto;max-width:980px;padding:0 16px}
h1{font-size:22px;margin:0 0 4px} h2{font-size:17px;margin:26px 0 8px;border-bottom:1px solid #d6dbe1;padding-bottom:4px}
.meta{color:#5b6672;font-size:12.5px} table{border-collapse:collapse;width:100%;margin:6px 0}
th,td{border:1px solid #d6dbe1;padding:5px 8px;text-align:left;vertical-align:top} th{background:#f2f4f7;font-weight:600}
td.num{text-align:right;font-variant-numeric:tabular-nums} .verdict{padding:10px 14px;border-radius:6px;margin:10px 0;font-weight:600}
.ok{background:#e6f4ea;color:#14532d} .bad{background:#fde8e8;color:#7f1d1d} .warn{background:#fff7e0;color:#6b4e00}
figure{margin:10px 0;display:inline-block;width:48%;vertical-align:top} figure img{width:100%;border:1px solid #d6dbe1}
figcaption{font-size:12.5px;color:#5b6672} @media print{figure{width:48%} body{margin:0}}
"""

KIND_RU = {"restraint": "закрепление", "force": "сила", "pressure": "давление", "gravity": "сила тяжести",
           "connector": "соединитель", "remoteload": "удалённая нагрузка", "temperature": "температура",
           "centrifugal": "центробежная сила", "bearingloads": "подшипниковая нагрузка"}


def _e(v):
    return html.escape("" if v is None else str(v))


def _num(v, nd=3):
    if isinstance(v, (int, float)):
        return f"{v:,.{nd}f}".replace(",", " ").rstrip("0").rstrip(".") if isinstance(v, float) else str(v)
    return _e(v)


def _table(head, rows):
    out = ["<table><tr>" + "".join(f"<th>{_e(h)}</th>" for h in head) + "</tr>"]
    for r in rows:
        out.append("<tr>" + "".join(f"<td class='num'>{_num(c)}</td>" if isinstance(c, (int, float)) else f"<td>{_e(c)}</td>"
                                    for c in r) + "</tr>")
    return "".join(out) + "</table>"


CHECK_RU = {"weld_check": "угловой шов", "bolt_check": "болт", "beam_check": "балка"}
ARG_RU = {"force_n": "F, Н", "length_mm": "L, мм", "throat_mm": "a, мм", "leg_mm": "катет, мм", "fu_mpa": "fu, МПа",
          "beta_w": "βw", "sides": "швов", "diameter_mm": "d, мм", "shear_n": "срез, Н", "axial_n": "растяжение, Н",
          "grade": "класс", "shear_planes": "плоскостей среза", "plate_t_mm": "t листа, мм", "plate_fu_mpa": "fu листа, МПа",
          "e1_mm": "e1, мм", "section": "сечение", "span_mm": "вылет, мм", "load_n": "F, Н", "udl_n_per_mm": "q, Н/мм",
          "support": "опоры", "b_mm": "b, мм", "h_mm": "h, мм", "t_mm": "t, мм", "d_mm": "d, мм", "fy_mpa": "fy, МПа"}


def _fos(results: dict):
    fos = (results.get("factor_of_safety") or {}).get("min")
    return results.get("factor_of_safety_min") if fos is None else fos


def away_fos(results: dict):
    """FoS of the part away from fixtures and loads when the von Mises peak sits on one (sim_results max_on /
    max_away_from_boundary): the FoS was taken at the peak, so yield = FoS × peak."""
    vm = results.get("von_mises_mpa") or {}
    away, fos = vm.get("max_away_from_boundary") or {}, _fos(results)
    if not (vm.get("max_on") and away.get("max") and vm.get("max") and fos is not None):
        return None
    return fos * vm["max"] / away["max"]


def _governing(results: dict):
    vm = results.get("von_mises_mpa") or {}
    return (vm.get("max_away_from_boundary") or {}).get("max") or vm.get("max")


def mesh_spread(results: dict, compared: list):
    """(min, max) of the governing stress (away from fixtures and loads, else the max) over the report's study and the
    compared ones (other meshes of the same case) — or None when it moves less than 15 %."""
    vals = [v for v in [_governing(results)] + [_governing(c.get("results") or {}) for c in compared or []] if v]
    if len(vals) < 2 or max(vals) <= 1.15 * min(vals):
        return None
    return min(vals), max(vals)


def verdict(results: dict, required_fos: float, checks: list = None, compared: list = None):
    """(css class, text) from the FoS in sim_results — the FoS away from fixtures and loads when the peak sits on one —
    and the hand checks (a failed one makes it "bad"). When the governing stress changes by more than 15 % between the
    compared meshes it is a singularity of the model, not a stress of the part: the FoS is given as a range and the
    verdict rests on the hand checks ("warn" without them)."""
    fos, vm = _fos(results), results.get("von_mises_mpa") or {}
    yld = fos * vm["max"] if fos is not None and vm.get("max") else None   # the FoS was taken at the peak
    lead, spread = "", mesh_spread(results, compared)
    if away_fos(results) is not None:
        on, away = vm["max_on"], vm["max_away_from_boundary"]
        lead = (f"Пик {vm['max']:g} МПа — {'на закреплении' if on.get('kind') == 'fixture' else 'под нагрузкой'} "
                f"«{on.get('name')}» (его даёт идеализация, не деталь). ")
        if not spread:
            lead += f"Вдали от закреплений и нагрузок {away['max']:g} МПа. "
        fos = away_fos(results)
    if spread and yld:
        cls, text = "warn", (f"Наибольшее напряжение {'вдали от закреплений и нагрузок ' if lead else ''}зависит от сетки: "
                             f"{spread[0]:g}–{spread[1]:g} МПа (запас {yld / spread[1]:.2f}–{yld / spread[0]:.2f}) — оно сидит "
                             "в острых углах соединений: это особенность модели, а не напряжение в детали; соединения "
                             "оцениваются ручными проверками.")
    elif fos is None:
        cls, text = "warn", "Запас прочности не определён — задайте предел текучести (yield_mpa) или материал."
    elif fos >= required_fos:
        cls, text = "ok", f"Запас прочности {fos:.2f} ≥ требуемого {required_fos:g} — прочность обеспечена."
    elif fos >= 1:
        cls, text = "warn", f"Запас прочности {fos:.2f} < требуемого {required_fos:g}, но > 1 — текучести нет, запас недостаточен."
    else:
        cls, text = "bad", f"Запас прочности {fos:.2f} < 1 — напряжения выше предела текучести."
    done = [c for c in checks or [] if "result" in c]
    if checks:
        bad = [c for c in done if not c["result"].get("ok")]
        text += f" Ручные проверки: проходят {len(done) - len(bad)} из {len(checks)}."
        if bad:
            cls = "bad"
        elif len(done) < len(checks):
            cls = "warn" if cls == "ok" else cls
        elif spread and yld:
            cls = "ok"   # the model's corners do not converge; the joints are proven by hand
    return cls, lead + text


def _page_head(status: dict, details: dict, study: str, title: str) -> list:
    doc = status.get("document") or ""
    return [f"<!doctype html><html lang='ru'><head><meta charset='utf-8'><title>{_e(title or 'Расчёт ' + doc)}</title>",
            f"<style>{CSS}</style></head><body>",
            f"<h1>{_e(title or 'Расчёт на прочность')}</h1>",
            f"<div class='meta'>Модель: {_e(status.get('path') or doc)} · исследование «{_e(study)}» "
            f"({_e(details.get('type', 'Static'))}) · {datetime.datetime.now():%d.%m.%Y %H:%M} · "
            f"SOLIDWORKS Simulation {_e(status.get('version', ''))}</div>"]


def _results_section(results: dict) -> list:
    """Stress / displacement / FoS table, the FoS basis, frequencies and buckling factors."""
    h = ["<h2>Результаты</h2>"]
    rows = []
    vm, disp = results.get("von_mises_mpa") or {}, results.get("displacement_mm") or {}
    on, away = vm.get("max_on") or {}, vm.get("max_away_from_boundary") or {}
    if vm:
        at = vm.get("max_at_mm")
        if on:
            at = f"{at} — {'на закреплении' if on.get('kind') == 'fixture' else 'под нагрузкой'} «{on.get('name')}»"
        rows.append(["Напряжения по Мизесу, МПа", vm.get("min"), vm.get("max"), at])
    if away:
        rows.append([f"Напряжения вдали от закреплений и нагрузок (дальше {_num(away.get('excluded_within_mm'))} мм), МПа",
                     "", away.get("max"), away.get("at_mm")])
    if disp:
        rows.append(["Перемещения, мм", disp.get("min"), disp.get("max"), disp.get("max_at_mm")])
    fos = results.get("factor_of_safety")
    if fos:
        rows.append(["Коэффициент запаса", fos.get("min"), fos.get("max"), ""])
    elif results.get("factor_of_safety_min") is not None:
        rows.append(["Коэффициент запаса", results["factor_of_safety_min"], "", ""])
    if away_fos(results) is not None:
        rows.append(["Коэффициент запаса вдали от закреплений и нагрузок", round(away_fos(results), 3), "", away.get("at_mm")])
    if rows:
        h.append(_table(["Величина", "min", "max", "где max, мм"], rows))
    if on and away:
        h.append("<p class='meta'>Пик напряжений лежит на закреплении или под нагрузкой: там его задаёт идеализация "
                 "(жёсткая заделка, сила на малой грани), у острой кромки он растёт без предела при сгущении сетки. О детали "
                 "судят по напряжениям вдали от закреплений и нагрузок, а сам узел крепления проверяют отдельно.</p>")
    basis = results.get("fos_basis")
    yf = results.get("yield_from_material")
    if basis or yf:
        h.append(f"<p class='meta'>Запас: {_e(basis or '')}"
                 + (f"; предел текучести {yf['yield_mpa']} МПа ({_e(', '.join(yf.get('materials', [])))})" if yf else "") + "</p>")
    for k in ("frequencies", "buckling_load_factors"):
        if results.get(k):
            h.append(_table(["Форма", "Частота, Гц" if k == "frequencies" else "Коэффициент"],
                            [[i + 1, v] for i, v in enumerate(results[k])]))
    return h


def _connectors_section(results: dict) -> list:
    conns = results.get("connectors") or []
    if not conns:
        return []
    return ["<h2>Соединители</h2>",
            _table(["Соединитель", "Силы (как вернул SolidWorks), Н", "Шов"],
                   [[c.get("name"), ", ".join(_num(x) for x in c.get("forces", [])) or c.get("forces_error", ""),
                     ("проходит" if c.get("weld_pass") else "НЕ проходит") if "weld_pass" in c else ""] for c in conns])]


def _checks_section(checks: list) -> list:
    """Hand checks (EN 1993-1-8) with their inputs: [{"title", "check", "args", "result" | "error"}]."""
    if not checks:
        return []
    rows = []
    for c in checks:
        r = c.get("result") or {}
        util = r.get("utilization", r.get("stress_util"))
        rows.append([c.get("title") or CHECK_RU.get(c.get("check"), c.get("check")),
                     "; ".join(f"{ARG_RU.get(k, k)} {_num(v)}" for k, v in (c.get("args") or {}).items()),
                     util if util is not None else "", c.get("error") or ("проходит" if r.get("ok") else "НЕ проходит")])
    return ["<h2>Ручные проверки (EN 1993-1-8)</h2>", _table(["Проверка", "Исходные данные", "Использование", "Вывод"], rows)]


def _mesh_section(results: dict, details: dict, compared: list) -> list:
    """Mesh sensitivity: the report's study and the compared ones [{"study", "details" (sim_status), "results"}]."""
    if not compared:
        return []
    rows = []
    for st, d, r in [(results.get("study"), details, results)] + [(c.get("study"), c.get("details") or {}, c.get("results") or {})
                                                                  for c in compared]:
        m, vm = d.get("mesh") or {}, r.get("von_mises_mpa") or {}
        away = vm.get("max_away_from_boundary") or {}
        rows.append([st, m.get("element_size_mm", ""), m.get("elements", ""),
                     ", ".join(f"{_num(c.get('element_size_mm'))} мм" for c in m.get("controls") or []), vm.get("max", ""),
                     away.get("max", ""), away.get("at_mm", ""), (r.get("displacement_mm") or {}).get("max", "")])
    return ["<h2>Сходимость по сетке</h2>",
            _table(["Исследование", "Элемент, мм", "Элементов", "Сгущение", "Пик σ, МПа", "σ вдали от закреплений, МПа",
                    "где, мм", "Перемещение, мм"], rows)]


def _notes_section(notes: list) -> list:
    if not notes:
        return []
    return ["<h2>Допущения и нагрузки</h2><ul>"] + [f"<li>{_e(n)}</li>" for n in notes] + ["</ul>"]


def _images_section(images: list) -> list:
    if not images:
        return []
    return ["<h2>Эпюры</h2>"] + [f"<figure><img src='{_e(os.path.basename(img['image']))}' alt='{_e(img['caption'])}'>"
                                 f"<figcaption>{_e(img['caption'])}</figcaption></figure>" for img in images]


def _setup_section(d: dict) -> list:
    """The study setup from sim_status(details): materials, loads and fixtures, contacts, mesh."""
    h = ["<h2>Исходные данные</h2>"]
    mats = [m for m in d.get("materials") or [] if m.get("meshed_as") != "shell"]   # a sheet-metal body: its shell row says it
    if mats:
        h.append(_table(["Тело / оболочка", "Материал", "Толщина, мм"],
                        [[m.get("body") or m.get("shell"), m.get("material"), m.get("thickness_mm", "")] for m in mats]))
    lrs = d.get("loads_fixtures_connectors") or []
    if lrs:
        h.append(_table(["Элемент", "Вид", "Граней/рёбер"],
                        [[x.get("name"), KIND_RU.get(x.get("kind"), x.get("kind")), x.get("entities")] for x in lrs]))
    if d.get("global_contact") or d.get("contact_sets"):
        rows = [["глобальный", d.get("global_contact", ""), ""]]
        rows += [[c.get("name"), c.get("type"), c.get("friction") or ""] for c in d.get("contact_sets") or []]
        h.append(_table(["Контакт", "Тип", "Трение"], rows))
    m = d.get("mesh") or {}
    if m:
        h.append(_table(["Сетка", "Элементов", "Узлов", "Размер, мм"],
                        [[m.get("state", ""), m.get("elements"), m.get("nodes"), m.get("element_size_mm")]]
                        + [[f"сгущение «{c.get('name')}»", "", "", c.get("element_size_mm")] for c in m.get("controls") or []]))
    return h


def build_html(status: dict, results: dict, images: list, required_fos: float = 1.5, title: str = "",
               checks: list = None, notes: list = None, compared: list = None) -> str:
    d = status.get("details") or {}
    study = results.get("study") or d.get("study") or ""
    cls, text = verdict(results, required_fos, checks, compared)
    h = (_page_head(status, d, study, title) + [f"<div class='verdict {cls}'>{_e(text)}</div>"] + _notes_section(notes)
         + _results_section(results) + _mesh_section(results, d, compared) + _checks_section(checks)
         + _connectors_section(results) + _images_section(images) + _setup_section(d))
    h.append("<p class='meta'>Отчёт сформирован по данным SOLIDWORKS Simulation. Линейный статический расчёт: "
             "проверьте закрепления и нагрузки, сингулярности у острых углов и точечных закреплений.</p>")
    h.append("</body></html>")
    return "".join(h)
