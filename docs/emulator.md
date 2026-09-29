# HAT emulator

The HAT emulator (`src/HVO.RoofControllerV4.Emulator`) lets the whole roof controller run without the HAT, the drive
or the roof: on a development machine, in CI, or on a Pi test rig. It hosts the emulated plant from
`HVO.RoofControllerV4.Simulation`:

- the Sequent SM-I-010 four-relay, four-input HAT and its registers;
- the Lenze SMVector drive, with the installed parameters;
- the ME-8108 limit switches;
- the roof;
- an MJPEG camera of the roof, standing in for the Blue Iris server (see [Camera](#camera));
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
production. The controller's camera is the emulator's camera (`BlueIris:BaseUrl` is `http://127.0.0.1:5290`, with no
credentials; `src/docker-compose.yml` points it at `http://hat-emulator:5290`). Start the emulator first, from
`src/`:

```bash
dotnet run --project HVO.RoofControllerV4.Emulator     # register port 127.0.0.1:5291, control API http://127.0.0.1:5290
dotnet run --project HVO.RoofControllerV4.RPi          # the controller on http://localhost:5195
dotnet run --project HVO.RoofControllerV4.Web          # the web UI on http://localhost:5188 (optional)
```

If the controller starts first, its first initialization cannot verify the relay register. It latches
`RelayVerificationFailed` and retries every `RestartOnFailureWaitTime` (10 s). Once the emulator is up, the controller
initializes, but the fault stays latched: every client shows Error and every move is refused until Clear Fault (the
web UI's button, `hvo-roof clear-fault` or `POST /api/v4.0/RoofControl/ClearFault`). While the emulator is down, the
HAT library also logs each failed input poll with a stack trace, as it does for a failed I2C bus.

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
so that it uses the physical HAT, with the limit switches in force. Use it only with the roof mechanism isolated.

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
| Every page of the web UI, the sign-in page too | An `EMULATED HAT` banner, from the status (on the sign-in page, from the anonymous `GET .../RoofControl/Mode`): "the controller drives the HAT emulator, not the physical HAT. The observatory roof does not move." |
| The clients' controller line (the web UI's status, `hvo-roof status`, the terminal interface) | `emulated HAT` |
| `/health` | `Degraded`, "Roof controller is running against the HAT emulator (*host:port*), not the physical HAT". A more serious result, such as a latched fault or failing reads, takes its place with " (HAT emulator at *host:port*)" at the end, so every description names the emulator. The data always has `HardwareMode` `Emulated` and `HatEmulatorEndpoint`. |
| `GET .../RoofControl/Status`, `GET .../RoofControl/Mode` | `hatMode: "Emulated"` |
| Startup log | Warnings from `HVO.RoofControllerV4.RPi.HatEmulation` and `RoofControllerServiceV4` naming the endpoint. The HAT library's own `Mode: Physical I²C` line is expected: the library takes its hardware path, and only the register accesses go to the emulator. |
| Telemetry | Resource attributes `hvo.roof.hat.mode=emulated` and `hvo.roof.hat.emulator.endpoint` |

`/health/ready` still answers 200 while Degraded, so container health checks and the deploy script work against a
test rig.

## Emulator mode outside Development

Outside the Development environment, the controller **refuses to start** in emulator mode. It starts only when
`HatEmulator:AllowOutsideDevelopment` is also `true`: a test rig, never the observatory roof.

- `--validate-deployment` fails in the same case. With the setting, it warns instead. It also fails whenever
  `/dev/i2c-1` is mapped in emulator mode: no supported deployment has both, so that is emulator settings left on the
  roof's Pi, for example in its secrets directory.
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
| `CameraFramesPerSecond` | `5` | The emulated camera's frame rate, from 0.1 to 30 (see [Camera](#camera)) |

The control API listens on `http://127.0.0.1:5290` unless `--urls` or `ASPNETCORE_URLS` says otherwise. Invalid
settings stop the emulator before it listens.

The register port serves one access at a time, in the order they arrive. A connection that sends no Hello within 5 s
is closed. A request whose connection closed while it waited for its turn is not run: an I2C transfer cannot land
after its caller has given up on it.

## Control API

Everything is under `/api/emulator`, in JSON. Enum values are names, such as `"Open"` or `"StuckReleased"`; numbers are
refused, and so is a list of names except for the flag sets (`wiring`, a limit `fault`), such as
`"SwappedLimitInputs, SwappedMotorLeads"`. The fields in the examples are required unless the table says otherwise.
The API changes the emulated plant and link only. **It has no authentication:** keep it on loopback (the default, and
how both compose files publish it) or on a private test network.

| Request | Body | Effect |
|---------|------|--------|
| `GET /status` | | The plant (position, velocity, relays, inputs, drive, limits, faults), the link and the camera |
| `GET /history?limit=200` | | The latest plant events (1 to 5000), with plant time since the last reset |
| `GET /violations` | | Invariant violations the plant recorded, such as the roof at a hard stop or both directions energized; empty in a correct run |
| `POST /reset` | `{"positionMeters": 1.0, "wiring": "None"}` | A fresh plant, and a live camera at its frame rate. Both fields are optional, and so is the body. |
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
| `POST /camera` | `{"mode": "Frozen"}`, `{"framesPerSecond": 2}`, `{"disconnect": true}` | Camera faults, each field optional (see [Camera](#camera)). The mode is `Live`, `Frozen`, `Unavailable` or `Unauthorized`; the frame rate is 0.1 to 30. `disconnect` ends the open streams once, as a camera server restart does. |

Each `POST` answers with the new `/status`, or with 400 and no change. A body that does not parse (malformed JSON, an
unknown name, a number or a list where one name belongs) gets a 400 with no body from the container (Production), or
with the developer exception details under `dotnet run` (Development). A missing required field or an out-of-range value
gets a problem detail titled "Invalid emulator request" that names it. For example, to jam the roof mid-travel and watch
the controller stop it:

```bash
curl -s -X POST http://127.0.0.1:5290/api/emulator/jam -H 'Content-Type: application/json' -d '{"jammed": true}'
curl -s 'http://127.0.0.1:5290/api/emulator/history?limit=20'
```

## Camera

The emulator also stands in for the Blue Iris server that the controller's camera proxy reads. `GET
/mjpg/camNN/video.mjpg` (camera 1 to 99, the path the proxy requests under `BlueIris:BaseUrl`) answers
`multipart/x-mixed-replace` with one JPEG per part, each with its `Content-Length`. Each frame is a 320x240 greyscale
drawing of the plant: the roof on its track between the two limit switches (bright while actuated), the plant time, the
position as a percentage and a frame counter, so consecutive frames always differ. Like the control API, it has no
authentication, and it ignores any credentials the proxy sends. Point a controller at it with `BlueIris__BaseUrl` set to
the emulator's control URL and `BlueIris__UserName` and `BlueIris__Password` empty.

`POST /api/emulator/camera` injects the failures the web UI must survive:

| Mode | The camera | The controller's proxy and the web UI |
|------|------------|------------------------------------|
| `Live` | Streams a frame every `1/framesPerSecond` s | The proxy relays each frame; the web UI shows `Live` |
| `Frozen` | Keeps the stream open and sends nothing, as a hung encoder does | The web UI shows `Stalled` after 5 s and reconnects after 15 s; the proxy aborts a stream that has had no data for `BlueIris:StreamIdleTimeout` (30 s) |
| `Unavailable` | Ends the open streams, and answers 503 | The proxy answers 502 "Camera unavailable"; the web UI shows `Offline` and retries with backoff |
| `Unauthorized` | Ends the open streams, and answers 401 with `WWW-Authenticate: Basic` | As `Unavailable`: the proxy's 502 tells the web UI nothing about the camera's credentials |

A disconnect, a change of mode and a reset end a stream between parts, never inside one.

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
| `src/HVO.RoofControllerV4.RPi/docker-compose.yaml`, profile `emulator` | Production, with `HatEmulator__AllowOutsideDevelopment=true` | The production roof settings against the emulator, on plain HTTP at `127.0.0.1:5195` (`HVO_EMULATED_ROOF_PORT`). The admin key comes from `HVO_EMULATED_ROOF_API_KEY`. Its camera proxy reads the emulator's camera (`BlueIris__BaseUrl=http://hat-emulator:5290`, no credentials; the frame rate is `HVO_EMULATOR_CAMERA_FPS`, 5 by default). It exports telemetry only when `HVO_EMULATED_ROOF_OTLP_ENDPOINT` is set, and rotates its logs as the Pi profiles do. It maps no devices, so it can run next to a Pi profile. |

`tests/emulator/compose-smoke-test.sh` builds both images through the `emulator` profile. It opens and closes the
emulated roof through the controller's API, checks that the plant recorded no violations, and checks the banner, the
status and the Degraded health. CI runs it in the "Emulator image" workflow, which also builds the emulator image for
both platforms.

### Container scenarios

`tests/emulator/deploy-scenarios.sh` runs what the in-process scenarios cannot reach: the deployed container, the deploy
script and Compose, on real Docker. It runs only against the local Docker daemon: it uses the default context, and
refuses to start when `DOCKER_HOST` names anything but a `unix://` socket, or on a Raspberry Pi. It uses the HAT
emulator, maps no host device, and builds both images for the machine it runs on (the script's
`BUILD_PLATFORM=linux/amd64` on a PC). It serves HTTPS with a self-signed certificate for `127.0.0.1` and an operator
key, both generated for the run.

| Scenario | What it runs |
|----------|--------------|
| `lifecycle` | `docker stop` while the roof travels and a camera stream is open through the proxy: the supervisor stops the controller first, which stops the roof (`HostShutdown`, relays off) and exits 0 before the supervisor stops the web UI; the stream ends within 5 s, and the container exits 0 within 5 s, not 137, then starts again ready and still, with the web UI live (C11 steps 1-3). `docker kill` while it travels: the HAT holds the relays, the restart policy does not restart the killed container, and the controller turns the relays off when it is started again. `docker stop` while the emulator fails relay writes (C11 step 4): once until the shutdown logs the stop unverified, when a retry verifies the relays off; and once for good, when the container still exits within the grace period with the relays held, and the restarted controller turns them off. `POST System/Restart` (C11 step 5): the controller answers 202 and exits 75, and the supervisor starts it again in the same container (Docker's restart count unchanged), ready with the relays off, while the web UI keeps running. |
| `supervisor` | [The container's two processes](deployment.md#the-containers-two-processes): the web UI runs as the image's `app` user with only its `RoofWeb__*` settings, and cannot read `/run/secrets`. The controller killed inside the container while the roof travels: the relays are held, and the supervisor starts it again after its backoff, when it turns them off; the web UI keeps running and Docker does not restart the container (C11 step 6). A crash loop: after the crash limit the controller is left stopped, the health check fails, and the web UI is live and says why (C11 step 7). A forced restart through the web UI's control file starts the crash-looping controller, ignores a second request within 10 s of its start, and kills and starts a running controller (C11 step 8). The web UI killed while the roof moves: the supervisor starts only the web UI again, and the controller and the move carry on (C11 step 9). |
| `c12` | [Commissioning](commissioning.md) C12 with `deploy-roofcontroller-rpi.sh`: a deploy with the roof idle; a deploy while it moves, which stops it (verified all-off) before the old controller is stopped; `--rollback` twice; pre-flight failures (a key that is not configured, a renamed certificate, a wrong certificate password, no RoofOperator key) that leave the running controller untouched; `ALLOWED_HOSTS=localhost`, which fails the remote check and rolls back; relay-register reads failing, so the Stop cannot be verified and the deploy aborts before it stops the controller; a HAT endpoint that does not resolve, so the new controller never becomes ready and is rolled back; and a new web UI that cannot start, which is rolled back with the previous controller and its web UI running again; the web UI live at its published port over HTTPS after a deploy; and the relay register sampled every 0.1 s through steps 3-9, never energized. |
| `migration` | [Moving between Compose and the deploy script](deployment.md#moving-between-compose-and-the-deploy-script): the `pi` profile on the emulator (a Compose override maps no devices and points the controller at the emulator), the script refusing its container, the move to the script, `docker compose up` failing on the name while the script's controller runs, and the move back to the Compose version. The deploy script's `--verify-remote` checks each Compose controller from this machine under a Docker context that does not exist, so any Docker call would fail. A key the controller does not know gets 401. A person added on the Compose controller, their session, and a setting changed through the API (saved to `appsettings.Local.json`, mode 644) carry over to the script's controller and back. Both controllers report the two log levels as changeable through the API, from the shipped defaults, since the image sets no log level in its environment. |

```bash
tests/emulator/deploy-scenarios.sh                      # all four
tests/emulator/deploy-scenarios.sh c12                  # one
SCN_RESULTS_DIR=out tests/emulator/deploy-scenarios.sh  # also writes out/deploy-scenarios.md, with the timings
```

It needs docker with buildx and compose 2.24 or later, curl, jq and openssl. The controller is named `roof-controller`,
as the script and the Compose `pi` profile name it, so the run refuses to start while a `roof-controller` container, or
the run's emulator container (`hvo-deploy-scenarios-hat`) or network (`hvo-deploy-scenarios`), exists on that Docker
host. A refused run removes nothing. A run that starts removes everything it started when it ends, stops a deploy it
left running, and prints the containers, the controller's last log lines and the emulator's history when a check
fails. CI runs it in the "Scenarios" workflow.

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
| `EmulatedCameraTests` | The camera: its JPEG encoder against an independent decoder, the frames it draws of the plant, the MJPEG stream, each mode, a disconnect, the status and the refused requests |
| `Browser/*BrowserTests` | The web UI in Chromium on phones and tablets, each upright and sideways, and a desktop window, against the whole controller, the web UI and the emulated plant: sign-in, Stop on every page in view without scrolling and stopping the roof, what each role is offered, stale and unhealthy status, the camera stalling and going offline, the lease during a lost connection (C9), the reconnect dialog's Stop (C15), and the screenshots in [the web UI's documentation](web.md) |
| `CameraProxyScenarios` | The controller's camera proxy reading the emulated camera over a socket: the frames relayed, 502 for a camera that refuses, the stream aborted after the idle timeout for a frozen one, and the stream ended by a camera server restart |
| `EmulatorModeAppTests` | The whole controller in emulator mode: open and close through the API; a link outage while moving stops the roof and latches a fault until ClearFault; a controller started before the emulator initializes with a latched fault until ClearFault; the start's warning, the telemetry marks and the Degraded health |
| `Web/DashboardTests` | The web UI's `EMULATED HAT` and `SIMULATION` banners, from the status |
| `DevelopmentConfigurationTests` | Development uses the emulator, with the limit switches in force and the production wiring |
| `RoofControllerHealthCheckTests` | Emulator mode's health: Degraded naming the endpoint, and every more serious result naming the emulator |
| `DeploymentValidatorTests` | The deployment check: emulator mode refused outside Development without `AllowOutsideDevelopment`, refused with `/dev/i2c-1` mapped, invalid `HatEmulator` settings |
| `tests/emulator/compose-smoke-test.sh` | The two images together through the compose `emulator` profile, including the camera proxy against the emulated camera |
| CI "Compose profiles" step | The Pi profiles pin emulator mode off; the `emulator` profile maps no devices and has its own network |
| `tests/emulator/deploy-scenarios.sh` | The container's stop and kill while the roof travels, C12 with the deploy script, and moving between Compose and the script ([Container scenarios](#container-scenarios)) |
| `tests/deploy/deploy-script-tests.sh` | Emulator mode: the refusal without `ALLOW_EMULATED_HAT`, the unmapped HAT and the recorded flag; `HatEmulator` settings refused in `EXTRA_DOCKER_ARGS` and in an `--env-file`; I2C devices, `--privileged` (any value Docker reads as true) and `/dev` mounts refused in emulator mode; the verified `hatMode`, with a rollback on a mismatch; and the rollback's HAT checks, before the swap and once the restored version runs |

`EmulatorModeAppTests` is `[DoNotParallelize]`. Each test starts a whole controller host whose startup and HAT polling
hold thread-pool threads, and the in-process emulator needs those threads to answer. Run in parallel, the hosts starve
the emulator into timeouts. The deployed emulator is a separate process, so this does not apply there.
