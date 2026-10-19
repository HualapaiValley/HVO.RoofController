# RoofController Web

The browser UI runs separately from the controller and reaches it only through `HVO.RoofControllerV4.Client`.
[Operator guide and reproducible captures](../../docs/web.md).

Web owns `wwwroot/css/hvo-dark.css` (all 20 roles from the pinned
[design contract](../../docs/ui-redesign/design-contract.md)), `app.css`, scoped component styles and the local
Bootstrap / Bootstrap Icons assets. Their MIT notices remain alongside them in `wwwroot/lib/*/LICENSE`; icon fonts
remain in `wwwroot/lib/bootstrap-icons/fonts`. The unreferenced OpenSans files from the removed theme RCL are not used.
The font stack uses installed system fonts; no remote font or replacement theme package is required.

Stop lives outside the Blazor circuit in `App.razor` and posts directly through `stop.js` / `WebStopEndpoint`. The same
form moves into native fullscreen, then returns on exit. Keep it red through focus, enabled during pending actions,
and present on every page, inline confirmation, reconnect and error. Keep the controller's actual Stop message:
`Acknowledged` alone does not establish verified register evidence. `ui.js` only supplies focus, Escape cancellation
and error association for existing inline editors; it never sends a motion command.

From `src/`, publish Web normally and run `python3 ../build/check-web-publish.py <publish-directory>` against the
result. CI checks both Linux publish RIDs and verifies canonical CSS bytes, every local CSS reference, scoped styles, fingerprint routes, font/icon
notices and runtime dependency removal. The image asset check also fetches the real published HTML/styles/fonts.
Use installed headless Chromium and the controlled test rig to validate layouts; never use a real controller for UI
motion or a screenshot containing a usable credential.
