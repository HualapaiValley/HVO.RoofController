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

From `src/`, validate both Linux publish RIDs:

```bash
for rid in linux-x64 linux-arm64; do
  dotnet publish HVO.RoofControllerV4.Web -c Release -r "$rid" --self-contained false -o "/tmp/hvo-web-$rid"
  python3 ../build/check-web-publish.py "/tmp/hvo-web-$rid"
done
```

CI runs the same checks for canonical CSS bytes, every local CSS reference, scoped styles, the camera module,
fingerprint routes, font/icon notices and runtime dependency removal. To compare served bytes/MIME too, start the
x64 output's `HVO.RoofControllerV4.Web.dll` on a controlled loopback URL with `RoofWeb:ControllerUrl` pointing to an
unreachable loopback port, then pass `--base-url http://127.0.0.1:<port>` to the checker. The image asset check also fetches the real published HTML/styles/fonts.
Use installed headless Chromium and the controlled test rig to validate layouts; never use a real controller for UI
motion or a screenshot containing a usable credential.
