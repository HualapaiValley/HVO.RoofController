# HVO.RoofControllerV4.iPad Copilot Notes

## Simulator Deployment
- Always target the iOS simulator by UDID, not by display name. The current iPad simulator UUID is `F878E277-60EC-43CF-90EC-B1C9050549E6`.
- Prefer `src/HVO.RoofControllerV4.iPad/run-roofcontroller-ipad-sim.sh` (or `run-roofcontroller-ipad-device.sh --udid <UDID>` for a physical iPad). For a manual run, use this MSBuild command pattern from `src/` so `src/global.json` selects the pinned SDK:
  ```bash
  cd src
  dotnet build HVO.RoofControllerV4.iPad/HVO.RoofControllerV4.iPad.csproj \
    -t:Run -f net10.0-ios \
    -p:_DeviceName=:v2:udid=F878E277-60EC-43CF-90EC-B1C9050549E6
  ```
  Failing to pass `_DeviceName` with the UDID causes deployment to select a default simulator and can trigger `KeyNotFoundException` errors at runtime.

## Safety-Critical Paths
- Stop goes through `StopCommandCoordinator` on its own named `HttpClient` ("RoofControllerStop"). Do not route it through the poll guard, the command lock or the typed client, and never disable the Stop button.
- Open, Close, Clear Fault and configuration POSTs are sent once. Do not add retries for them; after an uncertain outcome, query the status instead.
- Status snapshots and failures are applied through `StatusOrderingGate` so older results never overwrite newer ones.
- `RelayRegisterState` is a read-back of the relay board register. Never describe it as contact, drive or roof verification.
- The API key lives in the Keychain (`SecureStorageApiKeyStore`), never in the settings file or logs.

## CommunityToolkit Popup Wiring
- `HealthStatusPopupViewModel` exposes an internal `Popup` property. `HealthStatusPopup` **must** assign `viewModel.Popup = this;` during construction so the dashboard can retrieve the live popup instance.
- `RoofControllerViewModel.EnsureHealthDialogPopupAsync` captures the popup instance from the view model and attaches the `Closed` handler. Do **not** reintroduce `IPopupLifecycleController`; the captured instance fulfills lifecycle tracking and avoids `Health status popup instance was not created.` exceptions.

## Logging & Monitoring
- Expect verbose HTTP polling in the simulator output while the roof controller dashboard is active. Focus on non-200 responses before investigating popup lifecycle issues.

## Documentation
- Project README: `src/HVO.RoofControllerV4.iPad/README.md`
- Shared roof controller docs: `docs/projects/roof-controller-v4-rpi/` (hardware overview and diagrams) and `docs/architecture-review-2026-08.md`.
- Update those references when API endpoints or operational flows change so the mobile client stays aligned with the backend.

Keep these notes in mind when updating the iPad project or debugging simulator runs.
