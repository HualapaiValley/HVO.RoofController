# Safety conditions: telescope park and weather

The controller's safety service says whether the observatory's conditions allow the roof to open: every telescope
parked, and the weather safe. It reports **Safe**, **Unsafe** or **Unknown** with the reasons, on every status
snapshot, on the status hub and at `GET /api/v4.0/Safety`, and logs every change. An optional interlock lets the
conditions refuse Open.

This is separate from the controller's own safety systems (the watchdog, the limit switches, the fault latch, relay
read-back; see [Safety behavior](../README.md#safety-behavior)). Those apply whatever the conditions say, and the
conditions never stop or move the roof.

It is issue [#207](https://github.com/HualapaiValley/HVO.RoofController/issues/207)'s foundation: the service, its
source interfaces, the API, the interlock and the indicators. **No real source exists yet.** Until one is added, the
conditions are Unknown, "No safety sources configured", and with the interlock off (the default) Open behaves as it
always has.

- [What it reports](#what-it-reports)
- [Sources](#sources)
- [Staleness and failed reads](#staleness-and-failed-reads)
- [The Open interlock](#the-open-interlock)
- [Settings](#settings)
- [Where it shows](#where-it-shows)
- [The log](#the-log)
- [Testing](#testing)
- [Future source adapters](#future-source-adapters)
- [Decisions and open questions](#decisions-and-open-questions)

## What it reports

Each source is judged on its own, then the sources are combined:

| A source is | When |
|---|---|
| Safe | Its last reading is recent enough and says safe (the telescope parked, the weather safe). |
| Unsafe | Its last reading is recent enough and says unsafe (the telescope not parked, the weather unsafe). |
| Unknown | It has not been read yet, it cannot be read, or its last reading is too old, whatever that reading said. |

The conditions are **Unsafe** when any source is Unsafe; otherwise **Unknown** when there is no source or any source is
Unknown; otherwise **Safe**. Unknown is never read as safe.

`RoofSafetyStatus` (Common, `RoofStatusResponse.Safety` and `GET Safety`):

| Field | Meaning |
|---|---|
| `state` | `Safe`, `Unsafe` or `Unknown`. |
| `reasons` | Why it is not Safe: the Unsafe sources' reasons first, then the Unknown ones', or `No safety sources configured`. Empty when Safe. |
| `sources[]` | Each source: `name`, `kind` (`TelescopePark` or `Weather`), `state`, `reason`, `detail` (extra words from the source, at most 200 characters), `lastReadingUtc`, `isStale`, `maxReadingAgeSeconds`. |
| `stateSinceUtc` | When `state` last changed. |
| `interlockMode` | `Off`, `Warn` or `Block` ([The Open interlock](#the-open-interlock)). |
| `overrideRole` | In Warn mode, the role an override needs: `RoofOperator` (anyone who may open, the default) or `RoofAdmin`; null otherwise. |

The reasons are written once, by the controller, and every client shows them as they are, for example:

```text
Telescope 'Main' is not parked
Weather monitor 'Cloud sensor' reports unsafe weather
Telescope 'Main': the last reading (parked) is older than 60 s
Weather monitor 'Cloud sensor' cannot be read
Telescope 'Main' has not been read yet
No safety sources configured
```

The reasons do not change from one read to the next, so a change of reasons is a real change, and the log records it.
A source's `detail` (a sensor value, say) can change with every read, and is not part of the state.

## Sources

The source interfaces are in `HVO.RoofControllerV4.Common.Safety`, so the simulation can implement them:

| Interface | One reading | Future adapter |
|---|---|---|
| `ITelescopeParkSource` | `TelescopeParkReading(IsParked, ObservedUtc?, Detail?)` | ASCOM Alpaca `Telescope.AtPark`, one per mount |
| `IWeatherSafetySource` | `WeatherSafetyReading(IsSafe, ObservedUtc?, Detail?)` | ASCOM Alpaca `SafetyMonitor.IsSafe`, or `ObservingConditions` judged against thresholds |

Both extend `IRoofSafetySource`: a `Name` that the reasons quote, and an optional `MaxReadingAge` of the source's own.
An adapter reads its device when asked and throws when it cannot; it polls nothing and caches nothing, since the
service does both. A source is registered as an `ITelescopeParkSource` or `IWeatherSafetySource` singleton; the
service reads every one registered, in the order registered.

`ObservedUtc` is for a device that dates its observation (Alpaca `ObservingConditions.TimeSinceLastUpdate`, say): an
earlier time makes the reading that much older, and a time in the future counts as now. Without it, the reading counts
from when the read ended.

## Staleness and failed reads

`RoofSafetyService` (`src/HVO.RoofControllerV4.RPi/Safety/`) is a hosted service. It reads every source at start,
all at once, and again `PollInterval` after each round ends:

- Each read runs on the thread pool and is cancelled after `ReadTimeout`, so an adapter that blocks holds up only itself.
- A source whose last read has not ended (an adapter that ignores its cancellation) is not read again until it does; the
  attempt counts as a failed read.
- A failed or timed-out read keeps the last reading. That reading counts until it is older than the source's
  `MaxReadingAge` (or the service's), and the source is then Unknown, stale, with what it last said in the reason.
- A source never read successfully is Unknown: "has not been read yet", or "cannot be read" once a read has failed.
- Ages are measured on the monotonic clock, so a wall-clock step (NTP, a Pi without a clock battery) neither revives an
  old reading nor ages a new one.
- The state is judged against the clock each time it is asked for (each status, each hub message, `GET Safety`, each
  Open), so a reading counts as stale as soon as it is too old, not at the next read.

`MaxReadingAge` must be longer than `PollInterval` plus `ReadTimeout`, the longest gap between two reads, or a reading
could go stale before the next one; the controller refuses to start with such settings, and warns at start of a source
whose own `MaxReadingAge` is that short.

## The Open interlock

`RoofSafety:InterlockMode` sets what Open does with the conditions. Only Open is checked, before the controller's own
checks, which still apply to every Open it lets through. **Close and Stop are never refused for the conditions**, in
any mode. A repeated Open while the roof is opening is checked like any other Open.

| Mode | Safe | Unsafe or Unknown |
|---|---|---|
| `Off` (default) | Open goes ahead. | Open goes ahead, as without the service; the controller logs that it was not safe. |
| `Warn` | Open goes ahead. | Refused, 409 `SafetyOverrideRequired`, unless the request carries `overrideSafety=true`. Anyone who may open the roof (an operator or an admin) may override; the override is audit-logged (`AUDIT safety override`). `OverrideRole: Admin` narrows overrides to admins, and an override from anyone else is then refused, 409 `SafetyInterlockActive`, and logged. |
| `Block` | Open goes ahead. | Refused, 409 `SafetyInterlockActive`. No override. |

```text
POST /api/v4.0/RoofControl/Open?overrideSafety=true
```

The override flag is ignored while the conditions are Safe or the interlock is Off. A refusal is RFC 7807
ProblemDetails like every other: `code`, a detail with the reasons, and `roofStatus` with `safety`. With no source
configured the conditions stay Unknown, so Warn always needs an override and Block always refuses Open; the controller
warns of this at start.

Warn is the owner's chosen policy ([Decisions](#decisions-and-open-questions)): the safety check warns, and the person
opening decides. Unknown counts as Unsafe, so it asks for the override too. The default mode stays Off until the web
UI, the kiosk and the terminal interface can ask for the override: in Warn mode they show the refusal and cannot yet
send the override, and with no source configured every Open would ask for one.

With `OverrideRole: Admin`, the role is checked with its policy, so an admin's personal key needs its configure scope as
well.

The clients follow the same rule (`RoofCommandRules.IsOpenBlockedBySafety`): in Block mode, while the conditions are not
Safe, Open is not offered, with the reason "the safety interlock blocks it". In Warn mode Open is offered, and the
controller asks for the override.

## Settings

The `RoofSafety` section of the controller's configuration (`appsettings.json`, the settings file, environment
variables such as `RoofSafety__InterlockMode`). It is not in the settings catalogue yet, so the API and the UIs do not
change it. A value the controller cannot use stops it at start.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| `InterlockMode` | `Off` | `Off`, `Warn`, `Block` | [The Open interlock](#the-open-interlock). |
| `OverrideRole` | `Operator` | `Operator`, `Admin` | Who may override in Warn mode: anyone who may open the roof, or only admins. |
| `PollInterval` | `00:00:10` | 1 s to 10 min | How often each source is read. |
| `ReadTimeout` | `00:00:05` | 0.1 s to 1 min | How long one read may take. |
| `MaxReadingAge` | `00:01:00` | more than `PollInterval` plus `ReadTimeout`, at most 1 h | How old a reading may be and still count, for a source without its own. |

## Where it shows

The status hub sends the conditions on every message, as they are when it is published. A change to the conditions
alone reaches the clients with the next message, a heartbeat within a second at the latest.

| Client | Shows |
|---|---|
| Web UI, roof page | A badge in the header, "Safety: Safe / Unsafe / Unknown" (green, red, amber), with the reasons and the interlock in its tooltip, and a Safety row in the status card. |
| `hvo-roof ui`, roof page | A Safety row in ROOF STATE. |
| `hvo-roof status` | A `Safety:` row; `status --watch` ends each line with the state (`safety unsafe`, and `Open blocked` in Block mode). |
| `hvo-roof open` | `--override-safety` sends the override. |
| Kiosk, roof page | A chip, "Safety: Safe / Unsafe / Unknown" in the same colours; the reasons are in the Safety row on Readings. |

All of them use the client library's shared words (`RoofStatusText.DescribeSafety`), for example
`UNSAFE: Telescope 'Main' is not parked (Open blocked by the safety interlock)`. A client of an older controller,
which sends no `safety`, shows nothing for it.

Not yet: an override prompt in the web UI, the kiosk and the terminal interface. In Warn mode they show the
controller's refusal, whose detail names `overrideSafety=true`; `hvo-roof open --override-safety` and the API take the
override. The kiosk's indicator is deliberately small while its home page is redesigned (#208).

## The log

| Event | Level |
|---|---|
| The conditions become Unsafe, or Unknown after being judged, or their reasons change while not Safe | Warning |
| The conditions become Safe; the first judgement after start; Unknown with no source configured | Information |
| A source cannot be read (once, when it starts failing; the failure's type or the timeout, never its message, which could carry an address) | Warning |
| A source is read again after failing | Information |
| Open while not Safe, interlock Off | Information |
| Open refused by the interlock (Warn without an override, Block) | Information, Warning |
| `AUDIT safety override: Open by <caller> ...` | Warning |
| `AUDIT safety override by <caller> refused ...` | Warning |

## Testing

No test relies on hardware. The simulation has `SimulatedTelescopeParkSource` and `SimulatedWeatherSafetySource`
(`src/HVO.RoofControllerV4.Simulation/Safety/`), each settable while it is read: its value, a detail, the time the
device says it observed it, and how it answers (`Answers`, `Fails`, `Hangs` until cancelled, or `IgnoresCancellation`
until released).

| Tests | Cover |
|---|---|
| `Safety/RoofSafetyServiceTests` | Each state with its reasons; staleness per source and its own maximum age; observation times; failed, hung and blocking reads; recovery; the background loop; the logged transitions; the settings' validation. |
| `Safety/RoofSafetyInterlockTests` | Each mode against each state; the override role; the audit lines. |
| `Controllers/SafetyApiTests` | `GET Safety`; the conditions on the status and on a refusal; the interlock through the real pipeline with simulated sources; Close and Stop never gated; a bad setting stopping the start. |
| `Hubs/RoofStatusBroadcasterTests` | The conditions on every hub message, a change reaching the clients with the heartbeat. |
| `Client/RoofSafetyClientTests`, `Cli/RoofCliSafetyTests`, `Web/DashboardSafetyTests`, `Kiosk/KioskSafetyTests` | The client calls, the shared words, the Block rule, and each indicator. |

## Future source adapters

Each is an `ITelescopeParkSource` or `IWeatherSafetySource` registered by `AddRoofSafety` from configuration, for
example:

```json
"RoofSafety": {
  "Telescopes": [ { "Name": "Main", "AlpacaDevice": "<address>/api/v1/telescope/0", "MaxReadingAge": "00:00:30" } ],
  "Weather": [ { "Name": "Cloud sensor", "AlpacaDevice": "<address>/api/v1/safetymonitor/0" } ]
}
```

These keys do not exist yet. When they do, a device's address goes in the Pi's local settings, never in this
repository. Planned:

- **ASCOM Alpaca** `GET .../telescope/{n}/atpark` and `GET .../safetymonitor/{n}/issafe`: the adapter checks the
  Alpaca `ErrorNumber` and throws on an error, so an error is a failed read, never a value. `Connected` false is a
  failed read too.
- **The HAT emulator** serves the same Alpaca endpoints, modelled on the
  [ASCOM Alpaca API](https://ascom-standards.org/api/), with control-API calls to park or unpark a mount, make the
  weather unsafe, or stop answering, so the adapters are tested against the emulator and not against the controller's
  own idea of a device.
- **A park sensor on a HAT input** (#32) or a camera's view of the mounts (#76) could become sources of the same kind.

## Decisions and open questions

Decided by the owner (#207):

1. **The safety check is a warning only.** The policy is Warn: while the conditions are not Safe, Open asks for an
   override, and **anyone who may open the roof (an operator or an admin) may override it**; every override is
   audit-logged. `OverrideRole` defaults to `Operator`; `Admin` remains as a narrower setting. The default mode stays
   `Off` until the web UI, the kiosk and the terminal interface prompt for the override (see
   [The Open interlock](#the-open-interlock)).
2. **Unknown is treated the same as Unsafe**, in Warn and in Block: a source that cannot be read is not evidence of
   safety.

Kept as they are for now:

3. **Close is never gated**, even with a telescope not parked: closing is the safe direction for weather, and
   blocking it could leave the roof open in rain.
4. **Unsafe weather does not close the roof automatically**: the service never moves the roof.

Still open:

5. **When to turn Warn on by default**, and whether an override should need a reason typed in. The override prompts in
   the web UI, the kiosk (#208) and the terminal interface come first.
6. **Must every kind of source be present to be Safe?** Now the conditions cover the configured sources only: with a
   telescope source and no weather source, a parked telescope is Safe.
7. **The settings in the catalogue.** `RoofSafety` is configuration only for now; changing the mode through the API
   would be a safety-critical, admin-only setting with confirmation.
