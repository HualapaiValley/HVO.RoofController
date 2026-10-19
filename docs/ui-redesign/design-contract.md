# Roof design contract

[Tokens](tokens.json) are the canonical numeric colour data for #158. Keep colour values and exact Terminal.Gui
ANSI-16 fallback names there, not in additional mutable documentation tables. The file mirrors **all 20 semantic
roles** from the final merged
[Sky terminal README](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26/src/HVO.SkyMonitor.TerminalUi/README.md)
and its
[CameraAgent CSS](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26/src/HVO.SkyMonitor.CameraAgent/wwwroot/css/themes/hvo-dark.css).
Both were fetched from official GitHub contents at that exact commit through `gh api`; their SHA-256 hashes and source
identity are in the data. Status notice backgrounds are the CSS alpha colours composited over `background`, rounded
as the merged README specifies. Do not composite them again over another platform surface.

## Ownership and adoption

The existing [RoofUiPalette](../../src/HVO.RoofControllerV4.Client/RoofUiPalette.cs) already supplies
[RoofUiTheme](../../src/HVO.RoofControllerV4.TerminalUi/RoofUiTheme.cs) and
[KioskTheme](../../src/HVO.RoofControllerV4.Screens/KioskTheme.cs). Retain this structure. Web will own its CSS after
[theme removal](delivery.md#theme-removal-inputs). There is no replacement shared theme package and no dependency on
SkyMonitor assemblies. The contract file can be checked by adapter tests; runtime JSON parsing or unused framework
components are not needed. Add bounded palette members/aliases only when a consumer uses them.

The `roof_palette_member` field maps the canonical role onto an existing member where available; `surface-raised`
needs an explicit mapping during migration. Legacy `Badge`, `MutedWeak`, `FocusRing` and Bootstrap action constants
need a call-site audit: use surface/raised/muted/accent semantics, retain an alias only where compatibility requires it,
and test contrast before deleting any specialized treatment. These are migration tasks, not values already applied.
Of the 20 roles, nine current Roof values match, ten differ and surface-raised is absent; **Stop is currently amber**. #171 changes no runtime palette.
Coordinate adapter migrations so a palette change does not silently recolour only some supported UIs.

## Role pairing and contrast

- Background/surface/surface-raised describe stacking, border frames and separators; text/muted describe values and
  secondary labels; accent titles/keys; focus selection and primary actions. Success/warning/danger/info are standalone
  status tones. Every state also has a word, icon or mark, never colour alone.
- `*-text` is a notice foreground **only with its matching `*-background`**. In particular, success-text,
  warning-text and info-text map to ANSI **Black**, for Green, Yellow and Cyan notice backgrounds respectively.
  Putting them alone on the page's Black fallback makes text disappear. Danger-text uses White on Red.
  The `notice_pairs` mapping is normative across all adapters.
- [RoofUiTheme's Stop feedback](../../src/HVO.RoofControllerV4.TerminalUi/RoofUiTheme.cs) currently uses InfoText,
  SuccessText and WarningText over page Background. During terminal migration, switch standalone feedback to
  info/success/warning, or use the complete notice pair. Test **Sent, both Acknowledged message cases,
  RelayUnverified and Failed** independently in ANSI-16 as well as truecolour/NO_COLOR. Copying new constants into
  those old schemes is unsafe.
- Aim for at least 4.5:1 for normal text and 3:1 for large text and identifying control/focus boundaries in actual
  rendered combinations. Border tone is a separator, not sufficient by itself to identify keyboard focus. Check
  overlays, opacity/disabled text, selected input values and notice bands against their actual backgrounds. All four exact truecolour notice pairs calculate above 5:1, and text on focus is about 4.90:1; this is arithmetic, not rendered validation. A named
  ANSI colour's RGB varies with terminal configuration: retain labels/reverse/underline and test both dark and light
  terminal defaults instead of claiming a fixed ANSI contrast ratio from an RGB calculation.
- Disabled actions retain readable labels and a reason; busy actions say what is happening. Errors identify the
  field/action and preserve safe correction/cancel. Read-only fields stay distinguishable from disabled and loading.

## Actions, focus and selection

`roof_actions` in the data names aliases of canonical roles, not a second colour list. Stop uses the danger notice
pair and a danger border to preserve the approved red treatment in every platform. Keep the shared
[RoofStopText](../../src/HVO.RoofControllerV4.Client/RoofStopText.cs) label, a stop icon/shape where available, and the
roof TUI's F9 cue. This is Roof's mapping of the approved red action direction onto existing canonical roles; Sky's table itself defines no Stop action role. Acknowledgement is separate from button colour. **Focus must retain the action's red identity**:
use a visible outline/underline and bold as appropriate. Do not copy Sky's generic button scheme, which turns the
focused button blue, onto Stop. It remains enabled during pending commands, stale feeds, questions and lock screens.

Open/Close use raised controls with labelled directional/status indicators as in the approved prototypes; they do not
reuse Stop's emergency emphasis. They can be disabled by the existing command-eligibility contract, with the reason
shown. No new confirmation is imposed on Stop. Ordinary primary actions and selected tabs/items use focus treatment;
focus and selection also have an outline, marker or current-page text. Web `:focus-visible`, Avalonia keyboard focus,
and terminal underline/reverse must survive disabled neighbours, overlays and every navigation path.

Sent means only that the request is in progress. Preserve the existing
[RoofStopText.Classify](../../src/HVO.RoofControllerV4.Client/RoofStopText.cs) outcome **and message**, including both
successful cases that share `RoofStopOutcome.Acknowledged`:

- A successful response with `RelayRegisterState.Verified` selects `RoofStopText.AcknowledgedVerified`, which reports
  verified de-energized relay-register evidence. Read-back proves the register, never the physical contacts.
- A successful response with missing or `Unknown` verification selects generic `RoofStopText.Acknowledged`, making
  no register-verification claim. This includes HTTP 200 with an unreadable body. The enum alone cannot distinguish
  these acknowledgement cases; do not infer verification from it or invent a new outcome.

`RelayUnverified` retains its shared acknowledged-but-unverified or sent-but-unverified wording, as classified from
the response/error. `Failed` retains the failure/no-acknowledgement wording. Neither can be promoted to verified success.
A good-looking button, camera frame, animation or feed reconnect is never proof of successful roof control.

## Geometry and information hierarchy

These are Roof layout requirements derived from the approved prototypes, not imported CSS pixels in every toolkit.
Use existing platform helpers; introduce only controls actually used by a migrated workflow.

| Concern | Web | Shared native | Terminal / installer |
| --- | --- | --- | --- |
| Base spacing | 4px unit; 8/12/16/20/24/28px gaps/padding as needed by prototype hierarchy | `KioskMetrics.Gap` and its multiples from real/configured pixels per mm | Whole cells; one cell inside frames, aligned label/value columns; continuation text under its value |
| Panels | 1px border; 8px panel radius; raised inputs/actions; no competing decoration | Bounded titled panels; reserve permanent Stop column and notice budget before content | Single-cell frames/rules, capitalized title/accent; no shadows/gradients/rounded faux panels |
| Controls | At least 48px touch actions; roof directions approximately 82px tall and Stop at least 62px in prototype; fit rather than copying fixed dashboard widths | Every touch target at least `KioskMetrics.MinimumTouchMillimetres` (12mm); never shrink target to make content fit | Bracketed actions; full labels and visible selection; controls measured in renderer cells |
| Type | Clear page title/status/action/detail levels; wrap/error association; keyboard zoom/reflow | Existing metrics text scale; long labels bounded with whole-text detail path; never reduce to unreadable type | Whole graphemes; CJK/emoji/combining-width coverage; do not slice a grapheme or count UTF-16 length as columns |
| Overflow | 320px and up; responsive stacking, reachable tables/editors; no horizontal page overflow | Kiosk **no page scrolling** at 1920x1200 provisional, 1280x720 and 800x480; use bounded subviews/pagination for unbounded content | 80x24 usable, 120x32 expands; bounded scrollable region with position/keys, never clip critical status/Stop outcome |

The owner's landscape 10-inch 1920x1200 display is provisional. Reconcile effective mode, rotation, scale and physical
metrics using the existing configuration/display model and headless fixtures. Do not infer 12mm from resolution
alone. Preserve smaller supported layouts and Mac resizable keyboard/window behavior. Current KioskShell uses a
ScrollViewer; no-page-scrolling is a **pending migration**, not an existing guarantee. PIN/commissioning and long
fault/offline notices must satisfy the same fit requirement as Roof. Mac may adapt desktop overflow while keeping
Stop visible; its rights do not inherit kiosk PIN restrictions.

## Terminal capability contract

Mirror the merged Sky semantics with Roof's own implementation in the terminal slice. Roof currently supports nonempty NO_COLOR and delegates colour quantization to Terminal.Gui; explicit ANSI16 emission, capability detection and complete ASCII mode are migration gaps:

| Mode | Detection / expectation |
| --- | --- |
| Truecolour | `COLORTERM=truecolor` or `24bit`; use the token `value`. |
| ANSI-16 | Default otherwise, including `TERM=xterm-256color` without COLORTERM; emit the exact named 16-colour fallbacks, not nearest 256-colour/24-bit escapes. `Gray` is standard white 7, `DarkGray` bright black 8, `White` bright white 15. |
| NO_COLOR | Nonempty `NO_COLOR`, or `TERM=dumb`: terminal defaults; bold titles/notices, underlined inputs/keys, reverse focus/selected/primary and faint disabled. Empty NO_COLOR is not a request. |
| ASCII | First nonempty `LC_ALL`, then `LC_CTYPE`, then `LANG` is not UTF-8, or TERM is dumb/vt100: `+`, `-`, `\|` frames, ASCII checkbox/choice/arrow equivalents. No hidden non-ASCII border/check glyph in a stock widget. |
| Explicit override | Mirror `HVO_TERMINAL=truecolor,ansi16,nocolor,ascii,unicode` comma-list semantics; nonempty NO_COLOR wins. Detection/override support is a future Roof implementation requirement, not an already shipped option. |

Every notice has `[INFO]`, `[OK]`, `[WARNING]` or `[ERROR]`/`[REFUSED]`; choices `(*)`/`( )`, toggles `[x]`/`[ ]`,
current tab brackets and labels keep all information without colour. Tab/Enter navigate/activate, Esc closes a prompt;
roof F1–F5, F9 across prompts and F10 retain existing quit/Stop semantics. Installer Enter advances, Esc goes Back,
and F10 cancels (exit 130), retaining its apply/cancel guards; the installer never becomes a roof operator.
Plain/redirected/JSON command output keeps
its contract and never gains mandatory ANSI escapes.

[Validation](validation.md) must prove rendered combinations, focus states and actual published artifacts. Neither
Sky's installer smoke nor prototype previews establish Roof runtime/prompt/RID coverage.
