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

Use descriptive branch names with the following prefixes:

- `feature/` — new features
- `bugfix/` — bug fixes
- `docs/` — documentation changes
- `refactor/` — code refactoring
- `test/` — test additions or changes

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
- PRs are squash-merged into `main`
- Link the relevant issue in the PR description
- Ensure CI passes before requesting review
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

## Coding Standards

See [`.github/copilot-instructions.md`](.github/copilot-instructions.md) for
detailed coding standards and architectural guidelines.

## Dev Container

This repository includes Dev Container configurations in `.devcontainer/`
for a consistent development environment. Open the repository in VS Code and
use the "Reopen in Container" command to get started.

- `devcontainer.json` is the standard configuration (no devices; run the server against the
  HAT emulator as in step 4 above).
- `devcontainer.rpi.json` runs on a Raspberry Pi with the real I2C bus and GPIO, so it can
  move the roof. It forces `IgnorePhysicalLimitSwitches=false`. Use it only with the roof
  mechanism isolated.
