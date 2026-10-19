# UI redesign foundation (#171)

This is the delivery contract for [#158](https://github.com/HualapaiValley/HVO.RoofController/issues/158),
recorded against Roof main `2e4f1fbfac1216d508fc47913179e3c9b0a4cfb3` on 2026-10-19.
[Foundation #171](https://github.com/HualapaiValley/HVO.RoofController/issues/171) delivers an inventory and design
contract. **Every production redesign row remains pending. Keep #158 open** until the platform work, dependency
removal and evidence below are complete. Existing functionality is not evidence of a completed redesign.

- [Coverage matrix](inventory.csv): filter by `platform`, `slice`, `states` or `redesign_status`. Each row identifies
  an existing surface, access rule, current source and literal source anchor, Stop applicability and specific acceptance
  requirement. IDs remain stable as implementation proceeds; add rows for new supported workflows.
- [Installer plan-step output coverage](installer-steps.csv): each concrete backend step whose purpose, target and check/apply detail appears in Review/progress/plain output, mapped to its existing screen IDs. This inventories presentation of those steps; it does not authorize changing or executing their backend operations.
- [Canonical token data](tokens.json) and [design contract](design-contract.md): the only numeric colour list for this
  redesign, with final merged SkyMonitor provenance and exact ANSI-16 names. It is contract data, not a runtime import.
- [Validation and evidence requirements](validation.md): what source inspection proves now, and what production work
  must prove before a row can become verified.
- [Published payload baseline](payload-audit.md): six hash-verified real v4.1.1 bundles, the specific notice omission and limits; exclusion/runtime/notice-delivery work remains pending.
- [Delivery boundaries and theme removal](delivery.md): independent implementation task briefs for the parent to
  create after foundation review; these are slice names, not newly created issues or functional authorization.

## Reading the matrix

| IDs | Surface family | Delivery slice |
| --- | --- | --- |
| W01–W63 | Every Web route, roof/camera controls, administration, nested editors, reconnect/error/Stop components | `web` |
| N01–N31 | Shared Screens, kiosk PIN/commissioning/blanking, Mac password/admin/refusal and shared restricted editors | `native` |
| T01–T36 | Five roof TUI pages, every current editor/review/confirmation, Stop/quit and TerminalUi schemes | `terminal` |
| C01–C08 | Plain CLI prompts/output and published command contracts | `terminal` |
| I01–I45 | Installer wizard, conditional role forms, private credentials, lifecycle/certificate/backup/bootstrap prompts | `installer` |
| S01–S04 | Existing shared Client palette, wording, Stop and catalogue forms | `contract`, coordinated with all adapters |

`source` is relative to the repository root; `source_anchor` is an exact string in that file, more stable than a line
number as this document changes. The baseline is the commit above, not a deployed build. `baseline_evidence` names
inspection/test-definition families; it never means those tests were run for this foundation. `redesign_evidence`
starts at `none; redesign not implemented`. A platform PR must replace it with a durable test/capture reference,
commit and dimensions/modes/roles actually exercised. Use `in-progress`, `implemented-unverified`, then `verified`
only when the row's entire acceptance requirement is demonstrated; link split work instead of marking a partial row done.

The matrix enumerates reusable form kinds rather than copying every catalogue setting into a screen list.
[RoofSettingsForm](../../src/HVO.RoofControllerV4.Client/RoofSettingsForm.cs) builds those forms from the controller's
catalogue: each platform must exercise every group and supported type, writable/read-only/secret/nullable variants and
long content. It must not replace the catalogue with prototype fixtures. All rows inherit their applicable shell's
status, auth and Stop rules even while a form is open.

## State profiles

The semicolon-separated `states` field selects these requirements **in addition to each row's specific check**.
Each profile includes normal use. When a state cannot occur, record why in implementation evidence; do not manufacture
loading spinners or empty states for a purely local button.

| Profile | Required states and recovery |
| --- | --- |
| `shell` | Normal, selected/focused, busy and disabled controls, mode warnings, long context; retain applicable Stop and newest result. |
| `data` | Loading, populated, empty, malformed/refused read, denied, expired credential, offline, retry; old values either cleared or explicitly last-read. |
| `form` | Empty/default, edited, invalid/mismatch/range, denied, expired while open, sending/busy, acknowledged, refused, failed/lost answer, cancel and explicit retry. No automatic replay. |
| `auth` | Signed out/locked, allowed and denied effective roles, provider unavailable, invalid credential, lockout/rate limit, session revocation/expiry, fresh sign-in. |
| `motion` | Closed/open/between-limits/unknown/opening/closing, blocked direction, starting departure phase, latch, watchdog and optional lease enabled/disabled/renewing/lost/expired; controller-backed values only. |
| `feed` | No first snapshot, connecting/live, stale heartbeat/last-known timestamp, disconnected, refused/expired feed, reconnect and recovery; separate page circuit from controller feed. |
| `camera` | No configured IDs, initialization/waiting first frame, live, paused, stalled/stale retained frame, offline/error/refused/expired, retry/reconnect, unsupported browser operation and disposal. |
| `stop` | Sending; `Acknowledged` with verified register evidence (`AcknowledgedVerified` message); generic `Acknowledged` with missing/Unknown verification, including unreadable HTTP 200 body; `RelayUnverified`; failed/no acknowledgement; retry and concurrent result ordering. Preserve the shared outcome and message: the Acknowledged enum alone proves no register verification, and read-back never proves physical contacts. |
| `long` | Long names, paths, messages, unbounded rows, multiline errors, Unicode cell width; bounded content and reachable actions. |
| explicit states | `loading`, `empty`, `invalid`, `denied`, `expired`, `fault`, `offline`, `recovery` emphasize that case. `fault` includes plant latch versus communication/IO failure; `recovery` must never silently resume motion. |

## Stop applicability

| Matrix value | Contract |
| --- | --- |
| `web-shell` | App's always-enabled Stop and reconnect-popup Stop remain accessible on every page, including static sign-in/404 and open editors. Browser posts independently of the circuit, through the existing endpoint/client to REST. Authentication/pass/key refusal must be honest. Fullscreen camera must prove an accessible Stop path. |
| `native-shell` | Permanent column outside every page/question/PIN or password form; device viewer key works while locked. Idle blanking/wake remains intentional: the wake input is consumed, never passed through as a command. |
| `tui-f9` | Visible action/result and F9 across all pages and prompts, processed before the prompt consumes it. Offline attempts may fail; the control still sends directly and reports the result. |
| `command-only` | Plain CLI retains its `stop` command and outcome/exit semantics; it is not an interactive persistent roof-control shell. |
| `installer-no-motion` | Installer never starts roof motion. Existing deploy/lifecycle verified Stop checks stay in their services; do not add a decorative or motion-testing F9 button. |
| `not-connected` | Mac refusal window has no configured controller/console; no connected roof Stop to hide. Show the reason and Quit. |
| `adapter-owned` | Shared Client wording/palette/forms have no screen of their own; each consumer proves Stop accessibility. |

## Authoritative inputs and corrected scope

The approved prototype sources remain pinned at Roof
[`9cc8f2bbc677f41a9e30217a9881ded0c47860a9`](https://github.com/HualapaiValley/HVO.RoofController/commit/9cc8f2bbc677f41a9e30217a9881ded0c47860a9):
[Web](https://github.com/HualapaiValley/HVO.RoofController/blob/9cc8f2bbc677f41a9e30217a9881ded0c47860a9/docs/prototypes/roofcontroller-cameraagent.html),
[kiosk/Mac](https://github.com/HualapaiValley/HVO.RoofController/blob/9cc8f2bbc677f41a9e30217a9881ded0c47860a9/docs/prototypes/roofcontroller-avalonia-kiosk.html),
[terminal](https://github.com/HualapaiValley/HVO.RoofController/blob/9cc8f2bbc677f41a9e30217a9881ded0c47860a9/docs/prototypes/roofcontroller-terminal.html),
and [guide](https://github.com/HualapaiValley/HVO.RoofController/blob/9cc8f2bbc677f41a9e30217a9881ded0c47860a9/docs/prototypes/README.md).
They were fetched/read through `gh api` for this foundation. They are absent from this main baseline and are not copied
into production. Their recorded HTML checks establish design direction only, never production screenshots or behavior.

The latest owner requirement pins **final merged** SkyMonitor
[PR #1371](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1371), merge
[`bf5ef211fa66e76dd83ac171a3fea944b6d5dc26`](https://github.com/HualapaiValley/HVO.SkyMonitor/commit/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26),
merged 2026-10-19T01:15:31Z into `development/v1`. Earlier #158 comments calling that PR open, or citing its provisional
head or Sky `main`, are superseded for this redesign. See the token contract for the exact primary files.

Production [interface rights](../authentication-interfaces.md), [security](../security.md),
[commissioning](../commissioning.md) and [first-install PIN flow](../kiosk-commissioning.md) remain authoritative:

- Kiosk PIN sessions allow roof operation only; even an Admin PIN is capped at Operator. Settings/System navigation
  is intentionally absent. Mac password sessions retain their role-based desktop rights. Shared touch settings
  editors exist in Screens but do not make kiosk administration reachable. Discoverable policy wording belongs to
  the native redesign; authorized exit to a **Linux login terminal**, service handoff and locked return remain
  [#153](https://github.com/HualapaiValley/HVO.RoofController/issues/153). There is no such exit workflow today.
- Redesign the working Web camera now. Per-camera configuration, generic MJPEG/Basic/Digest, credential binding and
  installer/emulator protocol changes remain [#76](https://github.com/HualapaiValley/HVO.RoofController/issues/76).
  Camera Snapshot/Record/Fullscreen already exist in Web and must be covered; native/terminal camera features do not.
- Existing backup/restore stays usable; optional encryption remains
  [#89](https://github.com/HualapaiValley/HVO.RoofController/issues/89). Estimated roof position and drive choices
  remain [#32](https://github.com/HualapaiValley/HVO.RoofController/issues/32). No percentage/bar inferred from elapsed
  time, changed VFD parameters, generic camera or new host authorization is part of presentation work.
- The terminal published-payload/notice obligation coordinates with
  [#96](https://github.com/HualapaiValley/HVO.RoofController/issues/96). Its independent kiosk and legacy-cache purge
  work is not absorbed into #158. Source dependencies alone do not prove what a published single-file executable ships.

Foundation validation is documented in [validation.md](validation.md). No runtime layout/palette was changed, no .NET
suite or hardware/container operation was run, and no new screenshot is claimed. The contributor-only inventory needs
no CHANGELOG entry. The operator-facing kiosk PIN guidance correction has a narrow Unreleased Fixed entry for #171;
it describes existing policy, with no authentication behavior change. Platform behavior and theme-removal PRs need
their own entries and regenerated documentation images.
