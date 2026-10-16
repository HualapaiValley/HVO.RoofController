"""Exercise the rig image-label gate without installing a rig on the test host."""
from pathlib import Path
import re
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]


class RigIdentitySchemaTests(unittest.TestCase):
    def gate(self, value):
        script = (ROOT / "tests/installer/rig-scenario.sh").read_text()
        block = re.search(r'case "\$\{PUSHED_IDENTITY_SCHEMA\}" in.*?esac', script, re.S)
        self.assertIsNotNone(block)
        return subprocess.run(
            ["bash", "-c", 'PUSHED_IDENTITY_SCHEMA=$1; fail() { exit 42; }; '
             + block.group() + '; printf "%s" "$PUSHED_IDENTITY_SCHEMA"', "gate", value],
            capture_output=True, text=True, check=False)

    def test_supported_and_legacy_image_labels(self):
        for value, expected in [("", "1"), ("<no value>", "1"), ("1", "1"),
                                ("2", "2"), ("3", "3")]:
            with self.subTest(value=value):
                result = self.gate(value)
                self.assertEqual(0, result.returncode)
                self.assertEqual(expected, result.stdout)

    def test_unknown_or_malformed_labels_fail_closed(self):
        for value in ["0", "4", "-1", "true", "2.0", " 3", "3 ", "2\n3", "garbage"]:
            with self.subTest(value=value):
                self.assertEqual(42, self.gate(value).returncode)

    def test_current_controller_image_is_supported(self):
        dockerfile = (ROOT / "src/HVO.RoofControllerV4.RPi/Dockerfile").read_text()
        label = re.search(r'LABEL io.hvo.roof.identity-schema="([^"]+)"', dockerfile)
        self.assertIsNotNone(label)
        self.assertEqual(0, self.gate(label.group(1)).returncode)

    def test_auth_bundle_schema3_requires_native_worker(self):
        args = [sys.executable, str(ROOT / "tests/auth-integration/assemble-release.py")]
        for option in ["version", "commit", "registry", "controller-digest",
                       "emulator-digest", "installer", "cli", "output"]:
            args.extend(["--" + option, "fixture"])
        result = subprocess.run(args + ["--identity-schema", "3"],
                                capture_output=True, text=True, check=False)
        self.assertEqual(2, result.returncode)
        self.assertIn("--native-worker is required", result.stderr)

    def test_auth_bundle_rejects_future_schema(self):
        result = subprocess.run(
            [sys.executable, str(ROOT / "tests/auth-integration/assemble-release.py"),
             "--identity-schema", "4"], capture_output=True, text=True, check=False)
        self.assertEqual(2, result.returncode)
        self.assertIn("invalid choice", result.stderr)


if __name__ == "__main__":
    unittest.main()
