"""Tests of tests/alpaca/check-conformu-results.py: what passes and fails a ConformU run for this phase (#220)."""

import importlib.util
import io
import json
import pathlib
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("check_conformu_results", HERE / "check-conformu-results.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)

EXPECTED = {
    "protocol": {
        "Kind": "protocol-log",
        "InformationMessages": [
            {"Member": "PUT CloseShutter", "Contains": "(0x0000040B)", "Why": "commands disabled"},
            {"Member": "PUT OpenShutter", "Contains": "Test omitted", "Why": "DomeOpenShutter false"},
        ],
    },
    "conformance": {
        "Kind": "results",
        "ConfigurationAlerts": [
            {"Key": "Conform configuration", "Contains": "Shutter related tests were omitted", "Why": "DomeOpenShutter false"},
        ],
    },
}

PROTOCOL_LOG = """05:25:31.517 Found 0 errors, 0 issues and 2 information messages.
05:25:31.520 Information Message Summary
05:25:31.520 PUT OpenShutter ==> Test omitted due to Conform configuration setting

05:25:31.520 PUT CloseShutter ==> Good ID name casing - Received HTTP status 200 (OK) as expected but the device reported an ASCOM error:
  Response: Device returned a InvalidOperationException error (0x0000040B) for client transaction: 1, server transaction: 2.

"""


def results(errors=(), issues=(), alerts=(("Conform configuration", "Shutter related tests were omitted due to Conform configuration."),)):
    return {
        "ErrorCount": len(errors),
        "IssueCount": len(issues),
        "ConfigurationAlertCount": len(alerts),
        "Errors": [{"Key": key, "Value": value} for key, value in errors],
        "Issues": [{"Key": key, "Value": value} for key, value in issues],
        "ConfigurationAlerts": [{"Key": key, "Value": value} for key, value in alerts],
    }


class CheckConformuResultsTests(unittest.TestCase):
    def test_a_clean_conformance_run_with_the_expected_alert_passes(self):
        self.assertEqual(checker.check_results("conformance", results(), EXPECTED["conformance"]), [])

    def test_an_issue_fails_even_without_errors(self):
        problems = checker.check_results("conformance", results(issues=[("ShutterStatus", "not Opening straight after OpenShutter")]), EXPECTED["conformance"])
        self.assertTrue(any("1 issues, expected none" in problem for problem in problems))
        self.assertTrue(any("ShutterStatus" in problem for problem in problems))

    def test_an_error_fails(self):
        problems = checker.check_results("conformance", results(errors=[("AbortSlew", "threw")]), EXPECTED["conformance"])
        self.assertTrue(problems)

    def test_an_unexpected_alert_or_a_missing_one_fails(self):
        problems = checker.check_results("conformance", results(alerts=[("Conform configuration", "AbortSlew tests were omitted")]), EXPECTED["conformance"])
        self.assertTrue(any("unexpected configuration alert" in problem for problem in problems))
        self.assertTrue(any("was not reported" in problem for problem in problems))

    def test_a_protocol_log_with_only_the_expected_messages_passes(self):
        self.assertEqual(checker.check_protocol("protocol", PROTOCOL_LOG, EXPECTED["protocol"]), [])

    def test_a_protocol_log_with_issues_fails(self):
        log = PROTOCOL_LOG.replace("0 issues", "3 issues")
        problems = checker.check_protocol("protocol", log, EXPECTED["protocol"])
        self.assertTrue(any("3 issues" in problem for problem in problems))

    def test_an_unexpected_information_message_fails(self):
        log = PROTOCOL_LOG + "05:25:31.521 DELETE Connected ==> Received HTTP status 200 (OK)\n"
        problems = checker.check_protocol("protocol", log, EXPECTED["protocol"])
        self.assertTrue(any("DELETE Connected" in problem for problem in problems))

    def test_a_refusal_with_another_number_is_unexpected(self):
        log = PROTOCOL_LOG.replace("(0x0000040B)", "(0x00000400)")
        problems = checker.check_protocol("protocol", log, EXPECTED["protocol"])
        self.assertTrue(any("unexpected information message" in problem for problem in problems))
        self.assertTrue(any("was not reported" in problem for problem in problems))

    def test_a_protocol_log_without_its_summary_fails(self):
        problems = checker.check_protocol("protocol", "05:25:31.520 The Alpaca protocol checks were interrupted before completion.\n", EXPECTED["protocol"])
        self.assertEqual(len(problems), 1)
        self.assertIn("did not finish", problems[0])

    def test_a_clean_protocol_log_passes_only_when_nothing_is_expected(self):
        clean = "05:25:31.517 Congratulations there were no errors, issues or information alerts - Your device passes\n"
        self.assertEqual(checker.check_protocol("protocol", clean, {"Kind": "protocol-log", "InformationMessages": []}), [])
        self.assertTrue(checker.check_protocol("protocol", clean, EXPECTED["protocol"]))

    def test_the_command_line_fails_for_a_missing_results_file(self):
        with tempfile.TemporaryDirectory() as directory:
            expected = pathlib.Path(directory, "expected.json")
            expected.write_text(json.dumps(EXPECTED))
            log = pathlib.Path(directory, "protocol.log")
            log.write_text(PROTOCOL_LOG)
            good = pathlib.Path(directory, "results.json")
            good.write_text(json.dumps(results()))
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                self.assertEqual(checker.main(["--expected", str(expected), f"protocol={log}", f"conformance={good}"]), 0)
                self.assertEqual(checker.main(["--expected", str(expected), f"conformance={directory}/missing.json"]), 1)
                self.assertEqual(checker.main(["--expected", str(expected), f"unknown={good}"]), 1)

    def test_the_repositorys_expectations_say_why_for_each_entry(self):
        expectations = json.loads((HERE / "conformu-expected.json").read_text())
        for run in expectations.values():
            for entry in run.get("InformationMessages", []) + run.get("ConfigurationAlerts", []):
                self.assertTrue(entry.get("Why"), entry)

    def test_the_repositorys_settings_never_open_the_shutter(self):
        settings = json.loads((HERE / "conformu-settings.json").read_text())
        self.assertIs(settings["DomeOpenShutter"], False)
        self.assertEqual(settings["AlpacaDevice"]["IpAddress"], "127.0.0.1")


if __name__ == "__main__":
    unittest.main()
