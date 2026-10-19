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
clean non-root host, private HOME/XDG/tmp and an empty PATH; it applies nothing and invokes no host program.

`check-terminal-workflows.py` parses the exact copied/signed final files, then loads each program's exact owned managed assemblies into its own private copy of the compiled
driver/fake-machine test host and verifies every staged byte. ReadyToRun dependencies can differ between publishes;
the receipt records each program's assembly hashes separately. The independent test host explicitly disables ReadyToRun and JITs the exact bundled IL;
this is managed workflow evidence, not proof that every branch executed its published native ReadyToRun image.
The exact final executables run normally in the separate PTY and controlled emulator/tmux gates. Required test
names must appear as passed in TRX; a zero-test run fails. This exercises the distributed implementation beyond the
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

Fake bootstrap **149**, Python CLI **13**, releasing **119** and docs **17** pass. Prior full solution Debug/Release
builds have zero warnings/errors, and the corrected Release test build also treats warnings as errors. All 26
operator documentation SVGs are renewed from the corrected actual captures; the full mode/size catalogue includes
light-default comparisons. Reproduction commands above apply to the current implementation.

The prior-candidate required Release suite passes **3,843 with four opt-in skips**. Prior required Debug has **3,842
passes, four opt-in skips and one stale bUnit event-handler failure** in `PeoplePageTests.cs:41`. Its Web runtime/test
trees are identical to canonical main `e5f920a`; root identified the existing renderer-safe helper correction in #174.
No Web assertion or file is changed here. The failed TRX/classification is retained separately. Take #174/#177 through
reviewed MAIN and run final required validation before merge. Three opt-in host suites were not run locally; the final
installed-copy notice case skipped in the general suite separately passes in the exact distribution catalogue.

Hosted supported-RID/signature checks, reviewed MAIN integration and the #177 inert macOS clipboard-probe PATH helper
remain merge gates. Scope its inert PATH to actual render phases; keep meaningful non-render env/openssl contracts.
Statuses stay `implemented-unverified` until those gates/review finish. Earlier invalid attempts (old destination/
ETXTBSY, cross-program R2R equality, independent-host R2R discovery abort) stay separate under `/tmp/hvo-176`; none is
counted as a runtime pass. Final corrected checks use immutable `dist-trust-final`: the CLI retains its validated `981f2ea` bytes and the
installer uses its newly completed `647a6a8` publish. Each program's source/copy hashes and managed/native evidence
remain separate in the receipt; the prior immutable distributions and failed attempts remain intact.
