# Contributing to HVO.RoofController

Thank you for your interest in contributing! This document provides guidelines
and instructions for contributing to this project.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) matching `src/global.json`
  (10.0.401 or a later 10.0.4xx patch)
- [Docker](https://www.docker.com/) with Buildx (for building the Pi image)
- A code editor (VS Code with Dev Containers recommended)

## Getting Started

1. Clone the repository:

   ```bash
   git clone https://github.com/HualapaiValley/HVO.RoofController.git
   cd HVO.RoofController
   ```

2. Build the solution. Run `dotnet` from `src/` so `src/global.json` selects
   the SDK:

   ```bash
   cd src
   dotnet build HVO.RoofController.sln
   ```

3. Run tests, and build Release (warnings are errors in Release; CI checks both):

   ```bash
   dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
   dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj -c Release
   ```

   Coverage, as in CI:

   ```bash
   dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj \
     --settings coverage.runsettings --collect:"XPlat Code Coverage"
   ```

   The solution builds on Linux without Apple tooling.

4. Run the server locally against the HAT emulator (`http://localhost:5195`). Development
   uses the emulator, with the limit switches in force, so start it first, in its own terminal:

   ```bash
   dotnet run --project HVO.RoofControllerV4.Emulator
   dotnet run --project HVO.RoofControllerV4.RPi
   ```

   Protected endpoints need an API key; see [docs/security.md](docs/security.md). The
   emulator's fault-injection API, and how to run both in containers, are in
   [docs/emulator.md](docs/emulator.md).

## Development Workflow

### Branch Naming

Work on one issue per branch, from an up-to-date `main`. Name the branch
`<type>/<short-name>`, with the issue's number where it helps (`fix/installer-small-95`),
and the type one of:

- `feat/` — new features
- `fix/` — bug fixes
- `docs/` — documentation changes
- `refactor/` — code refactoring
- `test/` — test additions or changes
- `chore/` — maintenance, such as a version bump
- `release/` — a release's CHANGELOG section and upgrade notes
  ([docs/releasing.md](docs/releasing.md))

### Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

- `feat:` — a new feature
- `fix:` — a bug fix
- `docs:` — documentation only changes
- `refactor:` — code change that neither fixes a bug nor adds a feature
- `test:` — adding or updating tests
- `chore:` — maintenance tasks

### Pull Requests

- All changes must go through a pull request
- Say `Fixes #<n>` in the PR description, so that merging closes the issue
- A change a user or operator would notice adds its entry under `## [Unreleased]` in
  [CHANGELOG.md](CHANGELOG.md), in the same PR. A change only contributors see (tests, CI,
  contributor docs) needs none; say so in the PR
- Update the docs, and any screenshots, in the same PR as the behaviour they describe
- Ensure CI passes before requesting review
- PRs are merged into `main` with a merge commit (not squashed or rebased), and the branch
  is then deleted. To bring a branch up to date, merge `main` into it; never force-push
- The repository is public: run `build/secret-scan.py` before committing, pushing or posting
  an issue, PR or comment, and never commit a credential, key, PIN, token or private address
- Changes to relays, inputs, limits, the watchdog, the operator lease, the fault latch or
  the Stop path need tests. A change a commissioning check covers needs its scenario on the
  emulated plant, named with `[CommissioningCheck]` and listed in
  [docs/commissioning.md](docs/commissioning.md) with the installation assumptions it depends
  on. `ScenarioCoverageTests` fails when the two disagree. No test may rely on physical
  hardware.

### CI workflows

- Pin actions to a full commit SHA with a `# vX.Y.Z` comment; the repository rejects
  tag references.
- Keep `permissions: contents: read` at the top of each workflow.
- Keep CI on hosted runners; do not attach a self-hosted runner to a public-repository
  pull-request workflow. See [docs/ci-runners.md](docs/ci-runners.md).

### Releases

Releases are cut as [docs/releasing.md](docs/releasing.md) describes: a version bump PR, a
release PR (the CHANGELOG section and the upgrade notes), then a tag on `main`. A person
checks the draft release and publishes it.

## Coding Standards

See [AGENTS.md](AGENTS.md) for the architecture, the safety systems, coding standards and
the rules every change follows. It is written for coding agents, and applies to people too.

## Dev Container

This repository includes Dev Container configurations in `.devcontainer/`
for a consistent development environment. Open the repository in VS Code and
use the "Reopen in Container" command to get started.

- `devcontainer.json` is the standard configuration (no devices; run the server against the
  HAT emulator as in step 4 above).
- `devcontainer.rpi.json` runs on a Raspberry Pi with the real I2C bus and GPIO, so it can
  move the roof. It forces `IgnorePhysicalLimitSwitches=false`. Use it only with the roof
  mechanism isolated.
