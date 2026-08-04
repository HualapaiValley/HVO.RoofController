# Contributing to HVO.RoofController

Thank you for your interest in contributing! This document provides guidelines
and instructions for contributing to this project.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/) (for RPi deployment)
- A code editor (VS Code with Dev Containers recommended)

## Getting Started

1. Clone the repository:

   ```bash
   git clone https://github.com/RoySalisbury/HVO.RoofController.git
   cd HVO.RoofController
   ```

2. Build the portable server graph:

   ```bash
   cd src
   dotnet build ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
   ```

3. Run tests:

   ```bash
   dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/
   ```

   The full solution includes the `net10.0-ios` project and requires macOS, the MAUI workload, and a supported Xcode version.

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

## Coding Standards

See [`.github/copilot-instructions.md`](.github/copilot-instructions.md) for
detailed coding standards and architectural guidelines.

## Dev Container

This repository includes a Dev Container configuration in `.devcontainer/`
for a consistent development environment. Open the repository in VS Code and
use the "Reopen in Container" command to get started.
