"""Tests of tests/alpaca/alpaca-ci.py's chain guard and secret check (#220, phase 3), against fakes on loopback.

The guard is what stops the ConformU harness before anything may command a roof: these show it refuses a URL that is
not loopback, a controller that is not on the HAT emulator, and an Alpaca server that reaches another controller.
"""

import importlib.util
import io
import json
import os
import tempfile
import threading
import unittest
from contextlib import redirect_stderr, redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("alpaca_ci", HERE / "alpaca-ci.py")
alpaca_ci = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(alpaca_ci)


class FakeChain:
    """A fake controller and Alpaca server on one loopback port; records whether anything was commanded."""

    def __init__(self, *, hat_mode="Emulated", mode_hat="Emulated", instance="abc123", reached_instance="abc123",
                 reached_hat="Emulated"):
        self.commands = []
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def answer(self, body):
                data = json.dumps(body).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                if self.path.endswith("/RoofControl/Status"):
                    self.answer({"hatMode": hat_mode, "controllerInstanceId": instance})
                elif self.path.endswith("/RoofControl/Mode"):
                    self.answer({"hatMode": mode_hat})
                else:
                    self.send_error(404)

            def do_PUT(self):
                length = int(self.headers.get("Content-Length", "0"))
                self.rfile.read(length)
                if self.path.endswith("/action"):
                    self.answer({"Value": f"Closed. Controller: test (instance {reached_instance}, HAT {reached_hat})."})
                elif self.path.endswith("/connected"):
                    self.answer({"ErrorNumber": 0})
                else:
                    fake.commands.append(self.path)
                    self.answer({"ErrorNumber": 0})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.server.shutdown()
        self.server.server_close()


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.token = os.path.join(self.directory.name, "token")
        with open(self.token, "w", encoding="utf-8") as stream:
            stream.write("test-session-token-not-a-secret")

    def tearDown(self):
        self.directory.cleanup()

    def guard(self, controller, alpaca):
        args = SimpleNamespace(controller=controller, alpaca=alpaca, token=self.token)
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            try:
                alpaca_ci.guard(args)
                return 0, out.getvalue() + err.getvalue()
            except SystemExit as stop:
                return stop.code, out.getvalue() + err.getvalue()

    def test_this_runs_emulated_chain_passes(self):
        with FakeChain() as chain:
            code, text = self.guard(chain.url, chain.url)
        self.assertEqual(code, 0, text)
        self.assertIn("instance abc123", text)
        self.assertEqual(chain.commands, [])

    def test_a_url_that_is_not_loopback_is_refused_before_any_request(self):
        for controller, alpaca in (("http://127.0.0.1:1", "http://192.0.2.10:11111"),
                                   ("http://192.0.2.10:5195", "http://127.0.0.1:1"),
                                   ("http://localhost:5195", "http://127.0.0.1:1")):
            with self.subTest(controller=controller, alpaca=alpaca):
                code, text = self.guard(controller, alpaca)
                self.assertEqual(code, 1)
                self.assertIn("not a loopback address", text)

    def test_a_controller_on_a_physical_hat_is_refused(self):
        for hat_mode, mode_hat in (("Physical", "Physical"), ("Emulated", "Physical")):
            with self.subTest(status=hat_mode, mode=mode_hat), FakeChain(hat_mode=hat_mode, mode_hat=mode_hat) as chain:
                code, text = self.guard(chain.url, chain.url)
                self.assertEqual(code, 1)
                self.assertIn("nothing is commanded", text)
                self.assertEqual(chain.commands, [])

    def test_an_alpaca_server_reaching_another_controller_is_refused(self):
        for reached_instance, reached_hat in (("other", "Emulated"), ("abc123", "Physical")):
            with self.subTest(instance=reached_instance, hat=reached_hat), \
                    FakeChain(reached_instance=reached_instance, reached_hat=reached_hat) as chain:
                code, text = self.guard(chain.url, chain.url)
                self.assertEqual(code, 1)
                self.assertIn("not this run's emulated one", text)
                self.assertEqual(chain.commands, [])

    def test_the_close_runs_refuse_a_url_that_is_not_loopback(self):
        args = SimpleNamespace(controller="http://127.0.0.1:1", alpaca="http://127.0.0.1:2",
                               emulator="http://192.0.2.10:5290", token=self.token, external_key=self.token)
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as stop:
            alpaca_ci.close_runs(args)
        self.assertEqual(stop.exception.code, 1)

    def test_the_open_runs_and_the_check_after_conformu_refuse_a_url_that_is_not_loopback(self):
        for controller, alpaca, emulator in (("http://192.0.2.10:5195", "http://127.0.0.1:2", "http://127.0.0.1:3"),
                                             ("http://127.0.0.1:1", "http://192.0.2.10:11111", "http://127.0.0.1:3"),
                                             ("http://127.0.0.1:1", "http://127.0.0.1:2", "http://localhost:5290")):
            with self.subTest(controller=controller, alpaca=alpaca, emulator=emulator):
                args = SimpleNamespace(controller=controller, alpaca=alpaca, emulator=emulator, token=self.token,
                                       external_key=self.token, admin_key=self.token, settings_file=self.token)
                with redirect_stderr(io.StringIO()) as err, self.assertRaises(SystemExit) as stop:
                    alpaca_ci.open_runs(args)
                self.assertEqual(stop.exception.code, 1)
                self.assertIn("not a loopback address", err.getvalue())
                if alpaca == "http://127.0.0.1:2":
                    with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as stop:
                        alpaca_ci.after_conformu(args)
                    self.assertEqual(stop.exception.code, 1)

    def test_the_open_runs_change_only_a_settings_file_the_harness_prepared(self):
        settings = os.path.join(self.directory.name, "appsettings.Development.json")
        with open(settings, "w", encoding="utf-8") as stream:
            stream.write('{\n  "Logging": {}\n}\n')
        args = SimpleNamespace(controller="http://127.0.0.1:1", alpaca="http://127.0.0.1:2", emulator="http://127.0.0.1:3",
                               token=self.token, external_key=self.token, settings_file=settings)
        with redirect_stderr(io.StringIO()) as err, self.assertRaises(SystemExit):
            alpaca_ci.OpenRuns(args)
        self.assertIn("only a settings file the harness prepared is changed", err.getvalue())
        with open(settings, encoding="utf-8") as stream:
            self.assertEqual(stream.read(), '{\n  "Logging": {}\n}\n')


DEVELOPMENT_SETTINGS = """{
  // Development runs against the HAT emulator.
  "HatEmulator": {
    "Enabled": true
  }
}
"""


class ModesTests(unittest.TestCase):
    def test_the_modes_go_after_the_opening_brace_and_are_read_back(self):
        text = alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, "OpenAndClose", "Warn")
        self.assertTrue(text.startswith("{\n" + alpaca_ci.MODES_BEGIN + "\n"))
        self.assertIn('"RoofController": { "Alpaca": { "AllowCommands": "OpenAndClose" } },', text)
        self.assertIn('"RoofSafety": { "InterlockMode": "Warn" },', text)
        self.assertTrue(text.endswith(DEVELOPMENT_SETTINGS[1:]))
        self.assertEqual(alpaca_ci.read_modes(text), ("OpenAndClose", "Warn"))
        self.assertIsNone(alpaca_ci.read_modes(DEVELOPMENT_SETTINGS))

    def test_new_modes_replace_the_old_ones_and_leave_the_rest_as_it_was(self):
        text = alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, "OpenAndClose", "Warn")
        changed = alpaca_ci.with_modes(text, "CloseOnly", "Block")
        self.assertEqual(alpaca_ci.read_modes(changed), ("CloseOnly", "Block"))
        self.assertEqual(changed.count(alpaca_ci.MODES_BEGIN), 1)
        self.assertEqual(alpaca_ci.with_modes(changed, "OpenAndClose", "Warn"), text)
        self.assertEqual(len(changed), len(text) + len("CloseOnly") - len("OpenAndClose") + len("Block") - len("Warn"))

    def test_a_mode_that_is_not_one_of_the_settings_values_is_refused(self):
        for allow, interlock in (("Open", "Warn"), ("OpenAndClose", "block"), ('OpenAndClose" }, "X": { "Y": "Z', "Warn")):
            with self.subTest(allow=allow, interlock=interlock), self.assertRaises(ValueError):
                alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, allow, interlock)
        with self.assertRaises(ValueError):
            alpaca_ci.with_modes("// no object\n[]", "OpenAndClose", "Warn")

    def test_max_reading_age_is_written_with_the_modes_for_the_stale_weather_run_and_dropped_again(self):
        text = alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, "OpenAndClose", "Warn", "00:00:07")
        self.assertIn('"RoofSafety": { "InterlockMode": "Warn", "MaxReadingAge": "00:00:07" },', text)
        self.assertEqual(alpaca_ci.read_modes(text), ("OpenAndClose", "Warn"))
        json.loads("\n".join(line for line in text.splitlines() if not line.lstrip().startswith("//")))
        self.assertEqual(alpaca_ci.with_modes(text, "OpenAndClose", "Warn"), alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, "OpenAndClose", "Warn"))
        for bad in ("7", "00:00:07.5", '00:00:07" }, "X": { "Y": "Z'):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                alpaca_ci.with_modes(DEVELOPMENT_SETTINGS, "OpenAndClose", "Warn", bad)

    def test_writing_the_modes_replaces_the_file_whole_and_leaves_nothing_beside_it(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "appsettings.Development.json")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(DEVELOPMENT_SETTINGS)
            alpaca_ci.write_modes(path, "OpenAndClose", "Warn")
            alpaca_ci.write_modes(path, "OpenAndClose", "Block")
            with open(path, encoding="utf-8") as stream:
                self.assertEqual(alpaca_ci.read_modes(stream.read()), ("OpenAndClose", "Block"))
            self.assertEqual(os.listdir(directory), ["appsettings.Development.json"])


def hat(before, after):
    return {"at": "00:00:01", "kind": "Hat", "detail": f"relay register {before:#x} -> {after:#x}"}


class RelayTests(unittest.TestCase):
    def test_the_register_values_come_from_the_hat_events_only(self):
        history = [{"kind": "Input", "detail": "IN2 HIGH"}, hat(0, 8), {"kind": "Relay", "detail": "RLY4 contact closed"},
                   hat(8, 9), hat(9, 8), hat(8, 0)]
        self.assertEqual(alpaca_ci.relay_writes(history), [0, 8, 9, 8, 0])
        self.assertEqual(alpaca_ci.relay_writes([{"kind": "Input", "detail": "IN2 HIGH"}]), [0])

    def test_a_safe_open_and_a_reversal_pass(self):
        self.assertEqual(alpaca_ci.relay_problems([0, 8, 9, 8, 0], ["open"]), [])
        reversal = [0, 8, 9, 8, 0, 8, 0xA, 8, 0]
        self.assertEqual(alpaca_ci.direction_states(reversal), ["none", "open", "none", "close", "none"])
        self.assertEqual(alpaca_ci.relay_problems(reversal, ["open", "close"]), [])
        self.assertEqual(alpaca_ci.relay_problems([0, 8, 0], []), [])

    def test_both_direction_relays_on_fail(self):
        problems = alpaca_ci.relay_problems([0, 9, 0xB, 0xA, 0], ["open", "close"])
        self.assertTrue(any("both direction relays were on at once" in problem for problem in problems))

    def test_a_reversal_in_one_write_fails(self):
        problems = alpaca_ci.relay_problems([0, 9, 0xA, 0], ["open", "close"])
        self.assertTrue(any("was not off before" in problem for problem in problems), problems)

    def test_an_unexpected_motion_or_none_at_all_fails(self):
        self.assertTrue(alpaca_ci.relay_problems([0, 9, 0], []))
        self.assertTrue(alpaca_ci.relay_problems([0, 0xA, 0], ["open"]))
        self.assertTrue(alpaca_ci.relay_problems([0, 8, 0], ["open"]))
        self.assertTrue(alpaca_ci.relay_problems([0, 0xA, 0, 9, 0], ["open", "close"]))

    def test_a_direction_relay_left_on_fails(self):
        problems = alpaca_ci.relay_problems([0, 9], ["open"])
        self.assertTrue(any("still on" in problem for problem in problems))


class AlertTests(unittest.TestCase):
    def alert(self, id, count, keys, command="Open", kind="Refused"):
        return {"id": id, "count": count, "reasonKeys": keys, "command": command, "kind": kind}

    def test_a_new_alert_or_one_counted_again_is_found(self):
        before = [self.alert("a", 1, ["weather-unsafe:W"])]
        self.assertEqual(alpaca_ci.new_alert(before, [self.alert("a", 2, ["weather-unsafe:W"])], "Open", "weather-unsafe:W")["id"], "a")
        after = before + [self.alert("b", 1, ["scope-not-clear:Main"])]
        self.assertEqual(alpaca_ci.new_alert(before, after, "Open", "scope-not-clear:Main")["id"], "b")

    def test_an_old_alert_another_command_or_kind_is_not(self):
        before = [self.alert("a", 1, ["weather-unsafe:W"])]
        self.assertIsNone(alpaca_ci.new_alert(before, before, "Open", "weather-unsafe:W"))
        self.assertIsNone(alpaca_ci.new_alert([], [self.alert("c", 1, ["scope-not-clear:Main"], command="Close")], "Open", "scope-not-clear:Main"))
        self.assertIsNone(alpaca_ci.new_alert([], [self.alert("d", 1, ["scope-not-clear:Main"], kind="ContinuedWhileUnsafe")], "Open", "scope-not-clear:Main"))


class NoSecretTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name)
        (self.path / "external-key").write_text("test-external-key-not-a-real-one\n", encoding="utf-8")
        (self.path / "admin-key").write_text("test-admin-key-not-a-real-one\n", encoding="utf-8")
        (self.path / "clean.log").write_text("nothing here\n", encoding="utf-8")
        (self.path / "leaked.log").write_text("Basic header test-external-key-not-a-real-one\n", encoding="utf-8")
        (self.path / "admin.json").write_text('{"key": "test-admin-key-not-a-real-one"}', encoding="utf-8")
        self.secrets = [str(self.path / "external-key"), str(self.path / "admin-key"), str(self.path / "session")]

    def tearDown(self):
        self.directory.cleanup()

    def check(self, files, remove=False):
        args = SimpleNamespace(secret=self.secrets, remove=remove, files=[str(self.path / name) for name in files])
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            try:
                alpaca_ci.no_secret(args)
                return 0, out.getvalue() + err.getvalue()
            except SystemExit as stop:
                return stop.code, out.getvalue() + err.getvalue()

    def test_files_without_a_secret_pass_and_a_missing_secret_file_or_directory_is_skipped(self):
        (self.path / "home").mkdir()
        code, text = self.check(["clean.log", "missing.log", "home"])
        self.assertEqual(code, 0, text)

    def test_a_file_holding_any_secret_fails_without_printing_it(self):
        code, text = self.check(["clean.log", "leaked.log", "admin.json"])
        self.assertEqual(code, 1)
        self.assertIn("leaked.log", text)
        self.assertIn("admin.json", text)
        self.assertNotIn("not-a-real-one", text)
        self.assertTrue((self.path / "leaked.log").exists(), "without --remove nothing is deleted")

    def test_remove_deletes_each_file_holding_a_secret_and_keeps_the_rest(self):
        code, text = self.check(["clean.log", "leaked.log", "admin.json"], remove=True)
        self.assertEqual(code, 1)
        self.assertIn("removed", text)
        self.assertFalse((self.path / "leaked.log").exists())
        self.assertFalse((self.path / "admin.json").exists())
        self.assertTrue((self.path / "clean.log").exists())


if __name__ == "__main__":
    unittest.main()
