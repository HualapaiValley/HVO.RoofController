# Copilot Instructions — HVO.RoofController

## Project Overview

HVO.RoofController is the observatory roof automation system for HVO. It
consists of a Raspberry Pi server application and an iPad companion client for
remote roof control.

### Architecture

- **RPi Server** — ASP.NET Core + Blazor Server (SSR) application that manages
  GPIO and I2C hardware for roof motor control, limit switches, and relay
  management. Deployed via Docker to a Raspberry Pi (linux-arm64).
- **iPad Client** — .NET MAUI application providing a touch-optimized interface
  for monitoring and controlling the roof from the observatory floor.
- **Common Library** — Shared models, options, and DTOs used by both the RPi
  server and iPad client.
- **WebSite.Themes** — Razor Class Library providing shared CSS themes.

### Key Safety Systems

- Dead-man timer — roof movement stops automatically if the control signal is
  not continuously refreshed
- Limit switches — hardware stops at fully open and fully closed positions
- Relay control — fail-safe relay configuration for motor direction and power

### Technology Stack

- .NET 9 (RPi server and shared libraries)
- .NET 9 MAUI (iPad client)
- ASP.NET Core + Blazor Server (SSR)
- GPIO / I2C hardware interfaces
- Docker (RPi deployment)

### Dependencies

- `HVO.Core` — shared foundation library (NuGet from HVO.SDK)
- `HVO.Core.SourceGenerators` — compile-time code generation (NuGet from HVO.SDK)
- `HVO.Iot.Devices` — IoT device abstractions (NuGet from HVO.SDK)

## Solution Structure

```text
src/
  HVO.RoofControllerV4.RPi/         # Raspberry Pi server
  HVO.RoofControllerV4.iPad/        # iPad MAUI client
  HVO.RoofControllerV4.Common/      # Shared models
  HVO.WebSite.Themes/               # CSS theme RCL
  HVO.RoofController.sln            # Solution file
tests/
  HVO.RoofControllerV4.RPi.Tests/   # Unit and integration tests
```

## Coding Standards

- Follow existing code style and patterns in the repository
- Use `Directory.Build.props` and `Directory.Packages.props` for centralized
  package management
- All public APIs must have XML documentation comments
- Keep controllers thin — business logic belongs in services
- Use dependency injection throughout
- Hardware abstractions must be mockable for testing

## Build and Test

```bash
cd src
dotnet build HVO.RoofController.sln
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/
```

## Deployment

The RPi server is deployed via Docker:

```bash
cd src
docker compose up --build
```

Target runtime: `linux-arm64` (Raspberry Pi)

## CI/CD

- `ci.yml` — runs on ubuntu, builds the solution and executes all tests
- `ios.yml` — runs on macos-15, builds the iPad MAUI application

## Issue and PR Workflow

- Reference the issue number in branch names and PR descriptions
- Use conventional commit messages
- All PRs are squash-merged into `main`

## Dev Container

Use the provided Dev Container configuration for a consistent development
environment. Do not install additional tools or extensions into the Dev
Container without updating the configuration files.
