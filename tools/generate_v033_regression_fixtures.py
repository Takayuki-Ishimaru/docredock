#!/usr/bin/env python3
"""Regenerate the v0.3.2 evaluation follow-up fixtures synthetically.

Usage: python3 tools/generate_v033_regression_fixtures.py --output DIR

Each workbook has a title, then a unit note and a condition note directly above a long
sales table with a bold header: three columns in captioned_sales.xlsx, two in
captioned_two_columns.xlsx. Only the Python standard library is used and repeated runs
write byte-identical files; the workbook helpers come from
generate_v032_regression_fixtures.py.
"""
import argparse
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
import sys

V032_SCRIPT = Path(__file__).with_name('generate_v032_regression_fixtures.py')
_spec = spec_from_file_location('v032_fixtures', V032_SCRIPT)
v032 = module_from_spec(_spec)
_spec.loader.exec_module(v032)

FIXTURE_NAMES = ("captioned_sales.xlsx", "captioned_two_columns.xlsx")
TITLE = "2025年度 営業報告"
CAPTIONS = ("売上実績（単位: 千円、税抜）", "対象: 国内全店")
DATA_ROWS = 80
HEADER_ROW = 3 + len(CAPTIONS)


def cells_of(headers):
    """cell_ref -> (value, bold): title in A1, captions from A3, header, then data rows."""
    cells = {"A1": (TITLE, True)}
    for index, caption in enumerate(CAPTIONS):
        cells["A%d" % (3 + index)] = (caption, False)
    for column, header in enumerate(headers):
        cells["%s%d" % (v032.col_name(column), HEADER_ROW)] = (header, True)
    for index in range(1, DATA_ROWS + 1):
        row = HEADER_ROW + index
        cells["A%d" % row] = ("商品%03d" % index, False)
        cells["B%d" % row] = (100 + index * 37 % 900, False)
        if len(headers) > 2:
            cells["C%d" % row] = (1 + index % 9, False)
    return cells


def captioned_table(path, headers):
    v032.workbook_zip(path, v032._grid(cells_of(headers)), spacer=len(headers) + 2,
                      used_columns=list(range(1, len(headers) + 1)))


BUILDERS = {
    "captioned_sales.xlsx": lambda path: captioned_table(path, ("商品", "売上", "数量")),
    "captioned_two_columns.xlsx": lambda path: captioned_table(path, ("商品", "売上")),
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
