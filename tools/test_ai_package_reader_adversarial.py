"""Adversarial contracts for docs/examples/ai_package_reader.py.

Covers what tools/test_ai_package_reader_example.py leaves out: unusual JSON, ZIP
oddities, directory-package surprises, id and path edge cases, unknown fields, integer
boundaries and the exit code / stdout / stderr contract of main for each case.
"""
import contextlib
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
import warnings
import zipfile
import zlib

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('ai_package_reader', ROOT / 'docs/examples/ai_package_reader.py')
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)

SHA_EMPTY = hashlib.sha256(b'').hexdigest()


def v1_manifest():
    def location(extra):
        base = {'partition_id': 'part-0001', 'label': 'headings: A', 'page_number': 1,
                'slide_number': None, 'sheet_name': None, 'heading_path': None}
        base.update(extra)
        return base
    return {
        'schema_version': '1.0',
        'source': {'file_name': 'doc.docx', 'sha256': SHA_EMPTY, 'format': 'docx'},
        'content_policy': 'visible',
        'parts': [{'id': 'part-0001', 'path': 'parts/0001.md', 'node_ids': ['n1', 'n2'],
                   'sources': [location({'node_id': 'n1'}), location({'cell_address': 'B2'})]}],
        'files': []}


def v2_manifest():
    return {
        'schema_version': '2.1',
        'source': {'file_name': 'book.xlsx', 'sha256': SHA_EMPTY, 'format': 'xlsx'},
        'content_policy': 'visible',
        'table_row_blocks': True,
        'source_index': 'source-index.json',
        'locations': [
            {'id': 'source-0001', 'partition_id': 'sheet-S', 'label': 'sheet S', 'page_number': None,
             'slide_number': None, 'sheet_name': 'S', 'heading_path': None},
            {'id': 'source-0002', 'partition_id': 'slide-1', 'label': 'slide 1', 'page_number': None,
             'slide_number': 1, 'sheet_name': None, 'heading_path': ['A']}],
        'parts': [
            {'id': 'part-0001', 'path': 'parts/0001.md', 'node_count': 2, 'source_ids': ['source-0001'],
             'exceeds_target': False, 'estimated_tokens': 0,
             'table_block': {'table_id': 't1', 'index': 1, 'count': 1, 'header_range': 'A1:A1',
                             'context_range': 'A1', 'row_range': 'A2:A2', 'previous_part': None,
                             'next_part': None}},
            {'id': 'part-0002', 'path': 'parts/0002.md', 'node_count': 2,
             'source_ids': ['source-0001', 'source-0002']}],
        'files': []}


def v2_index():
    return {'schema_version': '1.0', 'parts': [
        {'id': 'part-0001', 'sources': [{'source_id': 'source-0001', 'node_ids': ['n1', 'n2'],
                                         'cell_addresses': ['A2', None]}]},
        {'id': 'part-0002', 'sources': [{'source_id': 'source-0001', 'node_ids': ['n3']},
                                        {'source_id': 'source-0002', 'node_ids': ['n4']}]}]}


def report_text(required=False):
    return json.dumps({'schema_version': '1.0', 'review': {'required': required}})


def body_files(schema):
    files = {'document.md': b'# doc\n', 'review.md': b'# review\n', 'parts/0001.md': b'# one\n',
             'parts/0002.md': b'# two\n', 'report.json': report_text().encode('utf-8')}
    if schema == '2.x':
        files['source-index.json'] = json.dumps(v2_index()).encode('utf-8')
    return files


def manifest_text(manifest, files):
    manifest['files'] = [{'path': path, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                         for path, data in sorted(files.items())]
    return json.dumps(manifest).encode('utf-8')


class PackageFixture(unittest.TestCase):
    """Helpers: build valid minimal packages in a temp folder, zip them, run the CLI."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.counter = 0

    def unique(self, name):
        self.counter += 1
        return self.root / ('%s-%d' % (name, self.counter))

    def build(self, schema='2.x', name='pkg'):
        target = self.unique(name)
        target.mkdir(parents=True)
        manifest = v1_manifest() if schema == '1.x' else v2_manifest()
        files = body_files(schema)
        (target / 'manifest.json').write_bytes(manifest_text(manifest, files))
        for rel, data in files.items():
            path = target / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        return target

    def manifest(self, package):
        return json.loads((package / 'manifest.json').read_text(encoding='utf-8'))

    def patch_manifest(self, package, change):
        raw = self.manifest(package)
        change(raw)
        (package / 'manifest.json').write_text(json.dumps(raw), encoding='utf-8')
        return package

    def relist(self, package, rel, data):
        """Write one package file and give the manifest its new size and hash."""
        path = package / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        def change(raw, digest=hashlib.sha256(data).hexdigest()):
            entry = next(item for item in raw['files'] if item['path'] == rel)
            entry.update(bytes=len(data), sha256=digest)
        return self.patch_manifest(package, change)

    def entries_of(self, directory):
        return [(path.relative_to(directory).as_posix(), path.read_bytes())
                for path in sorted(directory.rglob('*')) if path.is_file()]

    def write_zip(self, entries, name='pkg.zip'):
        """Write (name, bytes) entries in order; duplicates and odd names are kept."""
        target = self.unique(name)
        with warnings.catch_warnings():
            # Deliberate duplicate names are what the test is about (C3).
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(target, 'w', zipfile.ZIP_STORED) as archive:
                for entry_name, data in entries:
                    archive.writestr(entry_name, data)
        return target

    def zipped(self, directory, name='pkg.zip'):
        return self.write_zip(self.entries_of(directory), name)

    def load(self, path, error=None):
        with reader.open_package(path) as package:
            if error is None:
                return reader.load_manifest(package)
            with self.assertRaises(error) as raised:
                reader.load_manifest(package)
            return raised.exception

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = reader.main([str(arg) for arg in args])
        return code, out.getvalue(), err.getvalue()

    def assert_refused(self, path, *args, says=None):
        """Exit 2, empty stdout, exactly one stderr line and no traceback."""
        code, out, err = self.run_cli(path, *args)
        self.assertEqual((2, ''), (code, out), err)
        self.assertEqual(1, len(err.strip().splitlines()), err)
        self.assertNotIn('Traceback', err)
        if says is not None:
            self.assertIn(says, err)
        return err

    def assert_problems(self, path, expected, *args):
        """verify_files and the CLI agree on the listed-file problems (C4, C7)."""
        with reader.open_package(path) as package:
            problems = reader.verify_files(package, reader.load_manifest(package))
        self.assertEqual(sorted(expected), sorted(problems), problems)
        for problem in problems:
            self.assertTrue(problem.startswith(problem.split(':', 1)[0] + ':'), problem)
        code, out, err = self.run_cli(path, '--verify')
        self.assertEqual(1, code, (code, out, err))
        self.assertEqual(sorted(problems), sorted(out.splitlines()), (problems, out))
        self.assertEqual('', err)
        return problems

    def package_rejected(self, path, data):
        (path / 'manifest.json').write_bytes(data)
        return path


class TestOddJson(PackageFixture):
    """C2: a manifest that is not clean JSON of the right shape is a PackageError."""

    def assert_rejects_manifest(self, data, error=reader.PackageError):
        path = self.package_rejected(self.build(), data)
        raised = self.load(path, error)
        self.assertIsInstance(raised, ValueError)
        self.assert_refused(path)
        self.assert_refused(path, '--verify')
        return raised

    def test_invalid_utf8_and_a_bom_are_refused(self):
        valid = (self.build() / 'manifest.json').read_bytes()
        cases = {
            'invalid UTF-8 byte': b'\xff\xfe' + valid,
            'lone continuation byte': valid[:10] + b'\x80' + valid[10:],
            'truncated multi-byte character': valid + b'\xe3\x81',
            'UTF-8 BOM': b'\xef\xbb\xbf' + valid,
            'UTF-16 encoded': valid.decode('utf-8').encode('utf-16'),
        }
        for name, data in cases.items():
            with self.subTest(case=name):
                self.assert_rejects_manifest(data)

    def test_a_top_level_array_or_scalar_is_refused(self):
        for name, data in [('array', b'[]'), ('array of objects', b'[{}]'), ('string', b'"1.0"'),
                           ('number', b'2.1'), ('null', b'null'), ('true', b'true'),
                           ('empty file', b''), ('whitespace only', b'  \n')]:
            with self.subTest(case=name):
                self.assert_rejects_manifest(data)

    def test_truncated_json_is_refused(self):
        valid = (self.build() / 'manifest.json').read_bytes()
        for name, data in [('cut in half', valid[:len(valid) // 2]), ('missing brace', valid[:-1]),
                           ('trailing comma', b'{"schema_version": "2.1",}'),
                           ('two objects', valid + valid), ('single quotes', b"{'schema_version': '2.1'}")]:
            with self.subTest(case=name):
                self.assert_rejects_manifest(data)

    def test_nan_infinity_and_json5_literals_are_refused(self):
        for literal in ('NaN', 'Infinity', '-Infinity', 'nan', 'undefined'):
            data = b'{"schema_version": "2.1", "bytes": ' + literal.encode('utf-8') + b'}'
            with self.subTest(literal=literal):
                self.assertIn('manifest.json', str(self.assert_rejects_manifest(data)))

    def test_a_repeated_key_is_refused_at_every_level(self):
        for name, text in [
                ('top level', b'{"schema_version": "2.1", "schema_version": "1.0"}'),
                ('in source', b'{"source": {"file_name": "a", "file_name": "b"}}'),
                ('in a files entry', b'{"files": [{"path": "a", "path": "b"}]}'),
                ('in a part', b'{"parts": [{"id": "p", "id": "q"}]}'),
                ('inside a nested unknown field', b'{"x": {"y": {"z": 1, "z": 2}}}'),
                ('duplicate known field', b'{"content_policy": "a", "content_policy": "b"}')]:
            with self.subTest(case=name):
                self.assert_rejects_manifest(text)

    def test_deeply_nested_json_is_refused_without_a_traceback(self):
        for name, data in [('arrays', b'[' * 200000), ('objects', b'{"a": ' * 200000)]:
            with self.subTest(case=name):
                self.assert_rejects_manifest(data)

    def test_huge_numbers_in_the_manifest_are_refused(self):
        cases = {
            'bytes above 2**63': lambda m: m['files'][0].update(bytes=2 ** 63),
            'bytes far above 2**63': lambda m: m['files'][0].update(bytes=10 ** 400),
            'bytes with a fraction': lambda m: m['files'][0].update(bytes=1.0),
            'bytes with an exponent': lambda m: m['files'][0].update(bytes=1e30),
            'bytes negative one': lambda m: m['files'][0].update(bytes=-1),
            'source sha256 a huge number': lambda m: m['source'].update(sha256=10 ** 400),
        }
        for name, change in cases.items():
            with self.subTest(case=name):
                path = self.patch_manifest(self.build(), change)
                self.load(path, reader.PackageError)
                self.assert_refused(path, '--verify')


class TestZipOddities(PackageFixture):
    """C3/C4/C7: archives a writer could produce but a reader must not trust."""

    def test_an_archive_that_is_not_a_zip_is_refused(self):
        for name, data in [('text', b'not a zip at all\n'), ('empty file', b''),
                           ('a zip prefix cut short', b'PK\x03\x04' + b'\0' * 20)]:
            path = self.unique('not-zip-%s' % name.replace(' ', '_'))
            path.write_bytes(data)
            with self.subTest(case=name):
                self.assertRaises(reader.PackageError, reader.open_package, path)
                self.assert_refused(path, '--verify')

    def test_a_truncated_archive_is_refused_at_open(self):
        archive = self.zipped(self.build())
        data = archive.read_bytes()
        for name, cut in [('half the file', len(data) // 2), ('all but the first byte', 1),
                          ('all but the magic', 4)]:
            path = self.unique('truncated')
            path.write_bytes(data[:len(data) - cut])
            with self.subTest(case=name):
                self.assertRaises(reader.PackageError, reader.open_package, path)
                self.assert_refused(path, '--verify')

    def test_a_directory_entry_named_like_a_listed_file_is_not_read(self):
        # A listed path that only exists as a directory entry in the archive is not a file.
        path = self.build()
        manifest = self.load(path)
        entries = [(name, data) for name, data in self.entries_of(path) if name != 'parts/0001.md']
        archive = self.write_zip(entries + [('parts/0001.md/', b'')])
        with reader.open_package(archive) as package:
            self.assertRaises(FileNotFoundError, package.read_bytes, 'parts/0001.md')
            self.assertRaises(FileNotFoundError, package.size, 'parts/0001.md')
            self.assert_problems(archive, ['parts/0001.md: missing'])
        # Without --verify a missing part file is an unreadable package (C7).
        self.assert_refused(archive, '--part', 'part-0001', says='parts/0001.md')
        self.assertEqual(manifest['schema_version'], self.load(archive)['schema_version'])
        # An unrelated directory entry changes nothing.
        archive = self.write_zip(self.entries_of(path) + [('assets/', b''), ('nested/', b'')])
        self.assertEqual((0, 'files ok\n'), self.run_cli(archive, '--verify')[:2])

    def test_an_entry_name_with_a_backslash_or_nul_is_refused_at_open(self):
        # Python on Windows reads "parts\\0003.md" as "parts/0003.md" and cuts a name at NUL; .NET does
        # neither, so readers would see different packages.
        path = self.build()
        for odd in ('parts\\0003.md', 'parts/0001.md\0', 'manifest.json\0x'):
            with self.subTest(name=odd):
                archive = self.write_zip(self.entries_of(path) + [(odd, b'# odd\n')])
                self.assertRaises(reader.PackageError, reader.open_package, archive)
                self.assert_refused(archive, '--verify')
        with reader.open_package(self.zipped(path)) as package:
            for name in ('parts\\0003.md', '..\\escape.md', 'a\\..\\b', 'C:\\x'):
                with self.subTest(name=name):
                    self.assertRaises(reader.PackageError, package.read_bytes, name)
                    self.assertRaises(reader.PackageError, package.size, name)

    def test_names_without_the_utf8_flag_are_read_as_other_readers_read_them(self):
        path = self.build()
        (path / 'assets').mkdir()
        self.relist(path, 'document.md', b'# doc\n')
        data = b'# \xe7\x94\xbb\xe5\x83\x8f\n'
        manifest = self.manifest(path)
        manifest['files'].append({'path': 'assets/画像.md', 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        (path / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        (path / 'assets/画像.md').write_bytes(data)
        archive = bytearray(self.zipped(path).read_bytes())
        # Clear the UTF-8 flag of every entry, as macOS ditto writes them.
        offset = 0
        while True:
            local = archive.find(b'PK\x03\x04', offset)
            if local < 0:
                break
            struct.pack_into('<H', archive, local + 6, struct.unpack_from('<H', archive, local + 6)[0] & ~0x800)
            offset = local + 4
        offset = 0
        while True:
            central = archive.find(b'PK\x01\x02', offset)
            if central < 0:
                break
            struct.pack_into('<H', archive, central + 8, struct.unpack_from('<H', archive, central + 8)[0] & ~0x800)
            offset = central + 4
        target = self.unique('noflag.zip')
        target.write_bytes(bytes(archive))
        self.assertEqual((0, 'files ok\n'), self.run_cli(target, '--verify')[:2])

    def read_from(self, archive, name):
        with reader.open_package(archive) as package:
            return package.read_bytes(name)

    def test_the_encryption_flag_bit_is_refused(self):
        path = self.build()
        listed = self.load(path)['files'][0]['path']
        archive = self.zipped(path)
        data = bytearray(archive.read_bytes())
        central = data.index(b'PK\x01\x02')
        local = struct.unpack_from('<I', data, central + 42)[0]
        self.assertEqual(b'PK\x03\x04', bytes(data[local:local + 4]))
        struct.pack_into('<H', data, local + 6, 0x0001)
        struct.pack_into('<H', data, central + 8, 0x0001)
        target = self.unique('encrypted.zip')
        target.write_bytes(bytes(data))
        with reader.open_package(target) as package:
            self.assertRaises(reader.PackageError, package.read_bytes, listed)
            problems = reader.verify_files(package, reader.load_manifest(package))
        self.assertEqual(1, len(problems), problems)
        self.assertTrue(problems[0].startswith(listed + ': refused ('), problems)
        code, out, err = self.run_cli(target, '--verify')
        self.assertEqual(1, code, (code, out, err))
        self.assertIn(': refused (', out)
        self.assertEqual('', err)
        code, out, err = self.run_cli(target, '--verify', '--part', 'part-0001')
        self.assertEqual((1, ''), (code, out), (code, out, err))
        self.assertIn(': refused (', err)
        self.assertEqual(1, len(err.strip().splitlines()), err)
        self.assertNotIn('Traceback', err)

    def test_two_entries_with_the_same_name_are_refused_at_open(self):
        path = self.build()
        entries = self.entries_of(path)
        for name, data in [('document.md', b'# twice\n'), ('parts/0001.md', b'# twice\n'),
                           ('manifest.json', b'{}')]:
            archive = self.write_zip(entries + [(name, data)])
            with self.subTest(name=name):
                self.assertRaises(reader.PackageError, reader.open_package, archive)
                self.assert_refused(archive, '--verify')

    def test_a_leading_slash_in_an_entry_name_is_not_the_manifest(self):
        path = self.build()
        manifest_bytes = (path / 'manifest.json').read_bytes()
        entries = [(name, data) for name, data in self.entries_of(path) if name != 'manifest.json']
        archive = self.write_zip([('/manifest.json', manifest_bytes)] + entries)
        with self.subTest(case='manifest only with a leading slash'):
            self.load(archive, reader.UnsupportedSchemaError)
            self.assert_refused(archive, '--verify')
            self.assert_refused(archive)
        archive = self.write_zip(entries + [('manifest.json', manifest_bytes),
                                             ('/manifest.json', manifest_bytes)])
        with self.subTest(case='top level manifest plus the odd entry'):
            self.assertEqual('2.1', self.load(archive)['schema_version'])
            # Read as a file of its own, the odd entry is one the manifest does not list.
            self.assertEqual((1, '/manifest.json: not listed in the manifest\n'), self.run_cli(archive, '--verify')[:2])


class TestPackageShapes(PackageFixture):
    """C2/C3/C5/C6/C7: directory packages, integer boundaries, unknown fields, ids and the CLI matrix."""

    def test_a_folder_where_a_listed_file_belongs_is_missing(self):
        path = self.build()
        (path / 'parts/0001.md').unlink()
        (path / 'parts/0001.md').mkdir()
        self.assert_problems(path, ['parts/0001.md: missing'])
        self.assert_refused(path, '--part', 'part-0001', says='parts/0001.md')

    def test_a_linked_folder_inside_a_package_is_refused(self):
        path = self.build()
        outside = self.unique('outside')
        shutil.copytree(path / 'parts', outside)
        shutil.rmtree(path / 'parts')
        try:
            (path / 'parts').symlink_to(outside, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest('symbolic links are unavailable')
        with reader.open_package(path) as package:
            self.assertRaises(reader.PackageError, package.read_bytes, 'parts/0001.md')
            problems = reader.verify_files(package, reader.load_manifest(package))
        self.assertEqual(['parts/0001.md', 'parts/0002.md'], sorted(problem.split(':')[0] for problem in problems))
        self.assertTrue(all(': refused (' in problem for problem in problems), problems)
        self.assert_refused(path, '--part', 'part-0001', says='link')
        self.assertEqual(1, self.run_cli(path, '--verify')[0])

    def test_integer_boundaries(self):
        accepted = {
            'bytes 0': lambda m: m['files'][0].update(bytes=0),
            'bytes 2**63-1': lambda m: m['files'][0].update(bytes=2 ** 63 - 1),
            'node_count 0': lambda m: m['parts'][0].update(node_count=0),
            'node_count 2**31-1': lambda m: m['parts'][0].update(node_count=2 ** 31 - 1),
            'page_number 2**31-1': lambda m: m['locations'][0].update(page_number=2 ** 31 - 1),
            'estimated_tokens 2**31-1': lambda m: m['parts'][0].update(estimated_tokens=2 ** 31 - 1),
        }
        refused = {
            'bytes 2**63': lambda m: m['files'][0].update(bytes=2 ** 63),
            'node_count 2**31': lambda m: m['parts'][0].update(node_count=2 ** 31),
            'page_number 2**31': lambda m: m['locations'][0].update(page_number=2 ** 31),
            'slide_number -1': lambda m: m['locations'][1].update(slide_number=-1),
            'estimated_tokens 2**31': lambda m: m['parts'][0].update(estimated_tokens=2 ** 31),
            'table_block index 0': lambda m: m['parts'][0]['table_block'].update(index=0),
            'table_block count 2**31': lambda m: m['parts'][0]['table_block'].update(count=2 ** 31),
            'target_characters a boolean': lambda m: m.update(target_characters=True),
        }
        for name, change in accepted.items():
            with self.subTest(accepted=name):
                self.load(self.patch_manifest(self.build(), change))
        for name, change in refused.items():
            with self.subTest(refused=name):
                path = self.patch_manifest(self.build(), change)
                self.load(path, reader.PackageError)
                self.assert_refused(path)
        # A block count of 2**31-1 is a valid number, refused only because one block cannot make it up.
        path = self.patch_manifest(self.build(), lambda m: m['parts'][0]['table_block'].update(count=2 ** 31 - 1))
        self.assertEqual("part part-0001 says table 't1' has 2147483647 blocks, but the manifest lists 1",
                         str(self.load(path, reader.PackageError)))

    def test_unknown_fields_at_every_level_are_ignored(self):
        path = self.build()
        def manifest(m):
            for target in (m, m['source'], m['files'][0], m['locations'][0], m['parts'][0], m['parts'][0]['table_block']):
                target['future_field'] = {'nested': [1, 'two', None]}
        self.patch_manifest(path, manifest)
        index = v2_index()
        index['future'] = 1
        index['parts'][0]['future'] = True
        index['parts'][0]['sources'][0]['future'] = 'x'
        self.relist(path, 'source-index.json', json.dumps(index).encode('utf-8'))
        report = json.loads(report_text())
        report['future'] = []
        report['review']['future'] = {}
        self.relist(path, 'report.json', json.dumps(report).encode('utf-8'))
        with reader.open_package(path) as package:
            loaded = reader.load_manifest(package)
            self.assertEqual(['A2', None], reader.load_source_index(package, loaded)['part-0001'][0]['cell_addresses'])
            self.assertFalse(reader.review_required(package, loaded))
        self.assertEqual((0, 'files ok\n'), self.run_cli(path, '--verify', '--fail-on-review')[:2])

    def test_part_ids_that_differ_only_in_case_are_different_parts(self):
        path = self.build()
        self.patch_manifest(path, lambda m: m['parts'][1].update(id='PART-0001'))
        index = v2_index()
        index['parts'][1]['id'] = 'PART-0001'
        self.relist(path, 'source-index.json', json.dumps(index).encode('utf-8'))
        with reader.open_package(path) as package:
            loaded = reader.load_manifest(package)
            self.assertEqual({'part-0001', 'PART-0001'}, set(reader.load_source_index(package, loaded)))
        self.assertEqual((0, '# one\n', ''), self.run_cli(path, '--part', 'part-0001'))
        self.assertEqual((0, '# two\n', ''), self.run_cli(path, '--part', 'PART-0001'))
        self.assert_refused(path, '--part', 'Part-0001', says='unknown part id')

    def test_source_index_and_report_edge_cases(self):
        cases = {
            'index is an array': ('source-index.json', b'[]', reader.PackageError),
            'index schema 2.0': ('source-index.json', json.dumps(dict(v2_index(), schema_version='2.0')).encode('utf-8'),
                                 reader.UnsupportedSchemaError),
            'report is an array': ('report.json', b'[]', reader.PackageError),
            'report review.required null': ('report.json', b'{"schema_version": "1.1", "review": {"required": null}}',
                                            reader.PackageError),
            'report schema 1.x.y': ('report.json', b'{"schema_version": "1.1.0", "review": {"required": false}}',
                                    reader.UnsupportedSchemaError),
        }
        for name, (rel, data, error) in cases.items():
            with self.subTest(case=name):
                path = self.relist(self.build(), rel, data)
                with reader.open_package(path) as package:
                    loaded = reader.load_manifest(package)
                    read = reader.load_source_index if rel == 'source-index.json' else reader.review_required
                    self.assertRaises(error, read, package, loaded)
                if rel == 'report.json':
                    self.assert_refused(path, '--fail-on-review')
        later = self.relist(self.build(), 'source-index.json', json.dumps(dict(v2_index(), schema_version='1.7')).encode('utf-8'))
        with reader.open_package(later) as package:
            self.assertEqual(2, len(reader.load_source_index(package, reader.load_manifest(package))))

    def test_cli_matrix(self):
        path = self.build()
        (path / 'parts/0002.md').write_bytes(b'# TWO\n')  # same length, different hash
        self.relist(path, 'report.json', report_text(required=True).encode('utf-8'))
        # Verification comes first, then the review check, then output.
        code, out, err = self.run_cli(path, '--verify', '--part', 'part-0001', '--fail-on-review')
        self.assertEqual((1, ''), (code, out))
        self.assertIn('parts/0002.md: sha256 mismatch', err)
        code, out, err = self.run_cli(path, '--part', 'part-0001', '--fail-on-review')
        self.assertEqual((3, ''), (code, out))
        self.assertEqual(1, len(err.strip().splitlines()), err)
        self.assertEqual((3, ''), self.run_cli(path, '--fail-on-review')[:2])
        # An unknown part id is refused before anything is verified or printed.
        self.assert_refused(path, '--verify', '--part', 'absent', says='unknown part id: absent')
        self.assert_refused(self.root / 'no-such-package', '--verify')
        # Without the option the review does not change the result.
        self.assertEqual((0, '# one\n', ''), self.run_cli(path, '--part', 'part-0001'))


class TestReviewFindings(PackageFixture):
    """Inputs from the independent review of the hardened reader (2026-10-07)."""

    def test_a_zip_needing_a_newer_version_to_extract_is_refused_without_a_traceback(self):
        data = bytearray(self.zipped(self.build()).read_bytes())
        central = data.index(b'PK\x01\x02')
        struct.pack_into('<H', data, central + 6, 100)  # version needed to extract: 10.0
        target = self.unique('version.zip')
        target.write_bytes(bytes(data))
        self.assert_refused(target, '--verify')

    def test_entries_compressed_with_other_methods_are_refused(self):
        path = self.build()
        for method in (zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA):
            target = self.unique('method.zip')
            with zipfile.ZipFile(target, 'w') as archive:
                for name, data in self.entries_of(path):
                    archive.writestr(name, data, compress_type=method if name == 'parts/0001.md' else zipfile.ZIP_DEFLATED)
            with self.subTest(method=method):
                code, out, _ = self.run_cli(target, '--verify')
                self.assertEqual(1, code)
                self.assertIn('parts/0001.md: refused (', out)
                self.assert_refused(target, '--part', 'part-0001', says='compression method')

    def test_verify_with_part_does_not_read_a_part_whose_size_differs(self):
        path = self.build()
        (path / 'parts/0001.md').write_bytes(b'x' * 100000)
        read = []
        original = reader.Package.read_bytes
        def recording(package, rel):
            read.append(rel)
            return original(package, rel)
        reader.Package.read_bytes = recording
        try:
            code, out, err = self.run_cli(path, '--verify', '--part', 'part-0001')
        finally:
            reader.Package.read_bytes = original
        self.assertEqual((1, ''), (code, out))
        self.assertIn('parts/0001.md: size mismatch', err)
        self.assertNotIn('parts/0001.md', read)

    def test_the_part_is_printed_as_its_exact_bytes_whatever_the_console_encoding(self):
        path = self.build()
        data = '# 売上実績（単位: 千円）\n'.encode('utf-8')
        self.relist(path, 'parts/0001.md', data)
        for encoding in ('cp932', 'cp1252', 'utf-8'):
            with self.subTest(encoding=encoding):
                result = subprocess.run([sys.executable, str(ROOT / 'docs/examples/ai_package_reader.py'), str(path),
                                         '--verify', '--part', 'part-0001'], capture_output=True, timeout=30,
                                        env=dict(os.environ, PYTHONIOENCODING=encoding))
                self.assertEqual((0, data), (result.returncode, result.stdout), result.stderr)

    def test_unpaired_surrogates_are_refused_before_anything_is_printed(self):
        for name, change in {
                'label': lambda m: m['locations'][0].update(label='\ud800'),
                'file name': lambda m: m['source'].update(file_name='\ud800.xlsx'),
                'unknown key': lambda m: m.update({'future\ud800': 1}),
                'unknown value': lambda m: m.update(future='\udfff')}.items():
            with self.subTest(field=name):
                path = self.build()
                raw = self.manifest(path)
                change(raw)
                (path / 'manifest.json').write_text(json.dumps(raw), encoding='utf-8')  # ASCII escapes
                self.load(path, reader.PackageError)
                self.assert_refused(path)
                self.assert_refused(path, '--verify')
        path = self.patch_manifest(self.build(), lambda m: m['locations'][0].update(label='\U0001f600'))
        self.assertEqual('\U0001f600', self.load(path)['locations'][0]['label'])

    def test_nesting_is_limited_like_the_dotnet_reader(self):
        def nested(depth):
            value = 1
            for _ in range(depth):
                value = [value]
            return value
        accepted = self.patch_manifest(self.build(), lambda m: m.update(future=nested(63)))  # 64 levels with the root
        self.load(accepted)
        refused = self.patch_manifest(self.build(), lambda m: m.update(future=nested(64)))
        self.load(refused, reader.PackageError)
        self.assert_refused(refused, says='nested more than 64 levels deep')

    def test_a_huge_integer_in_an_unknown_field_is_ignored_but_not_as_a_count(self):
        path = self.build()
        text = (path / 'manifest.json').read_text(encoding='utf-8')
        (path / 'manifest.json').write_text(text[:-1] + ', "future": ' + '9' * 5000 + '}', encoding='utf-8')
        self.load(path)
        raw = (path / 'manifest.json').read_text(encoding='utf-8')
        first = raw.index('"bytes": ') + len('"bytes": ')
        end = raw.index(',', first)
        (path / 'manifest.json').write_text(raw[:first] + '9' * 5000 + raw[end:], encoding='utf-8')
        self.load(path, reader.PackageError)


class TestPlainZipAndSpecialFiles(PackageFixture):
    """A package ZIP is read only when it is plain, and a package folder holds only regular files: the
    same rules, order and words as the .NET AiPackageManifestReader (tests/DocRedock.Tests/Api/
    AiPackageReaderParityTests.cs runs both readers on the same packages)."""

    def patched(self, change):
        data = bytearray(self.zipped(self.build()).read_bytes())
        change(data)
        target = self.unique('patched.zip')
        target.write_bytes(bytes(data))
        return target

    @staticmethod
    def central(data, name):
        end = len(data) - 22
        position, = struct.unpack_from('<I', data, end + 16)
        while True:
            length, = struct.unpack_from('<H', data, position + 28)
            if bytes(data[position + 46:position + 46 + length]) == name.encode():
                return position
            position += 46 + length + sum(struct.unpack_from('<HH', data, position + 30))

    def test_a_pipe_in_a_package_folder_is_refused_without_being_opened(self):
        if not hasattr(os, 'mkfifo'):
            self.skipTest('pipes are unavailable')
        path = self.build()
        (path / 'parts/0001.md').unlink()
        os.mkfifo(path / 'parts/0001.md')
        self.assert_problems(path, ["parts/0001.md: refused (package path is not a regular file: 'parts/0001.md')"])
        self.assert_refused(path, '--part', 'part-0001', says='not a regular file')

    def test_archives_that_readers_would_read_differently_are_refused_whole(self):
        def version(data):
            struct.pack_into('<B', data, self.central(data, 'parts/0001.md') + 6, 100)
        def count(data):
            struct.pack_into('<HH', data, len(data) - 22 + 8, *(value + 1 for value in struct.unpack_from('<HH', data, len(data) - 22 + 8)))
        def disks(data):
            struct.pack_into('<H', data, len(data) - 22 + 4, 1)
        for name, change, says in [('version needed 10.0', version, 'uses a ZIP feature this reader does not read (zip file version 10.0)'),
                                   ('end record count', count, "the ZIP's end record counts"),
                                   ('several disks', disks, 'the ZIP spans several disks')]:
            with self.subTest(case=name):
                self.assert_refused(self.patched(change), '--verify', says=says)
        path = self.build()
        target = self.unique('unicode-path.zip')
        with zipfile.ZipFile(target, 'w') as archive:
            for name, data in self.entries_of(path):
                info = zipfile.ZipInfo(name)
                if name == 'review.md':
                    info.extra = struct.pack('<HHBL', 0x7075, 5 + 4, 1, zlib.crc32(name.encode())) + b'x.md'
                archive.writestr(info, data)
        self.assert_refused(target, '--verify', says="a Unicode Path extra field")

    def test_entries_are_checked_as_zipfile_opens_them_whatever_the_python_version(self):
        def flag(bit):
            def change(data):
                position = self.central(data, 'parts/0001.md') + 8
                struct.pack_into('<H', data, position, struct.unpack_from('<H', data, position)[0] | bit)
            return change
        def local_name(data):
            header, = struct.unpack_from('<I', data, self.central(data, 'parts/0001.md') + 42)
            data[header + 30 + 6] = ord('X')
        def overlap(data):
            position = self.central(data, 'parts/0001.md') + 20
            struct.pack_into('<I', data, position, struct.unpack_from('<I', data, position)[0] + 4096)
        prefix = "parts/0001.md: refused (cannot read 'parts/0001.md' from the ZIP: "
        for name, change, line in [
                ('patched data', flag(0x20), prefix + 'compressed patched data (flag bit 5))'),
                ('strong encryption', flag(0x40), prefix + 'strong encryption (flag bit 6))'),
                ('other local name', local_name, prefix + "File name in directory 'parts/0001.md' and header b'parts/X001.md' differ.)"),
                ('overlapping entries', overlap, prefix + "Overlapped entries: 'parts/0001.md' (possible zip bomb))")]:
            with self.subTest(case=name):
                self.assert_problems(self.patched(change), [line])
