"""sim_report.build_html: the strength report page, pinned on synthetic sim_status / sim_results payloads (no
SolidWorks). The clock is fixed so the whole page can be compared."""
import datetime
import os
import sys
import types

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import sim_report  # noqa: E402

FOOTER = ("<p class='meta'>Отчёт сформирован по данным SOLIDWORKS Simulation. Линейный статический расчёт: "
          "проверьте закрепления и нагрузки, сингулярности у острых углов и точечных закреплений.</p></body></html>")

STATUS = {"document": "Bracket.SLDPRT", "path": "D:/Work/Bracket.SLDPRT", "version": "2024 SP1",
          "details": {"study": "Static 1", "type": "Static",
                      "materials": [{"body": "Body1", "material": "AISI 304"},
                                    {"shell": "Shell-1", "material": "Ст3", "thickness_mm": 2}],
                      "loads_fixtures_connectors": [{"name": "Fixed-1", "kind": "restraint", "entities": 2},
                                                    {"name": "Odd-1", "kind": "unknownkind", "entities": 3}],
                      "global_contact": "bonded",
                      "contact_sets": [{"name": "CS1", "type": "no_penetration", "friction": 0.2}],
                      "mesh": {"state": "meshed", "elements": 12034, "nodes": 20345, "element_size_mm": 2.5,
                               "controls": [{"name": "Hole", "element_size_mm": 0.8}]}}}
RESULTS = {"study": "Static 1", "von_mises_mpa": {"min": 0.0123, "max": 187.4567, "max_at_mm": [10.0, 2.5, 0.0]},
           "displacement_mm": {"min": 0.0, "max": 0.0812, "max_at_mm": [50, 0, 3]},
           "factor_of_safety": {"min": 1.28, "max": 1234.5},
           "fos_basis": "von Mises / yield", "yield_from_material": {"yield_mpa": 240, "materials": ["Ст3", "AISI 304"]},
           "frequencies": [12.5, 40.25], "buckling_load_factors": [3.5],
           "connectors": [{"name": "Bolt-1", "forces": [120.5, -3.25, 0.0]},
                          {"name": "Weld-1", "forces": [], "forces_error": "no results", "weld_pass": False},
                          {"name": "Weld-2", "forces": [1.0], "weld_pass": True}]}
IMAGES = [{"image": "C:/tmp/vm.png", "caption": "Напряжения <Мизес>"}, {"image": "C:/tmp/u.png", "caption": "Перемещения"}]


@pytest.fixture(autouse=True)
def fixed_clock(monkeypatch):
    class Clock(datetime.datetime):
        @classmethod
        def now(cls, tz=None):
            return cls(2026, 10, 21, 9, 5)
    monkeypatch.setattr(sim_report, "datetime", types.SimpleNamespace(datetime=Clock))


def _row(*cells):
    return "<tr>" + "".join(cells) + "</tr>"


def _td(v):
    return f"<td>{v}</td>"


def _num(v):
    return f"<td class='num'>{v}</td>"


def test_minimal_report_is_the_whole_page():
    page = sim_report.build_html({"document": "Plate.SLDPRT"}, {"factor_of_safety_min": 0.8}, [], 2.0)
    assert page == (
        "<!doctype html><html lang='ru'><head><meta charset='utf-8'><title>Расчёт Plate.SLDPRT</title>"
        f"<style>{sim_report.CSS}</style></head><body><h1>Расчёт на прочность</h1>"
        "<div class='meta'>Модель: Plate.SLDPRT · исследование «» (Static) · 21.10.2026 09:05 · SOLIDWORKS Simulation "
        "</div><div class='verdict bad'>Запас прочности 0.80 &lt; 1 — напряжения выше предела текучести.</div>"
        "<h2>Результаты</h2><table><tr><th>Величина</th><th>min</th><th>max</th><th>где max, мм</th></tr>"
        + _row(_td("Коэффициент запаса"), _num("0.8"), _td(""), _td("")) + "</table>"
        "<h2>Исходные данные</h2>" + FOOTER)


def test_no_results_no_setup():
    page = sim_report.build_html({}, {}, [])
    assert "<div class='verdict warn'>Запас прочности не определён" in page
    assert page.endswith("<h2>Результаты</h2><h2>Исходные данные</h2>" + FOOTER)


def test_full_report_sections_in_order():
    page = sim_report.build_html(STATUS, RESULTS, IMAGES, required_fos=1.5, title="Кронштейн & опора")
    head = page[:page.index("<h2>")]
    assert head.endswith(
        "<title>Кронштейн &amp; опора</title>" f"<style>{sim_report.CSS}</style></head><body>"
        "<h1>Кронштейн &amp; опора</h1><div class='meta'>Модель: D:/Work/Bracket.SLDPRT · исследование «Static 1» "
        "(Static) · 21.10.2026 09:05 · SOLIDWORKS Simulation 2024 SP1</div><div class='verdict warn'>Запас прочности "
        "1.28 &lt; требуемого 1.5, но &gt; 1 — текучести нет, запас недостаточен.</div>")
    body = page[len(head):]
    expected = "".join([
        "<h2>Результаты</h2><table><tr><th>Величина</th><th>min</th><th>max</th><th>где max, мм</th></tr>",
        _row(_td("Напряжения по Мизесу, МПа"), _num("0.012"), _num("187.457"), _td("[10.0, 2.5, 0.0]")),
        _row(_td("Перемещения, мм"), _num("0"), _num("0.081"), _td("[50, 0, 3]")),
        _row(_td("Коэффициент запаса"), _num("1.28"), _num("1 234.5"), _td("")), "</table>",
        "<p class='meta'>Запас: von Mises / yield; предел текучести 240 МПа (Ст3, AISI 304)</p>",
        "<table><tr><th>Форма</th><th>Частота, Гц</th></tr>", _row(_num("1"), _num("12.5")),
        _row(_num("2"), _num("40.25")), "</table>",
        "<table><tr><th>Форма</th><th>Коэффициент</th></tr>", _row(_num("1"), _num("3.5")), "</table>",
        "<h2>Соединители</h2><table><tr><th>Соединитель</th><th>Силы (как вернул SolidWorks), Н</th><th>Шов</th></tr>",
        _row(_td("Bolt-1"), _td("120.5, -3.25, 0"), _td("")), _row(_td("Weld-1"), _td("no results"), _td("НЕ проходит")),
        _row(_td("Weld-2"), _td("1"), _td("проходит")), "</table>",
        "<h2>Эпюры</h2><figure><img src='vm.png' alt='Напряжения &lt;Мизес&gt;'>"
        "<figcaption>Напряжения &lt;Мизес&gt;</figcaption></figure>"
        "<figure><img src='u.png' alt='Перемещения'><figcaption>Перемещения</figcaption></figure>",
        "<h2>Исходные данные</h2><table><tr><th>Тело / оболочка</th><th>Материал</th><th>Толщина, мм</th></tr>",
        _row(_td("Body1"), _td("AISI 304"), _td("")), _row(_td("Shell-1"), _td("Ст3"), _num("2")), "</table>",
        "<table><tr><th>Элемент</th><th>Вид</th><th>Граней/рёбер</th></tr>",
        _row(_td("Fixed-1"), _td("закрепление"), _num("2")), _row(_td("Odd-1"), _td("unknownkind"), _num("3")), "</table>",
        "<table><tr><th>Контакт</th><th>Тип</th><th>Трение</th></tr>",
        _row(_td("глобальный"), _td("bonded"), _td("")), _row(_td("CS1"), _td("no_penetration"), _num("0.2")), "</table>",
        "<table><tr><th>Сетка</th><th>Элементов</th><th>Узлов</th><th>Размер, мм</th></tr>",
        _row(_td("meshed"), _num("12034"), _num("20345"), _num("2.5")),
        _row(_td("сгущение «Hole»"), _td(""), _td(""), _num("0.8")), "</table>",
        FOOTER])
    assert body == expected


def test_contact_and_mesh_without_global_contact():
    page = sim_report.build_html({"details": {"mesh": {"state": "none"}, "contact_sets": [{"name": "A", "type": "bonded"}]}},
                                 {}, [])
    assert ("<h2>Исходные данные</h2><table><tr><th>Контакт</th><th>Тип</th><th>Трение</th></tr>"
            + _row(_td("глобальный"), _td(""), _td("")) + _row(_td("A"), _td("bonded"), _td("")) + "</table>"
            "<table><tr><th>Сетка</th><th>Элементов</th><th>Узлов</th><th>Размер, мм</th></tr>"
            + _row(_td("none"), _td(""), _td(""), _td("")) + "</table>" + FOOTER) in page


def test_fos_basis_without_material_yield():
    page = sim_report.build_html({}, {"factor_of_safety_min": 1.2, "fos_basis": "x"}, [])
    assert "</table><p class='meta'>Запас: x</p><h2>Исходные данные</h2>" in page


PEAK = {"study": "S", "von_mises_mpa": {"min": 0.4, "max": 240.0, "max_at_mm": [-3, 80, -30.5],
                                        "max_on": {"name": "Fixed-1", "kind": "fixture", "distance_mm": 0.0},
                                        "max_away_from_boundary": {"max": 100.0, "at_mm": [-3, 29.8, -43.5],
                                                                   "excluded_within_mm": 7.18}},
        "factor_of_safety": {"min": 0.92, "max": 500}}


def test_peak_on_a_fixture_is_judged_away_from_it():
    """A welded support bracket: 240 MPa on the edge of a fixed bolt hole — the verdict rests on the stress away from it."""
    cls, text = sim_report.verdict(PEAK, 1.5)
    assert cls == "ok" and text.startswith("Пик 240 МПа — на закреплении «Fixed-1» (его даёт идеализация, не деталь). "
                                           "Вдали от закреплений и нагрузок 100 МПа. Запас прочности 2.21 ≥ требуемого 1.5")
    page = sim_report.build_html({}, PEAK, [])
    assert _row(_td("Напряжения по Мизесу, МПа"), _num("0.4"), _num("240"),
                _td("[-3, 80, -30.5] — на закреплении «Fixed-1»")) in page
    assert _row(_td("Напряжения вдали от закреплений и нагрузок (дальше 7.18 мм), МПа"), _td(""), _num("100"),
                _td("[-3, 29.8, -43.5]")) in page
    assert _row(_td("Коэффициент запаса вдали от закреплений и нагрузок"), _num("2.208"), _td(""), _td("[-3, 29.8, -43.5]")) in page


def test_a_stress_that_moves_with_the_mesh_needs_hand_checks():
    fine = {"study": "S fine", "von_mises_mpa": {"max": 254.0, "max_on": {"name": "Fixed-1", "kind": "fixture"},
                                                 "max_away_from_boundary": {"max": 189.0, "at_mm": [-47.6, 10, -12]}}}
    compared = [{"study": "S fine", "details": {"mesh": {"element_size_mm": 1.8, "elements": 21385}}, "results": fine}]
    cls, text = sim_report.verdict(PEAK, 1.5, None, compared)
    assert cls == "warn" and "зависит от сетки: 100–189 МПа (запас 1.17–2.21)" in text and "Вдали" not in text
    good = [{"title": "Шов", "check": "weld_check", "args": {"force_n": 1000, "length_mm": 50, "leg_mm": 4},
             "result": {"utilization": 0.1, "ok": True}}]
    assert sim_report.verdict(PEAK, 1.5, good, compared)[0] == "ok"
    bad = good + [{"title": "Болт", "check": "bolt_check", "args": {"diameter_mm": 8},
                   "result": {"utilization": 1.3, "ok": False}}]
    cls, text = sim_report.verdict(PEAK, 1.5, bad, compared)
    assert cls == "bad" and text.endswith("Ручные проверки: проходят 1 из 2.")
    page = sim_report.build_html({"details": {"mesh": {"element_size_mm": 3.59, "elements": 4860,
                                                       "controls": [{"element_size_mm": 1.0}]}}}, PEAK, [], 1.5, "",
                                 bad, ["Масса 840 кг", "Катет <4> мм"], compared)
    assert "<h2>Допущения и нагрузки</h2><ul><li>Масса 840 кг</li><li>Катет &lt;4&gt; мм</li></ul><h2>Результаты</h2>" in page
    assert ("<h2>Сходимость по сетке</h2><table><tr><th>Исследование</th><th>Элемент, мм</th><th>Элементов</th>"
            "<th>Сгущение</th><th>Пик σ, МПа</th><th>σ вдали от закреплений, МПа</th><th>где, мм</th><th>Перемещение, мм</th></tr>"
            + _row(_td("S"), _num("3.59"), _num("4860"), _td("1 мм"), _num("240"), _num("100"), _td("[-3, 29.8, -43.5]"), _td(""))
            + _row(_td("S fine"), _num("1.8"), _num("21385"), _td(""), _num("254"), _num("189"), _td("[-47.6, 10, -12]"), _td(""))
            + "</table><h2>Ручные проверки (EN 1993-1-8)</h2>") in page
    assert (_row(_td("Шов"), _td("F, Н 1000; L, мм 50; катет, мм 4"), _num("0.1"), _td("проходит"))
            + _row(_td("Болт"), _td("d, мм 8"), _num("1.3"), _td("НЕ проходит"))) in page
