"""Contracts for the redistributable v0.3.2 evaluation follow-up inputs."""
import hashlib
import importlib.util
from pathlib import Path
import re
import tempfile
import unittest
import zipfile
from xml.etree import ElementTree as ET

SCRIPT = Path(__file__).with_name('generate_v033_regression_fixtures.py')
spec = importlib.util.spec_from_file_location('v033_fixtures', SCRIPT)
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
X = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
FIXTURES = SCRIPT.parent.parent / 'tests/DocRedock.Tests/Fixtures/Evaluation/V032'
HEADERS = {'captioned_sales.xlsx': ['商品', '売上', '数量'], 'captioned_two_columns.xlsx': ['商品', '売上']}


class FixtureContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        generator.generate(self.root)

    def cells(self, name):
        with zipfile.ZipFile(self.root / name) as archive:
            styles = ET.fromstring(archive.read('xl/styles.xml'))
            sheet = ET.fromstring(archive.read('xl/worksheets/sheet1.xml'))
        fonts = list(styles.find(X + 'fonts'))
        xfs = list(styles.find(X + 'cellXfs'))
        got = {}
        for cell in sheet.iter(X + 'c'):
            style = cell.get('s')
            bold = style is not None and fonts[int(xfs[int(style)].get('fontId', '0'))].find(X + 'b') is not None
            value = ''.join(cell.find(X + 'is').itertext()) if cell.get('t') == 'inlineStr' else int(cell.find(X + 'v').text)
            got[cell.get('r')] = (value, bold)
        return got

    def test_inputs_repeat_exactly_and_match_the_committed_fixtures_and_readme(self):
        self.assertEqual(set(HEADERS), {path.name for path in self.root.iterdir()})
        before = {name: (self.root / name).read_bytes() for name in HEADERS}
        generator.generate(self.root)
        readme = (FIXTURES / 'README.md').read_text(encoding='utf-8')
        for name in HEADERS:
            with self.subTest(name=name):
                self.assertEqual(before[name], (self.root / name).read_bytes())
                self.assertEqual(before[name], (FIXTURES / name).read_bytes())
                self.assertRegex(readme, r'\| %s \| %s \|' % (re.escape(name), hashlib.sha256(before[name]).hexdigest()))

    def test_title_captions_bold_header_and_data_rows(self):
        for name, headers in HEADERS.items():
            with self.subTest(name=name):
                cells = self.cells(name)
                self.assertEqual(('2025年度 営業報告', True), cells['A1'])
                self.assertEqual(('売上実績（単位: 千円、税抜）', False), cells['A3'])
                self.assertEqual(('対象: 国内全店', False), cells['A4'])
                for column, header in zip('ABC', headers):
                    self.assertEqual((header, True), cells['%s5' % column])
                rows = sorted({int(ref[1:]) for ref in cells if int(ref[1:]) > 5})
                self.assertEqual(list(range(6, 86)), rows)
                self.assertEqual(('商品001', False), cells['A6'])
                self.assertTrue(all(isinstance(cells['B%d' % row][0], int) for row in rows))
                self.assertEqual(len(headers) > 2, 'C6' in cells)
                self.assertEqual(3 + len(headers) + 80 * len(headers), len(cells))
                with zipfile.ZipFile(self.root / name) as archive:
                    self.assertIsNone(archive.testzip())
                    self.assertFalse(any(part.startswith('docProps/') for part in archive.namelist()))


if __name__ == '__main__':
    unittest.main()
