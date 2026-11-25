"""Tests for the binary publication privacy gate."""

from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

TOOL = Path(__file__).resolve().parent / "check-distribution-privacy.py"
_spec = importlib.util.spec_from_file_location("check_distribution_privacy", TOOL)
mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mod)

HEADER = b"RSDS" + b"\x01" * 20


def record(path: str) -> bytes:
    return HEADER + path.encode("utf-8") + b"\x00"


def run_cli(root: Path) -> tuple[int, str]:
    proc = subprocess.run(
        [sys.executable, str(TOOL), "--root", str(root)],
        capture_output=True,
        text=True,
        timeout=30,
    )
    return proc.returncode, proc.stdout + proc.stderr


class TempDirCase(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def write(self, name: str, data: bytes) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def check(self, name: str = "app.exe", data: bytes | None = None) -> tuple[int, str]:
        if data is not None:
            self.write(name, data)
        return run_cli(self.root)


class UnsafePathUnit(unittest.TestCase):
    def test_windows_backslash(self):
        self.assertTrue(mod.is_unsafe_path(r"C:\Users\bob\src\a.pdb"))

    def test_windows_forward_slash(self):
        self.assertTrue(mod.is_unsafe_path("C:/Users/bob/src/a.pdb"))

    def test_windows_case_insensitive(self):
        self.assertTrue(mod.is_unsafe_path("d:\\USERS\\Bob\\a.pdb"))

    def test_unix_home(self):
        self.assertTrue(mod.is_unsafe_path("/home/bob/src/a.pdb"))

    def test_mac_users(self):
        self.assertTrue(mod.is_unsafe_path("/Users/bob/src/a.pdb"))

    def test_mapped_safe(self):
        self.assertFalse(mod.is_unsafe_path("/_/src/a.pdb"))

    def test_relative_safe(self):
        self.assertFalse(mod.is_unsafe_path("src/a.pdb"))

    def test_mapped_users_substring_safe(self):
        self.assertFalse(mod.is_unsafe_path("/_/src/users/bob/a.pdb"))


class TargetSelectionUnit(unittest.TestCase):
    def test_suffixes_case_insensitive(self):
        for name in ("a.exe", "A.EXE", "b.dll", "B.DLL", "c.pdb", "C.PDB"):
            self.assertTrue(mod.is_target(name), name)

    def test_host_names(self):
        self.assertTrue(mod.is_target("DocRedock"))
        self.assertTrue(mod.is_target("DocRedock.Cli"))

    def test_other_names_ignored(self):
        for name in ("notes.txt", "lib.so", "readme", "DocRedock.Cli.map"):
            self.assertFalse(mod.is_target(name), name)


class CliBehavior(TempDirCase):
    def test_clean_mapped_and_relative(self):
        data = b"MZ" + record("/_/src/a.pdb") + record("a.pdb")
        code, out = self.check(data=data)
        self.assertEqual(code, 0, out)

    def test_unsafe_windows(self):
        code, out = self.check(data=b"MZ" + record(r"C:\Users\bob\a.pdb"))
        self.assertNotEqual(code, 0)
        self.assertIn("CV001", out)

    def test_unsafe_home(self):
        code, _ = self.check(data=b"MZ" + record("/home/bob/a.pdb"))
        self.assertNotEqual(code, 0)

    def test_unsafe_mac_users(self):
        code, _ = self.check(data=b"MZ" + record("/Users/bob/a.pdb"))
        self.assertNotEqual(code, 0)

    def test_report_never_leaks_path_or_root(self):
        secret = r"C:\Users\PrivatePerson\secret\a.pdb"
        code, out = self.check(data=b"MZ" + record(secret))
        self.assertNotEqual(code, 0)
        self.assertNotIn("PrivatePerson", out)
        self.assertNotIn(secret, out)
        self.assertNotIn(str(self.root), out)

    def test_plain_text_not_flagged(self):
        text = b"built on /home/bob/src/a.pdb and C:\\Users\\bob\\x.pdb today"
        code, out = self.check(data=text)
        self.assertEqual(code, 0, out)

    def test_uppercase_extensions_scanned(self):
        self.write("APP.EXE", b"MZ" + record("/home/bob/a.pdb"))
        code, _ = run_cli(self.root)
        self.assertNotEqual(code, 0)

    def test_dll_and_pdb_extensions(self):
        self.write("lib.DLL", b"MZ" + record("/home/bob/a.pdb"))
        code, _ = run_cli(self.root)
        self.assertNotEqual(code, 0)

    def test_non_target_extension_ignored(self):
        self.write("notes.txt", b"MZ" + record("/home/bob/a.pdb"))
        code, out = run_cli(self.root)
        self.assertNotEqual(code, 0)
        self.assertIn("no executable", out)

    def test_recursive_subdirectory(self):
        self.write("sub/deep/app.dll", b"MZ" + record("/home/bob/a.pdb"))
        code, out = run_cli(self.root)
        self.assertNotEqual(code, 0)
        self.assertIn("sub/deep/app.dll", out)

    def test_missing_root(self):
        code, _ = run_cli(self.root / "nope")
        self.assertNotEqual(code, 0)

    def test_missing_root_does_not_print_root_name(self):
        missing = self.root / "PRIVROOTSECRETNAME-913"
        code, out = run_cli(missing)
        self.assertNotEqual(code, 0)
        self.assertNotIn("PRIVROOTSECRETNAME-913", out)

    def test_empty_root_no_binaries(self):
        code, out = run_cli(self.root)
        self.assertNotEqual(code, 0)
        self.assertIn("no executable", out)

    def test_read_failure_mocked(self):
        self.write("app.exe", b"MZ" + record("/_/src/a.pdb"))
        with mock.patch.object(mod, "scan_file", side_effect=PermissionError):
            findings, errors = mod.check_root(self.root)
            list(findings)
        self.assertIn("app.exe", errors)

    def test_read_failure_cli_nonzero(self):
        self.write("app.exe", b"MZ" + record("/_/src/a.pdb"))
        code, out = self._run_with_read_error()
        self.assertNotEqual(code, 0)
        self.assertIn("cannot read", out)

    def _run_with_read_error(self):
        # Drive main() in-process so the mock applies to the module under test.
        from contextlib import redirect_stderr, redirect_stdout
        import io

        buf_out, buf_err = io.StringIO(), io.StringIO()
        with mock.patch.object(mod, "scan_file", side_effect=PermissionError):
            with redirect_stdout(buf_out), redirect_stderr(buf_err):
                code = mod.main(["--root", str(self.root)])
        return code, buf_out.getvalue() + buf_err.getvalue()

    def test_symlink_outside_root_not_followed(self):
        fd, outside_str = tempfile.mkstemp(suffix=".pdb", prefix="outside_secret_")
        os.close(fd)
        outside = Path(outside_str)
        outside.write_bytes(b"MZ" + record("/home/bob/a.pdb"))
        self.addCleanup(outside.unlink, missing_ok=True)
        link = self.root / "link.pdb"
        try:
            os.symlink(outside, link)
        except (OSError, NotImplementedError) as exc:
            if isinstance(exc, PermissionError) or getattr(exc, "winerror", None) == 1314:
                self.skipTest("symlink creation not permitted on this OS")
            raise
        self.write("app.exe", b"MZ" + record("/_/src/a.pdb"))
        code, out = run_cli(self.root)
        self.assertEqual(code, 0, out)


class HostAppHostFiles(TempDirCase):
    def test_host_doredock_unsafe_only_host(self):
        self.write("DocRedock", b"MZ" + record("/home/bob/a.pdb"))
        code, out = run_cli(self.root)
        self.assertNotEqual(code, 0, out)
        self.assertIn("DocRedock", out)

    def test_host_doredock_clean_only_host(self):
        self.write("DocRedock", b"MZ" + record("/_/src/a.pdb"))
        code, out = run_cli(self.root)
        self.assertEqual(code, 0, out)

    def test_host_cli_unsafe_only_host(self):
        self.write("DocRedock.Cli", b"MZ" + record("C:\\Users\\bob\\a.pdb"))
        code, out = run_cli(self.root)
        self.assertNotEqual(code, 0, out)
        self.assertIn("DocRedock.Cli", out)

    def test_host_cli_clean_only_host(self):
        self.write("DocRedock.Cli", b"MZ" + record("a.pdb"))
        code, out = run_cli(self.root)
        self.assertEqual(code, 0, out)


class DebugPathRedaction(TempDirCase):
    def test_redaction_preserves_size_headers_and_other_binary_bytes(self):
        private = r"C:\Users\PrivatePerson\build\dependency.pdb"
        data = b"prefix" + record(private) + b"suffix" + record("/_/safe.pdb")
        path = self.write("app.exe", data)
        mod.redact_file(path)
        updated = path.read_bytes()
        self.assertEqual(len(updated), len(data))
        self.assertEqual(updated[:len(b"prefix") + len(HEADER)],
                         data[:len(b"prefix") + len(HEADER)])
        self.assertTrue(updated.endswith(b"suffix" + record("/_/safe.pdb")))
        self.assertNotIn(b"PrivatePerson", updated)
        self.assertIn(b"/_/dependency.pdb\x00", updated)
        self.assertEqual(list(mod.scan_file(path)), [])

    def test_clean_binary_is_unchanged(self):
        data = b"MZ" + record("/_/safe.pdb")
        path = self.write("app.exe", data)
        mod.redact_file(path)
        self.assertEqual(path.read_bytes(), data)

    def test_cli_redaction_failure_does_not_expose_root(self):
        from contextlib import redirect_stderr
        import io

        self.write("app.exe", record("/home/private/a.pdb"))
        stderr = io.StringIO()
        with mock.patch.object(mod, "redact_file", side_effect=PermissionError):
            with redirect_stderr(stderr):
                code = mod.main(["--root", str(self.root), "--redact"])
        self.assertNotEqual(code, 0)
        self.assertNotIn(str(self.root), stderr.getvalue())

    def test_cli_redacts_multiple_records_before_verification(self):
        path = self.write("app.exe", record("/home/private/a.pdb")
                          + record("/Users/private/b.pdb"))
        proc = subprocess.run([sys.executable, str(TOOL), "--root", str(self.root),
                               "--redact"], capture_output=True, text=True, timeout=30)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        self.assertNotIn("private", proc.stdout + proc.stderr)
        self.assertEqual(list(mod.scan_file(path)), [])


class StreamingBoundary(TempDirCase):
    def _split_magic(self, n: int):
        rec = record("/home/bob/a.pdb")
        c = mod.CHUNK_SIZE
        data = b"\x00" * (c - n) + rec + b"\x00" * 50
        return mod.scan_file(self.write("boundary.exe", data))

    def test_magic_split_1(self):
        self.assertEqual([off for off, _ in self._split_magic(1)], [mod.CHUNK_SIZE - 1])

    def test_magic_split_2(self):
        self.assertEqual([off for off, _ in self._split_magic(2)], [mod.CHUNK_SIZE - 2])

    def test_magic_split_3(self):
        self.assertEqual([off for off, _ in self._split_magic(3)], [mod.CHUNK_SIZE - 3])

    def test_record_split_on_chunk_boundary(self):
        rec = record("/home/bob/a.pdb")
        c = mod.CHUNK_SIZE
        data = b"\x00" * (c - 10) + rec + b"\x00" * 50
        code, out = self.check(data=data)
        self.assertNotEqual(code, 0, out)
        self.assertIn("CV001", out)

    def test_path_terminator_on_boundary(self):
        rec = record("/home/bob/a.pdb")
        c = mod.CHUNK_SIZE
        data = b"\x00" * (c - len(rec) + 5) + rec + b"\x00" * 50
        code, _ = self.check(data=data)
        self.assertNotEqual(code, 0)

    def test_clean_record_split_not_flagged(self):
        rec = record("/_/src/a.pdb")
        c = mod.CHUNK_SIZE
        data = b"\x00" * (c - 10) + rec + b"\x00" * 50
        code, out = self.check(data=data)
        self.assertEqual(code, 0, out)

    def test_duplicate_avoidance_single_finding(self):
        rec = record("/home/bob/a.pdb")
        c = mod.CHUNK_SIZE
        data = b"\x00" * (c - 10) + rec + b"\x00" * 50
        findings = list(mod.scan_file(self.write("app.exe", data)))
        self.assertEqual(len(findings), 1)
        self.assertEqual(findings[0][0], c - 10)
        self.assertEqual(findings[0][1], "CV001")

    def test_multiple_records_offsets(self):
        r1 = record("/home/bob/a.pdb")
        r2 = record("/_/src/ok.pdb")
        r3 = record("C:\\Users\\x\\y.pdb")
        data = r1 + r2 + r3
        findings = list(mod.scan_file(self.write("app.exe", data)))
        self.assertEqual([off for off, _ in findings], [0, len(r1) + len(r2)])

    def test_max_path_length_boundary(self):
        name = "/home/bob/" + "a" * (mod.MAX_PATH_LEN - len("/home/bob/"))
        data = b"\x00" * 4 + record(name)
        findings = list(mod.scan_file(self.write("app.exe", data)))
        self.assertEqual(len(findings), 1)

    def test_overlong_path_not_a_record(self):
        name = "/home/bob/" + "a" * (mod.MAX_PATH_LEN + 10)
        data = b"\x00" * 4 + record(name)
        findings = list(mod.scan_file(self.write("app.exe", data)))
        self.assertEqual(findings, [])

    def test_scan_file_is_generator(self):
        import types

        self.write("app.exe", b"MZ" + record("/_/src/a.pdb"))
        result = mod.scan_file(self.root / "app.exe")
        self.assertIsInstance(result, types.GeneratorType)


if __name__ == "__main__":
    unittest.main()
