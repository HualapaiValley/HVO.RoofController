# Roof Controller V4 security

The controller drives a real roof motor, so its HTTP surface is closed by default. Every roof command, status read,
configuration change, health detail and camera stream needs an API key, a person's session or a signed-in console
session. The anonymous endpoints are the liveness and readiness probes, and signing in with a name and password.

This page covers:

- how to provision keys
- what each role may do
- how people sign in, and how admins manage people, keys and sessions
- remote settings: who may change what, the settings file, hand edits and restarts
- how the console and camera authenticate
- the transport settings

For TLS certificates and the deploy script, see [deployment.md](deployment.md).

## Roles

Each API key, person and session grants exactly one role. Roles are hierarchical: Admin includes Operator, and
Operator includes Viewer.

| Role (`Role` value) | Policy name        | Allows                                                                |
|---------------------|--------------------|-----------------------------------------------------------------------|
| `RoofViewer`        | `RoofViewerPolicy` | Roof status, `/health` details, camera streams, console (read-only) |
| `RoofOperator`      | `RoofOperatorPolicy` | Everything above, plus Open, Close, ClearFault, lease renewal and the `ui` settings |
| `RoofAdmin`         | `RoofAdminPolicy`  | Everything above, plus every other setting, `System/*` (including Restart) and the OpenAPI document |

Stop uses its own policy, `RoofStopPolicy`. Any authenticated caller (any key or session) may stop the roof, because
Stop never starts motion. Sign-in lockouts never apply to Stop.
Anonymous callers may stop it only when `RoofControllerSecurity:AllowAnonymousStop` is `true` (default `false`).

## Endpoints

API routes accept an API key in the `X-Api-Key` header (scheme `ApiKey`) or a session token in
`Authorization: Bearer <token>` (scheme `RoofSession`). The policy scheme `RoofApi` uses the session when a request
carries `Authorization: Bearer`, and the key otherwise. A console cookie is never accepted there, and neither a key nor a
token is accepted in the query string. Commands use `POST`; a `GET` to a command route returns 405.

Stop uses its own scheme, `RoofStop`, so that a client holding a session that has just ended can still stop the roof. It
tries the session first; when the session is refused and the request also carries `X-Api-Key`, it uses the key. Every
other route refuses such a request with 401, and never falls back to the key.

| Method and route                                         | Policy            | Success | Other responses |
|----------------------------------------------------------|-------------------|---------|-----------------|
| `GET  /api/v4.0/RoofControl/Status`                      | Viewer            | 200 `RoofStatusResponse` (after a forced hardware read) | 401, 403, 500 |
| `POST /api/v4.0/RoofControl/Open`                        | Operator          | 200 `RoofStatusResponse` | 401, 403, 409, 503, 500 |
| `POST /api/v4.0/RoofControl/Close`                       | Operator          | 200 `RoofStatusResponse` | 401, 403, 409, 503, 500 |
| `POST /api/v4.0/RoofControl/Stop`                        | Stop (any key or session) | 200 `RoofStatusResponse` | 401, 503, 500 |
| `POST /api/v4.0/RoofControl/Lease`                       | Operator          | 200 `RoofStatusResponse` | 401, 403, 409 (`LeaseNotActive`), 503 |
| `POST /api/v4.0/RoofControl/ClearFault?pulseMs=250`      | Operator          | 200 `RoofStatusResponse` | 400 (`pulseMs` outside 50-2000), 401, 403, 409, 503 |
| `GET  /api/v4.0/RoofControl/Configuration`               | Admin             | 200 `RoofConfigurationResponse` (includes `version`) | 401, 403 |
| `POST /api/v4.0/RoofControl/Configuration`               | Admin             | 200 `RoofConfigurationResponse` | 400, 401, 403, 409, 503 (`SettingsStoreUnavailable`) |
| `GET  /api/v4.0/Settings`, `GET /api/v4.0/Settings/Catalogue` | Viewer (API key or session) | 200 `RoofSettingsResponse`, `RoofSettingsCatalogueResponse` (only the settings the caller may read) | 401 |
| `POST /api/v4.0/Settings/{group}`                        | Operator for `ui`, Admin for every other group | 200 `RoofSettingsResponse` | 400, 401, 403 (`SettingNotPermitted`), 404 (`SettingNotFound`), 409, 503 (`SettingsStoreUnavailable`) |
| `POST /api/v4.0/Settings/Reload`, `POST /api/v4.0/Settings/Discard` | Admin (API key or session) | 200 `RoofSettingsResponse` | 400, 401, 403, 409, 503 |
| `POST /api/v4.0/System/Restart`                          | Admin (API key or session) | 202 `RoofRestartResponse`, then the controller exits with code 75 | 401, 403, 409 (`RestartRefused`) |
| `GET  /api/v1.0/System/info`, `GET /api/v1.0/System/metrics` | Admin         | 200 | 401, 403 |
| `GET  /api/v1.0/Camera/{cameraId}/mjpeg` (1-99)          | Viewer (API key, session or console cookie) | 200 MJPEG | 401, 502, 503, 504 |
| `POST /api/v4.0/Auth/Session`                            | anonymous         | 200 `RoofSessionResponse` | 400, 401 (`SignInFailed`), 429 (`SignInLockedOut`, `SignInBusy`), 503 |
| `POST /api/v4.0/Auth/Pin`                                | a kiosk key (API key only) | 200 `RoofSessionResponse` | 400, 401, 403 (`KioskKeyRequired`), 429, 503 |
| `GET  /api/v4.0/Auth/Pin/Users`                          | a kiosk key (API key only) | 200 `RoofPinUserResponse[]` | 401, 403 (`KioskKeyRequired`), 503 |
| `GET  /api/v4.0/Auth/Me`                                 | any key or session | 200 `RoofCallerResponse` | 401 |
| `DELETE /api/v4.0/Auth/Session`                          | a session (signs it out) | 204 | 400 (an API key), 401, 503 |
| `POST /api/v4.0/Auth/Password`                           | a session (own password) | 204 | 400, 401 (`SignInFailed`), 429, 503 |
| `GET, POST /api/v4.0/Identity/Users`, `GET, PUT, DELETE /api/v4.0/Identity/Users/{name}` | Admin (not a PIN session) | 200, 201, 204 | 400, 401, 403 (`CredentialNotAllowed` for a PIN session), 404, 409, 429, 503 |
| `GET, POST /api/v4.0/Identity/ApiKeys`, `PUT, DELETE /api/v4.0/Identity/ApiKeys/{name}`, `POST .../ApiKeys/{name}/Rotate` | Admin (not a PIN session) | 200, 201, 204 | 400, 401, 403 (`CredentialNotAllowed`), 404, 409, 503 |
| `GET  /api/v4.0/Identity/Sessions`, `DELETE /api/v4.0/Identity/Sessions/{id}` | Admin (not a PIN session) | 200, 204 | 401, 403 (`CredentialNotAllowed`), 404, 503 |
| `GET  /openapi/v4.json`                                  | Admin (API key or session) outside Development | 200 | 401, 403 |
| `GET  /health`                                           | Viewer (API key, session or cookie) | 200, or 503 (with the JSON body) when Unhealthy | 401 |
| `GET  /health/live`, `GET /health/ready`                 | anonymous         | 200 / 503 | none |
| `/hubs/roof` (SignalR status hub, with `/hubs/roof/negotiate`) | Viewer (API key or session, never the cookie) | status messages ([Status hub](#status-hub)) | 401, 403 (`https_required`) |
| `POST /account/login`, `POST /account/logout`            | anonymous (form)  | 302 | 403 (`origin_not_allowed`) |
| `POST /console/stop`                                     | Stop (console cookie and antiforgery token only) | 200 `{outcome, message}` | 400 (stale form token), 401, 403 (`origin_not_allowed`), 503 (stop not verified), 500 |

When a request has no key or an unknown key, the response is 401 with `WWW-Authenticate: ApiKey`. When a session
token is unknown, ended or expired, the response is 401 with `WWW-Authenticate: Bearer error="invalid_token"`. When a
valid key or session lacks the required role, the response is 403.

### Error bodies

Roof command failures are RFC 7807 ProblemDetails with two extensions:

- `code` is the `RoofControllerErrorCode` name.
- `roofStatus` is the current `RoofStatusResponse`, when one is available.

`type` is `urn:hvo:roof-controller:<code>`.

| Status | Codes | Client action |
|--------|-------|---------------|
| 409 | `FaultLatched`, `InterlockActive`, `OperationInProgress`, `LeaseNotActive`, `ConfigurationVersionConflict`, `ConfigurationRejected` | Refused by an interlock or state. Show the reason; do not retry blindly. |
| 503 | `NotInitialized`, `ShuttingDown`, `HardwareUnavailable`, `RelayStateUnverified` | Not ready. `Retry-After: 2` is set. |
| 401 | `SignInFailed` | Wrong name, password or PIN. Every wrong answer looks the same. |
| 429 | `SignInLockedOut`, `SignInBusy` | Wait for `Retry-After` (seconds) before trying again. |
| 403 | `KioskKeyRequired` | PIN sign-in needs a kiosk key. |
| 403 | `CredentialNotAllowed` | A PIN session, even an admin's, cannot manage people, keys or sessions. Use an admin API key, or sign in with a password. |
| 404, 409 | `IdentityNotFound`; `IdentityNameConflict`, `IdentityReadOnly`, `LastAdministrator` | Refused change to people, keys or sessions. Show the reason. |
| 503 | `IdentityStoreUnavailable` | The identity store could not be read or saved. Configured keys and Stop still work. |
| 403 | `SettingNotPermitted` | The caller may not change that setting: it needs a higher role or a [local credential](#local-only-settings), or a layer above the settings file sets it. |
| 404 | `SettingNotFound` | There is no such settings group. |
| 409 | `SettingsHandEditPending` | The settings file was edited outside the API. An admin must reload or discard the edit first ([Hand edits](#hand-edits)). |
| 503 | `SettingsStoreUnavailable` | The settings could not be saved, and nothing was changed; or the store is not available. |
| 409 | `RestartRefused` | The roof stop could not be verified, or a pending hand edit cannot be loaded as it is. The controller did not restart. |
| 400 | `InvalidRequest`, and model validation | Fix the request. |
| 500 | `Unknown` | Unexpected. The detail text is generic; see the controller log. |

`RoofControl/Configuration` is the roof group of the [settings API](#settings) under its old route. Its `version` is
the settings version, an accepted change is saved to the settings file, and it never changes the
[local-only settings](#local-only-settings): it reports them, but its request has no fields for them.

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

Every applied change is written to the log as an `AUDIT` entry. The entry holds the caller's key or person name, the
old and new versions, and the changed fields.

## Provisioning API keys

Keys are secrets. Never put them in `appsettings*.json`, `launchSettings.json`, compose files or the `.http` samples.
Each entry has these settings:

| Setting | Meaning |
|---------|---------|
| `RoofControllerSecurity:ApiKeys:N:Name` | Identifies the holder in logs and audit entries (for example `console-operator`). Not secret. |
| `RoofControllerSecurity:ApiKeys:N:Role` | `RoofViewer`, `RoofOperator` or `RoofAdmin`. |
| `RoofControllerSecurity:ApiKeys:N:Key` | The key, at least 24 characters. |
| `RoofControllerSecurity:ApiKeys:N:KeySha256` | Alternative to `Key`: 64 hex characters of SHA-256 over the key's UTF-8 bytes. Use it so the controller never stores the key itself. |
| `RoofControllerSecurity:ApiKeys:N:Kiosk` | Optional, default `false`. `true` marks a kiosk's key, at which people sign in with a PIN. A kiosk key must have the `RoofViewer` role. |
| `RoofControllerSecurity:ApiKeys:N:Local` | Optional, default `false`. `true` marks a key kept only on the controller or the kiosk beside it. It makes the key a [local credential](#local-only-settings): a local admin key, or an admin's PIN session at a local kiosk, may change the local-only settings. |

Set exactly one of `Key` or `KeySha256`. An entry that is incomplete, too short, has an unknown role, sets both, or is
a kiosk key without the `RoofViewer` role is skipped. The startup log reports it by index and name, never by value. If no usable key remains, the controller logs a
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

The console login accepts any of these keys. Keys in the configuration are read-only through the API. An admin can
also add keys through the API ([managed API keys](#managed-api-keys)); they need no restart and no file on the Pi.

### Rotation

Keys reload when the configuration changes. After replacing a key file, restart the container to be certain. A console
session that was signed in with a removed, rotated or re-roled key ends at the next request or revalidation. Managed
keys are rotated through the API, and the old value stops working at once.

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

## People, sessions and managed API keys

API keys suit devices and scripts. People sign in with their own name and a password (the web UI, the CLI), or a
name and PIN at a kiosk, and get a session token. The controller keeps people, sessions and the keys added through the
API in its identity store.

### Signing in

- **Password.** `POST /api/v4.0/Auth/Session` with `{"name": ..., "password": ...}` needs no key. The answer is a
  `RoofSessionResponse`. Its `token` is shown only in this answer; send it as `Authorization: Bearer <token>`. A
  password session lasts `SessionLifetime` (default 12 hours).
- **PIN at a kiosk.** `POST /api/v4.0/Auth/Pin` with `{"name": ..., "pin": ...}`, sent with the kiosk's own key in
  `X-Api-Key`. Only a kiosk key may do it; any other key gets 403 `KioskKeyRequired`. Only operators and admins may
  have a PIN. A PIN session ends after `PinSessionIdleTimeout` (default 10 minutes) without a request, and after
  `PinSessionLifetime` (default 12 hours) in any case. It also ends when its kiosk key is removed or is no longer a
  kiosk key. `GET /api/v4.0/Auth/Pin/Users` lists the people who can sign in with a PIN, for the kiosk's picker.
  Between sessions a kiosk shows status and offers Stop with its own key, which has the `RoofViewer` role.
- **Who am I.** `GET /api/v4.0/Auth/Me` returns the caller's name, role and kind (`ApiKey`, `Session` or `Pin`).
- **Sign out.** `DELETE /api/v4.0/Auth/Session` ends the session that sent it.
- **Own password.** `POST /api/v4.0/Auth/Password` with `currentPassword` and `newPassword` changes a signed-in
  person's password. Their other sessions end; this one stays open.

Passwords are 12 to 256 characters. PINs are 6 to 12 digits. Names are 1 to 64 letters, digits, `.`, `_`, `@` or `-`,
starting with a letter or digit. Passwords and PINs are stored as PBKDF2 hashes. Session tokens and managed keys are
stored as SHA-256 hashes.

A wrong name, password or PIN always gets the same 401 `SignInFailed`, and an unknown name takes as long as a wrong
password. After `LockoutThreshold` failures in a row (default 5), sign-in is refused with 429 `SignInLockedOut` and
`Retry-After`:

- for that name, after wrong passwords;
- for that name at every kiosk, after wrong PINs for it;
- at that kiosk, after wrong PINs, whichever names were tried.

A PIN failure counts both for the name and for the kiosk. A password success clears that name's password count; a PIN
success clears that name's PIN count and the kiosk's count. Neither clears another person's, so signing in with your own
PIN between guesses does not reset the guesses at someone else's. Anyone at a kiosk can therefore lock a person's PIN at
every kiosk by guessing it, for up to `MaximumLockoutDuration`; that person can still sign in with their password.

The first lockout lasts `LockoutDuration` (default 5 minutes). Each further one doubles, up to
`MaximumLockoutDuration` (default 4 hours). A success, or `FailureMemory` (default 24 hours) without a failure, starts
again from nothing.

Each attempt is counted before its secret is checked, so attempts sent in parallel get no more guesses than the
threshold. Once the failures plus the attempts still being checked reach it, further attempts for that name or kiosk get
429 `SignInBusy` with `Retry-After: 2` until those end. The lockout always remembers the people in the identity store
and the kiosks. A name that is not a person is counted and locked out the same way, so a refusal does not tell the two
apart, but only the 16,384 such names tried most recently are remembered. A flood of made-up names can only push out
other made-up names: it never wipes a person's or a kiosk's count and never refuses them. (During such a flood a made-up
name's count can be forgotten, which could show that it is not a person. Each address has its own rate limit, below,
so a caller with many IPv6 networks can fill those slots within seconds, where one IPv4 address needs hours.)

Each caller may try `SignInAttemptsPerMinute` sign-ins (default 30; 0 turns the limit off) across `Auth/Session`,
`Auth/Pin` and `Auth/Password`. A kiosk counts by its key, a signed-in person changing their password by their name,
and an anonymous `Auth/Session` by its remote address, whatever cookie comes with it. A public IPv6 address from
another network counts by its /64 network, since one host there can pick a new address for every attempt; a loopback,
link-local or unique local IPv6 address, or one in the controller's own /64, counts by itself, since every host on the
LAN shares that /64. The limit is applied after the key or session is checked, so a request without a valid one is refused (401) without counting. Beyond the limit the
caller gets 429 `SignInBusy` with `Retry-After` before any secret is checked, and a `SECURITY` warning is logged at most
every 30 seconds. A client that signs people in for them, such as a web UI, is one address for all of them, so one
visitor there could use up the limit for the others; the web UI will limit sign-ins per visitor itself.

Hashing is limited to a few at a time, so sign-in cannot starve the roof. A sign-in that cannot get a turn also gets
429 `SignInBusy`. The settings are under `RoofControllerSecurity:Identity`, and the controller refuses to start with
invalid values.

A client that uses its own API key on a person's behalf (for example the web UI sending Stop) may add
`X-On-Behalf-Of: <name>`. The audit log records it as `<key> for <name>`; it grants nothing.

### Managing people, keys and sessions

Admins manage everything under `/api/v4.0/Identity`:

| Route | What it does |
|-------|--------------|
| `GET/POST Identity/Users`, `GET/PUT/DELETE Identity/Users/{name}` | People. A person needs a password, a PIN or both. Secrets are never returned, only whether each is set. Changing a person's role or password ends all their sessions; changing or removing the PIN ends their PIN sessions; removing them ends every session. |
| `GET/POST Identity/ApiKeys`, `PUT/DELETE Identity/ApiKeys/{name}`, `POST Identity/ApiKeys/{name}/Rotate` | API keys. The list includes the configured keys, marked `Configuration` and read-only (409 `IdentityReadOnly`). The controller generates a managed key's value and returns it once, on create and on rotate. A kiosk key must have the `RoofViewer` role. |
| `GET Identity/Sessions`, `DELETE Identity/Sessions/{id}` | Open sessions (never their tokens), and ending one. |

A PIN session cannot use these routes, even an admin's: it gets 403 `CredentialNotAllowed`. A PIN is short and typed
where others can see it, so it must not be able to create people or keys that work from anywhere and outlive the
session. Use an admin API key, or sign in with a password.

Everything else an admin may do, an admin's PIN session may do too, by design: run the roof, clear faults, read and
change the settings (including ignoring the limit switches where local configuration allows it, and turning off the
operator lease or the IN4 interlock, each with `ConfirmSafetyCriticalChange`; the [local-only settings](#local-only-settings)
only at a kiosk marked `Local`), restart the controller, and read the `System` routes and the OpenAPI document. Only a
kiosk's key can open a PIN session, but the bearer token it returns is not tied to the kiosk: whoever holds it can use
it from anywhere until it idles out (`PinSessionIdleTimeout`, default 10 minutes) or is ended. A kiosk must keep it as safe as its own key.

A change that would leave no admin credential is refused with 409 `LastAdministrator`. An admin credential is an admin
API key, or an admin with a password. Every change, sign-in and sign-out is logged as an `AUDIT` entry, and each failed
or locked-out sign-in as a `SECURITY` warning, with the name and remote address and never a secret. A request with a removed, rotated or re-roled key, or an ended
session, is refused at once. A status hub connection that used it closes within a second, and a console session ends at
its next revalidation.

### The identity store

`RoofControllerSecurity:Identity:StorePath` names the store's file. It holds hashes only, but a PIN hash can be
attacked offline, so keep it in its own directory, readable only by the controller and never next to shareable
settings. On the Pi the deploy script and both Pi compose profiles mount `/var/lib/hvo-roof/identity` read-write
and set the path to `/var/lib/hvo-roof/identity/identity.json` (see [deployment.md](deployment.md#3-the-identity-store)).
Back the directory up with the secrets directory.

- Each change is written to a new file (mode 0600), flushed, and renamed over the old one, and then the directory is
  flushed. A crash or power cut leaves the old file or the new one, never a torn one, and a change the controller has
  confirmed stays saved. The change is used only after it is saved.
- Sessions are kept in the file, so they survive a restart or a redeploy. A PIN session's idle time starts again when
  the controller starts.
- Without a `StorePath`, the store is kept in memory: people, sessions and managed keys are lost when the controller
  restarts. `/health` reports `identity_store` as Degraded, and the deployment check warns outside Development.
- When the file cannot be used (corrupt, an entry repeated, no permission, or a `StorePath` that does not name a file),
  the controller still starts. Sign-in and identity
  management return 503 `IdentityStoreUnavailable`, and `/health` reports `identity_store` as Unhealthy. Configured
  keys, Stop and the roof keep working. `identity_store` is not a hardware check, so readiness and deploys do not fail
  on it; the deployment check does (below).
- The deployment check (`--validate-deployment`) reads the file and saves a probe file next to it. It fails when the
  directory is missing, not writable, or the file is not valid, and when the identity settings are invalid.

## Settings

The clients configure the controller remotely through `/api/v4.0/Settings`. Every setting is described once, in the
settings catalogue, and the CLI, the web UI and the kiosk build their forms from it.

### The catalogue and the groups

`GET Settings/Catalogue` lists each setting the caller may read: its full configuration key, group, type, range, unit
and default, the role that may change it (`writeRole`), and whether a change is safety-critical (`safety`), takes effect
only after a restart (`appliesAfterRestart`), needs a local credential (`localOnly`) or is a secret (`secret`).

`GET Settings` returns the settings the caller may read, with:

- the settings `version`, which every change must send back;
- each value in effect and where it comes from (`source`: the settings file, the managed secrets file, the shipped
  defaults, the environment, the secrets directory, the command line or the code default);
- whether the caller may change it now (`canWrite`, and `readOnlyReason` when not);
- `restartPending` for a saved value that takes effect only after a restart;
- for admins only: the settings file's path, a [pending hand edit](#hand-edits), and warnings.

| Group | Changed by | Settings |
|-------|------------|----------|
| `roof` | Admin | `RoofControllerOptionsV4`: the relay ids, polling, the limit-switch type and debounce, the limit-switch bypass and its consent, fault polarity, input read failures, the watchdog, the operator lease, the IN4 interlock and its stop confirmation, and the departure check |
| `controller` | Admin | `RoofControllerHostOptionsV4`: `RestartOnFailureWaitTime` and `ControllerName` (both after a restart) |
| `camera` | Admin | `BlueIris`: the server address, the user and password (secrets), the timeouts and the stream limit (`MaxConcurrentStreams` and `ConnectTimeout` after a restart) |
| `security` | Admin | `RoofControllerSecurity`: `AllowAnonymousStop`, `RequireHttps` and `AllowedOrigins` |
| `identity` | Admin | `RoofControllerSecurity:Identity`: the session lifetimes, lockouts and the sign-in rate limit |
| `logging` | Admin | The log levels of the controller, the web server and the console's log view |
| `ui` | Operator | `RoofControllerUi`: `DefaultCamera` and `KioskScreenTimeout` |

Admin covers anything that changes how the roof moves, what is trusted, or who may act. The operator settings are
limited to presentation. Only admins can read the admin groups; everyone can read `ui`. API keys and people are not
settings: admins manage them under [`Identity`](#managing-people-keys-and-sessions).

### Changing settings

`POST Settings/{group}` replaces one group's settings:

```json
{
  "expectedVersion": 7,
  "confirmSafetyCriticalChange": false,
  "values": {
    "RoofControllerUi:DefaultCamera": "roof",
    "RoofControllerUi:KioskScreenTimeout": 600
  }
}
```

- `expectedVersion` is required and must equal the `version` from the last `GET Settings`; otherwise the change fails
  with 409 `ConfigurationVersionConflict`. One version covers every group.
- Send every setting of the group that you may change, by its full key, or the change fails with 400. You may leave out
  settings you may not change, or send them unchanged. You may leave out a secret, which keeps it.
- Values are sent in the setting's type: `true` or `false`, numbers, strings, arrays of strings, and durations as a
  number of seconds. A nullable setting takes `null`, which turns it off or leaves it unset.
- A safety-critical change needs `"confirmSafetyCriticalChange": true`, or it fails with 409 `ConfigurationRejected`.
  The catalogue's `safety` says when: `Always` for the relay ids, the limit-switch type, the limit-switch bypass and its
  consent, fault polarity, `AllowAnonymousStop` and `RequireHttps`; `WhenTurnedOff` for the operator lease, the IN4
  interlock and its stop confirmation, and the departure check, which need it only to be set to `null`.
- The group is checked as a whole, with the rules the controller checks at startup, before anything is saved. Every
  problem is returned with 400. `RequireHttps` cannot be turned on while there is no HTTPS listener (409
  `ConfigurationRejected`).
- Roof settings do not change while the roof moves or a clear-fault pulse runs: 409 `OperationInProgress`. Stop first.
- A setting that a layer above the settings file sets (the environment, the command line or the secrets directory)
  cannot be changed through the API: 403 `SettingNotPermitted`, and `source` names the layer. Change or remove it
  there. On the Pi, the deploy script and both compose profiles set `RoofControllerSecurity:RequireHttps` and
  `RoofControllerOptionsV4:IgnorePhysicalLimitSwitches` in the environment, so those two are read-only there: change
  them by redeploying (see [deployment.md](deployment.md#4-the-settings-directories)).
- The change is applied and saved together. When the files cannot be saved, the roof settings are put back and the
  change fails with 503 `SettingsStoreUnavailable`: nothing changed.

Most settings take effect at once. The others show `restartPending` until the controller [restarts](#restart).

Every change is written to the log as an `AUDIT` entry with the caller's key or person name, the old and new versions,
and each setting's old and new value. A safety-critical change is logged at Warning, any other at Information. A
secret is shown only as `(not set)` or `(set)`. So that no change can hide the entries, `Logging:LogLevel:Default`
takes only `Trace`, `Debug` or `Information`, through the API and in the settings file.

### Local-only settings

`AllowIgnoringLimitSwitchesOnPhysicalHardware`, `DriveStopConfirmationTimeout` and `DepartureReleaseTimeout` are
local-only, so that no one can, for example, allow ignoring the limit switches on the real roof from across the network.
Only a local credential may change them:

- an admin API key configured with `Local: true` and kept only on the controller, for the CLI in its shell; or
- an admin's PIN session at a kiosk whose configured key has both `Kiosk: true` and `Local: true`.

A password session and a key added through the API are never local. The check is on the credential, never on the
caller's address: behind Docker's port publishing, and with the web UI in the same container, an address does not show
where a person is. Everyone else gets 403 `SettingNotPermitted` for these settings. `RoofControl/Configuration` never
changes them.

### Secrets

The Blue Iris user and password are write-only: `GET Settings` returns only whether each is set. A value set through the
API is kept in the managed secrets file (`RoofControllerSettings:SecretsFilePath`, mode 0600, in a directory of its own
with mode 0700), never in the settings file. It holds only these settings. The secrets directory, `/run/secrets`, stays
read-only; a secret provisioned there takes precedence, and the API cannot change it.

Sending a secret always counts as a change, even with the value it already has, so neither the answer nor the version
shows whether a guess was right. The controller refuses to start with a settings file that holds a secret: any key whose
last part is `Key` or `Password`, or a secret setting.

The credentials stay with the server they were set for. Pointing `BlueIris:BaseUrl` at another server (another scheme,
host or port) while a Blue Iris user or password is set gets 409 `ConfigurationRejected`, unless the same request sends
both `BlueIris:UserName` and `BlueIris:Password` (null clears them). So a stolen admin credential cannot redirect the
proxy to collect them. Credentials provisioned in the secrets directory cannot be sent through the API; with those,
change the server where they are set. Turning the proxy off (null) is always allowed.

### The settings file

`RoofControllerSettings:FilePath` names the settings file; on the Pi it is `/etc/hvo-roof/config/appsettings.Local.json`
(see [commissioning.md](commissioning.md#the-settings-file)). It uses the section and key names of `appsettings.json`.
It holds only settings in the catalogue, because only those pass the checks of a change through the API. Other
configuration, such as Kestrel endpoints or API keys, belongs in the deployment's environment or the secrets directory.

- The configuration layers, lowest first: `appsettings.json`, `appsettings.{Environment}.json`, the settings file, the
  managed secrets file, user secrets (Development only), environment variables, the command line and the secrets
  directory. A higher layer overrides a lower one.
- A list the file sets, such as `AllowedOrigins`, replaces the list below it, and `[]` clears it. (The layers above the
  file still merge by index, as .NET configuration does.)
- The file holds only the settings that differ from the layers below it, so a default that a later release changes
  still takes effect.
- Its first property, `HvoRoofSettings`, holds the version and when and by whom it was last saved, so `expectedVersion`
  keeps working across restarts.
- Each save writes a new file, flushes it, renames it over the old one and flushes the directory. A crash or power cut
  leaves the old file or the new one, never a torn one. The file is mode 0644: it holds no secrets.
- The controller reads comments and trailing commas in the file, but a save through the API rewrites it without them.
- A file that is not valid JSON, sets a secret or a key outside the catalogue, or holds a value the controller cannot
  use stops the start. The error names the key but never its value. The controller writes `The roof controller did not start: ...` to standard error and exits with code 1. It never falls
  back to the defaults.
- Without a `FilePath` (or a `SecretsFilePath`), changes are kept in memory and lost at a restart. `GET Settings`
  reports `fileBacked: false` with a warning, and the deployment check warns outside Development.

### Hand edits

The file can be edited by hand while the controller runs. The controller compares the files on disk with the ones it
loaded at every settings request. An edit it finds is **not** in effect:

- `GET Settings` shows it to admins as `pendingHandEdit`: each changed setting, from and to (never a secret's value),
  values that cannot be used, and whether reloading it needs confirmation or a local credential. An edit that sets a key
  outside the catalogue shows as a file that cannot be used (`fileProblem`). An edit that turns `RequireHttps` on
  without an HTTPS listener is listed with the values that cannot be used, because a restart would load it.
- Every change through the API fails with 409 `SettingsHandEditPending`, so neither change silently overwrites the
  other. `GET Settings` reports every setting as read-only until then.
- `POST Settings/Reload` with the edit's `token` applies it, with the rules of a change through the API: 400 for values
  that cannot be used, 409 `ConfigurationRejected` for a file that cannot be read, or for a safety-critical change
  without `confirmSafetyCriticalChange: true`, 403 `SettingNotPermitted` for a local-only setting without a local
  credential, and 409 `OperationInProgress` for a roof setting while the roof moves. The reload saves the file again,
  without its comments, and is audited.
- `POST Settings/Discard` with the token overwrites the edit with the settings in effect. It also recovers from a file
  that can no longer be read. It is audited.
- When the files change again after the edit was read, the token no longer matches: 409
  `ConfigurationVersionConflict`. Read the settings again and review the edit.

A restart also loads a pending edit, so it is refused while the edit could not be reloaded (below).

### Restart

`POST System/Restart` (Admin) restarts the controller so that settings marked `appliesAfterRestart` take effect. It:

1. refuses with 409 `RestartRefused` when a pending hand edit could not be loaded as it is: the file cannot be read, a
   value cannot be used, a safety-critical change is not confirmed with `confirmSafetyCriticalChange: true` in the
   optional body, or a local-only setting changes and the caller has no local credential;
2. stops the roof and verifies the stop, and refuses with 409 `RestartRefused` when it cannot be verified;
3. writes an `AUDIT` entry, answers 202, then shuts down as on SIGTERM (running the verified stop again) and exits with
   code 75.

The container's restart policy, `unless-stopped`, starts it again. A controller that no supervisor restarts stays
stopped.

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

- **Authentication.** Every role may connect, with the `X-Api-Key` header or a session's `Authorization: Bearer`
  header on the negotiate request and on the WebSocket or long-polling requests that follow. Neither is accepted in
  the query string, so browser JavaScript, which cannot set headers on a WebSocket, cannot connect directly; a browser
  UI gets status through its server. The console cookie is not accepted, so a page on another site cannot open a
  connection with a signed-in browser's cookie. Without a valid key or session the negotiate request returns 401;
  plain HTTP from the network returns 403 `https_required` when `RequireHttps` is on, as the API does.
- **Revocation.** A connection authenticates once, when it opens. Once a second, on its own schedule and whether or
  not the status is changing, the controller checks each connection's key or session again. It closes the connection
  when the key was removed, rotated or given another role, or the session ended or its person's role changed, and
  logs the key's or person's name (never a secret).
- **Limits.** At most 32 connections are open at once, and at most 8 with one key or session. Give each client (the
  kiosk, the web UI, each CLI user) its own key or session: clients that share one share its 8 connections, and the
  per-key limit only stops one client that leaks connections from taking another's when their keys differ. A
  connection past a limit is closed with the reason ("The controller is not accepting more status connections." or
  "... for this key or session.") and
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

The first three are the `security` [settings](#settings) group, so admins can change them through the API, each with
`ConfirmSafetyCriticalChange` except `AllowedOrigins`. On the Pi, `RequireHttps` is the exception: the deploy script
(from `ALLOW_INSECURE_HTTP`) and both compose profiles set it in the environment, so the API shows it as read-only and a
redeploy changes it. `AllowedHosts` is not in the catalogue.

HSTS is sent only when an HTTPS endpoint is configured.

Because `/health/ready` is exempt, readiness passes even when every remote API request would get 403. The deployment
check (`--validate-deployment`, run first by the deploy script and by both Pi compose profiles, `pi` and
`pi-lan-http`; the test-rig `emulator` profile does not run it) therefore fails when
`RequireHttps` is in effect without an HTTPS listener, or with a certificate that cannot be loaded. See
[deployment.md](deployment.md#the-deployment-check).

Unhandled exceptions return a generic 500 ProblemDetails with a trace id. Exception messages and stack traces are
never returned to clients outside Development.

## Logging

Keys, session tokens, passwords, PINs, cookies and Blue Iris credentials are never logged. Outgoing Blue Iris request
headers are redacted in HTTP client logs. Each command logs the caller's key or person name and remote address.
Configuration and settings changes, restarts, sign-ins, sign-outs and identity changes log an `AUDIT` entry. Failed and locked-out sign-ins
log a `SECURITY` warning. A refused session token is logged at Warning with the remote address.
