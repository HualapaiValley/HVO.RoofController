# HVO.RoofControllerV4.Client

The client library for the roof controller's REST API and status hub. The web UI, the command-line client, the kiosk
and the Mac app use it, so they send the same requests, handle errors the same way and word things the same way. It
targets `net10.0` and has no UI dependencies.

Clients reach the controller only through its REST API and the status hub (`/hubs/roof`). Nothing here calls the
controller in process.

## Connecting

```csharp
using var client = new RoofControllerClient(new RoofConnectionOptions
{
    BaseAddress = new Uri("https://roof.local:5001/"),
    Credential = new RoofApiKeyCredential(apiKey),
    // Only for a controller with a self-signed certificate: its SHA-256, 64 hex digits.
    ServerCertificateSha256 = pinnedHash
});

var status = await client.Roof.GetStatusAsync();
```

The pin covers requests, Stop and the status hub's WebSocket. Any other certificate must be trusted as usual.
`RoofCertificatePinTests` checks all three over real HTTPS, with the right pin, another certificate's pin, and none.

`RoofControllerClient` groups the endpoints:

| Property | Endpoints |
|----------|-----------|
| `Roof` | `RoofControl`: status, open, close, lease, clear fault, configuration |
| `Auth` | `Auth`: sign-in with a password or a PIN, the caller, refresh, sign-out, password change, PIN users |
| `Identity` | `Identity`: people, API keys and sessions (admin) |
| `Settings` | `Settings`: the catalogue, the values, a group change, reloading or discarding a hand edit |
| `System` | `System`: restart, information and metrics |
| `Health` | `/health`, `/health/ready`, `/health/live` |
| `Camera` | the camera stream |

`RoofClientCoverageTests` checks that every endpoint the controller maps has a call here, apart from the web console's
own pages and the API description.

## Errors

A refusal throws `RoofApiException`. It carries the HTTP status, the controller's `code` (`RoofControllerErrorCode`),
the roof status the controller attached, `Retry-After` and any field errors, and its `Message` is the shared wording
from `RoofText`. A request that cannot reach the controller, or times out, keeps its `HttpRequestException` or
`TimeoutException`; `RoofText.DescribeFailure` words it without its internals. An answer that cannot be read throws
`RoofProtocolException`.

## Stop

```csharp
var result = await client.StopAsync();
// result.Outcome: Acknowledged, RelayUnverified or Failed. result.Message: the text to show.
```

Stop goes straight to REST on a connection of its own. It never waits behind another command, a camera stream or the
status hub, and nothing is queued: two Stops are two requests. It never needs a PIN: a kiosk sends its device key beside
the PIN session, and the controller accepts the device key for Stop when the PIN session has ended. A person whose
session has ended is told to sign in again or use the stop control at the roof. A key the controller does not accept (an
API key, or a kiosk's device key) is reported as refused, since there is nothing to sign in to. The result claims only
what the controller said: "relay register verified de-energized" only when the controller verified it.

Every client uses the wording in `RoofStopText`: the button label `Stop roof` and the result messages.
`RoofClientWordingTests` pins them, and checks that the web console uses the same text. The console's reconnect dialog
has no wording of its own: the server renders its texts into the page (`RoofConsoleStopTexts`), including those for a
proxy's error page, the HTTPS and origin checks, and no answer.

## Status feed

```csharp
await using var feed = client.CreateStatusFeed();
feed.StatusReceived += (_, e) => Show(e.Message.Status);
feed.StateChanged += (_, _) => ShowStale(feed.IsStale, feed.StaleSince);
feed.Start();
```

The feed connects to the status hub, reconnects with a doubling delay (1 s to 30 s, ±20%), and keeps the newest
snapshot in the hub's `sequence` order. A new `instanceId` means the controller restarted; the first snapshot from it
is taken whatever its sequence. The feed is stale from the moment the connection drops, or after 3 s without a message
(three heartbeats); `StaleSince` says since when. A client shows a stale snapshot as the last known state, never as the
current one. A handler that throws is logged, and the feed and the other handlers carry on. The feed only reads:
commands go over REST.

## Credentials

| Credential | Sends | Use |
|------------|-------|-----|
| `RoofApiKeyCredential` | `X-Api-Key`, and `X-On-Behalf-Of` when it names a person | scripts, the CLI, the web UI's service key |
| `RoofSessionCredential` | `Authorization: Bearer` | a person signed in with a password (`Auth.SignInAsync`) |
| `RoofKioskCredential` | the device key, plus the PIN session while unlocked | the kiosk (`Auth.SignInWithPinAsync`) |

`X-On-Behalf-Of` must be a user name, as the controller defines one. Any other name is refused when the credential is
made, so it cannot carry another header. A key, token or PIN session must be printable ASCII (no line break or control
character), and one that is not is refused when the credential is made, so no request, Stop included, can fail on a
header it cannot send. A credentials file that holds one is reported as a `RoofCredentialFileException`. A session that
the controller refuses is marked ended and raises `Ended`; a wrong password at sign-in does not end the session in use.
When a kiosk's PIN session ends, the kiosk locks, and the device key still reads status and sends Stop.

### Command-line credentials

`RoofCredentialStore` keeps a command-line client's controller address, API key or session, and certificate pin:

- In the environment: `HVO_ROOF_URL`, `HVO_ROOF_API_KEY` (with `HVO_ROOF_ON_BEHALF_OF`), `HVO_ROOF_SESSION` and
  `HVO_ROOF_CERT_SHA256`.
- In a file: `$XDG_CONFIG_HOME/hvo-roof/credentials.json`, or `~/.config/hvo-roof/credentials.json`. The file is
  written atomically with mode `0600` in a `0700` directory. A file that other users can read or change, or one in a
  directory they can change, is refused, with the `chmod` command that fixes it.

The saved records, and the sign-in, password and user requests, leave keys, tokens, passwords and PINs out of their
`ToString()`, so they can be logged.

## Settings forms

`RoofSettingsForm.Create(catalogue, settings)` turns `GET Settings/Catalogue` and `GET Settings` into groups of fields
with labels, the value to show, the default, and whether the caller may change each one (and if not, why). A secret is
shown only as `(set)` or `(not set)`. `form.Edit(group)` checks typed text against the setting (`5m` or `300` for a
duration, `yes` or `no` for a switch), and says whether the change is safety-critical, needs a local credential or
applies after a restart. A secret left empty keeps its value; `ClearSecret(key)` removes it. `ToRequest(confirm)` builds
the `POST Settings/{group}` body with the version the form was read at.

## Shared text

`RoofText` describes the roof's position, stop reasons and every error code, so every client shows the same words.
`RoofStatusRules` holds the rules the web console used before this library: which of two snapshots is newer, the safety
alerts a change raises, the motion the controller has commanded, and when to renew the operator lease.

## Colours

`RoofUiPalette` holds HVO Dark, the web console's theme (`hvo-dark.css` in `HVO.WebSite.Themes`), as `#rrggbb` values
for the clients that are not web pages: the terminal interface, the kiosk and the Mac app. Each value names the
stylesheet's token. Stop, Open and Close have the colours of the web console's buttons, so they look the same in every
client. `RoofUiPaletteTests` checks each value against the stylesheet.
