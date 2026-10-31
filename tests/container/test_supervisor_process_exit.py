"""The timeout regression must pass even when init has not reaped killed descendants."""
import ctypes
import os
from pathlib import Path
import signal
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]


def run_as_subreaper():
    # Adopt orphaned fixture processes and leave them unreaped until the shell test has checked them.
    # This deterministically reproduces the hosted runner's delay in reaping a killed install helper.
    if ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0) != 0:  # PR_SET_CHILD_SUBREAPER
        raise OSError(ctypes.get_errno(), "cannot become a child subreaper")
    try:
        return subprocess.run(
            ["tests/container/supervisor-tests.sh",
             "test_a_live_process_is_not_considered_ended",
             "test_a_web_ui_preparation_that_outlasts_its_time_is_killed_and_prepared_again"],
            cwd=ROOT, check=False, timeout=20,
        ).returncode
    finally:
        # Only this fixture's children can be adopted here. End and reap every remaining one.
        children_file = Path(f"/proc/self/task/{os.getpid()}/children")
        while children := children_file.read_text().split():
            for child in children:
                try:
                    os.kill(int(child), signal.SIGKILL)
                except ProcessLookupError:
                    pass
            for child in children:
                os.waitpid(int(child), 0)


class ProcessExitRegression(unittest.TestCase):
    def test_timeout_ends_helper_even_before_its_zombie_is_reaped(self):
        result = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), "--subreaper"],
            capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    if sys.argv[1:] == ["--subreaper"]:
        sys.exit(run_as_subreaper())
    unittest.main()
