# Native redesign evidence (#175)

This slice implements N01–N31 and the Client/native portion of S01 for #158, from the approved native prototype at
`9cc8f2bbc677f41a9e30217a9881ded0c47860a9` and the exact reviewed foundation `984dbdc13b5a22546e595f2a756a13e2e76a39ce`.
Sky primary README/CSS were fetched through canonical `gh api` at `bf5ef211fa66e76dd83ac171a3fea944b6d5dc26`;
`validate.py --sky-readme … --sky-css …` verifies their exact hashes and all 20 roles. No source is taken from a local
Sky checkout. The [token data](tokens.json) retains those source hashes and ANSI names.

## Implemented behavior

The shell has a full-width controller/session header, separate position and motion panels, permanent red Stop and
bottom navigation. Larger layouts add a compact readings grid; all sizes have bounded Readings/Notices/About documents.
`KioskPager` measures full-size controls/text into Previous/Next pages, splitting long paragraphs without dropping
words. It keeps the current page over status updates. Only the current page participates in keyboard navigation.
The safety summary keeps unavailable/stale/fault/mode/I/O states visible while full warnings remain in Readings.

People selection and private PIN entry are separate bounded steps. Errors/empty lists offer explicit recovery; setup
Choose/Confirm stays locked and requires a fresh personal-PIN login. Every PIN remains Operator-only, including an
Admin PIN. No kiosk Settings/System navigation or #153 host/terminal privilege handoff is added. Password desktop
sessions retain their catalogue/role rights, scrolling administration, Return/Escape editing and masked password clearing.
Startup refusal keeps Quit/Return/Escape outside the scrolling recovery document. Session/lease cleanup is unchanged.

Stop remains enabled and direct REST outside every editor, question, error and PIN page. Its exact red pair survives
pointer/focus/press. The shared outcome **and actual message** are retained: both Acknowledged messages remain distinct;
unknown/missing verification never becomes a verified-register claim. Long results have a bounded Full result document.
Read-back proves the register, never contacts. Blank/wake input consumption retains the production race handling.

`RoofUiPalette` adopts all 20 pinned values and adds consumed `SurfaceRaised`. `Badge`, `MutedWeak` and `FocusRing` are
compatible raised/muted/accent aliases. Open/Close use raised/text roles; Stop uses danger-background/danger-text.
Native notice bands keep canonical pairs together; standalone tones use standalone status roles. Existing terminal RGB
Stop/results and images are migrated without dependency/notice packaging edits. Explicit ANSI-16/capability/layout
acceptance remains #176; Web parity/removal remains #174. S01 cannot be marked globally complete from these native tests.

## Reproducible local checks

All .NET commands run from `src/` with the pinned installed SDK. Heavy commands use the shared lock and a bounded timeout:

```bash
flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout -k 30s 900s bash -c 'cd src && dotnet build HVO.RoofController.sln -c Release'
flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout -k 30s 900s bash -c 'cd src && HVO_KIOSK_RENDERS_DIR=/tmp/hvo-175/renders dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests -c Release --filter "(FullyQualifiedName~.Kiosk.|FullyQualifiedName~RoofUiPaletteTests|FullyQualifiedName~RoofStopTests|FullyQualifiedName~RoofTerminalUiTests)&TestCategory!=Scenario&TestCategory!=Browser&TestCategory!=Soak&TestCategory!=KioskSoak"'
```

`KioskRedesignTests` renders every bounded page with simultaneous long warnings/notices at 1920×1200 (configured
8.2 and 9.0 pixels/mm), 1280×720 (8.2), and 800×480 (5.2), plus people/empty/offline recovery and all Stop messages.
It traverses pages through real touch input, checks every visible target and critical control, and exercises every
catalogue group/field/editor and nested safety/hand-edit/discard/restart question at desktop 960×600 and 1280×800.
Existing `KioskRenderTests`, `KioskCommissioningFlowTests`, `MacScreenTests`, `MacAppTests`, `KioskPanelTests`,
`KioskConsoleTests` and `MacConsoleTests` retain input/role/session/failure/cleanup and no-replay coverage. Rendered
layout checks reject clipped text, overlap and undersized targets; Stop is hit-tested and checked against actual pixels.

`VideoMode` selects unrotated DRM resolution; rotation and density remain independent. The 1920×1200/10-inch panel is
provisional; these explicit density fixtures establish software fit, not physical dimensions or a hardware measurement.
The existing display/options tests check mode parsing, rotation and configuration. No hardware check is required.

The kiosk/Mac documentation uses actual Skia headless output from the controlled in-process API/fake roof. Captures
show synthetic users, masked secret input and no usable credential/private address. Host names are masked in System
captures. Terminal/installer refresh uses existing fake-machine/driver or controlled emulator workflows; no installation,
host service, real HAT, deployment, Docker, release or tag operation is needed.

## Gate record

The implementation matrix intentionally remains `implemented-unverified` until required platform CI and independent
review complete. Local full Debug non-Scenario/Browser/Soak checks passed 3,764 tests (3 opt-in platform tests skipped; 3,767 total).
The Release solution build passed with zero warnings/errors, and the same Release suite passed 3,767 tests
(3 opt-in platform tests skipped; 3,770 total after the independent mode/rotation fixtures). Final Debug focused checks passed all 346 selected native/Client/terminal-RGB tests. The final Release rebuild passed
with zero warnings/errors and all 346 focused tests passed, including the last sign-in card, simultaneous PIN-warning
captures and complete message traversal. Subsequent changes are documentation/comments and capture copying only. The skipped tests are live per-platform installer/keychain
jobs, which are intentionally not enabled in this checkout; CI owns those jobs. No assertion was weakened to hide
an unrelated failure. No C14 run, Docker action, SDK/tool installation or host/service change was performed.
Additional local checks passed: 17 documentation tests, 22 Mac bundle-script unit tests, pinned primary token/hash
validation and zero broken documentation links. The existing `tests/cli/dotnet-run-smoke.sh` passed against its own
loopback-only emulated HAT/roof/VFD, producing the refreshed existing RGB terminal screens, testing every page/F9,
F10 and interrupted-motion Stop behavior. This is framework-dependent local output, not #172 final-publish evidence.
Installer images come from the existing fake-machine wizard renders in the passing full suite.

To refresh the old RGB images, run the existing CLI smoke with `TERMINAL_SMOKE_OUT` and the installed Debug CLI
apphost, and run the installer/CLI driver tests with `HVO_INSTALLER_RENDERS_DIR` / `HVO_CLI_RENDERS_DIR` as in their
documentation. Convert each `.ans` with `tests/cli/ansi-to-svg.py`. For System SVGs, remove any text element equal to
Python `platform.node()` and replace its same `(x, y-14, textLength, 18)` bounds with a `#2f3237` rectangle before
copying; no real host identifier belongs in public captures. All 33 existing native screen PNGs were regenerated, with four additional native examples; the existing Mac icon/ICNS
was regenerated from its palette-consuming code. All 10 existing terminal and 16 installer SVGs were refreshed. Only
generated PNG/SVG/ICNS assets are copied into the documentation/bundle paths. No terminal script or layout changes are part of this refresh.

Web and terminal capability parity are separate gates, even when Client/native role tests pass.
