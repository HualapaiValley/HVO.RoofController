# Alpaca server

`HVO.RoofControllerV4.Alpaca` serves the roof to astronomy software as an [ASCOM Alpaca](https://ascom-standards.org/api/)
**Dome** (a roll-off roof is a dome with a shutter and nothing else), so NINA, SGP, Voyager, ACP and Windows ASCOM
clients (through the ASCOM Platform's Alpaca Dynamic Client) can watch the roof with no plug-in of ours. The proposal,
its reviews and the owner's decisions are in #220.

It is a process of its own, apart from the controller and the [web UI](web.md). It reaches the controller only through
the client library (the REST API and the status hub), with its own API key, and it never sees the controller's settings
or secrets. It is **off by default**, listens only on the addresses it is given, and answers only its LAN.

This page covers **phase 1, the Dome that reads the roof and stops it** (`AbortSlew`), **phase 2, the weather**: the
server also serves the observatory's weather as an Alpaca **SafetyMonitor**, and the controller reads other Alpaca
devices (mounts and weather monitors) as its [safety sources](#safety-sources), **phase 3, `CloseShutter`**: an
Alpaca client with a key of its own may close the roof, when the controller allows it
([Alpaca Close](#alpaca-close)), and **phase 4, `OpenShutter`**: the same client may open it, only when the weather is
fresh and safe and every telescope that can block the roof is clear of it ([Alpaca Open](#alpaca-open)). Commands are
off until the controller's `AllowCommands` turns them on: `CloseOnly` allows Close, `OpenAndClose` both.

## Contents

- [The roof as Dome 0](#the-roof-as-dome-0)
- [The weather as SafetyMonitor 0](#the-weather-as-safetymonitor-0)
- [Error numbers](#error-numbers)
- [The protocol](#the-protocol)
- [Security](#security)
- [Running it](#running-it)
- [Server settings](#server-settings)
- [Discovery](#discovery)
- [Alpaca Close](#alpaca-close)
- [Alpaca Open](#alpaca-open)
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
| 4 Error | every other state: `Stopped`, `PartiallyOpen`, `PartiallyClose`, `Error`, `NotInitialized`, `Unknown`; and, until the next start, Stop or ClearFault, a start refused when its direction-change delay ended ([Waiting out the direction-change delay](#waiting-out-the-direction-change-delay)) |

A roof that is not moving is never reported as moving, with one exception: a start accepted and waiting out the
direction-change delay reads as moving ([Waiting out the direction-change delay](#waiting-out-the-direction-change-delay)).
Why it reads as Error (a stop part way, a latched fault and its
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
| `CloseShutter` | Closes the roof when the controller allows it, with the client's own key ([Alpaca Close](#alpaca-close)). Answers once the controller has started the Close, refused it, or found nothing to do. |
| `OpenShutter` | Opens the roof when the controller allows it, with the client's own key ([Alpaca Open](#alpaca-open)). Answers once the controller has started the Open, refused it, or found nothing to do. |
| `Action` | `status` only: a line describing the shutter, the roof, the last stop, the fault latch, the last error, the safety conditions, the Alpaca commands' state (on or off, the human Stop hold, a person's operator lease), and which controller answers (its name, instance and HAT mode). Any other action is `0x40C`. No action moves the roof or clears a fault. |

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
since restarted) is never served: only the hub moves the server to a new instance. The command history records the
Stop under the server's own account, with the server's program and version as its `User-Agent`; the server's log names
the client (its address, `ClientID` and `ClientTransactionID`).

**An Alpaca client's `AbortSlew` does not hold off Alpaca commands.** It is the Alpaca server's own Stop (its own
account, from an address the controller trusts), so it does not set the [human Stop hold](#the-human-stop-hold) and
drops no override grant: the next `OpenShutter` or `CloseShutter` is judged as any other. To hold Alpaca commands off,
press Stop in the web UI, the terminal, the kiosk or `hvo-roof` (each sets the hold), or set `AllowCommands` to `Off`.
An `AbortSlew` that comes while an `OpenShutter` or `CloseShutter` waits for its ticket refuses that command (`0x40B`
"Aborted"): nothing is sent for it.

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
| `0x40B` (1035) | InvalidOperation | An `OpenShutter` or `CloseShutter` not authorised (no credential, a malformed or wrong one), overtaken by an `AbortSlew` ("Aborted"), or refused by the controller's rules ([Close](#what-the-controller-checks), [Open](#the-open-rule): `AlpacaCommandRefused`, `Superseded`); and the controller's refusals for the roof's state: `FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `SafetyInterlockActive`, `SafetyOverrideRequired`. |
| `0x40C` (1036) | ActionNotImplemented | An action other than `status`. |
| `0x500` (1280) | Controller unavailable | The roof's state, and the weather, are unknown: the controller cannot be reached, has sent no status yet, or has been silent for 3 s. Also an `OpenShutter` or `CloseShutter` the controller did not answer in time: it is never retried, so check the roof and send it again if it is still wanted. |
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

- **A separate process with its own account.** It runs apart from the controller (an unauthenticated listener in the
  safety process is avoided) and from the web UI. It reaches the controller with the key of its own account, role
  **`RoofAlpacaServer`**: a Viewer that may also report its own refusals (`POST api/v4.0/alpaca/events`), and whose
  Stop (`AbortSlew`) sets no [human Stop hold](#the-human-stop-hold). The controller accepts that key only from
  loopback and `RoofController:Alpaca:ServerAddresses`. Revoking the key cuts the server off: reads then answer
  `0x505`. A phase 1 server's plain Viewer key still reads and stops, but then every `AbortSlew` sets the hold and its
  reports are refused; the server says so in its log at start, and warns when its key has a higher role.
- **Commands carry the client's own key.** `OpenShutter` and `CloseShutter` need the key of the **External Alpaca
  account** (role `RoofAlpacaCommander`), sent by the client, which the server passes on to the controller for that
  command alone. The server holds no key that can open or close the roof. Reads, the common members and `AbortSlew` need
  no credential and never pass one on ([The credential](#the-credential)).
- **Off by default, LAN-only.** It starts nothing unless `Alpaca:Enabled` is true, and listens only on the
  `ListenAddresses` it is given. A public address is refused. Use this computer's LAN address, or `127.0.0.1`.
- **An address allowlist.** Only clients within `AllowedNetworks` are answered (403 otherwise), discovery included.
  Deployment's `GET /management/apiversions` probe is also answered from loopback when a LAN-only allowlist excludes
  it. That exception exposes only protocol versions; device routes, commands and the configured-device list still
  require an allowed address. The Host and Origin checks also apply to the probe.
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
- **Only Open, Close and Stop.** The server sends the controller an Open or a Close (a ticket, then the command), a Stop
  and its refusal reports, never Clear fault, a lease renewal, a grant or a setting; the External account's key can do
  nothing else at the controller either.
- **Plain HTTP.** The Alpaca listener has no TLS yet (#220 leaves it for later). So the External key crosses the LAN in
  the clear with every `OpenShutter` and `CloseShutter` (HTTP Basic is an encoding, not encryption), and anyone who can
  watch that LAN can take it. With `AllowCommands` `OpenAndClose`, that is a key that can **open** the roof, though only
  when the controller would allow it: fresh, safe weather and every blocking telescope clear, or a person's grant, and
  never past the Stop hold, a person's lease or a latched fault. It can never clear a fault, take a grant or change a
  setting. Keep the listener on the observatory's own LAN, start with `CloseOnly`, and rotate the key (the People page)
  if it may have been seen: rotating it ends every grant. The server's own connection to the
  controller can be HTTPS (`ControllerCertificateSha256` or `ControllerCaCertificatePath`, as the
  [client library](../src/HVO.RoofControllerV4.Client/README.md#connecting) checks them).

## Running it

The controller image includes the server, off by default. Enable it through the [installer](install.md),
[deploy script](deployment.md) or optional Compose overlay below. A source build can still run separately.
Enabling the server leaves `RoofController:Alpaca:AllowCommands=Off`: deployment never enables Open or Close.

### Container deployment

The installer's **Alpaca server** option is off by default. When enabled, give this host's private IPv4 LAN address
and the published TCP port (default `11111`). It creates a separate `RoofAlpacaServer` key without displaying it,
keeps the device identities in `/var/lib/hvo-roof-alpaca/alpaca-devices.json`, and includes them in backup and restore.
An upgrade keeps an existing opt-in; an explicit disable removes the listener and discovery responder.

For the deploy script, provision the dedicated server key as described below and set `ALPACA_ENABLED=true`,
`ALPACA_LAN_ADDRESS` to the host's private IPv4 LAN address, and `ALPACA_API_KEY_FILE` to the root-readable key file
on that host. `ALPACA_PORT` defaults to `11111`; `ALPACA_DATA_DIR` defaults to `/var/lib/hvo-roof-alpaca` and must
already exist. The directory is mounted read-write at `/var/lib/hvo-roof-alpaca`; keep it across replacements.
`ALPACA_DISCOVERY_PORT` defaults to UDP `32227`. Never bind the TCP port to `0.0.0.0`, a public address or an overlay
address. Deployment validates Alpaca's settings and existing identities before stopping the previous controller,
and verifies the supervised process and read-only management listener after starting it. Failure restores the
previous deployment and its discovery responder. `--stop` retires discovery too; `--rollback` matches discovery to
the restored controller's saved settings and exact image.

For Compose, explicitly add [`docker-compose.alpaca.yaml`](../src/HVO.RoofControllerV4.RPi/docker-compose.alpaca.yaml)
to the base file, selecting either `pi` or `pi-lan-http`. Set `HVO_ALPACA_LAN_ADDRESS` (required),
`HVO_ALPACA_API_KEY_FILE` (default `/etc/hvo-roof/secrets/alpaca-server-key`) and, optionally, `HVO_ALPACA_PORT`,
`HVO_ALPACA_DATA_DIR` and `HVO_ALPACA_DISCOVERY_PORT`. Create the data directory first, mode `0755`.
The release supplies both Compose files with the same pinned image. For example, with the variables already set:

```bash
docker compose -f docker-compose.yaml -f docker-compose.alpaca.yaml --profile pi config --quiet
docker compose -f docker-compose.yaml -f docker-compose.alpaca.yaml --profile pi up -d
```

The base file alone publishes no Alpaca port and runs no discovery responder. To disable an existing Compose
installation, bring down the composition with both files before bringing up the base file alone, so discovery and
the old published port are removed.

Inside the controller container, the supervisor starts Alpaca only with `Alpaca__Enabled=true`, as its own
unprivileged user (`alpaca`, UID/GID `1655`). It copies the account key into a root-controlled private runtime
directory, gives that process only `Alpaca__*` configuration and general runtime variables, and preserves its device
identities on the data mount. Those GUIDs are public (`0644`, directory `0755`); the account key stays private.
Deployment also waits for the host discovery responder to bind its UDP socket; a process that is alive without
answering discovery fails verification. A server crash restarts that process alone. Its preparation and shutdown have their own deadlines; a hang cannot
hold up relay-off after a controller exit or extend the container's 27-second stop bound. The host-network process
runs `--discovery-only`, with no controller key, data mount or HAT access, advertising the published TCP port.

The web System page, native About/System screens, terminal System page and `hvo-roof alpaca status` show the
published port, both device UniqueIDs and each device's connected-client count. A heartbeat older than three seconds
makes the server unavailable and the counts unknown; its validated IDs remain visible. The heartbeat contains no
addresses or credentials. Listener settings remain deployment-local; the controller's existing settings catalogue
contains its command policies and safety sources. The kiosk's [About view](kiosk.md#finding-the-installed-version)
shows the same details, including the full device identities, while locked.

### Running from source

1. **Give it a key.** An admin adds an API key named `alpaca-server`, role `RoofAlpacaServer` (the Alpaca server's
   own account), on the web UI's [People](web.md#people) page, with `hvo-roof keys add --role alpaca-server`, or with
   `POST /api/v4.0/Identity/ApiKeys` ([Managing people, keys and sessions](security.md#managing-people-keys-and-sessions)).
   Its value is shown once. Save it straight to a file only the server's user can read, on one line, and never on a
   command line:

   ```bash
   install -D -m 600 /dev/null ~/.config/hvo-roof-alpaca/key   # then paste the key into it with an editor
   ```

   The server refuses a key file that anyone but its owner can read or change. When the server runs on another computer
   than the controller, the controller must trust its address: `RoofController:Alpaca:ServerAddresses`
   ([Controller settings](#controller-settings)).
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
4. **Connect a client.** In NINA, connect the dome through the ASCOM Alpaca Dynamic Client ([Clients](#clients)): a
   dome picked from NINA's own Alpaca discovery list reads the roof and stops it, but cannot send a credential, so it
   can never close it. The safety monitor needs no credential and can come from either. Then open
   `http://<address>:11111/setup` in a browser to see what the server sees.
5. **To let clients close the roof** (optional): an admin adds a second key, role `RoofAlpacaCommander` (the External
   Alpaca account; `hvo-roof keys add --role alpaca-commander`), and gives it to each client as its credential
   ([Clients](#clients)); then someone with the local credential sets `RoofController:Alpaca:AllowCommands` to
   `CloseOnly` ([Controller settings](#controller-settings)). Measure the telescopes' latency first and size
   `ScopeRereadBudgetMs` from it.
6. **To let clients open the roof too** (optional, once Close has worked for a while): check that at least one weather
   source is `Required` and every telescope that can reach the roof's path is configured with `BlocksRoof` (or decide
   deliberately to set `AllowOpenWithoutBlockingScopes`, [The Open rule](#the-open-rule)), run the client against the
   [HAT emulator](emulator.md) first ([Clients](#clients)), then set `AllowCommands` to `OpenAndClose` with the local
   credential. (Turning commands on from `Off` sets the human Stop hold: a person clears it before the first Alpaca
   command. Going from `CloseOnly` to `OpenAndClose` leaves the hold as it is, and drops every grant.)

## Server settings

The settings are the `Alpaca` section, from the environment (`Alpaca__Port=11111`) or the command line
(`--Alpaca:Port=11111`). There is no settings file, and the controller's [settings catalogue](security.md#settings)
does not hold them.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Whether the server runs at all. |
| `ListenAddresses` | none (required) | The addresses it listens on: a LAN address, `127.0.0.1`, or `0.0.0.0` in a container whose published port is bound to the LAN address. Never a public address. |
| `Port` | `11111` | The Alpaca port. |
| `PublishedAddress` | none | When supplied by deployment, one private IPv4 LAN address on which Docker publishes TCP. Wildcard, loopback, public and overlay addresses are refused before listening. |
| `StatusFilePath` | none | Optional sanitized server heartbeat for the controller; the supervisor sets `/run/hvo-roof/alpaca-status/status.json`. |
| `AllowedNetworks` | loopback and the private ranges | The networks (CIDR) it answers, discovery included. Each must lie within loopback and the private ranges. The read-only loopback `GET /management/apiversions` deployment probe remains available with a LAN-only list. |
| `AllowedHosts` | none | Host names a request may name, besides IP addresses, `localhost` and this computer's name. |
| `ControllerUrl` | `http://localhost:8080` | The controller's API. |
| `ControllerApiKeyFile` | none (required) | The file holding the server's own account's key (`RoofAlpacaServer`), on one line, readable only by its owner. It never holds the External account's key: clients send that. |
| `ControllerCertificateSha256`, `ControllerCaCertificatePath` | none | How to trust the controller's HTTPS certificate: its SHA-256, or the CA that issued it (the installer's). Not both. |
| `DataPath` | `~/.local/share/hvo-roof-alpaca` | Where `alpaca-devices.json` (the `UniqueID`s) and its lock file are kept. |
| `Location` | empty | The management API's `Location`, such as the observatory's name. |
| `RequestsPerSecondPerAddress` | `100` | The polling limit per client address. |
| `MaxConnectedClients`, `ConnectionIdleMinutes` | `32`, `60` | How many clients may be connected, and how long an idle one stays so. |
| `Discovery:Enabled` | `true` | Whether to answer discovery (only while the server is enabled). |
| `Discovery:ReadyFilePath` | none | Optional absolute path for the host responder's bound-socket marker; deployment sets `/tmp/alpaca-discovery-ready` and waits for it. It contains no addresses or credentials. |
| `Discovery:Port` | `32227` | The UDP port discovery listens on. |
| `Discovery:AdvertisedPort` | `Port` | The port discovery tells clients, when it differs from `Port` (a container's published port). |
| `Discovery:IPv6` | `false` | Whether to answer IPv6 discovery (multicast `ff12::a1:9aca`) too. |

The log levels are the usual `Logging__LogLevel__*`. The server logs each `AbortSlew` and each `CloseShutter` with the
client's address, `ClientID` and `ClientTransactionID` and what came of it, the refused requests (at most once a minute
per address and reason), and which key it reaches the controller with. It never logs a key or a credential.

The server's settings and the controller's can share one environment (one container's): each reads only its own
section, `Alpaca` here and `RoofController:Alpaca` and `RoofController:Lease` at the controller.

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

## Alpaca Close

Phase 3 of #220: an Alpaca client may close the roof with `CloseShutter`. The controller alone decides each Close,
inside the same lock and with every check an ordinary Close has; the Alpaca server only refuses what it can see itself
(a client not connected, no usable credential, an `AbortSlew` that overtook the Close), forwards the rest, and words
the controller's answer. Nothing moves on its own: no Close is queued, retried or replayed, there is no automatic
Close on a disconnect or unsafe weather, and a refused Close needs a new `CloseShutter`. The owner's decisions are in
#220; the plan this follows is its phase 3.

Commands are **off** until someone with the local credential sets `RoofController:Alpaca:AllowCommands` to
`CloseOnly` or `OpenAndClose` ([Controller settings](#controller-settings)). An `OpenShutter` goes the same way, with a
rule of its own ([Alpaca Open](#alpaca-open)).

### How a Close goes

1. The client sends `CloseShutter` with its credential ([The credential](#the-credential)). The server refuses at once
   a client not connected (`0x407`), and a missing, malformed or conflicting credential (`0x40B` "Not authorised"),
   before anything reaches the controller.
2. The server asks the controller for a **ticket** (`POST api/v4.0/alpaca/shutter/ticket`) with the client's key. The
   ticket is single use, for one Close, from this controller instance, and expires 5 s after it is issued, on the
   controller's own clock. Issuing it starts a fresh read of every blocking telescope
   ([The telescopes, read again](#the-telescopes-read-again)).
3. If an `AbortSlew` came in the meantime, the server refuses the Close (`0x40B` "Aborted") and sends nothing more.
4. The server sends the Close with its ticket (`POST api/v4.0/alpaca/shutter/close`). The controller waits for the
   telescopes' read, then takes its lock and runs [its checks](#what-the-controller-checks), and starts the Close,
   finds nothing to do, or refuses.
5. The server answers the client. A started Close answers with `ShutterStatus` Closing at once (the controller's
   status after the start is served, as `AbortSlew`'s is); a refused one leaves the status as it was. A Close the
   controller did not answer in time is `0x500`, never retried: check the roof, and send it again if it is still
   wanted.

A delivery of the same ticket twice gets the first answer, never a second start; one that arrives while the first is
still being decided waits for that answer. The controller keeps its answers for 10 minutes after a ticket's deadline,
then forgets the ticket: it is refused from then on, as is any ticket from before a controller restart.

**How long a Close may take to answer**: the re-read budget (`ScopeRereadBudgetMs`, 600 ms by default), plus whatever
is left of the [direction-change delay](#waiting-out-the-direction-change-delay), plus 400 ms. A Close whose ticket
is already too old for that is refused (`DeadlineExceeded`) before it uses a grant. The server waits for the
controller that long plus 1 s.

### The credential

`CloseShutter` needs the key of the **External Alpaca account**: an API key with the role `RoofAlpacaCommander`, made
on the [People](web.md#people) page or with `hvo-roof keys add --role alpaca-commander`. A client sends it either way:

- the header `X-HVO-Api-Key: <key>`, or
- HTTP Basic, with the key as **both** the user name and the password.

Both, with different keys, or a Basic pair whose halves differ, is refused. The server compares in constant time,
never logs the key, and passes it to the controller for that Close alone (the ticket and the Close), never on another
request; its own account's key is never used for a Close. Reads, the common members, `Connected` and `AbortSlew` need
no credential, ignore one that is sent, and work with commands off, a fault latched or unsafe conditions.

The controller accepts the External key only from the Alpaca server's addresses: loopback, and
`RoofController:Alpaca:ServerAddresses`, judged on the connection's own peer address, never on a header a proxy adds.
From any other address the right key is refused. The account may do nothing else: it may ask for a ticket, send a
Close (and, under `OpenAndClose`, an Open: [Alpaca Open](#alpaca-open)), and read the status (`GET RoofControl/Status`,
`GET Safety`); every other route answers 403, Stop and the ordinary Open and Close included. No other key and no person's session may use the Alpaca routes.

**Guessing is slowed down.** After three refusals in a row from one client address, the server delays each further
"Not authorised" answer, by 100 ms, doubling, at most 2 s, and forgets the address 10 minutes after its last refusal.
It never delays forwarding a command, never refuses a correct key, and never touches reads or `AbortSlew`.

### What the controller checks

In this order, under the lock that starts the relays:

1. **Commands on** (`AllowCommands` is `CloseOnly` or `OpenAndClose`), and the External account, its keys,
   `AllowCommands` and `ServerAddresses` unchanged since the ticket (`AuthorizationChanged` otherwise).
2. **The hard protections**, as for any Close: not initialised, shutting down, the HAT unavailable, the relay register
   unverified, a latched fault, the drive fault input, contradictory limits, the drive still running.
3. **A person in charge.** The [human Stop hold](#the-human-stop-hold) (`StopHold`) and a person's
   [operator lease](#the-operator-lease) (`LeaseHeld`) refuse every Alpaca Close.
4. **What the Close would do.** Already closed: success, nothing sent. Already closing: success, nothing changes (no
   relay write, no new deadline, no grant used). Opening (or about to): a reversal. Otherwise it is a start.
5. **In time** for its answer's deadline (`DeadlineExceeded`).
6. **The grant is used up here**: from this point any [override grant](#override-grants) for a Close is spent, whatever
   follows.
7. **Nothing came after the ticket**: a Stop, a protective stop or another start since the ticket was issued refuses
   it (`Superseded`). A refusal, a repeat or a no-op never counts as one.
8. **Never a person's motion**: a reversal of an Open on a person's command, or of one nobody in particular started (a
   local input), is refused (`PersonsMotion`), the grant already used up; an Alpaca client never reverses a person's
   motion. A reversal of an Alpaca client's Open is admitted like a start (the relays go off, the delay is waited out,
   everything is checked again).
9. **The Close rule**: every telescope that can block the roof is clear of it, from a read made after the Close
   arrived; otherwise a grant must lift every reason (`NotSafe`, with the reasons, and an alert).

Then the Close starts, or [waits](#waiting-out-the-direction-change-delay). Any refusal leaves the roof as it was and
nothing queued. The refusal's words reach the client as the Alpaca `ErrorMessage`, with `0x40B`.

### The Close rule

**A Close is Safe when every telescope set `BlocksRoof` is clear of the roof** (today: parked). The weather never gates
a Close: unsafe, unknown, stale or no weather source never refuses one. A blocking telescope whose state is unknown
(its server down, a failed or timed-out read, a configuration error such as `CanPark` false) is not clear, so the Close
is refused and waits for a person's grant. Each configured blocking telescope missing from the safety service's list
counts as unknown.

With no blocking telescope configured, a Close checks nothing: it is allowed, and while commands are on every UI shows
"Alpaca Close checks no telescope", which the controller also logs at start. `RoofSafety:InterlockMode` does not
apply to an Alpaca Close.

Once the roof moves, it keeps moving when a telescope unparks or the weather turns: a UI alert says so. Every hard stop
(Stop, limits, the drive fault and running inputs, the watchdog, the fault latch) acts as always.

### The telescopes, read again

A stored "parked" is never trusted for a start: every Alpaca Close is decided on a read made after it arrived. Issuing
its ticket asks each blocking telescope for `connected` and `atpark` at once (and `canpark`, until a full read since
the controller started has shown it true), all telescopes in parallel, with `GET`s only. Clear needs `Connected` true,
`AtPark` true and `CanPark` true, each a clean answer. So NINA's park, then Close, closes, even when the background
poll last saw the mount unparked.

- **The budget**: the read must finish within `ScopeRereadBudgetMs` from the ticket; a telescope that does not answer
  in time is unknown, and the Close is refused ("scope re-read timed out") and waits for a grant. Set the budget to at
  least 250 ms plus the slowest of those `GET`s' p99 plus 100 ms: each status carries every telescope's p95 and p99 over
  its last 200 `GET`s (polls and re-reads alike), and while commands are on every UI warns when the budget is too small
  for them.
- **Rate limits**: at most two reads in flight per telescope, issued at least 250 ms apart; a Close uses only a read
  issued after it arrived, sharing one with other Closes. A burst of Closes reads each telescope a few times, not once
  per Close.
- **Newer wins**: a read's result never replaces a newer one, whether the newer came from a poll or another read.
- A Close that becomes a start with no read issued after its arrival is refused (`NoFreshEvidence`).

### Override grants

A person may let **one** refused Close through: a **grant**, made in the web UI, the terminal (or
`hvo-roof alpaca grant`, which requires a controlling foreground terminal and a fresh typed confirmation) or the
kiosk from the refusal's alert, by a person with the Operator role (on the kiosk with their own PIN). Never through
Alpaca, and never with an API key (403).

- **It lifts only the reasons it was given**, by key (`scope-not-clear:<telescope>`, `scope-unknown:<telescope>`; a
  re-read that timed out is `scope-unknown`). A Close refused for any other reason (say a second telescope unparked
  since) is refused again, with a fresh alert, and needs a new grant.
- **One Close each**: the next Close that gets past the checks above to step 6 uses it up, whatever then happens to
  that Close: started, refused for a reason it does not lift, superseded, or a failed relay start. A repeat or a no-op
  does not use it. Two Closes at once: exactly one start.
- **For a while**: `OverrideWindowMinutes` (5 by default, 1 to 15), on the controller's monotonic clock, so changing
  the wall clock neither lengthens nor revives one. One grant at a time; a new one replaces it and starts the window
  again. Any UI can revoke it.
- **Dropped** when the controller restarts, `AllowCommands`, `AllowOpenWithoutBlockingScopes`, `ServerAddresses`,
  `OverrideWindowMinutes` or `InterlockMode` changes, the External account's keys change (added, rotated, revoked or re-roled), a person takes
  the operator lease, or a Stop sets the [hold](#the-human-stop-hold).
- **Never a way past** the hard protections, the hold, a person's lease or `AllowCommands`.
- **The person who grants steps away, for as long as the grant lasts.** A grant never lifts a person's lease, so the
  granting person's own lease ends with it, in every one of their sessions. While the grant lasts their UI takes it back
  only when they do something there, and that drops the grant. A grant the same person makes again keeps every one of
  their sessions stepped away, also while a Close that used the earlier grant is still waiting out the direction-change
  delay: the new grant holds them away once that Close starts or is refused. Once every grant of theirs is gone (the
  Close that used it decided, which may be after it waits out the direction-change delay, or the grant lapsed, revoked
  or dropped), their UI holds the lease again with its next heartbeat. Another person's lease still refuses the Close.
- **Audited**: the log and the command history name who granted what, from which UI, and the Close that used it,
  with the reasons at grant and at use. A grant is kept in the controller's memory only.

`hvo-roof alpaca grant` uses the same terminal and confirmation checks for **Open and Close** grants: all standard
streams on the same controlling foreground tty, Linux tty/session matching, no known automation environment marker,
then typing the command kind and a fresh displayed six-digit value. Redirected answers, common detached/background pty wrappers and
fixed `yes` answers are refused. These are practical checks, not proof of human presence: a program that obtains a
controlling foreground pty, hides its markers and reads the challenge can still answer it; a determined local root
user can always script a pty. The [CLI grant rules and their limits](cli.md#alpaca) describe the checks.

A Close grant lifts only a Close's reasons; an Open has grants of its own ([Open grants](#open-grants)).

### The human Stop hold

**Every Stop sets the hold**, except the Alpaca server's own `AbortSlew`: a Stop from the web UI, the terminal, the
kiosk, `hvo-roof`, or the API with any other key or a person's session leaves Alpaca commands refused (`StopHold`), and
drops any grant, until a **person** clears the hold in the web UI, the terminal or the kiosk (`DELETE
api/v4.0/alpaca/hold`, a person's session; every API key 403; audited). The Alpaca server's `AbortSlew` counts as its
own only with its own account's key from a trusted address; with any other key it sets the hold too. The controller
sets the hold when it starts, and whenever Alpaca commands are turned on, so after any restart, and before the first
Alpaca command after commands go on, Alpaca waits for a person. The roof's protective stops (limits,
the watchdog, the drive inputs) do not set it: the ones that matter latch a fault, which refuses every Close until
cleared. A hold left overnight blocks Alpaca's Close, as a person's lease does.

### The operator lease

**While a person holds the operator lease, every Alpaca Close is refused** (`LeaseHeld`, naming them). A person's UI
holds it while it is open with an Operator's session (the web UI and the terminal always, the kiosk and the Mac app
while commands are on, when it matters; `hvo-roof`'s commands never), with a heartbeat every 10 s
(`POST api/v4.0/alpaca/lease`); it ends when the UI closes or signs out, or its heartbeats stop for 30 s.

- **Who holds it**: an Operator's session, so `hvo-roof`'s terminal interface run with the local credential on the
  controller itself (no person's session) holds none; nor do API keys.
- **Idle**: while commands are on, a person's lease lapses after `OperatorLeaseIdleMinutes` (10 by default, 1 to 120)
  with no input from them. Heartbeats alone never keep it, and the idle clock starts no earlier than commands going
  on. Each UI warns a minute before, then shows "Alpaca may now move the roof"; the person's next input takes the lease
  again. With commands off it never lapses for want of input.
- **Never stops a motion**: taking the lease leaves an Alpaca Close running (it blocks the next Alpaca command), and an
  idle lapse during the person's own motion waits until that motion ends. A person's motion still stops as it always
  has when their own Open or Close lease (`OperatorLeaseTimeout`) expires.
- **Not a maintenance lock-out**: to keep Alpaca from moving the roof while someone works on it, press Stop (the hold),
  set `AllowCommands` to `Off`, or open the isolator.

### Waiting out the direction-change delay

`RoofControllerOptionsV4:DirectionChangeDelay` (0 by default: no wait, as before; at most 10 s; local credential only)
makes every start, ordinary or Alpaca, wait that long after the relays were last verified off. A start that must wait
is admitted first (its grant used), then waits **without holding the lock**, so a Stop during the wait answers at
once, and then runs the checks again (the hold, a person's lease, commands on, the account unchanged, the Close or
[Open rule](#the-open-rule) on the newest readings, for an Open the interlock's mode too, its deadline, and nothing
having come since) before it energises a relay. If those refuse now, the roof stays stopped, the command is refused
and an alert says so. During the wait (a Close's, or an Open's alike):

- a second Alpaca command in the same direction joins the waiting one and gets its answer;
- a person's own command in that direction takes the waiting start over as theirs (under their lease), still waiting
  out the delay;
- a command the other way is a reversal of the waiting one: an Alpaca client's is admitted or refused under its own
  rule (refused, the waiting one goes on; admitted, it replaces it and waits out what is left of the delay), and a
  person's replaces it; a Stop cancels it;
- a request refused for its own fault (a bad ticket, the wrong kind) changes nothing.

**A limit asserting refuses a waiting Close (a known limitation).** Every time a limit switch asserts, the controller
raises its relay fence, whether or not a relay is on, so a start waiting out the delay is cancelled (`Superseded`), and
so is an Alpaca Close whose ticket came before it, even with every relay off. That happens when the roof coasts onto a
limit while a Close waits (an `AbortSlew` stopped it just short of the open limit, say), and when a limit chatters at
rest between an Alpaca client's ticket and its Close, even with no delay. It fails safe: nothing moves and the roof
stays where it is, but the Close is refused. The client should send the Close again (a retry in a NINA sequence, say, or
the weather trigger's next Close). It is tracked for a follow-up.

**Status during the wait** (an exception to "a roof not moving is never reported as moving"): the waiting start reads
`ShutterStatus` Closing (Opening for an Open) and `Slewing` true, since it has been accepted and will start unless something stops it. A start
refused when the wait ends reads `ShutterStatus` Error, wherever the roof stands (part way, or at a limit), with why in
the `status` action, until the next start, Stop (a Stop that had to wait for the controller's lock, once it has run) or
ClearFault, so a client polling the shutter sees its command failed. The controller's own status, and so every UI, keeps
the roof's real state (Open at the open limit, say), with the same reason as its last error and in an alert; its
`isContinuationRefused` says a refused continuation is being reported.

### Alerts

The web UI, the terminal and the kiosk show an alert for each Alpaca Close or Open refused for its policy, a person's
lease, the hold or a latched fault; for one refused when its wait ended; for a telescope changing while an Alpaca Close
or Open moves the roof, and for the weather changing while an Alpaca Open does; and for a Close not authorised (refused
by the server or by the controller), naming the client's address and `ClientID`, never the key. Each alert names its
command (`Close` or `Open`); a grant made from it is of that kind. Alerts of the same kind and reasons are counted
together with the last time, and a person acknowledges them (audited). The persistent warnings ("Alpaca Close checks no
telescope", "Alpaca Open checks no telescope" while `AllowOpenWithoutBlockingScopes` lets the weather alone open the
roof, a re-read budget too small for the measured latency) show while commands are on. An Open not authorised, an Open
refused under `CloseOnly`, and anything with commands off, are only logged.

The Alpaca server reports its own refusals (a missing or malformed credential) to the controller
(`POST api/v4.0/alpaca/events`, its own account from a trusted address only): the kind, the client's address,
`ClientID` and `ClientTransactionID`, never credential data.

### History

The command history records each Alpaca Close and Open (and each refusal) with the source `Alpaca`: the account and key name,
how the client authenticated (header or Basic), the client's address, `ClientID` and `ClientTransactionID` as the
Alpaca server reported them (labelled so; never used to authorise), the address the controller itself saw, the grant
it used and the reasons. Never a key or a Basic header.

### Command fencing

Every command that may start motion carries the controller's **command generation**, which every start and every way
the relays go off (Stop, a protective stop, shutdown) moves on. A relay write for a start whose generation is older
than the last relays-off write is dropped, so a start that passed its checks just before a Stop can never energise a
relay after it. A person's Stop first sets the human Stop hold, an in-memory change that waits for nothing (its change
notification runs on its own), so no Alpaca Close is admitted in between; then Stop raises the fence, before it waits
for anything. Stop has one deadline, 2 s from its start, for its wait for the lock and every HAT access it makes: the
stop of a watchdog, lease or drive deadline that has passed but not yet been acted on (which Stop makes first, and
then has nothing left to write if it verified the relays off), its own relays-off writes and read-back, and the LEDs.
An access gets at most what is left of the deadline, and none is sent once it has passed. A Stop whose deadline passes
before its HAT work has ended, the LEDs' included, answers `RelayStateUnverified` (`0x504` to an `AbortSlew`), and its
relays-off work goes on as soon as the lock is free, each access at its own limit, until a read-back shows the relays
off; nothing switches a relay on meanwhile. If the bus is too slow for the relay-off writes and read-back to finish in
the remaining stop bound, the stop remains unverified and latches `RelayVerificationFailed` (escalating a drive-fault
latch to that reason), even if each access fits its own 1.5 s limit. Deferred all-off work continues until verified;
the latch stays until explicit ClearFault. A protective stop has the same bound, over every HAT access from its
decision to the end of what decided it, that caller's LEDs included: from the moment they come for a limit or
drive-fault edge and the watchdog's timer (their wait for the lock counts), and from the decision for a supervision
cycle's stops and a deadline stop that a lease renewal or a repeated Open or Close finds, including an idle all-off
after a relay mismatch or repeated failed relay reads. Routine reads keep their own 1.5 s limit until a protective
fence moves: the section holding the lock then shares that fence's arrival deadline and skips remaining routine relay
reads and LEDs to let the protective work take the lock promptly. Pending requests stay recorded until they are
applied, including when they precede a section or pass to a queued worker; routine work uses the earliest foreign
deadline. A caller that decides its own stop retains that stop's deadline for all-off and verification, so an overdue
foreign request cannot suppress a fresh protective stop's writes. A routine retry of unverified all-off relays keeps
each access's own limit after those requests finish: its own fence does not bound it, but another pending request (an
edge, Stop, shutdown or queued/recovery stop) does. A shutdown raises the fence before waiting for the lock, as do a
drive-fault edge and a limit asserting; an input edge that cannot take the lock in time is never dropped: its own
level is applied the moment the lock is free, with the motion it came during, and then the inputs are read fresh.

**Every HAT access the controller makes is bounded.** A relay write, a relay read-back, an input read and an LED write
each run on a thread of the HAT's own, and the controller waits at most 1.5 s for one, less than Stop's 2 s. (The HAT
library's own input poll, which raises the input edges, reads outside it.) One that runs past that fails as
a failed transfer does: the relays count as unverified and the fault latches (`RelayVerificationFailed`), the
controller's lock is let go, so Stop and the protective stops act at once, and every later access fails at once
without reaching the HAT until the hanging one has returned. Then, before anything else reaches the HAT, the fence goes
up and every relay is written off and verified; the latch stays until a person clears it, and `ClearFault` is refused
before that.

**A late ON.** A relay write the controller gave up on must never land after a Stop that reported success. Until the
relays are read back off after it, they count as unverified: Stop answers `RelayStateUnverified`, and `ClearFault` is
refused. What proves a given-up write can no longer land depends on the transport:

- **The HAT emulator**: each access ends within `HatEmulator:RequestTimeout` (1 s by default), and once the client's
  next connection is answered the emulator runs no request of an older one (the
  [client-tagged Hello](emulator.md#settings)), so the first access that succeeds after it settles the write either way.
- **The Pi's HAT**: each relay write is a synchronous I2C transfer on the HAT's own access thread, one at a time, so no
  later relays-off write can reach the bus before an earlier write has returned. A transfer that does not return fails
  at the 1.5 s limit: Stop then answers `RelayStateUnverified` at once, and the relays are written off and verified as
  soon as the transfer returns.

The supervision cycle reads the relay register back every second while the roof is idle, and at least every second
while it moves. A register that does not match what was commanded (a late ON among them) stops the motion, or, idle,
writes every relay off, and latches `RelayVerificationFailed`; while the relays are unverified, each cycle writes them
off again until a read-back shows them off.

### Controller settings

The controller's side, under `RoofController:Alpaca` and `RoofController:Lease`, in its
[settings catalogue](security.md#settings) (group `alpaca`; each changed only with the local credential) except
`ServerAddresses`, a deployment setting given like the [safety sources](#source-settings):

| Setting | Default | What it does |
|---|---|---|
| `RoofController:Alpaca:AllowCommands` | `Off` | `Off`, `CloseOnly` or `OpenAndClose`. The controller refuses to start with anything else. Safety-critical; a change drops every grant and never stops a motion in progress. Start with `CloseOnly`. |
| `RoofController:Alpaca:AllowOpenWithoutBlockingScopes` | `false` | With no telescope set to block the roof, whether an Alpaca Open may go on the weather alone ([The Open rule](#the-open-rule)). Safety-critical; while it is on and Open is allowed, every UI warns, and the controller logs it at start. A change drops every grant. |
| `RoofController:Alpaca:ServerAddresses` | none (loopback only) | The Alpaca server's addresses, besides loopback: single IPv4 or IPv6 host addresses, no networks, never the Docker bridge gateway. Checked at start. |
| `RoofController:Alpaca:OverrideWindowMinutes` | `5` | How long a grant lasts, 1 to 15 minutes. A change drops every grant. |
| `RoofController:Alpaca:ScopeRereadBudgetMs` | `600` | The telescopes' re-read budget, 300 to 2000 ms ([The telescopes, read again](#the-telescopes-read-again)). |
| `RoofController:Lease:OperatorLeaseIdleMinutes` | `10` | A person's lease lapses after this long without input, 1 to 120 minutes, only while commands are on. |
| `RoofControllerOptionsV4:DirectionChangeDelay` | `0` | How long every start waits after the relays were last verified off, 0 to 10 s ([Waiting out the direction-change delay](#waiting-out-the-direction-change-delay)). |

The status (`GET RoofControl/Status`, and every hub snapshot) carries `alpaca`: the mode, the hold, the operator
lease, the grants, the alerts, the warnings, the number of blocking telescopes, the budget, the window, each
telescope's latency, `allowOpenWithoutBlockingScopes`, and `openGrantUnavailable` (why no Open grant can be made now:
the interlock is `Block`; null otherwise); and `isStartPending`, `pendingMotion`, `motionOwner` (a person, the Alpaca account, or nobody
in particular) and `isContinuationRefused` (a start refused when its direction-change delay ended, which Dome 0 reports
as Error).

The controller's routes, under `api/v4.0/alpaca`:

| Route | Who |
|---|---|
| `POST shutter/ticket`, `POST shutter/close`, `POST shutter/open` | The External Alpaca account only, from a trusted address. |
| `POST events` | The Alpaca server's own account only, from a trusted address. |
| `GET grants`, `GET alerts` | Any role. |
| `POST grants`, `DELETE grants/{kind}`, `DELETE hold`, `POST lease` | A person's session with the Operator role (every API key 403). |
| `DELETE lease`, `POST alerts/{id}/acknowledge` | A person's session (every API key 403). |

### Clients

A client must send the External key with `OpenShutter` and `CloseShutter`, as HTTP Basic (the key as user name and
password) or the `X-HVO-Api-Key` header, and must send it **pre-emptively**: the server answers a command without a
credential with `0x40B`, not with an HTTP challenge.

**Its timeout must outlast the controller's answer.** The server answers an Open or a Close within the re-read budget
(`ScopeRereadBudgetMs`), plus what is left of the direction-change delay, plus 400 ms, and waits for the controller 1 s
longer than that; with the defaults (600 ms, no delay) that is 2 s. Keep the budget plus the delay plus 1.4 s below the
client's response timeout: ASCOM's Alpaca client library (the Dynamic Client's) waits 10 s for a member's answer and
then sends it once more, which the server treats as a new command, admitted afresh.

- **ConformU** 4.5.3 sends HTTP Basic from its settings file (`AccessUserName`, `AccessPassword`) with every request,
  its `OpenShutter` and `CloseShutter` included ([Conformance](#conformance)).
- **NINA**'s own Alpaca client (a dome picked from its Alpaca discovery list) has no place for a user name and
  password, so its `OpenShutter` and `CloseShutter` are refused as not authorised. Connect NINA through the **ASCOM Alpaca Dynamic Client**
  instead (the owner's decision on #220), on the Windows PC that runs NINA:
  1. Install the ASCOM Platform (7 or later), which brings the Dynamic Client.
  2. In NINA's equipment for the dome, open the ASCOM Chooser. From its **Alpaca** menu, either enable discovery and
     discover now, or create an Alpaca driver by hand (it needs administrator rights) with the Alpaca server's LAN
     address, port `11111` and device number `0`.
  3. Choose that Dynamic Client driver (named after the dome, `HVO roll-off roof`), not the dome NINA lists under its own
     Alpaca discovery, and open its **Properties** (its setup dialog).
  4. In the setup dialog's authentication settings, enter the External Alpaca account's key as **both** the user name
     and the password, and save. The Dynamic Client then sends them as HTTP Basic with every request.
  5. Connect, and check that a `CloseShutter` (and, with `OpenAndClose`, an `OpenShutter`) from that PC is accepted:
     the server's log says `accepted by the controller`, and the History page shows it with the `Alpaca` source and the
     `basic` auth method.

  **The key travels in the clear.** HTTP Basic is an encoding, not encryption, and the Alpaca listener is plain HTTP
  (TLS is not there yet, [Not yet](#not-yet)), so anyone who can watch the observatory's LAN can read the key from a
  command. Keep NINA's PC and the Alpaca server on that LAN. The key can close the roof, and open it under
  `OpenAndClose`, only when the controller would allow it; rotating it on the People page ends every grant.
- Any other client: give it the key the same way, then check in the server's log that its commands arrive with a
  credential.

Try a client against the [HAT emulator](emulator.md) first, never against the roof.

#### The client matrix

The client matrix ([`tests/alpaca/HVO.RoofControllerV4.AlpacaClientMatrix`](../tests/alpaca/HVO.RoofControllerV4.AlpacaClientMatrix),
run in CI by `tests/alpaca/conformu.sh` after its chain guard) drives the server with ASCOM's own Alpaca client
library, `ASCOM.Alpaca.Components` 4.1.0, the library the Dynamic Client and NINA's native Alpaca devices are built on,
with its default timeouts, against the HAT emulator on loopback only. It connects by explicit address (no discovery),
and each profile goes through a capturing proxy that records what the client sends:

| Profile | What it does | Result |
|---|---|---|
| **Dynamic Client** (NINA through it): the External key as user name and password | A NINA-like session, in the order the program runs it: connects; finds the shutter in Error and closes first (NINA's Error recovery); opens, and polls until Open; parks the mount, then closes (park-then-close); a refusal while the weather is unsafe (the client gets InvalidOperation with the controller's reason, and nothing moves); a person's Open grant through the session API, then the Open goes; `AbortSlew` during a motion (stops, reads Error part way); disconnects and connects again, and recovers from the Error once more. | Passes. Every request carried HTTP Basic, the first (`PUT connected`) included, and none was challenged. Open answered in 160 to 204 ms, Close in 145 to 162 ms, `AbortSlew` in 97 ms, a refused Open in 25 ms, against the controller's 1 s deadline, the server's 2 s wait and the library's 10 s. |
| **NINA native**: no credential | Reads, `Connected`, `AbortSlew`; then `OpenShutter` and `CloseShutter`. | Passes. No request carried a credential; reads and `AbortSlew` work, and `OpenShutter` and `CloseShutter` answer `0x40B` "Not authorised" with nothing moving. |

No Windows PC, NINA or ASCOM Platform takes part in CI. **Before Alpaca Open is turned on at the observatory**, repeat
the session with the real NINA and the Dynamic Client on the Windows PC, against a HAT-emulator rig, never the roof:

1. Set up a [rig](emulator.md) on a computer not wired to the roof: its controller with `OpenAndClose`, the emulator's
   Alpaca mount as a blocking telescope and its safety monitor as a required weather source, and an External key; its
   Alpaca server on the rig's LAN address, with `Alpaca:Location` "EMULATOR RIG, not the roof" and `AllowedNetworks`
   narrowed to the PC. The emulator's control port (its fake mount and weather monitor, and the controls below) listens
   on loopback only, and it has no authentication. On a rig the installer made, work the emulator's mount from the rig
   itself, through that control API (its container publishes it on `127.0.0.1` only). Only an emulator started with
   `dotnet run` can be served on the rig's address for the test network alone (`--urls http://<rig>:<port>`,
   [Settings](emulator.md#settings)), so that NINA's mount can connect to its telescope. Clear the Stop hold in the
   rig's web UI.
2. On the PC, open `http://<rig>:11111/setup`: it must show the HAT emulated and the rig's controller.
3. In a NINA profile of its own, never the observatory's, open the ASCOM Chooser, **Alpaca**, **Create Alpaca Driver
   (Admin)**, named "HVO roof EMULATOR", with the rig's address, port `11111` and device `0`, and the External key as
   both user name and password (no discovery).
4. Run the session on the rig: reset the emulator part way (the shutter reads Error), close, then open; unpark the
   emulator's mount and park it again (from NINA's mount connected to the emulator's Alpaca telescope, when its control
   port is served on the test network, or through the rig's control API), then close; make the weather unsafe and open (NINA shows the controller's reason; nothing moves);
   grant the Open from the rig's alert and open again (it opens); stop the dome during a motion (the shutter reads
   Error); disconnect, connect again, close.
5. Check the rig's History: every Open and Close shows the `Alpaca` source, `basic`, the PC's address, and the
   server's log says `accepted by the controller`.
6. Optionally, NINA's own discovered Alpaca dome: its Open and Close must be "Not authorised". Connect it only after
   checking the device it lists is the rig's (its `Location`).

## Alpaca Open

Phase 4 of #220: an Alpaca client may open the roof with `OpenShutter`. An Open goes exactly the way a
[Close](#alpaca-close) goes, with the same credential, ticket, re-read of the telescopes, checks under the controller's
lock, fencing, history and alerts, and its own rule: the weather must be fresh and safe and every telescope that can
block the roof clear of it. Nothing moves on its own: no Open is queued, retried or replayed, a refused Open needs a new
`OpenShutter`, and there is no standing setting that opens without that rule (no "unattended Open").

Open needs `RoofController:Alpaca:AllowCommands` set to `OpenAndClose` ([Controller settings](#controller-settings)).
With `CloseOnly` every Alpaca Open is refused (`OpenNotAllowed`), with `Off` every Alpaca command (`CommandsOff`); both
are only logged, never alerted. Start with `CloseOnly`, and allow Open once Close has worked for a while.

### How an Open goes

As [a Close goes](#how-a-close-goes): the server refuses at once a client not connected (`0x407`) and a missing,
malformed or conflicting credential (`0x40B` "Not authorised"), asks the controller for an Open ticket
(`POST api/v4.0/alpaca/shutter/ticket?kind=Open`), which starts the re-read of every blocking telescope, refuses the
Open if an `AbortSlew` came meanwhile ("Aborted"), then sends it (`POST api/v4.0/alpaca/shutter/open`). A started Open
answers with `ShutterStatus` Opening at once; a refused one leaves the status as it was. An Open ticket is good for an
Open only, a Close ticket for a Close only. The deadline is the Close's: the re-read budget, what is left of the
[direction-change delay](#waiting-out-the-direction-change-delay), and 400 ms.

### What the controller checks for an Open

In the order a Close is checked ([What the controller checks](#what-the-controller-checks)), with these differences:

1. **Commands on** means `OpenAndClose`.
2. The hard protections, as for any Open.
3. The [human Stop hold](#the-human-stop-hold) and a person's [operator lease](#the-operator-lease) refuse every Alpaca
   Open, exactly as they refuse a Close.
4. **What the Open would do.** Already open: success, nothing sent. Already opening: success, nothing changes (no relay
   write, no new deadline, no grant used). Closing (or about to): a reversal. Otherwise it is a start.
5. to 7. In time, the grant used up, nothing after the ticket, as for a Close.
8. **Never a person's motion**: a reversal of a Close on a person's command, or of one nobody in particular started, is
   refused (`PersonsMotion`), the grant already used up; a reversal of an Alpaca client's Close is admitted like a start.
9. **The Open rule**, below.

### The Open rule

**An Alpaca Open is Safe when the weather is fresh and safe and every blocking telescope is clear of the roof**, the
owner's rule (#220), whatever `RoofSafety:InterlockMode` says:

- **The weather.** At least one weather source is set `Required`, and **every** required one has a **latest read that
  succeeded**, a reading no older than its `MaxReadingAge`, that says safe. A source whose latest read failed is
  unknown for an Open at once, even while its last good reading still counts for the ordinary
  [conditions](safety-service.md), which keep it until it is too old (the owner's decision, #220): an Open nobody is
  watching needs fresh weather. A source set `Ignored` never decides.
- **The telescopes.** Every telescope set `BlocksRoof` is clear of the roof (today: parked), from a read made after the
  Open arrived, exactly as for a Close ([The telescopes, read again](#the-telescopes-read-again)): a telescope unparked,
  unknown (its server down, a failed or timed-out read, `CanPark` false), stale, or configured but missing from the
  safety service's list is not clear.
- **No blocking telescope configured.** Then an Open is refused (`no-blocking-scopes`), and needs a person's grant,
  unless `RoofController:Alpaca:AllowOpenWithoutBlockingScopes` is `true`: the weather alone decides then, every UI shows
  "Alpaca Open checks no telescope" while Open is allowed, and the controller logs it at start. The setting never skips
  a telescope that is configured to block the roof.
- **`InterlockMode` does not relax it.** With the interlock `Off`, `Warn` or `Block`, an Alpaca Open needs this rule
  (or, outside `Block`, a grant). The ordinary Open's override prompt (`overrideSafety`) never applies to Alpaca. In
  `Block` only Safe opens the roof: no grant does ([Open grants](#open-grants)).

A refusal names every reason, in words and as keys, and raises an alert:

| Key | Reason |
|---|---|
| `scope-not-clear:<telescope>` | A blocking telescope is not parked. |
| `scope-unknown:<telescope>` | A blocking telescope's state is not known (a failed, timed-out or stale read, `CanPark` false, not being read). |
| `weather-unsafe:<monitor>` | A required weather source's latest reading says unsafe. |
| `weather-unknown:<monitor>` | A required weather source has not been read, its latest read failed, or its reading is older than `MaxReadingAge`. |
| `no-required-weather-source` | No weather source is set `Required`. |
| `no-blocking-scopes` | No telescope is set to block the roof, and `AllowOpenWithoutBlockingScopes` is off. |

**Once the roof moves, it keeps moving.** If the weather turns unsafe (or its source stops answering), or a blocking
telescope unparks, while an Alpaca Open runs, the roof goes on opening and every UI raises an alert ("The roof
continues; Stop it if it must not"). Every hard stop (Stop, the limits, the drive fault and running inputs, the
watchdog, the fault latch) acts as always. **Nothing closes the roof on its own**: no Close on unsafe weather, on a
client disconnecting or on the SafetyMonitor turning unsafe. A client that should close on bad weather (NINA's safety
monitor trigger, say) sends its own `CloseShutter`, which the weather never gates.

### Open grants

A person may let **one** refused Open through, as for a Close ([Override grants](#override-grants)): from the refused
Open's alert ("Allow this Open once") in the web UI, the terminal (or `hvo-roof alpaca grant`, with the same terminal
checks and typed confirmation as a Close) or the kiosk, by a person with the override role, never through Alpaca or
with an API key (403). Each UI asks first ("Allow one Alpaca Open?"), with what the grant lifts and that the roof stays
open if the weather turns.

- **Its own kind.** An Open grant lifts only an Open's reasons, and a Close grant only a Close's: one of each may be in
  force at once, each used up by one command of its kind.
- **Reasons-bound, the weather's too**: it lifts only the keys it was given (any of the table's above). A new reason
  since (rain started, a mount unparked) refuses the Open again, with a fresh alert, and the grant is still used up.
- **For a while**: `OverrideWindowMinutes` (5 by default, 1 to 15), on the controller's monotonic clock; logged with who
  granted it, from which UI, and the Open that used it.
- **Never in `Block`.** While `RoofSafety:InterlockMode` is `Block`, the grant route refuses an Open grant (403,
  `BlockMode`), every UI shows the Open grant control disabled with the reason, the status says why
  (`openGrantUnavailable`), a change of the mode drops every grant (as any change of it does), and an Open grant that
  is in force all the same is never honoured: the Open is refused and the grant used up.
- **Dropped** when anything that drops a Close grant happens, and also when `AllowOpenWithoutBlockingScopes` changes.
- **Never a way past** the hard protections, the hold, a person's lease or `AllowCommands` (`CloseOnly` refuses an Open
  before its grant is used).
- **The person who grants steps away**, as for a Close grant. With an Open and a Close grant of theirs both in force,
  the person stays stepped away until both have ended: ending one (used, lapsed, revoked) leaves them away while the
  other lasts. A person taking the lease drops every grant in force, another person's included, and that is also so
  when a person's UI takes it back by itself once all their own grants have ended: with two people's grants in force,
  the first person's UI coming back drops the second person's grant. It fails safe (the first person's lease would
  refuse that command anyway); the second person grants again once the first has stepped away.

### Known limitations

Phase 3's [known limitation](#waiting-out-the-direction-change-delay) of a limit switch applies to an Open as to a
Close, and fails safe the same way:

- **A limit asserting refuses a waiting Open.** An Open waiting out the direction-change delay (after an `AbortSlew`
  stopped a Close just short of the closed limit, say, the roof coasting onto it) is cancelled (`Superseded`) when a
  limit asserts, and so is an Open whose ticket came before it. Nothing moves; the client sends the Open again.

(Phase 3's other one, a grant made again while a used grant's command waits out the delay being dropped when that
command was decided, is gone for both kinds: the grant made again holds the person away too.)

## Conformance

[ConformU](https://github.com/ASCOMInitiative/ConformU), ASCOM's conformance checker, checks the server in CI (the
`alpaca-conformu` job in [scenarios.yml](ci-runners.md)), against the controller and the [HAT emulator](emulator.md)
on the runner's loopback, never against hardware, and never before its chain guard has passed:

- **Pinned.** ConformU 4.5.3, downloaded from its release and checked against its SHA-256 in
  [`tests/alpaca/conformu.sh`](../tests/alpaca/conformu.sh).
- **Its settings** are [`tests/alpaca/conformu-settings.json`](../tests/alpaca/conformu-settings.json):
  `DomeOpenShutter` true, a 60 s shutter timeout (three times the emulated roof's 20 s travel) and a 3 s stabilisation
  wait, and the protocol check's strict mode and URL-structure checks on. The script adds the run's External key as
  ConformU's HTTP Basic user name and password, in a copy only the run can read.
- **The chain guard** (`tests/alpaca/alpaca-ci.py guard`), before anything may command the roof: the controller's and
  the Alpaca server's URLs are loopback addresses (written as addresses, not names), the controller drives the HAT
  emulator, and the Alpaca server reaches that very controller instance (its `status` action names it). Otherwise the
  run stops before ConformU starts.
- **Run-only keys and settings.** The script makes the run's keys (the Alpaca server's account, the External account,
  an admin key that makes the CI person and reads the history) and the CI person's password in a directory only its
  user can read, gives the controller their hashes, and deletes them at the end. `AllowCommands` `OpenAndClose` and
  `RoofSafety:InterlockMode` `Warn` are for the run only, never shipped defaults: the script writes them into a run-only
  copy of the controller's `appsettings.Development.json` (its content root), not the environment, so the Open runs can
  change them while the controller runs (a configuration reload). The safety sources are read every second. The CI
  person (an Operator) clears the Stop hold set at the controller's start through the session API, as a person would in
  a UI, and holds no operator lease. No log or result kept may hold a run secret: the script fails if one does, and
  deletes it before CI keeps the results.
- **Four checks**, the Alpaca protocol check and the conformance check of Dome 0 and of SafetyMonitor 0, must report
  **no error and no issue**. The SafetyMonitor's run uses the same settings, its device changed; the controller behind it
  reads the emulator's Alpaca safety monitor as its weather source and lists the server's `UniqueID`s (made by the script
  for the run) as its own, so `IsSafe` is true end to end, and the script waits for that before ConformU starts.
  ConformU logs some failures, such as the shutter state straight after a command, as issues, so no errors alone is not
  enough. Every information message and configuration alert must be one
  [`tests/alpaca/conformu-expected.json`](../tests/alpaca/conformu-expected.json) lists, with why, and each listed one
  must be reported: for both devices, only the status codes of the URL-structure checks' malformed URLs; no
  configuration alert.
- **ConformU opens and closes the emulated roof** (the weather safe, the mount parked), through the Alpaca server, with
  the External key. The script then checks the roof is at rest at a limit with every relay off, no plant violation and
  no fault, and that the history records ConformU's Opens and Closes with the `Alpaca` source and HTTP Basic.
- **Then the scripted Close runs** (`alpaca-ci.py close-runs`), each starting the emulated roof part way open through
  the emulator, checking the starting state, and checking the relays and the roof afterwards: the Stop hold left set
  refuses the Close; a missing and a wrong credential are not authorised; an unparked mount refuses it with an alert
  and nothing queued; a Close grant (made through the session API) lets the refused Close go and is used up; a new
  reason after a grant refuses it and the grant is still used; unsafe weather never gates it.
- **Then the scripted Open runs** (`alpaca-ci.py open-runs`), the same way: a safe Open reads Opening at once and
  reaches Open with the open relay alone; unsafe weather refuses it (`0x40B`, an alert with `weather-unsafe`) with
  nothing moving or queued; a weather monitor that stops answering refuses it at once ("its latest read failed") while
  the ordinary conditions still show its last safe reading; stale weather (that monitor kept silent until its last safe
  reading is older than `MaxReadingAge`, shortened to 7 s for the run by a reload, so the ordinary conditions read it
  stale too) refuses it with `weather-unknown`, nothing moving and nothing queued once fresh safe readings return (end
  to end a reading goes stale only after failed reads, so the refusal names the failed latest read; the age rule on its
  own is a unit test's); an unparked mount refuses it; an Open grant made through the
  session API lets the refused Open go and is used up; a grant made in `Warn` is dropped when `InterlockMode` turns
  `Block` at runtime (the reload checked on the status), `openGrantUnavailable` says why, a new Open grant is refused
  (403 `BlockMode`) and the Open is refused with nothing moving; an Alpaca Open then an Alpaca Close reverses the roof
  (the open relay off before the close relay on, never both); and `CloseOnly`, set at runtime, refuses an Open while a
  Close goes.
- **Then the client matrix** ([The client matrix](#the-client-matrix)).

Run it locally from the repository root with `tests/alpaca/conformu.sh` (it builds the three programs and the client
matrix; `--no-build` uses the last build). It uses ports 47210 to 47232 (`HVO_ALPACA_TEST_PORT_BASE` moves them) and
keeps its logs and ConformU's reports in the directory it names. `tests/alpaca/check-conformu-results.py` and the
harness's helpers (`test_alpaca_ci.py`: the modes file, the relay-write checks, alert matching and refusing anything but
loopback) have their own tests (`python3 -m unittest discover -s tests/alpaca -p 'test_*.py'`).

> **Never point ConformU at an Alpaca server in front of the real roof.** The repository's settings file opens the
> shutter (`DomeOpenShutter` true), and `conformu conformance <url>` and `conformu alpacaprotocol <url>` run with it on
> whatever a settings file says. With commands on and the External key, ConformU opens and closes the roof through this
> server. Run it only through `tests/alpaca/conformu.sh` and its chain guard, against the emulator.

The contract tests in `tests/HVO.RoofControllerV4.RPi.Tests/Alpaca` check the envelope, the parameters, the
transaction numbers, the error table, connecting, `AbortSlew`, refused commands, an unreachable or silent controller,
the allowlist, the `Host` and `Origin` checks and the polling limit, against an in-memory controller.
`AlpacaCloseShutterTests` check the server's `CloseShutter` and `OpenShutter`: the credential (header, Basic, both,
mismatched), passing it on for that command alone, the local refusals before any ticket, an `AbortSlew` overtaking a
command, no retries, and the failure limit. `RoofAlpaca*Tests` check the controller's rules (the order, tickets,
re-reads, grants, the hold, waiting starts; `RoofAlpacaOpen*Tests` the Open rule, Open grants and Block, and an Open's
waiting starts and reversals), `RoofOperatorLeaseTests` the operator lease, `AlpacaCommandsApiTests` its routes (the
deny-by-default account, trusted addresses, person-only routes, events, the history, no key anywhere), and
`AlpacaCloseScenarios` and `AlpacaOpenScenarios` an Alpaca client's Close and Open end to end on the emulated plant.
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

What an ordinary Open does with the conditions is the interlock's (`RoofSafety:InterlockMode`, Warn by default),
unchanged: [The Open interlock](safety-service.md#the-open-interlock). An ordinary Close and every Stop are never gated.
An **Alpaca** Close has its own rule, the blocking telescopes alone, read again for it, never the weather
([The Close rule](#the-close-rule)). An **Alpaca** Open has a stricter rule than the conditions, whatever the interlock
says: every required weather source's latest read must also have succeeded, the telescopes are read again for it, and
no blocking telescope at all is not Safe unless `AllowOpenWithoutBlockingScopes` says so
([The Open rule](#the-open-rule)). The conditions never move or stop the roof.

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

Phases 1 to 4 leave these to follow-ups of #220:

- **Deployment checks** (#229): `--validate-deployment` checks of the `Host` allowlist and `ServerAddresses`.
- **TLS** on the Alpaca listener, so that the External key no longer crosses the LAN in the clear.
