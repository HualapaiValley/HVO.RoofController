# Alpaca server

`HVO.RoofControllerV4.Alpaca` serves the roof to astronomy software as an [ASCOM Alpaca](https://ascom-standards.org/api/)
**Dome** (a roll-off roof is a dome with a shutter and nothing else), so NINA, SGP, Voyager, ACP and Windows ASCOM
clients (through the ASCOM Platform's Alpaca Dynamic Client) can watch the roof with no plug-in of ours. The proposal,
its reviews and the owner's decisions are in #220.

It is a process of its own, apart from the controller and the [web UI](web.md). It reaches the controller only through
the client library (the REST API and the status hub), with its own API key, and it never sees the controller's settings
or secrets. It is **off by default**, listens only on the addresses it is given, and answers only its LAN.

This page covers **phase 1, a read-only Dome**, and **phase 2, the weather**: the server also serves the observatory's
weather as an Alpaca **SafetyMonitor**, and the controller reads other Alpaca devices (mounts and weather monitors) as
its [safety sources](#safety-sources). The server reads the roof and stops it (`AbortSlew`). It never opens or closes
it: `OpenShutter` and `CloseShutter` are refused. Later phases of #220 add `CloseShutter` (phase 3), then `OpenShutter`
(phase 4). See [Not yet](#not-yet).

## Contents

- [The roof as Dome 0](#the-roof-as-dome-0)
- [The weather as SafetyMonitor 0](#the-weather-as-safetymonitor-0)
- [Error numbers](#error-numbers)
- [The protocol](#the-protocol)
- [Security](#security)
- [Running it](#running-it)
- [Server settings](#server-settings)
- [Discovery](#discovery)
- [Conformance](#conformance)
- [Safety sources](#safety-sources)
- [Not yet](#not-yet)

## The roof as Dome 0

The roof is `/api/v1/dome/0` (IDomeV3, `InterfaceVersion` 3). Every read comes from the last status the controller
published on its status hub. A read never asks the controller and never touches the HAT. A status the server can no
longer vouch for is never served: the controller cannot be reached, it has sent no status yet, or its hub has been
silent for 3 s (three heartbeats). Each read checks the 3 s itself, against when the last message arrived. Then the
read is error `0x500` ([Error numbers](#error-numbers)).

### The shutter

| `ShutterStatus` | The roof |
|---|---|
| 0 Open | `Open` (at the open limit) |
| 1 Closed | `Closed` (at the closed limit) |
| 2 Opening | `Opening` |
| 3 Closing | `Closing` |
| 4 Error | every other state: `Stopped`, `PartiallyOpen`, `PartiallyClose`, `Error`, `NotInitialized`, `Unknown` |

A roof that is not moving is never reported as moving. Why it reads as Error (a stop part way, a latched fault and its
reason, the last error, the [safety conditions](safety-service.md)) is in the `status` action.

`Slewing` is true while the controller drives the roof, and also while the drive still reports running (IN4) after a
stop, as the roof coasts to a halt.

### The members

| Member | Answer |
|---|---|
| `Connected`, `Connect`, `Disconnect`, `Connecting` | A flag per client ([Connecting](#connecting)). `Connecting` is always false: connecting takes no time. |
| `Name`, `Description`, `DriverInfo`, `DriverVersion`, `InterfaceVersion`, `SupportedActions` | `HVO roll-off roof`; a description of at most 64 characters; the server's full version; the version as major.minor (`4.3`); 3; `["status"]`. |
| `DeviceState` | `ShutterStatus`, `Slewing` and `TimeStamp` (UTC). The roof has no `Altitude`, `Azimuth`, `AtHome` or `AtPark` to add. |
| `ShutterStatus`, `Slewing` | As above. |
| `CanSetShutter` | true. |
| `CanFindHome`, `CanPark`, `CanSetAltitude`, `CanSetAzimuth`, `CanSetPark`, `CanSlave`, `CanSyncAzimuth` | false. |
| `Slaved` | Reads false. Setting it false is accepted; setting it true is `0x400` NotImplemented. |
| `Altitude`, `Azimuth`, `AtHome`, `AtPark` | `0x400` NotImplemented: a roll-off roof has none. |
| `SlewToAltitude`, `SlewToAzimuth`, `SyncToAzimuth`, `FindHome`, `Park`, `SetPark` | `0x400` NotImplemented. |
| `CommandBlind`, `CommandBool`, `CommandString` | `0x400` NotImplemented. |
| `AbortSlew` | **Stops the roof**: the controller's Stop ([AbortSlew](#abortslew)). |
| `OpenShutter`, `CloseShutter` | `0x40B` InvalidOperation: commands are disabled in this phase. Nothing is sent to the controller. |
| `Action` | `status` only: a line describing the shutter, the roof, the last stop, the fault latch, the last error and the safety conditions. Any other action is `0x40C`. No action moves the roof or clears a fault. |

### Connecting

A client connects with `Connected=true` or `Connect`. Connecting is a flag the server keeps for that client, its
address and its `ClientID`, and nothing else: it never reaches the controller or the roof, and `Disconnect` never stops
or moves the roof. The flags are few and expire. A client idle for `ConnectionIdleMinutes` (60) is forgotten, and
connecting one more than `MaxConnectedClients` (32) forgets the one idle longest. A restart of the server forgets them
all; clients connect again.

Every device member needs the client connected (`0x407` NotConnected otherwise), except `AbortSlew` and the common
members: `Connected`, `Connecting`, `Name`, `Description`, `DriverInfo`, `DriverVersion`, `InterfaceVersion`,
`SupportedActions` and `DeviceState`.

### AbortSlew

`AbortSlew` sends the controller's Stop, and answers once the controller has. It is always allowed: without connecting,
after a restart forgot the client, with a fault latched, and past the polling limit ([Security](#security)). Its answer
claims only what the controller said:

- success: the controller's answer shows the stop done: the relay register read back and verified all off (zero), and
  no motion commanded;
- `0x504`: the controller could not verify the relays off, or its answer does not show them verified off (an answer
  it could not read, a register not read back, a relay still on, motion still commanded). Confirm at the roof that the
  motor has stopped;
- another error: the Stop was refused or did not arrive ([Error numbers](#error-numbers)).

Stop's answer carries the controller's status straight after it. That status is served once it is newer than the
hub's status from the same controller instance, so the next read reflects the stop even before the hub's next message,
and an older hub message cannot undo it. An answer from a controller instance the hub has moved on from (one that has
since restarted) is never served: only the hub moves the server to a new instance. The command history records the Stop under the server's API key, with
the server's program and version as its `User-Agent`. Naming the Alpaca client there comes with phase 3.

## The weather as SafetyMonitor 0

The observatory's weather is `/api/v1/safetymonitor/0` (ISafetyMonitorV3, `InterfaceVersion` 3), in the same server,
with the same settings, protocol, security and polling limit as the roof. Its `IsSafe` is the controller's weather
alone, `weatherState` on the status it last published ([The weather alone](#the-weather-alone)): **true only while
that is Safe**, that is at least one weather source is `Required` and every required one has a fresh reading that says
safe, **and the controller lists this monitor's `UniqueID` among its own** (`RoofSafety:OwnAlpacaUniqueIds`), so it
can never read the monitor back as its weather ([Never our own SafetyMonitor](#never-our-own-safetymonitor)).

- **Never the telescopes.** NINA and Voyager park the mount and close the roof when their safety monitor turns unsafe.
  A monitor that also said unsafe whenever a mount unparked would end every session, so a mount's park state, which is
  part of the controller's own conditions for Open, is not part of `IsSafe`.
- **Anything short of Safe is false**: unsafe weather, unknown weather (a required source unread, failing or stale; no
  required source at all), a controller that does not report its weather, and a controller that does not list this
  monitor's `UniqueID` (the `status` action, the setup page and the log say so, with the settings to give it). Unknown
  counts as unsafe.
- **The same staleness as the roof.** A read is served from the last status the controller published, never older than
  3 s: when the controller cannot be reached, has sent no status, or has been silent for 3 s, `IsSafe` is error `0x500`
  ([Error numbers](#error-numbers)), and its `Value` is false, so a client that overlooks `ErrorNumber` still reads
  unsafe.
- **Its own connection.** A client connects to it on its own (`Connected=true` or `Connect`); connecting to the Dome is
  not connecting to it, and the other way round. `IsSafe` needs the client connected (`0x407`, with `Value` false);
  the common members and `DeviceState` do not ([Connecting](#connecting)).
- **It never reaches the roof.** No member of it sends anything to the controller; it only reads.

| Member | Answer |
|---|---|
| `IsSafe` | As above. |
| `Name`, `Description`, `DriverInfo`, `DriverVersion`, `InterfaceVersion`, `SupportedActions` | `HVO observatory weather`; a description of at most 64 characters; the server's full version (it begins `HVO Roof Controller Alpaca server`, which the controller's sources recognise, [Never our own SafetyMonitor](#never-our-own-safetymonitor)); major.minor; 3; `["status"]`. |
| `DeviceState` | `IsSafe` and `TimeStamp` (UTC). |
| `Connected`, `Connect`, `Disconnect`, `Connecting` | As the Dome's, with flags of its own. |
| `Action` | `status` only: `Weather: Safe.`, or `Weather: Unsafe: Weather monitor 'Cloud sensor' reports unsafe weather.`, the controller's words, with why `IsSafe` is false when the controller does not list this monitor. Any other action is `0x40C`. |
| `CommandBlind`, `CommandBool`, `CommandString` | `0x400` NotImplemented. |

Its parameters are the Alpaca API's, as the roof's ([The protocol](#the-protocol)): a PUT's names must be cased exactly
(`Connected`, not `connected`).

## Error numbers

Alpaca errors are answered with HTTP 200, `ErrorNumber` and `ErrorMessage`. The table is fixed:

| Number | Name | When |
|---|---|---|
| `0x400` (1024) | NotImplemented | A member a roll-off roof does not have. |
| `0x401` (1025) | InvalidValue | (Not used in this phase.) |
| `0x407` (1031) | NotConnected | The client has not connected. Never for `AbortSlew`. |
| `0x40B` (1035) | InvalidOperation | `OpenShutter` and `CloseShutter` (commands disabled), and the controller's refusals for the roof's state: `FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `SafetyInterlockActive`, `SafetyOverrideRequired`. |
| `0x40C` (1036) | ActionNotImplemented | An action other than `status`. |
| `0x500` (1280) | Controller unavailable | The roof's state, and the weather, are unknown: the controller cannot be reached, has sent no status yet, or has been silent for 3 s. |
| `0x501` (1281) | Controller not initialized | The controller's `NotInitialized`. |
| `0x502` (1282) | Controller shutting down | The controller's `ShuttingDown`. |
| `0x503` (1283) | Hardware unavailable | The controller's `HardwareUnavailable`: it cannot read its safety inputs. |
| `0x504` (1284) | Relay state unverified | The controller's `RelayStateUnverified`, or a Stop's answer that does not show the relays verified off. |
| `0x505` (1285) | Controller refused the key | The controller does not accept the server's API key (revoked, rotated, or not allowed). |
| `0x50F` (1295) | Controller error | Any other failure. |

A property's error still carries a `Value`, as the Alpaca API's responses define it: its type's default (false, 0 or
empty), except `ShutterStatus`, whose `Value` on an error is 4 (Error), so a client that overlooks `ErrorNumber` still
does not see a roof that is fine. `IsSafe`'s is false: unsafe.

## The protocol

- **The envelope.** Every device answer is JSON with `Value` (a property's), `ClientTransactionID`,
  `ServerTransactionID`, `ErrorNumber` and `ErrorMessage`, cased exactly so. `ClientTransactionID` is the client's,
  echoed, or 0 when it sent none. `ServerTransactionID` counts up from 1 for the life of the process.
- **Parameters**, as the Alpaca API defines them. A GET's are in the query string, and their names match in any case
  (`clienttransactionid` is `ClientTransactionID`). A PUT's are in a form-encoded body
  (`application/x-www-form-urlencoded`), and their names must be cased exactly. A wrongly cased name is ignored, as if
  it were not sent, so a required one is then missing. A PUT's parameters in its query string are ignored too. Values
  `true` and `false` are read in any case.
- **Malformed requests** are answered with HTTP 400 and a plain-text reason: an unknown device, member or path under
  `/api` or `/management`; a member with the wrong HTTP method (only GET and PUT are used); a path not in lower case
  (`/api/V1`, `/api/v1/Dome`, `/shutterstatus` cased otherwise); a missing or malformed parameter; a PUT body that is
  not form-encoded or is over 16 KiB; a `ClientID` or `ClientTransactionID` that is not a number from 0 to 4294967295.
  Any other path is 404.
- **The management API**: `/management/apiversions` (`[1]`), `/management/v1/description` (the server's name, the
  manufacturer, its version and `Location`) and `/management/v1/configureddevices` (Dome 0 and SafetyMonitor 0, each
  with its `UniqueID`). Its answers carry `Value`, `ClientTransactionID` and `ServerTransactionID`.
- **The `UniqueID`s** are made the first time the server starts, kept in `alpaca-devices.json` in its data directory
  (`dome/0` and `safetymonitor/0`), and never changed, so a client recognises the roof and the weather across restarts
  and upgrades. A file from phase 1, with the Dome's alone, gains the SafetyMonitor's the first time the server reads it;
  the Dome's stays as it is. It is written whole and then put in
  place, and servers starting at the same moment take turns (`alpaca-devices.json.lock`), so each serves the one kept.
  Back the file up with the server's settings. A file that cannot be read stops the server rather than being replaced:
  restore it from a backup.
- **The setup pages**, `/setup`, `/setup/v1/dome/0/setup` and `/setup/v1/safetymonitor/0/setup`, show what the server
  is and does, each device's `UniqueID`, the roof's state and the weather, and each device's connected clients, in HVO
  Dark. They only read: there is no form, and the settings are
  changed where the server is installed.

## Security

The Alpaca API has no real authentication, so the server keeps itself small and apart:

- **A separate process with its own key.** It runs apart from the controller (an unauthenticated listener in the
  safety process is avoided) and from the web UI. It reaches the controller with a **Viewer** API key of its own,
  which is all phase 1 needs: Viewer reads the status, and every role may send Stop. Revoking the key cuts it off:
  reads then answer `0x505`. The server warns in its log when its key has a higher role.
- **Off by default, LAN-only.** It starts nothing unless `Alpaca:Enabled` is true, and listens only on the
  `ListenAddresses` it is given. A public address is refused. Use this computer's LAN address, or `127.0.0.1`.
- **An address allowlist.** Only clients within `AllowedNetworks` are answered (403 otherwise), discovery included.
  By default these are loopback and the private ranges (10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7, fe80::/10),
  not carrier-grade NAT (100.64/10, which overlay VPNs use). A network beyond them is refused. Narrow it to the site's
  LAN, for example `192.168.1.0/24`. Behind Docker's NAT every client can look like the gateway, so the allowlist is
  defence in depth. The real controls are the listen address (or the address a container's port is published on) and
  the host's firewall.
- **DNS rebinding and other web pages.** A request's `Host` must be an IP address, `localhost`, this computer's name
  (and `.local`) or one of `AllowedHosts`; any other name is refused (400). So a web page served from another name
  cannot reach the server through DNS rebinding. A request from a web page of another origin (an `Origin` header naming
  another host) is refused (403), and the server sends no CORS headers, so a browser never lets another site read an
  answer.
- **Polling is limited** to `RequestsPerSecondPerAddress` (100 a second, bursts of 200) per client address (429 past
  it). `AbortSlew` is never limited: the exemption is the member the device route matched, however the path is
  written.
- **No commands.** Phase 1 never sends Open, Close, Clear fault or a lease renewal to the controller. Commands, with an
  Alpaca-only credential, the history's Alpaca source and the owner's lease and unattended-Close decisions, come with
  phases 3 and 4.
- **Plain HTTP.** The Alpaca listener has no TLS yet (#220 leaves it for later), so it carries nothing secret: no
  credential is accepted from clients. The server's own connection to the controller can be HTTPS
  (`ControllerCertificateSha256` or `ControllerCaCertificatePath`, as the [client library](../src/HVO.RoofControllerV4.Client/README.md#connecting)
  checks them).

## Running it

Phase 1 ships the server as a program, run from a build of this repository. It is not yet in the controller's image,
the [deploy script](deployment.md) or the [installer](install.md) ([Not yet](#not-yet)).

1. **Give it a key.** An admin adds an API key named `alpaca`, role `RoofViewer`, on the web UI's
   [People](web.md#people) page (or with `POST /api/v4.0/Identity/ApiKeys`,
   [Managing people, keys and sessions](security.md#managing-people-keys-and-sessions)). Its value is shown once. Save
   it straight to a file only the server's user can read, on one line, and never on a command line:

   ```bash
   install -D -m 600 /dev/null ~/.config/hvo-roof-alpaca/key   # then paste the key into it with an editor
   ```

   The server refuses a key file that anyone but its owner can read or change.
2. **Build it** on the computer that runs it (Linux x64 or arm64, or macOS arm64, with the .NET 10 runtime), from
   `src/`:

   ```bash
   dotnet publish HVO.RoofControllerV4.Alpaca -c Release -o ~/hvo-roof-alpaca
   ```

3. **Run it** with its settings in the environment ([Settings](#server-settings)):

   ```bash
   Alpaca__Enabled=true \
   Alpaca__ListenAddresses__0=192.168.1.20 \
   Alpaca__AllowedNetworks__0=192.168.1.0/24 \
   Alpaca__ControllerUrl=https://192.168.1.10:8443 \
   Alpaca__ControllerCaCertificatePath=/etc/hvo-roof/ca.pem \
   Alpaca__ControllerApiKeyFile=$HOME/.config/hvo-roof-alpaca/key \
   dotnet ~/hvo-roof-alpaca/HVO.RoofControllerV4.Alpaca.dll
   ```

   Off, it says so and exits with 0. With settings it cannot use it names them and exits with 1, before it listens.
4. **Connect a client.** In NINA, choose the dome, and the safety monitor, from the Alpaca devices it discovers, or give
   it the server's address and port. Then open `http://<address>:11111/setup` in a browser to see what the server sees.

## Server settings

The settings are the `Alpaca` section, from the environment (`Alpaca__Port=11111`) or the command line
(`--Alpaca:Port=11111`). There is no settings file, and the controller's [settings catalogue](security.md#settings)
does not hold them.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Whether the server runs at all. |
| `ListenAddresses` | none (required) | The addresses it listens on: a LAN address, `127.0.0.1`, or `0.0.0.0` in a container whose published port is bound to the LAN address. Never a public address. |
| `Port` | `11111` | The Alpaca port. |
| `AllowedNetworks` | loopback and the private ranges | The networks (CIDR) it answers, discovery included. Each must lie within loopback and the private ranges. |
| `AllowedHosts` | none | Host names a request may name, besides IP addresses, `localhost` and this computer's name. |
| `ControllerUrl` | `http://localhost:8080` | The controller's API. |
| `ControllerApiKeyFile` | none (required) | The file holding the server's Viewer key, on one line, readable only by its owner. |
| `ControllerCertificateSha256`, `ControllerCaCertificatePath` | none | How to trust the controller's HTTPS certificate: its SHA-256, or the CA that issued it (the installer's). Not both. |
| `DataPath` | `~/.local/share/hvo-roof-alpaca` | Where `alpaca-devices.json` (the `UniqueID`s) and its lock file are kept. |
| `Location` | empty | The management API's `Location`, such as the observatory's name. |
| `RequestsPerSecondPerAddress` | `100` | The polling limit per client address. |
| `MaxConnectedClients`, `ConnectionIdleMinutes` | `32`, `60` | How many clients may be connected, and how long an idle one stays so. |
| `Discovery:Enabled` | `true` | Whether to answer discovery (only while the server is enabled). |
| `Discovery:Port` | `32227` | The UDP port discovery listens on. |
| `Discovery:AdvertisedPort` | `Port` | The port discovery tells clients, when it differs from `Port` (a container's published port). |
| `Discovery:IPv6` | `false` | Whether to answer IPv6 discovery (multicast `ff12::a1:9aca`) too. |

The log levels are the usual `Logging__LogLevel__*`. The server logs each `AbortSlew` with the client's address,
`ClientID` and `ClientTransactionID`, the refused requests (at most once a minute per address and reason), and which
key it reaches the controller with. It never logs the key.

## Discovery

Clients find the server by broadcasting `alpacadiscovery1` to UDP port 32227. The server answers to the sender with
`{"AlpacaPort":11111}`. The port is shared (`SO_REUSEADDR`), so another Alpaca server on the same computer can answer
too.
It answers only clients within `AllowedNetworks`, and of those only loopback ones while it listens on loopback alone,
since nothing else could reach it. With `Discovery:IPv6` it also answers the IPv6 multicast group `ff12::a1:9aca`.

**Behind Docker's bridge network**, the LAN's broadcasts never reach a container. There, run the responder by itself
on the host's network, telling clients the port the container publishes the server on:

```bash
Alpaca__Enabled=true Alpaca__Discovery__AdvertisedPort=11111 Alpaca__AllowedNetworks__0=192.168.1.0/24 \
  dotnet ~/hvo-roof-alpaca/HVO.RoofControllerV4.Alpaca.dll --discovery-only
```

`--discovery-only` needs no key and no controller. Running the whole server on the host's network instead would make
every process on the host a loopback client.

## Conformance

[ConformU](https://github.com/ASCOMInitiative/ConformU), ASCOM's conformance checker, checks the server in CI (the
`alpaca-conformu` job in [scenarios.yml](ci-runners.md)), against the controller and the [HAT emulator](emulator.md)
on the runner's loopback, never against hardware:

- **Pinned.** ConformU 4.5.3, downloaded from its release and checked against its SHA-256 in
  [`tests/alpaca/conformu.sh`](../tests/alpaca/conformu.sh).
- **Its settings** are [`tests/alpaca/conformu-settings.json`](../tests/alpaca/conformu-settings.json):
  `DomeOpenShutter` false, and the protocol check's strict mode and URL-structure checks on.
- **Four checks**, the Alpaca protocol check and the conformance check of Dome 0 and of SafetyMonitor 0, must report
  **no error and no issue**. The SafetyMonitor's run uses the same settings, its device changed; the controller behind it
  reads the emulator's Alpaca safety monitor as its weather source and lists the server's `UniqueID`s (made by the script
  for the run) as its own, so `IsSafe` is true end to end, and the script waits for that before ConformU starts.
  ConformU logs some failures, such as the shutter state straight after a command, as issues, so no errors alone is not
  enough. Every information message and configuration alert must be one
  [`tests/alpaca/conformu-expected.json`](../tests/alpaca/conformu-expected.json) lists, with why, and each listed one
  must be reported. In phase 1 these are the expected refusals: `CloseShutter` answered `0x40B` (the protocol check
  sends it whatever `DomeOpenShutter` says), and the shutter tests ConformU leaves out with `DomeOpenShutter` false. The
  rest, for both devices, are the status codes of the URL-structure checks' malformed URLs.
- **The roof never moves.** The script fails if the emulated roof moved or a relay was left on.

Run it locally from the repository root with `tests/alpaca/conformu.sh` (it builds the three programs; `--no-build`
uses the last build). It uses ports 47210 to 47230 (`HVO_ALPACA_TEST_PORT_BASE` moves them) and keeps its logs and
ConformU's reports in the directory it names. `tests/alpaca/check-conformu-results.py` has its own tests
(`python3 -m unittest discover -s tests/alpaca -p 'test_*.py'`).

> **Never point ConformU at an Alpaca server in front of the real roof.** `conformu conformance <url>` and
> `conformu alpacaprotocol <url>` run with `DomeOpenShutter` on, whatever a settings file says, and the protocol check
> sends `CloseShutter` even with it off. This phase's server refuses both, but a later phase's would move the roof.
> Use `conformance-settings` and `alpacaprotocol-settings` with the repository's settings file, against the emulator.

The contract tests in `tests/HVO.RoofControllerV4.RPi.Tests/Alpaca` check the envelope, the parameters, the
transaction numbers, the error table, connecting, `AbortSlew`, refused commands, an unreachable or silent controller,
the allowlist, the `Host` and `Origin` checks and the polling limit, against an in-memory controller.
`AlpacaSafetyMonitorTests` check the SafetyMonitor: `IsSafe` as the weather alone (Safe with an unparked mount; false
for unsafe, unknown or no required weather, an unreachable controller, and a controller that does not list its
`UniqueID`), its own connection, its members and
action, its `UniqueID` (and a phase 1 file gaining it), the setup page, and that reading it never moves or stops the
roof. `AlpacaSafetyLoopTests` point the controller's weather source at this server's own SafetyMonitor and check it is
refused. `AlpacaScenarios` stops the emulated roof with `AbortSlew` as it opens.

## Safety sources

Phase 2 of #220: the controller's [safety service](safety-service.md) reads other Alpaca devices, mounts'
`Telescope.AtPark` and weather `SafetyMonitor.IsSafe`, as safety sources, and decides with them whether Open counts as
Safe. This is the controller's own work, apart from the Alpaca server: it needs no Alpaca server running.

### Reading Alpaca devices

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
- **Never the controller's own SafetyMonitor.** It publishes the weather the controller reads, so reading it would loop.
  A weather source that is it is refused ([Never our own SafetyMonitor](#never-our-own-safetymonitor)).

### When Open counts as Safe

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

### Source settings

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
| `OwnAlpacaUniqueIds:N` | none | This installation's own Alpaca devices' `UniqueID`s, from the Alpaca server's setup page: a weather source with one of them is refused ([Never our own SafetyMonitor](#never-our-own-safetymonitor)). At most 16. Read at start, as the devices are. |

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

### When a device cannot be read

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
| is this controller's own Alpaca SafetyMonitor | `configuration error: the device is this roof controller's own Alpaca SafetyMonitor, which publishes the weather the controller reads (a loop)` ([Never our own SafetyMonitor](#never-our-own-safetymonitor)) |
| has no `UniqueID` its server lists, while `OwnAlpacaUniqueIds` is set | `the Alpaca server does not list this device's UniqueID, so it cannot be told from this roof controller's own SafetyMonitor` |
| cannot be reached | `the Alpaca server could not be reached (connection refused)`, `(host unreachable)`, `(host name not found)`, … |
| answers an HTTP error | `the Alpaca server answered HTTP 500` |
| answers something that is not the Alpaca JSON | `the Alpaca server's answer is not JSON`, `… has no ErrorNumber`, `… has no ErrorMessage`, `… has no ServerTransactionID`, `… has no true or false Value`, `… is too long to be an Alpaca answer` |
| answers ambiguously | `the Alpaca server's answer repeats a field` (one field twice, whatever its case), `… reports success with an error message` (`ErrorNumber` 0 with a non-empty `ErrorMessage`), `… is not the answer to this request` (a `ClientTransactionID` other than the one sent: a cached or replayed answer) |
| does not answer within `ReadTimeout` | `no answer within 5 s` |

For example: `Telescope 'Main' cannot be read (the device is not connected)`, or, once its last reading is too old,
`Telescope 'Main': the last reading (parked) is older than 60 s; the last read failed (no answer within 5 s)`.

A mount that cannot park can never show it is clear of the roof, so it is a configuration error, not "not parked". The
controller starts whatever the devices say (a device may be off at the time), and logs each failure when it starts.

### The weather alone

Besides the conditions, every status and `GET /api/v4.0/Safety` carry the weather alone, with no telescope:

| Field | Meaning |
|---|---|
| `weatherState` | `Safe` only when at least one weather source is required and every required one has a recent reading that says safe; `Unsafe` when a required one says unsafe; otherwise `Unknown`. |
| `weatherReasons` | Why it is not Safe, in the same words as `reasons`, or `No required weather source configured`. |

It is what the Alpaca server's SafetyMonitor publishes ([The weather as SafetyMonitor 0](#the-weather-as-safetymonitor-0)):
`IsSafe` true only while `weatherState` is `Safe`; Unknown counts as unsafe. It never includes telescope park.

### Never our own SafetyMonitor

The controller never reads its own weather back: a weather source that is this installation's own SafetyMonitor would
make the weather depend on itself. Such a source is refused with `configuration error: the device is this roof
controller's own Alpaca SafetyMonitor, which publishes the weather the controller reads (a loop)`, a failed read every
time, so it neither makes nor refreshes a reading.

The Alpaca server makes and keeps its devices' `UniqueID`s itself, where the controller cannot see them. So the
controller is told them: **`RoofSafety:OwnAlpacaUniqueIds`** lists this installation's own Alpaca devices' `UniqueID`s,
the Dome's and the SafetyMonitor's. The server's setup page and its log give the exact lines, for the secrets directory
on the Pi:

```text
RoofSafety__OwnAlpacaUniqueIds__0=<the Dome's UniqueID>
RoofSafety__OwnAlpacaUniqueIds__1=<the SafetyMonitor's UniqueID>
```

**Forgetting them cannot make a loop.** The server's SafetyMonitor says safe only while the controller's status lists its
`UniqueID`; until then `IsSafe` is false, and its `status` action, the setup page and the log say why. The controller
reads the list at start, with its weather sources, and its status publishes exactly the list those sources refuse: a
change takes a restart, so the list published is never one not yet enforced.

**Every read** of a weather source checks its identity first, with `GET` requests only, before `Connected` and
`IsSafe`, so a device that changes behind the address between two good reads (a hub retargeted, a server replaced) is
caught at the next read:

- **The device's own `DriverInfo`**: every one of this server's devices' begins `HVO Roof Controller Alpaca server`. A
  hub that relays a device passes its `DriverInfo` on as it is.
- **The `UniqueID` its server lists for it** (`/management/v1/configureddevices`, the SafetyMonitor with its number),
  whatever that server calls itself, compared with `RoofSafety:OwnAlpacaUniqueIds`: a hub that lists one of ours for the
  device is refused. That answer must be this request's own, as a device's must: every field once, the request's
  `ClientTransactionID` echoed and a `ServerTransactionID` (and an `ErrorNumber`, should it carry one, of 0), so a
  cached or replayed device list proves nothing and the read fails. With own `UniqueID`s listed, a source whose server does not list it (or serves no Management API,
  which the Alpaca API requires) is refused too, with `the Alpaca server does not list this device's UniqueID, so it
  cannot be told from this roof controller's own SafetyMonitor`: it cannot be told from ours.

**What it cannot catch**: a hub that relays our SafetyMonitor under another `UniqueID` and another `DriverInfo`. Do not
configure our SafetyMonitor as a weather source by any route. Such a source would only repeat the controller's own
weather: beside any other required weather source it can never make the weather Safe, since that other source still
decides; as the **only** required weather source it would keep whatever the weather last was. So keep at least one
required weather source that the controller reads from its own device.

### Testing

No test relies on a real device. The HAT emulator serves fake Alpaca mounts and safety monitors, modelled on the Alpaca
API, with their park, weather, connection and failure states set through its control API
([docs/emulator.md](emulator.md#alpaca-devices)); both compose files point the controller at them. ASCOM ConformU
checks the fakes themselves (the results are in that section).

| Tests | Cover |
|---|---|
| `Safety/AlpacaSafetySourceTests` | The adapters against the emulator's devices and against answers no device should give: GET only, the order of the requests, the transaction numbers, every failure's words, a mount that cannot park, a timeout, an unreachable server, no address in a failure or its inner exception, and our own SafetyMonitor refused (by its `DriverInfo`, and by the `UniqueID` its server lists for it, also behind a hub that names itself otherwise), at every read, so a device that changes between good reads, a replayed device list, or a primed loop never keeps the weather Safe; the own `UniqueID`s published are the ones enforced, a reload included. |
| `Safety/RoofSafetyServiceTests` | The readiness rule (a required weather source, every blocking scope clear, `Ignored` and `BlocksRoof: false`), the weather alone, the settings' validation, the start log. |
| `Emulation/EmulatedAlpacaTests` | The emulator's devices against the Alpaca API: the answer's shape, parameter names (any case in a `GET`, exact in a `PUT`), form-encoded `PUT`s, HTTP 400, `0x407` while not connected, a mount that cannot park, the fault injection, the control API. |
| `Controllers/SafetyAlpacaApiTests` | The controller with configured devices, through its real pipeline: the conditions on the status, what Open needs, Close and Stop never gated, the conditions never moving the roof, and no address in the log at any level. |
| `Scenarios/AlpacaSafetySourceScenarios` | The emulated plant with the production settings and the default Warn interlock, the devices over a real socket: unsafe weather refuses Open and moves nothing, weather turning unsafe while the roof opens stops nothing, Close is never gated, and a mount that stops answering asks Open for the override. |
| `Alpaca/AlpacaSafetyMonitorTests`, `Alpaca/AlpacaSafetyLoopTests` | The Alpaca server's SafetyMonitor, and the controller refusing it as a weather source ([Conformance](#conformance)). |
| `tests/emulator/compose-smoke-test.sh` | The containers: the conditions follow the emulator's devices, and the Warn interlock refuses Open while the weather is unsafe. |

### Decisions

From the owner (#220, 2026-10-30):

1. **Open readiness** (Q11): Safe only when at least one fresh weather source reports and every scope that can block
   the roof is clear. Each weather source is configured and set `Required` or `Ignored`.
2. **A scope's "blocks roof" setting** (Q9): the check asks whether the scope is clear of the roof, not whether it is
   parked. Parked is the only proof today; a camera or safe zones can show it later.

From the proposal's reviews, kept here:

3. The consumer only reads, never writes another program's device, and pins each device's address (re-discovery by
   UniqueID can be spoofed).
4. `ObservingConditions` is deferred: it needs per-sensor thresholds, hysteresis and freshness of its own.
5. The `SafetyMonitor` the Alpaca server publishes is weather-only (the proposal's Q4), and the controller never reads it
   back: our own devices' `UniqueID`s are refused as a weather source (approved by the owner on 2026-10-31).

## Not yet

Phases 1 and 2 leave these to later phases of #220 or to follow-ups:

- **Commands.** `CloseShutter` (phase 3) and `OpenShutter` (phase 4), with an Alpaca-only credential, the command
  history's `Alpaca` source (the client's `ClientID` and address), the owner's decisions on the operator lease, an
  unattended Close and Open readiness, `Alpaca:UnattendedOpen`, and a `--validate-deployment` check of the `Host`
  allowlist.
- **Deployment.** The server in the controller's image and container, the deploy script, Compose and the installer's
  Alpaca option (off by default), its settings in the settings catalogue and the UIs, its `UniqueID`s in backups, and the
  host-side discovery responder set up for a containerised server.
- **TLS and HTTP Basic authentication** on the Alpaca listener.
