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
