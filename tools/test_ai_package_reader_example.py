"""Contracts for docs/examples/ai_package_reader.py against real packages of every schema."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import warnings
import zipfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('ai_package_reader', ROOT / 'docs/examples/ai_package_reader.py')
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)
SAMPLES = ROOT / 'tests/DocRedock.Tests/Fixtures/AiPackage'
# schema -> (major, minor, node count of each part)
EXPECTED = {
    'schema-1.0': (1, 0, [3, 2, 4]),
    'schema-2.0': (2, 0, [3, 4, 11, 3]),
    'schema-2.1': (2, 1, [1, 8, 8, 8, 4, 4]),
    'schema-2.2': (2, 2, [120, 119, 17]),
}


class ReaderContracts(unittest.TestCase):
    def test_insert_revision_markup_is_ignored_for_search_but_escaped_literal_tags_are_preserved(self):
        self.assertEqual("before added after", reader._searchable("before <ins>**added**</ins> after"))
        self.assertEqual("<ins>literal</ins>", reader._searchable("&lt;ins&gt;literal&lt;/ins&gt;"))

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def zip_of(self, directory):
        target = self.root / (directory.name + '.zip')
        with zipfile.ZipFile(target, 'w') as archive:
            for path in sorted(directory.rglob('*')):
                if path.is_file():
                    archive.write(path, path.relative_to(directory).as_posix())
        return target

    def copy_of(self, sample, name):
        target = self.root / name
        shutil.copytree(SAMPLES / sample, target)
        return target

    def patched(self, sample, change):
        target = self.copy_of(sample, 'patched-%d' % len(list(self.root.iterdir())))
        manifest = json.loads((target / 'manifest.json').read_text(encoding='utf-8'))
        change(manifest)
        (target / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        return target

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = reader.main([str(arg) for arg in args])
        return code, out.getvalue(), err.getvalue()

    def relisted(self, package, rel, data):
        """Write a package file and list its new size and hash, as a consistent package would."""
        (package / rel).write_bytes(data)
        manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        entry = next(item for item in manifest['files'] if item['path'] == rel)
        entry.update(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        (package / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')

    def assert_refused(self, path, *args, says=None):
        """Exit 2 with one short line on stderr, nothing on stdout and no traceback."""
        code, out, err = self.run_cli(path, *args)
        self.assertEqual((2, ''), (code, out), err)
        self.assertEqual(1, len(err.strip().splitlines()), err)
        if says is not None:
            self.assertIn(says, err)
        return err

    def test_every_sample_reads_the_same_way_from_a_directory_and_a_zip(self):
        for name, (major, minor, nodes) in EXPECTED.items():
            for path in (SAMPLES / name, self.zip_of(SAMPLES / name)):
                with self.subTest(sample=name, form=path.suffix or 'dir'), reader.open_package(path) as package:
                    manifest = reader.load_manifest(package)
                    self.assertEqual((major, minor), (manifest['major'], manifest['minor']))
                    self.assertEqual(nodes, [part['node_count'] for part in manifest['parts']])
                    index = reader.load_source_index(package, manifest)
                    for part in manifest['parts']:
                        self.assertEqual(part['node_count'], sum(len(group['node_ids']) for group in index[part['id']]))
                        self.assertEqual(part['location_ids'], [location['id'] for location in part['locations']])
                        self.assertTrue(reader.read_part(package, part).startswith('# '))
                    self.assertEqual([], reader.verify_files(package, manifest))

    def test_schema_1_0_keeps_inline_nodes_and_builds_shared_locations(self):
        with reader.open_package(SAMPLES / 'schema-1.0') as package:
            manifest = reader.load_manifest(package)
            raw = json.loads(package.read_text('manifest.json'))
        self.assertIsNone(manifest['source_index'])
        self.assertFalse(manifest['table_row_blocks'])
        self.assertEqual([part['node_ids'] for part in raw['parts']], [part['node_ids'] for part in manifest['parts']])
        self.assertEqual(['source-0001', 'source-0002', 'source-0003', 'source-0004'],
                         [location['id'] for location in manifest['locations']])
        self.assertEqual(['source-0001', 'source-0002'], manifest['parts'][0]['location_ids'])
        self.assertEqual(['AIパッケージのサンプル', '1. 概要'], manifest['locations'][1]['heading_path'])
        self.assertTrue(all(part['estimated_tokens'] is None and part['table_block'] is None for part in manifest['parts']))

    def test_schema_2_0_uses_the_source_index_and_has_no_inline_nodes(self):
        with reader.open_package(SAMPLES / 'schema-2.0') as package:
            manifest = reader.load_manifest(package)
        self.assertEqual('source-index.json', manifest['source_index'])
        self.assertTrue(all(part['node_ids'] is None for part in manifest['parts']))
        self.assertEqual(['headings: Section 1', 'headings: Section 2', 'headings: Section 3', 'headings: Section 4'],
                         [part['locations'][0]['label'] for part in manifest['parts']])

    def test_schema_2_1_token_estimates_and_table_block_chains(self):
        with reader.open_package(SAMPLES / 'schema-2.1') as package:
            manifest = reader.load_manifest(package)
            index = reader.load_source_index(package, manifest)
        self.assertTrue(manifest['table_row_blocks'])
        self.assertTrue(all(isinstance(part['estimated_tokens'], int) for part in manifest['parts']))
        chains = {}
        for part in manifest['parts']:
            if part['table_block']:
                chains.setdefault(part['table_block']['table_id'], []).append(part)
        self.assertEqual({'table-0001': 3, 'table-0002': 2}, {key: len(value) for key, value in chains.items()})
        for parts in chains.values():
            for position, part in enumerate(parts):
                block = part['table_block']
                self.assertEqual((position + 1, len(parts)), (block['index'], block['count']))
                self.assertEqual(parts[position - 1]['id'] if position else None, block['previous_part'])
                self.assertEqual(parts[position + 1]['id'] if position + 1 < len(parts) else None, block['next_part'])
        cells = [cell for group in index['part-0002'] for cell in (group['cell_addresses'] or [])]
        self.assertIn('A2', cells)
        self.assertIn('A3', cells)

    def test_schema_2_2_blocks_repeat_the_text_above_the_table_and_name_its_cells(self):
        with reader.open_package(SAMPLES / 'schema-2.2') as package:
            manifest = reader.load_manifest(package)
            index = reader.load_source_index(package, manifest)
            texts = [reader.read_part(package, part) for part in manifest['parts']]
        blocks = [part['table_block'] for part in manifest['parts']]
        self.assertEqual([(1, 3), (2, 3), (3, 3)], [(block['index'], block['count']) for block in blocks])
        self.assertTrue(all(block['context_range'] == 'A3:A4' for block in blocks))
        for position, text in enumerate(texts):
            repeated = ' (repeated)' if position else ''
            self.assertIn('; header A5:C5%s; context A3:A4%s\n' % (repeated, repeated), text)
            notes = text.index('売上実績（単位: 千円、税抜）\n\n対象: 国内全店\n\n| 商品 | 売上 | 数量 |')
            self.assertEqual(1, text.count('売上実績（単位: 千円、税抜）'))
            self.assertGreater(notes, text.index('## Data'))
            cells = [cell for group in index[manifest['parts'][position]['id']] for cell in group['cell_addresses'] or []]
            self.assertTrue({'A3', 'A4', 'A5', 'B5', 'C5'} <= set(cells), cells)
        code, out, _ = self.run_cli(SAMPLES / 'schema-2.2')
        self.assertEqual(0, code)
        self.assertIn('part-0002 parts/0002.md tokens=325 sheet Data block 2/3 rows A44:C81 context A3:A4', out)

    def test_unsupported_or_missing_schema_versions_are_refused_by_name(self):
        for value in ['3.0', '0.9', 2, '2', 'abc', '2.x', '02.1', None]:
            def change(manifest, value=value):
                if value is None:
                    del manifest['schema_version']
                else:
                    manifest['schema_version'] = value
            path = self.patched('schema-2.1', change)
            with self.subTest(value=value), reader.open_package(path) as package:
                with self.assertRaises(reader.UnsupportedSchemaError) as raised:
                    reader.load_manifest(package)
                message = str(raised.exception)
                self.assertIn('supported: 1.x, 2.x', message)
                self.assertIn('missing' if value is None else (value if isinstance(value, str) else repr(value)), message)
            code, _, err = self.run_cli(path)
            self.assertEqual(2, code)
            self.assertEqual(1, len(err.strip().splitlines()))

    def test_later_minor_versions_with_unknown_fields_are_read(self):
        for sample, version in [('schema-2.1', '2.7'), ('schema-1.0', '1.3')]:
            def change(manifest, version=version):
                manifest['schema_version'] = version
                manifest['future_field'] = {'anything': 1}
            with self.subTest(version=version), reader.open_package(self.patched(sample, change)) as package:
                self.assertEqual(version, reader.load_manifest(package)['schema_version'])

    def test_changed_truncated_and_missing_files_are_reported(self):
        path = self.copy_of('schema-2.1', 'tampered')
        changed = path / 'parts/0002.md'
        changed.write_bytes(changed.read_bytes().replace(b'Revenue', b'Revenuf'))
        truncated = path / 'parts/0004.md'
        truncated.write_bytes(truncated.read_bytes()[:-5])
        (path / 'parts/0003.md').unlink()
        with reader.open_package(path) as package:
            problems = reader.verify_files(package, reader.load_manifest(package))
        self.assertEqual(3, len(problems))
        for name in ('parts/0002.md', 'parts/0003.md', 'parts/0004.md'):
            self.assertTrue(any(name in problem for problem in problems), (name, problems))
        code, out, _ = self.run_cli(path, '--verify')
        self.assertEqual(1, code)
        self.assertIn('parts/0003.md', out)

    def test_paths_outside_the_package_are_refused(self):
        with reader.open_package(SAMPLES / 'schema-2.0') as package:
            for path in ('../x', '/etc/passwd', 'a\\b', 'parts/../../x', 'C:/Windows/win.ini', 'C:x', './parts/0001.md', 'parts//0001.md'):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    package.read_bytes(path)

    def test_links_inside_a_package_folder_are_refused(self):
        path = self.copy_of('schema-2.1', 'linked')
        outside = self.root / 'outside.md'
        outside.write_text('# outside the package\n', encoding='utf-8')
        (path / 'parts/0002.md').unlink()
        (path / 'parts/0002.md').symlink_to(outside)
        with reader.open_package(path) as package:
            with self.assertRaises(ValueError):
                package.read_bytes('parts/0002.md')
            problems = reader.verify_files(package, reader.load_manifest(package))
        self.assertEqual(1, len(problems))
        self.assertTrue(problems[0].startswith('parts/0002.md: refused'), problems)

    def test_cli_lists_parts_prints_one_part_and_reports_errors(self):
        code, out, _ = self.run_cli(SAMPLES / 'schema-2.1')
        self.assertEqual(0, code)
        self.assertIn('schema 2.1', out)
        self.assertIn('source workbook_base.xlsx', out)
        self.assertIn('part-0002 parts/0002.md tokens=52 sheet Summary block 1/3 rows A3:F3', out)
        code, out, _ = self.run_cli(SAMPLES / 'schema-1.0')
        self.assertEqual(0, code)
        self.assertIn('part-0003 parts/0003.md tokens=- headings: AIパッケージのサンプル > 3. 確認', out)
        code, out, _ = self.run_cli(SAMPLES / 'schema-2.0', '--part', 'part-0002')
        self.assertEqual(0, code)
        self.assertEqual((SAMPLES / 'schema-2.0/parts/0002.md').read_text(encoding='utf-8'), out)
        self.assertEqual(2, self.run_cli(SAMPLES / 'schema-2.0', '--part', 'part-9999')[0])
        self.assertEqual((0, 'files ok\n'), self.run_cli(SAMPLES / 'schema-2.0', '--verify')[:2])
        code, _, err = self.run_cli(self.root / 'no-such-package')
        self.assertEqual(2, code)
        self.assertTrue(err.strip())

    # v0.3.2 evaluation R1: --verify together with --part used to print the part unchecked.
    def test_verify_with_part_prints_only_verified_bytes(self):
        path = self.copy_of('schema-2.1', 'same-length')
        part = path / 'parts/0002.md'
        original = part.read_bytes()
        part.write_bytes(original.replace(b'Revenue', b'Revenuf'))
        self.assertEqual(len(original), part.stat().st_size)
        for form in (path, self.zip_of(path)):
            with self.subTest(form=form.suffix or 'dir'):
                code, out, err = self.run_cli(form, '--verify', '--part', 'part-0002')
                self.assertEqual((1, ''), (code, out))
                self.assertIn('parts/0002.md: sha256 mismatch', err)
                # Another changed file stops the part too: the whole package is verified first.
                code, out, err = self.run_cli(form, '--verify', '--part', 'part-0003')
                self.assertEqual((1, ''), (code, out))
                self.assertIn('parts/0002.md', err)
        part.write_bytes(original)
        code, out, err = self.run_cli(path, '--verify', '--part', 'part-0002')
        self.assertEqual((0, original.decode('utf-8'), ''), (code, out, err))
        part.unlink()
        code, out, err = self.run_cli(path, '--verify', '--part', 'part-0002')
        self.assertEqual((1, ''), (code, out))
        self.assertIn('parts/0002.md: missing', err)
        # Without --verify a missing part is an unreadable package, not a verification result.
        self.assert_refused(path, '--part', 'part-0002', says='parts/0002.md')

    def test_the_bytes_printed_are_the_bytes_verified(self):
        path = self.copy_of('schema-2.1', 'swapped')
        original = (path / 'parts/0002.md').read_bytes()
        package = reader.open_package(path)
        self.addCleanup(package.close)
        manifest = reader.load_manifest(package)
        # A file changed after it was read for printing does not affect what is checked or printed.
        contents = {'parts/0002.md': package.read_bytes('parts/0002.md')}
        (path / 'parts/0002.md').write_bytes(b'# swapped\n')
        self.assertEqual([], reader.verify_files(package, manifest, contents))
        self.assertEqual(original, contents['parts/0002.md'])
        self.assertEqual(1, len(reader.verify_files(package, manifest)))

    # R2 and R3: required fields, types and shapes are checked when the manifest is loaded.
    def test_missing_or_mistyped_manifest_fields_are_refused_by_name(self):
        cases = {
            'source is missing': lambda m: m.pop('source'),
            'source must be an object': lambda m: m.update(source=123),
            'source.sha256 must be 64 hexadecimal digits': lambda m: m['source'].update(sha256='abc'),
            'source.file_name must be a string': lambda m: m['source'].update(file_name=7),
            'content_policy is missing': lambda m: m.pop('content_policy'),
            'files is missing': lambda m: m.pop('files'),
            'files must be an array': lambda m: m.update(files='bad'),
            'files[0] must be an object': lambda m: m['files'].insert(0, 'document.md'),
            'files[0].bytes is missing': lambda m: m['files'][0].pop('bytes'),
            'files[0].bytes must be a whole number': lambda m: m['files'][0].update(bytes=True),
            'files[1].bytes must be a whole number': lambda m: m['files'][1].update(bytes=-1),
            'files[2].bytes must be a whole number': lambda m: m['files'][2].update(bytes='120'),
            'files[0].sha256 is missing': lambda m: m['files'][0].pop('sha256'),
            'files[0].sha256 must be 64 hexadecimal digits': lambda m: m['files'][0].update(sha256='z' * 64),
            'files[0].path is null': lambda m: m['files'][0].update(path=None),
            'files[0].path: unsafe package path': lambda m: m['files'][0].update(path='../document.md'),
            'parts is missing': lambda m: m.pop('parts'),
            'parts[0] must be an object': lambda m: m.update(parts=[None]),
            'parts[1].node_count is missing': lambda m: m['parts'][1].pop('node_count'),
            'parts[1].node_count must be a whole number': lambda m: m['parts'][1].update(node_count='8'),
            'parts[1].source_ids must hold only strings': lambda m: m['parts'][1].update(source_ids=[1]),
            'parts[1].exceeds_target must be true or false': lambda m: m['parts'][1].update(exceeds_target='no'),
            'parts[1].estimated_tokens must be a whole number': lambda m: m['parts'][1].update(estimated_tokens=1.5),
            'parts[1].table_block must be an object': lambda m: m['parts'][1].update(table_block=[]),
            'parts[1].table_block.index must be from 1 to count': lambda m: m['parts'][1]['table_block'].update(index=4),
            'parts[1].table_block.row_range is missing': lambda m: m['parts'][1]['table_block'].pop('row_range'),
            'locations must be an array': lambda m: m.update(locations={}),
            'locations[0].label is missing': lambda m: m['locations'][0].pop('label'),
            'locations[0].heading_path must hold only strings': lambda m: m['locations'][0].update(heading_path=[None]),
            'source_index: unsafe package path': lambda m: m.update(source_index='/etc/passwd'),
        }
        for message, change in cases.items():
            with self.subTest(message=message):
                path = self.patched('schema-2.1', change)
                with reader.open_package(path) as package, self.assertRaises(reader.PackageError) as raised:
                    reader.load_manifest(package)
                self.assertIn(message, str(raised.exception))
                self.assert_refused(path, '--verify', says=message)

    def test_a_manifest_with_only_a_version_is_refused(self):
        path = self.root / 'version-only'
        path.mkdir()
        (path / 'manifest.json').write_text('{"schema_version": "2.1"}', encoding='utf-8')
        self.assert_refused(path, '--verify', says='source is missing')

    def test_every_file_the_manifest_names_must_be_listed_with_a_hash(self):
        def unlist(name):
            return lambda m: m.update(files=[entry for entry in m['files'] if entry['path'] != name])
        for name in ('document.md', 'review.md', 'report.json', 'source-index.json', 'parts/0004.md'):
            with self.subTest(name=name):
                self.assert_refused(self.patched('schema-2.1', unlist(name)), '--verify',
                                    says="manifest files does not list '%s'" % name)
        err = self.assert_refused(self.patched('schema-2.1', lambda m: m.update(files=[])), '--verify')
        self.assertIn('and 5 more', err)
        self.assert_refused(self.patched('schema-1.0', unlist('parts/0002.md')), '--part', 'part-0001',
                            says="'parts/0002.md'")
        # A file listed but referred to by nothing (an image) is still verified.
        path = self.copy_of('schema-2.1', 'extra')
        (path / 'assets').mkdir()
        self.relisted_new(path, 'assets/image.png', b'png')
        self.assertEqual((0, 'files ok\n'), self.run_cli(path, '--verify')[:2])
        (path / 'assets/image.png').write_bytes(b'PNG')
        self.assertIn('assets/image.png: sha256 mismatch', self.run_cli(path, '--verify')[1])

    def relisted_new(self, package, rel, data):
        (package / rel).write_bytes(data)
        manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        manifest['files'].append({'path': rel, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        (package / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')

    def test_ids_paths_keys_and_zip_entries_listed_twice_are_refused(self):
        def location_twice(m):
            m['locations'].append(dict(m['locations'][0], label='sheet Other', sheet_name='Other'))
        def part_twice(m):
            m['parts'].append(dict(m['parts'][0], path='parts/0009.md'))
            m['files'].append(dict(m['files'][0], path='parts/0009.md'))
        cases = {
            "locations list 'source-0001' twice": location_twice,
            "parts list the id 'part-0001' twice": part_twice,
            "parts list the path 'parts/0001.md' twice": lambda m: m['parts'][1].update(path='parts/0001.md'),
            "files list 'document.md' twice": lambda m: m['files'].append(dict(m['files'][0])),
            "part part-0002 names a location twice": lambda m: m['parts'][1]['source_ids'].append('source-0001'),
            "next_part names an unknown part 'part-0099'": lambda m: m['parts'][1]['table_block'].update(next_part='part-0099'),
        }
        for message, change in cases.items():
            with self.subTest(message=message):
                self.assert_refused(self.patched('schema-2.1', change), says=message)
        path = self.copy_of('schema-2.1', 'duplicate-key')
        text = (path / 'manifest.json').read_text(encoding='utf-8')
        (path / 'manifest.json').write_text(text.replace('"schema_version": "2.1",', '"schema_version": "2.1", "schema_version": "3.0",', 1),
                                            encoding='utf-8')
        self.assert_refused(path, says="manifest.json has the key 'schema_version' more than once")
        archive = self.zip_of(SAMPLES / 'schema-2.1')
        with warnings.catch_warnings():
            warnings.simplefilter('ignore')
            with zipfile.ZipFile(archive, 'a') as package:
                package.writestr('parts/0002.md', b'# a second part 2\n')
        self.assert_refused(archive, '--part', 'part-0002', says="more than one entry named 'parts/0002.md'")

    def test_unreadable_archives_and_entries_are_reported_not_raised(self):
        not_zip = self.root / 'not-a-package.zip'
        not_zip.write_text('this is not a zip\n', encoding='utf-8')
        self.assert_refused(not_zip, '--verify', says='not a package folder or ZIP archive')
        # A stored entry whose bytes no longer match the ZIP's own CRC.
        archive = self.root / 'corrupt.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_STORED) as package:
            for path in sorted((SAMPLES / 'schema-2.1').rglob('*')):
                if path.is_file():
                    package.write(path, path.relative_to(SAMPLES / 'schema-2.1').as_posix())
        data = archive.read_bytes()
        marker = data.index(b'table_block: table-0001 1/3')  # only in parts/0002.md
        archive.write_bytes(data[:marker] + b'X' + data[marker + 1:])
        code, out, err = self.run_cli(archive, '--verify')
        self.assertEqual((1, "parts/0002.md: refused (cannot read 'parts/0002.md' from the ZIP: Bad CRC-32 for file 'parts/0002.md')\n"),
                         (code, out))
        self.assertNotIn('Traceback', err)
        self.assert_refused(archive, '--part', 'part-0002', says='Bad CRC-32')
        self.assertEqual(0, self.run_cli(archive, '--part', 'part-0003')[0])
        # Zipping the package folder itself puts every file one level down.
        nested = self.root / 'nested.zip'
        with zipfile.ZipFile(nested, 'w') as package:
            for path in sorted((SAMPLES / 'schema-2.0').rglob('*')):
                if path.is_file():
                    package.write(path, 'schema-2.0/' + path.relative_to(SAMPLES / 'schema-2.0').as_posix())
        self.assert_refused(nested, says="found 'schema-2.0/manifest.json', but package files must be at the top level")

    def test_versions_with_surrounding_whitespace_are_refused(self):
        for value in ['2.1\n', ' 2.1', '2.1 ', '2.١']:
            path = self.patched('schema-2.1', lambda m, value=value: m.update(schema_version=value))
            with self.subTest(value=value), reader.open_package(path) as package:
                self.assertRaises(reader.UnsupportedSchemaError, reader.load_manifest, package)
            self.assert_refused(path, says='is unsupported (supported: 1.x, 2.x)')

    def test_schema_1_nodes_and_sources_must_pair_one_to_one(self):
        def fewer_sources(m):
            m['parts'][0]['sources'] = m['parts'][0]['sources'][:-1]
        def other_node(m):
            m['parts'][0]['sources'][1]['node_id'] = 'not-the-node'
        for message, change in {'parts[0] lists 3 node_ids but 2 sources': fewer_sources,
                                'parts[0].sources[1].node_id': other_node}.items():
            with self.subTest(message=message):
                path = self.patched('schema-1.0', change)
                with reader.open_package(path) as package, self.assertRaises(reader.PackageError) as raised:
                    reader.load_source_index(package, reader.load_manifest(package))
                self.assertIn(message, str(raised.exception))

    def test_the_source_index_must_agree_with_the_manifest(self):
        def change_index(change):
            path = self.copy_of('schema-2.1', 'index-%d' % len(list(self.root.iterdir())))
            index = json.loads((path / 'source-index.json').read_text(encoding='utf-8'))
            change(index)
            self.relisted(path, 'source-index.json', json.dumps(index).encode('utf-8'))
            return path
        def drop_cell(index):
            index['parts'][1]['sources'][0]['cell_addresses'].pop()
        def drop_node(index):
            group = index['parts'][1]['sources'][0]
            group['node_ids'].pop()
            group['cell_addresses'].pop()
        def other_location(index):
            index['parts'][1]['sources'][0]['source_id'] = 'source-0099'
        cases = {
            "source-index.json schema_version '99.0' is unsupported": lambda index: index.update(schema_version='99.0'),
            'source-index.json.schema_version is missing': lambda index: index.pop('schema_version'),
            'source-index.json has no entry for part-0006': lambda index: index['parts'].pop(),
            "names a part the manifest does not list: 'part-0099'": lambda index: index['parts'][0].update(id='part-0099'),
            "lists part 'part-0001' twice": lambda index: index['parts'].append(index['parts'][0]),
            'must hold one address or null for each of its': drop_cell,
            'lists 7 node ids, but the manifest part counts 8': drop_node,
            "groups name the locations ['source-0099']": other_location,
            'source-index.json parts[1].sources[0].node_ids must hold only strings':
                lambda index: index['parts'][1]['sources'][0]['node_ids'].append(5),
        }
        for message, change in cases.items():
            with self.subTest(message=message):
                path = change_index(change)
                # The files match their hashes, but --verify also reads the index it vouches for.
                self.assert_refused(path, '--verify', says=message)
                with reader.open_package(path) as package, self.assertRaises(reader.PackageError) as raised:
                    reader.load_source_index(package, reader.load_manifest(package))
                self.assertIn(message, str(raised.exception))
        # Schema 2.x keeps node identities only in the source index, so it cannot be left out.
        for change, message in [(lambda m: m.pop('source_index'), 'source_index is missing'),
                                (lambda m: m.update(source_index=None), 'source_index is null')]:
            path = self.patched('schema-2.1', change)
            with reader.open_package(path) as package, self.assertRaises(reader.PackageError) as raised:
                reader.load_manifest(package)
            self.assertEqual(message, str(raised.exception))
            self.assert_refused(path, '--verify', says=message)

    def test_fail_on_review_stops_before_anything_is_printed(self):
        path = self.copy_of('schema-2.1', 'review')
        report = json.loads((path / 'report.json').read_text(encoding='utf-8'))
        with reader.open_package(path) as package:
            self.assertFalse(reader.review_required(package, reader.load_manifest(package)))
        self.assertEqual(0, self.run_cli(path, '--fail-on-review')[0])
        report['review']['required'] = True
        self.relisted(path, 'report.json', json.dumps(report).encode('utf-8'))
        with reader.open_package(path) as package:
            self.assertTrue(reader.review_required(package, reader.load_manifest(package)))
        for args in [('--fail-on-review',), ('--part', 'part-0002', '--fail-on-review'),
                     ('--verify', '--part', 'part-0002', '--fail-on-review')]:
            with self.subTest(args=args):
                code, out, err = self.run_cli(path, *args)
                self.assertEqual((3, ''), (code, out))
                self.assertIn('review required: read review.md', err)
        code, out, err = self.run_cli(path, '--verify', '--fail-on-review')
        self.assertEqual((3, 'files ok\n'), (code, out))
        # Body output stops by default, and requires explicit acknowledgement.
        self.assertEqual((11, ''), self.run_cli(path, '--part', 'part-0002')[:2])
        self.assertEqual(0, self.run_cli(path, '--part', 'part-0002', '--allow-review')[0])
        # A changed report is a verification failure first.
        (path / 'report.json').write_text(json.dumps(dict(report, review={'required': False})), encoding='utf-8')
        self.assertEqual(1, self.run_cli(path, '--verify', '--fail-on-review')[0])
        for message, change in {"report.json schema_version '2.0' is unsupported": lambda r: r.update(schema_version='2.0'),
                                'report.json.review is missing': lambda r: r.pop('review'),
                                'report.json.review.required must be true or false':
                                    lambda r: r['review'].update(required='yes')}.items():
            with self.subTest(message=message):
                changed = json.loads(json.dumps(report))
                change(changed)
                self.relisted(path, 'report.json', json.dumps(changed).encode('utf-8'))
                self.assert_refused(path, '--fail-on-review', says=message)


if __name__ == '__main__':
    unittest.main()
