"""The PTY smoke fails promptly and reaps children, including after a UI ignores F10."""
import importlib.util
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('published_terminal', Path(__file__).with_name('published-terminal.py'))
published = importlib.util.module_from_spec(spec)
spec.loader.exec_module(published)


class PtyBounds(unittest.TestCase):
    def test_child_exits_before_screen_is_reported_and_reaped(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(RuntimeError, 'startup/F10'):
                published.pty_start('/bin/sh', ['-c', 'exit 0'], {}, tmp, 80, 24, False, Path(tmp)/'capture')
            with self.assertRaises(ChildProcessError): os.waitpid(-1, os.WNOHANG)

    def test_timeout_before_screen_and_after_ignored_f10_is_bounded_and_reaped(self):
        for screen in ['', 'HVO roof Setup F9']:
            with self.subTest(screen=screen), tempfile.TemporaryDirectory() as tmp:
                start = time.monotonic()
                with patch.object(published, 'TIMEOUT', 0.25), self.assertRaisesRegex(RuntimeError, 'timed out'):
                    published.pty_start('/bin/sh', ['-c', 'printf "%s" "$1"; while :; do :; done', 'test', screen],
                                        {}, tmp, 80, 24, False, Path(tmp)/'capture')
                self.assertLess(time.monotonic()-start, 5)
                with self.assertRaises(ChildProcessError): os.waitpid(-1, os.WNOHANG)
