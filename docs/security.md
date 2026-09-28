# Roof Controller V4 security

The controller drives a real roof motor, so its HTTP surface is closed by default. Every roof command, status read,
configuration change, health detail and camera stream needs an API key or a signed-in console session. Only two
anonymous endpoints remain: the liveness and readiness probes.

This page covers:

- how to provision keys
- what each role may do
- how the console and camera authenticate
- the transport settings

For TLS certificates and the deploy script, see [deployment.md](deployment.md).

## Roles

Each API key grants exactly one role. Roles are hierarchical: Admin includes Operator, and Operator includes Viewer.

| Role (`Role` value) | Policy name        | Allows                                                                |
|---------------------|--------------------|-----------------------------------------------------------------------|
| `RoofViewer`        | `RoofViewerPolicy` | Roof status, `/health` details, camera streams, console (read-only) |
| `RoofOperator`      | `RoofOperatorPolicy` | Everything above, plus Open, Close, ClearFault and lease renewal       |
| `RoofAdmin`         | `RoofAdminPolicy`  | Everything above, plus configuration, `System/*` and the OpenAPI document |

Stop uses its own policy, `RoofStopPolicy`. Any authenticated key may stop the roof, because Stop never starts motion.
Anonymous callers may stop it only when `RoofControllerSecurity:AllowAnonymousStop` is `true` (default `false`).

## Endpoints

API routes accept only the `X-Api-Key` header (scheme `ApiKey`). A console cookie is never accepted there. Commands
use `POST`; a `GET` to a command route returns 405.

| Method and route                                         | Policy            | Success | Other responses |
|----------------------------------------------------------|-------------------|---------|-----------------|
| `GET  /api/v4.0/RoofControl/Status`                      | Viewer            | 200 `RoofStatusResponse` (after a forced hardware read) | 401, 403, 500 |
| `POST /api/v4.0/RoofControl/Open`                        | Operator          | 200 `RoofStatusResponse` | 401, 403, 409, 503, 500 |
| `POST /api/v4.0/RoofControl/Close`                       | Operator          | 200 `RoofStatusResponse` | 401, 403, 409, 503, 500 |
| `POST /api/v4.0/RoofControl/Stop`                        | Stop (any key)    | 200 `RoofStatusResponse` | 401, 503, 500 |
| `POST /api/v4.0/RoofControl/Lease`                       | Operator          | 200 `RoofStatusResponse` | 401, 403, 409 (`LeaseNotActive`), 503 |
| `POST /api/v4.0/RoofControl/ClearFault?pulseMs=250`      | Operator          | 200 `RoofStatusResponse` | 400 (`pulseMs` outside 50-2000), 401, 403, 409, 503 |
| `GET  /api/v4.0/RoofControl/Configuration`               | Admin             | 200 `RoofConfigurationResponse` (includes `version`) | 401, 403 |
| `POST /api/v4.0/RoofControl/Configuration`               | Admin             | 200 `RoofConfigurationResponse` | 400, 401, 403, 409 |
| `GET  /api/v1.0/System/info`, `GET /api/v1.0/System/metrics` | Admin         | 200 | 401, 403 |
| `GET  /api/v1.0/Camera/{cameraId}/mjpeg` (1-99)          | Viewer (API key or console cookie) | 200 MJPEG | 401, 502, 503, 504 |
| `GET  /openapi/v4.json`                                  | Admin (API key) outside Development | 200 | 401, 403 |
| `GET  /health`                                           | Viewer (API key or cookie) | 200, or 503 (with the JSON body) when Unhealthy | 401 |
| `GET  /health/live`, `GET /health/ready`                 | anonymous         | 200 / 503 | none |
| `/hubs/roof` (SignalR status hub, with `/hubs/roof/negotiate`) | Viewer (API key only) | status messages ([Status hub](#status-hub)) | 401, 403 (`https_required`) |
| `POST /account/login`, `POST /account/logout`            | anonymous (form)  | 302 | 403 (`origin_not_allowed`) |
| `POST /console/stop`                                     | Stop (console cookie and antiforgery token only) | 200 `{outcome, message}` | 400 (stale form token), 401, 403 (`origin_not_allowed`), 503 (stop not verified), 500 |

When a request has no key or an unknown key, the response is 401 with `WWW-Authenticate: ApiKey`. When a valid key
lacks the required role, the response is 403.

### Error bodies

Roof command failures are RFC 7807 ProblemDetails with two extensions:

- `code` is the `RoofControllerErrorCode` name.
- `roofStatus` is the current `RoofStatusResponse`, when one is available.

`type` is `urn:hvo:roof-controller:<code>`.

| Status | Codes | Client action |
|--------|-------|---------------|
| 409 | `FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `ConfigurationVersionConflict`, `ConfigurationRejected` | Refused by an interlock or state. Show the reason; do not retry blindly. |
| 503 | `NotInitialized`, `ShuttingDown`, `HardwareUnavailable`, `RelayStateUnverified` | Not ready. `Retry-After: 2` is set. |
| 400 | `InvalidRequest`, and model validation | Fix the request. |
| 500 | `Unknown` | Unexpected. The detail text is generic; see the controller log. |

Configuration updates are optimistic. `expectedVersion` must equal the `version` from the last GET; otherwise the
update fails with 409 `ConfigurationVersionConflict`. Every field except `confirmSafetyCriticalChange` must be sent,
or the update fails with 400; only `operatorLeaseTimeoutSeconds` and `atSpeedConfirmationTimeoutSeconds` may be null,
which turns them off. Some changes also need `"confirmSafetyCriticalChange": true`, or they fail with 409
`ConfigurationRejected`:

- relay ids
- limit-switch type
- fault polarity
- `ignorePhysicalLimitSwitches`
- turning off the operator lease or the IN4 interlock (turning either on, or changing its timeout, needs no
  confirmation)

Every applied change is written to the log as an `AUDIT` entry. The entry holds the key name, the old and new versions,
and the changed fields.

## Provisioning API keys

Keys are secrets. Never put them in `appsettings*.json`, `launchSettings.json`, compose files or the `.http` samples.
Each entry has these settings:

| Setting | Meaning |
|---------|---------|
| `RoofControllerSecurity:ApiKeys:N:Name` | Identifies the holder in logs and audit entries (for example `console-operator`). Not secret. |
| `RoofControllerSecurity:ApiKeys:N:Role` | `RoofViewer`, `RoofOperator` or `RoofAdmin`. |
| `RoofControllerSecurity:ApiKeys:N:Key` | The key, at least 24 characters. |
| `RoofControllerSecurity:ApiKeys:N:KeySha256` | Alternative to `Key`: 64 hex characters of SHA-256 over the key's UTF-8 bytes. Use it so the controller never stores the key itself. |

Set exactly one of `Key` or `KeySha256`. An entry that is incomplete, too short, has an unknown role, or sets both is
skipped. The startup log reports it by index and name, never by value. If no usable key remains, the controller logs a
Critical message and answers 401 on every protected endpoint.

Generate a key and its hash without a trailing newline:

```bash
KEY=$(openssl rand -base64 32 | tr -d '\n')
printf '%s' "$KEY" | sha256sum | cut -d' ' -f1     # value for KeySha256 (optional)
```

### On the Pi (Docker secrets directory)

The app reads one file per setting from `/run/secrets` (`AddKeyPerFile`). The file name is the setting name, with `__`
in place of `:`. The deploy script and `docker-compose.yaml` bind-mount `/etc/hvo-roof/secrets` there read-only.

```bash
sudo install -d -m 700 /etc/hvo-roof/secrets
cd /etc/hvo-roof/secrets
printf '%s' 'console-operator' | sudo tee RoofControllerSecurity__ApiKeys__0__Name >/dev/null
printf '%s' 'RoofOperator' | sudo tee RoofControllerSecurity__ApiKeys__0__Role >/dev/null
printf '%s' "$KEY"         | sudo tee RoofControllerSecurity__ApiKeys__0__Key  >/dev/null
# ...repeat with index 1, 2, ... for a viewer key, an admin key, etc.
sudo chmod 600 /etc/hvo-roof/secrets/*
```

If the image runs as a non-root user (the .NET images define `APP_UID`, 1654), that UID must be able to read the files.
Use `sudo chown -R 1654 /etc/hvo-roof/secrets`. Do not make the files world-readable.

Environment variables with the same names (`RoofControllerSecurity__ApiKeys__0__Key=...`) also work. However, they
are visible to `docker inspect`, so prefer the secrets directory.

Recommended keys:

| Holder | Role |
|--------|------|
| Console operator | `RoofOperator` |
| Deploy script | `RoofOperator` (the pre-deploy Stop and the post-deploy Status and Stop checks; the deployment check confirms it is configured) |
| Monitoring | `RoofViewer` |
| Maintainer | `RoofAdmin` (configuration and OpenAPI) |

The console login accepts any of these keys.

### Rotation

Keys reload when the configuration changes. After replacing a key file, restart the container to be certain. A console
session that was signed in with a removed, rotated or re-roled key ends at the next request or revalidation.

### Local development

`launchSettings.json` runs in Development over plain HTTP, and there `RequireHttps` defaults to `false`. Export keys in
the shell that starts the app:

```bash
export RoofControllerSecurity__ApiKeys__0__Name=dev-admin
export RoofControllerSecurity__ApiKeys__0__Role=RoofAdmin
export RoofControllerSecurity__ApiKeys__0__Key="$(openssl rand -base64 32 | tr -d '\n')"
export ROOF_API_KEY="$RoofControllerSecurity__ApiKeys__0__Key"   # used by HVO.RoofControllerV4.RPi.http
dotnet run --project src/HVO.RoofControllerV4.RPi --launch-profile Debug
```

## Web console

The Blazor console uses a separate cookie scheme, `RoofConsoleCookie`. Its cookie, `hvo.roof.console`, is:

- HttpOnly
- `SameSite=Strict`
- Secure when served over HTTPS
- valid for 8 hours, with sliding expiration

Signing in works like this:

1. An unauthenticated page navigation redirects to `/login?returnUrl=...`.
2. The `/login` page is static server-rendered. It posts a form to `POST /account/login` with these fields:
   - `accessKey`: any configured API key
   - `returnUrl`
   - the antiforgery token (`<AntiforgeryToken />`)
3. On success, the response is a 302 to `returnUrl` (local paths only; anything else goes to `/`). On failure:
   - `/login?error=1`: wrong key. The answer comes after a 1-second delay.
   - `/login?error=2`: missing or stale form token.
4. `POST /account/logout` clears the cookie and redirects to `/login`.

A user with the wrong role for a page is redirected to `/access-denied`.

The Blazor hub (`/_blazor`) refuses unauthenticated connections with 401. The hub, `POST /account/*` and
`POST /console/*` also refuse a cross-site `Origin` with 403 `origin_not_allowed`. Behind a reverse proxy that changes
the host name, add the public origin to `RoofControllerSecurity:AllowedOrigins`.

The console runs over a SignalR connection, so two things do not depend on it:

- **Stop in the reconnect dialog.** While the connection is down, the dialog's **Stop roof** button posts to
  `POST /console/stop` with `fetch`. The endpoint accepts only the console cookie, applies the Stop policy, and needs
  the antiforgery token rendered into the dialog. It answers with the same outcome the console's own Stop reports
  (`Acknowledged`, `RelayUnverified` or `Failed`, with a message). When the cookie is no longer accepted (it expired,
  or its key was removed or rotated), the endpoint returns 401 and the dialog reports that the session has ended.
- **Operator lease.** The console renews the lease on the server only while the browser connection is up. When the
  server sees the connection drop (at once for a closed tab, within about 30 s for a silent network loss) renewal
  stops, and it does not resume on reconnect, so the lease runs out and stops the roof.

## Status hub

`/hubs/roof` is a SignalR hub (JSON protocol only) that pushes the roof's status to UI clients, so they do not poll
`GET .../Status`. It only sends: it has no methods a client can call, and every command, Stop included, stays on the
REST API. Invoking any method returns an error for that call and changes nothing.

- **Authentication.** Every role may connect, with the `X-Api-Key` header on the negotiate request and on the
  WebSocket or long-polling requests that follow. The key is never accepted in the query string. The console cookie is
  not accepted, so a page on another site cannot open a connection with a signed-in browser's cookie. Without a valid
  key the negotiate request returns 401; plain HTTP from the network returns 403 `https_required` when
  `RequireHttps` is on, as the API does.
- **Revocation.** A connection authenticates once, when it opens. Once a second, on its own schedule and whether or
  not the status is changing, the controller checks each connection's key again and closes the connection when the key
  was removed, rotated or given another role, and logs the key's name (never its value).
- **Limits.** At most 32 connections are open at once, and at most 8 with one key. Give each client (the kiosk, the
  web UI, each CLI user) its own key: clients that share a key share its 8 connections, and the per-key limit only
  stops one client that leaks connections from taking another's when their keys differ. A connection past a limit is
  closed with the reason ("The controller is not accepting more status connections." or "... for this key.") and
  SignalR's close message tells the client not to reconnect; a client that still wants status reconnects on its own
  after a growing delay. Refusals are checked before the controller is read, and logged at Warning at most once every
  10 seconds with a count of the others; SignalR also logs each refused connection at Error under
  `Microsoft.AspNetCore.SignalR.HubConnectionHandler`. A client may send nothing but the handshake and pings (messages
  over 4 KB close the connection).
- **Messages.** The client method `Status` receives a `RoofStatusHubMessage` (`RoofStatusHubContract` in
  `HVO.RoofControllerV4.Common`):

  | Field | Meaning |
  |-------|---------|
  | `status` | The full `RoofStatusResponse`, as `GET .../Status` returns it (camelCase names, enums as strings). |
  | `sequence` | Goes up by one for each message the controller publishes, from 1 when the process starts. A slow client can see gaps, never an older message after a newer one. |
  | `serverTimeUtc` | The controller's clock when the message was published. |
  | `instanceId` | Changes when the controller restarts; the sequence starts again, so compare sequences only within one instance. |

  A connection receives the current status when it opens, every status change, and the current status again when
  nothing was published for 1 second (the heartbeat). A client that has heard nothing for 3 seconds should treat its
  view as stale. A client that falls behind receives only the newest message it has not yet had, so a slow or stalled
  client never delays the controller or the other clients.

## Camera proxy

The controller proxies the Blue Iris MJPEG stream so clients never see Blue Iris credentials. API clients call
`GET /api/v1.0/Camera/{id}/mjpeg` with a Viewer `X-Api-Key`. The console's player uses its cookie.

Stream limits and failure responses:

| Situation | Response |
|-----------|----------|
| More than `BlueIris:MaxConcurrentStreams` (default 4) streams | 503 with `Retry-After: 5` |
| Blue Iris fails or answers with an error | 502 |
| Blue Iris does not answer within `BlueIris:ResponseHeadersTimeout` | 504 |
| Proxy not configured | 503 |

A stream with no data for `BlueIris:StreamIdleTimeout` is closed. Streams also end when the host begins shutting down.

Blue Iris settings:

| Setting | Secret |
|---------|--------|
| `BlueIris:BaseUrl` | No |
| `BlueIris:UserName` | Yes |
| `BlueIris:Password` | Yes |
| `BlueIris:MaxConcurrentStreams`, `BlueIris:ConnectTimeout`, `BlueIris:ResponseHeadersTimeout`, `BlueIris:StreamIdleTimeout` | No |

Put the user name and password in the secrets directory as `BlueIris__UserName` and `BlueIris__Password`. Set both or
neither. When both are empty, no `Authorization` header is sent. An empty `BaseUrl` disables the proxy.

> **Rotate the Blue Iris credential.** Earlier revisions of `Controllers/CameraController.cs` contained a hard-coded
> Blue Iris credential. It has been removed from the source, but it remains in the public git history (commit
> `99e51a3` and earlier), so treat it as disclosed:
>
> 1. Change that Blue Iris user's password (or delete the user).
> 2. Create a dedicated low-privilege, view-only user for the proxy.
> 3. Provision the new user name and password through the secrets directory.
>
> Rewriting history is optional and does not replace rotation.

## Transport and host settings

| Setting | Default | Effect |
|---------|---------|--------|
| `RoofControllerSecurity:RequireHttps` | `true` outside Development | Plain-HTTP requests from non-loopback clients get 403 `https_required` instead of being served. There is no redirect, because a redirect would already have exposed the key. Loopback requests are exempt, as are `/health/live` and `/health/ready`. Setting `false` in Production logs a warning. |
| `RoofControllerSecurity:AllowAnonymousStop` | `false` | Lets unauthenticated callers use `POST .../Stop`. Logs a warning. |
| `RoofControllerSecurity:AllowedOrigins` | empty | Extra origins allowed for the console hub and `/account/*`. |
| `AllowedHosts` | `*` | Set it to the controller's host names (for example `roof-pi;roof-pi.local;localhost`) to refuse DNS-rebinding requests. The list must include `localhost`: the container health check and the deploy script's in-container calls use it, and the [deployment check](deployment.md#the-deployment-check) refuses a list without it. Production logs a warning while it is `*`. |

HSTS is sent only when an HTTPS endpoint is configured.

Because `/health/ready` is exempt, readiness passes even when every remote API request would get 403. The deployment
check (`--validate-deployment`, run first by the deploy script and by both Pi compose profiles, `pi` and
`pi-lan-http`; the test-rig `emulator` profile does not run it) therefore fails when
`RequireHttps` is in effect without an HTTPS listener, or with a certificate that cannot be loaded. See
[deployment.md](deployment.md#the-deployment-check).

Unhandled exceptions return a generic 500 ProblemDetails with a trace id. Exception messages and stack traces are
never returned to clients outside Development.

## Logging

Keys, cookies and Blue Iris credentials are never logged. Outgoing Blue Iris request headers are redacted in HTTP
client logs. Each command logs the caller's key name and remote address. Configuration changes log an `AUDIT` entry.
