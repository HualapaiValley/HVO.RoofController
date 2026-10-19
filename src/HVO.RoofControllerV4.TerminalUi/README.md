# Terminal feature boundary

This project supplies Roof's existing HVO Dark terminal theme and embedded third-party notices.
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
