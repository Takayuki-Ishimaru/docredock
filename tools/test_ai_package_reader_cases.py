"""The shared reader cases (tests/DocRedock.Tests/Fixtures/AiPackage/reader-cases.json) against
docs/examples/ai_package_reader.py. tests/DocRedock.Tests/Api/AiPackageReaderCaseTests.cs holds the
.NET AiPackageManifestReader to the same file, so both readers accept and refuse alike."""
import contextlib
import hashlib
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
CASES = json.loads((SAMPLES / 'reader-cases.json').read_text(encoding='utf-8'))['cases']


def pointer_segments(pointer):
    """The segments of a JSON pointer; a segment [key=value] runs to its closing bracket, so the
    value may contain "/"."""
    assert pointer.startswith('/'), pointer
    segments, rest = [], pointer[1:]
    while True:
        end = rest.index(']') + 1 if rest.startswith('[') else (rest.find('/') if '/' in rest else len(rest))
        segments.append(rest[:end].replace('~1', '/').replace('~0', '~'))
        if end >= len(rest):
            return segments
        assert rest[end] == '/', pointer
        rest = rest[end + 1:]


def child(container, segment):
    if isinstance(container, list):
        if segment.startswith('['):
            key, value = segment[1:-1].split('=', 1)
            matches = [i for i, item in enumerate(container) if isinstance(item, dict) and item.get(key) == value]
            assert len(matches) == 1, (segment, matches)
            return matches[0]
        return len(container) if segment == '-' else int(segment)
    return segment


def apply_change(package, change):
    def path_of(rel):
        return package.joinpath(*rel.split('/'))

    if 'set' in change or 'remove' in change:
        rel = change.get('set') or change.get('remove')
        document = json.loads(path_of(rel).read_text(encoding='utf-8'))
        *parents, last = pointer_segments(change['at'])
        container = document
        for segment in parents:
            container = container[child(container, segment)]
        key = child(container, last)
        if 'remove' in change:
            del container[key]
        elif isinstance(container, list) and key == len(container):
            container.append(change['to'])
        else:
            container[key] = change['to']
        path_of(rel).write_text(json.dumps(document, ensure_ascii=False), encoding='utf-8')
    elif 'replace' in change:
        path = path_of(change['replace'])
        text = path.read_text(encoding='utf-8')
        assert text.count(change['text']) == 1, change
        path.write_bytes(text.replace(change['text'], change['with']).encode('utf-8'))
    elif 'write' in change:
        path = path_of(change['write'])
        path.parent.mkdir(parents=True, exist_ok=True)
        text = change['text'] if 'text' in change else json.dumps(change['json'], ensure_ascii=False)
        path.write_bytes(text.encode('utf-8'))
    elif 'rename' in change:
        path_of(change['rename']).rename(path_of(change['to']))
    elif 'delete' in change:
        path_of(change['delete']).unlink()
    elif 'reseal' in change:
        data = path_of(change['reseal']).read_bytes()
        manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        entry = next(item for item in manifest['files'] if item['path'] == change['reseal'])
        entry.update(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        (package / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False), encoding='utf-8')
    else:
        raise AssertionError('unknown change %r' % change)


def zip_of(directory, target):
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(directory.rglob('*'), key=lambda p: p.relative_to(directory).as_posix().encode('utf-8')):
            if path.is_file():
                archive.write(path, path.relative_to(directory).as_posix())
    return target


class SharedReaderCases(unittest.TestCase):
    def test_every_case_is_accepted_or_refused_as_the_case_file_says(self):
        self.assertGreaterEqual(len(CASES), 40)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for number, case in enumerate(CASES):
                package = root / ('%02d' % number)
                shutil.copytree(SAMPLES / case['from'], package)
                for change in case['changes']:
                    apply_change(package, change)
                forms = {'dir': package, 'zip': zip_of(package, root / ('%02d.zip' % number))}
                for form in case.get('forms', ['dir', 'zip']):
                    with self.subTest(case=case['name'], form=form):
                        out, err = io.StringIO(), io.StringIO()
                        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                            code = reader.main([str(forms[form]), '--verify'])
                        expected = case['verify']
                        self.assertEqual(expected['exit'], code, (out.getvalue(), err.getvalue()))
                        self.assertNotIn('Traceback', err.getvalue())
                        if code == 2:
                            self.assertEqual('', out.getvalue())
                            self.assertEqual(1, len(err.getvalue().splitlines()), err.getvalue())
                            for text in expected['says']:
                                self.assertIn(text, err.getvalue())
                        else:
                            self.assertEqual(expected['lines'], out.getvalue().splitlines())
                            self.assertEqual('', err.getvalue())

    def test_pointer_segments_keep_slashes_inside_brackets(self):
        self.assertEqual(['files', '[path=parts/0003.md]', 'path'], pointer_segments('/files/[path=parts/0003.md]/path'))
        self.assertEqual(['parts', '-'], pointer_segments('/parts/-'))
        self.assertEqual(['a/b', 'c~d'], pointer_segments('/a~1b/c~0d'))


if __name__ == '__main__':
    unittest.main()
