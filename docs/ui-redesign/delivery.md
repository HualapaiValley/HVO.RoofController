# Delivery boundaries

After foundation review, the parent can create linked implementation issues from these slices, each referencing #158
and the matrix IDs it owns. #171 closes only the foundation. The payload-notices task is now [#172](https://github.com/HualapaiValley/HVO.RoofController/issues/172); other platform issue IDs remain to be assigned by the parent.
Keep one branch/PR per task and link evidence back to the matrix. Avoid parallel edits to the same theme/palette files
without an explicit owner and integration order.

| Slice | Bounded responsibility | Dependencies and completion evidence |
| --- | --- | --- |
| `contract` | Maintain token data, map existing RoofUiPalette members, common wording/Stop/catalogue contracts (S rows). | Foundation defines values; first runtime migration must include adapter integration plan and token-parity tests. Do not change safety/auth behavior or create a new package. |
| `web` | W rows: all routes, secondary forms/admin/auth, current camera player, responsive shell/error/reconnect/Stop; Web-owned CSS/assets. | Can start with existing camera API, independent of #76. Integrate palette ownership with native/terminal migration. Browser + component tests, 320px+, all states/roles, images in docs/web.md. |
| `native` | N rows: Screens/Kiosk/Mac together, PIN policy explanation, commissioning and lock/wake, bounded layouts, Mac keyboard/admin/refusal. | 12mm touch targets, no kiosk page scrolling, all display sizes and Mac resizing. Do not expose restricted shared Settings/System through PIN; #153 host handoff stays separate. Headless renders and regressions, kiosk/mac docs. |
| `terminal` | T/C rows: TerminalUi visual primitives actually consumed by both apps, five roof pages, all prompts/private fields/Stop/quit and existing plain CLI contract. | Coordinate shared theme changes with installer; truecolour/ANSI16/NO_COLOR/ASCII in both sizes; published CLI payload and notices obligation below; CLI docs images. |
| `installer` | I rows: every wizard step/conditional role form/nested consent/private entry/plan/progress/done/failure plus current plain lifecycle/bootstrap output. | Reuse terminal adapter without importing roof TUI session/quit rules. Preserve private input, plan/apply boundaries, resource/state/rollback contracts. Published installer and headless wizard validation, install docs images. |
| `theme-removal` | Delete HVO.WebSite.Themes and all build/static-asset/guidance dependencies once Web assets and palette adapters are ready. | Build/publish/container/asset checks and repository reference audit below. May be combined with the Web issue if explicitly owned; avoid a broken intermediate published asset path. |
| `payload-notices` — [#172](https://github.com/HualapaiValley/HVO.RoofController/issues/172) | Correct the verified historical omission through scoped highlighting exclusions and retained notice delivery; audit candidate final artifacts. Historical inventory/omission research is complete. | Active independent delivery task; correction/runtime/publish validation pending. Cover both programs/all supported RIDs and final installed notice delivery. It closes neither #158 nor #96; kiosk/legacy-cache work remains separate. |

Root owns independent review, CI readiness/watches and merging. Implementation PRs update their matrix rows and the
operator-facing changelog/documentation/screenshots. Final #158 reconciliation checks every row and dependency-removal
acceptance item, including any newly integrated #153/#76 presentation surfaces, rather than closing it when the three
main dashboards look finished.

## Independent functional contracts

- [#153](https://github.com/HualapaiValley/HVO.RoofController/issues/153): authorize exit to a Linux **login** terminal,
  release kiosk display/input ownership, prevent immediate `Restart=always` restart, provide authorized return and
  locked re-entry. Keep controller/supervisor running; closing UI drops its held lease under current rules. PIN roof
  rights never grant shell/service privileges. Define failure/cancel/expiry/service-cleanup cases with headless fixtures
  before exposing the eventual controls. Current shell has no exit control. Redesign can explain today's PIN policy now.
- [#76](https://github.com/HualapaiValley/HVO.RoofController/issues/76): generic MJPEG entries, Basic/Digest, credentials
  and origin binding, legacy compatibility, emulator challenges and installer/configuration schema. New forms follow
  that feature's contract when available. Existing Web camera presentation does not wait; no camera is added to native/TUI.
- [#89](https://github.com/HualapaiValley/HVO.RoofController/issues/89),
  [#32](https://github.com/HualapaiValley/HVO.RoofController/issues/32): encryption and estimated position/drive choices
  are separate future features. Preserve working backup/restore and current actual roof readings.
- [#96](https://github.com/HualapaiValley/HVO.RoofController/issues/96): cache lifecycle, including kiosk/legacy-cache
  purge, remains separate from visual styling and the bounded terminal highlighting/notice correction.

## Theme removal inputs

Web/#174 owns the removal and active [Web stylesheet](../../src/HVO.RoofControllerV4.Web/wwwroot/css/hvo-dark.css).
The project, solution GUID/configuration rows, Docker copies, workflow filters and coverage dependency are removed.
The original project-tree link below is pinned to the foundation baseline; it is historical evidence, not an active
path. [Web delivery evidence](web-evidence.md) records actual builds, published assets/notices and browser captures.
The retained Bootstrap/Icon font licences stay with their Web assets; the unused OpenSans files were not referenced
by the baseline stylesheet and are removed. No native or terminal runtime input is removed by that change.

This was the **pending removal checklist** recorded by #171; the table retains its required proof, not a claim that
source inspection alone establishes published output. Exact direct references were searched in tracked and
hidden repository files at the baseline; validate again after platform work. Historical mention in this foundation,
release notes or CHANGELOG is not a runtime dependency and should not be silently erased.

| Input | Required action / proof |
| --- | --- |
| [Theme project tree](https://github.com/HualapaiValley/HVO.RoofController/blob/2e4f1fbfac1216d508fc47913179e3c9b0a4cfb3/src/HVO.WebSite.Themes/README.md) | Delete project, CSS and obsolete OpenSans assets after enumerating consumers. Prove required fonts/icons/assets owned by Web still ship with their notices; do not blindly copy unrelated CameraAgent styles. |
| [Solution](../../src/HVO.RoofController.sln) | Remove project entry **and its GUID configuration rows**, not only the path. Debug and Release solution builds must pass. |
| [Web project](../../src/HVO.RoofControllerV4.Web/HVO.RoofControllerV4.Web.csproj) | Remove ProjectReference; Web publishes with its own required CSS/fonts/icons and scoped assets. No replacement RCL/package. |
| [App](../../src/HVO.RoofControllerV4.Web/Components/App.razor), [app.css](../../src/HVO.RoofControllerV4.Web/wwwroot/css/app.css) | Replace `_content/HVO.WebSite.Themes` asset URL and ownership comment; validate fingerprinted asset mapping and every CSS import/url, including font/data references. |
| [Dockerfile](../../src/HVO.RoofControllerV4.RPi/Dockerfile) | Remove both project-file restore-stage COPY and full-source COPY for the RCL. Preserve Web restore-before-COPY correctness and publish assets. Controller image is shared by physical and emulator deployment paths. |
| [Image workflow](../../.github/workflows/pi-image.yml), [emulator image workflow](../../.github/workflows/emulator-image.yml) | Remove obsolete theme path triggers from push and PR filters; retain Web/Client/Terminal/native inputs where needed. Actions remain SHA pinned. |
| [Coverage settings](../../src/coverage.runsettings), [palette tests](../../tests/HVO.RoofControllerV4.RPi.Tests/Client/RoofUiPaletteTests.cs) | Remove obsolete assembly include; repoint parity tests from old RCL CSS and old Stop Bootstrap assumptions to Web-owned stylesheet + canonical tokens, including notice pairs/ANSI roles. |
| [Published web asset check](../../build/check-web-assets.sh), [CI](../../.github/workflows/ci.yml) | At the foundation baseline, the asset check verified Blazor bootstrap/auth only. #174 extends it to fetch Web-owned theme/required assets from actual published output and assert correct MIME/content, with no obsolete `_content` dependency. Source builds alone miss Docker publish asset errors; local publish/HTTP proof and the remaining image-CI gate are recorded in Web evidence. |
| [Release workflow](../../.github/workflows/release.yml), [asset builder](../../build/release-assets.py), [release compose](../../build/release-compose.py) | No direct RCL string was found here, but follow CI's image/artifact inputs to release archives/manifest. Validate required assets in real publish/image outputs; do not assert packaging unaffected merely because string search is empty. No tag/release publication needed for validation. |
| [Development compose](../../src/docker-compose.yml), [deployment compose](../../src/HVO.RoofControllerV4.RPi/docker-compose.yaml), [deployment docs](../deployment.md) | No direct RCL reference found; confirm build-context/image paths and published Web remain correct. Deployment behavior and physical hardware are outside redesign validation. |
| [Client palette](../../src/HVO.RoofControllerV4.Client/RoofUiPalette.cs), [Client guide](../../src/HVO.RoofControllerV4.Client/README.md), [TerminalUi theme](../../src/HVO.RoofControllerV4.TerminalUi/RoofUiTheme.cs) | Remove old theme ownership/amber-Stop/automatic-nearest-colour descriptions when implementing their new adapters; preserve shared names where practical. |
| [Root guide](../../AGENTS.md), [README](../../README.md), [contributor guide](../../CONTRIBUTING.md), [controller guide](../../src/HVO.RoofControllerV4.RPi/AGENTS.md), [agent entrypoints](../../CLAUDE.md) | Update project table, theme ownership and UI guidance at removal. Copilot and Claude point to root AGENTS; preserve those links. Search hidden files, case/escaped paths and theme project's GUID, not only exact product name. |
| [Web](../web.md), [kiosk](../kiosk.md), [Mac](../mac.md), [CLI](../cli.md), [install](../install.md), [releasing](../releasing.md) | Reconcile ownership/colour/fallback descriptions with actually shipped behavior; regenerate each affected UI's documented images through headless workflows. Do not relabel old screenshots as redesign evidence. |

## Published terminal payload and notices

The owner requested an **actual published-artifact audit**. The completed [durable baseline](payload-audit.md)
records six hash-verified v4.1.1 installer/CLI bundles across `linux-x64`, `linux-arm64`, `osx-arm64`: each parsed
232-entry manifest contains the highlighting closure/native wrapper. The supplied Onigwrap notice file and specific
Oniguruma/Fluent Solutions material were absent in the inspected distributions. Other embedded MIT text exists;
this is a specific verified omission, not a claim that no notices ship or a legal-compliance certification.

Exclusion, current-main publish/runtime behavior and final remaining-notice delivery are **pending in
[#172](https://github.com/HualapaiValley/HVO.RoofController/issues/172)**. The historical inventory/omission research is complete; the foundation does not wait for #172 implementation. Do not add
`osx-x64` or execute a release installer against a live host as part of an audit.

The implementation obligation is broader than Sky #1371's published installer smoke. That smoke covers installer
help/no-TTY/first page at 80x24 and 120x32 and uses byte-name searching; it does not establish a parsed Roof bundle
manifest, the Roof CLI's actual UI/commands, all screens, or each supported RID.

1. Inventory each downloaded release asset by release/tag/source identity and SHA-256, archive and parsed .NET
   single-file bundle entries, managed/native dependency closure and shipped notice files. Distinguish embedded,
   external and self-extracted payload; searching filenames/strings alone is supporting evidence.
2. Trace Terminal.Gui 2.5.0 Code/Markdown/Wizard/TextMate usage and actual load paths. Exclude TextMateSharp,
   grammar and Oniguruma material **only after proving unused in both Roof terminal programs**. Custom Roof wizard
   classes are not Terminal.Gui's highlighting Wizard. If required material remains, ship applicable licence and
   author notices and verify them in distributed assets. Retain other required dependencies and notices.
3. Publish candidates with the release settings for both programs and all three RIDs. Inspect actual bundles again.
   Exercise compatible published executables' --help/version/no-terminal and plain/JSON command paths, plus real
   Terminal.Gui pages/prompts at both sizes with controlled fixtures/emulated controller and private input handling.
   Use appropriate supported runners for native RID execution; do not label nonexecuted RIDs smoke-tested.
4. Verify removal does not cause dynamic assembly/native-load failure after the first page: navigate all five CLI
   pages/editors and every applicable installer step, failure/recovery and credential prompts in isolation. No real
   install/deploy/service changes or hardware access; the installer execution harness must fake machine operations.
5. Record per-program/RID results and remaining notices in durable evidence and a regression guard. Verify applicable
   notices survive final release assembly and installed CLI/installer copying, not only a loose publish folder. Correct
   the verified omission in **the next release**; never rewrite a published release/tag. Keep unrelated kiosk/legacy
   cache purge in #96.

Published inventory and the specific omission are verified in the baseline; the correction and runtime/notice delivery
gates remain pending. Package-reference/source-build tests, a Sky result, or successful CLI --help alone cannot close them.
