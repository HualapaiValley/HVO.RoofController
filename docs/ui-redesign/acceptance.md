# Final UI redesign acceptance (#158)

The redesign is integrated at main `c0c41b87d443861523ec9d7eccdc8448658c2d30` on 2026-10-19.
This record closes the foundation's implementation gates using reviewed production changes, actual tests and final
distributed files. [The machine-readable receipt](acceptance.json) contains safe counters, exact program identities
and artifact IDs. The final reconciliation changes documentation and matrix status/evidence only; its own normal PR
checks and review still gate merging and closure of #158.

| Delivery | Reviewed final head | Merged through |
| --- | --- | --- |
| Foundation #171 | `984dbdc13b5a22546e595f2a756a13e2e76a39ce` | [PR173](https://github.com/HualapaiValley/HVO.RoofController/pull/173) |
| Payload/notices #172 | `6380ebc67d668112e9d1e4f36b5cf6fdef54ddb2` | [PR177](https://github.com/HualapaiValley/HVO.RoofController/pull/177) |
| Native/shared palette #175 | `ae6ec457ab98233932bbbc6e81e4cd4feca64f2e` | [PR178](https://github.com/HualapaiValley/HVO.RoofController/pull/178) |
| Web/theme removal #174 | `be0602add7ea902300f54ecc2eaa54cf4a9e00e1` | [PR183](https://github.com/HualapaiValley/HVO.RoofController/pull/183) |
| Terminal/installer #176 | `90ab3e14fed0309fe98fc76476491607dcf6b91e` | [PR184](https://github.com/HualapaiValley/HVO.RoofController/pull/184) |

The terminal final head includes only reviewed main integrations: Web/native/payload, identity/TLS/concurrent-host
fixture corrections, Mac PKCS12 import #187/PR188, and Mac nonregular socket classification #189/PR190. Its last
commit changes two evidence documents only. The tested combined source is
`7d75b32f5d67eab8469604f234f9739d5cea52b4`; main's merge tree equals the reviewed final PR184 tree.

## Combined gates

Separate Debug and Release solution builds finish with **zero warnings/errors**. Each matching required suite passes
**3,910 of 3,910 executed tests**, with four existing opt-in `NotExecuted` cases (3,914 total). All 12 new regular-file
cases execute and pass in each configuration. The filter remains
`TestCategory!=Scenario&TestCategory!=Browser&TestCategory!=Soak`, with `--no-build` after the corresponding build,
default method parallelism, inherited umask `0002`, and separate bounded shared-lock windows. The four opt-ins cover
live per-platform installation/keychain or exact-distribution environments; supported hosted jobs supply those proofs.
The [combined receipt](terminal-final-combined-validation.json) retains exact results and earlier invalid/failed attempts.

All **18 triggered checks pass**, with only optional `soak` and `kiosk-soak` skipped. Final-head
[CI](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084),
[scenario/browser/container checks](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606199), and
[emulator image checks](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606176) are complete.
Web's Docker/theme-removal inputs additionally passed the triggered
[controller image build](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39204609850/job/117904615944)
and [emulator image build](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39204609784/job/117904616636)
on its reviewed final head. Subsequent integrations preserve Web source/assets and those guards.

## Web

W01–W63 retain their individual test/capture mappings in [inventory.csv](inventory.csv).
[Web evidence](web-evidence.md) records the actual route/admin/editor/camera/reconnect states, 48px targets,
320px portrait and 568×320 landscape geometry, red focused Stop, independent REST/no-JS POST and fullscreen motion-Stop
proof. Final local Debug required checks passed 3,785 with three opt-ins; final Release Browser checks passed **66/66**.
Actual Web publishes for linux-x64 and linux-arm64 each validated **108 serving asset routes**, and controlled published
x64 HTTP checks verified exact bytes, MIME, fingerprints, fonts/notices and obsolete-theme 404. Earlier Release suite
results are explicitly pre-reconnect-CSS; final browser and combined gates cover that correction.

## Native

N01–N31 retain their specific [native tests and controlled captures](native-evidence.md). Final review corrections
passed **352/352 Debug and 352/352 Release**, after six original-source regression failures reproduced long-token
clipping and cached-input qualification. Measured grapheme/whitespace-preserving page traversal, per-input last-read/
unavailable/recovery text, PIN/commissioning, nested questions and permanent Stop have rendered/input assertions.
Bounded kiosk fixtures cover 1920×1200 at 8.2/9.0 pixels/mm, 1280×720 at 8.2 and 800×480 at 5.2, preserving 12mm
targets. Mac desktop tests cover resizing, keyboard administration and refusal. PIN rights remain capped at Operator;
no kiosk Settings/System or host-login exit is introduced. Hosted Mac/kiosk installation and app/CLI jobs pass.

## Terminal and installer

T01–T36, C01–C08, I01–I45 and all 48 installer plan-step presentation rows retain their concrete test maps in
[the matrices](inventory.csv) and [step inventory](installer-steps.csv). [Terminal evidence](terminal-evidence.md)
preserves mode/size geometry, complete prompts, long errors/results, ASCII masks/borders/scroll controls, F9 before
prompt handling, private input, cancellation, recovery and actual fake-machine lifecycle assertions.

All three supported RIDs run **both exact final programs natively**, in four capability modes at 80×24 and 120×32,
with offline/help/notices/private-prompt and applicable page/Back/cancel paths. Separate rebuilt test hosts execute
each program's **five exact bundled managed assemblies with ReadyToRun disabled**, testing the actual API/fake-machine
workflow catalogue. Render groups use the inert PATH/private-home helper; complementary command/crypto contracts
retain their established environment and absolute OpenSSL. Native ReadyToRun execution and complementary IL/JIT
workflow proof are distinct; the latter is not claimed as full native ReadyToRun branch coverage.

| RID | Runtime artifact | CLI contracts + render | Installer contracts + render |
| --- | --- | ---: | ---: |
| linux-x64 | [12171389575](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12171389575) | 371 + 147 = **518** | 880 + 84 = **964** |
| linux-arm64 | [12171994602](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12171994602) | 371 + 147 = **518** | 880 + 84 = **964** |
| osx-arm64 | [12172990890](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12172990890) | 371 + 147 = **518** | 880 + 84 = **964** |

Every workflow case passes with no skips. Final Mac contracts execute all three real FIFO/socket/device refusals,
the 12 new regular-file cases and the previously host-guarded restore cases. Separate actual Mac
[file-guard evidence](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12172221742)
passes **12/12** and [certificate-import evidence](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12172226660)
passes **3/3**. Strict ad-hoc signatures and exact identifiers, final offline notices and installed/rollback-copy notice
delivery pass. Controlled emulator/tmux motion/quit/signal smoke and regenerated documentation captures retain their
original CLI981/installer647 scopes; later Mac import/socket changes are proved by these new final files, not old binaries.

The matrix describes shared presentations and their concrete backend selection/output, not authorization to run
installer operations on this host. Step-specific fake-machine tests and common review/progress/error geometry compose
that evidence. Neither every backend-class/state Cartesian product nor every state as a separate screenshot is claimed.

## Shared contracts

The receipt records passing Debug/Release outcomes for each shared Client test class, alongside adapter proof:

| Row | Evidence and preserved contract |
| --- | --- |
| S01 | `RoofUiPaletteTests` checks all 20 pinned roles/actions and mandatory Web CSS. Native notice/focus renders and terminal/installer all-mode cases retain paired notice foregrounds, explicit ANSI names, red focused Stop, neutral direction controls and noncolour markers. Tokens/provenance remain unchanged; only adoption metadata changes. |
| S02 | `RoofStatusTextTests` and `RoofStatusFeedTests` cover controller-backed wording, unavailable reads, stale/recovery/refused feeds, ordering and instance changes. Native cached/never-read/recovered input renders, Web stale/reconnect cases and actual TUI status cases retain last-known distinctions without invented position values. |
| S03 | `RoofStopTests` and `RoofCommandRulesTests`, plus actual Web/native/TUI/CLI adapters, preserve Sent, refused/unverified/failed and both Acknowledged messages. `Verified` register evidence permits the verified message; Unknown/missing/unreadable HTTP200 stays generic. The enum alone proves no verification, and read-back proves the register, never contacts. Permanent Stop/direct REST, F9 over prompts, reconnect POST and outcome ordering remain covered. |
| S04 | `RoofSettingsClientTests` retain schema/review/version/safety and role gates. Native desktop and Web editor tests exercise the catalogue while preserving PIN restrictions. `AdminSettingsCatalogue_TraversesEveryGroupAndType_ThroughActualControls` executes eight terminal mode/size profiles against all **seven real Admin API groups and 42 fields**, five published types (Boolean, Integer, String, Duration, Enum), read-only/secret/nullable/long input, invalid Boolean/Enum, cancelled safety review and an actual non-safety Enum save/readback. Number has no published descriptor; generic type logic remains client-owned. |

## Payload and theme removal

The legacy theme RCL, project/GUID/build/Docker/coverage/filter dependencies and unused font assets are removed.
Web owns its direct 20-role CSS and required assets/notices; publish/HTTP and image proof appears above.

The [six final manifest records](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219606084/artifacts/12171637959)
each contain **228 parsed entries and zero NativeBinary entries** after scoped unused highlighting exclusions.
For every program/RID the manifest hash equals both native runtime and exact bundled-IL workflow identity.
Remaining embedded notices are available offline before config/auth/network or machine actions and survive installed
copy/rollback paths. This completes #172's bounded obligation. The [specific v4.1.1 omission](payload-audit.md) remains
historical; published releases/tags were not rewritten, and this is not a certification of all releases' legal compliance.

## Evidence limits and independent work

All 187 surface rows and 48 step rows are verified by their preserved requirements, specific mappings and final gates.
The final status update changes no source anchor, access/state profile, acceptance requirement or token value/provenance.
Screenshots are reproducible selected actual renders, complemented by assertions and shared-state tests; they are not
an exhaustive screenshot catalogue. Historical red baselines, fixture failures, invalid umask attempt and old-binary
limits remain in platform evidence. Structural validation checks links/data/source anchors; it does not execute UIs.

No release/tag publication, deployment, real roof motion or hardware-dependent check is part of this reconciliation.
[#96](https://github.com/HualapaiValley/HVO.RoofController/issues/96) cache lifecycle,
[#153](https://github.com/HualapaiValley/HVO.RoofController/issues/153) authorized host-login handoff,
[#76](https://github.com/HualapaiValley/HVO.RoofController/issues/76) generic cameras,
[#89](https://github.com/HualapaiValley/HVO.RoofController/issues/89) backup encryption and
[#32](https://github.com/HualapaiValley/HVO.RoofController/issues/32) estimated position/drive choices remain separate.
