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
| `RoofViewer`        | `RoofViewerPolicy` | Roof status, `/health` details, camera tickets and streams, console (read-only) |
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
| `POST /api/v1.0/Camera/{cameraId}/ticket` (1-99)         | Viewer (API key)  | 200 `CameraStreamTicketResponse` | 401, 403 |
| `GET  /api/v1.0/Camera/{cameraId}/mjpeg`                 | API key, console cookie, or `?ticket=` | 200 MJPEG | 401, 502, 503, 504 |
| `GET  /openapi/v4.json`                                  | Admin (API key) outside Development | 200 | 401, 403 |
| `GET  /health`                                           | Viewer (API key or cookie) | 200, or 503 (with the JSON body) when Unhealthy | 401 |
| `GET  /health/live`, `GET /health/ready`                 | anonymous         | 200 / 503 | none |
| `POST /account/login`, `POST /account/logout`            | anonymous (form)  | 302 | 403 (`origin_not_allowed`) |

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
update fails with 409 `ConfigurationVersionConflict`. Some changes also need `"confirmSafetyCriticalChange": true`,
or they fail with 409 `ConfigurationRejected`:

- relay ids
- limit-switch type
- fault polarity
- `ignorePhysicalLimitSwitches`

Every applied change is written to the log as an `AUDIT` entry. The entry holds the key name, the old and new versions,
and the changed fields.

## Provisioning API keys

Keys are secrets. Never put them in `appsettings*.json`, `launchSettings.json`, compose files or the `.http` samples.
Each entry has these settings:

| Setting | Meaning |
|---------|---------|
| `RoofControllerSecurity:ApiKeys:N:Name` | Identifies the holder in logs and audit entries (for example `ipad-dome`). Not secret. |
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
printf '%s' 'ipad-dome'    | sudo tee RoofControllerSecurity__ApiKeys__0__Name >/dev/null
printf '%s' 'RoofOperator' | sudo tee RoofControllerSecurity__ApiKeys__0__Role >/dev/null
printf '%s' "$KEY"         | sudo tee RoofControllerSecurity__ApiKeys__0__Key  >/dev/null
# ...repeat with index 1, 2, ... for a viewer key, an admin key, the web console key, etc.
sudo chmod 600 /etc/hvo-roof/secrets/*
```

If the image runs as a non-root user (the .NET images define `APP_UID`, 1654), that UID must be able to read the files.
Use `sudo chown -R 1654 /etc/hvo-roof/secrets`. Do not make the files world-readable.

Environment variables with the same names (`RoofControllerSecurity__ApiKeys__0__Key=...`) also work. However, they
are visible to `docker inspect`, so prefer the secrets directory.

Recommended keys:

| Holder | Role |
|--------|------|
| iPad app | `RoofOperator` |
| Deploy script | `RoofOperator` (used for the pre-deploy Stop) |
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

The Blazor hub (`/_blazor`) refuses unauthenticated connections with 401. The hub, and `POST /account/*`, also refuse a
cross-site `Origin` with 403 `origin_not_allowed`. Behind a reverse proxy that changes the host name, add the public
origin to `RoofControllerSecurity:AllowedOrigins`.

## Camera proxy

The controller proxies the Blue Iris MJPEG stream so clients never see Blue Iris credentials. Clients that can send
headers call `GET /api/v1.0/Camera/{id}/mjpeg` with `X-Api-Key`. The console's `<img>` uses its cookie.

Clients that cannot attach headers, such as a WebView image, work like this:

1. Call `POST /api/v1.0/Camera/{id}/ticket` with a Viewer key.
2. Open the returned relative `url` (`/api/v1.0/Camera/{id}/mjpeg?ticket=...`) within 60 seconds.

A ticket is HMAC-signed with a per-process random key. It is bound to one camera, and it only authorizes *starting* a
stream.

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
| `AllowedHosts` | `*` | Set it to the controller's host names (for example `roof-pi;roof-pi.local;localhost`) to refuse DNS-rebinding requests. Production logs a warning while it is `*`. |

HSTS is sent only when an HTTPS endpoint is configured.

Unhandled exceptions return a generic 500 ProblemDetails with a trace id. Exception messages and stack traces are
never returned to clients outside Development.

## Logging

Keys, cookies and Blue Iris credentials are never logged. Outgoing Blue Iris request headers are redacted in HTTP
client logs. Each command logs the caller's key name and remote address. Configuration changes log an `AUDIT` entry.
