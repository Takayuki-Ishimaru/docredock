"""docs/examples/ai_package_reader.py: one-call verified reading (--json, read_verified_part), search,
and output that stays safe to print and pipe whatever the package holds."""
import contextlib
import errno
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parent.parent
READER = ROOT / 'docs/examples/ai_package_reader.py'
spec = importlib.util.spec_from_file_location('ai_package_reader', READER)
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)
SAMPLES = ROOT / 'tests/DocRedock.Tests/Fixtures/AiPackage'
COST_CELLS = ['A2', 'B2', 'D2', 'F2', 'A4', 'B4', 'D4', 'F4']


class ReaderTest(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def copy_of(self, sample, name=None):
        target = self.root / (name or '%s-%d' % (sample, len(list(self.root.iterdir()))))
        shutil.copytree(SAMPLES / sample, target)
        return target

    def edit_manifest(self, package, change):
        manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        change(manifest)
        (package / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False), encoding='utf-8')

    def relisted(self, package, rel, data):
        (package / rel).write_bytes(data)
        def change(manifest):
            entry = next(item for item in manifest['files'] if item['path'] == rel)
            entry.update(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        self.edit_manifest(package, change)

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = reader.main([str(arg) for arg in args])
        return code, out.getvalue(), err.getvalue()

    def run_process(self, *args, **environment):
        # -I would ignore PYTHONIOENCODING; the reader is the repository's own file.
        return subprocess.run([sys.executable, str(READER)] + [str(arg) for arg in args], capture_output=True,
                              env=dict(os.environ, **environment), timeout=60)

    def changed_cost(self):
        package = self.copy_of('schema-2.1')
        part = package / 'parts/0003.md'
        part.write_bytes(part.read_bytes().replace(b'| 80 |', b'| 99 |'))
        return package


class OneCallJson(ReaderTest):
    def test_verify_part_json_gives_the_text_its_cells_and_the_review_state(self):
        sample = SAMPLES / 'schema-2.1'
        code, out, err = self.run_cli(sample, '--verify', '--part', 'part-0003', '--json')
        self.assertEqual((0, ''), (code, err))
        document = json.loads(out)
        self.assertIs(True, document['verified'])
        self.assertEqual({'file_name': 'workbook_base.xlsx', 'format': 'xlsx',
                          'sha256': '9589660d035b66bf1908a8577c36e32d831da5ac2510a55cdb550700c3d0885d'}, document['source'])
        self.assertEqual({'required': False, 'review_items_detected': False, 'hidden_content_included': False,
                          'file': 'review.md'}, document['review'])
        part = document['part']
        self.assertEqual((sample / 'parts/0003.md').read_text(encoding='utf-8'), part['markdown'])
        self.assertEqual(['Summary'], [location['sheet_name'] for location in part['locations']])
        self.assertEqual(('A2:F2', 'A4:F4', 'part-0002', 'part-0004'),
                         tuple(part['table_block'][key] for key in ('header_range', 'row_range', 'previous_part', 'next_part')))
        self.assertEqual(1, len(part['sources']))
        self.assertEqual(('source-0001', COST_CELLS), (part['sources'][0]['source_id'], part['sources'][0]['cell_addresses']))
        self.assertEqual(8, len(part['sources'][0]['node_ids']))
        # Q2 is the third column shown and the third cell of the row: D4, not C4.
        row = next(line for line in part['markdown'].splitlines() if line.startswith('| Cost'))
        self.assertEqual('80', row.split('|')[3].strip())
        self.assertEqual('D4', part['sources'][0]['cell_addresses'][4 + 2])

    def test_a_failed_verification_gives_the_problems_and_no_text(self):
        code, out, err = self.run_cli(self.changed_cost(), '--verify', '--part', 'part-0003', '--json')
        self.assertEqual((1, ''), (code, err))
        document = json.loads(out)
        self.assertIs(False, document['verified'])
        self.assertTrue(document['problems'][0].startswith('parts/0003.md: sha256 mismatch'), document)
        self.assertNotIn('part', document)
        self.assertNotIn('| 99 |', out)

    def test_review_required_with_fail_on_review_gives_the_state_and_no_text(self):
        package = self.copy_of('schema-2.1')
        report = json.loads((package / 'report.json').read_text(encoding='utf-8'))
        report['review']['required'] = True
        self.relisted(package, 'report.json', json.dumps(report).encode('utf-8'))
        code, out, err = self.run_cli(package, '--verify', '--part', 'part-0003', '--fail-on-review', '--json')
        self.assertEqual(3, code)
        self.assertIn('review required: read review.md', err)
        document = json.loads(out)
        self.assertIs(True, document['review']['required'])
        self.assertNotIn('part', document)
        # Without --fail-on-review the text comes with the state, for the caller to decide.
        code, out, err = self.run_cli(package, '--verify', '--part', 'part-0003', '--json')
        self.assertEqual((0, ''), (code, err))
        self.assertIs(True, json.loads(out)['review']['required'])

    def test_a_package_that_cannot_be_read_prints_nothing_on_stdout(self):
        package = self.copy_of('schema-2.1')
        self.edit_manifest(package, lambda manifest: manifest['parts'][1].update(id='part-0003'))
        code, out, err = self.run_cli(package, '--part', 'part-0003', '--json')
        self.assertEqual((2, ''), (code, out))
        self.assertIn("parts list the id 'part-0003' twice", err)

    def test_unverified_json_says_so(self):
        code, out, err = self.run_cli(SAMPLES / 'schema-2.2', '--json')
        self.assertEqual((0, ''), (code, err))
        document = json.loads(out)
        self.assertIs(False, document['verified'])
        self.assertEqual(['part-0001', 'part-0002', 'part-0003'], [part['id'] for part in document['parts']])
        self.assertEqual('A3:A4', document['parts'][1]['table_block']['context_range'])
        self.assertNotIn('markdown', document['parts'][0])

    def test_schema_1_parts_carry_their_inline_sources(self):
        code, out, err = self.run_cli(SAMPLES / 'schema-1.0', '--verify', '--part', 'part-0001', '--json')
        self.assertEqual((0, ''), (code, err))
        document = json.loads(out)
        self.assertEqual(['source-0001', 'source-0002'], [group['source_id'] for group in document['part']['sources']])
        self.assertIsNone(document['review']['review_items_detected'])  # report 1.0 predates the flag

    def test_json_escapes_characters_that_would_reorder_or_break_the_text(self):
        package = self.copy_of('schema-2.1')
        self.edit_manifest(package, lambda manifest: manifest['locations'][0].update(label='sheet ‮yrammuS\x85\x1b[2J'))
        code, out, err = self.run_cli(package, '--json')
        self.assertEqual((0, ''), (code, err))
        for raw in ('‮', '\x85', '\x1b'):
            self.assertNotIn(raw, out)
        self.assertIn('\\u202e', out)
        self.assertEqual('sheet ‮yrammuS\x85\x1b[2J', json.loads(out)['parts'][0]['locations'][0]['label'])


class VerifiedPartApi(ReaderTest):
    def test_read_verified_part_returns_the_verified_text_with_its_sources(self):
        with reader.open_package(SAMPLES / 'schema-2.1') as package:
            manifest = reader.load_manifest(package)
            result = reader.read_verified_part(package, manifest, 'part-0003')
        self.assertEqual((SAMPLES / 'schema-2.1/parts/0003.md').read_text(encoding='utf-8'), result['markdown'])
        self.assertEqual(COST_CELLS, result['sources'][0]['cell_addresses'])
        self.assertEqual('part-0003', result['part']['id'])
        self.assertIs(False, result['review']['required'])

    def test_problems_unknown_parts_and_malformed_packages_raise(self):
        with reader.open_package(self.changed_cost()) as package:
            manifest = reader.load_manifest(package)
            with self.assertRaises(reader.PackageVerificationError) as raised:
                reader.read_verified_part(package, manifest, 'part-0003')
            self.assertEqual(1, len(raised.exception.problems))
            self.assertTrue(raised.exception.problems[0].startswith('parts/0003.md: sha256 mismatch'))
            with self.assertRaises(KeyError):
                reader.read_verified_part(package, manifest, 'part-9999')
        package = self.copy_of('schema-2.1')
        index = json.loads((package / 'source-index.json').read_text(encoding='utf-8'))
        index['parts'][2]['sources'][0]['cell_addresses'] = ['D4']
        self.relisted(package, 'source-index.json', json.dumps(index).encode('utf-8'))
        with reader.open_package(package) as opened, self.assertRaises(reader.PackageError):
            reader.read_verified_part(opened, reader.load_manifest(opened), 'part-0003')

    def test_read_review_gives_the_summary_flags_or_none(self):
        for sample, detected in [('schema-1.0', None), ('schema-2.1', False)]:
            with reader.open_package(SAMPLES / sample) as package:
                review = reader.read_review(package, reader.load_manifest(package))
            self.assertEqual({'required': False, 'review_items_detected': detected, 'hidden_content_included': False},
                             review)


class Search(ReaderTest):
    def test_search_ignores_case_full_width_forms_and_markdown_escapes(self):
        sample = SAMPLES / 'schema-2.1'
        lines = (sample / 'parts/0003.md').read_text(encoding='utf-8').splitlines()
        number = next(i for i, line in enumerate(lines, 1) if line.startswith('| Cost'))
        for text in ('cost', 'ＣＯＳＴ', 'Cost | 70'):
            with self.subTest(text=text):
                code, out, err = self.run_cli(sample, '--search', text)
                self.assertEqual((0, ''), (code, err))
                self.assertEqual(['part-0003 parts/0003.md tokens=54 sheet Summary block 2/3 rows A4:F4',
                                  '  %d: | Cost | 70 | 80 | normal |' % number], out.splitlines())
        code, out, err = self.run_cli(sample, '--search', 'candidate_one')  # written CANDIDATE\_ONE
        self.assertEqual(['part-0005'], [line.split()[0] for line in out.splitlines() if not line.startswith(' ')])
        code, out, err = self.run_cli(sample, '--search', 'workbook_base', '--json')
        self.assertEqual(['part-%04d' % i for i in range(1, 7)], [part['id'] for part in json.loads(out)['search']['parts']])
        self.assertEqual([{'number': 2, 'text': 'ソース: workbook\\_base.xlsx'}], json.loads(out)['search']['parts'][0]['lines'])

    def test_searchable_drops_inline_markup_but_keeps_escaped_characters_and_references(self):
        # Underline, strikethrough and bold marks do not stand between the words of a line.
        for marked in ('これは<u>重要</u>な点', 'これは~~重要~~な点', 'これは**重要**な点', 'これは***重要***な点',
                       'これは<u>~~**重要**~~</u>な点', '<u>これは</u><u>重要</u>な点'):
            with self.subTest(marked=marked):
                self.assertEqual('これは重要な点', reader._searchable(marked))
        # An escaped delimiter and a character reference are the characters they stand for, not markup.
        self.assertEqual('a~~b', reader._searchable(r'a\~\~b'))
        self.assertEqual('a**b', reader._searchable(r'a\*\*b'))
        self.assertEqual('a<u>b</u>', reader._searchable('a&lt;u&gt;b&lt;/u&gt;'))
        self.assertEqual('a<u>b', reader._searchable(r'a\<u>b'))
        self.assertEqual('a\\b', reader._searchable(r'a\\b'))     # an escaped backslash
        self.assertEqual('a\\', reader._searchable(r'a\\**'))     # and bold right after it
        self.assertEqual('a**b', reader._searchable(r'a\**b'))     # an escaped asterisk, then a single one: text
        # A single tilde or asterisk, and any other tag, are text.
        self.assertEqual('2 ~ 3 * 4 <b>x</b>', reader._searchable('2 ~ 3 * 4 <b>x</b>'))

    def test_search_finds_text_across_inline_markup_and_what_was_written_as_text(self):
        package = self.copy_of('schema-2.1')
        added = ('\n\nこれは<u>重要</u>な点です。\n\n'
                 r'literal \~\~kept\~\~ and **bold** &lt;u&gt;typed&lt;/u&gt;' + '\n')
        self.relisted(package, 'parts/0003.md', (package / 'parts/0003.md').read_bytes() + added.encode('utf-8'))
        lines = (package / 'parts/0003.md').read_text(encoding='utf-8').splitlines()
        underlined = next(i for i, line in enumerate(lines, 1) if line.startswith('これは'))
        typed = next(i for i, line in enumerate(lines, 1) if line.startswith('literal'))
        for query, number in (('重要な点', underlined), ('これは重要な点です', underlined),   # as it reads
                              ('これは<u>重要</u>な点', underlined), ('**bold**', typed),    # as it is written
                              ('~~kept~~', typed), ('<u>typed</u>', typed), ('literal ~~kept~~ and bold', typed)):
            with self.subTest(query=query):
                code, out, err = self.run_cli(package, '--search', query)
                self.assertEqual((0, ''), (code, err))
                self.assertEqual(['part-0003'], [line.split()[0] for line in out.splitlines() if not line.startswith(' ')])
                self.assertEqual(['  %d: %s' % (number, lines[number - 1])], [line for line in out.splitlines() if line.startswith(' ')])
        code, out, err = self.run_cli(package, '--search', '重要な点', '--json')
        self.assertEqual([{'number': underlined, 'text': 'これは<u>重要</u>な点です。'}], json.loads(out)['search']['parts'][0]['lines'])
        # Marks are not the text: nothing reads "重要 な点" or "重要~~な点".
        for query in ('重要 な点', '重要~~な点'):
            code, out, err = self.run_cli(package, '--search', query)
            self.assertEqual((0, ''), (code, out))
            self.assertIn('no part contains', err)

    def test_search_with_verify_refuses_a_changed_package_before_printing(self):
        code, out, err = self.run_cli(self.changed_cost(), '--verify', '--search', 'Cost')
        self.assertEqual((1, ''), (code, out))
        self.assertIn('parts/0003.md: sha256 mismatch', err)

    def test_no_match_and_bad_combinations(self):
        code, out, err = self.run_cli(SAMPLES / 'schema-2.1', '--search', 'zzz-not-there')
        self.assertEqual((0, '', "no part contains 'zzz-not-there'\n"), (code, out, err))
        for args in (['--search', 'Cost', '--part', 'part-0001'], ['--search', '  ']):
            with self.subTest(args=args):
                result = self.run_process(SAMPLES / 'schema-2.1', *args)
                self.assertEqual((2, b''), (result.returncode, result.stdout))
                self.assertIn(b'usage:', result.stderr)

    def test_long_lines_are_cut_around_the_match(self):
        line = 'x' * 300 + 'needle' + 'y' * 300
        excerpt = reader._excerpt(line, 'NEEDLE')
        self.assertIn('needle', excerpt)
        self.assertTrue(excerpt.startswith('...') and excerpt.endswith('...'))
        self.assertLessEqual(len(excerpt), 166)


class SafeOutput(ReaderTest):
    def test_labels_and_ids_cannot_add_lines_or_escape_sequences(self):
        package = self.copy_of('schema-2.1')
        def change(manifest):
            manifest['locations'][0]['label'] = 'sheet \x1b[31mRED\x1b[0m\nfake-part parts/9999.md'
            manifest['source']['file_name'] = 'a\x1b]0;title\x07.xlsx'
        self.edit_manifest(package, change)
        code, out, err = self.run_cli(package)
        self.assertEqual((0, ''), (code, err))
        self.assertEqual(8, len(out.splitlines()))
        self.assertNotIn('\x1b', out)
        self.assertIn('source a\\x1b]0;title\\x07.xlsx', out)
        # Messages naming a value from the package stay one line.
        self.edit_manifest(package, lambda manifest: manifest['parts'][0].update(id='part\n0001\x1b[2J', source_ids=['nowhere']))
        code, out, err = self.run_cli(package)
        self.assertEqual((2, ''), (code, out))
        self.assertEqual(1, len(err.splitlines()), err)
        self.assertNotIn('\x1b', err)

    @unittest.skipIf(os.name == 'nt', 'Windows file names cannot hold control characters')
    def test_names_of_unlisted_files_are_shown_escaped(self):
        package = self.copy_of('schema-2.1')
        (package / 'parts' / 'x\x1b[2Jfiles ok\n.md').write_bytes(b'planted')
        code, out, err = self.run_cli(package, '--verify')
        self.assertEqual((1, ''), (code, err))
        self.assertEqual(['parts/x\\x1b[2Jfiles ok\\x0a.md: not listed in the manifest'], out.splitlines())

    def test_a_closed_pipe_ends_the_reader_quietly(self):
        package = self.copy_of('schema-2.1')
        big = ('# Big\n' + ''.join('| row %06d | value |\n' % i for i in range(200000))).encode('utf-8')
        self.relisted(package, 'parts/0003.md', big)
        process = subprocess.Popen([sys.executable, '-I', str(READER), str(package), '--part', 'part-0003'],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        process.stdout.read(16)
        process.stdout.close()
        error = process.stderr.read()
        process.stderr.close()
        self.assertEqual(141, process.wait(timeout=60))
        self.assertEqual(b'', error)

    def test_windows_reports_a_closed_pipe_as_einval_and_it_counts_as_closed(self):
        def closed():
            raise OSError(errno.EINVAL, 'Invalid argument')
        with mock.patch.object(reader.os, 'name', 'nt'):
            self.assertRaises(BrokenPipeError, reader._to_stdout, closed)
        # Elsewhere EINVAL is an ordinary error, never mistaken for a closed pipe.
        with mock.patch.object(reader.os, 'name', 'posix'), self.assertRaises(OSError) as raised:
            reader._to_stdout(closed)
        self.assertNotIsInstance(raised.exception, BrokenPipeError)

    def test_a_console_that_cannot_show_a_label_gets_escapes(self):
        result = self.run_process(SAMPLES / 'schema-1.0', PYTHONIOENCODING='ascii')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(b'headings: AI\\u30d1', result.stdout)
        self.assertEqual(b'', result.stderr)

    def test_a_bug_in_the_reader_is_not_reported_as_an_unreadable_package(self):
        with mock.patch.object(reader, 'load_manifest', side_effect=ValueError('a bug')):
            with self.assertRaises(ValueError):
                self.run_cli(SAMPLES / 'schema-2.1')


class Zip64Records(ReaderTest):
    def test_zip64_end_records_must_be_where_their_locator_points(self):
        plain = self.root / 'plain.zip'
        with zipfile.ZipFile(plain, 'w', zipfile.ZIP_DEFLATED) as archive:
            for path in sorted((SAMPLES / 'schema-2.1').rglob('*')):
                if path.is_file():
                    archive.write(path, path.relative_to(SAMPLES / 'schema-2.1').as_posix())
        data = plain.read_bytes()
        end = len(data) - 22
        _, _, _, count, total, size, offset, _ = struct.unpack('<4sHHHHIIH', data[end:])
        record = struct.pack('<4sQHHIIQQQQ', b'PK\x06\x06', 44, 45, 45, 0, 0, count, total, size, offset)
        end_record = struct.pack('<4sHHHHIIH', b'PK\x05\x06', 0, 0, 0xFFFF, 0xFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0)
        cases = {
            'where it belongs': (end, (0, 'files ok\n')),
            'past the record': (2 ** 64 - 1, "Zip64 end record locator points past the record"),
            'far past the record': (2 ** 63 - 1, "Zip64 end record locator points past the record"),
            'at the start': (0, "Zip64 end record is not where its locator points"),
        }
        for name, (pointer, expected) in cases.items():
            with self.subTest(name=name):
                archive = self.root / ('zip64-%d.zip' % len(list(self.root.iterdir())))
                locator = struct.pack('<4sIQI', b'PK\x06\x07', 0, pointer, 1)
                archive.write_bytes(data[:end] + record + locator + end_record)
                code, out, err = self.run_cli(archive, '--verify')
                if isinstance(expected, tuple):
                    self.assertEqual(expected, (code, out), err)
                else:
                    self.assertEqual((2, ''), (code, out))
                    self.assertIn(expected, err)
        archive = self.root / 'zip64-size.zip'
        longer = struct.pack('<4sQHHIIQQQQ', b'PK\x06\x06', 45, 45, 45, 0, 0, count, total, size, offset)
        archive.write_bytes(data[:end] + longer + struct.pack('<4sIQI', b'PK\x06\x07', 0, end, 1) + end_record)
        code, out, err = self.run_cli(archive, '--verify')
        self.assertEqual((2, ''), (code, out))
        self.assertIn('Zip64 end record does not end where its locator begins', err)


if __name__ == '__main__':
    unittest.main()
