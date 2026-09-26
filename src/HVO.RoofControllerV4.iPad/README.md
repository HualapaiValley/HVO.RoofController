# HVO.RoofControllerV4.iPad

The HVO Roof Controller V4 iPad app is a .NET MAUI client (`net10.0-ios`) that monitors and operates the observatory roof through the Raspberry Pi controller's HTTP API (`/api/v4.0/RoofControl/...`).

The app is a convenience for the operator. It is not a safety device: the physical stop and the controller's own interlocks (limit switches, watchdog, fault input, operator lease) are what stop the roof.

## Documentation

- [`docs/projects/roof-controller-v4-rpi/hardware-overview.md`](../../docs/projects/roof-controller-v4-rpi/hardware-overview.md): hardware and wiring overview for the controller this app talks to.
- [`docs/architecture-review-2026-08.md`](../../docs/architecture-review-2026-08.md): architecture review that the current Stop, lease and security behaviour follows from.

## Prerequisites

- macOS with Xcode and an iPadOS simulator runtime (or a provisioned iPad).
- .NET SDK 10.0.302, pinned by [`src/global.json`](../global.json) (patch roll-forward only). Run `dotnet` commands from `src/` so the pin applies.
- The MAUI iOS workload: `dotnet workload install maui-ios` (or `maui`).
- Network access to the roof controller, and its API key.

The project does not build on Linux or Windows (`NETSDK1147` / `BuildProject=false`); that is expected.

## Build and launch

Both scripts live next to this README, work from any directory and run the build from `src/`.

Simulator (defaults to the development iPad simulator `F878E277-60EC-43CF-90EC-B1C9050549E6`; override with `--udid` or `HVO_IPAD_SIM_UDID`):

```bash
src/HVO.RoofControllerV4.iPad/run-roofcontroller-ipad-sim.sh --configuration Debug
```

Device (the UDID is required; list devices with `xcrun devicectl list devices`):

```bash
src/HVO.RoofControllerV4.iPad/run-roofcontroller-ipad-device.sh --udid <device-udid> --configuration Release
```

Arguments after `--` are passed to `dotnet build`, for example `-- -p:CodesignKey="Apple Development: ..."`.

### Manual dotnet CLI run

```bash
cd src
dotnet build HVO.RoofControllerV4.iPad/HVO.RoofControllerV4.iPad.csproj \
  -t:Run \
  -f net10.0-ios \
  -p:_DeviceName=:v2:udid=F878E277-60EC-43CF-90EC-B1C9050549E6
```

`_DeviceName` must contain the simulator UDID in the `:v2:udid=` form; a device name causes a `KeyNotFoundException` during deployment. For a physical iPad use `-p:RuntimeIdentifier=ios-arm64 -p:_DeviceName=<device-udid>`.

## First-run setup

Open the **Configuration** tab. Saved local settings apply immediately: requests still in flight to the previous controller are cancelled, the dashboard clears what it showed about that controller and reconnects. The controller URL, camera URL and API key are applied together or not at all.

### Controller URL: prefer https

- Enter the API base URL, for example `https://observatory.local:7151/api/v4.0/`. Only `http` and `https` URLs are accepted.
- With `http://` the API key and every command cross the network in clear text. The app asks for confirmation before using an http URL, shows an "HTTP (unencrypted)" badge while connected over http, and the controller may refuse http outright (`403 https_required`).
- App Transport Security (Info.plist) blocks plain http except for local-network names (`NSAllowsLocalNetworking`) and the observatory controller at `192.168.2.3` (`NSExceptionAllowsInsecureHTTPLoads`). Any other http host must either move to https or get its own exception.

### API key

- The controller requires an `X-Api-Key` header on every request. Enter the key in **Configuration → API Key**. Leave the field blank to keep the stored key; use **Remove the stored API key** to delete it.
- The key is stored in the iOS Keychain (MAUI `SecureStorage`). It is never written to the settings file, the logs or the notification history.
- Migration: a settings file from an older version that still contains an `ApiKey` value is migrated on first launch. The key is moved into the Keychain and the file is rewritten without it; the dashboard says so.
- If the Keychain is unavailable, the key is kept for the current session only and the app says so. This is expected on the **simulator** when the build is not signed with a Keychain entitlement (CI builds use `Codesign=false`). Signed device builds use the app's default Keychain access group and need no extra entitlement.

### Settings file

- Local settings are stored in `roofcontroller.settings.json` in the app data folder and written atomically (temporary file, then replace).
- If the file cannot be read or fails validation, it is moved aside to a timestamped backup, the built-in defaults from `appsettings.json` are used, and the dashboard shows a notice naming the backup. The app does not crash on a bad file.

## Operating notes

### Stop

- **Stop is always enabled.** It does not wait for the status poll or for another command, uses its own HTTP connection pool, and is sent even while the status is stale or the controller looks unreachable.
- Each press has a deadline of about 3 seconds, with at most one retry inside it. Stop is the only command that is retried automatically.
- The Controls card shows what actually happened to the most recent press:
  - **Stop: sending…**: the request is in flight.
  - **Stop acknowledged (relay register not verified)**: the controller accepted Stop.
  - **Stop acknowledged; relay register reads off**: the controller also read back its relay register as off. This is a read-back of the relay board's register, not a check of the relay contacts, the drive or the roof.
  - **STOP NOT VERIFIED**: the controller ran Stop but could not verify its relay register (`503 RelayStateUnverified`).
  - **STOP REFUSED**: the controller rejected the request.
  - **STOP OUTCOME UNKNOWN**: no answer arrived within the deadline (timeout or network failure). The controller may or may not have stopped.
  - **STOP NOT SENT**: no valid controller URL is configured.
- For anything other than an acknowledgement, check the roof and use the physical stop if it is moving. Results and failures are ordered by press and by the controller's status version, so a late poll or an older Open result never overwrites a newer Stop outcome.

### Open, Close and Clear Fault

- Sent once and never re-sent automatically. If the outcome is unclear (timeout, connection drop, gateway error), the app says so and queries the status instead.
- Disabled while the status is stale, the roof is moving, a fault is latched, the controller is shutting down or a Clear Fault pulse is in progress. Stop stays enabled in all of these.
- Controller rejections are shown with the controller's reason (for example `FaultLatched`, `InterlockActive`, `OperationInProgress`) and are distinguished from connectivity loss.

### Operator lease

- When the controller has an operator lease configured (`OperatorLeaseTimeoutSeconds`), a motion only continues while an operator keeps renewing it. After Open or Close the app renews the lease about every third of the lease period while the roof is moving, and shows the time remaining.
- Renewal only runs while the app is in the foreground. If the iPad locks or the app is backgrounded during a motion, renewals stop and the controller stops the roof when the lease runs out (`OperatorLeaseExpired`). This is deliberate.
- Switching to another controller in Configuration also ends renewal for the previous one; the app asks for confirmation if a motion may be in progress.

### Status freshness and warnings

- The status is considered stale after max(2 × poll interval + 3 s, 8 s) without a successful update. Stale status is shown with a "Status not current" banner and neutral badges (never green), and motion commands are disabled.
- The dashboard also shows: simulation mode (no hardware), limit switches being ignored, a latched fault and its reason, relay register not verified, unhealthy safety inputs, the controller shutting down, and the controller's name and instance id.
- If the controller's instance id changes (restart or replacement), the app warns that any earlier motion or lease has ended.
- After repeated connection failures the app offers **Retry** or **Keep offline**. Either choice keeps the app running and polling; there is no exit button.

### Controller configuration

- The **Remote Controller Settings** card edits the controller's configuration. Saves send the version the values were loaded at; if someone else changed the configuration in the meantime, nothing is applied and the current values are reloaded.
- Changes to relay IDs, limit switch wiring (normally closed), the drive fault input polarity, or ignoring the physical limit switches need explicit confirmation before they are sent.

### Camera

- The camera stream is only loaded while the Camera tab is visible and the app is in the foreground. Leaving the tab closes the stream.
- For the controller's own camera proxy (`/api/v1.0/camera/{id}/mjpeg` on the controller host) the app first requests a short-lived stream ticket (`POST /api/v1.0/Camera/{id}/ticket`, valid 60 seconds), because the WebView cannot send the API key header. Other camera URLs are loaded as configured.
- The status shows **Stream loaded (first frame)** or **Connection failed**. A stream that stops updating after the first frame is not detected; check the image timestamp or the roof itself.

## Troubleshooting and log collection

Tail the simulator logs filtered to the app:

```bash
xcrun simctl spawn F878E277-60EC-43CF-90EC-B1C9050549E6 \
  log show --last 5m --style compact \
  --predicate "processImagePath CONTAINS 'RoofController'" \
  --info --debug
```

For continuous monitoring, use `log stream` instead of `log show`:

```bash
xcrun simctl spawn F878E277-60EC-43CF-90EC-B1C9050549E6 \
  log stream --style compact \
  --predicate "processImagePath CONTAINS 'RoofController'"
```

Common issues:

- **401 / 403 "Access refused by the controller"**: the API key is missing or wrong, or the key's role does not allow the action (configuration changes need an Admin key).
- **403 `https_required`**: the controller only accepts https; change the controller URL to `https://`.
- **409 responses** (`FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `ConfigurationVersionConflict`): the controller refused the command in its current state. The message names the reason.
- **503 responses**: the controller is not ready (not initialized, shutting down, hardware unavailable) or could not verify its relay register. The health dialog shows the controller's health checks, including when `/health` itself returns 503.
- **"Controller unreachable"**: network, ATS or URL problem. Stop is still attempted when pressed; use the physical stop if the roof is moving.
- **"Secure storage is unavailable"** on the simulator: see the API key section above.

## Code map

- `ViewModels/RoofControllerViewModel.cs`: dashboard, commands, status ordering, staleness, lease renewal and settings.
- `Services/StopCommandCoordinator.cs`: the dedicated Stop path (deadline, single retry, outcome states).
- `Services/StatusOrderingGate.cs`: orders status snapshots and failures by request sequence and `StatusVersion`.
- `Services/LeaseRenewalTracker.cs`: operator lease renewal schedule.
- `Services/RoofControllerApiClient.cs`: HTTP client, timeouts (status 3 s, commands 5 s), ProblemDetails parsing; only GETs are retried.
- `Services/RoofControllerConfigurationService.cs`, `Services/SettingsFileStore.cs`, `Services/SecureStorageApiKeyStore.cs`: settings file and Keychain storage.
- `Services/CameraStreamResolver.cs`: camera proxy detection and ticket URL validation.
- `appsettings.json`: built-in defaults (controller and camera URLs, poll interval).
- `Platforms/iOS/Info.plist`: App Transport Security and local network settings.
