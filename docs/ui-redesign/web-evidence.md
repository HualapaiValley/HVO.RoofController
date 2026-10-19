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
No camera, position, authentication or encryption feature is added.

Stop uses the canonical danger notice pair with a danger edge and a visible outline/underline on keyboard focus.
The form remains outside the live circuit, posts through the existing Web/client REST path and is never disabled.
Native fullscreen moves **that same form** inside the fullscreen element and restores it on exit. The existing
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
all canonical roles, every local CSS URL/import, scoped component CSS, plain/fingerprinted serving routes, both notices
and fonts, dependency manifest and absence of obsolete theme files. Nine regression cases cover missing fonts,
notices, stale bytes, missing fingerprints, obsolete dependencies, broken URLs, valid URL imports and canonical-role drift.
The optional `--base-url http://127.0.0.1:<controlled-port>` follows the published HTML's actual fingerprint URLs,
compares HTTP-served bytes/MIME and checks the old URL returns 404. Image CI's existing
[asset check](../../build/check-web-assets.sh) additionally checks the Dockerfile's actual published process; it has
not been executed locally because Docker operations were outside this task's authorization.

## Validation record

Evidence is being collected under `/tmp/hvo-174`; the final matrix and this record will name completed runs and limits.

- Debug solution build: PASS, zero warnings/errors.
- Release solution build: PASS, zero warnings/errors.
- Interim full Release non-scenario/non-browser/non-soak suite before #175 integration: 3,751 passed, 3 opt-in skips;
  26 failures all from legacy `Client.RoofUiPaletteTests` reading the removed RCL path. These assertions are not weakened
  or skipped; final suite awaits #175's reviewed migration through main.
- New `WebRedesignBrowserTests`: six cases PASS (320px/desktop nested routes/editors and recreated row-action focus,
  actual red keyboard focus, fullscreen direct Stop, both acknowledgement messages and all reconnect-state Stop
  reachability at 1440x900, 320x720 and 568x320).
- Existing full browser run: 59 PASS; three screenshot cases initially chose a safety-tightening edit which requires
  no confirmation. The fixture now previews an unsent at-speed-confirmation disable; all four screenshot cases pass.
- Focused Web/Stop/browser/capture run: 431 PASS with two new fixture failures (bUnit Done assertion needed renderer
  synchronization; camera snapshot/record uses its existing explicit download link). Both are corrected; the targeted
  three-case personal-key/media run passes, including actual JPEG/WebM downloads, pause/resume and camera reconnect.
- Actual Release Web publish and anonymous loopback HTTP checks: PASS, 108 serving routes; canonical values,
  exact shipped/served bytes, fingerprinted HTML CSS links, correct CSS MIME, icon fonts/notices, scoped CSS and old URL 404.
- Python docs/asset-check tests: 26 PASS; 52 Markdown files, no broken links; shell asset checker passes ShellCheck.

## Reproducible captures and scope

[Operator guide](../web.md#tests) documents `WebScreenshotTests` and `WEB_SCREENS_OUT`. They run the actual Web process
against the emulated HAT/roof/camera and synthetic identities. Captures never show entered passwords or one-time key
values; host facts are masked. Sign-in/provider selection, roof idle/moving, Health, Settings, People, System, private
person form, safety review, password change, Linux role/access and scoped personal-key form use real components.

Existing bUnit/API/browser cases remain the behavior evidence for refusal, stale/last-known values, authentication,
expiry, settings conflicts and hand edits, private inputs, leases, camera recovery and Stop outcomes. Prototype renders
are not counted as production evidence. No test uses real hardware or a live controller; there was no deployment,
host-service change, Docker operation, release, tag or external publication.
