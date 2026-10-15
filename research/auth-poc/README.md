# Isolated Linux identity authentication experiment

This is one dependency-free Python policy prototype, separate from every production
project. Start with the [architecture, threats and migration proposal](../../docs/proposals/linux-auth-poc.md).
The core model evaluates decisions only. It has no controller endpoint, hardware command
or persistent credential storage. Native primitive tests and the optional
[disposable integrated fixture](integration/README.md) extend coverage with Linux APIs;
the latter provisions test accounts and PAM/sudo policy only inside its throwaway container.

Run from the repository root on Python 3.10 or later. The operator-designated Linux
development VM is now the canonical test environment; use it by default instead of
native M4 execution. The M4 originals remain recovery/reference copies:

```sh
python3 research/auth-poc/run_checks.py
python3 research/auth-poc/demo.py
```

`run_checks.py` prints machine-readable results on stdout and a test summary on stderr.
The [recorded Linux verification](verification.json) includes the platform, passed test
identities and counts. The [initial macOS run](verification-macos.json) is retained separately. The mock provider accepts the public string `public-mock-input`; generated
handles begin `MOCK-ONLY-`. These are deliberately inert fixtures, not passwords, API
keys or tokens suitable for another process or system. Nothing reads existing credentials.

| Component | What it demonstrates | Important boundary |
|---|---|---|
| [auth_model.py](auth_model.py) | Two local authorization gates, provider-qualified identities, audience separation, scoped keys, expiry/revocation and interaction decisions | Admin policy, account enrollment and password providers are mocked; Python objects supplied by the harness are trusted inputs |
| [linux_peer.py](linux_peer.py) | Narrow, real Linux `SO_PEERCRED` reader on a connected Unix stream socket | Executed on Linux; does not implement polkit subject binding, namespaces or authorization; not wired to a daemon |
| [test_auth_model.py](test_auth_model.py) | Unauthorized/spoofed callers, role/scoping failures, lifecycle and prompt rules | Policy tests do not prove resistance to a Linux kernel, broker or PAM integration bug |
| [test_linux_peer.py](test_linux_peer.py) | Kernel peer identity and payload-spoof rejection on Linux; fail-closed platform guard elsewhere | All five native tests pass on the designated VM; no accounts are created |
| [test_native_linux.py](test_native_linux.py) | Child PID, inherited-socket identity, forged ancillary credentials, pidfd lifetime and real PAM API fixtures | PAM uses private permit/deny stacks only; no real password/account validation, polkit grant or sudo call |
| [demo.py](demo.py) | Eight readable allow/deny scenarios | No interactive UI is implemented and no action is executed |

The first M4 run had no active OrbStack engine. The operator subsequently identified and
authorized the existing Linux development VM. Its identity and existing trusted SSH
access were verified; the same POC was migrated there without starting an engine or
changing guest accounts, security/network configuration, services or other checkouts.

Current result: **71 passed, one non-Linux guard skipped**. Twelve native Linux tests
exercise kernel mechanisms and the real PAM transaction API. PAM uses an ephemeral,
private configuration with existing `pam_permit`/`pam_deny` modules, and no secret input.
This verifies API success and auth/account failure handling, not real Linux passwords,
account expiry/lockout, PAM MFA or production integration. No system PAM file is changed.
The kernel tests demonstrate that inherited sockets retain connection-time identity:
SO_PEERCRED alone does not identify whichever process later sends on a passed socket.

Polkit authorization, a complete race-resistant broker binding, namespace boundaries,
real account lifecycle monitoring and production UI/browser integration remain unverified.
The separate disposable integration now verifies a constrained sudo helper's original
UID, real pam_unix password/account checks, and fixture loopback HTTP authorization. Native pidfd lifetime is tested, but that does not prove safe polkit integration.
No host packages were installed. The optional container image adds Python and sudo.
Existing headers for PAM/polkit development are absent;
the private PAM tests use the installed runtime library through ctypes. Any future
native build dependencies need an explicit setup decision before installation.

POC scope/lifetime constants are test values, not proposed production defaults. The core
model generates no real hashes, random secrets or API credentials. The disposable
integration uses public fixture passwords and hashes only within the removed container. Existing kiosk PIN code and the
earlier installer fixes remain unchanged. The initial standalone transfer is only a
migration receipt; continue this same POC in the canonical auth research worktree.

The integrated result is **31 passed, zero failed**; separately, **122 existing production
authentication tests passed**. See the [integration evidence and production-install
blocker](integration/README.md). These results do not establish a complete production
installation with the proposed authentication architecture.
