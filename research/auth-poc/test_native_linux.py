"""Native unprivileged Linux tests. PAM uses PRIVATE permit/deny fixture stacks.

No real password/account verification, system PAM edits, polkit grants or sudo calls.
"""
import ctypes
import ctypes.util
import errno
import json
import os
from pathlib import Path
import select
import socket
import struct
import sys
import tempfile
import unittest
from linux_peer import read_peer


@unittest.skipUnless(sys.platform == "linux", "Linux kernel unavailable")
class NativeProcessTests(unittest.TestCase):
    def test_connected_child_has_kernel_child_pid(self):
        with tempfile.TemporaryDirectory() as directory:
            address = str(Path(directory) / "peer.sock")
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.settimeout(4)
                listener.bind(address)
                listener.listen(1)
                child = os.fork()
                if child == 0:
                    try:
                        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                            client.settimeout(4)
                            client.connect(address)
                            client.sendall(b"identity fields are not evidence")
                        os._exit(0)
                    except BaseException:
                        os._exit(1)
                try:
                    connection, _ = listener.accept()
                    with connection:
                        peer = read_peer(connection)
                        self.assertEqual(child, peer.pid)
                        self.assertEqual(os.geteuid(), peer.uid)
                        self.assertNotEqual(os.getpid(), peer.pid)
                finally:
                    _, status = os.waitpid(child, 0)
                self.assertEqual(0, os.waitstatus_to_exitcode(status))

    def test_inherited_socket_keeps_connection_time_pid(self):
        parent_pid = os.getpid()
        first, second = socket.socketpair()
        with first, second:
            first.settimeout(4)
            child = os.fork()
            if child == 0:
                try:
                    second.sendall(json.dumps({"child": os.getpid()}).encode())
                    os._exit(0)
                except BaseException:
                    os._exit(1)
            try:
                report = json.loads(first.recv(1024))
                observed_sender = read_peer(first)
                self.assertEqual(parent_pid, observed_sender.pid)
                self.assertEqual(child, report["child"])
                self.assertNotEqual(observed_sender.pid, child)
            finally:
                _, status = os.waitpid(child, 0)
            self.assertEqual(0, os.waitstatus_to_exitcode(status))

    def test_kernel_rejects_forged_root_ancillary_credentials(self):
        status = Path("/proc/self/status").read_text().splitlines()
        capability = int(next(x.split()[1] for x in status if x.startswith("CapEff:")), 16)
        uids = [int(x) for x in next(x for x in status if x.startswith("Uid:")).split()[1:]]
        if 0 in uids or capability & (1 << 7):
            self.skipTest("Requires an unprivileged process without CAP_SETUID")
        first, second = socket.socketpair()
        with first, second:
            second.setsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED, 1)
            with self.assertRaises(PermissionError) as raised:
                first.sendmsg([b"x"], [(socket.SOL_SOCKET, socket.SCM_CREDENTIALS,
                    struct.pack("iII", os.getpid(), 0, os.getgid()))])
            self.assertEqual(errno.EPERM, raised.exception.errno)

    @unittest.skipUnless(hasattr(os, "pidfd_open"), "pidfd_open unavailable")
    def test_pidfd_tracks_child_exit_without_pid_lookup(self):
        read_end, write_end = os.pipe()
        child = os.fork()
        if child == 0:
            os.close(write_end)
            try:
                os.read(read_end, 1)
                os._exit(0)
            except BaseException:
                os._exit(1)
        os.close(read_end)
        handle = None
        try:
            handle = os.pidfd_open(child, 0)
            poll = select.poll()
            poll.register(handle, select.POLLIN)
            self.assertEqual([], poll.poll(0))
            os.close(write_end)
            write_end = None
            self.assertTrue(poll.poll(4000), "pidfd must become readable after child exit")
        finally:
            if write_end is not None:
                os.close(write_end)
            if handle is not None:
                os.close(handle)
            _, status = os.waitpid(child, 0)
        self.assertEqual(0, os.waitstatus_to_exitcode(status))


@unittest.skipUnless(sys.platform == "linux", "Linux PAM fixture test")
class NativePamFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        library = ctypes.util.find_library("pam")
        if not library:
            raise unittest.SkipTest("Existing libpam unavailable; no installation attempted")
        cls.lib = ctypes.CDLL(library)
        if not hasattr(cls.lib, "pam_start_confdir"):
            raise unittest.SkipTest("Private PAM configuration API unavailable")
        cls.modules = {}
        for name in ("pam_permit", "pam_deny"):
            candidates = list(Path("/usr/lib").glob("*/security/" + name + ".so"))
            candidates += [Path("/usr/lib/security") / (name + ".so")]
            found = next((p for p in candidates if p.is_file()), None)
            if found is None:
                raise unittest.SkipTest("Existing PAM fixture module unavailable")
            cls.modules[name] = found

    def fixture_transaction(self, auth_module, account_module):
        callback_type = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_int,
                                        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
        class Conversation(ctypes.Structure):
            _fields_ = [("conv", callback_type), ("appdata_ptr", ctypes.c_void_p)]
        conversations = []
        def refuse_conversation(count, messages, responses, context):
            conversations.append(count)
            return 19  # PAM_CONV_ERR: fixture must never request a real secret.
        callback = callback_type(refuse_conversation)
        conversation = Conversation(callback, None)
        lib = self.lib
        lib.pam_start_confdir.argtypes = [ctypes.c_char_p, ctypes.c_char_p,
            ctypes.POINTER(Conversation), ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
        lib.pam_start_confdir.restype = ctypes.c_int
        for name in ("pam_authenticate", "pam_acct_mgmt", "pam_end"):
            function = getattr(lib, name)
            function.argtypes = [ctypes.c_void_p, ctypes.c_int]
            function.restype = ctypes.c_int
        # The private directory overrides /etc/pam.d. Neither module consults
        # a real account/password, and no pam_open_session/chauthtok is called.
        with tempfile.TemporaryDirectory(prefix="hvo-pam-fixture-") as directory:
            service = Path(directory) / "hvo-poc-fixture"
            service.write_text(f"auth required {self.modules[auth_module]}\n"
                               f"account required {self.modules[account_module]}\n")
            service.chmod(0o600)
            handle = ctypes.c_void_p()
            started = lib.pam_start_confdir(b"hvo-poc-fixture", b"hvo-inert-fixture-user",
                                           ctypes.byref(conversation), os.fsencode(directory),
                                           ctypes.byref(handle))
            self.assertEqual(0, started)
            result = {"auth": None, "account": None}
            try:
                result["auth"] = lib.pam_authenticate(handle, 0)
                if result["auth"] == 0:
                    result["account"] = lib.pam_acct_mgmt(handle, 0)
            finally:
                ended = lib.pam_end(handle, result["account"] or result["auth"] or 0)
                self.assertEqual(0, ended)
            self.assertEqual([], conversations)
            return result

    def test_real_pam_library_accepts_private_permit_fixture(self):
        self.assertEqual({"auth": 0, "account": 0},
                         self.fixture_transaction("pam_permit", "pam_permit"))

    def test_real_pam_auth_rejection_does_not_proceed_to_account(self):
        result = self.fixture_transaction("pam_deny", "pam_permit")
        self.assertNotEqual(0, result["auth"])
        self.assertIsNone(result["account"])

    def test_real_pam_account_rejection_after_authentication(self):
        result = self.fixture_transaction("pam_permit", "pam_deny")
        self.assertEqual(0, result["auth"])
        self.assertNotEqual(0, result["account"])


if __name__ == "__main__":
    unittest.main()
