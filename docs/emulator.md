# HAT emulator

The HAT emulator (`src/HVO.RoofControllerV4.Emulator`) lets the whole roof controller run without the HAT, the drive
or the roof: on a development machine, in CI, or on a Pi test rig. It hosts the emulated plant from
`HVO.RoofControllerV4.Simulation`:

- the Sequent SM-I-010 four-relay, four-input HAT and its registers;
- the Lenze SMVector drive, with the installed parameters;
- the ME-8108 limit switches;
- the roof;
- the wiring in the [hardware overview](projects/roof-controller-v4-rpi/hardware-overview.md).

Each part is modelled on its vendor documentation. The emulator serves the HAT's registers on a TCP port. With
`HatEmulator:Enabled`, the controller sends its register reads and writes there instead of to the Pi's I2C bus. The
controller's own code, and the HAT library it uses, are unchanged. The only difference is what answers the register
accesses.

The one physical event that cannot be emulated is the roof striking a limit switch. The plant makes the switch operate
at the configured travel position instead, which exercises the same controller logic.

## Development

Development runs against the emulator. `appsettings.Development.json` turns emulator mode on at `127.0.0.1:5291`. The
limit switches come from the emulator, so they stay in force, with the documented normally open wiring as in
production. Start the emulator first, from `src/`:

```bash
dotnet run --project HVO.RoofControllerV4.Emulator     # register port 127.0.0.1:5291, control API http://127.0.0.1:5290
dotnet run --project HVO.RoofControllerV4.RPi          # the controller on http://localhost:5195
```

If the controller starts first, its first initialization cannot verify the relay register. It latches
`RelayVerificationFailed` and retries every `RestartOnFailureWaitTime` (10 s). Once the emulator is up, the controller
initializes, but the fault stays latched: the console shows Error and every move is refused until Clear Fault (the
console button or `POST /api/v4.0/RoofControl/ClearFault`). While the emulator is down, the HAT library also logs each
failed input poll with a stack trace, as it does for a failed I2C bus.

The ways of running the emulator share default host ports: 5290 for the control API in all three, and 5195 for the
controller with both `dotnet run` and the compose `emulator` profile. Run one at a time, or move the ports:

| How | Variables |
|-----|-----------|
| `dotnet run` | `--urls` for the control API and the controller, `Emulator__RegisterPort` and `HatEmulator__Port` for the register port |
| `src/docker-compose.yml` | `HVO_EMULATOR_CONTROL_PORT` (5290) and `HVO_DEV_ROOF_PORT` (5200) |
| The `emulator` profile | `HVO_EMULATOR_CONTROL_PORT` (5290) and `HVO_EMULATED_ROOF_PORT` (5195) |

**The in-memory register simulation is kept for unit tests, and as the fallback with emulator mode off and no I2C
bus.** This is the HAT library's
`FourRelayFourInputHatMemoryClient`: registers only, with no roof, drive or limit switches behind them. Before the
emulator, Development used it with the limit switches ignored, so an Open never reached a limit. The controller still
falls back to it when it runs with emulator mode off and no I2C bus, for example Production settings on a machine that
is not a Pi. It then reports `HatMode` `Simulation` and Degraded health.

`.devcontainer/devcontainer.rpi.json` gives its container the Pi's real I2C bus. It sets `HatEmulator__Enabled=false`
so that it uses the physical HAT, with the limit switches in force. Use it only on a commissioned bench.

For a containerized run, `src/docker-compose.yml` builds both images and runs the controller in Development against
the emulator, on `http://localhost:5200`:

```bash
cd src
HVO_DEV_ROOF_API_KEY=$(openssl rand -hex 24) docker compose up --build
```

## How emulator mode shows

A controller in emulator mode cannot be mistaken for the roof:

| Where | What |
|-------|------|
| Every page (the console's layout and the sign-in layout) | An `EMULATED HAT` banner naming the emulator endpoint: "The observatory roof does not move." |
| Console status badge and footer | `Emulated HAT` |
| `/health` | `Degraded`, "Roof controller is running against the HAT emulator (*host:port*), not the physical HAT". The data has `HardwareMode` `Emulated` and `HatEmulatorEndpoint`. |
| `GET .../RoofControl/Status` | `hatMode: "Emulated"` |
| Startup log | Warnings from `HVO.RoofControllerV4.RPi.HatEmulation` and `RoofControllerServiceV4` naming the endpoint. The HAT library's own `Mode: Physical I²C` line is expected: the library takes its hardware path, and only the register accesses go to the emulator. |
| Telemetry | Resource attributes `hvo.roof.hat.mode=emulated` and `hvo.roof.hat.emulator.endpoint` |

`/health/ready` still answers 200 while Degraded, so container health checks and the deploy script work against a
test rig.

## Emulator mode outside Development

Outside the Development environment, the controller **refuses to start** in emulator mode. It starts only when
`HatEmulator:AllowOutsideDevelopment` is also `true`: a test rig, never the observatory roof.

- `--validate-deployment` fails in the same case. With the setting, it warns instead, and it also warns when
  `/dev/i2c-1` is mapped, since the controller does not use it in emulator mode.
- The deploy script turns emulator mode on only with both `HAT_EMULATOR_ENDPOINT` and `ALLOW_EMULATED_HAT=true`. It then
  records the choice in its dry-run, its warning and its final report. See
  [HAT emulator mode](deployment.md#hat-emulator-mode-test-rigs) in the deployment guide.

## Settings

The controller's `HatEmulator` section:

| Setting | Default | Meaning |
|---------|---------|---------|
| `Enabled` | `false` (`true` in `appsettings.Development.json`) | Use the emulator instead of the Pi's I2C bus |
| `Host` | `127.0.0.1` | The emulator's host name or IP address, without a scheme or a port. The controller refuses to start, and `--validate-deployment` fails, on a value such as `http://hat-emulator` or `hat-emulator:5291`. |
| `Port` | `5291` | The emulator's register port |
| `ConnectTimeout` | `00:00:01` | How long a connection attempt may take (50 ms to 30 s) |
| `RequestTimeout` | `00:00:01` | How long a register access may take (50 ms to 30 s) |
| `AllowOutsideDevelopment` | `false` | Allow emulator mode outside Development (a test rig) |

A lost connection, a timeout or a malformed reply reaches the controller as an I/O error, exactly like a failed I2C
transfer. The controller fails safe the same way: relay read-back failures stop motion and latch
`RelayVerificationFailed`, and input read failures stop motion after `MaxConsecutiveInputReadFailures`. The client
reconnects on the next access.

Each access completes within `RequestTimeout`, or within `ConnectTimeout` plus `RequestTimeout` when it opens the
connection: the Hello shares the request timeout. A busy thread pool does not stretch either one. There is no backoff:
while the emulator is away, each access makes one connection attempt, and each new kind of failure is logged once, as a
warning. When the controller stops, an access in flight fails at once instead of waiting for its timeout.

The emulator's `Emulator` section (environment variables `Emulator__TimeScale` and so on):

| Setting | Default | Meaning |
|---------|---------|---------|
| `RegisterAddress` | `127.0.0.1` | IP address the register port listens on (`0.0.0.0` in the container image) |
| `RegisterPort` | `5291` | The register port |
| `TimeScale` | `1` | How many times as fast as real time the roof runs, from 0.1 to 100. The controller's own timing is not scaled. |
| `TravelMeters` | `2.0` | Distance between the limits' operating points (about 20 s of travel) |
| `InitialPosition` | just past the closed limit | Where the roof starts, in metres from the closed limit's operating point |

The control API listens on `http://127.0.0.1:5290` unless `--urls` or `ASPNETCORE_URLS` says otherwise. Invalid
settings stop the emulator before it listens.

The register port serves one access at a time, in the order they arrive. A connection that sends no Hello within 5 s
is closed. A request whose connection closed while it waited for its turn is not run: an I2C transfer cannot land
after its caller has given up on it.

## Control API

Everything is under `/api/emulator`, in JSON. Enum values are names, such as `"Open"` or `"StuckReleased"`; numbers are
refused. The fields in the examples are required unless the table says otherwise. The API changes the emulated plant
and link only. **It has no authentication:** keep it on loopback (the default, and how both compose files publish it) or on a
private test network.

| Request | Body | Effect |
|---------|------|--------|
| `GET /status` | | The plant (position, velocity, relays, inputs, drive, limits, faults) and the link |
| `GET /history?limit=200` | | The latest plant events (1 to 5000), with plant time since the last reset |
| `GET /violations` | | Invariant violations the plant recorded, such as the roof at a hard stop or both directions energized; empty in a correct run |
| `POST /reset` | `{"positionMeters": 1.0, "wiring": "None"}` | A fresh plant. Both fields are optional, and so is the body. |
| `POST /time-scale` | `{"scale": 4}` | Change the time scale; time stays continuous |
| `POST /drive/trip` | `{"trip": "External"}` | Trip the drive: `External` (the default), `MotorOverload` or `StartTooSoonAfterPowerUp`. The drive's Clear Fault input (RLY3) resets it. |
| `POST /drive/power` | `{"powered": false}` | Drive power loss and return |
| `POST /hat/power` | `{"powered": false}` | HAT power loss: register accesses fail with I/O errors |
| `POST /external-stop` | `{"open": true}` | Open the external STOP circuit |
| `POST /jam` | `{"jammed": true}` | Jam the roof: the drive runs, the roof does not move |
| `POST /limit-fault` | `{"limit": "Open", "fault": "StuckReleased"}` | Limit switch faults: `StuckActuated`, `StuckReleased`, `BrokenNcWire`, `BrokenMonitorWire` (combinable, comma-separated), or `None` |
| `POST /relay-fault` | `{"relay": 1, "fault": "Welded"}` | Relay contact faults: `Welded`, `Dead` or `None` |
| `POST /wiring` | `{"wiring": "SwappedLimitInputs"}` | Wiring variants, comma-separated. Examples: `SwappedDirectionRelays`, `SwappedMotorLeads`, `StopPermitBypassed`, `NoHardwiredEndStops`, `FaultMonitorWireBroken`, `RunMonitorWireBroken` |
| `POST /bus` | `{"failReads": true, "failNextWrites": 2}` | I2C transaction failures, answered as I/O errors (the link stays up). Each field is optional; one left out is unchanged. |
| `POST /link` | `{"outage": true}`, `{"responseDelayMilliseconds": 200}`, `{"disconnect": true}` | Link faults, each field optional. An outage closes the open connections and each new one as soon as it is accepted, as a stopped emulator would, so the controller sees connection errors. A response delay (0 to 60000 ms) models a stalled emulator: use it to exercise the controller's timeouts. `disconnect` drops the open connections once. |

Each `POST` answers with the new `/status`, or with 400 and no change. A body that does not parse (malformed JSON, an
unknown name, or a number where a name belongs) gets a plain 400. A missing required field or an out-of-range value
gets a problem detail that names it. For example, to jam the roof mid-travel and watch the controller stop it:

```bash
curl -s -X POST http://127.0.0.1:5290/api/emulator/jam -H 'Content-Type: application/json' -d '{"jammed": true}'
curl -s 'http://127.0.0.1:5290/api/emulator/history?limit=20'
```

## Containers

`src/HVO.RoofControllerV4.Emulator/Dockerfile` builds the emulator image for `linux/amd64` and `linux/arm64` from the
repository root:

```bash
docker build -f src/HVO.RoofControllerV4.Emulator/Dockerfile -t hvo/roof-hat-emulator:dev .
```

The image runs as an unprivileged user and maps no devices. It listens on all interfaces inside the container: the
control API on 5290 and the register port on 5291. Publish 5290 on loopback only. The controller reaches 5291 over a
Docker network, so that port does not need to be published.

Two compose files run the controller with the emulator:

| File | Environment | Purpose |
|------|-------------|---------|
| `src/docker-compose.yml` | Development | Local development, on `http://localhost:5200`. The admin key comes from `HVO_DEV_ROOF_API_KEY`. |
| `src/HVO.RoofControllerV4.RPi/docker-compose.yaml`, profile `emulator` | Production, with `HatEmulator__AllowOutsideDevelopment=true` | The production configuration with only the HAT emulated, on `127.0.0.1:5195` (`HVO_EMULATED_ROOF_PORT`). The admin key comes from `HVO_EMULATED_ROOF_API_KEY`. It maps no devices, so it can run next to a Pi profile. |

`tests/emulator/compose-smoke-test.sh` builds both images through the `emulator` profile. It opens and closes the
emulated roof through the controller's API, checks that the plant recorded no violations, and checks the banner, the
status and the Degraded health. CI runs it in the "Emulator image" workflow, which also builds the emulator image for
both platforms.

## Tests

No test relies on physical hardware.

| Tests | What they cover |
|-------|-----------------|
| `HatEmulatorProtocolTests` | The wire format: framing, limits, malformed frames |
| `HatEmulatorServerTests` | The emulator's TCP server: the Hello handshake, accesses, refusals and the link controls |
| `HatEmulatorParityTests` | Register accesses through the socket answer exactly as the in-process emulated client does |
| `HatEmulatorSessionTests` | The emulator session: the documented installation at start, the scaled clock, reset |
| `SocketI2cRegisterClientTests` | The controller's socket client: the Hello check, the time bounds of each access (connect, Hello, request), disconnects, reconnects, disposal during an access, and the `Host` rules |
| `RoofHatConnectionTests` | Emulator mode selection, the refusal outside Development, the startup warning and the telemetry attributes |
| `EmulatorApiTests` | The emulator host: the control API changes the plant and the link, and the register port serves the HAT |
| `EmulatorModeAppTests` | The whole controller in emulator mode: open and close through the API; a link outage while moving stops the roof and latches a fault until ClearFault; a controller started before the emulator initializes with a latched fault until ClearFault; the sign-in page banner and the Degraded health |
| `EmulatedHatDisplayTests` | The banner in the main layout (the console) and on its own, the console's HAT badge and the footer |
| `DevelopmentConfigurationTests` | Development uses the emulator, with the limit switches in force and the production wiring |
| `tests/emulator/compose-smoke-test.sh` | The two images together through the compose `emulator` profile |
| `tests/deploy/deploy-script-tests.sh` | The deploy script's refusal without `ALLOW_EMULATED_HAT` and its record of the flag |

`EmulatorModeAppTests` is `[DoNotParallelize]`. Each test starts a whole controller host whose startup and HAT polling
hold thread-pool threads, and the in-process emulator needs those threads to answer. Run in parallel, the hosts starve
the emulator into timeouts. The deployed emulator is a separate process, so this does not apply there.
