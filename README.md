# HVO.RoofController

[![CI](https://github.com/RoySalisbury/HVO.RoofController/actions/workflows/ci.yml/badge.svg)](https://github.com/RoySalisbury/HVO.RoofController/actions/workflows/ci.yml)
[![iOS](https://github.com/RoySalisbury/HVO.RoofController/actions/workflows/ios.yml/badge.svg)](https://github.com/RoySalisbury/HVO.RoofController/actions/workflows/ios.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-Proprietary-red)

Roof Controller V4 for the HVO observatory — Raspberry Pi (Blazor SSR + API)
and iPad (.NET MAUI) applications.

## Features

- **Roof Automation** — full open/close control of the observatory roll-off roof
- **Safety Systems** — dead-man timer, limit switches, and fail-safe relay control
- **GPIO / I2C** — direct hardware interface on Raspberry Pi for motor and sensor management
- **iPad Control** — .NET MAUI companion app for touch-based roof operation
- **Docker Deployment** — containerized deployment to Raspberry Pi (linux-arm64)

## Projects

| Project | Description |
|---------|-------------|
| `HVO.RoofControllerV4.RPi` | ASP.NET Core web app for Raspberry Pi (GPIO/I2C roof control) |
| `HVO.RoofControllerV4.iPad` | .NET MAUI iPad client |
| `HVO.RoofControllerV4.Common` | Shared models and options |
| `HVO.WebSite.Themes` | CSS theme (Razor Class Library) |
| `HVO.RoofControllerV4.RPi.Tests` | Unit and integration tests |

## Quick Start

```bash
# Build
cd src
dotnet build HVO.RoofController.sln

# Test
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/
```

## Docker Deployment

Deploy the RPi server via Docker:

```bash
cd src
docker compose up --build
```

Target runtime: `linux-arm64` (Raspberry Pi)

## Dev Container

This repository includes Dev Container configurations in `.devcontainer/` for
a consistent development environment. Open in VS Code and use
**Reopen in Container** to get started.

## Documentation

| Document | Description |
|----------|-------------|
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guidelines and development workflow |
| [CHANGELOG.md](CHANGELOG.md) | Version history and release notes |
| [copilot-instructions.md](.github/copilot-instructions.md) | Architecture and coding standards |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards,
and pull request guidelines.

## License

This project is proprietary software. See [LICENSE](LICENSE) for details.
