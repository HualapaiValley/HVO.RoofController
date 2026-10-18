# RoofController UI redesign prototypes

These three standalone HTML files are the approved visual references for redesigning all supported RoofController interfaces. They use SkyMonitor CameraAgent's clean HVO Dark styling, adapted to each platform.

| Interface | Prototype | Design direction |
| --- | --- | --- |
| Web | [Web prototype](roofcontroller-cameraagent.html) | Responsive panels, camera preview, clear status, and touch-friendly roof controls. |
| Avalonia kiosk and shared Mac screens | [Native kiosk prototype](roofcontroller-avalonia-kiosk.html) | A fixed screen layout, large touch targets, a permanent Stop column, and notices that fit within the screen. Extend this visual vocabulary to all native pages and dialogs. |
| Terminal.Gui | [Terminal prototype](roofcontroller-terminal.html) | Character-cell panels, aligned readings, persistent F9 Stop, keyboard navigation, and terminal color fallbacks. Extend the styling to the installer wizard as well. |

Download the files and open them in a browser; they have no external dependencies. Their buttons and sample states are local simulations. The files are design references, not native Avalonia or Terminal.Gui implementations.

## Agreed direction

- Address every screen, dialog, form, wizard step, shared component, and loading, empty, denied, fault, stale, and disconnected state. The prototypes establish a common visual language; the redesign is broader than the illustrated dashboard screens.
- Use the web prototype's red Stop treatment throughout. The kiosk prototype includes the approved change from amber to matching red.
- Keep the Pi kiosk free of page scrolling. The 10-inch panel's 1920 x 1200 resolution is an assumption to reconcile with the configured display mode and physical touch sizing. Preserve existing supported display sizes.
- Retain the native kiosk's minimum 12 mm touch targets. Preserve terminal operation at 80 x 24 and color, monochrome, and ASCII fallbacks.
- Replace the HVO.WebSite.Themes Razor Class Library with styles owned by the Web project and a documented shared token/palette contract for the native and terminal clients. Remove the obsolete project and its build, asset, deployment, and documentation references.

## Implementation boundaries

The production API, client library, authentication and authorization, Stop availability and acknowledgement, motion interlocks, lease renewal, watchdogs, and stale-state rules remain authoritative. Do not copy simplified prototype JavaScript as production behavior. In particular, production Stop remains visible and enabled on every applicable page and prompt, including stale or disconnected states, and never claims delivery without an acknowledgement.

Validate the redesign through the emulator and the existing headless web, Avalonia, and terminal test workflows. Replace simulated fixture values with controller-backed information, and regenerate the documented screenshots when implementing the redesign.

## Prototype checks already performed

- Web: layouts from 320 to 1280 pixels wide, touch targets, and simulated motion, fault, disconnect, camera expansion, and console interactions.
- Native concept: complete screen fit at 1920 x 1200, 1280 x 800, 1280 x 720, and 960 x 600; locked, moving, fault, stale, offline, and notice states; Stop remains available.
- Terminal concept: all five pages across six sample states at 80 x 24 and 120 x 32 with no character-cell overflow; keyboard navigation, F9 with a review prompt open, monochrome and ASCII modes, and simulated Stop and quit handling.

These checks validate the HTML prototypes. The production implementations require their own platform-specific verification.
