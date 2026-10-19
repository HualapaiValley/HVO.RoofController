# Validation and evidence

## Foundation evidence

This foundation inspected source at `2e4f1fbfac1216d508fc47913179e3c9b0a4cfb3`, full #171 and #158 body/comments,
root guidance and PR template. It fetched/read all four approved prototype sources and both final merged Sky primary
files through `gh api` (no local Sky checkout). [Token data](tokens.json) records byte hashes. The matrix traces existing
surfaces to source strings and deliberately labels all production redesign work pending.

Local foundation checks on 2026-10-19 passed: 187 surface rows, 11 Razor routes, 48 concrete installer plan-step
outputs and all 20 pinned semantic roles validated; repository documentation links had zero broken targets; all 17
`tests/docs` tests passed; staged diff whitespace checks passed. These are documentation/source checks, not UI renders.

Reproduce the inexpensive structural/source checks from the repository root:

```bash
python3 docs/ui-redesign/validate.py
python3 build/check-doc-links.py
python3 -m unittest discover -s tests/docs -p 'test_*.py'
git diff --check
```

To verify the tokens against primary source bytes, fetch the pinned files (not the current branch):

```bash
mkdir -p /tmp/hvo-171
# Every gh command names its repository explicitly through the API path.
gh api 'repos/HualapaiValley/HVO.SkyMonitor/contents/src/HVO.SkyMonitor.TerminalUi/README.md?ref=bf5ef211fa66e76dd83ac171a3fea944b6d5dc26' \
  --jq .content | base64 -d > /tmp/hvo-171/sky-terminal-readme.md
gh api 'repos/HualapaiValley/HVO.SkyMonitor/contents/src/HVO.SkyMonitor.CameraAgent/wwwroot/css/themes/hvo-dark.css?ref=bf5ef211fa66e76dd83ac171a3fea944b6d5dc26' \
  --jq .content | base64 -d > /tmp/hvo-171/sky-hvo-dark.css
python3 docs/ui-redesign/validate.py \
  --sky-readme /tmp/hvo-171/sky-terminal-readme.md --sky-css /tmp/hvo-171/sky-hvo-dark.css
```

The validator checks data shape, unique IDs, all current Razor routes/native pages/five TUI pages/installer page
classes and concrete installer plan-step outputs represented, each source anchor, all twenty roles and matching pairs, exact fetched primary table/hash and
CSS composition. It does not prove exhaustive semantic coverage or runtime behavior; review nested workflows against
the matrix and test definitions. An existing test named below is a **validation starting point**, never a passing
result from this foundation. The supplied read-only published-payload audit is summarized separately in [payload-audit.md](payload-audit.md);
it does not establish runtime success after exclusions. No .NET build/test, fresh render/browser check, Docker,
service/deploy or hardware work
is required for these docs/data. No screenshots were generated or claimed.

## Platform acceptance evidence

For each row supply the implementing commit/PR, test name(s), role/credential kind, state combinations, size/mode,
and screenshot/capture path or CI artifact. A single normal admin dashboard screenshot does not cover denied PIN,
expired forms or nested prompts. Screenshots use synthetic identities/private stores and the emulated HAT/roof/VFD;
never show credentials or private addresses. Record narrow gaps honestly rather than marking an entire family verified.

For every Web, native, TUI and plain-CLI Stop adapter, preserve the shared classification/message for both
`RoofStopOutcome.Acknowledged` cases: `Verified` register evidence uses `RoofStopText.AcknowledgedVerified`, while
missing/`Unknown` verification uses generic `RoofStopText.Acknowledged`, including successful HTTP 200 with an
unreadable body. The enum alone does not prove register verification; read-back never proves physical contacts.
The existing [RoofStopTests](../../tests/HVO.RoofControllerV4.RPi.Tests/Client/RoofStopTests.cs) definitions
`AnAcceptedStop_ClaimsOnlyWhatTheRelayRegisterShowed` and
`AnAcceptedStopWhoseAnswerCannotBeRead_IsAcknowledged_WithoutARelayClaim` provide the source-traced regression cases.
These definitions were inspected, not executed for this documentation correction. Exercise both message cases in
each adapter's applicable states and colour modes alongside Sent, RelayUnverified and Failed, without changing the enum.

| Profile | Existing source/test workflow to extend | Required new evidence |
| --- | --- | --- |
| Web | [Web tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Web), [Browser tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Browser), [refresh guide](../web.md#tests) | bUnit form/state and API/client-call assertions, Playwright screenshots/keyboard/focus/touch at 320px+ including long content; each route, admin tab/editor and confirmation; camera controls/unsupported operations and fullscreen Stop path; reconnect popup Stop and no-JS POST. |
| Native | [Kiosk tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Kiosk), [commissioning UI tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Kiosk/KioskCommissioningFlowTests.cs), [kiosk guide](../kiosk.md), [Mac guide](../mac.md) | Avalonia headless layout/render/input assertions at 1920x1200 provisional, 1280x720 and 800x480 with configured density/rotation. No kiosk page scroll/critical clipping even with multiple warnings, many names, PIN/Choose/Confirm and questions. Preserve 12mm targets; Mac resize/Tab/Return/Esc/refusal. PIN Admin remains Operator-only and no settings navigation. |
| Terminal | [RoofTerminalUiTests](../../tests/HVO.RoofControllerV4.RPi.Tests/Cli/RoofTerminalUiTests.cs), [terminal smoke](../../tests/cli/terminal-smoke.sh), [dotnet-run smoke](../../tests/cli/dotnet-run-smoke.sh), [ANSI render converter](../../tests/cli/ansi-to-svg.py), [CLI guide](../cli.md) | Terminal.Gui driver/tmux captures for every page/prompt at 80x24 and 120x32 in truecolour, ANSI16, NO_COLOR and ASCII. Test focus, choices, long/grapheme text, F1-F5/F9-over-prompts/F10/Esc, owned-motion quit/lost-answer Stop, notice pair fallback readability and fixed red focused Stop. Real published CLI smoke is separately required. |
| Installer | [Wizard tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Installer/InstallerWizardTests.cs), [controller cases](../../tests/HVO.RoofControllerV4.RPi.Tests/Installer/InstallerWizardControllerTests.cs), [client cases](../../tests/HVO.RoofControllerV4.RPi.Tests/Installer/InstallerWizardClientTests.cs), [install guide](../install.md#refreshing-the-screenshots) | Every applicable step across role/OS choices, nested consent/private inputs, blank/invalid/busy/denied/nonTTY/failure/recovery. Driver captures plus parsed-payload and published-executable smoke under fake-machine harness. No installer roof-motion or live-host apply. |
| Shared | [Palette tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Client/RoofUiPaletteTests.cs), [Stop text](../../src/HVO.RoofControllerV4.Client/RoofStopText.cs), [command state](../../src/HVO.RoofControllerV4.Client/RoofCommandRules.cs) | Adapter parity to token file/CSS including raised surface and every ANSI role; actual text/background contrast and focus/disabled/selected states; Sent, both Acknowledged message cases (verified register vs verification unavailable), RelayUnverified and Failed text in each fallback, retaining shared classification/messages. Changes to safety behavior need their own emulated scenario/commissioning update, outside this foundation. |
| Removal / payload | [Delivery checklist](delivery.md) | Debug/Release and affected publish/image/CI asset checks after RCL removal; actual CLI/installer bundle+notice closure for every release RID; only proven-unused highlighting excluded. |

Useful existing test distinctions include WebStopBrowserTests, WebSessionBrowserTests, WebStaleStatusBrowserTests,
WebCameraBrowserTests, WebRolesBrowserTests and C9WebLeaseBrowserTests; native KioskRenderTests, KioskConsoleTests,
KioskPanelTests, MacScreenTests and MacConsoleTests. Extend meaningful coverage instead of adding tests that merely
mirror styling constants. Reproduce documentation images using each platform's existing guide/test capture workflow.
A prototype's HTML interactions never count as a production regression test.

## Completion gate for #158

Every current/new matrix row must be verified with durable evidence; theme-removal and terminal-payload obligations
must be met; documentation/screenshots and user-visible CHANGELOG entries must match shipped behavior. Reconcile
functional dependencies without marking #153/#76/#89/#32 implemented by a visual PR. Review full status, stale/IO/latch,
authentication and optional lease semantics against the current controller/client. No real roof motion, deployment,
release/tag publication or hardware-dependent check is part of this validation plan.
