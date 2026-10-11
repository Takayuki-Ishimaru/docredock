import importlib.util
import unittest
import base64
import gzip
import re
import tempfile
import subprocess
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("generator", Path(__file__).with_name("generate-pdf-japan1.py"))
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)

class CMapGeneratorTests(unittest.TestCase):
    def test_chars_and_sequences(self):
        self.assertEqual({1: "A", 2: "AB", 3: chr(0x20000)}, g.parse_cmap("3 beginbfchar <0001><0041> <0002><00410042> <0003><D840DC00> endbfchar"))
    def test_ranges_and_arrays(self):
        self.assertEqual({1: "A", 2: "B", 3: "C", 4: "X", 5: "YZ"}, g.parse_cmap("2 beginbfrange <0001><0003><0041> <0004><0005>[<0058><0059005A>] endbfrange"))
    def test_comments(self):
        self.assertEqual({1: "A"}, g.parse_cmap("% 1 beginbfchar <0002><0042> endbfchar\n1 beginbfchar <0001><0041> endbfchar"))
    def test_duplicate(self):
        with self.assertRaises(ValueError): g.parse_cmap("2 beginbfchar <0001><0041> <0001><0042> endbfchar")
    def test_bad_counts(self):
        with self.assertRaises(ValueError): g.parse_cmap("2 beginbfchar <0001><0041> endbfchar")
    def test_bad_unicode(self):
        with self.assertRaises(ValueError): g.parse_cmap("1 beginbfchar <0001><D800> endbfchar")
    def test_reversed_range(self):
        with self.assertRaises(ValueError): g.parse_cmap("1 beginbfrange <0003><0001><0041> endbfrange")
    def test_array_size(self):
        with self.assertRaises(ValueError): g.parse_cmap("1 beginbfrange <0001><0002>[<0041>] endbfrange")
    def test_pinned_source(self):
        generated = (Path(__file__).resolve().parents[1] / "src/DocRedock.Formats.Pdf/PdfJapan1UnicodeData.g.cs").read_text()
        encoded = "".join(re.findall(r'"([A-Za-z0-9+/=]+)"', generated))
        data = gzip.decompress(base64.b64decode(encoded))
        source = g.generate(data)
        self.assertIn("2dd5e53fb74a01718b9dfd448a0d1cce6fff2aa5", source)
        self.assertIn("BSD-3-Clause", source)
        self.assertIn("internal static class PdfJapan1UnicodeData", source)
        self.assertEqual(source, g.generate(data))
        self.assertEqual(generated, source)
        with tempfile.TemporaryDirectory() as directory:
            input_path = Path(directory) / "input"
            output_path = Path(directory) / "output.cs"
            input_path.write_bytes(data)
            command = [sys.executable, str(Path(g.__file__)), "--input", str(input_path), "--output", str(output_path)]
            subprocess.run(command, check=True, capture_output=True, timeout=10)
            subprocess.run(command + ["--check"], check=True, capture_output=True, timeout=10)
            output_path.write_text("stale")
            self.assertNotEqual(0, subprocess.run(command + ["--check"], capture_output=True, timeout=10).returncode)
        with self.assertRaises(ValueError): g.generate(data + b" ")
        entries = g.parse_cmap(data.decode("ascii"))
        self.assertGreater(len(entries), 23000)
        self.assertEqual("\ufffd", entries[0])
        self.assertEqual("\u00a5", entries[0x003D])

if __name__ == "__main__": unittest.main()
