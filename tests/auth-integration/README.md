# Real Linux authentication acceptance

These scripts exercise installed release artifacts against real PAM, sudo, systemd,
Docker, TLS, the controller API and SignalR. Run them only inside the marked
[disposable guest](../installer/disposable-guest/README.md). They are destructive to
that guest's fixture accounts and application state. Never run them on a development
host, a Raspberry Pi, or an existing controller.

The guest guard requires root, the test marker and QEMU/KVM virtualization, and
rejects the physical GPIO device. Only the emulated HAT is used. The acceptance
suite sends Stop; it does not send roof Open or Close.

## Preparation and sequence

Build the Release ARM64 binaries on the designated Linux development host.
Use `assemble-release.py --help` to create complete, hashed, private release
bundles. Stage a genuine schema-1 legacy release at
`/opt/hvo-auth-test/release-legacy` and schema-2 releases
`4.0.2-auth.1` and `4.0.2-auth.2` at `release-a` and `release-b`.
These fixed versions are fixture expectations, not published versions.
Import image archives into the guest's own Docker daemon and registry with
`prepare_registry.py`; no host Docker socket or external registry is used at runtime.

Copy this directory to `/opt/hvo-auth-test/tests`. Execute phases serially through
`guestctl.py exec`, waiting for each to finish:

1. `run_acceptance.py prepare` provisions inert, guest-only accounts and private
   fixture input files. Run this only before installation, never to repair a
   persistence test. It sets a five-second PIN idle timeout and disables fixture
   sign-in throttling; production PAM, native startup and controller authentication
   deadlines remain unchanged.
2. `run_installation.py legacy`, `enable --kiosk`, `rerun`, then
   `reject-legacy` verify migration, repeat installation and safe incompatibility refusal.
   `verify-enabled --kiosk` is an observational follow-up after a completed install.
3. `run_acceptance.py acceptance`, `run_clients.py --release /opt/hvo-auth-test/release-a`,
   and `check_hub.py` exercise the API, installed CLI and live hub.
4. Run `run_browser.py --output <new-private-directory>` in the outer container
   with Playwright Chromium and the documented guest-loopback SSH tunnel. It waits
   for actual Blazor event bindings before interacting with prerendered forms.
5. Reboot the existing guest using `guestctl.py reboot`, wait for services, then
   run `run_acceptance.py reboot`.
6. Run `run_installation.py upgrade`, `rollback`, and `rollback-again`, then
   `run_acceptance.py rollback`. This is application rollback, not restoration
   of an identity backup or guest snapshot.

The schema-capability and emulator-metadata helpers repair only explicit fixture
image metadata. They verify unchanged RootFS layers; they do not weaken production
ownership checks or substitute a new application binary.

## Evidence and limitations

Reports are written under `/var/lib/hvo-auth-acceptance` with private permissions.
Export only individually reviewed JSON result files. Never export `state.json`,
identity stores, account files, credentials, installer answers, raw logs, SSH trust
material, disks or snapshots. A runner exception before report creation is a failed
or incomplete run, not a pass.

Browser captures occur with empty sign-in fields or after the one-time key panel
has closed. Visually inspect every capture before sharing. The separate kiosk
capture helper records the actual virtual framebuffer without controlling the roof.

Software-rendered Avalonia under full-system TCG emulation can cause severe CPU
pressure. Preserve failed full-stack runs. If the virtual kiosk is temporarily
stopped to isolate authentication or lifecycle checks, record that explicitly,
restore it afterward, and keep all production deadlines unchanged. Passing with
the kiosk stopped does not establish simultaneous full-stack acceptance.

Keep original failure receipts when correcting fixture assertions. Report a
targeted follow-up separately from a complete rerun. After exporting sanitized
evidence, use the harness's guarded cleanup and verify host account/PAM/sudo hashes
are unchanged.

## Optional timeout diagnostics

The diagnostic helpers are test-only. Run them only in the marked disposable
guest; do not install instrumented binaries on a controller that drives hardware.

`build_diagnostics.py --base <saved-commit> --output <new-private-directory>`
archives the saved commit into a new source directory, adds allowlisted timing
records, and publishes ARM64 Release copies. It does not edit production sources
in the worktree or change the 5-second PAM wait, 2-second reap, 8-second controller
HTTP timeout, or 15-second worker service startup limit.

Match the installed controller's packaging. For a framework-dependent controller,
republish the generated controller project with `--self-contained false -p:PublishSingleFile=false`
and the same version/revision, then replace only its controller assembly in a
derived test image. Verify the base image, inherited configuration, added layer
and installed assembly hash. Preserve the original release for recovery.

`run_diagnostics.py` accepts an exact guest run ID, phase, kiosk state and
count. The comparison is kiosk off → on → off, with at least 65 seconds of settling
per phase and one to three identical correct-password API requests. Check the
separate controller account-lockout state and effective configuration before choosing
the count: the default five-failure threshold, five-minute lockout and 24-hour
failure memory are independent of the native worker's one-minute window and API
rate limiting. Never reset failures to make a comparison pass. It reads
`passwd -S` and `chage -l` status without reading password hashes. This
precheck establishes an unlocked password and non-expired account, not every
possible PAM eligibility condition.

The slim native worker may omit distributed trace IDs. The runner retains
`trace=none` and uses explicitly inferred exclusive serial correlation:
record multisets, one API attempt with a password transport and an optional
sequential account revalidation on the same controller trace, stable native worker
PID, matched child PID/completion counts, and a quiet settling interval. Extra requests, dropped records or incomplete drains make the result
inconclusive. A child completion count of zero supplies no child-stage evidence;
it cannot establish that PAM did not run. Throttle rejection must be distinguished
from PAM denial, child timeout and controller transport timeout.

Reports contain allowlisted events, numeric timing/resource fields and response
status/token-presence only. Never publish raw logs, credentials, tokens, identity
files, guest disks or unreviewed screenshots. Keep the outer-container isolation
and resource-profile proof with results, capture the actual virtual framebuffer
when investigating display behavior, and clean up only the owned test resources.

Instrumentation and log/resource collection add overhead under software CPU
emulation; stderr draining/nonblocking diagnostic writes also differ from the
uninstrumented worker. These trials diagnose observed sequences, not production
latency or physical Raspberry Pi performance. Do not change production deadlines
to make an emulated test pass.
