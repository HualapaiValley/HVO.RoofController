"""Real kernel checks only on Linux. Skips are not Linux integration evidence."""
import json
import os
from pathlib import Path
import socket
import sys
import tempfile
import unittest
from linux_peer import UnsupportedPlatform, read_peer


@unittest.skipUnless(sys.platform == "linux", "Linux kernel unavailable on this host")
class LinuxKernelTests(unittest.TestCase):
    def test_socketpair_returns_kernel_credentials(self):
        first, second = socket.socketpair()
        with first, second:
            peer = read_peer(first)
            self.assertEqual((os.getpid(), os.geteuid(), os.getegid()), (peer.pid, peer.uid, peer.gid))

    def test_named_socket_ignores_user_supplied_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            address = str(Path(directory) / "probe.sock")
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(address)
                listener.listen(1)
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.connect(address)
                    accepted, _ = listener.accept()
                    with accepted:
                        client.sendall(json.dumps({"uid": os.geteuid() + 1, "SUDO_UID": 0}).encode())
                        claim = json.loads(accepted.recv(512))
                        actual = read_peer(accepted)
                        self.assertNotEqual(claim["uid"], actual.uid)
                        self.assertEqual(os.geteuid(), actual.uid)

    def test_tcp_socket_is_rejected(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as connection:
            with self.assertRaises(ValueError):
                read_peer(connection)

    def test_datagram_socket_is_rejected(self):
        with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as connection:
            with self.assertRaises(ValueError):
                read_peer(connection)

    def test_unconnected_stream_is_rejected(self):
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            with self.assertRaises((ValueError, OSError)):
                read_peer(connection)


@unittest.skipIf(sys.platform == "linux", "Non-Linux refusal check")
class PlatformGuardTests(unittest.TestCase):
    def test_no_username_environment_fallback(self):
        with self.assertRaises(UnsupportedPlatform):
            read_peer(None)


if __name__ == "__main__":
    unittest.main()
