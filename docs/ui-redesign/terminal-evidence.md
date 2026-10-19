# Terminal and installer redesign evidence (#176)

This delivery owns T/C/I rows and the 48 plan-output surfaces in the [inventory](inventory.csv) and
[installer step catalogue](installer-steps.csv). #158 remains the family completion issue; #96, #153 and #76
retain their independent functional contracts. The source pins and exact semantic roles remain in
[the design contract](design-contract.md) and [tokens](tokens.json).

## Implementation

Both real applications consume `RoofUiTheme`, `TerminalCapabilities`, `TerminalCells`, `TerminalForm`,
`TerminalReadingView`, `TerminalTextView` and `TerminalButton`. The roof shell reserves content-measured rows for
critical status and the newest Stop outcome before laying out the page or a prompt. Its five pages use titled work
panels. All existing editors, private inputs, provider choices, confirmations and quit paths keep their handlers.
Exceptionally long Stop details use a bounded scrollable result area; a new result starts at its first line.
The installer uses the same form/reading controls and keeps Back, cancellation and validation outside the work area.
Plan, lifecycle, backup/restore, resource, state-contract and bootstrap behavior is unchanged; plain output gains cell
alignment and optional emphasis only on real interactive consoles.

RGB values come from Client's palette, owned by #175. ANSI16 mapping uses the exact names in the pinned contract;
`Force16Colors` prevents extended-colour escapes. Terminal defaults remain `Color.None`. ASCII frames, bracketed
actions and choice/toggle cues are rendered explicitly; relying on RGB quantization or replacing only the outer border
would not satisfy these modes. Field context menus also use the shared scheme.

## Controlled workflow coverage

- `RoofTerminalUiTests` drives the real API with a mocked plant: every page, F9 over prompts, both actual Stop
  acknowledgement messages, refusal/unverified/lost answers, stale status, owned-motion quit and leases, settings and
  hand edits, people/managed keys/sessions/Linux access, provider sign-in and connection/trust/private-input paths.
- `TerminalDesignTests` checks capability precedence and whole-grapheme geometry, then draws all five pages and long
  eight-field private prompts at 80×24 and 120×32 in RGB, ANSI16, terminal defaults and ASCII. It checks the newest
  Stop result remains visible, private values remain masked, and focused Stop retains Red in ANSI16.
  Its actual workflow catalogue also reruns the existing provider/sign-in, first-admin, connection/trust, people,
  password/role/Linux access, settings/safety/private input, restart/confirmation and quit/lost-answer assertions at
  every mode and size. Separate catalogue cases open PIN/removal/key-change/rotation/session-end and nested pending-edit
  review/discard prompts and send F9 above each. The one-time generated API-key reveal is drawn and asserted but never
  saved to artifacts.
- `InstallerWizardTests`, `InstallerWizardControllerTests` and `InstallerWizardClientTests` preserve existing field-fit,
  choice, CA trust, private-input, review/save, fake apply, progress, failure and recovery assertions.
  `InstallerDesignTests` adds both-size/all-mode conditional forms for controller+kiosk, rig and Mac+CLI profiles,
  focus revelation, invalid private input and distinct Back/cancellation behavior. Every command stays inside FakeMachine.
- Installer plan/lifecycle/backup/resource/state/command tests cover the common text surfaces for the catalogue's step
  classes. A shared renderer provides wrapping/navigation; class-specific check/apply behavior stays in the plan steps.

The focused candidate run and final validation counts are recorded in the PR. No hardware, real install/deploy,
service changes or Docker operations are used for this evidence.

The retired-client production guard and test-host bootstrap solve different problems. The controlled
`Reconnect_RetiredOrdinaryReadCannotReplaceTheNewConnectionMessage` regression releases a failed ordinary read only
after reconnect and checks that the new connection message survives. Motion failures and lost answers remain on their
existing path. Separately, `CliRig(host)` creates its lazy WebApplicationFactory server on the test thread before
feed/caller/provider reads begin; this prevents competing fixture startups. Neither change is the separate #179
numeric-fixture/session-ID correction.

## Reproduce captures

From `src/`, with the pinned installed SDK:

```bash
HVO_CLI_RENDERS_DIR=/tmp/hvo-176/roof HVO_INSTALLER_RENDERS_DIR=/tmp/hvo-176/installer \
  flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout --kill-after=20s 900s \
  dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests -c Release \
  --filter 'FullyQualifiedName~RoofTerminalUiTests|FullyQualifiedName~TerminalDesignTests|FullyQualifiedName~InstallerWizard|FullyQualifiedName~InstallerDesignTests'
```

The ANSI files and accompanying `.cells.json` are actual rendered cells, attached to test results. The JSON keeps
whole graphemes and the driver's cell widths rather than estimating emoji from individual code points. Then run the
controlled emulator tmux smoke described in [cli.md](../cli.md#tests), with its output at `/tmp/hvo-176/tmux`.
From the repository root, convert/select the complete documentation set:

```bash
flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout --kill-after=20s 180s \
  python3 tests/cli/refresh-captures.py --roof /tmp/hvo-176/roof --installer /tmp/hvo-176/installer \
  --tmux /tmp/hvo-176/tmux --evidence /tmp/hvo-176/renders --docs
```

The helper masks the local host name without changing cell positions, emits light-default NO_COLOR comparisons,
and refuses an incomplete documentation set. Synthetic fixture identities and empty/masked private inputs are used.
The complete mode/size matrix stays in CI artifacts. A prototype image is not runtime evidence.

## Exact distribution gate

The existing #172 parser, ZERO NativeBinary policy, embedded notices, final signatures, final-file hashes and installed
copy checks remain authoritative. `published-terminal.py` additionally runs both cell sizes in all four modes, all five
offline roof pages, a private connection prompt/F9, and installer roles/Back/cancel in isolated PTYs. It requires a
clean non-root host and private HOME/XDG/tmp. Its PATH contains only the reviewed inert bash stub, which returns
127 without executing clipboard probes or any other arguments; host survey commands remain unavailable.

`check-terminal-workflows.py` parses the exact copied/signed final files, then loads each program's exact owned managed assemblies into its own private copy of the compiled
driver/fake-machine test host and verifies every staged byte. ReadyToRun dependencies can differ between publishes;
the receipt records each program's assembly hashes separately. The independent test host explicitly disables ReadyToRun and JITs the exact bundled IL;
this is managed workflow evidence, not proof that every branch executed its published native ReadyToRun image.
The exact final executables run normally in the separate PTY and controlled emulator/tmux gates. Required test
names must appear as passed in TRX; a zero-test run fails. Driver groups run with private HOME/XDG/tmp and the
same reviewed inert PATH, with the installed dotnet host resolved to an absolute path before restriction. The
complementary non-render groups retain their established environment: the actual command-environment and absolute
`/usr/bin/openssl` chain contracts must pass there. Disjoint filters retain the original catalogue, and separate
TRX/logs and per-phase PATH policies/counts accompany each program's identity receipt. This exercises the distributed implementation beyond the
entry page without adding a production simulation switch or installing on the runner. Native execution of each final
file and its installed-copy notice check remain separate checks. Hosted CI supplies Linux x64, Linux arm64 and macOS
arm64 execution; local macOS signing proof is unavailable because rcodesign is not installed, and no tool is installed
for this task. No local result is presented as hosted all-RID evidence.


## Local validation and remaining gates

The [Linux x64 receipt](terminal-local-validation.json) records separate runtime sources: CLI `981f2ea` and installer `647a6a8`, with exact final
copied program/staged assembly hashes. Both copied programs match their completed publish sources byte-for-byte and
remain read-only. Renewed native offline PTYs pass all four modes at both sizes. The unchanged CLI `981f2ea` controlled emulator/tmux
checks pass the live five-page/private-prompt/F9 matrix, plus terminal loss and SIGTERM Stop/exit assertions.
The complementary exact bundled-IL/JIT catalogue passes **510 CLI and 941 installer cases**, including actual
installation/rollback file-copy notice delivery on FakeMachine. Both corrected owned suites pass **576/576 in Debug
and 576/576 in Release**; Release includes full default Next brackets and hit bounds. The new 28-case F1/F2/F3/ASCII
matrix proves actual error tails, focus, action rendering/hit areas, F9 and distinct installer Back/cancellation.
The final installer caption-only correction passes **20/20 focused Release cases**, including complete trust-choice
labels in every mode and both sizes; its exact bundled-IL/JIT and native PTY checks are renewed separately.
ASCII masks/arrows/tracks use Terminal.Gui's immutable glyph settings; its read-only facade has no writable properties.

Fake bootstrap **149**, Python CLI **13**, releasing **123** and docs **17** pass. Prior full solution Debug/Release
builds have zero warnings/errors, and the corrected Release test build also treats warnings as errors. All 26
operator documentation SVGs are renewed from the corrected actual captures; the full mode/size catalogue includes
light-default comparisons. Reproduction commands above apply to the current implementation.

The prior-candidate required Release suite passes **3,843 with four opt-in skips**. Prior required Debug has **3,842
passes, four opt-in skips and one stale bUnit event-handler failure** in `PeoplePageTests.cs:41`. Its Web runtime/test
trees are identical to canonical main `e5f920a`; root identified the existing renderer-safe helper correction in #174.
No Web assertion or file is changed here. The failed TRX/classification is retained separately. Take #174 through
reviewed MAIN and run final required validation before merge. Three opt-in host suites were not run locally; the final
installed-copy notice case skipped in the general suite separately passes in the exact distribution catalogue.

Reviewed payload MAIN `e14f479` is merged and its foreign dependency diff is removed. Production source trees are
unchanged by this integration; the CLI `981f2ea` and installer `647a6a8` distributions, native PTY/motion results
and documentation captures remain their existing exact evidence. The scoped managed PATH gate renews harness
evidence against those immutable copies, without republishing or repeating motion/capture work. CLI contracts
**371** and inert-PATH drivers **139** pass; installer contracts **857** and inert-PATH drivers **84** pass. The
original totals remain **510/941**, with unchanged distributed assembly hashes. The existing environment-inheritance
and absolute OpenSSL chain assertions pass in the contract phase. Three focused PATH regressions also prove actual
absolute-host subprocess startup, inert arguments/no file writes, and preservation of the contract environment.

Hosted supported-RID/signature checks, #174 through reviewed MAIN and final required validation remain merge gates.
Statuses stay `implemented-unverified` until those gates/review finish. Earlier invalid attempts (old destination/
ETXTBSY, cross-program R2R equality, independent-host R2R discovery abort) stay separate under `/tmp/hvo-176`; none is
counted as a runtime pass. Final corrected checks use immutable `dist-trust-final`: the CLI retains its validated `981f2ea` bytes and the
installer uses its newly completed `647a6a8` publish. Each program's source/copy hashes and managed/native evidence
remain separate in the receipt; the prior immutable distributions and failed attempts remain intact.


## Focused Admin settings catalogue acceptance

`AdminSettingsCatalogue_TraversesEveryGroupAndType_ThroughActualControls` closes the independent actual-TUI evidence
gap for the catalogue form kinds. It compares the independent real Admin API catalogue/settings with every group and
key in the actual `RoofUiSettingsPage`, then navigates the real group/field lists by keyboard. All **7 groups and 42
fields** are visited in truecolour, ANSI16, NO_COLOR and ASCII at both 80×24 and 120×32. The current catalogue publishes
Boolean, Integer, String, Duration and Enum; it publishes no Number setting, so this proof adds no synthetic one.

Every writable editor is opened and cancelled with its actual initial value and Tab/Enter actions; read-only states
refuse an editor, secret controls stay blank/masked, nullable states are included, and a long/wide value's tail remains
readable. Prompt bounds/hit areas and permanent Stop are checked, including actual F9 over an editor in every group.
Invalid boolean and enum input are rejected. A boolean safety review is cancelled with the API version unchanged;
a non-safety logging enum is saved through the real API and read back, with all other values/IsSet unchanged.
The pre-existing nullable-duration and multipart-secret save/clear workflows retain all their assertions.

The [focused receipt](terminal-settings-catalogue-validation.json) records **8/8 Release source cases** and **8/8 exact
retained CLI bundled-IL/JIT cases**. The rebuilt private test host uses the existing final-bundle extraction/staging and
`run_phase` helpers, explicit ReadyToRun=0, absolute dotnet and the reviewed inert PATH. Its five staged member hashes
match the retained CLI `981f2ea` distribution (`c52aaa3a…fa42`), with no republish or native replay. Local raw proof is
under `/tmp/hvo-176/settings-catalogue-{source-final,bundle}`; the normal exact-bundle CI catalogue now requires this
method too. Independent API reads run off the GUI context because the in-process test handler captures it; initial
cancelled attempts remain private and are not counted as passes. No production files or matrix statuses change.
Shared S rows remain root-owned; #183 through reviewed MAIN and final required/hosted checks remain pending.

The narrow source reproduction, from `src/`, is:

```bash
flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout --kill-after=20s 600s \
  dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests -c Release \
  --filter FullyQualifiedName~AdminSettingsCatalogue_TraversesEveryGroupAndType_ThroughActualControls
```

## Hosted CI job budget cancellation

At `184de4f`, [run 39201562307, job 117895607620](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39201562307/job/117895607620)
was cancelled at **20m23s** with the annotation “The job has exceeded the maximum execution time of 20m0s”.
The main test step passed **3,879 tests, four skips and zero failures in 302s**; the focused umask test also passed.
Debug/Release builds, every CLI/kiosk/installer/Mac/native-auth publish, and the pre-upload terminal assembly,
native-policy and notice checks succeeded. Cancellation occurred during `Upload hvo-roof-install`, which began
at 19m58s; later normal uploads were skipped. This is cumulative job-budget evidence, not a functional test failure
or a completed-run/platform-gate pass.

Only `build-and-test.timeout-minutes` changes from **20 to 30** to accommodate the serial supported-RID publishes
and expanded real workflow suite. Every check and every other job/step timeout, signature, native-policy, notice
and byte check remains intact. The correction uses YAML/diff and existing CI-signature checks; it repeats no build,
product suite, publish, motion or image work, and requests no manual CI rerun. #183 and #185/#186 through reviewed
MAIN, final required Debug/Release suites and current-head hosted gates remain prerequisites.
