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
- `InstallerWizardTests`, `InstallerWizardControllerTests` and `InstallerWizardClientTests` preserve existing field-fit,
  choice, CA trust, private-input, review/save, fake apply, progress, failure and recovery assertions.
  `InstallerDesignTests` adds both-size/all-mode conditional forms for controller+kiosk, rig and Mac+CLI profiles,
  focus revelation, invalid private input and distinct Back/cancellation behavior. Every command stays inside FakeMachine.
- Installer plan/lifecycle/backup/resource/state/command tests cover the common text surfaces for the catalogue's step
  classes. A shared renderer provides wrapping/navigation; class-specific check/apply behavior stays in the plan steps.

The focused candidate run and final validation counts are recorded in the PR. No hardware, real install/deploy,
service changes or Docker operations are used for this evidence.

## Reproduce captures

From `src/`, with the pinned installed SDK:

```bash
HVO_CLI_RENDERS_DIR=/tmp/hvo-176/roof HVO_INSTALLER_RENDERS_DIR=/tmp/hvo-176/installer \
  flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout --kill-after=20s 900s \
  dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests -c Release \
  --filter 'FullyQualifiedName~RoofTerminalUiTests|FullyQualifiedName~TerminalDesignTests|FullyQualifiedName~InstallerWizard|FullyQualifiedName~InstallerDesignTests'
```

The ANSI files are actual rendered cells, attached to the test results. Convert them with
`python3 tests/cli/ansi-to-svg.py INPUT.ans OUTPUT.svg --title 'hvo-roof'` from the repository root, under the same
bounded heavy gate. Use synthetic fixture identities and empty/masked private inputs. Documentation images are selected
from these captures; the complete matrix stays in CI artifacts. A prototype image is not runtime evidence.

## Exact distribution gate

The existing #172 parser, ZERO NativeBinary policy, embedded notices, final signatures, final-file hashes and installed
copy checks remain authoritative. `published-terminal.py` additionally runs both cell sizes in all four modes, all five
offline roof pages, a private connection prompt/F9, and installer roles/Back/cancel in isolated PTYs. It requires a
clean non-root host, private HOME/XDG/tmp and an empty PATH; it applies nothing and invokes no host program.

`check-terminal-workflows.py` parses the exact copied/signed final files, checks shared assembly byte identity, then
loads their owned managed assemblies into a private copy of the compiled driver/fake-machine test host. Required test
names must appear as passed in TRX; a zero-test run fails. This exercises the distributed implementation beyond the
entry page without adding a production simulation switch or installing on the runner. Native execution of each final
file and its installed-copy notice check remain separate checks. Hosted CI supplies Linux x64, Linux arm64 and macOS
arm64 execution; local macOS signing proof is unavailable because rcodesign is not installed, and no tool is installed
for this task. No local result is presented as hosted all-RID evidence.
