# Alpaca server

`HVO.RoofControllerV4.Alpaca` serves the roof to astronomy software as an [ASCOM Alpaca](https://ascom-standards.org/api/)
**Dome** (a roll-off roof is a dome with a shutter and nothing else), so NINA, SGP, Voyager, ACP and Windows ASCOM
clients (through the ASCOM Platform's Alpaca Dynamic Client) can watch the roof with no plug-in of ours. The proposal,
its reviews and the owner's decisions are in #220.

It is a process of its own, apart from the controller and the [web UI](web.md). It reaches the controller only through
the client library (the REST API and the status hub), with its own API key, and it never sees the controller's settings
or secrets. It is **off by default**, listens only on the addresses it is given, and answers only its LAN.

This page covers **phase 1: a read-only Dome**. The server reads the roof and stops it (`AbortSlew`). It never opens or
closes it: `OpenShutter` and `CloseShutter` are refused. Later phases of #220 add reading other Alpaca devices as
[safety sources](safety-service.md) (phase 2), then `CloseShutter` (phase 3), then `OpenShutter` (phase 4). See
[Not yet](#not-yet).

## Contents

- [The roof as Dome 0](#the-roof-as-dome-0)
- [Error numbers](#error-numbers)
- [The protocol](#the-protocol)
- [Security](#security)
- [Running it](#running-it)
- [Server settings](#server-settings)
- [Discovery](#discovery)
- [Conformance](#conformance)
- [Not yet](#not-yet)

## The roof as Dome 0

The roof is `/api/v1/dome/0` (IDomeV3, `InterfaceVersion` 3). Every read comes from the last status the controller
published on its status hub. A read never asks the controller and never touches the HAT. A status the server can no
longer vouch for is never served: the controller cannot be reached, it has sent no status yet, or its hub has been
silent for more than 3 s (three heartbeats). Then the read is error `0x500` ([Error numbers](#error-numbers)).

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

- success: the controller stopped the roof and verified the relay register de-energized;
- `0x504`: the controller could not verify the relays off. Confirm at the roof that the motor has stopped;
- another error: the Stop was refused or did not arrive ([Error numbers](#error-numbers)).

Stop's answer carries the controller's status straight after it. That status replaces the one the server held, merged
in the controller's order with the hub's messages, so the next read reflects the stop even before the hub's next
message, and an older hub message cannot undo it. The command history records the Stop under the server's API key, with
the server's program and version as its `User-Agent`. Naming the Alpaca client there comes with phase 3.

## Error numbers

Alpaca errors are answered with HTTP 200, `ErrorNumber` and `ErrorMessage`. The table is fixed:

| Number | Name | When |
|---|---|---|
| `0x400` (1024) | NotImplemented | A member a roll-off roof does not have. |
| `0x401` (1025) | InvalidValue | (Not used in this phase.) |
| `0x407` (1031) | NotConnected | The client has not connected. Never for `AbortSlew`. |
| `0x40B` (1035) | InvalidOperation | `OpenShutter` and `CloseShutter` (commands disabled), and the controller's refusals for the roof's state: `FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `SafetyInterlockActive`, `SafetyOverrideRequired`. |
| `0x40C` (1036) | ActionNotImplemented | An action other than `status`. |
| `0x500` (1280) | Controller unavailable | The roof's state is unknown: the controller cannot be reached, has sent no status yet, or has been silent for more than 3 s. |
| `0x501` (1281) | Controller not initialized | The controller's `NotInitialized`. |
| `0x502` (1282) | Controller shutting down | The controller's `ShuttingDown`. |
| `0x503` (1283) | Hardware unavailable | The controller's `HardwareUnavailable`: it cannot read its safety inputs. |
| `0x504` (1284) | Relay state unverified | The controller's `RelayStateUnverified`: a Stop whose relays it could not verify off. |
| `0x505` (1285) | Controller refused the key | The controller does not accept the server's API key (revoked, rotated, or not allowed). |
| `0x50F` (1295) | Controller error | Any other failure. |

A property's error still carries a `Value`, as the Alpaca API's responses define it: its type's default (false, 0 or
empty), except `ShutterStatus`, whose `Value` on an error is 4 (Error), so a client that overlooks `ErrorNumber` still
does not see a roof that is fine.

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
  manufacturer, its version and `Location`) and `/management/v1/configureddevices` (Dome 0 and its `UniqueID`). Its
  answers carry `Value`, `ClientTransactionID` and `ServerTransactionID`.
- **The `UniqueID`** is made the first time the server starts, kept in `alpaca-devices.json` in its data directory, and
  never changed, so a client recognises the roof across restarts and upgrades. Back the file up with the server's
  settings. A file that cannot be read stops the server rather than being replaced: restore it from a backup.
- **The setup pages**, `/setup` and `/setup/v1/dome/0/setup`, show what the server is and does, the roof's
  `UniqueID` and state, and the connected clients, in HVO Dark. They only read: there is no form, and the settings are
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
  it). `AbortSlew` is never limited.
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
4. **Connect a client.** In NINA, choose the dome from the Alpaca devices it discovers, or give it the server's address
   and port. Then open `http://<address>:11111/setup` in a browser to see what the server sees.

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
| `DataPath` | `~/.local/share/hvo-roof-alpaca` | Where `alpaca-devices.json` (the `UniqueID`) is kept. |
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
It answers only clients within `AllowedNetworks`, and only loopback ones while it listens on loopback alone, since
nothing else could reach it. With `Discovery:IPv6` it also answers the IPv6 multicast group `ff12::a1:9aca`.

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
- **Both checks**, the Alpaca protocol check and the Dome conformance check, must report **no error and no issue**.
  ConformU logs some failures, such as the shutter state straight after a command, as issues, so no errors alone is not
  enough. Every information message and configuration alert must be one
  [`tests/alpaca/conformu-expected.json`](../tests/alpaca/conformu-expected.json) lists, with why, and each listed one
  must be reported. In phase 1 these are the expected refusals: `CloseShutter` answered `0x40B` (the protocol check
  sends it whatever `DomeOpenShutter` says), and the shutter tests ConformU leaves out with `DomeOpenShutter` false. The
  rest are the status codes of the URL-structure checks' malformed URLs.
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
`AlpacaScenarios` stops the emulated roof with `AbortSlew` as it opens.

## Not yet

Phase 1 leaves these to later phases of #220 or to follow-ups:

- **Commands.** `CloseShutter` (phase 3) and `OpenShutter` (phase 4), with an Alpaca-only credential, the command
  history's `Alpaca` source (the client's `ClientID` and address), the owner's decisions on the operator lease, an
  unattended Close and Open readiness, `Alpaca:UnattendedOpen`, and a `--validate-deployment` check of the `Host`
  allowlist.
- **Safety sources and the SafetyMonitor device** (phase 2).
- **Deployment.** The server in the controller's image and container, the deploy script, Compose and the installer's
  Alpaca option (off by default), its settings in the settings catalogue and the UIs, its `UniqueID` in backups, and the
  host-side discovery responder set up for a containerised server.
- **TLS and HTTP Basic authentication** on the Alpaca listener.
