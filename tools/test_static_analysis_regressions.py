"""Regression tests for the corpus-regression harness's subprocess lifetime.

These drive real POSIX subprocesses (no .NET, no Office fixtures) to pin the
rule that a timed-out conversion must not leave the CLI's descendants running
with the output pipes still open.
"""

import importlib.util
import json
import os
import signal
import subprocess
import tempfile
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest import mock

SPEC = importlib.util.spec_from_file_location(
    "corpus_regression", Path(__file__).with_name("corpus-regression") / "run.py"
)
corpus_regression = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(corpus_regression)

MARKER_DELAY_SEC = 1.0

TREE_BODY = (
    '( sleep {delay}; touch "{marker}" ) &\n'
    'echo $! > "{pid}"\n'
    "sleep 10\n"
)
PIPE_HOLDER_BODY = (
    '( sleep {delay}; echo MARKER ) &\n'
    'echo $! > "{pid}"\n'
    "exit 0\n"
)


class FakeCli:
    """A shell script in a temp dir whose background child can be reaped."""

    def __init__(self, test, name, template):
        directory = tempfile.TemporaryDirectory()
        test.addCleanup(directory.cleanup)
        root = Path(directory.name)
        self.marker = root / "marker"
        self.pid_file = root / "child.pid"
        self.script = root / (name + ".sh")
        self.script.write_text(
            "#!/bin/sh\n" + template.format(delay=MARKER_DELAY_SEC, marker=self.marker, pid=self.pid_file),
            encoding="utf-8",
        )
        self.script.chmod(0o755)
        test.addCleanup(self._reap_child)

    def command(self):
        return [str(self.script)]

    def child_pid(self):
        try:
            return int(self.pid_file.read_text(encoding="utf-8").strip())
        except (OSError, ValueError):
            return None

    def child_is_alive(self):
        pid = self.child_pid()
        if pid is None:
            return False
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True

    def _reap_child(self):
        pid = self.child_pid()
        if pid is None:
            return
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            return
        deadline = time.time() + 2.0
        while time.time() < deadline:
            try:
                if os.waitpid(pid, os.WNOHANG)[0]:
                    return
            except (ChildProcessError, OSError):
                return
            time.sleep(0.02)


@unittest.skipIf(os.name != "posix", "process-group semantics are POSIX specific")
class ProcessTreeTimeoutTests(unittest.TestCase):
    def test_timeout_kills_descendants_that_would_write_after_the_timeout(self):
        cli = FakeCli(self, "tree", TREE_BODY)
        started = time.time()
        result = corpus_regression.run_cli(cli.command(), timeout=0.2)
        elapsed = time.time() - started
        self.assertTrue(result["timed_out"])
        self.assertIsNone(result["returncode"])
        self.assertLess(elapsed, 1.0, "run_cli must not wait for the timed-out tree")
        time.sleep(max(0, MARKER_DELAY_SEC + 0.2 - (time.time() - started)))
        self.assertFalse(cli.marker.exists(), "a descendant survived the timeout")
        self.assertFalse(cli.child_is_alive(), "the descendant is still running")
        json.dumps(result)

    def test_returns_promptly_when_parent_exits_but_a_descendant_holds_the_pipes(self):
        cli = FakeCli(self, "pipe-holder", PIPE_HOLDER_BODY)
        started = time.time()
        result = corpus_regression.run_cli(cli.command(), timeout=0.2)
        elapsed = time.time() - started
        self.assertTrue(result["timed_out"])
        self.assertLess(elapsed, corpus_regression.PIPE_DRAIN_TIMEOUT_SEC + 1.0)
        self.assertNotIn("MARKER", result["stdout"])
        time.sleep(max(0, MARKER_DELAY_SEC + 0.2 - (time.time() - started)))
        self.assertFalse(cli.child_is_alive(), "the pipe-holding descendant survived")
        json.dumps(result)

    def test_concurrent_success_and_timeout_do_not_kill_each_other(self):
        slow = FakeCli(self, "slow-success", "sleep 0.4\necho ok\n")
        tree = FakeCli(self, "tree", TREE_BODY)
        with ThreadPoolExecutor(max_workers=2) as pool:
            ok_future = pool.submit(corpus_regression.run_cli, slow.command(), 10)
            timeout_future = pool.submit(corpus_regression.run_cli, tree.command(), 0.2)
            ok = ok_future.result()
            timed_out = timeout_future.result()
        self.assertFalse(ok["timed_out"])
        self.assertEqual(0, ok["returncode"])
        self.assertEqual("ok\n", ok["stdout"])
        self.assertTrue(timed_out["timed_out"])
        time.sleep(MARKER_DELAY_SEC + 0.4)
        self.assertFalse(tree.child_is_alive(), "the timed-out tree survived")
        self.assertFalse(tree.marker.exists())

    def test_normal_exit_paths_keep_returncode_and_output(self):
        success = FakeCli(self, "success", "echo out\necho err >&2\n")
        result = corpus_regression.run_cli(success.command(), timeout=10)
        self.assertFalse(result["timed_out"])
        self.assertEqual(0, result["returncode"])
        self.assertEqual("out\n", result["stdout"])
        self.assertEqual("err\n", result["stderr"])

        failing = FakeCli(self, "failing", "echo boom >&2\nexit 3\n")
        result = corpus_regression.run_cli(failing.command(), timeout=10)
        self.assertFalse(result["timed_out"])
        self.assertEqual(3, result["returncode"])
        self.assertEqual("boom\n", result["stderr"])

    def test_timeout_keeps_real_incomplete_utf8_output(self):
        cli = FakeCli(self, "invalid-utf8", "printf '\\377'\\nsleep 10\\n".replace("\\n", "\n"))
        result = corpus_regression.run_cli(cli.command(), timeout=0.2)
        self.assertTrue(result["timed_out"])
        self.assertEqual("\ufffd", result["stdout"])

    def test_launch_failure_is_reported_without_a_traceback(self):
        result = corpus_regression.run_cli(["/definitely/not/a/real/cli-binary"], timeout=5)
        self.assertFalse(result["timed_out"])
        self.assertIsNone(result["returncode"])
        self.assertEqual("", result["stdout"])
        self.assertIn("failed to launch", result["stderr"])
        json.dumps(result)


class MockedTimeoutShapeTests(unittest.TestCase):
    """The timeout branch must keep the documented return shape and decoding."""

    def run_with_partial_streams(self, stdout, stderr):
        error = subprocess.TimeoutExpired(["fake-cli"], 1, output=stdout, stderr=stderr)
        popen = mock.Mock()
        popen.pid = 999999  # never a real pid: a stray killpg would be harmless
        popen.communicate.side_effect = [error, (stdout, stderr)]
        popen.returncode = -9
        with mock.patch.object(corpus_regression.subprocess, "Popen", return_value=popen), \
             mock.patch.object(corpus_regression.os, "killpg", create=True) as killpg:
            result = corpus_regression.run_cli(["fake-cli"], 1)
        if os.name == "posix":
            killpg.assert_called_once_with(999999, signal.SIGKILL)
        else:
            popen.kill.assert_called_once_with()
        return result

    def test_partial_bytes_are_replaced_not_raised(self):
        result = self.run_with_partial_streams(b"WARNING Slow: partial\n\xff", "\u9014\u4e2d".encode("utf-8") + b"\xe3")
        self.assertTrue(result["timed_out"])
        self.assertIsNone(result["returncode"])
        self.assertEqual("WARNING Slow: partial\n\ufffd", result["stdout"])
        self.assertIn("\u9014\u4e2d\ufffd", result["stderr"])
        self.assertIn("timed out after 1s", result["stderr"])
        self.assertEqual("Slow", corpus_regression.parse_diagnostics(result["stdout"])[0]["code"])
        json.dumps(result)

    def test_text_and_absent_partial_streams_are_accepted(self):
        for stdout, stderr in [("partial", "error"), (None, None)]:
            with self.subTest(stdout=stdout):
                result = self.run_with_partial_streams(stdout, stderr)
                self.assertEqual(stdout or "", result["stdout"])
                self.assertTrue(result["stderr"].startswith(stderr or ""))
                json.dumps(result)

    def test_keyboard_interrupt_kills_the_tree_and_propagates(self):
        popen = mock.Mock()
        popen.pid = 999999  # never a real pid: a stray killpg would be harmless
        popen.communicate.side_effect = KeyboardInterrupt
        with mock.patch.object(corpus_regression.subprocess, "Popen", return_value=popen), \
             mock.patch.object(corpus_regression.os, "killpg", create=True) as killpg:
            with self.assertRaises(KeyboardInterrupt):
                corpus_regression.run_cli(["fake-cli"], 5)
        if os.name == "posix":
            killpg.assert_called_once_with(999999, signal.SIGKILL)
        else:
            popen.kill.assert_called_once_with()
        popen.stdout.close.assert_called_once_with()
        popen.stderr.close.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
