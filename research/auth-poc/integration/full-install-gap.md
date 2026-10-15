# What remains for a real disposable full installation

This is the result of inspecting the current installer and the designated Linux VM on
2026-10-14. No system installation, new VM, host policy change or Pi action was performed.
The disposable integrated prototype passed 31 checks. A further **557 installer regression
tests passed**, with zero failures/skips, excluding KioskInstall and MacInstall. The
122 existing application authentication tests also passed. These suites do not constitute
a complete installed-system test.

## Why a prefix flag alone is insufficient

[InstallerMachine](../../../src/HVO.RoofControllerV4.Installer/Machine/InstallerMachine.cs)
already has a Root/OnDisk abstraction for tests. Its Current factory always selects the
real root and ProcessCommandRunner. [Commands](../../../src/HVO.RoofControllerV4.Installer/Machine/Commands.cs)
are separate from filesystem redirection: changing Root does not redirect systemctl,
useradd, package managers, Docker or the deployment script.

[InstallPaths](../../../src/HVO.RoofControllerV4.Installer/Record/InstallPaths.cs) and the
controller/kiosk layouts use system paths. Survey, container steps and lifecycle steps
also share fixed controller/emulator/network names. Some deployment-script settings are
already configurable, but the installer must propagate a consistent isolated context
through survey, deploy, identity setup, upgrades, backups, rollback and cleanup.

A safe prefix enhancement would therefore need:

1. An explicit installation context: root, instance ID, data/secrets/log paths, Docker
   endpoint and resource names, ports, service names and rollback ownership.
2. A command executor that actually enters the isolated environment. Refuse host
   commands when a prefix is selected; never treat path translation as containment.
3. An allowlist for service/account/package actions. A staging-only executor may emit
   files and a manifest, but must report service startup and OS account policy untested.
4. Prefix containment and symlink-escape checks plus ownership-bound cleanup; prohibit
   privileged fallback, host Docker-socket mounting and shared default names.
5. Real execution tests in a separate service-manager environment after staging tests.

Exposing the existing test Root as a CLI option without these changes would be unsafe.

## Disposable-system options

| Option | Can it cover the real full installer? | Current boundary |
|---|---|---|
| Existing constrained Docker fixture | No; native accounts/PAM/sudo and application processes only | The cached image has systemd, but its private cgroup mount is read-only and no nested Docker daemon is installed. |
| Prefix/staging harness using existing Root | File/layout, idempotency and plan coverage; not real system services | Reuse current test abstractions, but do not label fake commands as a complete install. |
| Proper systemd container with a private nested Docker daemon | Potentially service/account/container installation, not Pi boot/DRM behavior | Needs a properly delegated writable cgroup subtree, container manager/mount setup and nested-daemon isolation. The current constraints do not provide these. Do not substitute privileged mode, unconfined host access, writable host cgroup root or the host Docker socket. |
| Fresh emulated guest inside an unprivileged Docker container | In principle, real systemd, package/account policy, private Docker and guest reboot without host policy changes | New harness and verified guest/release artifacts required. CPU emulation can avoid KVM; performance and this configuration are unverified. Outer runtime would have no host mounts/devices/ports and network none; guest traffic would stay within its outer container. |
| Separate disposable full VM/CI runner | Existing installer scenarios were designed for this | Requires a separately selected disposable target and its lifecycle/resources, not converting the canonical development VM into a disposable host. |

The VM currently has no systemd-nspawn, qemu-system-aarch64, qemu-img, rootlesskit,
slirp4netns or fuse-overlayfs; /dev/kvm is absent. An unprivileged user/network namespace
probe failed at writing uid_map. Docker uses cgroup v2 with its systemd driver,
AppArmor and seccomp. These are observed capabilities, not a reason to weaken host policy.

Systemd documents delegated cgroup ownership and container mount requirements in its
[container interface](https://systemd.io/CONTAINER_INTERFACE/).
Docker's [rootless prerequisites](https://docs.docker.com/engine/security/rootless/)
also mean that merely installing a rootless client is not a substitute for available
namespace mappings. Sources checked 2026-10-14. The emulated-guest option is an engineering
proposal, not a tested result or a claim that it needs a host security-policy change.

The repository's [rig scenario](../../../tests/installer/rig-scenario.sh) is not safe to
run as-is on the canonical VM: it writes system directories, creates shared Docker
resources and exercises a dummy network interface. Its cleanup does not make those
host changes authorized. [KioskInstall](../../../tests/HVO.RoofControllerV4.RPi.Tests/Installer/InstallerKioskSystemTests.cs)
likewise performs real package/user/systemd changes and is deliberately excluded.

## Production adapter wiring required regardless of environment

| Component | Concrete missing work |
|---|---|
| Identity persistence | Version the schema in RoofIdentityDocument/RoofIdentityStore; add immutable provider-qualified principal IDs, Linux enrollment/generation and key owner/scopes/expiry; migrate copies without merging same-name users or changing existing password/PIN hashes. Preserve legacy kiosk/device permissions and last-admin protection. |
| Native local authorization | Implement a packaged broker plus PAM worker or explicitly selected constrained sudo helper. Authenticate the original caller and operation; require Linux policy and controller role separately. Define polkit subject binding or helper handoff, namespace handling and account-change revalidation. |
| API and sessions | Extend AuthController/RoofSignInService and shared contracts with explicit provider selection. Revalidate principal/role/epoch for requests and hub sessions. Add owner/scope enforcement to key issuance, revocation and validation. No client-supplied UID or provider assertion may authenticate a caller. |
| Website | Wire provider selection through SignIn.razor, WebAccountEndpoints and the shared client to the controller. Keep antiforgery, rate limiting, TLS, safe returns and server-side session handling. Do not call PAM directly from the web UI. |
| CLI/terminal | Add explicit local/remote/API modes. Local mode must use the native broker and never fall through to saved web credentials. Batch mode must not prompt. Preserve the existing remote sign-in path. |
| Installer/rollback | Package adapters and versions, explicit enrollment/admin-policy choices, socket/service ownership, PAM/policy files and private state. Add idempotency, migration, backup/rollback and uninstall ownership rules behind a disabled-by-default feature. |
| End-to-end acceptance | Run real installer twice, then real CLI/API/browser logins as Linux admin/nonadmin and virtual roles; assert spoof denial, scoped/revoked keys, account/session expiry and logout, unchanged PIN/device/Stop semantics, reboot persistence and rollback. Keep all identities disposable and emulator-only. |

These are production behavior changes, not missing test switches. The current Python
fixture cannot establish them by receiving more mocked inputs. Choices still needing
a bounded implementation decision are the supported local authorization backend, explicit
Linux enrollment/bootstrap behavior and legacy-key migration. The current deliverable
preserves the prototype and records its evidence; it does not silently select those
production security policies or alter the working installation.
