"""Required CI coverage and the publish jobs' aggregate failure contract."""
import itertools
import os
from pathlib import Path
import re
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


def jobs(name):
    text = (ROOT / ".github" / "workflows" / name).read_text().split("\njobs:\n", 1)[1]
    entries = list(re.finditer(r"^  ([a-z][a-z0-9-]*):\n", text, re.MULTILINE))
    return {entry.group(1): text[entry.end():entries[i + 1].start() if i + 1 < len(entries) else len(text)]
            for i, entry in enumerate(entries)}


def scalar(block, name):
    match = re.search(r"^    " + re.escape(name) + r": (.+)$", block, re.MULTILINE)
    return match.group(1) if match else None


class PublishWorkflow(unittest.TestCase):
    def test_required_test_suites_run_for_every_changed_path(self):
        # Required checks must not report a successful skip based on a classifier's estimate of build inputs.
        required = {
            "ci.yml": ("unit-tests", "publish-terminal", "publish-apps", "deploy-script", "secret-scan", "install-sh-mac"),
            "scenarios.yml": ("scenarios", "browser", "container-scenarios", "installer-rig"),
        }
        for workflow, names in required.items():
            entries = jobs(workflow)
            self.assertNotIn("changes", set(entries))
            for name in names:
                with self.subTest(workflow=workflow, job=name):
                    self.assertIsNone(scalar(entries[name], "if"))
                    self.assertIsNone(scalar(entries[name], "needs"))

    def test_artifact_consumers_wait_only_for_their_publisher(self):
        entries = jobs("ci.yml")
        consumers = {
            "publish-terminal": ("terminal-distributions", "mac-cli"),
            "publish-apps": ("installer-kiosk", "installer-mac", "mac-app"),
        }
        for publisher, names in consumers.items():
            for name in names:
                with self.subTest(job=name):
                    self.assertEqual(publisher, scalar(entries[name], "needs"))
                    condition = scalar(entries[name], "if") or ""
                    self.assertNotIn("outputs", condition)
                    self.assertNotIn("github.", condition)

    def test_aggregate_fails_unless_every_build_test_and_publish_succeeded(self):
        aggregate = jobs("ci.yml")["build-and-test"]
        prerequisites = scalar(aggregate, "needs")
        self.assertEqual({"unit-tests", "publish-terminal", "publish-apps"},
                         set(prerequisites.strip("[] ").replace(" ", "").split(",")))
        self.assertIn("!cancelled()", scalar(aggregate, "if"))
        script = re.search(r"^        run: \|\n((?:^          .*\n|^\n)*)", aggregate, re.MULTILINE).group(1)
        script = textwrap.dedent(script)
        for results in itertools.product(("success", "failure", "skipped", "cancelled"), repeat=3):
            with self.subTest(results=results):
                environment = dict(os.environ, **dict(zip(
                    ("UNIT_TESTS", "PUBLISH_TERMINAL", "PUBLISH_APPS"), results)))
                result = subprocess.run(["bash", "-e", "-u", "-o", "pipefail", "-c", script],
                                        env=environment, capture_output=True, text=True, timeout=5)
                self.assertEqual(all(value == "success" for value in results), result.returncode == 0,
                                 result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
