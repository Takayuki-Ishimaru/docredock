"""Contracts for docs/examples/ai_package_reader.py against real packages of every schema."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import tempfile
import unittest
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
}


class ReaderContracts(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
