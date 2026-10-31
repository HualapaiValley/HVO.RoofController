# ASCOM Alpaca

[ASCOM Alpaca](https://ascom-standards.org/api/) is the HTTP form of the ASCOM device interfaces that observatory
software (NINA, SGP, Voyager, ACP) uses. Issue [#220](https://github.com/HualapaiValley/HVO.RoofController/issues/220)
adds it to the roof controller in four phases, each opt-in:

| Phase | What | Where |
|---|---|---|
| 1 | A read-only Alpaca `Dome` for the roof, with `AbortSlew` (Stop), in a separate Alpaca process | its own pull request |
| 2 | **Reading other Alpaca devices** as safety sources: mounts' `Telescope.AtPark` and weather `SafetyMonitor.IsSafe`, and the weather alone for a weather-only `SafetyMonitor` | this page |
| 3 | `CloseShutter` | later |
| 4 | `OpenShutter` | later |

Our own API (`api/v4.0`), the user interfaces and the safety service work without any of it. This page covers phase 2.

- [Reading Alpaca devices](#reading-alpaca-devices)
- [When Open counts as Safe](#when-open-counts-as-safe)
- [Settings](#settings)
- [When a device cannot be read](#when-a-device-cannot-be-read)
- [The weather alone](#the-weather-alone)
- [Testing](#testing)
- [Decisions](#decisions)

## Reading Alpaca devices

The controller's safety service ([docs/safety-service.md](safety-service.md)) reads each configured device once per
`RoofSafety:PollInterval`, with `GET` requests of the Alpaca Device API only:

| Device | Requests, in order | Gives |
|---|---|---|
| Telescope (`RoofSafety:Telescopes`) | `connected`, `canpark`, `atpark` | whether the mount is parked, the only proof for now that it is clear of the roof |
| Safety monitor (`RoofSafety:WeatherSources`) | `connected`, `issafe` | whether the weather is safe |

- **Read only.** The controller never writes `Connected`, never calls `Connect`, `Disconnect`, `Park` or anything
  else, so it never takes a mount away from the program that drives it.
- **The address is pinned.** Each device is a configured server address and device number; the controller does no
  Alpaca discovery and never looks for a device elsewhere.
- **Plain HTTP or HTTPS, with no credentials.** A device server that needs a user name and password is not supported
  yet. The controller follows no redirect and uses no proxy.
- **Addresses are private.** They go in the Pi's local configuration, never in this repository, and the controller never
  logs or shows one: a reason names the device by its configured `Name`, and a failed read is described in fixed words
  (below), never with the device's own message or the HTTP client's (which names the host and port), at any log level.
  The controller's trace export leaves the polls out.
- Each request carries the Alpaca `ClientID` (chosen at start, 1 to 65535) and a `ClientTransactionID` counting from 1,
  as the Alpaca API asks.
- **Never configure the controller's own SafetyMonitor as a weather source.** It publishes the weather the controller
  reads, so the controller would read itself: it fails safe (its weather stays unsafe), but it can never be Safe.

## When Open counts as Safe

The owner's rule (#220, 2026-10-30): **Open counts as Safe only when at least one fresh weather source is reporting
and every scope that can block the roof shows it is clear of the roof.**

- Each weather source is set `Required` (the default) or `Ignored`. Every required one must have a recent reading that
  says safe. With no required weather source the conditions are Unknown, "No required weather source configured",
  however the telescopes stand.
- Each telescope has `BlocksRoof` (true by default). For one that can block the roof, the safety service asks "is this
  scope clear of the roof?". Today parked is the only proof; a camera that sees where the mounts point, or safe zones
  in right ascension and declination, can prove it later, in the one place the service decides it
  (`TelescopeRoofClearance`).
- A source set `Ignored`, and a telescope with `BlocksRoof: false`, are read and shown with their state and reason
  (marked `[ignored]` or `[does not block the roof]` on every interface), but never make the conditions Safe, Unsafe or
  Unknown.

What Open does with the conditions is the interlock's (`RoofSafety:InterlockMode`, Warn by default), unchanged:
[The Open interlock](safety-service.md#the-open-interlock). Close and Stop are never gated, and the conditions never
move or stop the roof.

## Settings

Under `RoofSafety`, beside the service's own settings ([Settings](safety-service.md#settings)). The section is not in
the settings catalogue, so neither the API nor the settings file holds it: on the Pi, write each setting as a file in
the secrets directory, one per setting, as for `BlueIris__BaseUrl`
([deployment.md](deployment.md#1-secrets-directory)); elsewhere, environment variables or `appsettings.json`. The
controller reads the devices at start: a change takes a restart. A setting it cannot use stops it at start, named by
its key, never by the address it holds: a value that is not of its type (`BlocksRoof: tru`, a `DeviceNumber` that is
not a number, `Requirement: Requred`) and a key no setting has (`DeviceNumer`) as much as a bad address, so a typo can
never drop a device and leave the rest to decide.

| Setting | Default | Meaning |
|---|---|---|
| `Telescopes:N:Name`, `WeatherSources:N:Name` | | The name the reasons quote, 1 to 64 characters. Each device of a kind needs its own. |
| `…:Url` | | The Alpaca device server: `http` or `https`, host and port (`http://192.0.2.10:11111`), with a path only when a proxy puts the Alpaca API under one. No credentials, query or fragment. |
| `…:DeviceNumber` | `0` | The device's number on that server. |
| `…:MaxReadingAge` | the service's | How old its reading may be and still count (longer than `PollInterval` plus `ReadTimeout`, at most 1 h). |
| `Telescopes:N:BlocksRoof` | `true` | Whether the mount can block the roof, and so must be clear of it. |
| `WeatherSources:N:Requirement` | `Required` | `Required` or `Ignored`. |

At most 16 devices of each kind. For example, on the Pi (with documentation addresses; the directory is root's, mode
700, and each file should be mode 600):

```bash
printf '%s' 'Main' | sudo tee /etc/hvo-roof/secrets/RoofSafety__Telescopes__0__Name >/dev/null
printf '%s' 'http://192.0.2.10:11111' | sudo tee /etc/hvo-roof/secrets/RoofSafety__Telescopes__0__Url >/dev/null
printf '%s' 'Cloud sensor' | sudo tee /etc/hvo-roof/secrets/RoofSafety__WeatherSources__0__Name >/dev/null
printf '%s' 'http://192.0.2.11:11111' | sudo tee /etc/hvo-roof/secrets/RoofSafety__WeatherSources__0__Url >/dev/null
printf '%s' '00:05:00' | sudo tee /etc/hvo-roof/secrets/RoofSafety__WeatherSources__0__MaxReadingAge >/dev/null
sudo sh -c 'chmod 600 /etc/hvo-roof/secrets/RoofSafety__*'
```

Then restart the controller (redeploy, or `POST /api/v4.0/System/Restart`).

or, in a JSON configuration file:

```json
{
  "RoofSafety": {
    "Telescopes": [
      { "Name": "Main", "Url": "http://192.0.2.10:11111", "DeviceNumber": 0 },
      { "Name": "Solar", "Url": "http://192.0.2.10:11111", "DeviceNumber": 1, "BlocksRoof": false }
    ],
    "WeatherSources": [
      { "Name": "Cloud sensor", "Url": "http://192.0.2.11:11111", "MaxReadingAge": "00:05:00" },
      { "Name": "Old sensor", "Url": "http://192.0.2.12:11111", "Requirement": "Ignored" }
    ]
  }
}
```

## When a device cannot be read

A read that is not a clean answer is a failed read, never a value: only an unambiguous success counts, `ErrorNumber` 0
with an empty `ErrorMessage` (the Alpaca API's success, both halves checked), every field once, and the request's own
`ClientTransactionID` echoed. The service keeps the last reading until it is
older than its `MaxReadingAge`, then the source is Unknown ([Staleness and failed reads](safety-service.md#staleness-and-failed-reads)).
The reason says why, in these words:

| The device | The reason's words |
|---|---|
| answers `Connected` false | `the device is not connected` |
| answers an Alpaca error (`ErrorNumber` not 0) | `the device is not connected (Alpaca error 0x407)`, `the device reports Alpaca error 0x500 (driver error)`, … |
| is a mount that cannot park (`CanPark` false) | `configuration error: the mount reports it cannot park (CanPark is false)` |
| is a mount without `AtPark` (`0x400`) | `configuration error: the mount does not implement AtPark` |
| cannot be reached | `the Alpaca server could not be reached (connection refused)`, `(host unreachable)`, `(host name not found)`, … |
| answers an HTTP error | `the Alpaca server answered HTTP 500` |
| answers something that is not the Alpaca JSON | `the Alpaca server's answer is not JSON`, `… has no ErrorNumber`, `… has no ErrorMessage`, `… has no ServerTransactionID`, `… has no true or false Value`, `… is too long to be an Alpaca answer` |
| answers ambiguously | `the Alpaca server's answer repeats a field` (one field twice, whatever its case), `… reports success with an error message` (`ErrorNumber` 0 with a non-empty `ErrorMessage`), `… is not the answer to this request` (a `ClientTransactionID` other than the one sent: a cached or replayed answer) |
| does not answer within `ReadTimeout` | `no answer within 5 s` |

For example: `Telescope 'Main' cannot be read (the device is not connected)`, or, once its last reading is too old,
`Telescope 'Main': the last reading (parked) is older than 60 s; the last read failed (no answer within 5 s)`.

A mount that cannot park can never show it is clear of the roof, so it is a configuration error, not "not parked". The
controller starts whatever the devices say (a device may be off at the time), and logs each failure when it starts.

## The weather alone

Besides the conditions, every status and `GET /api/v4.0/Safety` carry the weather alone, with no telescope:

| Field | Meaning |
|---|---|
| `weatherState` | `Safe` only when at least one weather source is required and every required one has a recent reading that says safe; `Unsafe` when a required one says unsafe; otherwise `Unknown`. |
| `weatherReasons` | Why it is not Safe, in the same words as `reasons`, or `No required weather source configured`. |

It is what the controller's Alpaca `SafetyMonitor` is to publish (`IsSafe` true only while `weatherState` is `Safe`;
Unknown counts as unsafe). It must not include telescope park: NINA and Voyager park the mount and close the roof
when their safety monitor turns unsafe, so a monitor that turned unsafe whenever a mount unparked would end every
session. The `SafetyMonitor` device itself belongs in the Alpaca process that phase 1 adds, as one more device beside
the `Dome`, reading `weatherState` from the controller's status; until that process exists, the weather alone is on
the API only.

## Testing

No test relies on a real device. The HAT emulator serves fake Alpaca mounts and safety monitors, modelled on the Alpaca
API, with their park, weather, connection and failure states set through its control API
([docs/emulator.md](emulator.md#alpaca-devices)); both compose files point the controller at them. ASCOM ConformU
checks the fakes themselves (the results are in that section).

| Tests | Cover |
|---|---|
| `Safety/AlpacaSafetySourceTests` | The adapters against the emulator's devices and against answers no device should give: GET only, the order of the requests, the transaction numbers, every failure's words, a mount that cannot park, a timeout, an unreachable server, and no address in a failure or its inner exception. |
| `Safety/RoofSafetyServiceTests` | The readiness rule (a required weather source, every blocking scope clear, `Ignored` and `BlocksRoof: false`), the weather alone, the settings' validation, the start log. |
| `Emulation/EmulatedAlpacaTests` | The emulator's devices against the Alpaca API: the answer's shape, parameter names (any case in a `GET`, exact in a `PUT`), form-encoded `PUT`s, HTTP 400, `0x407` while not connected, a mount that cannot park, the fault injection, the control API. |
| `Controllers/SafetyAlpacaApiTests` | The controller with configured devices, through its real pipeline: the conditions on the status, what Open needs, Close and Stop never gated, the conditions never moving the roof, and no address in the log at any level. |
| `Scenarios/AlpacaSafetySourceScenarios` | The emulated plant with the production settings and the default Warn interlock, the devices over a real socket: unsafe weather refuses Open and moves nothing, weather turning unsafe while the roof opens stops nothing, Close is never gated, and a mount that stops answering asks Open for the override. |
| `tests/emulator/compose-smoke-test.sh` | The containers: the conditions follow the emulator's devices, and the Warn interlock refuses Open while the weather is unsafe. |

## Decisions

From the owner (#220, 2026-10-30):

1. **Open readiness** (Q11): Safe only when at least one fresh weather source reports and every scope that can block
   the roof is clear. Each weather source is configured and set `Required` or `Ignored`.
2. **A scope's "blocks roof" setting** (Q9): the check asks whether the scope is clear of the roof, not whether it is
   parked. Parked is the only proof today; a camera or safe zones can show it later.

From the proposal's reviews, kept here:

3. The consumer only reads, never writes another program's device, and pins each device's address (re-discovery by
   UniqueID can be spoofed).
4. `ObservingConditions` is deferred: it needs per-sensor thresholds, hysteresis and freshness of its own.
5. The `SafetyMonitor` the controller publishes is weather-only.
