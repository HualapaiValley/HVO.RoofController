# Terminal feature boundary

This project supplies Roof's terminal capability detection, Sky semantic adapter, cell geometry, bounded forms,
reading views, interactive plain-output emphasis and embedded third-party notices.
It is shared by the installer and `hvo-roof`; there is no shared theme package with SkyMonitor.

`Directory.Build.targets` excludes TextMateSharp 2.0.4, TextMateSharp.Grammars 2.0.4 and Onigwrap 1.0.11
from this project, its direct consumers and the in-process tests. This follows SkyMonitor's merged
[exclusion policy at bf5ef211](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26/Directory.Build.targets).
The pins match Terminal.Gui 2.5.0's closure. Markdig and other required dependencies remain.

Do not introduce Terminal.Gui `Code`, `Markdown`, `Wizard`, `WizardStep`, or a TextMate highlighter
while this policy applies. They initialize highlighting dependencies. Roof's `InstallerWizard` is
its own coordinator over ordinary views, including `Window`, `Label` and `ListView`. Compiled type
and assembly reference tests guard this boundary; runtime driver and published-file tests prove the
supported screens and commands remain usable without the excluded files.

Both programs expose `--third-party-notices` before configuration, authentication, network access
or machine changes. The notice document is an embedded resource, so single-file release copying,
installation and rollback preserve it. See [terminal distributions](../../docs/terminal-distributions.md)
for the pinned inventory, release gates and precise audit limits.

## Terminal contract

`RoofUiTheme.For(environment)` detects RGB only from `COLORTERM=truecolor`/`24bit`; otherwise it uses exact ANSI16.
Call `Prepare(app)` before creating controls: it sets driver `Force16Colors` and selects the toolkit's glyphs.
Nonempty `NO_COLOR` or `TERM=dumb` uses `Color.None` for terminal defaults. UTF-8 is detected from the first nonempty
`LC_ALL`, `LC_CTYPE`, then `LANG`; other locales and dumb/vt100 use ASCII. `HVO_TERMINAL` accepts comma-separated
`truecolor`, `ansi16`, `nocolor`, `ascii`, `unicode`, with nonempty `NO_COLOR` taking precedence.

RGB comes only from Client's `RoofUiPalette`; ANSI names implement [the pinned contract](../../docs/ui-redesign/tokens.json).
Stop uses the complete danger notice pair in normal/focused states. Result text uses standalone status roles over
Background, never the Black ANSI notice foreground alone. A success enum does not imply verified-register wording.

`TerminalCells` measures extended graphemes, preserving combining sequences, CJK, flags and joined emoji when wrapping
or aligning. `TerminalReadingView` wraps long output with hanging indents and Page Up/Down/Home/End. `TerminalForm`
keeps a bounded viewport and reveals focused fields. These are ordinary views, shared by the two real applications;
no highlighting, Markdown or toolkit Wizard path is introduced.

The [terminal delivery evidence](../../docs/ui-redesign/terminal-evidence.md) records the page/form/role matrix,
reproducible captures and final-distribution workflow gates. Installer Back/cancel/apply guards remain distinct from
roof F9/owned-motion quit semantics.
