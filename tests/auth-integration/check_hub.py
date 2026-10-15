#!/usr/bin/env python3
"""Real installed SignalR acceptance, restricted to the marked disposable guest.

Uses the guest's real CA and Python's standard library. No fake API, socket, clock, credential
validator or SignalR transport is substituted. Never emits credentials or response bodies.
"""
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

from run_acceptance import Api, STATE, guard, private_write, run, expect, AcceptanceMismatch, local_assertion


class HubClosed(Exception):
    pass


def negotiate(api, token=None, key=None):
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    if key:
        headers["X-Api-Key"] = key
    request = urllib.request.Request(api.url + "/hubs/roof/negotiate?negotiateVersion=1",
                                     data=b"", method="POST", headers=headers)
    try:
        with urllib.request.urlopen(request, context=api.ssl, timeout=30) as reply:
            return reply.status, json.loads(reply.read())
    except urllib.error.HTTPError as error:
        return error.code, None


class Hub:
    """Minimal RFC6455 masked client speaking the SignalR JSON v1 protocol."""
    def __init__(self, api, token=None, key=None):
        self.socket = None
        self.buffer = bytearray()
        self.records = bytearray()
        self.fragment = bytearray()
        status, negotiation = negotiate(api, token, key)
        expect(status, 200)
        expect(isinstance(negotiation, dict) and bool(negotiation.get("connectionToken")), True)
        parsed = urllib.parse.urlsplit(api.url)
        expect(parsed.scheme, "https")
        expect(parsed.hostname in ("localhost", "127.0.0.1", "::1"), True)
        endpoint = "/hubs/roof?id=" + urllib.parse.quote(negotiation["connectionToken"], safe="")
        host = parsed.hostname
        port = parsed.port or 443
        raw = socket.create_connection((host, port), timeout=30)
        try:
            self.socket = api.ssl.wrap_socket(raw, server_hostname=host)
            websocket_key = base64.b64encode(os.urandom(16)).decode("ascii")
            host_header = ("[" + host + "]" if ":" in host else host) + ":" + str(port)
            headers = [
                "GET " + endpoint + " HTTP/1.1", "Host: " + host_header,
                "Connection: Upgrade", "Upgrade: websocket", "Sec-WebSocket-Version: 13",
                "Sec-WebSocket-Key: " + websocket_key,
            ]
            if token:
                headers.append("Authorization: Bearer " + token)
            if key:
                headers.append("X-Api-Key: " + key)
            self.socket.sendall(("\r\n".join(headers) + "\r\n\r\n").encode("ascii"))
            response = bytearray()
            while b"\r\n\r\n" not in response:
                block = self.socket.recv(4096)
                if not block:
                    raise HubClosed()
                response.extend(block)
                if len(response) > 32768:
                    raise ValueError("Oversized WebSocket response header")
            header, remainder = bytes(response).split(b"\r\n\r\n", 1)
            self.buffer.extend(remainder)
            lines = header.decode("ascii").split("\r\n")
            expect(int(lines[0].split(" ")[1]), 101)
            fields = dict(line.split(":", 1) for line in lines[1:] if ":" in line)
            fields = {name.lower(): value.strip() for name, value in fields.items()}
            accepted = base64.b64encode(hashlib.sha1(
                (websocket_key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()).decode("ascii")
            expect(fields.get("sec-websocket-accept"), accepted)
            self.send(b'{"protocol":"json","version":1}\x1e')
            handshake = self.message(time.monotonic() + 20)
            expect(handshake, {})
        except BaseException:
            raw.close()
            if self.socket:
                self.socket.close()
            raise

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        if self.socket:
            try:
                self.send(struct.pack("!H", 1000), opcode=8)
            except (OSError, HubClosed):
                pass
            self.socket.close()
            self.socket = None

    def send(self, payload, opcode=1):
        mask = os.urandom(4)
        length = len(payload)
        if length < 126:
            header = bytes([0x80 | opcode, 0x80 | length])
        elif length < 65536:
            header = bytes([0x80 | opcode, 0x80 | 126]) + struct.pack("!H", length)
        else:
            header = bytes([0x80 | opcode, 0x80 | 127]) + struct.pack("!Q", length)
        masked = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
        self.socket.sendall(header + mask + masked)

    def read(self, count, deadline):
        while len(self.buffer) < count:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Live hub deadline exceeded")
            self.socket.settimeout(remaining)
            try:
                data = self.socket.recv(max(4096, count - len(self.buffer)))
            except (ConnectionResetError, BrokenPipeError, ssl.SSLEOFError):
                raise HubClosed() from None
            if not data:
                raise HubClosed()
            self.buffer.extend(data)
        result = bytes(self.buffer[:count])
        del self.buffer[:count]
        return result

    def text(self, deadline):
        while True:
            first, second = self.read(2, deadline)
            opcode = first & 15
            final = bool(first & 128)
            length = second & 127
            if length == 126:
                length = struct.unpack("!H", self.read(2, deadline))[0]
            elif length == 127:
                length = struct.unpack("!Q", self.read(8, deadline))[0]
            if length > 2 * 1024 * 1024:
                raise ValueError("Oversized WebSocket frame")
            mask = self.read(4, deadline) if second & 128 else None
            payload = self.read(length, deadline)
            if mask:
                payload = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
            if opcode == 8:
                raise HubClosed()
            if opcode == 9:
                self.send(payload, opcode=10)
                continue
            if opcode == 10:
                continue
            if opcode not in (0, 1):
                raise ValueError("Unexpected SignalR WebSocket opcode")
            self.fragment.extend(payload)
            if len(self.fragment) > 2 * 1024 * 1024:
                raise ValueError("Oversized SignalR message")
            if final:
                result = bytes(self.fragment)
                self.fragment.clear()
                return result

    def message(self, deadline):
        while b"\x1e" not in self.records:
            self.records.extend(self.text(deadline))
        record, _, rest = self.records.partition(b"\x1e")
        self.records = bytearray(rest)
        value = json.loads(record)
        if value.get("type") == 7:
            raise HubClosed()
        return value

    def status(self, timeout=20):
        deadline = time.monotonic() + timeout
        while True:
            value = self.message(deadline)
            if value.get("type") == 1 and value.get("target") == "Status":
                arguments = value.get("arguments", [])
                expect(len(arguments), 1)
                snapshot = arguments[0]
                expect(isinstance(snapshot.get("sequence"), int), True)
                expect(bool(snapshot.get("instanceId")), True)
                expect(snapshot["status"].get("hatMode"), "Emulated")
                return snapshot

    def wait_closed(self, timeout=15):
        deadline = time.monotonic() + timeout
        try:
            while True:
                self.message(deadline)
        except HubClosed:
            return


def acceptance():
    marker = guard()
    api = Api()
    cases = []
    report_path = STATE / "hub-report.json"

    def record(name, fn):
        try:
            fn()
            case = {"name": name, "passed": True, "coverage": "installed-production-SignalR-over-verified-TLS"}
        except Exception as error:
            case = {"name": name, "passed": False, "error_type": type(error).__name__}
            if isinstance(error, AcceptanceMismatch):
                case["diagnostic"] = error.diagnostic
        cases.append(case)
        private_write(report_path, json.dumps({"run_id": marker["run_id"], "cases": cases}, indent=2) + "\n")
        print(json.dumps(case), flush=True)

    def mint(session, suffix, seconds=300, scopes=None):
        expires = dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=seconds)
        status, created = api.request("POST", "Auth/Keys",
            {"name": "hub-fixture-" + suffix, "scopes": scopes if scopes is not None else ["read"], "expiresUtc": expires.isoformat()}, token=session)
        expect(status, 201)
        return created["secret"], created["key"]["id"]

    def live_session():
        session = api.login("virtual")
        with Hub(api, token=session) as hub:
            first = hub.status()
            second = hub.status()
            expect(second["sequence"] > first["sequence"], True)
            expect(second["instanceId"], first["instanceId"])

    def session_revocation():
        session = api.login("virtual")
        with Hub(api, token=session) as hub:
            hub.status()
            expect(api.request("DELETE", "Auth/Session", token=session)[0], 204)
            hub.wait_closed()
            expect(negotiate(api, token=session)[0], 401)

    def key_revocation():
        session = api.login("virtual")
        key, key_id = mint(session, "revocation")
        try:
            with Hub(api, key=key) as hub:
                hub.status()
                expect(api.request("DELETE", "Auth/Keys/" + key_id, token=session)[0], 204)
                hub.wait_closed()
                expect(negotiate(api, key=key)[0], 401)
        finally:
            api.request("DELETE", "Auth/Keys/" + key_id, token=session)

    def key_expiry():
        session = api.login("virtual")
        key, key_id = mint(session, "expiry", seconds=15)
        try:
            with Hub(api, key=key) as hub:
                hub.status()
                hub.wait_closed(timeout=30)
                expect(negotiate(api, key=key)[0], 401)
        finally:
            api.request("DELETE", "Auth/Keys/" + key_id, token=session)

    def operate_only_key():
        session = api.login("virtual")
        key, key_id = mint(session, "operate-only", scopes=["operate"])
        try:
            expect(negotiate(api, key=key)[0], 403)
        finally:
            api.request("DELETE", "Auth/Keys/" + key_id, token=session)

    def local_read_lease_expiry():
        returncode, assertion = local_assertion("fixture-admin", "read")
        expect(returncode, 0)
        expect(bool(assertion), True)
        status, session = api.request("POST", "Auth/LocalSession", {"assertion": assertion})
        expect(status, 200)
        expires = dt.datetime.fromisoformat(session["expiresUtc"].replace("Z", "+00:00"))
        remaining = (expires - dt.datetime.now(dt.timezone.utc)).total_seconds()
        expect(0 < remaining <= 30.5, True)
        with Hub(api, token=session["token"]) as hub:
            hub.status()
            hub.wait_closed(timeout=remaining + 8)
            expect(dt.datetime.now(dt.timezone.utc) >= expires, True)
            expect(negotiate(api, token=session["token"])[0], 401)

    def linux_disabled():
        admin = api.login("virtual")
        session = api.login("linux", "fixture-user")
        status, principals = api.request("GET", "Identity/Principals", token=admin)
        expect(status, 200)
        owner = next(p for p in principals if p["provider"] == "linux" and p["name"] == "fixture-user")
        expect(owner["enabled"], True)
        key, key_id = mint(session, "linux-disable")
        try:
            with Hub(api, token=session) as session_hub, Hub(api, key=key) as key_hub:
                session_hub.status()
                key_hub.status()
                expect(api.request("PUT", "Identity/Principals/" + owner["id"],
                    {"role": owner["role"], "enabled": False}, token=admin)[0], 200)
                session_hub.wait_closed()
                key_hub.wait_closed()
                expect(negotiate(api, token=session)[0], 401)
                expect(negotiate(api, key=key)[0], 401)
        finally:
            expect(api.request("PUT", "Identity/Principals/" + owner["id"],
                {"role": owner["role"], "enabled": True}, token=admin)[0], 200)

    def linux_account_expiry():
        session = api.login("linux", "fixture-user")
        key, key_id = mint(session, "linux-account-expiry")
        try:
            with Hub(api, token=session) as session_hub, Hub(api, key=key) as key_hub:
                session_hub.status()
                key_hub.status()
                expect(run(["chage", "--expiredate", "0", "fixture-user"]).returncode, 0)
                session_hub.wait_closed()
                key_hub.wait_closed()
                expect(negotiate(api, token=session)[0], 401)
                expect(negotiate(api, key=key)[0], 401)
        finally:
            expect(run(["chage", "--expiredate", "-1", "fixture-user"]).returncode, 0)
            restored = api.login("linux", "fixture-user")
            api.request("DELETE", "Auth/Keys/" + key_id, token=restored)

    record("hub_rejects_unauthenticated_negotiate", lambda: expect(negotiate(api)[0], 401))
    record("hub_virtual_session_receives_initial_and_live_status", live_session)
    record("hub_logout_closes_connected_session", session_revocation)
    record("hub_read_scoped_key_connects_and_revocation_closes_it", key_revocation)
    record("hub_key_wall_clock_expiry_closes_connection", key_expiry)
    record("hub_operate_only_key_cannot_read_hub", operate_only_key)
    record("hub_native_local_read_lease_closes_within_real_expiry", local_read_lease_expiry)
    record("hub_disabled_linux_owner_closes_session_and_key", linux_disabled)
    record("hub_native_account_expiry_closes_session_and_key", linux_account_expiry)
    result = {"phase": "hub", "run_id": marker["run_id"], "cases": cases,
              "passed": sum(case["passed"] for case in cases),
              "failed": sum(not case["passed"] for case in cases),
              "scope": "real HTTPS negotiate, WebSocket handshake, SignalR JSON status and live credential revalidation; no motion"}
    private_write(report_path, json.dumps(result, indent=2) + "\n")
    print(json.dumps({"passed": result["passed"], "failed": result["failed"], "report": str(report_path)}))
    return 0 if not result["failed"] else 1


if __name__ == "__main__":
    sys.exit(acceptance())
