#!/usr/bin/env python3
"""The Alpaca write path's checks for tests/alpaca/conformu.sh (#220, phases 3 and 4), against processes on loopback only.

Subcommands, each reading its secrets from files (never from arguments, never printed):

- session:       creates the CI person (an Operator) with the admin key and writes their session token to a file.
- guard:         the chain guard, before anything may command the roof: the Alpaca server's URL is loopback, the
                 controller it reaches is this run's (the same instance), and that controller drives an emulated HAT.
                 Exits non-zero otherwise, so the harness stops before ConformU starts.
- clear-hold:    a person clears the human Stop hold set at the controller's start, and no person's lease is left.
- no-secret:     fails when a file holds one of the run's secrets (a log or result about to be kept as an artifact);
                 with --remove it deletes each such file, so a failed run keeps none.
- modes:         writes this run's Alpaca command mode and safety interlock mode into the run-only copy of the
                 controller's appsettings.Development.json (before the controller starts; the Open runs change them
                 while it runs, a configuration reload).
- after-conformu: ConformU opened and closed the emulated roof through Alpaca: it is at rest at a limit with every relay
                 off, no plant violation and no fault, and the history (read with the admin key) records ConformU's
                 Open and Close with the Alpaca source.
- close-runs:    the scripted Close runs against the real processes: the hold left set, a missing and a wrong
                 credential, an unparked mount, a Close grant, a new reason after a grant, and unsafe weather; each
                 checks what the emulated roof and relays did.
- open-runs:     the scripted Open runs: a safe Open, unsafe weather, a weather monitor not connected and one failing,
                 stale weather,
                 an unparked mount, an Open grant, a grant dropped when the interlock turns Block at runtime, an Alpaca
                 Close reversing an Alpaca Open, and CloseOnly; each starts the emulated roof in a known state through the
                 emulator, checks it, and then checks the relays' register writes, the roof's position and the grants.
"""

import argparse
import base64
import ipaddress
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# Alpaca ShutterStatus values.
OPEN, CLOSED, OPENING, CLOSING, ERROR = 0, 1, 2, 3, 4

# The relay register's direction bits (docs/projects/roof-controller-v4-rpi/hardware-overview.md): RLY1 runs the drive
# forward (open), RLY2 reverse (close).
OPEN_RELAY, CLOSE_RELAY = 0x1, 0x2

# This run's modes in the run-only copy of the controller's appsettings.Development.json, between these two lines.
MODES_BEGIN = "  // alpaca-ci.py: this run's modes (a run-only copy; rewritten while the controller runs)"
MODES_END = "  // alpaca-ci.py: end of this run's modes"
ALLOW_COMMANDS = ("Off", "CloseOnly", "OpenAndClose")
INTERLOCK_MODES = ("Off", "Warn", "Block")


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


def with_modes(text, allow_commands, interlock_mode, max_reading_age=None):
    """The settings file's text with this run's modes, right after its opening brace, in place of any earlier ones; with
    `max_reading_age` (hh:mm:ss), RoofSafety:MaxReadingAge too, for the stale-weather run."""
    if allow_commands not in ALLOW_COMMANDS or interlock_mode not in INTERLOCK_MODES:
        raise ValueError(f"AllowCommands must be one of {ALLOW_COMMANDS} and InterlockMode one of {INTERLOCK_MODES}")
    if max_reading_age is not None and not re.fullmatch(r"\d\d:\d\d:\d\d", max_reading_age):
        raise ValueError("MaxReadingAge must be hh:mm:ss")
    start = text.find("\n" + MODES_BEGIN)
    if start >= 0:
        text = text[:start] + text[text.index(MODES_END, start) + len(MODES_END):]
    if not text.lstrip().startswith("{"):
        raise ValueError("The settings file does not begin with '{'")
    brace = text.index("{")
    block = "\n".join(["", MODES_BEGIN,
                       f'  "RoofController": {{ "Alpaca": {{ "AllowCommands": "{allow_commands}" }} }},',
                       f'  "RoofSafety": {{ "InterlockMode": "{interlock_mode}"'
                       + (f', "MaxReadingAge": "{max_reading_age}"' if max_reading_age else '') + ' },',
                       MODES_END])
    return text[:brace + 1] + block + text[brace + 1:]


def read_modes(text):
    """(AllowCommands, InterlockMode) from this run's modes in the settings file's text, or None when it has none."""
    match = re.search(re.escape(MODES_BEGIN) + r'\n.*"AllowCommands": "(\w+)".*\n.*"InterlockMode": "(\w+)"', text)
    return (match.group(1), match.group(2)) if match else None


def write_modes(path, allow_commands, interlock_mode, max_reading_age=None):
    """Rewrites the run-only settings file in one step (a rename), so the controller never reads half of it."""
    with open(path, encoding="utf-8") as stream:
        text = with_modes(stream.read(), allow_commands, interlock_mode, max_reading_age)
    temporary = os.path.join(os.path.dirname(path), "." + os.path.basename(path) + ".alpaca-ci")
    with open(temporary, "w", encoding="utf-8") as stream:
        stream.write(text)
    os.replace(temporary, path)


def modes(args):
    write_modes(args.settings_file, args.allow_commands, args.interlock_mode)
    print(f"This run's settings: AllowCommands {args.allow_commands}, InterlockMode {args.interlock_mode}")


RELAY_WRITE = re.compile(r"^relay register 0x([0-9A-Fa-f]+) -> 0x([0-9A-Fa-f]+)$")


def relay_writes(history):
    """The relay register's values, in order, from the emulated plant's history (its first value, then each write's)."""
    values = []
    for event in history:
        match = RELAY_WRITE.match(event.get("detail", "")) if event.get("kind") == "Hat" else None
        if match:
            if not values:
                values.append(int(match.group(1), 16))
            values.append(int(match.group(2), 16))
    return values or [0]


def direction_states(values):
    """The direction relays' states the register went through: none, open, close or both, repeats collapsed."""
    names = {0: "none", OPEN_RELAY: "open", CLOSE_RELAY: "close", OPEN_RELAY | CLOSE_RELAY: "both"}
    states = []
    for value in values:
        state = names[value & (OPEN_RELAY | CLOSE_RELAY)]
        if not states or states[-1] != state:
            states.append(state)
    return states


def relay_problems(values, expected):
    """What is wrong with the register's values for a run whose direction relays should go on in `expected`'s order."""
    states = direction_states(values)
    problems = []
    if "both" in states:
        problems.append("both direction relays were on at once")
    for before, after in zip(states, states[1:]):
        if {before, after} == {"open", "close"}:
            problems.append(f"one write turned the {before} relay off and the {after} relay on: the first was not off before the second went on")
    energised = [state for state in states if state != "none"]
    if energised != list(expected):
        problems.append(f"the direction relays went on as {energised or 'never'}, expected {list(expected) or 'never'}")
    if values[-1] & (OPEN_RELAY | CLOSE_RELAY):
        problems.append(f"a direction relay is still on (register {values[-1]:#x})")
    return problems


def new_alert(before, after, command, key):
    """The alert in `after` for a refused `command` with reason `key` that is new, or counted again, since `before`."""
    counts = {alert["id"]: alert["count"] for alert in before}
    return next((alert for alert in after
                 if alert["kind"] == "Refused" and alert.get("command") == command and key in alert["reasonKeys"]
                 and alert["count"] > counts.get(alert["id"], 0)), None)


def after_conformu(args):
    """ConformU opened and closed the emulated roof through Alpaca: it is at rest, and the history says it was Alpaca."""
    for url, what in ((args.controller, "controller"), (args.emulator, "HAT emulator")):
        loopback(url, what)
    admin = {"X-Api-Key": read_secret(args.admin_key)}
    status_code, status = request("GET", f"{args.controller}/api/v4.0/RoofControl/Status", headers=admin)
    if status_code != 200:
        fail(f"The controller's status answered {status_code}")
    _, plant = request("GET", f"{args.emulator}/api/emulator/status")
    _, violations = request("GET", f"{args.emulator}/api/emulator/violations")
    problems = []
    if status["status"] not in ("Open", "Closed") or status["isMoving"] or status["isStartPending"]:
        problems.append(f"the roof is {status['status']} (moving: {status['isMoving']}), not at rest at a limit")
    if status["isFaultLatched"]:
        problems.append(f"a fault is latched ({status['latchedFaultReason']})")
    if status["relayRegisterState"] != "Verified" or status["relayRegisterMask"] != 0 or plant["plant"]["relayRegister"] != 0:
        problems.append(f"a relay is on or unverified (controller {status['relayRegisterState']} {status['relayRegisterMask']}, plant {plant['plant']['relayRegister']})")
    if violations:
        problems.append(f"the emulated plant recorded {len(violations)} violations: {', '.join(v['kind'] for v in violations)}")

    status_code, history = request("GET", f"{args.controller}/api/v4.0/RoofControl/History?take=500", headers=admin)
    if status_code != 200 or not history.get("allUsers"):
        fail(f"The history answered {status_code}, or not everyone's")
    alpaca = [entry for entry in history["entries"] if entry.get("source") == "Alpaca" and entry["accepted"]]
    for command in ("Open", "Close"):
        if not any(entry["command"] == command and entry["authMethod"] == "basic" for entry in alpaca):
            problems.append(f"the history has no accepted Alpaca {command} sent with HTTP Basic")
    if problems:
        fail("After ConformU: " + "; ".join(problems))
    counts = {command: sum(entry["command"] == command for entry in alpaca) for command in ("Open", "Close")}
    print(f"After ConformU: the emulated roof is {status['status']} at rest, every relay off, no violation or fault; "
          f"the history has {counts['Open']} Alpaca Open and {counts['Close']} Alpaca Close accepted")


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

    def command(self, member, headers):
        status, answer = request("PUT", f"{self.args.alpaca}/api/v1/dome/0/{member}", headers=headers,
                                 form={"ClientID": str(self.client_id), "ClientTransactionID": "41"})
        if status != 200:
            fail(f"{member} answered HTTP {status}")
        return answer["ErrorNumber"], answer["ErrorMessage"]

    def close(self, headers):
        return self.command("closeshutter", headers)

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

    def grant(self, keys, kind="Close"):
        status, _ = request("POST", f"{self.args.controller}/api/v4.0/alpaca/grants", headers=bearer(self.token),
                            body={"kind": kind, "reasonKeys": keys})
        if status != 201:
            fail(f"The {kind} grant answered {status}")

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


class OpenRuns(Runs):
    """The scripted Open runs (phase 4): each starts from a reset of the emulated plant, checked, and checks after it."""

    SCOPE = "Main"
    WEATHER = "Emulated weather"

    def __init__(self, args):
        super().__init__(args)
        self.client_id = 8
        with open(args.settings_file, encoding="utf-8") as stream:
            current = read_modes(stream.read())
        if current is None:
            fail(f"{args.settings_file} has no modes of this run's: only a settings file the harness prepared is changed")
        self.allow_commands, self.interlock_mode = current
        self.max_reading_age = None

    def open(self, headers):
        return self.command("openshutter", headers)

    def shutter(self):
        status, answer = request("GET", f"{self.args.alpaca}/api/v1/dome/0/shutterstatus?ClientID={self.client_id}&ClientTransactionID=42")
        if status != 200 or answer["ErrorNumber"] != 0:
            fail(f"ShutterStatus answered HTTP {status}: {answer}")
        return answer["Value"]

    def source(self, name):
        return next(source for source in self.status()["safety"]["sources"] if source["name"] == name)

    def monitor_requests(self):
        return self.emulator("/alpaca")["safetyMonitors"][0]["requests"]

    def set_modes(self, allow_commands=None, interlock_mode=None):
        """Rewrites the run-only settings file, and waits for the running controller to have reloaded it."""
        self.allow_commands = allow_commands or self.allow_commands
        self.interlock_mode = interlock_mode or self.interlock_mode
        write_modes(self.args.settings_file, self.allow_commands, self.interlock_mode, self.max_reading_age)
        self.wait(lambda: self.status()["alpaca"]["mode"] == self.allow_commands
                  and self.status()["safety"]["interlockMode"] == self.interlock_mode,
                  f"the controller to reload its settings (AllowCommands {self.allow_commands}, InterlockMode {self.interlock_mode})", timeout=20)

    def set_max_reading_age(self, value, seconds):
        """RoofSafety:MaxReadingAge for the stale-weather run (None: the default, `seconds`), and waits for the reload."""
        self.max_reading_age = value
        write_modes(self.args.settings_file, self.allow_commands, self.interlock_mode, value)
        self.wait(lambda: self.source(self.WEATHER)["maxReadingAgeSeconds"] == seconds,
                  f"the controller to reload its settings (MaxReadingAge {seconds} s)", timeout=20)

    def begin(self, what, metres=None):
        """Resets the plant with the roof closed (or `metres` open) and its Alpaca devices parked, safe and answering, and
        waits for the controller to see it so, the weather from a read made after the reset (a failed read leaves the
        ordinary conditions Safe on the last good reading, but refuses an Alpaca Open)."""
        reading = self.source(self.WEATHER)["lastReadingUtc"]
        self.emulator("/reset", {} if metres is None else {"positionMeters": metres})

        def ready():
            status = self.status()
            at = status["status"] == "Closed" if metres is None else not (status["isOpenLimitActive"] or status["isClosedLimitActive"])
            sources = {source["name"]: source for source in status["safety"]["sources"]}
            return (at and not status["isMoving"] and not status["isStartPending"] and not status["isFaultLatched"]
                    and status["relayRegisterState"] == "Verified" and status["relayRegisterMask"] == 0
                    and {name: source["state"] for name, source in sources.items()} == {self.SCOPE: "Safe", self.WEATHER: "Safe"}
                    and sources[self.WEATHER]["lastReadingUtc"] != reading)

        self.wait(ready, f"the starting state ({what})")
        status = self.status()
        alpaca = status["alpaca"]
        if alpaca["hold"]["held"] or alpaca["operatorLease"]["held"] or alpaca["grants"]:
            fail(f"The starting state ({what}): the Stop hold, a person's lease or a grant is there")
        if alpaca["mode"] != self.allow_commands or status["safety"]["interlockMode"] != self.interlock_mode:
            fail(f"The starting state ({what}): AllowCommands {alpaca['mode']}, InterlockMode {status['safety']['interlockMode']}")
        if self.plant()["relayRegister"] != 0:
            fail(f"The starting state ({what}): a relay is on")
        self.position = self.plant()["positionMeters"]

    def settled(self, expected, why):
        """The run ended at rest: the direction relays went on only as `expected`, each off before the other on, and no
        plant violation. Returns the direction relays' states."""
        self.wait(lambda: not self.status()["isMoving"] and self.plant()["relayRegister"] == 0, f"the roof at rest: {why}")
        values = relay_writes(self.emulator("/history?limit=5000"))
        problems = relay_problems(values, expected)
        violations = self.emulator("/violations")
        if violations:
            problems.append(f"the emulated plant recorded {len(violations)} violations")
        if self.status()["isFaultLatched"]:
            problems.append(f"a fault latched ({self.status()['latchedFaultReason']})")
        if problems:
            fail(f"{why}: " + "; ".join(problems))
        return direction_states(values)

    def refused(self, member, contains, why):
        number, message = self.command(member, basic(self.key))
        if number != 0x40B or contains not in message:
            fail(f"{why}: {member} answered {number:#x}: {message}")
        return message

    def refused_with_alert(self, keys, contains, why):
        """An Open refused for exactly `keys`: 0x40B with `contains` in its words, a new alert with those reasons, and
        nothing moves."""
        before = self.alerts()
        self.refused("openshutter", contains, why)
        alert = new_alert(before, self.alerts(), "Open", sorted(keys)[0])
        if alert is None:
            fail(f"{why}: no new alert for the refused Open with {sorted(keys)[0]}")
        if set(alert["reasonKeys"]) != set(keys):
            fail(f"{why}: the refused Open's alert names {alert['reasonKeys']}, expected {sorted(keys)}")
        self.still(why)
        return alert

    def opens(self, why, headers=None):
        number, message = self.open(headers or basic(self.key))
        if number != 0:
            fail(f"{why}: OpenShutter answered {number:#x}: {message}")
        shutter = self.shutter()
        if shutter != OPENING:
            fail(f"{why}: ShutterStatus read {shutter} straight after OpenShutter, not Opening ({OPENING})")

    def reaches(self, state, shutter, why, timeout=60):
        self.wait(lambda: self.status()["status"] == state and not self.status()["isMoving"], f"the roof {state}: {why}", timeout=timeout)
        if self.shutter() != shutter:
            fail(f"{why}: the roof is {state} but ShutterStatus reads {self.shutter()}")

    def run(self):
        self.connect()

        # A safe Open: it starts at once (Opening straight after), the open relay alone goes on, the roof reaches Open.
        self.begin("closed, the weather safe, the mount parked")
        started = time.monotonic()
        self.opens("a safe Open")
        self.reaches("Open", OPEN, "a safe Open")
        self.settled(["open"], "a safe Open")
        plant = self.plant()
        if plant["positionMeters"] < plant["travelMeters"] - 0.01:
            fail(f"A safe Open stopped at {plant['positionMeters']:.3f} m, short of the open limit")
        print(f"Run: a safe Open starts at once (Opening) and reaches Open in {time.monotonic() - started:.1f} s; only the open relay went on")

        # Unsafe weather: refused with the weather's reason and an alert; nothing moves, and nothing is queued for later.
        self.begin("closed")
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": False})
        self.wait(lambda: self.source(self.WEATHER)["state"] == "Unsafe", "the controller to read the weather unsafe")
        self.refused_with_alert({f"weather-unsafe:{self.WEATHER}"}, f"Weather monitor '{self.WEATHER}' reports unsafe weather", "unsafe weather")
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": True})
        self.wait(lambda: self.source(self.WEATHER)["state"] == "Safe", "the controller to read the weather safe again")
        self.still("a refused Open is never queued", seconds=1.5)
        self.settled([], "unsafe weather")
        print("Run: unsafe weather refuses the Open (0x40B) with an alert; nothing moves, nor once the weather is safe again")

        # A weather monitor not connected, then one failing: the latest read failed, so the Open is refused at once, while
        # the ordinary conditions still count the last safe reading (until it is older than MaxReadingAge).
        for name, change in (("not connected", {"connected": False}), ("failing", {"behaviour": "DriverError"})):
            self.begin("closed")
            requests = self.monitor_requests()
            self.emulator("/alpaca/safetymonitor", {"device": 0, **change})
            self.wait(lambda: self.monitor_requests() >= requests + 2, "the controller to read the weather monitor again")
            time.sleep(0.5)
            source = self.source(self.WEATHER)
            if source["state"] != "Safe" or source["isStale"]:
                fail(f"A weather monitor {name}: the ordinary conditions read {source['state']}, not the last safe reading")
            self.refused_with_alert({f"weather-unknown:{self.WEATHER}"}, f"Weather monitor '{self.WEATHER}': its latest read failed",
                                    f"a weather monitor {name}")
            self.settled([], f"a weather monitor {name}")
        print("Run: a weather monitor not connected, and one failing, refuse the Open at once (its latest read failed), "
              "while the ordinary conditions still show the last safe reading; nothing moves")

        # Stale weather (plan section 9): the last safe reading expires with no good read since (the monitor stops
        # answering, and MaxReadingAge is shortened to 7 s for this run by a reload; it must be longer than the 1 s poll and
        # the 5 s read timeout), so the ordinary conditions read it stale too. End to end a reading only goes stale after
        # failed reads, so the Open is refused by its failed latest read (the Open rule checks that first), with the
        # weather's reason and an alert; nothing moves, and nothing is queued for when fresh safe readings return. The age
        # rule on its own is RoofAlpacaOpenTests.AStaleWeatherReading_RefusesTheOpen's.
        self.begin("closed")
        self.set_max_reading_age("00:00:07", 7)
        self.emulator("/alpaca/safetymonitor", {"device": 0, "connected": False})
        self.wait(lambda: self.source(self.WEATHER)["isStale"], "the weather's last safe reading to go stale", timeout=30)
        source = self.source(self.WEATHER)
        if source["state"] != "Unknown":
            fail(f"Stale weather: the ordinary conditions read {source['state']}, not Unknown")
        self.refused_with_alert({f"weather-unknown:{self.WEATHER}"}, f"Weather monitor '{self.WEATHER}': its latest read failed", "stale weather")
        self.emulator("/alpaca/safetymonitor", {"device": 0, "connected": True})
        self.wait(lambda: self.source(self.WEATHER)["state"] == "Safe" and not self.source(self.WEATHER)["isStale"],
                  "the controller to read the weather safe and fresh again")
        self.still("a refused Open is never queued", seconds=1.5)
        self.settled([], "stale weather")
        self.set_max_reading_age(None, 60)
        print("Run: stale weather (the last safe reading older than MaxReadingAge, the ordinary conditions Unknown) refuses "
              "the Open (0x40B, its latest read failed) with an alert; nothing moves, nor once fresh safe readings return")

        # An unparked blocking mount: refused with an alert; nothing moves or is queued once it parks.
        self.begin("closed")
        self.emulator("/alpaca/telescope", {"device": 0, "parked": False})
        self.refused_with_alert({f"scope-not-clear:{self.SCOPE}"}, f"Telescope '{self.SCOPE}' is not parked", "an unparked mount")
        self.emulator("/alpaca/telescope", {"device": 0, "parked": True})
        self.still("a refused Open is never queued", seconds=1.5)
        self.settled([], "an unparked mount")
        print("Run: an unparked mount refuses the Open (0x40B) with an alert; nothing moves, nor once it parks")

        # An Open grant: a person grants the refused Open's reasons (the weather's and the mount's) through the session
        # API; the next Open goes and uses it up. Near the open limit, so the move is short.
        self.begin("part way, near the open limit", metres=1.6)
        self.emulator("/alpaca/telescope", {"device": 0, "parked": False})
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": False})
        self.wait(lambda: self.source(self.WEATHER)["state"] == "Unsafe", "the controller to read the weather unsafe")
        keys = {f"scope-not-clear:{self.SCOPE}", f"weather-unsafe:{self.WEATHER}"}
        alert = self.refused_with_alert(keys, f"Telescope '{self.SCOPE}' is not parked; Weather monitor '{self.WEATHER}' reports unsafe weather",
                                        "the mount unparked and the weather unsafe")
        self.grant(alert["reasonKeys"], kind="Open")
        grants = self.grants()
        if [(grant["kind"], set(grant["reasonKeys"])) for grant in grants] != [("Open", keys)]:
            fail(f"The Open grant is not in force as made: {grants}")
        self.opens("an Open grant")
        if self.grants():
            fail("The Open grant was not used up by the Open it let go")
        self.reaches("Open", OPEN, "an Open grant")
        self.settled(["open"], "an Open grant")
        print("Run: an Open grant (the weather's and the mount's reasons) lets the refused Open go, and is used up")

        # Block: a grant made in Warn is dropped when the interlock turns Block at runtime (a reload of the controller's
        # settings file); no Open grant can be made (403 BlockMode), and the Open is refused with nothing moving.
        self.begin("closed")
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": False})
        self.wait(lambda: self.source(self.WEATHER)["state"] == "Unsafe", "the controller to read the weather unsafe")
        alert = self.refused_with_alert({f"weather-unsafe:{self.WEATHER}"}, f"Weather monitor '{self.WEATHER}' reports unsafe weather", "unsafe weather, in Warn")
        self.grant(alert["reasonKeys"], kind="Open")
        if [grant["kind"] for grant in self.grants()] != ["Open"]:
            fail("The Open grant made in Warn is not in force")
        self.set_modes(interlock_mode="Block")
        alpaca = self.status()["alpaca"]
        if alpaca["grants"]:
            fail(f"The Open grant made in Warn survived the interlock turning Block: {alpaca['grants']}")
        if "Block" not in (alpaca["openGrantUnavailable"] or ""):
            fail(f"In Block the status does not say why no Open grant can be made: {alpaca['openGrantUnavailable']}")
        status, problem = request("POST", f"{self.args.controller}/api/v4.0/alpaca/grants", headers=bearer(self.token),
                                  body={"kind": "Open", "reasonKeys": alert["reasonKeys"]})
        if status != 403 or not isinstance(problem, dict) or problem.get("alpacaRefusal") != "BlockMode":
            fail(f"In Block a new Open grant answered {status}, {problem.get('alpacaRefusal') if isinstance(problem, dict) else problem}; expected 403 BlockMode")
        self.refused("openshutter", "The safety interlock is Block", "an Open in Block")
        self.still("an Open refused in Block")
        if self.grants():
            fail("A grant appeared in Block")
        self.set_modes(interlock_mode="Warn")
        if self.status()["alpaca"]["openGrantUnavailable"] is not None:
            fail("Back in Warn, Open grants are still unavailable")
        self.emulator("/alpaca/safetymonitor", {"device": 0, "safe": True})
        self.settled([], "Block")
        print("Run: a grant made in Warn is dropped when the interlock turns Block at runtime; in Block no Open grant can be "
              "made (403 BlockMode) and the Open is refused; nothing moves")

        # A reversal: an Alpaca Close while the Alpaca client's own Open runs reverses the roof; the open relay is off
        # before the close relay goes on, never both, and the roof reaches Closed.
        self.begin("closed")
        self.opens("the reversal's Open")
        self.wait(lambda: self.plant()["positionMeters"] > 0.2, "the roof to open 0.2 m")
        number, message = self.close(basic(self.key))
        if number != 0:
            fail(f"An Alpaca Close during the Alpaca client's Open answered {number:#x}: {message}")
        if self.shutter() != CLOSING:
            fail(f"ShutterStatus read {self.shutter()} straight after the reversing Close, not Closing ({CLOSING})")
        self.reaches("Closed", CLOSED, "the reversal")
        states = self.settled(["open", "close"], "the reversal")
        print(f"Run: an Alpaca Close reverses the Alpaca client's Open ({' -> '.join(states)}): the open relay off before the "
              "close relay on, never both, and the roof closes")

        # CloseOnly (a reload): an Open is refused and nothing moves, while a Close goes.
        self.set_modes(allow_commands="CloseOnly")
        self.begin("part way open", metres=0.3)
        self.refused("openshutter", "AllowCommands is CloseOnly", "an Open with CloseOnly")
        self.still("an Open refused with CloseOnly")
        number, message = self.close(basic(self.key))
        if number != 0:
            fail(f"A Close with CloseOnly answered {number:#x}: {message}")
        self.reaches("Closed", CLOSED, "a Close with CloseOnly")
        self.settled(["close"], "CloseOnly")
        self.set_modes(allow_commands="OpenAndClose")
        if self.status()["alpaca"]["hold"]["held"]:
            fail("Going back from CloseOnly to OpenAndClose set the Stop hold")
        print("Run: with CloseOnly an Open is refused and nothing moves, while a Close goes")

        self.begin("closed, for what follows")


def open_runs(args):
    for url, what in ((args.controller, "controller"), (args.alpaca, "Alpaca server"), (args.emulator, "HAT emulator")):
        loopback(url, what)
    OpenRuns(args).run()


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

    one = commands.add_parser("modes")
    one.add_argument("--settings-file", required=True)
    one.add_argument("--allow-commands", required=True, choices=ALLOW_COMMANDS)
    one.add_argument("--interlock-mode", required=True, choices=INTERLOCK_MODES)
    one.set_defaults(handler=modes)

    one = commands.add_parser("after-conformu")
    one.add_argument("--controller", required=True)
    one.add_argument("--emulator", required=True)
    one.add_argument("--admin-key", required=True)
    one.set_defaults(handler=after_conformu)

    one = commands.add_parser("open-runs")
    one.add_argument("--controller", required=True)
    one.add_argument("--alpaca", required=True)
    one.add_argument("--emulator", required=True)
    one.add_argument("--token", required=True)
    one.add_argument("--external-key", required=True)
    one.add_argument("--settings-file", required=True, help="the run-only copy of the controller's appsettings.Development.json")
    one.set_defaults(handler=open_runs)

    args = parser.parse_args()
    args.handler(args)


if __name__ == "__main__":
    main()
