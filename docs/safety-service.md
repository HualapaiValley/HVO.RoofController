# Safety conditions: telescope park and weather

The controller's safety service says whether the observatory's conditions allow the roof to open: the weather safe,
and every telescope that can block the roof clear of it. It reports **Safe**, **Unsafe** or **Unknown** with the
reasons on every status snapshot,
on the status hub and at `GET /api/v4.0/Safety`, and logs every change. The Open interlock uses the conditions: by
default (**Warn**) an Open while they are not Safe asks for an override, which anyone who may open the roof can give and
which is audit-logged.

This is separate from the controller's own safety systems (the watchdog, the limit switches, the fault latch, relay
read-back; see [Safety behavior](../README.md#safety-behavior)). Those apply whatever the conditions say, and the
conditions never stop or move the roof.

Issues [#207](https://github.com/HualapaiValley/HVO.RoofController/issues/207) and
[#215](https://github.com/HualapaiValley/HVO.RoofController/issues/215): the service, its source interfaces, the API, the
interlock, the indicators and the override prompts on every user interface.
[#220](https://github.com/HualapaiValley/HVO.RoofController/issues/220) (phase 2): the real sources, ASCOM Alpaca
mounts (`Telescope.AtPark`) and weather safety monitors (`SafetyMonitor.IsSafe`), configured under `RoofSafety`
([docs/alpaca.md](alpaca.md)), and the owner's readiness rule. With no source configured, the conditions are Unknown,
"No safety sources configured", so with the default Warn interlock every Open asks for the override.

- [What it reports](#what-it-reports)
- [Sources](#sources)
- [Staleness and failed reads](#staleness-and-failed-reads)
- [The Open interlock](#the-open-interlock)
- [Settings](#settings)
- [Where it shows](#where-it-shows)
- [The override prompts](#the-override-prompts)
- [The log](#the-log)
- [Testing](#testing)
- [Other sources](#other-sources)
- [Decisions and open questions](#decisions-and-open-questions)

## What it reports

Each source is judged on its own, then the sources that count are combined:

| A source is | When |
|---|---|
| Safe | Its last reading is recent enough and says safe (the telescope clear of the roof, the weather safe). |
| Unsafe | Its last reading is recent enough and says unsafe (the telescope not clear of the roof, the weather unsafe). |
| Unknown | It has not been read yet, it cannot be read, or its last reading is too old, whatever that reading said. |

A telescope is clear of the roof when it is parked: parked is the only proof for now (`TelescopeRoofClearance`, the
one place that decides it, where a camera or safe zones can add their proof later).

The sources that count are every weather source set `Required` and every telescope that can block the roof
(`BlocksRoof`). A weather source set `Ignored`, and a telescope that cannot block the roof, are judged and shown the
same way but never decide the conditions. The owner's rule (#220): **Open counts as Safe only when at least one fresh
weather source is reporting and every scope that can block the roof is clear.** So the conditions are:

- **Unsafe** when any source that counts is Unsafe;
- otherwise **Unknown** when there is no source, no required weather source ("No required weather source
  configured"), or any source that counts is Unknown;
- otherwise **Safe**.

Unknown is never read as safe. A parked telescope alone is not enough: without a required weather source the
conditions are Unknown.

`RoofSafetyStatus` (Common, `RoofStatusResponse.Safety` and `GET Safety`):

| Field | Meaning |
|---|---|
| `state` | `Safe`, `Unsafe` or `Unknown`. |
| `reasons` | Why it is not Safe: the counting sources' Unsafe reasons first, then their Unknown ones', then `No required weather source configured` when it applies; or only `No safety sources configured`. Empty when Safe. |
| `weatherState`, `weatherReasons` | The weather alone, with no telescope: `Safe` only when at least one weather source is required and every required one says safe, recently; `Unsafe` when one says unsafe; otherwise `Unknown`, with the same words. What the controller's Alpaca `SafetyMonitor` publishes ([docs/alpaca.md](alpaca.md#the-weather-alone)). |
| `sources[]` | Each source: `name`, `kind` (`TelescopePark` or `Weather`), `state`, `reason`, `detail` (extra words from the source, at most 200 characters), `lastReadingUtc`, `isStale`, `maxReadingAgeSeconds`, `counts` (false for a weather source set `Ignored` or a telescope that cannot block the roof; true from a controller that does not say). |
| `stateSinceUtc` | When `state` last changed. |
| `interlockMode` | `Off`, `Warn` or `Block` ([The Open interlock](#the-open-interlock)). |
| `overrideRole` | In Warn mode, the role an override needs: `RoofOperator` (anyone who may open, the default) or `RoofAdmin`; null otherwise. |

The reasons are written once, by the controller, and every client shows them as they are, for example:

```text
Telescope 'Main' is not parked
Weather monitor 'Cloud sensor' reports unsafe weather
Telescope 'Main' cannot be read (no answer within 5 s)
Telescope 'Main': the last reading (parked) is older than 60 s; the last read failed (no answer within 5 s)
Telescope 'Main' has not been read yet
No required weather source configured
No safety sources configured
```

A source that cannot be read says why in brackets when the words are fit to show: a timeout, or an adapter's own words
(a `RoofSafetySourceException`, whose message names no address and quotes nothing the device sent). Any other failure
is named only by its type, in the log. A change of
reasons is a real change, and the log records it. A source's `detail` can change with every read, and is not part of
the state.

## Sources

The source interfaces are in `HVO.RoofControllerV4.Common.Safety`, so the simulation can implement them:

| Interface | One reading | Its setting |
|---|---|---|
| `ITelescopeParkSource` | `TelescopeParkReading(IsParked, ObservedUtc?, Detail?)`, one per mount | `BlocksRoof` (true unless it says otherwise) |
| `IWeatherSafetySource` | `WeatherSafetyReading(IsSafe, ObservedUtc?, Detail?)` | `Requirement`: `Required` (unless it says otherwise) or `Ignored` |

Both extend `IRoofSafetySource`: a `Name` that the reasons quote, and an optional `MaxReadingAge` of the source's own.
An adapter reads its device when asked and throws when it cannot (a `RoofSafetySourceException` when it can say why in
words fit to show); it polls nothing and caches nothing, since the service does both.

The controller's adapters read ASCOM Alpaca devices: `AlpacaTelescopeParkSource` for each of `RoofSafety:Telescopes`
and `AlpacaSafetyMonitorSource` for each of `RoofSafety:WeatherSources`, in the order configured
([docs/alpaca.md](alpaca.md)). Any other `ITelescopeParkSource` or `IWeatherSafetySource` singleton (the tests'
simulated ones, [Testing](#testing)) is read too, before the configured ones.

`ObservedUtc` is for a device that dates its observation: an earlier time makes the reading that much older, and a time
in the future counts as now. Without it, the reading counts from when the read ended.

## Staleness and failed reads

`RoofSafetyService` (`src/HVO.RoofControllerV4.RPi/Safety/`) is a hosted service. It reads every source at start,
all at once, and again `PollInterval` after each round ends:

- Each read runs on the thread pool and is cancelled after `ReadTimeout`, so an adapter that blocks holds up only itself.
- A source whose last read has not ended (an adapter that ignores its cancellation) is not read again until it does; the
  attempt counts as a failed read.
- A failed or timed-out read keeps the last reading. That reading counts until it is older than the source's
  `MaxReadingAge` (or the service's), and the source is then Unknown, stale, with what it last said and why the last
  read failed in the reason. A short outage (a device restarting) does not change the conditions; one longer than
  `MaxReadingAge` makes them Unknown.
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
| `Warn` (default) | Open goes ahead. | Refused, 409 `SafetyOverrideRequired`, unless the request carries `overrideSafety=true`. Anyone who may open the roof (an operator or an admin) may override; the override is audit-logged (`AUDIT safety override`). `OverrideRole: Admin` narrows overrides to admins, and an override from anyone else is then refused, 409 `SafetyInterlockActive`, and logged. |
| `Off` | Open goes ahead. | Open goes ahead, as without the service; the controller logs that it was not safe. |
| `Block` | Open goes ahead. | Refused, 409 `SafetyInterlockActive`. No override. |

```text
POST /api/v4.0/RoofControl/Open?overrideSafety=true
```

The override flag is ignored while the conditions are Safe or the interlock is Off. A refusal is RFC 7807
ProblemDetails like every other: `code`, a detail with the reasons, and `roofStatus` with `safety`. With no source
configured the conditions stay Unknown, so Warn asks every Open for an override and Block refuses every Open; the
controller warns of this at start.

Warn is the owner's policy ([Decisions](#decisions-and-open-questions)): the safety check warns, and the person opening
decides. Unknown counts as Unsafe, so it asks for the override too. Every user interface asks for the override before
it sends Open ([The override prompts](#the-override-prompts)).

With `OverrideRole: Admin`, the role is checked with its policy, so an admin's personal key needs its configure scope as
well.

## Settings

The `RoofSafety` section of the controller's configuration (`appsettings.json`, the secrets directory, environment
variables such as `RoofSafety__InterlockMode`). It is not in the settings catalogue, so the API, the UIs and the
settings file do not hold it. A value the controller cannot use stops it at start, as does a key under `RoofSafety` that
no setting has (a misspelt one).

| Setting | Default | Range | Meaning |
|---|---|---|---|
| `InterlockMode` | `Warn` | `Off`, `Warn`, `Block` | [The Open interlock](#the-open-interlock). |
| `OverrideRole` | `Operator` | `Operator`, `Admin` | Who may override in Warn mode: anyone who may open the roof, or only admins. |
| `PollInterval` | `00:00:10` | 1 s to 10 min | How often each source is read. |
| `ReadTimeout` | `00:00:05` | 0.1 s to 1 min | How long one read may take. |
| `MaxReadingAge` | `00:01:00` | more than `PollInterval` plus `ReadTimeout`, at most 1 h | How old a reading may be and still count, for a source without its own. |
| `Telescopes` | none | at most 16 | The ASCOM Alpaca mounts read: `Name`, `Url`, `DeviceNumber`, `MaxReadingAge`, `BlocksRoof` ([docs/alpaca.md](alpaca.md#source-settings)). |
| `WeatherSources` | none | at most 16 | The ASCOM Alpaca safety monitors read: `Name`, `Url`, `DeviceNumber`, `MaxReadingAge`, `Requirement`. |

To keep Open as it was before the interlock (the conditions only reported and logged), set `InterlockMode` to `Off`.

## Where it shows

The status hub sends the conditions on every message, as they are when it is published. A change to the conditions
alone reaches the clients with the next message, a heartbeat within a second at the latest.

| Client | Shows |
|---|---|
| Web UI, roof page | A badge in the header, "Safety: Safe / Unsafe / Unknown" (green, red, amber); a safety panel under the roof's state with the reasons, what Unknown means, each source's state and what Open does; a Safety row and a row per source in the status card. |
| Kiosk and Mac app, roof page | The chip "Safety: Safe / Unsafe / Unknown" in the same colours, the first reason and what Open does under it (cut short when long); the whole Safety row and a row per source on Readings. |
| `hvo-roof ui`, roof page | The Safety row and a row per source in ROOF STATE. |
| `hvo-roof status` | A `Safety:` row and a `Safety source:` row per source; `status --watch` ends each line with the state (`safety unsafe`, and `Open blocked` in Block mode); `--json` carries `safety`. |

All of them use the client library's shared words (`RoofStatusText.DescribeSafety`, `RoofSafetyText`), for example
`UNSAFE: Telescope 'Main' is not parked (Open needs a safety override)` and `Unsafe: Telescope 'Main' is not parked`.
A source that does not count ends with why: `Unsafe: Telescope 'Solar' is not parked [does not block the roof]`,
`Unknown: Weather monitor 'Old sensor' cannot be read [ignored]`. A client of an older controller, which sends no
`safety`, shows nothing for it.

## The override prompts

While the interlock is in Warn mode and the conditions are not Safe (Unknown included), every interface asks before it
sends Open, with the same words (`RoofSafetyText`, `RoofCommandRules.NeedsSafetyOverride`):

```text
Safety override
The safety conditions are UNSAFE: Telescope 'Main' is not parked.
Open the roof anyway? The override is logged with your name. Close and Stop are never affected.
[Open anyway] [Cancel]
```

With the conditions Unknown it adds "Unknown counts as unsafe: nothing confirms that the telescopes are parked and the
weather is safe." Nothing is sent until the person chooses **Open anyway**, which sends Open with the override; Cancel
sends nothing.

| Interface | The prompt |
|---|---|
| Web UI | A panel in the controls card, above Open and Close. Stop stays at the bottom of the page. |
| Kiosk and Mac app | In the motion controls' place on the roof page, with the two answers side by side; Stop stays at the right. Esc answers Cancel. |
| `hvo-roof ui` | A prompt over the page; Esc answers Cancel, and F9 still stops the roof. |
| `hvo-roof open` | No prompt: the controller's refusal ends with "To open anyway: 'hvo-roof open --override-safety'", and `--override-safety` sends the override. |

- The question is asked only of an Open that would be sent (not of one blocked for another reason, which says why).
- Stop, Close, the end of the person's session, and the kiosk locking withdraw it: a question asked of one person is
  never answered by the next, and an Open withdrawn by Stop is never sent. The answer and the Open it sends belong to
  the session and the Stops the question was asked under, checked as the Open is reserved: an **Open anyway** that
  crosses a Stop, even one the interface has not heard of yet, sends nothing.
- When the conditions change between the status and the Open (the interface showed Safe), the controller refuses with
  `SafetyOverrideRequired`, and the interface asks then, with the conditions the controller sent; unless Stop was sent
  after that Open, or the controller's status now blocks Open for another reason (a latched fault, say), when the
  refusal is shown and nothing is asked.
- With `OverrideRole: Admin`, Open is not offered to someone the interface knows is not an admin, with the reason "the
  safety conditions are not confirmed safe, and only an admin may override them".
- With the interlock Off, nothing is asked; in Block mode, Open is not offered while the conditions are not Safe, with the
  reason "the safety interlock blocks it".
- The command history (#206) records an Open accepted with an override as `Accepted (safety override, conditions …)`.

## The log

| Event | Level |
|---|---|
| The conditions become Unsafe, or Unknown after being judged, or their reasons change while not Safe | Warning |
| The conditions become Safe; the first judgement after start; Unknown with no source configured | Information |
| The sources read, at start (`Reading 2 safety source(s) every 00:00:10 …`), by kind and name, with those that do not count marked `(ignored)` or `(does not block the roof)` | Information |
| Sources configured, none of them a required weather source, at start: the conditions stay Unknown | Warning |
| A source cannot be read (once, when it starts failing; the adapter's words, the timeout or the failure's type, never another message, which could carry an address) | Warning |
| A source is read again after failing | Information |
| Open accepted by the controller while not Safe, interlock Off | Information |
| Open refused by the interlock (Warn without an override, Block) | Information, Warning |
| `AUDIT safety override: Open by <caller> accepted ...`, written only once the controller has accepted Open | Warning |
| An override whose Open the controller then refused (a latched fault, say): it had no effect | Information |
| `AUDIT safety override by <caller> refused ...` (an override from outside a narrowed `OverrideRole`) | Warning |

The interlock decides before the controller's own checks, but what Open did is logged after them: an override is
audited only when the controller accepted Open, so the audit never records an override that moved nothing.

## Testing

No test relies on hardware. The simulation has `SimulatedTelescopeParkSource` and `SimulatedWeatherSafetySource`
(`src/HVO.RoofControllerV4.Simulation/Safety/`), each settable while it is read: its value, a detail, the time the
device says it observed it, and how it answers (`Answers`, `Fails`, `Hangs` until cancelled, or `IgnoresCancellation`
until released); and, before it is read, `BlocksRoof` or `Requirement`. The HAT emulator serves fake ASCOM Alpaca
mounts and safety monitors for the controller's Alpaca adapters ([docs/emulator.md](emulator.md#alpaca-devices)).

| Tests | Cover |
|---|---|
| `Safety/RoofSafetyServiceTests` | Each state with its reasons; the readiness rule (a required weather source, every scope that can block the roof clear, `Ignored` and `BlocksRoof: false` shown but never deciding); the weather alone; staleness per source and its own maximum age; observation times; failed, hung and blocking reads; an adapter's words in the reason, and no other failure's message; recovery; the background loop; the logged transitions; the settings' validation, the devices' included; Warn by default. |
| `Safety/AlpacaSafetySourceTests`, `Emulation/EmulatedAlpacaTests`, `Controllers/SafetyAlpacaApiTests`, `Scenarios/AlpacaSafetySourceScenarios` | The Alpaca adapters, the emulator's Alpaca devices, the controller with configured devices, and the emulated plant with them ([docs/alpaca.md](alpaca.md#testing)). |
| `Safety/RoofSafetyInterlockTests` | Each mode against each state; the override role; the audit lines. |
| `Controllers/SafetyApiTests`, `Controllers/SafetyDefaultApiTests` | `GET Safety`; the conditions on the status and on a refusal; the interlock through the real pipeline with simulated sources, in each mode and with the default Warn; Close and Stop never gated; unsafe weather never moving the roof; Unknown asking for the override; a bad setting stopping the start. |
| `Hubs/RoofStatusBroadcasterTests` | The conditions on every hub message, a change reaching the clients with the heartbeat. |
| `Client/RoofSafetyClientTests`, `Client/RoofSafetyTextTests` | The client calls, the shared words, the Block rule, when Open asks for the override, and the rows per source. |
| `Web/WebSafetyOverrideTests`, `Kiosk/KioskSafetyOverrideTests`, `Cli/RoofTerminalUiSafetyTests`, `Cli/RoofCliSafetyTests` | Each interface's prompt (nothing sent until confirmed, the override sent with Open anyway, Cancel, Stop and the lock withdrawing it, a refusal asking), and each indicator. |

The test hosts set the interlock Off, so the tests of anything else open as before; the safety tests set the mode they
test, and those of the default leave it unset.

Both compose files point the controller at the emulator's Alpaca mount and safety monitor, parked and safe, so those
rigs' conditions are Safe: `compose-smoke-test.sh` checks that they follow the emulator's devices, and that with the
weather unsafe the containerized controller refuses Open without the override (`SafetyOverrideRequired`) and moves
nothing. The rigs of `tests/emulator/deploy-scenarios.sh` and `tests/installer/rig-scenario.sh` configure no source, so
their conditions are Unknown; those scripts, and `tests/cli/terminal-smoke.sh`, give the override with every Open.

## Other sources

ASCOM Alpaca mounts and safety monitors are the sources now ([docs/alpaca.md](alpaca.md)). Others discussed, none
adopted yet:

- **ASCOM Alpaca `ObservingConditions`**: deferred, since it needs thresholds per sensor, hysteresis and freshness of
  its own.
- **Other proof that a scope is clear of the roof**: a camera's view of the mounts (#76), or safe zones in right
  ascension and declination. Each would add its proof where the service decides clearance (`TelescopeRoofClearance`).
- **A park sensor on a HAT input** (#32).

Any of them becomes an `ITelescopeParkSource` or `IWeatherSafetySource`, so the service, the interlock, the indicators
and the prompts stay as they are. A device's address goes in the Pi's local configuration, never in this repository,
and never in a log or a reason.

## Decisions and open questions

Decided by the owner (#207, #215):

1. **The safety check is a warning only.** The policy is Warn, the default: while the conditions are not Safe, Open asks
   for an override, and **anyone who may open the roof (an operator or an admin) may override it**; every override is
   audit-logged. `OverrideRole` defaults to `Operator`; `Admin` remains as a narrower setting.
2. **Unknown is treated the same as Unsafe**, in Warn and in Block, and in the prompts and indicators: a source that
   cannot be read is not evidence of safety.
3. **Close is never gated**, even with a telescope not parked: closing is the safe direction for weather, and blocking
   it could leave the roof open in rain. **Stop is always available.**
4. **Nothing closes the roof automatically**: unsafe weather does not close it, and the service never moves the roof.
5. **The command history** keeps #216: no "conditions unknown" note on an Open accepted with the interlock Off and no
   source configured; the note is kept for everything else.

Decided by the owner for the real sources (#220, 2026-10-30):

6. **The sources are ASCOM Alpaca devices**: mounts' `Telescope.AtPark` and weather `SafetyMonitor.IsSafe`, only read,
   at pinned addresses ([docs/alpaca.md](alpaca.md)).
7. **Open readiness**: Open counts as Safe only when at least one fresh weather source is reporting and every scope that
   can block the roof is clear. Each weather source is configured and set `Required` or `Ignored`. A parked telescope
   alone is no longer Safe.
8. **Clear of the roof, not parked**: each telescope has a "blocks roof" setting, and the check asks whether the scope
   is clear of the roof. Parked is the only proof today; a camera or safe zones can show it later. (An unattended Close
   through Alpaca that waits for such a scope is #220's phase 3; Close through this API stays ungated, decision 3.)

Decided here:

9. **A failed read keeps the last reading until it is too old** (`MaxReadingAge`), as before: a single lost poll does
   not ask the next Open for an override. A shorter `MaxReadingAge` makes a lost source Unknown sooner.
10. **No override reason is typed**: the audit line names the person, the conditions and their reasons.
11. **Off asks nothing**: the prompts appear only where the controller would ask for the override (Warn).

Still open:

12. **The settings in the catalogue.** `RoofSafety` is configuration only; changing the mode through the API would be a
    safety-critical, admin-only setting with confirmation.
