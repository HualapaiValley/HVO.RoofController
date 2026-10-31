#!/usr/bin/env python3
"""The Alpaca write path's checks for tests/alpaca/conformu.sh (#220, phase 3), against processes on loopback only.

Subcommands, each reading its secrets from files (never from arguments, never printed):

- session:     creates the CI person (an Operator) with the admin key and writes their session token to a file.
- guard:       the chain guard, before anything may command the roof: the Alpaca server's URL is loopback, the controller
               it reaches is this run's (the same instance), and that controller drives an emulated HAT. Exits non-zero
               otherwise, so the harness stops before ConformU starts.
- clear-hold:  a person clears the human Stop hold set at the controller's start, and no person's lease is left.
- no-secret:   fails when a file holds one of the run's secrets (a log or result about to be kept as an artifact);
               with --remove it deletes each such file, so a failed run keeps none.
- close-runs:  the scripted Close runs against the real processes: the hold left set, a missing and a wrong credential,
               an unparked mount, a Close grant, a new reason after a grant, and unsafe weather; each checks what the
               emulated roof and relays did.
"""

import argparse
import base64
import ipaddress
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


def read_secret(path):
    with open(path, encoding="utf-8") as stream:
        return stream.read().strip()


def request(method, url, *, headers=None, body=None, form=None, timeout=10):
    """(status, parsed JSON or text) of one request; HTTP errors are answers, not exceptions."""
    data = None
    headers = dict(headers or {})
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    elif form is not None:
        data = urllib.parse.urlencode(form).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            text = response.read().decode()
            status = response.status
    except urllib.error.HTTPError as error:
        text = error.read().decode()
        status = error.code
    try:
        return status, json.loads(text) if text else None
    except json.JSONDecodeError:
        return status, text


def fail(message):
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(1)


def bearer(token):
    return {"Authorization": f"Bearer {token}"}


def basic(key):
    return {"Authorization": "Basic " + base64.b64encode(f"{key}:{key}".encode()).decode()}


def session(args):
    admin = {"X-Api-Key": read_secret(args.admin_key)}
    password = read_secret(args.password)
    status, answer = request("POST", f"{args.controller}/api/v4.0/Identity/Users", headers=admin,
                             body={"name": args.name, "role": "RoofOperator", "password": password})
    if status != 201:
        fail(f"Creating the CI person answered {status}")
    status, answer = request("POST", f"{args.controller}/api/v4.0/Auth/Session", body={"name": args.name, "password": password})
    if status != 200:
        fail(f"Signing the CI person in answered {status}")
    with open(args.token, "w", encoding="utf-8") as stream:
        stream.write(answer["token"])
    print(f"The CI person {args.name} is signed in (session {answer.get('sessionId', '?')})")


def controller_status(args, token):
    status, answer = request("GET", f"{args.controller}/api/v4.0/RoofControl/Status", headers=bearer(token))
    if status != 200:
        fail(f"The controller's status answered {status}")
    return answer


def loopback(url, what):
    """Fails unless the URL names a loopback address literally (a name, even localhost, is refused: it could resolve)."""
    host = urllib.parse.urlparse(url).hostname or ""
    try:
        is_loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        is_loopback = False
    if not is_loopback:
        fail(f"Chain guard: the {what}'s URL is not a loopback address ({host or 'none'}); nothing is commanded")
    return host


def guard(args):
    loopback(args.controller, "controller")
    host = loopback(args.alpaca, "Alpaca server")
    token = read_secret(args.token)
    status = controller_status(args, token)
    if status.get("hatMode") != "Emulated":
        fail(f"Chain guard: the controller drives a {status.get('hatMode')} HAT, not the emulator; nothing is commanded")
    mode_status, mode = request("GET", f"{args.controller}/api/v4.0/RoofControl/Mode")
    if mode_status != 200 or mode.get("hatMode") != "Emulated":
        fail("Chain guard: the controller's mode is not the emulated HAT; nothing is commanded")
    instance = status.get("controllerInstanceId")
    form = {"Action": "status", "Parameters": "", "ClientID": "4242", "ClientTransactionID": "1"}
    request("PUT", f"{args.alpaca}/api/v1/dome/0/connected", form={"Connected": "true", "ClientID": "4242"})
    _, action = request("PUT", f"{args.alpaca}/api/v1/dome/0/action", form=form)
    text = action.get("Value", "") if isinstance(action, dict) else ""
    request("PUT", f"{args.alpaca}/api/v1/dome/0/connected", form={"Connected": "false", "ClientID": "4242"})
    if f"(instance {instance}, HAT Emulated)" not in text:
        error = action.get("ErrorMessage") if isinstance(action, dict) else action
        fail(f"Chain guard: the Alpaca server's controller is not this run's emulated one; nothing is commanded. Its status: {text or error}")
    print(f"Chain guard: the Alpaca server on {host} reaches controller instance {instance}, which drives the HAT emulator")


def clear_hold(args):
    token = read_secret(args.token)
    status, answer = request("DELETE", f"{args.controller}/api/v4.0/alpaca/hold", headers=bearer(token))
    if status != 200 or answer.get("held"):
        fail(f"Clearing the Stop hold answered {status}")
    alpaca = controller_status(args, token)["alpaca"]
    if alpaca["hold"]["held"] or alpaca["operatorLease"]["held"]:
        fail("The Stop hold, or a person's operator lease, is still there")
    print("The start-up Stop hold is cleared; no person holds the operator lease")


def no_secret(args):
    """Fails when a file holds one of the run's secrets; with --remove, deletes each such file first (an artifact)."""
    secrets = [value for value in (read_secret(path) for path in args.secret if exists(path)) if value]
    found = []
    for path in args.files:
        try:
            with open(path, encoding="utf-8", errors="replace") as stream:
                text = stream.read()
        except (FileNotFoundError, IsADirectoryError):
            continue
        if any(secret in text for secret in secrets):
            found.append(path)
            if args.remove:
                os.remove(path)
    if found:
        removed = " (removed, never kept)" if args.remove else ""
        fail(f"A run secret appears in {', '.join(found)}{removed}: it must never be kept")
    print(f"No run secret appears in any of {len(args.files)} files")


def exists(path):
    return os.path.isfile(path)


class Runs:
    """The scripted Close runs: one roof, one emulated mount and weather monitor, one Alpaca client."""

    def __init__(self, args):
        self.args = args
        self.key = read_secret(args.external_key)
        self.token = read_secret(args.token)
        self.client_id = 7

    def emulator(self, path, body=None):
        status, answer = request("POST" if body is not None else "GET", f"{self.args.emulator}/api/emulator{path}", body=body)
        if status != 200:
            fail(f"The emulator's {path} answered {status}")
        return answer

    def plant(self):
        return self.emulator("/status")["plant"]

    def status(self):
        return controller_status(self.args, self.token)

    def wait(self, condition, what, timeout=30):
        deadline = time.monotonic() + timeout
        while True:
            value = condition()
            if value:
                return value
            if time.monotonic() > deadline:
                fail(f"Timed out waiting for {what}")
            time.sleep(0.1)

    def connect(self):
        request("PUT", f"{self.args.alpaca}/api/v1/dome/0/connected", form={"Connected": "true", "ClientID": str(self.client_id)})

    def close(self, headers):
        status, answer = request("PUT", f"{self.args.alpaca}/api/v1/dome/0/closeshutter", headers=headers,
                                 form={"ClientID": str(self.client_id), "ClientTransactionID": "41"})
        if status != 200:
            fail(f"CloseShutter answered HTTP {status}")
        return answer["ErrorNumber"], answer["ErrorMessage"]

    def part_way(self, metres=0.2):
        """The roof part way open, set through the emulator; every Alpaca device parked, safe and answering."""
        self.emulator("/reset", {"positionMeters": metres})
        self.wait(lambda: not self.status()["isMoving"] and self.status()["isOpenLimitActive"] is False
                  and self.status()["isClosedLimitActive"] is False and not self.status()["isFaultLatched"], "the controller to see the roof part way")
        self.wait(lambda: self.status()["safety"]["sources"][0]["state"] == "Safe", "the mount read parked again")

    def still(self, why, seconds=1.0):
        before = self.plant()
        time.sleep(seconds)
        after = self.plant()
        if abs(after["positionMeters"] - before["positionMeters"]) > 0.001 or after["relayRegister"] != 0:
            fail(f"The roof moved or a relay is on: {why}")

    def closes(self, why):
        self.wait(lambda: self.status()["status"] == "Closed" and not self.status()["isMoving"], f"the roof to close: {why}")

    def alerts(self):
        status, answer = request("GET", f"{self.args.controller}/api/v4.0/alpaca/alerts", headers=bearer(self.token))
        if status != 200:
            fail(f"The alerts answered {status}")
        return answer

    def grants(self):
        return self.status()["alpaca"]["grants"]

    def grant(self, keys):
        status, _ = request("POST", f"{self.args.controller}/api/v4.0/alpaca/grants", headers=bearer(self.token),
                            body={"kind": "Close", "reasonKeys": keys})
        if status != 201:
            fail(f"The Close grant answered {status}")

    def run(self):
        self.connect()

        # The hold left set (by a person's Stop): Close refused, nothing moves.
        self.part_way()
        status, _ = request("POST", f"{self.args.controller}/api/v4.0/RoofControl/Stop", headers=bearer(self.token))
        if status != 200:
            fail(f"The person's Stop answered {status}")
        number, message = self.close(basic(self.key))
        if number != 0x40B or "Stop hold" not in message:
            fail(f"With the hold set, CloseShutter answered {number:#x}")
        self.still("the hold refuses every Alpaca command")
        status, _ = request("DELETE", f"{self.args.controller}/api/v4.0/alpaca/hold", headers=bearer(self.token))
        if status != 200:
            fail("Clearing the hold failed")
        print("Run: the Stop hold left set refuses the Close; nothing moves")

        # A missing and a wrong credential: not authorised, nothing moves.
        for name, headers in (("no credential", {}), ("a wrong key", basic("not-the-external-alpaca-key-00000000"))):
            number, _ = self.close(headers)
            if number != 0x40B:
                fail(f"With {name}, CloseShutter answered {number:#x}")
            self.still(f"{name} is not authorised")
        print("Run: a missing and a wrong credential are not authorised; nothing moves")

        # An unparked mount: refused, an alert, nothing moves or is queued.
        self.emulator("/alpaca/telescope", {"device": 0, "parked": False})
        number, message = self.close(basic(self.key))
        if number != 0x40B or "Telescope 'Main'" not in message:
            fail(f"With the mount unparked, CloseShutter answered {number:#x}: {message}")
        refused = [alert for alert in self.alerts() if alert["kind"] == "Refused" and "scope-not-clear:Main" in alert["reasonKeys"]]
        if not refused:
            fail("No alert for the refused Close")
        self.emulator("/alpaca/telescope", {"device": 0, "parked": True})
        self.still("a refused Close is never queued", seconds=1.5)
        print("Run: an unparked mount refuses the Close with an alert; nothing moves or is queued")

        # A Close grant: the refused Close goes, and the grant is used.
        self.emulator("/alpaca/telescope", {"device": 0, "parked": False})
        number, _ = self.close(basic(self.key))
        if number != 0x40B:
            fail("The unparked mount did not refuse the Close")
        self.grant(refused[0]["reasonKeys"])
        number, message = self.close(basic(self.key))
        if number != 0:
            fail(f"With a Close grant, CloseShutter answered {number:#x}: {message}")
        self.closes("the grant let it go")
        if self.grants():
            fail("The grant was not used up")
        print("Run: a Close grant lets the refused Close go, and is used up")

        # A new reason after a grant: refused, and the grant used.
        self.part_way()
        self.emulator("/alpaca/telescope", {"device": 0, "parked": False})
        self.grant(["scope-not-clear:Main"])
        self.emulator("/alpaca/telescope", {"device": 0, "connected": False})
        number, _ = self.close(basic(self.key))
        if number != 0x40B:
            fail(f"A new reason after the grant was not refused ({number:#x})")
        if self.grants():
            fail("The grant was not used up by the refused Close")
        self.still("the new reason was not lifted")
        print("Run: a new reason after a grant refuses the Close, and the grant is used")

        # Unsafe weather never gates Close.
        self.part_way()
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": False})
        number, message = self.close(basic(self.key))
        if number != 0:
            fail(f"With unsafe weather, CloseShutter answered {number:#x}: {message}")
        self.closes("weather never gates Close")
        print("Run: unsafe weather never gates the Close")
        violations = self.emulator("/violations")
        if violations:
            fail(f"The emulated plant recorded {len(violations)} violations")


def close_runs(args):
    for url, what in ((args.controller, "controller"), (args.alpaca, "Alpaca server"), (args.emulator, "HAT emulator")):
        loopback(url, what)
    Runs(args).run()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    one = commands.add_parser("session")
    one.add_argument("--controller", required=True)
    one.add_argument("--admin-key", required=True)
    one.add_argument("--password", required=True)
    one.add_argument("--token", required=True)
    one.add_argument("--name", default="ci-operator")
    one.set_defaults(handler=session)

    one = commands.add_parser("guard")
    one.add_argument("--controller", required=True)
    one.add_argument("--alpaca", required=True)
    one.add_argument("--token", required=True)
    one.set_defaults(handler=guard)

    one = commands.add_parser("clear-hold")
    one.add_argument("--controller", required=True)
    one.add_argument("--token", required=True)
    one.set_defaults(handler=clear_hold)

    one = commands.add_parser("no-secret")
    one.add_argument("--secret", required=True, action="append")
    one.add_argument("--remove", action="store_true")
    one.add_argument("files", nargs="*")
    one.set_defaults(handler=no_secret)

    one = commands.add_parser("close-runs")
    one.add_argument("--controller", required=True)
    one.add_argument("--alpaca", required=True)
    one.add_argument("--emulator", required=True)
    one.add_argument("--token", required=True)
    one.add_argument("--external-key", required=True)
    one.set_defaults(handler=close_runs)

    args = parser.parse_args()
    args.handler(args)


if __name__ == "__main__":
    main()
