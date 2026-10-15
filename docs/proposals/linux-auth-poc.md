# Linux and virtual identity authentication: isolated design and proof of concept

**Status: research, a policy model and disposable native integration; no production
deployment or authentication change.** This is the current direction for local CLI/UI authentication and supersedes
the earlier proposal to solve local access by prefilling an application-login username.
The working installation and accounts stay as they are. The
[earlier bootstrap and UX requirements](initial-admin-bootstrap.md) remain context,
including the still-undecided initial administrator/password policy.

## Recommended architecture

Use a small native host broker for local Linux identity and authorization. Keep the CLI
and terminal UI unprivileged so the broker sees the actual caller. Independently require
an approved Linux admin action and a controller role. Website users choose Linux-backed
or virtual authentication, both mapped to controller principals. Preserve the graphical
kiosk's existing PIN path. This is a recommendation to review, not installed configuration.

| Entry point | Identity proof | Authorization | Session behavior |
|---|---|---|---|
| Local CLI/terminal UI | Kernel-derived Unix peer identity plus safe process/connection binding | Explicit Linux admin policy **and** controller role for the operation | Connection-bound local lease; never a saved web token or claimed username |
| Website, Linux account | Dedicated host PAM service: authentication followed by account validation | Enrolled Linux principal's controller role | Web session with provider/account binding, expiry and revocation |
| Website, virtual account | Existing application password verifier | Virtual principal's controller role | Existing web-session protections, extended identity representation |
| Explicit API automation | Scoped, revocable credential owned by either principal type | Key scopes intersect current owner role and enabled state | Independent key expiry/revocation; no interactive prompts |
| Graphical kiosk | Existing kiosk key plus PIN flow | Existing PIN/session/device rules | Existing PIN lifetime, idle timeout and Stop behavior remain unchanged |

The controller remains the final authorization authority and retains all safety and Stop
semantics. A Linux admin grant is not permission to bypass controller roles or interlocks.
Root compromise is outside this application's trust boundary; claiming the software can
constrain a hostile host root administrator would be misleading.

### Existing code findings

The inspected baseline is `26fe1b4`, including all preceding installer fixes:

- `StoredUser` is currently keyed by name with one role and password/PIN hashes.
  `StoredSession` carries a user name, role, credential kind and expiry. `StoredApiKey`
  has a name, role, kiosk flag and hash, without an owner principal, operation scopes or
  expiry. See [identity records](../../src/HVO.RoofControllerV4.RPi/Security/Identity/RoofIdentityDocument.cs).
- Password and PIN authentication share [RoofSignInService](../../src/HVO.RoofControllerV4.RPi/Security/Identity/RoofSignInService.cs).
  [Credential validation](../../src/HVO.RoofControllerV4.RPi/Security/RoofCredentialValidator.cs)
  already rechecks revocation, roles and kiosk-device binding. Extend those checks rather
  than adding a second path that bypasses them.
- [CLI credential resolution](../../src/HVO.RoofControllerV4.Client/RoofCredentialStore.cs)
  prefers environment credentials, then a saved session, then a saved key. That is useful
  for explicit remote/API mode but must not satisfy the new local Linux-admin requirement.
  [Terminal startup](../../src/HVO.RoofControllerV4.Cli/RoofCli.Ui.cs) already rejects JSON
  mode and redirected input; output/TTY and new challenge behavior still need tests.
- [Web sign-in](../../src/HVO.RoofControllerV4.Web/Sessions/WebAccountEndpoints.cs) already
  has antiforgery, safe-return handling, rate limiting and server-side controller sessions.
  Retain these boundaries while adding explicit provider selection.

## Local caller proof and Linux admin rights

Linux `SO_PEERCRED` returns kernel credentials associated with a Unix connection. Request
fields, `USER`, `SUDO_USER`, `SUDO_UID` and a proposed username cannot substitute for it.
Credentials are associated with connection creation, so retaining or passing an open
socket needs an explicit capability/lifetime policy. The POC's reader returns PID/UID/GID;
it does **not** claim that a PID alone proves a live authorized process.
[Linux Unix-domain socket documentation](https://man7.org/linux/man-pages/man7/unix.7.html).

Current polkit documentation explicitly warns that its PID fallback has known race
problems. Prefer a supported pidfd-backed subject or a verified system-bus sender binding
whose implementation supplies the necessary safe process identity. Verify the actual
kernel, D-Bus and polkit combination; fail closed if it cannot provide that guarantee.
Do not substitute a `/proc` PID/start-time lookup and call it race-free.
[PolkitUnixProcess](https://polkit.pages.freedesktop.org/polkit/PolkitUnixProcess.html).

| Alternative | Assessment |
|---|---|
| Native broker plus polkit | Preferred design: retain the unprivileged caller, authorize an exact HVO action and use the OS authentication agent if policy requires it. The subject still needs explicit eligibility and controller enrollment; another administrator completing a challenge does not change the caller's principal. Full polkit subject binding remains untested; kernel peer and pidfd primitives now have native coverage. |
| Narrow sudo helper | Possible fallback for SSH/headless use. Permit a fixed, root-owned helper with constrained inputs, no shell or user-selected executable, and bind its result to the original unprivileged connection/action. Origin attribution and descriptor handoff need a separate native proof before adoption. |
| Entire CLI/UI under sudo | Avoid: expands privileged parsing/UI code and makes the socket peer root. Reading an untrusted original-user claim afterward does not repair identity attribution. |
| Group name, username or `sudo -n true` test | Insufficient: a group or successful unrelated command is not an authorization decision for this action. Do not parse `sudo -l` output into a replacement policy engine. |

Polkit distinguishes authorization, challenges and refusal. Its retained authorization
may remain valid for an action even when request details change; avoid using retained
authorization to bypass a different operation's policy. Ship an action description with
conservative defaults; site-specific rules are a later administrator decision.
[polkit authorization model](https://polkit.pages.freedesktop.org/polkit/polkit.8.html).
Sudo's policy uses the real invoking UID, and `-n` prevents interaction. Any supported
sudo route should delegate prompting to sudo, not capture its password in the app.
[sudo manual](https://man7.org/linux/man-pages/man8/sudo.8.html).

A future broker should have an owned runtime directory, bounded messages, timeouts and
no arbitrary command/file proxy. Use distinct local-client and controller/PAM interfaces.
The controller may accept broker identity only over a separate protected Unix API
listener with verified broker credentials; public TCP must never accept an identity header
as proof. Broker assertions identify principals, never grant a client-supplied role.
The broker proxy still uses the controller API and does not implement roof behavior.

The existing controller runs in a container. Host broker and container UIDs, user
namespaces, socket ownership/mount permissions and service identities need explicit
verification. Container UID 0 is not automatically host root. Do not mount the Docker
socket or host shadow database into the controller to solve authentication.
[Linux user namespace mappings](https://man7.org/linux/man-pages/man7/user_namespaces.7.html).

## Website Linux authentication and principal mapping

Keep provider choice explicit: `linux` versus `virtual`; never try one provider then
silently fall back to the other after failure. The same display name may exist in both.
Represent identities by immutable application principal IDs with provider and enrolled
external subject. For Linux, record UID plus an enrollment generation and validated
account mapping. Linux UID reuse has no universal native incarnation token: detect
deletion/recreation through managed enrollment or require re-enrollment. The POC models
this generation; it does not implement NSS monitoring.

Put PAM behind a narrowly scoped native host worker, not inside the unprivileged web
container with access to host credentials. A dedicated, administrator-owned PAM service
must run `pam_authenticate` and then `pam_acct_mgmt`; resolve the final canonical PAM user
before mapping the result. Fail closed for account expiry, restrictions or required
password changes. Do not silently change Linux passwords or invoke account creation.
[PAM authentication](https://man7.org/linux/man-pages/man3/pam_authenticate.3.html),
[PAM account checks](https://man7.org/linux/man-pages/man3/pam_acct_mgmt.3.html).

PAM modules may need privileged helpers; running a web process as an arbitrary user is
not proof it can authenticate every host account. Give only a dedicated worker the
privileges required by its reviewed PAM stack, with per-attempt isolation, limits and
timeouts. Do not invoke or repurpose `unix_chkpwd` as a generic password-verification
command. [pam_unix](https://man7.org/linux/man-pages/man8/pam_unix.8.html).

A Linux user's web password necessarily exists briefly in the TLS endpoint and PAM
conversation memory. “Hidden entry” does not make that plaintext disappear. Prevent
request-body logging, tracing and core-dump disclosure; never put it in arguments, an
environment variable, a durable queue or a stored session. Zero native conversation
buffers where practical; managed/browser copies cannot be promised perfect erasure.
Preserve TLS, CSRF protection, uniform failed-login messages, bounded concurrency and
rate limits at the public endpoint and worker. Choose supported PAM conversation/MFA
behavior explicitly; reject unsupported challenges rather than granting access.

Linux account lock/revocation must affect later web sessions and keys as well as new
logins. Define bounded revalidation and an enrollment/account epoch updated on disable,
role change and revocation. Fail closed when required identity freshness cannot be
established. The POC tests an epoch/disable update, not automatic OS-change detection.

## Terminal UX and saved credentials

For local mode, retain the caller's Linux identity; an editable application username is
not appropriate. If Linux authorization and the controller role already permit access,
open directly. If the OS policy requires authentication, use its trusted agent with
Retry/Cancel. No local application-password fallback and no automatic saved-web-session
bypass. A controller role failure is an explicit permission error.

For explicitly selected remote mode, the earlier inline application login remains useful:
an accepted saved session opens directly; a missing/expired session offers provider,
editable username and hidden password, then resumes the UI. Noninteractive commands never
prompt or consume unexpected input. Clear distinction between local, remote and API modes
is a proposed UX contract; exact flags and compatibility behavior still need review.
Client mode selection is not itself a security boundary: each server listener enforces
its own authentication and authorization rules.

Keep local leases connection-bound and memory-only. Revalidate admin grants and controller
roles at operations and status-feed renewal; do not replay a command after reauthentication.
Saved web tokens retain expiration/revocation and private storage. Logout must distinguish
local removal from confirmed server revocation. Scope cached state to controller/provider/
principal; never reuse a saved identity for another Linux caller, and explain explicit
environment overrides instead of silently switching identities. No default session or
credential migration is performed by this research.

## Scoped keys for both principal types

Use owner principal ID, audience, explicit operation scopes, expiry, revocation and an
owner epoch. Effective permission is the intersection of key scope and current owner
role; changing or disabling the owner must not leave a privileged key behind. Audit the
key ID and owner, never its secret. Key issuance requires authenticated authorization,
not a supplied owner name. Do not permit a narrow key to mint broader keys.

The POC demonstrates self-owned keys for Linux and virtual principals. All handles are
public `MOCK-ONLY-...` strings kept in memory; no entropy, real key material, hashes or
persistent credentials are generated. Production hashing, storage, key delivery,
rotation, delegated issuance and exact scope taxonomy remain unimplemented. Existing
kiosk keys and PIN privileges must be preserved explicitly, including Stop availability.

## Threat model

| Threat or failure | Required control | What the POC establishes |
|---|---|---|
| Spoofed username, UID, role or sudo environment | Kernel-derived caller; reject identity fields on local protocol | Model rejects spoofed fields/environment; native Linux socket and forged ancillary-credential checks pass |
| PID reuse, socket transfer or privilege transition | Safe subject binding, connection lifecycle and bounded grants | Model binding/grant tests plus native pidfd lifetime and inherited-socket tests; no complete polkit subject or transfer protection |
| Host/container identity confusion | Explicit host identity boundary and verified namespace mapping | Disposable single-container fixture only; production host/container boundary unverified |
| Admin rights mistaken for controller authority | Require both grants; deny unregistered/ambiguous principals | Unauthorized, unenrolled and insufficient-role tests pass |
| Stolen web session bypasses local admin check | Separate audiences and peer-bound local leases | Cross-audience and wrong-peer tests pass |
| Same Linux/virtual name or reused UID | Provider-qualified immutable ID and enrollment lifecycle | Same-name separation and changed-generation rejection pass |
| PAM password succeeds but account is blocked | Authentication plus account-management checks | Native private-stack tests plus disposable pam_unix password and expired-account tests pass; production PAM policy remains unverified |
| Password theft, brute force or login-worker exhaustion | TLS, no secret logging, PAM isolation and bounded admission | Fixture loopback/password pipeline only; production TLS, rate and worker isolation unverified |
| Key escalation, theft or stale authority | Scope intersection, authenticated issuance, expiry and revocation | Inert key scope, lifetime, disable and role-change tests pass |
| Broker or host-root compromise | Minimize trusted code, isolated interfaces and audit; acknowledge TCB | Not claimed to be contained by this prototype |
| Login loops, accidental prompts or command replay | Explicit denial states, prompt-free batch mode; no replay | Interaction decisions tested; no real terminal/browser UI implemented |
| Kiosk regression | Preserve current PIN/device/Stop path and regression coverage | Existing PIN/device/session/Stop tests pass; installed graphical kiosk not exercised |

## Migration and review gates

1. Review this architecture and remaining choices: exact Linux admin action/eligibility,
   supported kernel/polkit binding, sudo fallback, principal enrollment, web PAM/MFA,
   remote/API CLI syntax, key scopes and account-change revalidation. The earlier initial
   admin/password bootstrap decision is still open; local admin access does not implicitly
   create or reset the fixed bootstrap account.
2. Run native integration in a separate disposable Linux environment. Test actual kernel
   credentials with different UIDs, user namespaces, inherited/passed sockets, PID reuse
   and safe polkit subjects; challenge/deny/noninteractive cases; sudo origin binding if
   adopted; and PAM success, wrong password, account expiry, lockout, canonical user and
   unsupported conversation. Use only disposable fixtures. Do not use the working Pi.
3. Design a versioned store migration on copies. Preserve virtual IDs, password/PIN hashes,
   configured device keys and role semantics. Require explicit linking of Linux identities;
   never merge on a matching name. Add key owner/scope/expiry fields without silently
   expanding legacy access. Audit last-admin protection and every API/status-hub check.
4. Add production adapters behind disabled feature switches, and extend emulator-only
   authorization, browser, terminal, concurrent-store and kiosk/Stop tests. Mock results
   cannot satisfy these native and end-to-end gates. Review worker sandboxing and the
   host/container Unix API boundary before enabling it anywhere.
5. A later deployment requires a separate approved plan and rollback rehearsal. Retain
   compatible old data, explicit schema checks and the revocation history. Never downgrade
   a newer store blindly or restore old credentials in a way that resurrects revoked access.
   Preserve the current known-working login and kiosk paths until acceptance is complete.

## Evidence and limits

The [single prototype](../../research/auth-poc/README.md) now runs in the operator-designated
Linux development VM, the default location for development tests. The current result is
**72 tests run: 71 passed, one non-Linux platform-guard skipped**, with zero failures.
Twelve tests exercise native Linux behavior: the original five socket checks, child PID,
inherited connection identity, forged ancillary-credential refusal, pidfd process lifetime,
and three real PAM-library transactions. The remaining 59 tests exercise the policy model.
See [Linux verification](../../research/auth-poc/verification.json) for passed test identities,
class counts, platform, time and demo scenarios. The
[initial macOS report](../../research/auth-poc/verification-macos.json) remains archived.

The unprivileged PAM tests use `pam_start_confdir` to select a temporary private service
configuration with installed permit/deny fixture modules. They verify authentication
success, authentication refusal without proceeding, and account refusal after successful
authentication. No real user/password or shadow data is read, no secret is entered, no
system PAM configuration is edited and no account/session/password-management operation
runs. This is native PAM transaction coverage, **not** proof of Linux password validation,
real account expiry/lockout/MFA or the production privilege boundary.
[PAM private configuration API](https://man7.org/linux/man-pages/man3/pam_start_confdir.3.html).

The inherited-socket test confirms connection-time credentials do not necessarily name
the later sending process. The pidfd test proves a process handle becomes readable at
child exit; it does not prove the eventual polkit subject binding. Polkit authorization, account-enrollment generations and OS account-change monitoring
remain modeled or unimplemented. Sudo origin handling now has the constrained disposable
fixture coverage below; production deployment and descriptor binding remain unimplemented. Host/container isolation and UI integration still need
native end-to-end tests. No real persistent credentials were created.

The VM was already running and had Python, a C compiler, PAM and polkit installed.
Existing trusted access and its Linux/Parallels identity were verified. No installation,
reboot, service start or security/network change was needed. PAM/polkit development headers
are absent; ctypes can exercise the installed PAM runtime without adding packages.
Future native adapter build dependencies require a separate setup decision.

Both unpublished RoofController branch histories were transferred as an incremental Git
bundle above the already-present release base. The installer worktree had 761 tracked
files and the initial auth worktree 770; every file's SHA-256, type and executable bit
matched the M4 source. All four pre-existing VM worktrees, including one dirty source
file, were unchanged. M4 originals and their untracked build output were retained; build
binaries, baseline backups, keys and deployment credentials were not transferred.
Canonical branch names are `feat/kiosk-splash-install` and `research/linux-auth-poc` in
separate worktrees on that VM. Source development and validation continue there.

No production authentication, SSH policy, account, password/PIN, service or hardware
configuration changed, and the live Pi was not contacted. Research uses authoritative
documentation accessed on 2026-10-14; the migration does not remove the review gates above.


## Integrated installation check and production blocker

The [disposable integration](../../research/auth-poc/integration/README.md) extends this
same POC, not a separate authentication design. On the designated Linux VM it installed
its own package, four disposable Linux accounts, a fixed-command sudo rule and a private
PAM service inside a network-isolated container. **All 31 checks passed**. Native sudo
established the original caller UID; the root helper's kernel peer identity and the
controller role were checked separately. pam_unix accepted correct fixture inputs and
rejected wrong passwords and an expired account. Fixture HTTP requests exercised both
provider identities, roles, scoped/revoked keys, session reuse, wall-clock expiry,
logout, and role changes. Spoofed local caller/session fields were rejected.

The package, account/policy state and native Linux calls are real. The authorization
model, HTTP endpoints and predictable process-local credential handles remain prototype
code; no production controller, website or graphical kiosk is deployed by this test.
PAM runs in the same disposable root process, so this does not establish the proposed
privilege-separated login worker. Root remains trusted. It does not prove namespace,
polkit challenge, live account-change monitoring, lockout/MFA or production key security.

The production installer currently uses fixed system paths, system service/account
changes and shared Docker names. It offers no isolated-prefix complete system install.
It does not yet install the broker, website Linux provider or scoped-key ownership
migration; the production application does not implement them either. A complete
production installation with this new authentication architecture is therefore blocked
on those implementations and a disposable system installation environment. No host
account or policy modification was substituted for that missing integration.

Separately, **122 existing production authentication tests passed in Release**, covering
the sign-in service, credential validation, identity API, web sessions, Stop and CLI
identity commands. Those use the application's existing in-process/emulated fixtures,
including PIN/device/session semantics; they are not a graphical kiosk installation test.

The [verification record](../../research/auth-poc/integration/verification.json) separates
real and simulated coverage and records cleanup. The test container and temporary image
were removed; its runtime-only accounts/password hashes and policy files disappeared
with it. Host account files and fixture-policy absence were unchanged. Python and sudo
were installed only in the temporary image; no host packages, users, PAM/polkit/sudo
policy, listeners, existing application services or Pi state were changed.

The follow-up [full-install analysis](../../research/auth-poc/integration/full-install-gap.md)
identifies the existing test Root abstraction, why prefixing alone is insufficient,
container/guest options and exact production adapter changes. Another **557 isolated
installer regression tests passed** with host system-install categories excluded.
