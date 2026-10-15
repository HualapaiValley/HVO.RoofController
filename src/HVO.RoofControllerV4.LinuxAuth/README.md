# Native Linux authentication worker

This opt-in Linux host service validates local password accounts and attests a fixed sudo helper's caller. It is
disabled unless explicitly installed and enabled. The controller remains responsible for enrollment, role, operation,
session and API-key policy. It must verify every assertion and revalidate Linux-backed authorization with this worker.
The worker has no LAN listener, roof API client or hardware access.

## Installation contract

Publish `HVO.RoofControllerV4.LinuxAuth.csproj` for `linux-arm64` or `linux-x64` as a self-contained single executable
using `-p:PublishSingleFile=true --self-contained true`. Install it root-owned at
`/opt/hvo-roof-linux-auth/HVO.RoofControllerV4.LinuxAuth`. Templates live in
[`deploy/linux-auth`](../../deploy/linux-auth).

The root-owned `/etc/hvo-roof/linux-auth/worker.json` selects the audience, controller UID/GID, enrollment and signing
key paths, and Unix socket paths. No command accepts a configurable path from an unprivileged caller. Configuration,
key and enrollment must be regular files owned by root, with no group-write or other-user permissions; every parent
directory must be root-owned and not group/other-writable. Use mode 0600. The signing key is 32 random raw bytes,
generated at installation and retained on rerun. Never write its contents to an answers file or log.

`enrollment.json` is an array of `{uid,userName,subject,enrollmentStamp,enabled}`. Each native account, subject and name
must be unique; root cannot be enrolled. Subjects and stamps are random persistent identifiers, not names or numeric
UIDs. App enrollment is separate and must explicitly assign a matching subject/stamp and controller role. Installation
does not create host accounts or promote accounts automatically. Preserve enrolled identities on rerun; an account
replacement or explicit reenrollment requires a new stamp. UID/name checking does not by itself distinguish a deleted
account recreated with the same UID and name; an administrator must revoke/rotate its enrollment when replacing it.

The service creates two sockets under a root-controlled runtime directory:

* `/run/hvo-roof-linux-auth/controller/worker.sock`: mode 0660, exact configured controller UID plus root accepted.
* `/run/hvo-roof-linux-auth/local.sock`: mode 0600, root helper only.

Mount only the **controller subdirectory**, read-only, into the controller container. Mounting the subdirectory lets
recreated sockets appear after worker restarts. Never expose the local socket to that container. Keep the signing key
private to root and the controller. HMAC means the controller is a trusted verifier that can also sign; root and the
controller are part of this trust boundary. This mechanism does not defend against either being compromised.

The service preserves its runtime directory across stops/restarts so existing bind mounts retain the same directory
inode. A root-owned exclusive `flock` prevents simultaneous workers; startup removes only stale root-owned socket
inodes inside trusted parents, never links or other files. A reboot recreates `/run`, so start and ready the worker
before Docker starts containers mounting its directory. For a non-root controller UID, provision both runtime parent
directories root-owned with that controller group and mode 0750; the service template defaults to a root controller.
The `Type=notify` service sends `READY=1` only after both sockets are listening with final permissions; start has a
15-second deadline. Its Docker ordering is opt-in installer packaging, not a request to restart an existing daemon.
The service working directory is its private program directory. The executable also explicitly sets its content root
to its trusted binary directory and removes unused ASP.NET configuration providers. Otherwise, a launch from `/`
would recursively watch the host filesystem for optional `appsettings` reloads before sockets become ready. The native
fixture launches from `/` and verifies bounded readiness and the absence of those unused file watchers.

## Protocol and caller chain

The private controller HTTP socket accepts:

* `POST /v1/password`, `{userName,password}` → `{assertion}` after native account and PAM authentication/account checks.
* `POST /v1/revalidate`, `{subject,enrollmentStamp,purpose}` → `{valid:true,userName}` only while that exact enrollment and
  native account remain valid. No cached positive decision outlives an account/enrollment change.
  Purpose defaults to `linux-password`; `local-admin` follows the native sudo account policy below.

`POST /v1/local` accepts `{uid,operation}` only on the separate local socket and only from a kernel root peer. The
fixed `/usr/local/libexec/hvo-roof-local-auth` wrapper accepts no arguments, removes runtime environment overrides and
executes the helper mode. Its stdin accepts only `{operation}` with `read`, `operate`, `configure` or `identity`.
The helper requires effective root, the exact protected `SUDO_COMMAND`, and a nonzero protected `SUDO_UID` established
by sudo. The kernel socket peer proves the **root helper**, not the original person; protected sudo origin establishes
that person. No arbitrary caller UID, username or saved session can enter through helper input. Root remains trusted.
Use `sudo -n -- /usr/local/libexec/hvo-roof-local-auth` for noninteractive use; a denial never falls back to a saved web
credential. The sudoers template requires the exact command with no arguments (`""`).

Success prints only JSON `{assertion}`. A proof is `base64url(payload).base64url(HMAC-SHA256(payloadBytes))`. Its camelCase
JSON payload contains `version:1`, purpose `linux-password` or `local-admin`, `provider:"linux"`, `subject`, `userName`,
`enrollmentStamp`, `audience`, `operation`, `issuedAt`, `expiresAt` and a random 32-byte `nonce`. Lifetime is 30 seconds.
Password assertions use operation `login`. The controller must enforce purpose, audience, freshness, exact operation,
single use, current enrollment and its independent role policy. Native sudo approval alone grants no controller role.

## Native account handling and limits

The dedicated `hvo-roof-linux-auth` PAM service uses `pam_unix` authentication and account management. It never opens a
PAM session or changes a password. Expiry, disabled enrollment and UID/name mismatch deny. Since `pam_acct_mgmt` alone
does not reject `passwd -l`, a private native shadow lookup examines only the leading marker for a locked/empty local
password for web password/session/key identities; it never marshals or logs the hash and clears the native buffer.
Local sudo proofs and their `local-admin` revalidation require account/enrollment validity but do not require an
enabled password: an SSH-key-only account with valid NOPASSWD helper authorization can use them. Account expiry still
denies both routes. This provider is deliberately local-account only; LDAP/MFA/challenge flows
are not implemented by this one-password adapter.

Each NSS/PAM transaction runs in a private child with credentials on stdin, a cleared environment and a five-second
deadline. A timed-out process tree is killed and access is denied. The worker serializes native checks, limits HTTP
connections and input sizes, and allows at most five failed password attempts per username per minute. Passwords are
not arguments or logs; managed input strings are not guaranteed erased from memory. Core dumps are disabled by the
service. Authentication failures return generic codes, without account enumeration details or PAM diagnostics.

## Verification

[`tests/linux-auth/native_worker_fixture.py`](../../tests/linux-auth/native_worker_fixture.py) refuses to run outside
a marked disposable Docker container. It creates only temporary fixture accounts, randomly generates their passwords
in memory, and exercises the actual compiled executable, PAM, sudo and kernel socket checks. The
[`test runner`](../../tests/linux-auth/run_native_worker.py) compiles in the selected Linux development environment,
builds a constrained no-network runtime container, captures account-file hashes, and removes its exact container/image.
This verifies the native component; it does not claim a production controller, installer, kiosk or full-system test.
