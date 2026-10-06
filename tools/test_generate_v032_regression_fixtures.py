"""Contracts for the redistributable v0.3.2 regression inputs."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile
from xml.etree import ElementTree as ET

SCRIPT = Path(__file__).with_name('generate_v032_regression_fixtures.py')
spec = importlib.util.spec_from_file_location('v032_fixtures', SCRIPT)
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
X = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
R = '{http://schemas.openxmlformats.org/officeDocument/2006/relationships}'
P = '{http://schemas.openxmlformats.org/package/2006/relationships}'
C = '{http://schemas.openxmlformats.org/package/2006/content-types}'
BOLD = 'bold'
FIXTURES = SCRIPT.parent.parent / 'tests/DocRedock.Tests/Fixtures/Evaluation/V031'

EXPECTED_CELLS = {
    'unified_numeric_id.xlsx': {
        'A1': ('ID', BOLD), 'B1': ('Item', BOLD), 'D1': ('Quantity', BOLD),
        'E1': ('Amount', BOLD),
        'A2': (1, ''), 'B2': ('Apple', ''), 'D2': (2, ''), 'E2': (100, ''),
        'A3': (2, ''), 'B3': ('Pear', ''), 'D3': (3, ''), 'E3': (200, '')},
    'unified_text_labels.xlsx': {
        'A1': ('Label', BOLD), 'B1': ('Name', BOLD), 'D1': ('Quantity', BOLD),
        'E1': ('Amount', BOLD),
        'A2': ('Apple', ''), 'B2': ('Fruit A', ''), 'D2': (2, ''), 'E2': (100, ''),
        'A3': ('Pear', ''), 'B3': ('Fruit B', ''), 'D3': (3, ''), 'E3': (200, '')},
    'independent_one_column.xlsx': {
        'A1': ('Item', BOLD), 'B1': ('Q1', BOLD), 'D1': ('Candidates', BOLD),
        'A2': ('Revenue', ''), 'B2': (100, ''), 'D2': ('CANDIDATE_ONE', ''),
        'A3': ('Cost', ''), 'B3': (80, ''), 'D3': ('CANDIDATE_TWO', '')},
    'independent_two_columns.xlsx': {
        'A1': ('Item', BOLD), 'B1': ('Q1', BOLD), 'D1': ('Candidates', BOLD),
        'E1': ('Value', BOLD),
        'A2': ('Revenue', ''), 'B2': (100, ''), 'D2': ('CANDIDATE_ONE', ''), 'E2': (1, ''),
        'A3': ('Cost', ''), 'B3': (80, ''), 'D3': ('CANDIDATE_TWO', ''), 'E3': (2, '')},
    'row_labels_spacer.xlsx': {
        'A1': ('Item', BOLD), 'C1': ('Q1', BOLD), 'D1': ('Q2', BOLD),
        'A2': ('Revenue', ''), 'C2': (100, ''), 'D2': (120, ''),
        'A3': ('Cost', ''), 'C3': (80, ''), 'D3': (90, '')},
    'excel_table_with_side_list.xlsx': {
        'A1': ('Item', BOLD), 'B1': ('Q1', BOLD), 'D1': ('Candidates', BOLD),
        'A2': ('Revenue', ''), 'B2': (100, ''), 'D2': ('CANDIDATE_ONE', ''),
        'A3': ('Cost', ''), 'B3': (80, ''), 'D3': ('CANDIDATE_TWO', '')},
    'excel_tables_side_by_side.xlsx': {
        'A1': ('Item', BOLD), 'B1': ('Q1', BOLD), 'D1': ('Month', BOLD),
        'E1': ('Target', BOLD),
        'A2': ('Revenue', ''), 'B2': (100, ''), 'D2': (4, ''), 'E2': (120, ''),
        'A3': ('Cost', ''), 'B3': (80, ''), 'D3': (5, ''), 'E3': (90, '')},
}

EXPECTED_TABLES = {
    'excel_table_with_side_list.xlsx': [('Sales', 'A1:B3', ['Item', 'Q1'])],
    'excel_tables_side_by_side.xlsx': [('Sales', 'A1:B3', ['Item', 'Q1']),
                                       ('Targets', 'D1:E3', ['Month', 'Target'])],
}

SPACERS = {'row_labels_spacer.xlsx': 'B'}
TABLE_TYPE = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/table'


def column_of(ref):
    index = 0
    for ch in ref:
        if not ch.isalpha():
            break
        index = index * 26 + ord(ch) - 64
    return index


class FixtureContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        generator.generate(self.root)

    def xml(self, name, member):
        with zipfile.ZipFile(self.root / name) as archive:
            return ET.fromstring(archive.read(member))

    def members(self, name):
        with zipfile.ZipFile(self.root / name) as archive:
            return set(archive.namelist())

    def bold_lookup(self, name):
        styles = self.xml(name, 'xl/styles.xml')
        fonts = list(styles.find(X + 'fonts'))
        xfs = list(styles.find(X + 'cellXfs'))

        def is_bold(style):
            if style is None:
                return False
            font = fonts[int(xfs[int(style)].get('fontId', '0'))]
            return font.find(X + 'b') is not None
        return is_bold

    def cells(self, name):
        is_bold = self.bold_lookup(name)
        sheet = self.xml(name, 'xl/worksheets/sheet1.xml')
        got = {}
        for cell in sheet.iter(X + 'c'):
            if cell.get('t') == 'inlineStr':
                value = ''.join(cell.find(X + 'is').itertext())
            else:
                self.assertIn(cell.get('t'), (None, 'n'))
                value = int(cell.find(X + 'v').text)
            got[cell.get('r')] = (value, BOLD if is_bold(cell.get('s')) else '')
        return got

    def test_all_seven_inputs_repeat_exactly_and_match_committed_fixtures(self):
        expected = set(EXPECTED_CELLS)
        self.assertEqual(expected, {path.name for path in self.root.iterdir()})
        before = {name: (self.root / name).read_bytes() for name in expected}
        generator.generate(self.root)
        for name in expected:
            with self.subTest(name=name):
                self.assertEqual(before[name], (self.root / name).read_bytes())
                self.assertEqual(before[name], (FIXTURES / name).read_bytes())

    def test_packages_have_parseable_xml_fixed_timestamps_and_no_external_parts(self):
        for name in EXPECTED_CELLS:
            with self.subTest(name=name), zipfile.ZipFile(self.root / name) as archive:
                self.assertIsNone(archive.testzip())
                members = set(archive.namelist())
                self.assertFalse(any(part.startswith('docProps/') for part in members))
                for entry in archive.infolist():
                    self.assertEqual((2026, 10, 4, 0, 0, 0), entry.date_time)
                    if entry.filename.endswith(('.xml', '.rels')):
                        tree = ET.fromstring(archive.read(entry))
                        self.assertFalse(any(node.get('TargetMode') == 'External'
                                             for node in tree.iter()))
                workbook = ET.fromstring(archive.read('xl/workbook.xml'))
                self.assertEqual(['Data'],
                                 [s.get('name') for s in workbook.iter(X + 'sheet')])
                for member in ('xl/workbook.xml', 'xl/_rels/workbook.xml.rels',
                               'xl/styles.xml', 'xl/worksheets/sheet1.xml',
                               '_rels/.rels', '[Content_Types].xml'):
                    self.assertIn(member, members)
                if name in EXPECTED_TABLES:
                    self.assertIn('xl/worksheets/_rels/sheet1.xml.rels', members)
                else:
                    self.assertNotIn('xl/worksheets/_rels/sheet1.xml.rels', members)
                    self.assertFalse(any(part.startswith('xl/tables/') for part in members))

    def test_every_fixture_has_exactly_its_cells_types_and_bold_headers(self):
        for name, expected in EXPECTED_CELLS.items():
            with self.subTest(name=name):
                self.assertEqual(expected, self.cells(name))
                sheet = self.xml(name, 'xl/worksheets/sheet1.xml')
                spacer = SPACERS.get(name, 'C')
                self.assertFalse(any(ref[0] == spacer for ref in self.cells(name)))
                widths = {col.get('min'): (col.get('width'), col.get('customWidth'))
                          for col in sheet.iter(X + 'col')}
                self.assertEqual(('4', '1'), widths[str(ord(spacer) - 64)])
                used = {str(column_of(ref)) for ref in expected}
                self.assertNotIn(str(ord(spacer) - 64), used)
                for column in used:
                    self.assertEqual(('18', '1'), widths[column])

    def test_declared_tables_are_readable_from_worksheet_and_package_parts(self):
        for name, tables in EXPECTED_TABLES.items():
            with self.subTest(name=name):
                sheet = self.xml(name, 'xl/worksheets/sheet1.xml')
                parts = sheet.find(X + 'tableParts')
                self.assertIsNotNone(parts)
                self.assertEqual(str(len(tables)), parts.get('count'))
                self.assertEqual(X + 'tableParts', sheet[-1].tag)
                ids = [part.get(R + 'id') for part in parts.findall(X + 'tablePart')]
                self.assertEqual(['rId%d' % i for i in range(1, len(tables) + 1)], ids)
                rels = {rel.get('Id'): rel for rel in
                        self.xml(name, 'xl/worksheets/_rels/sheet1.xml.rels').iter(P + 'Relationship')}
                overrides = {o.get('PartName'): o.get('ContentType') for o in
                             self.xml(name, '[Content_Types].xml').iter(C + 'Override')}
                for rid, (display, ref, columns) in zip(ids, tables):
                    rel = rels[rid]
                    self.assertEqual(TABLE_TYPE, rel.get('Type'))
                    target = 'xl/' + rel.get('Target').replace('../', '')
                    table = self.xml(name, target)
                    self.assertEqual(X + 'table', table.tag)
                    self.assertEqual(ref, table.get('ref'))
                    self.assertEqual(display, table.get('displayName'))
                    self.assertEqual(display, table.get('name'))
                    self.assertEqual(ref, table.find(X + 'autoFilter').get('ref'))
                    self.assertEqual(columns, [c.get('name') for c in
                                               table.find(X + 'tableColumns')])
                    self.assertEqual(generator.TABLE_CONTENT_TYPE, overrides['/' + target])

    def test_invalid_output_file_fails_without_changing_it(self):
        target = self.root / 'existing-file'
        target.write_bytes(b'retain me')
        result = subprocess.run([sys.executable, str(SCRIPT), '--output', str(target)],
                                capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(b'retain me', target.read_bytes())
        self.assertTrue(result.stderr)


if __name__ == '__main__':
    unittest.main()
