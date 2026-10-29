"""GOST 2.106 specification builder (gost_spec.py) — sections, positions, order."""
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

ROWS = [
    {"file": "Болт М8х20.SLDPRT", "type": "part", "qty": 8, "Наименование": "Болт М8-6gx20.88 ГОСТ 7798-70"},
    {"file": "Plate.SLDPRT", "type": "part", "qty": 2, "Обозначение": "ОП.100.002", "Наименование": "Пластина"},
    {"file": "Bracket.SLDPRT", "type": "part", "qty": 1, "Обозначение": "ОП.100.010", "Наименование": "Кронштейн"},
    {"file": "Frame.SLDASM", "type": "assembly", "qty": 1, "Обозначение": "ОП.100.100", "Наименование": "Рама"},
    {"file": "PSU.SLDPRT", "type": "part", "qty": 1, "Наименование": "Блок питания LRS-150"},
    {"file": "Gasket.SLDPRT", "type": "part", "qty": 2, "Наименование": "Прокладка", "Раздел": "Материалы"},
    {"file": "Шайба 8.SLDPRT", "type": "part", "qty": 8, "Наименование": "Шайба 8 ГОСТ 11371-78"},
]


def test_sections_and_positions():
    import gost_spec
    spec = gost_spec.build_spec(ROWS, {"Обозначение": "ОП.100.000", "Наименование": "Кронштейн"})
    names = [s["section"] for s in spec]
    assert names == ["Документация", "Сборочные единицы", "Детали", "Стандартные изделия", "Прочие изделия", "Материалы"]
    assert spec[0]["items"][0]["designation"] == "ОП.100.000 СБ" and spec[0]["items"][0]["pos"] == ""
    parts = spec[2]["items"]
    assert [p["designation"] for p in parts] == ["ОП.100.002", "ОП.100.010"]     # natural order
    assert [p["pos"] for p in parts] == [2, 3]                                    # positions run through sections
    std = spec[3]["items"]
    assert [s["name"][:4] for s in std] == ["Болт", "Шайб"] and std[0]["qty"] == 8
    assert spec[-1]["items"][0]["pos"] == 7


def test_xlsx(tmp_path):
    pytest.importorskip("openpyxl")
    import gost_spec
    import openpyxl
    spec = gost_spec.build_spec(ROWS, {"Обозначение": "ОП.100.000"})
    x = gost_spec.write_xlsx(str(tmp_path / "s.xlsx"), spec, {"Обозначение": "ОП.100.000"})
    assert x["rows"] == 8
    ws = openpyxl.load_workbook(x["xlsx"])["Спецификация"]
    vals = [[c.value for c in row] for row in ws.iter_rows(min_row=3)]
    assert vals[0] == ["Формат", "Зона", "Поз.", "Обозначение", "Наименование", "Кол.", "Примечание"]
    assert any(v[4] == "Стандартные изделия" for v in vals)


def test_template_default_details_moved_for_purchased_and_standard():
    """A real template said «Детали» for every part — DIN nuts and bought units included. Without «Обозначение» a standard
    name or a purchased-items folder overrides it; the type from bom_export is upper case."""
    rows = [
        {"file": "Гайка-барашковая-ГОСТ-3032-76-DIN-315.sldprt", "Раздел": "Детали", "type": "PART", "qty": 6,
         "path": r"D:\Projects\Machine\Покупное\Крепёж\Гайка-барашковая-ГОСТ-3032-76-DIN-315.sldprt"},
        {"file": "Блок питания.SLDPRT", "Раздел": "Детали", "type": "PART", "qty": 1,
         "path": r"D:\Projects\Machine\Покупное\Электрика\Блок питания.SLDPRT"},
        {"file": "Панель.SLDPRT", "Раздел": "Детали", "type": "PART", "qty": 2, "path": r"D:\Projects\Machine\Панель.SLDPRT"},
        {"file": "Узел.SLDASM", "type": "ASSEMBLY", "qty": 1, "path": r"D:\Projects\Machine\Узел.SLDASM"},
    ]
    gost_spec = pytest.importorskip("gost_spec")
    moved = []
    spec = gost_spec.build_spec(rows, {}, False, moved)
    by = {s["section"]: [i["name"] for i in s["items"]] for s in spec}
    assert by == {"Сборочные единицы": ["Узел"], "Детали": ["Панель"],
                  "Стандартные изделия": ["Гайка-барашковая-ГОСТ-3032-76-DIN-315"], "Прочие изделия": ["Блок питания"]}
    assert [(m["to"], m["why"]) for m in moved] == [("Стандартные изделия", "the name cites a standard"),
                                                     ("Прочие изделия", "it lies in a purchased-items folder")]
