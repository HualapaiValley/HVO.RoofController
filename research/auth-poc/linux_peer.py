"""Real Linux SO_PEERCRED probe, not a complete authorization adapter.

No listener is installed. The caller provides a connected Unix stream socket.
The PID is diagnostic only: it is NOT a safe polkit process binding. Native
pidfd/bus subject validation, namespace policy and admin authorization are absent.
"""
import socket
import struct
import sys
from dataclasses import dataclass


class UnsupportedPlatform(RuntimeError):
    pass


@dataclass(frozen=True)
class KernelPeer:
    pid: int
    uid: int
    gid: int


def read_peer(connection):
    if sys.platform != "linux" or not hasattr(socket, "SO_PEERCRED"):
        raise UnsupportedPlatform("Linux SO_PEERCRED is unavailable; no identity fallback")
    if connection.family != socket.AF_UNIX or connection.getsockopt(
            socket.SOL_SOCKET, socket.SO_TYPE) != socket.SOCK_STREAM:
        raise ValueError("A connected Unix stream socket is required")
    # Linux ucred: signed pid_t, unsigned uid_t and gid_t.
    raw = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("iII"))
    pid, uid, gid = struct.unpack("iII", raw)
    if pid <= 0 or uid == 0xffffffff or gid == 0xffffffff:
        raise ValueError("No valid connected peer credentials")
    return KernelPeer(pid, uid, gid)
