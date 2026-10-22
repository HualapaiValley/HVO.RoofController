# CI Runners and Workflow Security

This repository is public. CI uses GitHub-hosted runners for the server tests, the emulated
commissioning scenarios, the Pi image build and the HAT emulator image. The former iOS/M5 workflows were removed with the native application.

## Workflows

| Workflow | Runner | Triggers | Purpose |
|---|---|---|---|
| `ci.yml` | GitHub-hosted `ubuntu-latest`, `ubuntu-24.04-arm` and `macos-latest` | push to `main`, pull requests, nightly, called by `release.yml` | Build and test the server graph (Debug with coverage, then Release with warnings as errors); the installer, the kiosk, the Mac app, `hvo-roof` on macOS, the deploy script, `install.sh` and the build scripts; and `build/secret-scan.py` over the files and every commit since the Blue Iris credential's (#22). The scenario, browser and soak categories run in `scenarios.yml`. The tests (`unit-tests`) and the programs' publishes (`publish-terminal`, `publish-apps`) run side by side; `build-and-test` passes only when all three succeeded |
| `scenarios.yml` | GitHub-hosted `ubuntu-latest` and `ubuntu-24.04-arm` | push to `main`, pull requests, nightly, manual, called by `release.yml` | The commissioning checks as emulated scenarios: the `Scenario` tests (a 90 s C14 soak included), the web UI in Chromium with phone, tablet and desktop emulation, and the container lifecycle, C12 deploy and rollback, a deploy of a pulled image, and Compose migration (`tests/emulator/deploy-scenarios.sh`), and a test rig set up, upgraded and rolled back by `hvo-roof-install` on x64 and arm64 (`tests/installer/rig-scenario.sh`). Nightly and on demand, the C14 soak runs for two hours and publishes its invariant results to the run summary and an artifact |
| `pi-image.yml` | GitHub-hosted `ubuntu-latest` | push and pull requests touching the Pi image inputs, weekly, manual | Check the [base image pins](#base-images), build the controller's image for `linux/arm64` (the Pi) and `linux/amd64` from a clean builder, and check each platform's version labels; nothing is pushed or deployed |
| `release.yml` | GitHub-hosted `ubuntu-latest` and `ubuntu-24.04-arm` | a `v*` tag, manual | A draft release: checks the tag against the version, runs `ci.yml` and `scenarios.yml`, pushes the images to GHCR and makes the assets, each image's offline archives included, in the `release` environment, then installs a rig from the draft end to end, from those archives with ghcr.io out of reach. A person checks and publishes it ([releasing.md](releasing.md)) |
| `release-latest.yml` | GitHub-hosted `ubuntu-latest` | a release published, manual | Gives the newest final release's images the `latest` tag |
| `emulator-image.yml` | GitHub-hosted `ubuntu-latest` | push and pull requests touching the emulator or controller image inputs, weekly, manual | Check the [base image pins](#base-images), build the HAT emulator image for `linux/amd64` and `linux/arm64`, and open and close the emulated roof through the containerized controller (`tests/emulator/compose-smoke-test.sh`); no hardware, nothing pushed or deployed |

The C14 results upload also keeps the flushed memory-diagnostic JSONL stream, runtime/build metadata and sanitized
Linux mapping snapshots, including partial-run evidence when the job times out. The existing summaries are still
written at the end of a completed run. File formats and interpretation are described in
[C14 commissioning](commissioning.md#c14-rv-6-soak); diagnostics do not change its pass criteria.

CI's `terminal-distributions` matrix runs the final release-assembled installer and CLI on Linux x64,
Linux arm64 and macOS arm64, after macOS signing. It checks offline notices, help/version, nonterminal
refusal and isolated PTY startup, and proves the notice document survives the actual installer copy
and rollback paths on fake machines. Manifest/dependency validation also runs in the production
release assembler. See [terminal distributions](terminal-distributions.md) for the inventory and bounds.

All workflows:

- declare `permissions: contents: read`;
- pin every action to a full commit SHA with a `# vX.Y.Z` comment (Dependabot updates the SHA
  and the comment together);
- run `dotnet` from `src/` so `src/global.json` selects the SDK.

## Jobs a pull request can skip

`ci.yml` and `scenarios.yml` start with a `changes` job: for a pull request into `main`,
[`build/ci-changes.py`](../build/ci-changes.py) diffs the pull request's base commit against the merge commit tested
(`git diff --name-status --no-renames`), and a job is skipped when every changed file is in a class of files it cannot
read. A required check that is skipped counts as passed, so the skip has to be sure. Three classes:

- **docs**: `docs/**` and the Markdown files at the top of the repository;
- **test code**: `tests/HVO.RoofControllerV4.RPi.Tests/**`;
- **a project**: `src/<project>/**`, for a project the job neither builds, runs nor copies into an image. It is worked
  out from the project references (and any other file a project file names outside its folder) and the Dockerfiles'
  `COPY` lines at the commit tested, so a new reference moves a project into a job's set in the same pull request.

| Job | Skipped when every change is in |
|---|---|
| `deploy-script` | test code, other than Markdown, with nothing deleted or renamed anywhere (its link check reads every link) |
| `install-sh-mac` | docs, test code |
| `installer-kiosk`, `installer-mac`, `mac-cli`, `terminal-distributions` | docs |
| `mac-app` | docs, test code, projects outside the Mac app's references (today all but Mac, Screens, Client and Common) |
| `scenarios`, `browser` | docs |
| `container-scenarios` | docs, test code, projects neither image builds (today Cli, Installer, Kiosk, LinuxAuth, Mac, Screens and TerminalUi) |
| `installer-rig` (both) | docs, test code, projects outside the images, the installer and `hvo-roof` (today LinuxAuth and Mac) |

Any other file (`.github/`, `build/`, `deploy/`, the root's build files, `src/`'s own files, the other `tests/`
folders, `research/`, ...) runs every job, as does a push, the nightly run, a release, a pull request into another
branch, or a diff the script cannot work out. `build-and-test` and `secret-scan` always run. `installer-rig` is a
matrix, and a matrix job skipped as a whole reports one check without its matrix values, which would leave both
required checks waiting: it runs instead and skips its steps.

The docs and test-code rules are about what the jobs' tests and scripts read, which the script cannot derive, so
`tests/ci/test_ci_changes.py` (run by `deploy-script`) fails when a test class that a skipping job selects names a
`docs/` path, when a script those jobs run names one, or when a file outside `src/` and the test project names the
test project. If a job has to start reading such a file, change its rule in `build/ci-changes.py` and this table in the
same pull request.

## Base images

The controller's and the HAT emulator's Dockerfiles pin each base image
(`mcr.microsoft.com/dotnet/sdk` and `mcr.microsoft.com/dotnet/aspnet`) by tag and by the digest
of its multi-platform index, as in `sdk:<tag>@sha256:...`, so a rebuild of the same commit
uses the same bases: a tag can be republished, a digest cannot. Section 2 of the
[HVO install and release contract](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/development/v1/docs/hvo-install-contract.md)
asks for this. The dev container follows the same tag and index-digest rule, using the fully
versioned `2.3.2-10.0-noble` tag (image version, .NET line and Ubuntu variant). The disposable
installer guest also pins its Ubuntu base by tag and multi-platform index digest.

[`build/check-base-images.sh`](../build/check-base-images.sh), which `pi-image.yml` and
`emulator-image.yml` and CI's "Check dev container SDK" step run with `--registry`, checks all
four Dockerfiles and requires that:

- every external `FROM` has a literal tag and a digest, exempting only `scratch` and
  references to earlier named stages in the same file (matched without regard to case);
- bases are extracted from logical instructions, joining continuations with the initial
  `# escape=` directive (backslash or backtick), ignoring full comment and empty continuation
  lines, and keeping heredoc bodies out of the instruction stream;
- the product SDK's tag satisfies `src/global.json`: the exact version, or a newer patch in
  the same feature band when `rollForward` is `patch`;
- both product Dockerfiles use the same SDK image and the same runtime image, tag and digest;
- every `dotnet-version` input in `.github/workflows/*.yml` equals the product SDK tag; inputs
  use literal, single-line version scalars (unsupported input syntax fails closed); CI also
  requires `dotnet --version` to equal that tag before building;
- with `--registry`, every distinct external reference across all four files is inspected once,
  including bases beyond the required SDK and runtime; the response must be one schema-version-2
  Docker manifest list or OCI image index (checked by media type), holding `linux/amd64` and
  `linux/arm64` entries.

Before parsing, the checker rejects any character outside printable ASCII plus TAB, LF and CR,
including in comments and heredoc bodies. This excludes all other control characters, Unicode
(including Unicode whitespace) and BOMs; errors give the file, line and code point without
printing the rejected source text. Invalid UTF-8 bytes are reported as surrogate code points
`U+DC80` through `U+DCFF`.

Directive recognition mirrors BuildKit v0.29.0's ASCII regex, its `syntax`/`escape`/`check` keys,
case-insensitive key names and case-sensitive values. Only the initial uninterrupted directive
prologue is active: a blank, ordinary comment, unknown directive or instruction ends it;
repeating an active directive is an error. As an additional fail-closed rule, header comments
mentioning `escape` or `syntax` must match a recognized directive exactly, with supported values,
even after an ordinary header comment has ended the active prologue.

The checker needs Python 3 and jq (plus Docker Buildx for `--registry`). It accepts the standard
Dockerfile frontend, with no syntax directive or `# syntax=docker/dockerfile:1`, and matches
instruction keywords without regard to case. It handles unquoted leading flags such as
`--platform=$BUILDPLATFORM`, and simple literal heredoc delimiters in `RUN`, `COPY` and `ADD`
(including quoted delimiters, multiple bodies and `<<-` tab stripping). It fails closed with
`file:line` on unsupported frontend or instruction syntax, quoted/escaped flags, image variables,
ambiguous heredoc headers (including `ONBUILD` heredocs), or unfinished continuations/bodies.
It checks base references, rather than validating every instruction's build-time semantics.

Dependabot ([`.github/dependabot.yml`](../.github/dependabot.yml)) updates a tag and its digest
together, in both Dockerfiles in one pull request, and proposes a new digest alone when Microsoft
republishes a tag (for base OS fixes). A pull request that moves the SDK's tag must update
`src/global.json` with it when the new tag leaves the allowed feature band.
The dev-container image may lag the product SDK, so `src/global.json` can retain a compatible
minimum with `rollForward: patch`. Raise that minimum only when the dev container includes it.
Every product SDK bump must also move all workflow SDK inputs; the gate rejects any mismatch,
including a later product-only patch. CI installs the product SDK in its own runner-temporary
directory so an older exact minimum cannot take precedence. The cloud session hook also selects
the product Dockerfile's SDK tag. To pin by hand, use the `Digest` that
`docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:<tag>` prints: the index's,
not a platform's.

## Retired M5 runner

Deleting the workflow does not unregister a runner or revoke its access. The repository
owner must check whether the M5 runner serves other projects, then remove this repository
from its runner group (or unregister the runner if no longer used). Revoke any dispatch
tokens and credentials that were dedicated to this repository. Until then, confirm the
runner cannot be targeted by other workflows or reach the roof controller network.

## GitHub settings the owner must confirm

Record the confirmed values in the private operations record, not here:

1. **Fork pull request approval.** Require approval for outside collaborators before running
   untrusted changes, even though retained workflows use hosted runners.
2. **Action pinning.** Keep the full-length SHA requirement enabled and limit allowed actions.
3. **Default workflow permissions.** Keep the `GITHUB_TOKEN` read-only by default and prevent
   workflows from approving pull requests.
4. **Runner retirement.** Confirm this repository no longer has access to the M5 runner,
   including through a shared organization runner group, and that any dedicated dispatch
   tokens and credentials were revoked.
5. **Network.** No untrusted runner can reach the roof controller or other observatory controls.
