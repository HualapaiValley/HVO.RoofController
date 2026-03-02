# HVO.RoofController

Roof Controller V4 for the HVO observatory — Raspberry Pi (Blazor SSR + API) and iPad (.NET MAUI) applications.

## Projects

| Project | Description |
|---------|-------------|
| `HVO.RoofControllerV4.RPi` | ASP.NET Core web app for Raspberry Pi (GPIO/I2C roof control) |
| `HVO.RoofControllerV4.iPad` | .NET MAUI iPad client |
| `HVO.RoofControllerV4.Common` | Shared models and options |
| `HVO.WebSite.Themes` | CSS theme (Razor Class Library) |
| `HVO.RoofControllerV4.RPi.Tests` | Unit and integration tests |

## Build

```bash
cd src
dotnet build HVO.RoofController.sln
dotnet test ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj
```

## Docker (Raspberry Pi)

```bash
cd src
docker compose up --build
```
