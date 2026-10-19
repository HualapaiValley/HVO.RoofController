# Web redesign evidence (#174)

This records the Web-owned delivery (W01–W63) and theme-project removal for #158. It does not close the native,
terminal, installer or shared-contract rows. The approved prototype remains pinned at
[9cc8f2b](https://github.com/HualapaiValley/HVO.RoofController/blob/9cc8f2bbc677f41a9e30217a9881ded0c47860a9/docs/prototypes/roofcontroller-cameraagent.html).
The final Sky sources were fetched through canonical `gh api` at `bf5ef211fa66e76dd83ac171a3fea944b6d5dc26`;
`validate.py --sky-readme ... --sky-css ...` verifies the exact primary hashes and 20-role table in `tokens.json`.

## Implementation

Web owns [all 20 direct CSS role declarations](../../src/HVO.RoofControllerV4.Web/wwwroot/css/hvo-dark.css),
[its layout/controls](../../src/HVO.RoofControllerV4.Web/wwwroot/css/app.css), scoped dashboard/camera styles and local
Bootstrap/Icon assets. The dashboard follows the approved hierarchy: overview; roof controls and camera; detailed
readings and command feedback. Every route uses the same typography, panel, field, notice and action vocabulary.
People tables become labelled records on narrow screens; inline target editors remain in their record. Settings
retain their catalogue groups, private repeated fields and explicit reviews. My Keys groups creation beside its list.
No camera, position, authentication or encryption feature is added. An empty configured camera-ID list selects camera 2
in the existing `RoofWebOptions.Cameras` contract, so a zero-tile camera state cannot occur in valid configuration;
waiting/offline/refused player states cover unavailable video without inventing a no-camera setting.

Stop uses the canonical danger notice pair with a danger edge and a visible outline/underline on keyboard focus.
The form remains outside the live circuit, posts through the existing Web/client REST path and is never disabled.
Browser fullscreen moves **that same form** inside the fullscreen element and restores it on exit. Escape explicitly
exits even after focus moves to Stop and returns focus to the camera fullscreen button. The existing
outcome/message remains authoritative, including both `Acknowledged` messages. `ui.js` only adds focus/return,
Escape cancellation and field error association to existing inline editors. Controller authorization, settings
versions/safety confirmations, watchdog and optional lease behavior remain unchanged.

## Asset ownership and removal

The old project, project reference, solution entry and **all GUID configuration rows**, both Docker restore/source
copies, old static URL, workflow path filters and coverage include are removed. The foundation project-tree link is
pinned to the historical source; its audit text remains historical. Native and terminal inputs are retained and
shared palette tests/docs are owned by #175.

Bootstrap CSS and Bootstrap Icons CSS, `.woff`/`.woff2` icon fonts and both MIT `LICENSE` files remain under Web's
`wwwroot/lib`. The removed OpenSans files were not referenced by the baseline theme (`@font-face`/font URL inventory);
Web uses the system sans-serif stack with no remote font request. No required distributed font or notice was moved
outside the publish output, and there is no replacement theme package.

[check-web-publish.py](../../build/check-web-publish.py) inspects a **real publish directory**: exact source bytes,
all canonical roles, every local CSS URL/import, scoped component CSS, the exact collocated camera module, plain/fingerprinted serving routes, both notices
and fonts, dependency manifest and absence of obsolete theme files. Ten regression cases cover missing fonts,
notices, stale bytes, missing fingerprints, obsolete dependencies, broken URLs, valid URL imports, canonical-role drift and a missing collocated camera module.
CI inspects both supported Linux Web RIDs (`linux-x64`, `linux-arm64`).
The optional `--base-url http://127.0.0.1:<controlled-port>` follows the published HTML's actual fingerprint URLs,
compares HTTP-served bytes/MIME and checks the old URL returns 404. Image CI's existing
[asset check](../../build/check-web-assets.sh) additionally checks the Dockerfile's actual published process; it has
not been executed locally because Docker operations were outside this task's authorization.

## Validation record

The reviewed native palette/main integration is `e5f920af3c367ff8501dd32db5acba3090383c88`, merged into this
branch at `66fd77798ac5e9147b519142a15dc46eaf1d4e99`. The active Web path is mandatory in the migrated shared
palette test; it checks every direct role declaration rather than skipping Web parity. Final evidence is collected
under `/tmp/hvo-174`; commands use the pinned SDK from `src/`. Every heavy command used
`flock -w 600 -o /tmp/hvo-heavy-gate.lock timeout -k 20s <bounded-duration>`, releasing the gate between commands.

Final product source is `c35d0b5b7101fbfe0ecdac1bf7c68cd5c2a30d2b` (including the accepted initial-reconnect
Stop correction). Completed local gates:

- Debug and Release solution builds: PASS, zero warnings/errors (`final-build-debug.log`, `final-build-release.log`).
- Full required Debug unit/API suite: **3,785 PASS, three existing opt-in skips, zero failures**
  (`final-tests-debug.log`, `results/final-debug.trx`). Shared palette parity and all existing Stop/API/lease/security
  assertions are retained. The full Release suite at `930f3eb`, before the final reconnect-only markup/CSS change,
  also passed 3,785 with three opt-in skips (`final-tests-release.log`); Release browser checks cover the final change.
- Full Release browser suite: **66 PASS, zero skips/failures** (`final-browser.log`, `results/final-browser.trx`),
  including all role/session/security/lease/Stop/camera/touch/layout/capture cases and all five initial reconnect states.
- Focused camera/capture run: 19 PASS (`camera-captures.log`), including actual JPEG/WebM download links,
  paused/reconnected video, unsupported/failing controls with Live state retained, failed recording-stop retry,
  moving-emulator fullscreen direct Stop/Escape/focus and four screenshot fixtures. Reconnect checks pass nine
  cases, followed by four compact-height cases (`reconnect-initial-fixed.log`, `reconnect-short-final.log`).
- Actual Release Web publishes for **linux-x64 and linux-arm64**: PASS, 108 serving routes each. The x64 published
  process additionally passes controlled anonymous loopback HTTP byte/MIME/fingerprint checks and old-theme URL
  404 (`final-publish-check.log`, `publish-linux-x64.log`, `publish-linux-arm64.log`). This verifies fresh published
  output, not the historical released-terminal audit. ARM publish output is inspected; ARM execution is not claimed.
- Python docs/asset-check regressions: 27 PASS, including ten published-Web guard cases. Release-script Python
  checks: 102 PASS. Documentation: 53 Markdown files, zero broken links. ShellCheck, diff whitespace and the
  foundation validator pass: 187 source-traced rows, 11 routes, 48 installer steps and all 20 exact pinned primary
  role/source hashes.

Required and triggered GitHub CI/image checks remain a PR integration gate. The Docker image asset script is
extended and statically checked, but Docker/image execution was not performed locally. Only W01–W63 are updated;
all unowned rows, including native N rows and the current S01 integration status, remain as received from main.
#158 remains open.

The full unit/API filter is `TestCategory!=Scenario&TestCategory!=Browser&TestCategory!=Soak`; the separate browser
run uses `TestCategory=Browser`. Opt-in platform-installation tests remain opt-in. No C14 or unrelated stress/soak
was run. The new minimum-width cases use signed-in Admin names with 59 unbroken characters and managed-key names
with 54, at 320/1440px; actions, text/password/number inputs, selects and checkbox labels retain 48px targets.
Browser role/session cases cover Viewer, Operator, Admin, anonymous/refused and expired sessions. The camera uses the
controlled emulated stream; no-frame, stale/offline/recovery and unsupported/failing controls retain distinct states.

### Retained failures and corrections

- Before reviewed #175 reached main, the full Release run had 3,751 passes, three opt-in skips and 26 failures in the
  old shared palette initializer reading the deleted RCL. No assertion was weakened or skipped; the migrated tests
  pass after main integration.
- The first browser run had 59 passes and three screenshot fixture failures: reducing a watchdog already makes the
  setting safer, so it needs no extra confirmation. The fixture now previews an **unsent** at-speed-confirmation
  disable. Four corrected screenshot cases pass. The new personal-key Done fixture uses renderer synchronization;
  media tests follow the existing explicit JPEG/WebM download links rather than assuming automatic downloads.
- The first post-main Debug run had 3,784 passes, three opt-in skips and one bUnit stale-handler exception at
  `PeoplePageTests.cs:41`. Independent #176 Debug evidence reproduced the same raw-click exception while its Web
  runtime/tests were byte-identical to main `e5f920a`: `/tmp/hvo-176/required-results/required-debug.trx`, SHA-256
  `8861785306ea60356cee61c77251c2176d2b2d0576907af0a42247406c9158d3`. The safe classification is
  `/tmp/hvo-176/required-debug-failure-classification.json`. Commit `930f3eba0a59254be1ad4564912ecc6c68cb5d3c`
  replaces the raw click/change with the existing renderer-safe helpers, preserving all API/result assertions.
  Its full Release run passes 3,785 tests with three opt-in skips.
- The strengthened reconnect probe resets modal/state scroll to zero **before** any focus/click/scroll, then checks
  full button bounds, center hit-test and enabled state for show/failed/rejected/paused/resume-failed. It reproduced
  the candidate's 568×320 clipping: Stop's bottom was 322.203px in a 320px viewport (two other sizes passed).
  `reconnect-initial-baseline.log`/TRX retain this red result. The fix bounds the explanatory state and long result
  scroll areas separately from Stop, with keyboard access. Nine focused cases pass, including C15 REST/failure
  behavior and a real 568×320 capture after a verified emulated Stop; the original assertions are retained.

## Reviewed main integration after Web review

Canonical main `e14f479196c88899b847737e418751ad9a68a8d2` (reviewed #172 / PR #177) was merged after
Web head `aa2a22f27ffff3bfd166e85efa7b1e24e8124bc0`. The only conflict was CHANGELOG; both entries remain.
The combined CI file, after removing the unchanged #174 dual-RID Web publish block, is byte-identical to reviewed
main. Its exact final bundle/notice/native inventory, distribution matrix, strict macOS signature and supported-host
checks remain intact. The reviewed inert Bash helper is also retained byte-for-byte.

Integration checks pass: 120 release Python regressions (including inert-helper and signature-step test doubles),
27 docs/published-Web guard tests, YAML parsing/step ordering/all three terminal RIDs, ShellCheck, whitespace and
55 Markdown files with zero broken links. The foundation validator still passes all 187 rows, 11 routes,
48 installer steps and 20 pinned primary role/source hashes. Logs are `/tmp/hvo-174/integration177-{releasing,docs,links}.log`.

Web runtime source, tests, assets, captures and the entire matrix remain byte-identical to the reviewed Web head.
The earlier build/test/publish/capture evidence therefore remains applicable; broad local runs were not repeated.
Normal new CI validates the merged head. No hardware, Docker, publication or host-service operation was performed;
#158 remains open and all unowned rows/S01 status are preserved.

## Reproducible captures and scope

[Operator guide](../web.md#tests) documents `WebScreenshotTests` and `WEB_SCREENS_OUT`. They run the actual Web process
against the emulated HAT/roof/camera and synthetic identities. Captures never show entered passwords or one-time key
values; host facts are masked. Sign-in/provider selection, roof idle/moving, Health, Settings, People, System, private
person form, safety review, password change, Linux role/access, scoped personal-key form, separate camera-control
warning and 568×320 reconnect result use real components.

Matrix evidence names classes/methods relative to `tests/HVO.RoofControllerV4.RPi.Tests`. Verification combines
state/action assertions with the shared responsive shell and measured editor/dialog geometry; it does not claim a
screenshot of every role/state/size Cartesian combination.

Existing bUnit/API/browser cases remain the behavior evidence for refusal, stale/last-known values, authentication,
expiry, settings conflicts and hand edits, private inputs, leases, camera recovery and Stop outcomes. Prototype renders
are not counted as production evidence. No test uses real hardware or a live controller; there was no deployment,
host-service change, Docker operation, release, tag or external publication.
