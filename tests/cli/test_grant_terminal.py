#!/usr/bin/env python3
"""The grant CLI's Linux terminal admission, through real kernel ptys (#231).

Run after building the CLI. HVO_ROOF_CLI may name a published executable or a
framework-dependent DLL; without it, use the repository's Debug build. Script
only jobs without a build skip these tests. No roof controller is started: the
only possible HTTP request is a GET to a reserved, unlistening loopback port.
"""

import errno
import os
import pathlib
import select
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import unittest

if sys.platform == "linux":
    import pty


ROOT = pathlib.Path(__file__).resolve().parents[2]
TERMINAL_REFUSAL = "A grant is a person's decision, asked and answered at a terminal"
CONFIRMATION_REQUIRED = 10
UNREACHABLE = 4

# This wrapper owns the pty's session. In background mode it remains the
# foreground process group, just as an interactive shell would for a job run &.
LAUNCHER = r"""
import fcntl
import os
import signal
import subprocess
import sys
import termios

mode = sys.argv[1]
terminal = int(sys.argv[2])
command = sys.argv[3:]
if mode != 'no-controlling-tty':
    fcntl.ioctl(terminal, termios.TIOCSCTTY, 0)
os.set_inheritable(terminal, False)
if mode != 'background':
    os.execv(command[0], command)
signal.signal(signal.SIGTTIN, signal.SIG_IGN)
signal.signal(signal.SIGTTOU, signal.SIG_IGN)
child = subprocess.Popen(command, process_group=0)
try:
    sys.exit(child.wait(timeout=10))
finally:
    if child.poll() is None:
        child.kill()
        child.wait()
"""


@unittest.skipUnless(sys.platform == "linux", "Linux controlling-terminal checks")
class GrantTerminalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        configured = os.environ.get("HVO_ROOF_CLI")
        executable = pathlib.Path(configured) if configured else (
            ROOT / "src/HVO.RoofControllerV4.Cli/bin/Debug/net10.0/hvo-roof.dll"
        )
        if not executable.is_file():
            if configured:
                raise AssertionError("HVO_ROOF_CLI does not name a built CLI")
            raise unittest.SkipTest("Build hvo-roof first, or set HVO_ROOF_CLI")
        executable = executable.resolve()
        if executable.suffix == ".dll":
            dotnet = shutil.which("dotnet")
            if not dotnet:
                raise AssertionError("A framework-dependent CLI needs dotnet")
            cls.command = [dotnet, str(executable)]
        else:
            cls.command = [str(executable)]

    def run_cli(self, mode="foreground", redirect=None, markers=None):
        with tempfile.TemporaryDirectory(prefix="hvo-grant-terminal-") as directory:
            # Hold this port without listening, so no controller or other service
            # can receive a request even when admission intentionally succeeds.
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
                command = [
                    *self.command, "alpaca", "grant", "--alert", "1",
                    "--controller", f"http://127.0.0.1:{port}",
                    "--credentials-file", str(pathlib.Path(directory) / "credentials.json"),
                ]
                # No inherited credentials, automation flags or private settings.
                environment = {
                    name: value for name in ("PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64")
                    if (value := os.environ.get(name)) is not None
                }
                environment.update(HOME=directory, XDG_CONFIG_HOME=directory,
                                   TERM="xterm-256color", LANG="C.UTF-8",
                                   DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
                environment.update(markers or {})
                master, slave = pty.openpty()
                extra_master, extra_slave = pty.openpty()
                try:
                    with tempfile.TemporaryFile() as redirected:
                        def stream(name):
                            return redirected if redirect == name else extra_slave if redirect == f"other-{name}" else slave

                        process = subprocess.Popen(
                            [sys.executable, "-c", LAUNCHER, mode, str(slave), *command],
                            stdin=stream("stdin"), stdout=stream("stdout"), stderr=stream("stderr"),
                            env=environment, start_new_session=True, pass_fds=(slave,),
                        )
                        try:
                            code = process.wait(timeout=15)
                        finally:
                            if process.poll() is None:
                                # Normal foreground execs have this process group;
                                # the background wrapper bounds and kills its child.
                                os.killpg(process.pid, signal.SIGKILL)
                                process.wait()
                        redirected.seek(0)
                        output = redirected.read()
                    os.close(slave)
                    slave = None
                    os.close(extra_slave)
                    extra_slave = None
                    for descriptor in (master, extra_master):
                        while select.select([descriptor], [], [], 0)[0]:
                            try:
                                chunk = os.read(descriptor, 65536)
                            except OSError as error:
                                if error.errno == errno.EIO:
                                    break  # Linux reports a closed pty this way.
                                raise
                            if not chunk:
                                break
                            output += chunk
                    return code, output.decode("utf-8", errors="replace")
                finally:
                    for descriptor in (master, slave, extra_master, extra_slave):
                        if descriptor is not None:
                            os.close(descriptor)

    def assert_refused(self, **settings):
        code, output = self.run_cli(**settings)
        self.assertEqual(CONFIRMATION_REQUIRED, code, output)
        self.assertIn(TERMINAL_REFUSAL, output)

    def test_foreground_controlling_terminal_is_admitted(self):
        code, output = self.run_cli()
        self.assertEqual(UNREACHABLE, code, output)
        self.assertNotIn(TERMINAL_REFUSAL, output)

    def test_terminal_without_a_controlling_terminal_is_refused(self):
        self.assert_refused(mode="no-controlling-tty")

    def test_background_process_group_is_refused(self):
        self.assert_refused(mode="background")

    def test_redirected_standard_stream_is_refused(self):
        for stream in ("stdin", "stdout", "stderr"):
            with self.subTest(stream=stream):
                self.assert_refused(redirect=stream)

    def test_different_terminal_for_a_standard_stream_is_refused(self):
        for stream in ("stdin", "stdout", "stderr"):
            with self.subTest(stream=stream):
                self.assert_refused(redirect=f"other-{stream}")

    def test_automation_markers_are_refused_even_with_a_controlling_terminal(self):
        for marker in ("CI", "GITHUB_ACTIONS", "GITLAB_CI", "TF_BUILD", "JENKINS_URL",
                       "TEAMCITY_VERSION", "EXPECT", "EXPECT_SCRIPT", "PEXPECT",
                       "AUTOMATION", "NONINTERACTIVE"):
            with self.subTest(marker=marker):
                self.assert_refused(markers={marker: "1"})
        for markers in ({"TERM": "dumb"}, {"DEBIAN_FRONTEND": "noninteractive"}):
            with self.subTest(markers=markers):
                self.assert_refused(markers=markers)


if __name__ == "__main__":
    unittest.main()
