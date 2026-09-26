# HVO.RoofControllerV4.iPad Copilot Notes

## Simulator Deployment
- Always target the iOS simulator by UDID, not by display name. The current iPad simulator UUID is `F878E277-60EC-43CF-90EC-B1C9050549E6`.
- Drive manual runs with the following MSBuild command pattern:
  ```bash
  dotnet build src/HVO.RoofControllerV4.iPad/HVO.RoofControllerV4.iPad.csproj \
    -t:Run -f net9.0-ios \
    -p:_DeviceName=:v2:udid=F878E277-60EC-43CF-90EC-B1C9050549E6
  ```
  Failing to pass `_DeviceName` with the UDID causes deployment to select a default simulator and can trigger `KeyNotFoundException` errors at runtime.

## CommunityToolkit Popup Wiring
- `HealthStatusPopupViewModel` exposes an internal `Popup` property. `HealthStatusPopup` **must** assign `viewModel.Popup = this;` during construction so the dashboard can retrieve the live popup instance.
- `RoofControllerViewModel.EnsureHealthDialogPopupAsync` captures the popup instance from the view model and attaches the `Closed` handler. Do **not** reintroduce `IPopupLifecycleController`; the captured instance fulfills lifecycle tracking and avoids `Health status popup instance was not created.` exceptions.

## Logging & Monitoring
- Expect verbose HTTP polling in the simulator output while the roof controller dashboard is active. Focus on non-200 responses before investigating popup lifecycle issues.

## Documentation
- Project README: `src/HVO.RoofControllerV4.iPad/README.md`
- Shared roof controller docs: `docs/projects/roof-controller-v4-rpi/` (hardware overview, API contract, operator guide, logging reference)
- Update those references when API endpoints or operational flows change so the mobile client stays aligned with the backend.

Keep these notes in mind when updating the iPad project or debugging simulator runs.
