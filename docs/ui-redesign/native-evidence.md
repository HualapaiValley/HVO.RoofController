# Native redesign evidence (#175)

This slice implements N01–N31 and the Client/native portion of S01 for #158, from the approved native prototype at
`9cc8f2bbc677f41a9e30217a9881ded0c47860a9` and the exact reviewed foundation `984dbdc13b5a22546e595f2a756a13e2e76a39ce`.
Sky primary README/CSS were fetched through canonical `gh api` at `bf5ef211fa66e76dd83ac171a3fea944b6d5dc26`;
`validate.py --sky-readme … --sky-css …` verifies their exact hashes and all 20 roles. No source is taken from a local
Sky checkout. The [token data](tokens.json) retains those source hashes and ANSI names.

## Implemented behavior

The shell has a full-width controller/session header, separate position and motion panels, permanent red Stop and
bottom navigation. Larger layouts add a compact readings grid; all sizes have bounded Readings/Notices/About documents.
`KioskPager` measures full-size controls/text into Previous/Next pages, splitting long paragraphs and oversized tokens at measured grapheme boundaries without dropping
text or whitespace. It keeps the current page over status updates. Only the current page participates in keyboard navigation.
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
captures and complete message traversal. Those pre-review gates apply to `075f913`; the runtime review corrections below have separate focused gates. The skipped tests are live per-platform installer/keychain
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


## PR #178 review corrections

Review of `075f913` found two gaps in the original evidence. Repeated spaced prose did not exercise a token larger
than one page, and the compact facts had dropped the former per-input last-read qualification. The six new regression
fixtures run against the original production files failed as expected: actual Readings clipped the oversized token at
all four size/density fixtures; both visible-facts geometries displayed bare cached `active`/`inactive`.
The valid comparison is `/tmp/hvo-175/review-baseline/review-baseline.trx` (6 failures, 0 passes).

F1 replaces ASCII-space splitting with measured Unicode text-element prefixes. It prefers nearby whitespace for normal
prose, preserves source whitespace/newlines verbatim, includes the continuation cue in measurement, and falls back to
complete graphemes for oversized tokens and CJK. Fonts and 12 mm controls retain their metrics. The current page still
survives a heartbeat. `ArbitraryServerText_IsFullyRenderedAcrossReadingsAndStopPages` walks actual Readings and Full
result documents by touch at 1920×1200/8.2 and /9.0, 1280×720/8.2 and 800×480/5.2. Its sources include a 6,000-character
single token, CRLF/LF-only paragraph separators and CJK text without spaces containing combining sequences. Every page is drawn;
laid-out line spans must be complete, ordered, uncollapsed, grapheme-safe and inside their own block and every clipping
ancestor. Concatenating visible spans and source fragments must recover the exact original text, including whitespace.
The shared traversal helper now uses these rendered spans for existing bounded-page tests too. Only bounded documents
permit necessary word wrapping inside a measured token wider than its box; normal prose and ordinary UI-label word-breaking assertions remain in force.

F2 qualifies each compact limit/drive input with **last read** and `LastSuccessfulInputReadUtc` after input reads fail or the feed becomes stale.
Without a successful read it shows **unavailable (no successful read)**, including the non-nullable drive-running false
value. `SnapshotUtc` is not substituted as input evidence. `CompactInputs_DistinguishCachedNeverReadAndRecoveredValues`
uses fresh hub snapshots through the controlled API fixture, retaining cached true/false inputs while reads fail,
then reporting never-read data, healthy recovery and a stale feed with previously healthy inputs. It asserts the actual visible text/geometry at 1920×1200/9.0 and
960×600/4.0 where the facts are present. Controller/API behavior and Client status formatting are unchanged.

These corrections retain all N rows as `implemented-unverified`; required remote CI and the parent's follow-up review
remain acceptance gates. No terminal files or captures change in this correction, and no full-suite/publish/runtime
repeat is required for it.


The additional controlled captures are copied directly from the final passing Debug Skia frames (no image editing):

| Committed capture | Reproducible `KioskRedesignTests` frame name |
| --- | --- |
| [Cached inputs with live feed](../images/kiosk/input-read-failure-1920x1200.png) | `CompactInputs…`: `review-inputs-cached-1920x1200.png` |
| [Never-read inputs](../images/kiosk/inputs-unavailable-1920x1200.png) | `CompactInputs…`: `review-inputs-never-read-1920x1200.png` |
| [No-space full Stop result](../images/kiosk/long-stop-result-800x480.png) | `ArbitraryServerText…`: `review-stop-2-1-800x480.png` |

Set `HVO_KIOSK_RENDERS_DIR` on the focused command above; it saves every reachable page, not only these examples.
The final Debug artifacts/TRX are `/tmp/hvo-175/review-final-debug-renders` and
`/tmp/hvo-175/review-final-debug/review-final-debug.trx` (352 passed, no skips/failures). The precise durable evidence
is the source-traced tests, captures, commands and gate results here; temporary local logs are supplementary.


Review-fix gates on 2026-10-19:

| Gate | Result |
| --- | --- |
| Final focused Debug native/Client/terminal RGB | 352 passed, 0 failed/skipped; `review-final-debug.trx` |
| Final focused Release, including warning-as-error build | 352 passed, 0 failed/skipped; no compiler warnings/errors; `review-release.trx` |
| Pinned primary source/hash validator | PASS: all 187 rows and 20 roles, exact primary hashes |
| Documentation unit tests | 17 passed |
| Documentation links / diff whitespace | 0 broken links / PASS |

The Release artifacts/TRX are `/tmp/hvo-175/review-release-renders` and
`/tmp/hvo-175/review-release/review-release.trx`. Debug and Release used the same focused filter and shared-lock/bounded
command above, selecting `-c Debug` or `-c Release`. No full suite, publish, emulator-runtime or terminal capture was
repeated after these review corrections. The prior full-suite and RGB-runtime evidence is explicitly pre-review.
Required new-head CI/platform execution and independent follow-up review are still pending; these local gates do not
mark platform acceptance complete.
