#!/usr/bin/env python3
"""Regenerate the seven V031 evaluation regression fixtures synthetically.

Usage: python3 tools/generate_v032_regression_fixtures.py --output DIR

Only the Python standard library is used. Output is deterministic: repeated
runs write byte-identical files. Shared ZIP/XML helpers come from
generate_v031_regression_fixtures.py; this script adds a bold style, column
widths and declared Excel tables (ListObjects).
"""
import argparse
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
import sys

V031_SCRIPT = Path(__file__).with_name('generate_v031_regression_fixtures.py')
_spec = spec_from_file_location('v031_fixtures', V031_SCRIPT)
v031 = module_from_spec(_spec)
_spec.loader.exec_module(v031)

write_zip = v031.write_zip
esc = v031.esc
col_name = v031.col_name
validate_cell = v031.validate_cell
X_NS = v031.X_NS
R_NS = v031.R_NS
PKG_REL_NS = v031.PKG_REL_NS
CT_NS = v031.CT_NS

FIXTURE_NAMES = (
    "unified_numeric_id.xlsx",
    "unified_text_labels.xlsx",
    "independent_one_column.xlsx",
    "independent_two_columns.xlsx",
    "row_labels_spacer.xlsx",
    "excel_table_with_side_list.xlsx",
    "excel_tables_side_by_side.xlsx",
)

TABLE_TYPE = R_NS + "/table"
TABLE_CONTENT_TYPE = ("application/vnd.openxmlformats-officedocument"
                      ".spreadsheetml.table+xml")


def cell_xml(ref, value, bold):
    """One cell element: inline string or integer, s="1" only when bold."""
    style = ' s="1"' if bold else ""
    if isinstance(value, str):
        return ('<c r="%s"%s t="inlineStr"><is><t xml:space="preserve">%s</t>'
                "</is></c>" % (ref, style, esc(value)))
    return '<c r="%s"%s><v>%d</v></c>' % (ref, style, value)


def col_xml(index, width):
    return '<col min="%d" max="%d" width="%d" customWidth="1"/>' % (index, index, width)


def sheet_xml(rows, spacer, used_columns, tables=()):
    """rows: list of (row_number, [(cell_ref, value, bold), ...])."""
    widths = {spacer: 4}
    for column in used_columns:
        widths.setdefault(column, 18)
    parts = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<worksheet xmlns="%s" xmlns:r="%s">' % (X_NS, R_NS),
             "<cols>" + "".join(col_xml(index, widths[index])
                                for index in sorted(widths)) + "</cols>",
             "<sheetData>"]
    for row_number, cells in rows:
        parts.append('<row r="%d">' % row_number)
        for ref, value, bold in cells:
            validate_cell(ref, value)
            parts.append(cell_xml(ref, value, bold))
        parts.append("</row>")
    parts.append("</sheetData>")
    parts.append('<pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" '
                 'header="0.3" footer="0.3"/>')
    if tables:
        parts.append('<tableParts count="%d">' % len(tables)
                     + "".join('<tablePart r:id="rId%d"/>' % i
                               for i in range(1, len(tables) + 1))
                     + "</tableParts>")
    parts.append("</worksheet>")
    return "".join(parts).encode("utf-8")


def table_xml(index, name, ref, columns):
    """One ListObject part: autoFilter, header columns and a table style."""
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<table xmlns="%s" id="%d" name="%s" displayName="%s" ref="%s" '
            'totalsRowShown="0">'
            '<autoFilter ref="%s"/>'
            '<tableColumns count="%d">%s</tableColumns>'
            '<tableStyleInfo name="TableStyleMedium2" showFirstColumn="0" '
            'showLastColumn="0" showRowStripes="1" showColumnStripes="0"/>'
            "</table>" % (X_NS, index, esc(name), esc(name), ref, ref,
                          len(columns),
                          "".join('<tableColumn id="%d" name="%s"/>'
                                  % (i, esc(header))
                                  for i, header in enumerate(columns, start=1))))


def styles_xml():
    """Font 0 regular, font 1 bold; cellXfs index 1 selects the bold font."""
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<styleSheet xmlns="%s">'
            '<fonts count="2">'
            '<font><sz val="11"/><name val="Calibri"/></font>'
            '<font><b/><sz val="11"/><name val="Calibri"/></font>'
            "</fonts>"
            '<fills count="1"><fill><patternFill patternType="none"/></fill></fills>'
            "<borders count=\"1\"><border/></borders>"
            '<cellStyleXfs count="1"><xf/></cellStyleXfs>'
            '<cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs>'
            "</styleSheet>" % X_NS).encode("utf-8")


def workbook_zip(path, rows, spacer, used_columns, tables=()):
    """One-sheet workbook named Data; tables: (name, ref, [headers])."""
    workbook = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                '<workbook xmlns="%s" xmlns:r="%s"><sheets>'
                '<sheet name="Data" sheetId="1" r:id="rId1"/>'
                "</sheets></workbook>" % (X_NS, R_NS)).encode("utf-8")
    wb_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
               '<Relationships xmlns="%s">'
               '<Relationship Id="rId1" Type="%s/worksheet" Target="worksheets/sheet1.xml"/>'
               '<Relationship Id="rId2" Type="%s/styles" Target="styles.xml"/>'
               "</Relationships>" % (PKG_REL_NS, R_NS, R_NS)).encode("utf-8")
    types = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<Types xmlns="%s">' % CT_NS,
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>',
             '<Default Extension="xml" ContentType="application/xml"/>',
             '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>',
             '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>',
             '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>']
    entries = [("xl/workbook.xml", workbook),
               ("xl/_rels/workbook.xml.rels", wb_rels),
               ("xl/styles.xml", styles_xml()),
               ("xl/worksheets/sheet1.xml",
                sheet_xml(rows, spacer, used_columns, tables))]
    if tables:
        sheet_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                      '<Relationships xmlns="%s">' % PKG_REL_NS
                      + "".join('<Relationship Id="rId%d" Type="%s" Target="../tables/table%d.xml"/>'
                                % (i, TABLE_TYPE, i)
                                for i in range(1, len(tables) + 1))
                      + "</Relationships>").encode("utf-8")
        entries.append(("xl/worksheets/_rels/sheet1.xml.rels", sheet_rels))
        for i, (name, ref, columns) in enumerate(tables, start=1):
            entries.append(("xl/tables/table%d.xml" % i, table_xml(i, name, ref, columns)))
            types.append('<Override PartName="/xl/tables/table%d.xml" ContentType="%s"/>'
                         % (i, TABLE_CONTENT_TYPE))
    types.append("</Types>")
    root_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                 '<Relationships xmlns="%s">'
                 '<Relationship Id="rId1" Type="%s/officeDocument" Target="xl/workbook.xml"/>'
                 "</Relationships>" % (PKG_REL_NS, R_NS)).encode("utf-8")
    entries.append(("_rels/.rels", root_rels))
    entries.append(("[Content_Types].xml", "".join(types).encode("utf-8")))
    write_zip(path, entries)


def _grid(cells):
    """cells: dict of cell_ref -> (value, bold); rows ascending, columns in order."""
    rows = {}
    for ref in cells:
        digits = next(i for i, ch in enumerate(ref) if ch.isdigit())
        rows.setdefault(int(ref[digits:]), []).append(ref)
    return [(number, [(ref, cells[ref][0], cells[ref][1])
                      for ref in sorted(rows[number], key=_column_index)])
            for number in sorted(rows)]


def _column_index(ref):
    index = 0
    for ch in ref:
        if not ch.isalpha():
            break
        index = index * 26 + ord(ch) - ord("A") + 1
    return index


def _header(value):
    return (value, True)


def unified_numeric_id(path):
    workbook_zip(path, _grid({
        "A1": _header("ID"), "B1": _header("Item"),
        "D1": _header("Quantity"), "E1": _header("Amount"),
        "A2": (1, False), "B2": ("Apple", False), "D2": (2, False), "E2": (100, False),
        "A3": (2, False), "B3": ("Pear", False), "D3": (3, False), "E3": (200, False),
    }), spacer=3, used_columns=[1, 2, 4, 5])


def unified_text_labels(path):
    workbook_zip(path, _grid({
        "A1": _header("Label"), "B1": _header("Name"),
        "D1": _header("Quantity"), "E1": _header("Amount"),
        "A2": ("Apple", False), "B2": ("Fruit A", False),
        "D2": (2, False), "E2": (100, False),
        "A3": ("Pear", False), "B3": ("Fruit B", False),
        "D3": (3, False), "E3": (200, False),
    }), spacer=3, used_columns=[1, 2, 4, 5])


def independent_one_column(path):
    workbook_zip(path, _grid({
        "A1": _header("Item"), "B1": _header("Q1"), "D1": _header("Candidates"),
        "A2": ("Revenue", False), "B2": (100, False), "D2": ("CANDIDATE_ONE", False),
        "A3": ("Cost", False), "B3": (80, False), "D3": ("CANDIDATE_TWO", False),
    }), spacer=3, used_columns=[1, 2, 4])


def independent_two_columns(path):
    workbook_zip(path, _grid({
        "A1": _header("Item"), "B1": _header("Q1"), "D1": _header("Candidates"),
        "E1": _header("Value"),
        "A2": ("Revenue", False), "B2": (100, False), "D2": ("CANDIDATE_ONE", False),
        "E2": (1, False),
        "A3": ("Cost", False), "B3": (80, False), "D3": ("CANDIDATE_TWO", False),
        "E3": (2, False),
    }), spacer=3, used_columns=[1, 2, 4, 5])


def row_labels_spacer(path):
    workbook_zip(path, _grid({
        "A1": _header("Item"), "C1": _header("Q1"), "D1": _header("Q2"),
        "A2": ("Revenue", False), "C2": (100, False), "D2": (120, False),
        "A3": ("Cost", False), "C3": (80, False), "D3": (90, False),
    }), spacer=2, used_columns=[1, 3, 4])


def excel_table_with_side_list(path):
    workbook_zip(path, _grid({
        "A1": _header("Item"), "B1": _header("Q1"), "D1": _header("Candidates"),
        "A2": ("Revenue", False), "B2": (100, False), "D2": ("CANDIDATE_ONE", False),
        "A3": ("Cost", False), "B3": (80, False), "D3": ("CANDIDATE_TWO", False),
    }), spacer=3, used_columns=[1, 2, 4],
        tables=[("Sales", "A1:B3", ["Item", "Q1"])])


def excel_tables_side_by_side(path):
    workbook_zip(path, _grid({
        "A1": _header("Item"), "B1": _header("Q1"),
        "D1": _header("Month"), "E1": _header("Target"),
        "A2": ("Revenue", False), "B2": (100, False),
        "D2": (4, False), "E2": (120, False),
        "A3": ("Cost", False), "B3": (80, False),
        "D3": (5, False), "E3": (90, False),
    }), spacer=3, used_columns=[1, 2, 4, 5],
        tables=[("Sales", "A1:B3", ["Item", "Q1"]),
                ("Targets", "D1:E3", ["Month", "Target"])])


BUILDERS = {
    "unified_numeric_id.xlsx": unified_numeric_id,
    "unified_text_labels.xlsx": unified_text_labels,
    "independent_one_column.xlsx": independent_one_column,
    "independent_two_columns.xlsx": independent_two_columns,
    "row_labels_spacer.xlsx": row_labels_spacer,
    "excel_table_with_side_list.xlsx": excel_table_with_side_list,
    "excel_tables_side_by_side.xlsx": excel_tables_side_by_side,
}


def generate(output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    for name in FIXTURE_NAMES:
        BUILDERS[name](output / name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        generate(args.output)
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
