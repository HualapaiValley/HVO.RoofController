# Published terminal payload baseline

Read-only research completed 2026-10-19 for #171/#158; Roof source baseline
`2e4f1fbfac1216d508fc47913179e3c9b0a4cfb3`. This durable summary records the supplied parsed-artifact audit, not an
execution test by the foundation. It scopes the active [payload-fix task #172](https://github.com/HualapaiValley/HVO.RoofController/issues/172), coordinated with
[#96](https://github.com/HualapaiValley/HVO.RoofController/issues/96); #171 changes no packaging/runtime behavior.

## Verified release inventory

The inspected [v4.1.1 release](https://github.com/HualapaiValley/HVO.RoofController/releases/tag/v4.1.1), published
2026-10-16, points at
[`400622ccc00d8306636e3876b3fd328e9a784de8`](https://github.com/HualapaiValley/HVO.RoofController/commit/400622ccc00d8306636e3876b3fd328e9a784de8).
It predates the source baseline. All six downloaded asset sizes/hashes matched both GitHub asset metadata and the
[shipped SHA256SUMS](https://github.com/HualapaiValley/HVO.RoofController/releases/download/v4.1.1/SHA256SUMS).

| Release asset | Bytes | SHA-256 |
| --- | ---: | --- |
| hvo-roof-install-linux-x64 | 52,047,874 | `963681f9552ab21d728074db5bb5cbffbc8cff53d910c690be968586dfc568eb` |
| hvo-roof-linux-x64 | 50,942,988 | `81300db773c487f8fd7316fc5ad831ad9f686eed18f1e594f0007a206e59d043` |
| hvo-roof-install-linux-arm64 | 49,088,133 | `ce4845a367e3aba7f56f1bfde151829f431c2a70cf0378914bb4a26603079503` |
| hvo-roof-linux-arm64 | 48,032,684 | `11c6b6e4790745a712f5d6dd5532856a39feab3e6718d3024a892285a1263c96` |
| hvo-roof-install-osx-arm64 | 48,332,768 | `d04604b49eb57592a8be805de1d4253dc68ab5c9b9d511411fabf92a8fb65160` |
| hvo-roof-osx-arm64 | 47,272,064 | `fd4e72815ed21afbea97091dbeb28f8a2cbdde6c96c5214924b7862eb5084df1` |

Each parsed .NET bundle format 6.0 manifest has **232 entries**, including Terminal.Gui 2.5.0, Wcwidth,
TextMateSharp/Grammars 2.0.4 and Onigwrap 1.0.11. Each includes the native wrapper: Linux `libonigwrap.so` or macOS
`libonigwrap.dylib`, its only NativeBinary manifest entry. This is actual shipped content, not a package-reference
inference. The highlighting closure occupies roughly 7.45–7.48 MB unpacked / 1.13–1.15 MB stored per binary.

The audit validated member paths/ranges/types, duplicate names and overlaps before decoding content. Its parser follows
the primary .NET
[Manifest](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs),
[FileEntry](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs)
and [Bundler](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Bundler.cs)
formats. Managed resource enumeration followed ECMA-335 II.22.24/II.24. Downloaded binaries were never executed or
extracted to member-controlled paths. Raw binaries/manifests are deliberately not committed with this foundation;
the immutable release identity/hashes and method above let the payload task reproduce and retain its own audit evidence.

## Verified specific notice omission and limits

The supplied Onigwrap
[THIRD-PARTY-NOTICES.TXT](https://github.com/aikawayataro/Onigwrap/blob/dd8a1cbbe3190c18033b96f2922b95c1761ae793/THIRD-PARTY-NOTICES.TXT)
is absent as a file from each inspected distribution. Its specific Oniguruma disclaimer and Fluent Solutions copyright
were absent from raw binaries and **all decoded members**, checking ASCII and UTF-16LE. No separate notice release
asset was present. This confirms omission of that supplied notice material in these six v4.1.1 distributions.

**Do not generalize this to “no notices anywhere.”** Five embedded grammar `cgmanifest.json` resources contain MIT
permission material, and JetBrains.Annotations has an MIT clause. This is neither exhaustive author-attribution
coverage nor certification of legal compliance for the whole release, other products or future builds.

## Pending implementation and validation (#172)

Source inspection found no direct use of Terminal.Gui Code/Markdown/Wizard/WizardStep in Roof's two terminal programs
and shared theme. Roof's [InstallerWizard](../../src/HVO.RoofControllerV4.Installer/Wizard/InstallerWizard.cs) is its own
Window-based coordinator, and [ReadingView](../../src/HVO.RoofControllerV4.Installer/Wizard/ReadingView.cs) a ListView.
That supports investigating scoped highlighting exclusions; it does not prove runtime success without that closure.
Current-main publishing, post-exclusion runtime behavior and complete remaining-notice delivery **were not tested**.

Use merged Sky
[exclusions](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26/Directory.Build.targets#L25)
as a bounded reference, preserving required dependencies. Sky's
[published installer smoke](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/bf5ef211fa66e76dd83ac171a3fea944b6d5dc26/scripts/lib/published-installer-terminal.py)
covers help/no-TTY/first page at two sizes with raw filename checks; it does not prove Roof CLI/every RID or a complete
bundle inventory. Retain Roof's existing Linux/macOS published CLI terminal coverage.

The [delivery obligations](delivery.md#published-terminal-payload-and-notices) require final-binary manifests and
dependency checks for both programs/all three RIDs, native isolated PTY smoke, every page/prompt via fakes/drivers,
and applicable notices surviving release assembly **and installed distribution**. If material remains required, ship
its notices, including relevant grammar/author material. Even after highlighting exclusion, audit actual remaining
Terminal.Gui/Wcwidth/Markdig and other dependencies. [Release copying](../../build/release-assets.py) and
[CI artifact paths](../../.github/workflows/ci.yml) currently carry standalone executables: a loose publish-folder
notice does not prove it ships or is installed.

Fix future builds/releases only. Published v4.1.1 assets/tags remain immutable. The kiosk's required native libraries
and kiosk/legacy-home cache purge/uninstall requirements remain independent #96 work.

Historical inventory/omission research is complete. Correction and runtime/publish/notice-delivery validation belong to
#172 and remain pending; its completion does not close #158 or the independent #96 cache-lifecycle requirements.
