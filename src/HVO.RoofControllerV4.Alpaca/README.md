# HVO.RoofControllerV4.Alpaca

The roof's ASCOM Alpaca server (#220): a process of its own that serves the roof as Alpaca Dome 0 to astronomy
software, through the controller's REST API and status hub (the [client library](../HVO.RoofControllerV4.Client/README.md))
with its own Viewer API key. It is off by default. Phase 1 reads the roof and stops it (`AbortSlew`); it never opens or
closes it.

How it behaves, its error numbers, security, settings, discovery and the ConformU check: [docs/alpaca.md](../../docs/alpaca.md).

| Folder | What is there |
|---|---|
| `Program.cs`, `AlpacaOptions.cs` | The host, its settings (`Alpaca__*`) and their checks, the key file, `--discovery-only` |
| `AlpacaEndpoints.cs`, `AlpacaSetupPage.cs` | The management API, the device routes and the read-only setup pages |
| `Protocol/` | The request parameters, the response envelope, transaction numbers and the error table |
| `Devices/` | Dome 0's members, the shutter mapping, the per-client connections and the `UniqueID` store |
| `Roof/` | The roof as the server knows it: the status hub, merged with Stop's answers, and Stop |
| `Security/` | The address allowlist, the `Host` and `Origin` checks |
| `Discovery/` | The UDP discovery responder |

Tests: `tests/HVO.RoofControllerV4.RPi.Tests/Alpaca` (contract, unit and emulated-plant) and `tests/alpaca` (ConformU).
