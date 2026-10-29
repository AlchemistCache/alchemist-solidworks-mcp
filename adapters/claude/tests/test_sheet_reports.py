"""sheet_reports: the cut-list workbook (write_cutlist_xlsx) and the generic table (write_table_xlsx), pinned cell by
cell — values, formulas, formats, fills, layout. No SolidWorks."""
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

openpyxl = pytest.importorskip("openpyxl")
import sheet_reports  # noqa: E402

ROWS = [
    {"part": "Bracket", "config": "A", "Обозначение": "OP-10", "Наименование": "Кронштейн", "qty": 2, "material": "Ст3",
     "thickness_mm": 3.0, "blank_length_mm": 120.0, "blank_width_mm": 60.0, "net_area_m2": 0.0068, "cut_length_mm": 400.0,
     "cut_outs": 2, "bends": 2, "bend_radius_mm": 3.0, "k_factor": 0.44, "mass_kg": 0.16123},
    {"part": "Bracket copy", "config": "A", "Обозначение": "OP-10", "qty": 1, "material": "Ст3", "thickness_mm": 3.0,
     "blank_length_mm": 120.0, "blank_width_mm": 60.0, "bends": 2, "mass_kg": 0.16},
    {"part": "Plate", "Обозначение": "OP-2", "qty": 1, "material": "Ст3", "thickness_mm": 3.0, "mass_kg": 1.7},
    {"part": "Cover", "qty": 4, "material": "AISI 304", "thickness_mm": 1.0, "mass_kg": 0.3},
]
TOTALS = [{"thickness_material": "3 | Ст3", "positions": 2, "qty": 4, "mass_kg": 2.18123}]
HEADERS = ["№", "Обозначение", "Наименование", "Файл детали", "Исполнение", "Кол., шт", "Заготовка L, мм",
           "Заготовка B, мм", "Площадь нетто, м²", "Длина реза, мм", "Вырезы", "Гибы", "R гиба, мм", "K",
           "Масса 1 шт, кг", "Масса всего, кг", "Площадь всего, м²", "Рез всего, м", "Гибов всего"]


def _grid(ws):
    """{row: values without the trailing empty cells}"""
    out = {}
    for r in ws.iter_rows():
        v = [c.value for c in r]
        while v and v[-1] is None:
            v.pop()
        out[r[0].row] = v
    return out


def _formulas(r):
    return [f"=F{r}*O{r}", f"=F{r}*I{r}", f"=F{r}*J{r}/1000", f"=F{r}*L{r}"]


def _sums(r, first, last):
    blank = [None] * 8
    return ([None, None, None, None, None, f"=SUM(F{first}:F{last})"] + blank + [None]
            + [f"=SUM({c}{first}:{c}{last})" for c in "PQRS"])


@pytest.fixture
def cutlist(tmp_path):
    x = sheet_reports.write_cutlist_xlsx(str(tmp_path / "c.xlsx"), ROWS, TOTALS, "Test.SLDASM")
    return x, openpyxl.load_workbook(x["xlsx"])


def test_cutlist_values_and_formulas(cutlist):
    x, wb = cutlist
    assert (x["positions"], x["groups"]) == (3, 2) and os.path.isabs(x["xlsx"])
    assert wb.sheetnames == ["Ведомость", "Итоги"]
    g = _grid(wb["Ведомость"])
    empty = [None] * 8
    sub6, sub11 = _sums(6, 5, 5), _sums(11, 9, 10)
    sub6[1], sub11[1] = "Итого 1 мм — AISI 304", "Итого 3 мм — Ст3"
    assert g == {
        1: ["Ведомость раскроя листового металла — Test.SLDASM"], 2: [], 3: HEADERS,
        4: ["Лист 1 мм — AISI 304"],
        5: [1, None, None, "Cover", None, 4] + empty + [0.3] + _formulas(5),
        6: sub6, 7: [],
        8: ["Лист 3 мм — Ст3"],
        9: [2, "OP-2", None, "Plate", None, 1] + empty + [1.7] + _formulas(9),
        10: [3, "OP-10", "Кронштейн", "Bracket, Bracket copy", "A", 3, 120, 60, 0.007, 400, 2, 2, 3, 0.44, 0.161]
            + _formulas(10),
        11: sub11, 12: [],
        13: [None, "ВСЕГО", None, None, None, "=F6+F11"] + empty + [None, "=P6+P11", "=Q6+Q11", "=R6+R11", "=S6+S11"],
    }
    assert _grid(wb["Итоги"]) == {1: ["Толщина | материал", "Позиций", "Кол., шт", "Площадь нетто, м²",
                                      "Площадь заготовок, м²", "Рез, м", "Гибов", "Масса, кг"],
                                  2: ["3 | Ст3", 2, 4, None, None, None, None, 2.181]}


def test_cutlist_formats_and_layout(cutlist):
    _, wb = cutlist
    ws = wb["Ведомость"]
    assert ws["A1"].font.b and ws["A1"].font.sz == 13
    head = ws["F3"]
    assert head.font.b and head.fill.fgColor.rgb == "00D9D9D9" and head.border.left.style == "thin"
    assert (head.alignment.horizontal, head.alignment.vertical, head.alignment.wrap_text) == ("center", "center", True)
    assert ws.row_dimensions[3].height == 32
    assert [ws.column_dimensions[c].width for c in "ABCDS"] == [5, 22, 26, 26, 9]
    assert [c.number_format for c in ws[10]] == [c[3] for c in sheet_reports.COLUMNS]
    assert all(c.border.left.style == "thin" for c in ws[10])
    assert all(c.fill.fgColor.rgb == "00EDF2F9" for c in ws[8]) and ws["A8"].font.b
    assert ws["B11"].font.b and ws["F11"].font.b and ws["P11"].number_format == "0.000"
    assert all(c.border.left.style == "thin" for c in ws[11])
    assert ws["B13"].font.b and ws["B13"].font.sz == 12 and ws["R13"].number_format == "0.00"
    assert all(c.fill.fgColor.rgb == "00FFF2CC" for c in ws[13])
    assert (ws.freeze_panes, ws.auto_filter.ref, ws.print_title_rows) == ("C4", "A3:S11", "$3:$3")
    assert ws.page_setup.orientation == "landscape" and ws.sheet_properties.pageSetUpPr.fitToPage
    assert (ws.page_setup.fitToWidth, ws.page_setup.fitToHeight) == (1, 0)
    ts = wb["Итоги"]
    assert ts["A1"].font.b and ts["A1"].alignment.horizontal == "center" and ts.column_dimensions["E"].width == 12
    assert ts["H2"].number_format == "0.000" and ts["B2"].border.left.style == "thin"


def test_empty_cutlist_keeps_a_zero_total(tmp_path):
    x = sheet_reports.write_cutlist_xlsx(str(tmp_path / "sub" / "e.xlsx"), [], None)
    assert (x["positions"], x["groups"]) == (0, 0)
    ws = openpyxl.load_workbook(x["xlsx"])["Ведомость"]
    assert ws["A1"].value == "Ведомость раскроя листового металла"
    assert [ws.cell(4, c).value for c in (2, 6, 16)] == ["ВСЕГО", 0, 0]
    assert ws.auto_filter.ref == "A3:S3"


def test_table_xlsx_with_totals(tmp_path):
    x = sheet_reports.write_table_xlsx(str(tmp_path / "t.xlsx"), "A very long sheet name over thirty-one chars",
                                       "Ведомость", [("Имя", "name", 20), ("Кол.", "qty", 6)],
                                       [{"name": "A", "qty": 1.23456}, {"name": "B"}],
                                       ("Итого", [("Всего", "total", 30)], [{"total": 3}]))
    assert x["xlsx_rows"] == 2 and os.path.isabs(x["xlsx"])
    wb = openpyxl.load_workbook(x["xlsx"])
    ws = wb.active
    assert ws.title == "A very long sheet name over thi"
    assert _grid(ws) == {1: ["Ведомость"], 2: [], 3: ["Имя", "Кол."], 4: ["A", 1.235], 5: ["B"], 6: [],
                         7: ["Итого"], 8: ["Всего"], 9: [3]}
    assert ws["A1"].font.b and ws["A1"].font.sz == 13 and ws["A7"].font.b
    assert ws["A3"].fill.fgColor.rgb == "00D9D9D9" and ws["A8"].fill.fgColor.rgb == "00D9D9D9"
    assert ws["B5"].border.left.style == "thin" and ws["A9"].border.left.style == "thin"
    assert ws.freeze_panes == "A4"
    # the wider of both blocks; openpyxl's default width (13) counts as the first one
    assert (ws.column_dimensions["A"].width, ws.column_dimensions["B"].width) == (30, 13)


def test_table_xlsx_skips_empty_totals(tmp_path):
    x = sheet_reports.write_table_xlsx(str(tmp_path / "t.xlsx"), "S", "T", [("Имя", "name", 20)], [{"name": "A"}],
                                       ("Итого", [("Всего", "total", 5)], []))
    ws = openpyxl.load_workbook(x["xlsx"])["S"]
    assert _grid(ws) == {1: ["T"], 2: [], 3: ["Имя"], 4: ["A"]}
