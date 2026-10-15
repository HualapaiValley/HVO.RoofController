"""Emit reproducible verification metadata; no credentials or host configuration."""
import datetime
from collections import Counter
import json
from pathlib import Path
import platform
import sys
import unittest
from demo import run


class RecordingResult(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.passed_ids = []

    def addSuccess(self, test):
        super().addSuccess(test)
        self.passed_ids.append(test.id())


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent), "test_*.py")
    result = unittest.TextTestRunner(stream=sys.stderr, verbosity=1, resultclass=RecordingResult).run(suite)
    report = {
        "checked_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "system": platform.system(), "architecture": platform.machine(),
        "python": platform.python_version(), "kernel": platform.release(),
        "passed_by_class": dict(Counter(name.rsplit(".", 1)[0] for name in result.passed_ids)),
        "passed_test_ids": result.passed_ids,
        "tests_run": result.testsRun,
        "passed": result.testsRun - len(result.skipped) - len(result.failures) - len(result.errors),
        "skipped": [{"test": str(test), "reason": reason} for test, reason in result.skipped],
        "failures": len(result.failures), "errors": len(result.errors),
        "mechanisms": {
            "policy_and_mock_session_lifecycle": "executed",
            "linux_so_peercred": "executed" if sys.platform == "linux" and result.wasSuccessful() else "not verified",
            "pidfd_lifecycle": "executed" if any("test_pidfd_tracks_child_exit" in name for name in result.passed_ids) else "not verified",
            "polkit_authorization": "mocked; no native authorization integration",
            "sudo_origin_and_policy": "not implemented or tested",
            "pam_transaction_api": "real libpam with private permit/deny fixture stacks" if sum("NativePamFixtureTests" in name for name in result.passed_ids) == 3 else "not verified",
            "linux_password_and_account_policy": "mocked; no real account/password validation",
            "real_credentials_or_persistent_keys": "none",
            "kiosk_pin_runtime": "not exercised; production source unchanged",
            "live_controller_or_hardware": "never contacted by this prototype",
        },
        "demo": run(),
    }
    print(json.dumps(report, indent=2))
    sys.exit(0 if result.wasSuccessful() else 1)
