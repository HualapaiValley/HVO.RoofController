# Terminal distributions and third-party notices

The standalone installer and CLI each carry their third-party notice document. Read it offline,
without credentials or a terminal:

```sh
hvo-roof --third-party-notices
hvo-roof-install --third-party-notices
```

The option is global and exits successfully before the command runs, including
`hvo-roof open --third-party-notices` or `hvo-roof-install uninstall --third-party-notices`.
It prints the same document on standard output; it does not read configuration, authorize, connect,
survey the machine, install or remove anything. Existing help, version and cancellation outcomes
are preserved. `--help` lists the option. The installer still uses F10 to cancel (130); Esc goes Back.

The document is embedded in `HVO.RoofControllerV4.TerminalUi.dll` inside each single-file executable.
Release packaging and the installer copy only the program file; this makes the notices accompany
both a standalone download and an installed or restored program without requiring a companion file.
The [notice document](../src/HVO.RoofControllerV4.TerminalUi/Notices/THIRD-PARTY-NOTICES.txt) preserves
upstream licence text (including its imported whitespace) and supplied copyright and author strings. This inventory is scoped to the
terminal programs, not a certification of whole-product legal compliance.

## Payload policy and inventory

Terminal.Gui's unused highlighting closure is excluded: TextMateSharp and TextMateSharp.Grammars
2.0.4, Onigwrap 1.0.11, and native `libonigwrap.so` / `libonigwrap.dylib`. Roof uses ordinary views,
including its custom installer coordinator. Terminal.Gui's `Code`, `Markdown`, `Wizard`, `WizardStep`
and TextMate highlighter remain outside the supported feature boundary. See the
[TerminalUi contract](../src/HVO.RoofControllerV4.TerminalUi/README.md).

The retained material was inventoried from decoded final `.deps.json` metadata and bundle entries.
[terminal-notices.json](../build/terminal-notices.json) records the exact package versions, authors,
licences, repository revisions and source SHA-256 values. The notice document includes:

- Terminal.Gui 2.5.0: MIT, Novell and Microsoft copyrights, package authors Miguel de Icaza,
  Tig Kindel and BDisp; [pinned licence](https://github.com/tui-cs/Terminal.Gui/blob/ece73d25c37df9bb17cb0c0d2171abd4d98eeaff/LICENSE).
- Wcwidth 4.0.1: MIT, Patrik Svensson and Phil Scott;
  [pinned licence](https://github.com/spectreconsole/wcwidth/blob/64a3f523d5dc27124e04ff9079137ac2c5c72143/LICENSE.md).
- Markdig 1.3.2: BSD-2-Clause, Alexandre Mutel; retained even though highlighting is excluded;
  [pinned licence](https://github.com/xoofx/markdig/blob/fc705234fa211d179ee1d5e7656b51ab99f70ca9/license.txt).
- ColorHelper 1.8.1: MIT, Artyom Gritsuk;
  [pinned licence](https://github.com/iamartyom/ColorHelper/blob/0b4574db8981ade5e45638d180ec094dd7d1eef9/LICENSE).
- JetBrains.Annotations 2026.2.0: MIT, JetBrains; both package copyright (2016–2025) and
  [licence copyright (2016–2024)](https://github.com/JetBrains/JetBrains.Annotations/blob/51456a32946d20b160c91b384e08ba6d4a5731e4/license.md)
  are retained as supplied.
- System.IO.Abstractions and TestableIO.System.IO.Abstractions / Wrappers 22.2.0: MIT,
  Tatham Oddie and contributors;
  [pinned licence](https://github.com/TestableIO/System.IO.Abstractions/blob/ec68061d1cf181d50f089f979301f874885bf0be/LICENSE).
- Testably.Abstractions.FileSystem.Interface 10.3.0: MIT, Valentin Breuß; package authors and
  Testably copyright also retained;
  [pinned licence](https://github.com/Testably/Testably.Abstractions/blob/9b967ee024caa2d47e3ac27a23d26ea55cd20ded/LICENSE).
- System.CommandLine 2.0.12, Microsoft SignalR/Extensions packages and self-contained .NET 10.0.13:
  Microsoft's MIT text plus every distinct supplied `THIRD-PARTY-NOTICES.TXT` in the retained
  packages and the three runtime packs. Package repository commits and exact source hashes are
  recorded in the inventory. Duplicate identical notice documents are included once; supplied
  third-party notices are preserved even when they cover a broader upstream distribution.

When updating dependencies or the runtime, review the actual payload and upstream notice material,
update the inventory and embedded document, and rerun the final-artifact checks. Unknown package
versions fail packaging so a dependency update cannot silently bypass this review. Preserve
upstream wording; do not derive legal text from a licence identifier alone.

## Verification

`build/dotnet_bundle.py` reads format 6.0 manifests using the pinned primary
[.NET runtime manifest format](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs)
and [entry format](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs).
It bounds file/member/count/string sizes, validates paths, types, duplicate and overlapping ranges,
metadata header locations and compressed streams, and never extracts to member paths or executes
programs. Malformed-input fixtures cover these boundaries, supported RIDs, signing trailers,
metadata-only dependencies and both native suffixes.

`build/release-assets.py` checks each **final copied** installer and CLI, after macOS signing, before
it writes release metadata/checksums. `build/terminal_payload.py DIST` inventories all six final
programs, requiring Terminal.Gui, Wcwidth and Markdig, rejecting excluded members and dependency
metadata, and requiring an inventoried notice entry for every shipped package version.
The current accepted closure permits **zero `NativeBinary` bundle entries**, irrespective of filename
or dependency metadata. Future native additions require an explicit payload-policy and applicable
notice review before this gate can admit them. Fixtures cover ordinary `.so` and `.dylib` additions,
including native content injected into the final copy produced by the production release assembler.

CI also exercises the complete release assembler with the six real terminal programs and inert
fixtures for unrelated products; it retains only the final terminal files from that test assembly.
A GitHub-hosted Linux x64, Linux arm64 and macOS arm64 matrix executes those exact files after signing:
help, version, full notice output, command termination by the notice option, nonterminal refusal,
and real PTY first-page startup at 80×24 and 120×32. HOME, configuration, cache, state, temporary,
working and extraction directories are private. An empty PATH disables host survey subprocesses;
no controller address or credential is configured. Smoke refuses root and installed Roof hosts.
It permits the installer's cancellation log, and asserts no installation, saved answers,
configuration or extracted payload was created. Child processes and waits are bounded.
Before runtime execution on macOS, `codesign --verify --strict` verifies **both final downloaded files**.
Their displayed identities must be exactly `io.github.hualapaivalley.hvo-roof` and
`io.github.hualapaivalley.hvo-roof-install`, each with `Signature=adhoc`. Verification and signature
details are retained with the per-host runtime evidence, including when a later check fails.

`TerminalInstalledNoticeTests` uses a fake machine and the actual CLI/installer program-copy steps,
including the kept-program swap used for rollback, then executes **only** `--third-party-notices`
on each installed copy. Existing wizard/CLI driver tests exercise pages, prompts, progress, failure,
Stop and safe quit in-process with the exclusions. The existing Linux/macOS published CLI tmux
smoke against the emulated controller remains in CI. Local Linux execution establishes only Linux
x64 runtime coverage; other RIDs run on their hosted supported machines.

## Confirmed historical omission and limits

The read-only #172 audit inspected all six public v4.1.1 installer/CLI assets, each matching GitHub
metadata and SHA256SUMS, with 232 validated bundle entries apiece. Every one carried the highlighting
assemblies and native wrapper. The Onigwrap package's supplied notice file and specific Oniguruma
disclaimer / Fluent Solutions copyright text were absent. Five grammar `cgmanifest.json` resources
did contain MIT text, so the finding is a specific distribution omission, not “no notices anywhere”.
See [issue #172](https://github.com/HualapaiValley/HVO.RoofController/issues/172) for source-pinned evidence.

This change applies to future builds/releases. Published v4.1.1 assets and tags are immutable.
Other release products, earlier versions and full-product legal compliance are outside this scoped
inventory. The independent kiosk and legacy-home cache purge requirements in
[#96](https://github.com/HualapaiValley/HVO.RoofController/issues/96) remain open, as does the broader
[#158 redesign](https://github.com/HualapaiValley/HVO.RoofController/issues/158).
