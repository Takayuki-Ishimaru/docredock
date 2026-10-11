"""Adversarial contract tests for docs/examples/ai_package_reader.py.

Each test quotes the contract sentence (docs/reference/ai-package.md or the reader's own
docstrings) whose behaviour it checks. Drafted by an independent worker that saw only the
contract, the reader and the other suites, then corrected where its expectations went beyond
the contract.
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parent.parent
READER = ROOT / 'docs/examples/ai_package_reader.py'
_SAMPLE_SPEC = importlib.util.spec_from_file_location('ai_package_reader', READER)
reader = importlib.util.module_from_spec(_SAMPLE_SPEC)
_SAMPLE_SPEC.loader.exec_module(reader)
SAMPLES = ROOT / 'tests/DocRedock.Tests/Fixtures/AiPackage'


class EvoFixture(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.counter = 0

    def unique(self, name):
        self.counter += 1
        return self.root / ('%s-%d' % (name, self.counter))

    def copy_of(self, sample, name=None):
        target = self.unique(name or sample)
        shutil.copytree(SAMPLES / sample, target)
        return target

    def patch_manifest(self, package, change):
        raw = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        change(raw)
        (package / 'manifest.json').write_text(json.dumps(raw, ensure_ascii=False), encoding='utf-8')
        return package

    def relist(self, package, rel, data):
        path = package / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

        def change(raw, digest=hashlib.sha256(data).hexdigest()):
            next(item for item in raw['files'] if item['path'] == rel).update(
                bytes=len(data), sha256=digest)
        return self.patch_manifest(package, change)

    def set_report(self, package, required):
        report = json.loads((package / 'report.json').read_text(encoding='utf-8'))
        report['review']['required'] = required
        return self.relist(package, 'report.json', json.dumps(report).encode('utf-8'))

    def zipped(self, directory, skip=(), name='pkg.zip'):
        target = self.unique(name)
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(directory.rglob('*')):
                if path.is_file() and path.relative_to(directory).as_posix() not in skip:
                    archive.write(path, path.relative_to(directory).as_posix())
        return target

    def run_cli(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = reader.main([str(arg) for arg in args])
        return code, out.getvalue(), err.getvalue()

    def run_refused(self, *args):
        code, out, err = self.run_cli(*args)
        self.assertEqual((2, ''), (code, out), err)
        self.assertNotIn('Traceback', err)
        self.assertEqual(1, len(err.strip().splitlines()), err)
        return err

    def problems_of(self, path):
        with reader.open_package(path) as package:
            return reader.verify_files(package, reader.load_manifest(package))

    def markdown_of(self, package, path):
        return (package / path).read_text(encoding='utf-8')


class FlagCombinations(EvoFixture):
    # "--fail-on-review stops without printing the part, parts or matches when report.json says
    # review is required."
    def test_review_stop_covers_search(self):
        package = self.set_report(self.copy_of('schema-2.1'), True)
        code, out, err = self.run_cli(package, '--verify', '--search', 'Cost', '--fail-on-review')
        self.assertEqual((3, ''), (code, out), err)
        self.assertEqual(['review required: read review.md before giving this package to a model'],
                         err.splitlines())
        code, out, err = self.run_cli(package, '--verify', '--search', 'Cost')
        self.assertEqual((0, ''), (code, err))
        self.assertIn('part-0003', out)

    # "Checks run before anything is printed." / "Together with --part or --search, those lines go
    # to stderr and the part ... or the matches are printed only when everything matches." /
    # "Exit codes: ... 1 --verify found a file missing, changed, refused or not listed"
    def test_failed_verify_beats_search_and_fail_on_review(self):
        package = self.copy_of('schema-2.1')
        part = package / 'parts/0003.md'
        part.write_bytes(part.read_bytes().replace(b'| 80 |', b'| 99 |'))
        self.set_report(package, True)
        code, out, err = self.run_cli(package, '--verify', '--search', 'Cost', '--fail-on-review')
        self.assertEqual((1, ''), (code, out), err)
        self.assertEqual(1, len(err.strip().splitlines()), err)
        self.assertIn('parts/0003.md: sha256 mismatch', err)
        self.assertNotIn('review required', err)

    # "--json prints one JSON object instead of text: source, content_policy, verified (true only
    # when --verify was given and passed), review (required, review_items_detected,
    # hidden_content_included, file), and part ..., parts or search"
    def test_search_json_document_fields(self):
        with (SAMPLES / 'schema-2.1/manifest.json').open(encoding='utf-8') as handle:
            raw = json.load(handle)
        code, out, err = self.run_cli(SAMPLES / 'schema-2.1', '--search', 'cost', '--json')
        self.assertEqual((0, ''), (code, err))
        document = json.loads(out)
        self.assertIs(False, document['verified'])
        self.assertEqual('visible', document['content_policy'])
        self.assertEqual({key: raw['source'][key] for key in ('file_name', 'sha256', 'format')},
                         document['source'])
        self.assertEqual({'required': False, 'review_items_detected': False, 'hidden_content_included': False,
                          'file': 'review.md'}, document['review'])
        self.assertEqual('cost', document['search']['text'])
        self.assertNotIn('markdown', document['search']['parts'][0])
        code, out, err = self.run_cli(SAMPLES / 'schema-2.1', '--verify', '--search', 'cost', '--json')
        self.assertEqual((0, ''), (code, err))
        self.assertIs(True, json.loads(out)['verified'])

    # "when verification fails it holds problems and no text"
    def test_search_json_reports_problems_and_no_text(self):
        package = self.copy_of('schema-2.1')
        (package / 'parts/0005.md').unlink()
        (package / 'review.md').write_text('changed review page\n', encoding='utf-8')
        code, out, err = self.run_cli(package, '--verify', '--search', 'Cost', '--json')
        self.assertEqual((1, ''), (code, err))
        document = json.loads(out)
        self.assertIs(False, document['verified'])
        self.assertEqual(['parts/0005.md: missing',
                          'review.md: size mismatch (expected 1223 bytes, got 20)'],
                         document['problems'])
        self.assertNotIn('search', document)
        self.assertNotIn('part', document)
        self.assertNotIn('parts', document)

    # "Exit codes: ... 141 the output pipe was closed early (as by head); each error is one line on stderr."
    def test_pipe_closed_during_json_part_exit_141(self):
        package = self.copy_of('schema-2.1')
        self.relist(package, 'parts/0002.md', ('# ' + 'キ.line ' * 40000 + '\n').encode('utf-8'))
        process = subprocess.Popen([sys.executable, '-I', str(READER), str(package), '--verify',
                                    '--part', 'part-0002', '--json'],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        process.stdout.read(64)
        process.stdout.close()
        error = process.stderr.read()
        process.stderr.close()
        self.assertEqual(141, process.wait(timeout=60))
        self.assertEqual(b'', error)

    # "Labels, file names and lines are printed with control, line-separator and bidirectional
    # formatting characters as \xNN/\uNNNN escapes, so a package cannot fake lines or terminal
    # output (the part's own Markdown, and --json's strings once parsed, are unchanged)."
    def test_package_text_never_prints_raw_control_or_bidi_characters(self):
        package = self.copy_of('schema-2.0')
        planted = 'BEFORE\x02MID\u202eTAIL\u200fEND'
        rel = 'parts/0001.md'
        self.relist(package, rel, ('# T1\n' + planted + ' MARKER\ntail\n').encode('utf-8'))
        for path in (package, self.zipped(package)):
            code, out, err = self.run_cli(path, '--verify', '--search', 'MARKER')
            self.assertEqual((0, ''), (code, err))
            self.assertNotIn(planted, out)
            for line in out.splitlines():
                self.assertIsNone(reader.UNSHOWN_RE.search(line), line)
            self.assertIn('BEFORE\\x02MID\\u202eTAIL\\u200fEND MARKER', out)
            code, out, err = self.run_cli(path, '--verify', '--part', 'part-0001', '--json')
            self.assertEqual((0, ''), (code, err))
            self.assertEqual(planted + ' MARKER', json.loads(out)['part']['markdown'].splitlines()[1])
            for chunk in ('\\u0002', '\\u202e', '\\u200f'):
                self.assertIn(chunk, out)
            for line in out.splitlines():
                self.assertIsNone(reader.UNSHOWN_IN_JSON_RE.search(line), line)


class UnlistedFiles(EvoFixture):
    # "verification also reports any other file in the package, such as an image under assets/ that
    # the manifest does not list" and "names are matched exactly, as in a ZIP: parts/0003.MD is not
    # parts/0003.md, even where the file system would open one for the other".
    def test_file_in_another_letter_case_is_unlisted(self):
        package = self.copy_of('schema-2.1')
        shutil.copyfile(package / 'parts/0003.md', package / 'parts/0099.MD')
        problems = ['parts/0099.MD: not listed in the manifest']
        self.assertEqual(problems, self.problems_of(package))
        code, out, err = self.run_cli(package, '--verify')
        self.assertEqual((1, problems, ''), (code, out.splitlines(), err))
        code, out, err = self.run_cli(package, '--verify', '--part', 'part-0003')
        self.assertEqual((1, [], problems), (code, out.splitlines(), err.splitlines()))

    # "names are matched exactly, as in a ZIP: parts/0003.MD is not parts/0003.md, even where the
    # file system would open one for the other (macOS, Windows)" and "A name differing in letter
    # case or Unicode form is a missing file everywhere, as in a ZIP".
    @unittest.skipIf(os.name == 'nt', 'a name in another Unicode form cannot be chosen freely on Windows')
    def test_folder_name_in_another_unicode_form_hides_a_listed_file(self):
        package = self.copy_of('schema-2.1')
        nfc, nfd = '\u00e9.png', 'e\u0301.png'  # same name composed, or e + combining acute
        (package / 'assets').mkdir()
        (package / 'assets' / nfd).write_bytes(b'pixel')

        def change(raw):
            raw['files'].append({'path': 'assets/' + nfc, 'bytes': 5,
                                 'sha256': hashlib.sha256(b'pixel').hexdigest()})
        self.patch_manifest(package, change)
        problems = self.problems_of(package)
        self.assertEqual(['assets/' + nfc + ': missing',
                          'assets/' + nfd + ': not listed in the manifest'], problems)
        code, out, err = self.run_cli(package, '--verify')
        self.assertEqual((1, problems), (code, out.splitlines()))

    # "a link, pipe or device is refused without being opened" and "--verify prints ... one line per
    # file that is ... refused (a link, pipe or device, ...)".
    def test_link_stands_in_place_of_a_file_and_is_not_followed(self):
        if not hasattr(os, 'symlink'):
            self.skipTest('symbolic links are unavailable')
        package = self.copy_of('schema-2.1')
        outside = self.unique('outside')
        outside.mkdir()
        (outside / 'note.md').write_bytes(b'# FROM OUTSIDE\n')
        part = package / 'parts/0003.md'
        data = part.read_bytes()
        part.unlink()
        try:
            os.symlink(str(outside / 'note.md'), str(part))
        except OSError:
            self.skipTest('symbolic links are unavailable')
        problems = self.problems_of(package)
        self.assertEqual(['parts/0003.md: refused (package path is a link: %r)' % 'parts/0003.md'],
                         problems)
        code, out, err = self.run_cli(package, '--verify', '--part', 'part-0003')
        self.assertEqual((1, ''), (code, out), err)
        self.assertEqual(problems, err.splitlines())
        self.assertNotIn('OUTSIDE', out + err)
        os.unlink(str(part))
        part.write_bytes(data)

    # "In a package folder, a link, pipe or device is refused without being opened" and
    # "verification also reports any other file in the package ... the manifest does not list."
    def test_link_to_a_folder_is_listed_by_its_own_name(self):
        if not hasattr(os, 'symlink'):
            self.skipTest('symbolic links are unavailable')
        package = self.copy_of('schema-2.1')
        outside = self.unique('outside')
        (outside / 'deep').mkdir(parents=True)
        (outside / 'deep' / 'b.png').write_bytes(b'pixel')
        try:
            os.symlink(str(outside), str(package / 'assets'), target_is_directory=True)
        except OSError:
            self.skipTest('symbolic links are unavailable')
        self.assertEqual(['assets: not listed in the manifest'], self.problems_of(package))
        code, out, err = self.run_cli(package, '--verify')
        self.assertEqual((1, ['assets: not listed in the manifest'], ''), (code, out.splitlines(), err))

    # "--verify prints files ok when every listed file matches its size and SHA-256, and the package
    # holds no other file." An empty folder holds no file, so it changes nothing.
    def test_empty_folder_is_not_a_file(self):
        package = self.copy_of('schema-2.1')
        (package / 'empty-deep' / 'deeper').mkdir(parents=True)
        self.assertEqual([], self.problems_of(package))
        self.assertEqual((0, 'files ok\n', ''), self.run_cli(package, '--verify'))

    # "files Finder and Explorer leave behind - .DS_Store, Thumbs.db, desktop.ini, AppleDouble ._
    # files, __MACOSX/ - are ignored".
    def test_operating_system_leftovers_are_ignored(self):
        package = self.copy_of('schema-2.1')
        for rel in ('.DS_Store', 'Thumbs.db', 'desktop.ini', '._0003.md', 'a/b/.DS_Store',
                    '__MACOSX/x', '__MACOSX/._y'):
            path = package / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'left over')
        self.assertEqual([], self.problems_of(package))
        archive = self.zipped(package)
        self.assertEqual([], self.problems_of(archive))
        self.assertEqual((0, 'files ok\n', ''), self.run_cli(archive, '--verify'))

    # "verification also reports any other file in the package, such as an image under assets/ that
    # the manifest does not list".
    def test_zip_entry_the_manifest_does_not_list_is_reported(self):
        archive = self.zipped(self.copy_of('schema-2.1'))
        with zipfile.ZipFile(archive, 'a', zipfile.ZIP_STORED) as out:
            out.writestr('parts/9999.md', 'planted')
        problems = ['parts/9999.md: not listed in the manifest']
        self.assertEqual(problems, self.problems_of(archive))
        code, out, err = self.run_cli(archive, '--verify', '--part', 'part-0003')
        self.assertEqual((1, []), (code, out.splitlines()))
        self.assertEqual(problems, err.splitlines())

    # "a path would leave the package or holds a control, line-separator or bidirectional formatting
    # character (DocRedock writes none; they could fake lines or reorder names when printed)" ...
    # "must be refused with an explicit error" ... "each error is one line on stderr".
    def test_manifest_paths_that_escape_or_fake_output_are_refused(self):
        for bad in ('../escape.md', 'a/../../b.md', 'evil\x00.md', 'evil\x1b.md', 'evil\u202e.md'):
            package = self.patch_manifest(self.copy_of('schema-2.1'), lambda raw, path=bad: raw['files'][0].update(path=path))
            self.run_refused(package, '--verify')
            self.run_refused(package)
            code, out, err = self.run_cli(package, '--verify')
            printed = (out + err).rstrip('\n')
            self.assertNotIn('\n', printed)
            self.assertIsNone(reader.UNSHOWN_RE.search(printed), repr(printed))

    # "Each manifest file entry contains a relative path, bytes, and SHA-256." / "an id or path
    # appears twice (locations, parts, files)" ... refuse it rather than reading part of it.
    def test_files_listing_one_path_twice_is_refused(self):
        package = self.copy_of('schema-2.1')

        def change(raw):
            raw['files'].append(dict(raw['files'][0]))
        self.patch_manifest(package, change)
        self.assertIn('twice', self.run_refused(package))


class TableChains(EvoFixture):
    # "the parts with one table_id must number its count, hold each index from 1 to count once, and
    # name exactly the blocks before and after them in previous_part and next_part (null at either
    # end), so following next_part reaches every block of the table once and never another table or
    # a circle" ... refuse it rather than reading part of it.
    def test_chain_break_is_refused_before_anything_is_printed(self):
        def both_ends(raw):
            raw['parts'][1]['table_block'].update(previous_part='part-0003', next_part='part-0003')

        def unknown_neighbour(raw):
            raw['parts'][0]['table_block']['next_part'] = 'part-0099'

        def missing_block(raw):
            raw['parts'].pop(2)
        for change in (both_ends, unknown_neighbour, missing_block):
            package = self.patch_manifest(self.copy_of('schema-2.2'), change)
            self.run_refused(package)
            self.run_refused(package, '--verify', '--part', 'part-0002', '--json')
            self.run_refused(package, '--verify')


class IndexConsistency(EvoFixture):
    # "When the source index is read, it must list each manifest part exactly once ... refuse it
    # rather than reading part of it" / "it must ... list each manifest part exactly once"
    def test_source_index_missing_one_part_is_refused(self):
        package = self.copy_of('schema-2.1')
        raw = json.loads((package / 'source-index.json').read_text(encoding='utf-8'))
        raw['parts'].pop()
        self.relist(package, 'source-index.json', json.dumps(raw).encode('utf-8'))
        # Listing parts reads no source index; verifying, or printing a part with its sources, does.
        self.assertEqual(0, self.run_cli(package, '--json')[0])
        self.assertIn('no entry for part-0006', self.run_refused(package, '--verify', '--json'))
        self.assertIn('no entry for part-0006', self.run_refused(package, '--part', 'part-0001', '--json'))

    # "a part names an unknown location" ... "or a path appears twice (locations, parts, files)"
    def test_part_naming_a_doubled_or_unknown_location_is_refused(self):
        def doubled(raw):
            raw['parts'][0]['source_ids'] = raw['parts'][0]['source_ids'] + ['loc-0001']

        def unknown(raw):
            raw['parts'][0]['source_ids'] = ['loc-9999']
        for change in (doubled, unknown):
            package = self.patch_manifest(self.copy_of('schema-2.1'), change)
            self.run_refused(package, '--json')

    # "have one cell address (or null) per node ID where cell_addresses is present"
    def test_cell_addresses_must_pair_with_node_ids(self):
        package = self.copy_of('schema-2.2')
        raw = json.loads((package / 'source-index.json').read_text(encoding='utf-8'))
        group = next(item for part in raw['parts'] for item in part['sources']
                     if item.get('cell_addresses'))
        group['cell_addresses'].append('Z9')
        self.relist(package, 'source-index.json', json.dumps(raw).encode('utf-8'))
        self.assertIn('cell_addresses', self.run_refused(package, '--verify', '--json'))

    # "A part lists a node once, and every part that lists a node ... puts it at the same location
    # and cell ... this holds for 1.x inline sources too."
    def test_inline_node_at_two_locations_is_refused(self):
        def change(raw):
            # part-0002 claims part-0001's first node, under its own heading.
            raw['parts'][1]['node_ids'][0] = 'n_4L5SQ2FD7FAKPOC3'
            raw['parts'][1]['sources'][0]['node_id'] = 'n_4L5SQ2FD7FAKPOC3'
        package = self.patch_manifest(self.copy_of('schema-1.0'), change)
        self.assertIn("parts[1] puts node 'n_4L5SQ2FD7FAKPOC3' at source-0003 without a cell, "
                      "but part part-0001 puts it at source-0001 without a cell",
                      self.run_refused(package, '--verify', '--json'))

    # "schema 1.x pairs them one to one": "1.x node_ids and sources of the same length"
    def test_inline_sources_must_pair_with_node_ids(self):
        def change(raw):
            raw['parts'][0]['node_ids'].pop()
        package = self.patch_manifest(self.copy_of('schema-1.0'), change)
        self.assertIn('pairs them one to one', self.run_refused(package, '--json'))


class Library(EvoFixture):
    # "read_verified_part ... returns the part's Markdown ... exactly the bytes verified, with the
    # part's locations and table block, its source-index groups and the review state" /
    # "Raises KeyError for an unknown part id, PackageVerificationError when a file is missing,
    # changed, refused or not listed"
    def test_read_verified_part_and_verify_files_use_the_bytes_given(self):
        package = self.copy_of('schema-2.1')
        with reader.open_package(package) as handle:
            manifest = reader.load_manifest(handle)
            verified = reader.read_verified_part(handle, manifest, 'part-0003')
            self.assertEqual(self.markdown_of(package, 'parts/0003.md'), verified['markdown'])
            self.assertEqual('part-0003', verified['part']['id'])
            self.assertEqual({'required': False, 'review_items_detected': False,
                              'hidden_content_included': False}, verified['review'])
            self.assertTrue(verified['sources'])
            self.assertTrue(all(group['source_id'] in {location['id'] for location in manifest['locations']}
                                for group in verified['sources']))
            self.assertRaises(KeyError, reader.read_verified_part, handle, manifest, 'part-0099')
            entry = {item['path']: item for item in manifest['files']}['parts/0003.md']
            planted = b'# planted instead of %d bytes\n' % entry['bytes']
            planted = planted[:entry['bytes']].ljust(entry['bytes'])
            problems = reader.verify_files(handle, manifest, {'parts/0003.md': planted})
            self.assertEqual(['parts/0003.md: sha256 mismatch (expected %s, got %s)'
                              % (entry['sha256'], hashlib.sha256(planted).hexdigest())],
                             problems)
            self.assertEqual([], reader.verify_files(handle, manifest, {'parts/0003.md': (package / 'parts/0003.md').read_bytes()}))
            (package / 'parts/0004.md').write_bytes(b'# planted\n')
            self.assertRaises(reader.PackageVerificationError, reader.read_verified_part,
                              handle, manifest, 'part-0003')

    # "read_review: what report.json says about review" / "--fail-on-review stops ... when
    # report.json says review is required": the library flag drives the CLI stop.
    def test_read_review_tracks_report_changes(self):
        package = self.copy_of('schema-2.1')
        with reader.open_package(package) as handle:
            manifest = reader.load_manifest(handle)
            self.assertEqual({'required': False, 'review_items_detected': False,
                              'hidden_content_included': False}, reader.read_review(handle, manifest))
        self.set_report(package, True)
        self.assertIn('review required', self.run_cli(package, '--part', 'part-0001', '--fail-on-review')[2])
        with reader.open_package(package) as handle:
            manifest = reader.load_manifest(handle)
            self.assertTrue(reader.read_review(handle, manifest)['required'])

    # "--search TEXT lists the parts containing the text, ignoring case, full-width forms and
    # Markdown escapes" / "contents maps package paths to bytes already read (and verified)".
    def test_search_parts_comparison_and_contents(self):
        package = self.copy_of('schema-2.1')
        rel = 'parts/0001.md'
        self.relist(package, rel, ('# P1\nWide ＣＡＮＤＩＤＡＴＥ\nEsc cand\\_idate\nRef &amp;amp\n'
                                   'BEFORE\x02MID MARKER\ntail\n').encode('utf-8'))
        with reader.open_package(package) as handle:
            manifest = reader.load_manifest(handle)
            for needle in ('wide candidate', 'candidate', 'CAND\\_IDATE', 'ref &amp'):
                found = {part['id'] for part, lines in reader.search_parts(handle, manifest, needle)}
                self.assertIn('part-0001', found, needle)
            self.assertEqual([], reader.search_parts(handle, manifest, 'zzz-not-there'))
            self.assertEqual([], reader.search_parts(handle, manifest, 'cand idate'))
            matches = reader.search_parts(handle, manifest, 'marker',
                                          {rel: ('# P1\nhidden MARKER line\n').encode('utf-8')})
            self.assertEqual([('part-0001', [(2, 'hidden MARKER line')])],
                             [(part['id'], lines) for part, lines in matches])
            matches = reader.search_parts(handle, manifest, 'BEFORE')
            lines = matches[0][1]
            self.assertEqual(5, lines[0][0])
            self.assertIn('\x02', lines[0][1])  # the library returns the line as written
