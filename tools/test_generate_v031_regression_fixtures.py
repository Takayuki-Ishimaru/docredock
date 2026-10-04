"""Contracts for the redistributable v0.3.1 regression inputs."""
import importlib.util
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile
import zlib
from xml.etree import ElementTree as ET

SCRIPT = Path(__file__).with_name('generate_v031_regression_fixtures.py')
spec = importlib.util.spec_from_file_location('v031_fixtures', SCRIPT)
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
X = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'


class FixtureContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        generator.generate(self.root)

    def xml(self, name, member):
        with zipfile.ZipFile(self.root / name) as archive:
            return ET.fromstring(archive.read(member))

    def test_all_eight_inputs_repeat_exactly_and_match_committed_fixtures(self):
        expected = {'workbook.xlsx', 'workbook_base.xlsx', 'twocol.pdf', 'onecol.pdf',
                    'image_space.docx', 'image_nospace.docx', 'scale500.xlsx', 'chunking.docx'}
        self.assertEqual(expected, {path.name for path in self.root.iterdir()})
        before = {name: (self.root / name).read_bytes() for name in expected}
        generator.generate(self.root)
        fixtures = SCRIPT.parent.parent / 'tests/DocRedock.Tests/Fixtures/Evaluation/V030'
        for name in expected:
            with self.subTest(name=name):
                self.assertEqual(before[name], (self.root / name).read_bytes())
                self.assertEqual(before[name], (fixtures / name).read_bytes())

    def test_office_packages_have_parseable_xml_and_no_external_media_or_metadata(self):
        for path in self.root.iterdir():
            if path.suffix == '.pdf':
                continue
            with self.subTest(name=path.name), zipfile.ZipFile(path) as archive:
                self.assertIsNone(archive.testzip())
                for entry in archive.infolist():
                    self.assertEqual((2026, 10, 4, 0, 0, 0), entry.date_time)
                    self.assertNotIn('docProps/', entry.filename)
                    self.assertFalse(entry.filename.endswith(('.ttf', '.otf', '.woff')))
                    if entry.filename.endswith(('.xml', '.rels')):
                        tree = ET.fromstring(archive.read(entry))
                        self.assertFalse(any(node.get('TargetMode') == 'External' for node in tree.iter()))

    def test_excel_banner_and_independent_candidates_have_their_required_gutters(self):
        for name, hidden in [('workbook.xlsx', '1'), ('workbook_base.xlsx', '0')]:
            with self.subTest(name=name):
                sheet = self.xml(name, 'xl/worksheets/sheet1.xml')
                self.assertEqual('A1:F1', sheet.find('.//' + X + 'mergeCell').get('ref'))
                cells = {cell.get('r'): ''.join(cell.itertext()) for cell in sheet.iter(X + 'c')}
                self.assertEqual('Financial summary', cells['A1'])
                self.assertEqual('Item', cells['A2'])
                self.assertEqual('Q1', cells['B2'])
                self.assertEqual('Q2', cells['D2'])
                self.assertEqual('Revenue', cells['A3'])
                self.assertEqual('Candidates', cells['H1'])
                self.assertEqual('CANDIDATE_ONE', cells['H2'])
                self.assertEqual('CANDIDATE_TWO', cells['H3'])
                self.assertFalse(any(re.match(r'[CEG]\d+$', ref) for ref in cells))
                self.assertEqual(hidden, sheet.find('.//' + X + 'col').get('hidden'))

    def test_dense_workbook_keeps_every_cell_through_F501(self):
        sheet = self.xml('scale500.xlsx', 'xl/worksheets/sheet1.xml')
        self.assertEqual(501, len(list(sheet.iter(X + 'row'))))
        cells = {cell.get('r') for cell in sheet.iter(X + 'c')}
        self.assertEqual(3006, len(cells))
        self.assertTrue({'A1', 'F1', 'A501', 'F501'} <= cells)

    def test_pdf_has_six_variable_length_lines_and_no_embedded_font(self):
        expected = ['Revenue policy.', 'Revenue is recognized', 'when delivery is complete.',
                    'Refund policy.', 'Refunds are accepted', 'within thirty days.']
        for name, columns in [('twocol.pdf', 2), ('onecol.pdf', 1)]:
            with self.subTest(name=name):
                data = (self.root / name).read_bytes()
                self.assertIn(b'/BaseFont /Helvetica', data)
                self.assertNotIn(b'/FontFile', data)
                self.assertNotIn(b'/Metadata', data)
                self.assertEqual(expected, [line.decode() for line in re.findall(rb'\(([^()]*)\) Tj', data)])
                positions = re.findall(rb'1 0 0 1 ([\d.]+) ([\d.]+) Tm', data)
                self.assertEqual(6, len(positions))
                self.assertEqual(columns, len({x for x, _ in positions}))
                xref = int(re.search(rb'startxref\n(\d+)', data).group(1))
                self.assertEqual(b'xref', data[xref:xref+4])

    def test_image_paragraph_whitespace_and_owned_solid_png(self):
        for name, caption in [('image_space.docx', 'Caption: '), ('image_nospace.docx', 'Caption:')]:
            with self.subTest(name=name):
                document = self.xml(name, 'word/document.xml')
                paragraphs = document.findall('.//' + W + 'p')
                self.assertEqual([caption, 'Editable OLD'],
                                 [''.join(p.itertext()) for p in paragraphs])
                self.assertEqual(1, len(document.findall('.//' + W + 'drawing')))
                with zipfile.ZipFile(self.root / name) as archive:
                    png = archive.read('word/media/image1.png')
                self.assertEqual(b'\x89PNG\r\n\x1a\n', png[:8])
                chunks = {}
                offset = 8
                while offset < len(png):
                    size = struct.unpack('!I', png[offset:offset+4])[0]
                    tag = png[offset+4:offset+8]
                    body = png[offset+8:offset+8+size]
                    self.assertEqual(zlib.crc32(tag+body), struct.unpack('!I', png[offset+8+size:offset+12+size])[0])
                    chunks[tag] = body
                    offset += 12 + size
                self.assertEqual({b'IHDR', b'IDAT', b'IEND'}, set(chunks))
                width, height, depth, kind, *_ = struct.unpack('!IIBBBBB', chunks[b'IHDR'])
                self.assertEqual((64, 18, 8, 2), (width, height, depth, kind))
                self.assertEqual((b'\0' + bytes([31, 119, 180]) * 64) * 18,
                                 zlib.decompress(chunks[b'IDAT']))

    def test_table_and_list_follow_a_heading_and_large_body_blocks(self):
        body = self.xml('chunking.docx', 'word/document.xml').find(W + 'body')
        section = None
        tables = lists = headings = 0
        for child in body:
            style = child.find('.//' + W + 'pStyle')
            if style is not None and style.get(W + 'val') == 'Heading1':
                section = ''.join(child.itertext())
                headings += 1
            elif child.tag == W + 'tbl':
                self.assertEqual('Section 2', section)
                tables += 1
            elif child.find('.//' + W + 'numPr') is not None:
                self.assertEqual('Section 3', section)
                self.assertGreater(len(''.join(child.itertext())), 128)
                lists += 1
        self.assertEqual((4, 1, 8), (headings, tables, lists))

    def test_invalid_output_file_fails_without_changing_it(self):
        target = self.root / 'existing-file'
        target.write_bytes(b'retain me')
        result = subprocess.run([sys.executable, str(SCRIPT), '--output', str(target)],
                                capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(b'retain me', target.read_bytes())


if __name__ == '__main__':
    unittest.main()
