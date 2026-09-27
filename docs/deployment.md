# Deploying Roof Controller V4 to the Raspberry Pi

This page covers:

- preparing the Pi (secrets, TLS certificate)
- deploying with `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh` or `docker-compose.yaml`
- the deployment check (`--validate-deployment`) both of them run first
- how the controller is stopped safely during a deploy, verified afterwards and rolled back

For API keys, roles and the Blue Iris credentials, see [security.md](security.md).

## One-time preparation on the Pi

### 1. Secrets directory

Both the deploy script and compose bind-mount `/etc/hvo-roof/secrets` read-only at `/run/secrets`. The app reads one
file per setting from there. The file name is the setting name with `__` in place of `:`.

```
/etc/hvo-roof/secrets/                                      (mode 700)
  RoofControllerSecurity__ApiKeys__0__Name                  e.g. console-operator
  RoofControllerSecurity__ApiKeys__0__Role                  RoofOperator
  RoofControllerSecurity__ApiKeys__0__Key                   <random, >= 24 chars>
  RoofControllerSecurity__ApiKeys__1__...                   more keys (viewer, admin, deploy)
  BlueIris__UserName                                        dedicated view-only Blue Iris user
  BlueIris__Password
  Kestrel__Certificates__Default__Password                  PFX password (when using HTTPS)
```

Write each value with `printf '%s'` so that no trailing newline becomes part of it. Make the files mode `600`. See
[security.md](security.md#provisioning-api-keys) for commands. The directory must exist and must hold at least one
`RoofOperator` or `RoofAdmin` key: the [deployment check](#the-deployment-check) refuses to deploy otherwise.

> The Blue Iris credential that used to be hard-coded in `CameraController.cs` is still in the git history. Rotate it
> before provisioning `BlueIris__UserName`/`BlueIris__Password` (see security.md).

### 2. TLS certificate

Outside Development, the controller refuses plain-HTTP requests from other machines with 403 `https_required`. It
does not redirect them. Two exceptions remain on plain HTTP:

- loopback clients, which covers the Docker healthcheck and the deploy script's in-container calls
- the anonymous `/health/live` and `/health/ready` probes

Give Kestrel a PFX certificate whose subject alternative names include every name and IP clients use (for example
`roof-pi`, `roof-pi.local`, `192.168.x.y`).

A private CA is best: clients trust the CA once, and certificates can then be reissued freely. For a quick
self-signed certificate:

```bash
sudo install -d -m 700 /etc/hvo-roof/https
openssl req -x509 -newkey rsa:3072 -sha256 -days 825 -nodes \
  -keyout roof.key -out roof.crt -subj "/CN=roof-pi" \
  -addext "subjectAltName=DNS:roof-pi,DNS:roof-pi.local,IP:192.168.1.50"
PFX_PASSWORD=$(openssl rand -base64 24 | tr -d '\n')
openssl pkcs12 -export -inkey roof.key -in roof.crt -out roof-controller.pfx -passout "pass:${PFX_PASSWORD}"
sudo mv roof-controller.pfx /etc/hvo-roof/https/
printf '%s' "$PFX_PASSWORD" | sudo tee /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password >/dev/null
shred -u roof.key
```

Install `roof.crt`, or your CA certificate, as trusted on browsers that use the console. Keep a copy on the machine
that runs the deploy script too: pass it as `REMOTE_CA_CERT` so the script can check the HTTPS endpoint after a deploy
(not needed when that machine already trusts the CA).

The deployment check loads the certificate as Kestrel does, with its password, before anything is replaced. It fails
when the file is missing or unreadable, the password is wrong, the private key is missing, the certificate is not for
server authentication or has expired, and it warns from 30 days before expiry. A PEM or DER certificate needs
`KeyPath` for its PEM key, and a PFX must not have one. With `KeyPath`, a `Password` that is set, even to an empty
string, means the key is encrypted.

Kestrel takes its listeners from one place only: `Kestrel:Endpoints` when any endpoint is configured, otherwise
`ASPNETCORE_URLS`, otherwise `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_HTTPS_PORTS`. So `ASPNETCORE_HTTPS_PORTS` does
nothing while `ASPNETCORE_URLS` is set, as it is in the image. `ASPNETCORE_URLS` works only as an environment variable
or the `--urls` option; a secret file of that name has no effect (name it `urls`). The deployment check resolves the
listeners the same way and prints them, with any settings that are ignored.

HSTS is sent only when an HTTPS endpoint is configured.

To stay on plain HTTP on an isolated, trusted network, set `RoofControllerSecurity__RequireHttps=false` explicitly:
`ALLOW_INSECURE_HTTP=true` for the deploy script, or the `pi-lan-http` compose profile. A warning is logged at every
start, because keys then cross the network in clear text. There is no mode where `RequireHttps` is on without an
HTTPS listener: the deployment check rejects it, because every remote request would get 403 while `/health/ready`
still passed.

### 3. Operator key for the deploy script

The deploy script sends a Stop before replacing the running controller, and afterwards checks the new one with an
authenticated Status and Stop. It uses an operator key, which must also be one of the keys in the secrets directory.
Provide it on the machine that runs the script, in one of two ways:

- an environment variable, `ROOF_OPERATOR_API_KEY`
- a file, `~/.config/hvo-roof/operator.key`, with mode `600`

The script refuses a key file that is readable by group or others. The key is passed to `curl` on stdin, never as an
argument. The deployment check receives only its SHA-256, to confirm that the new container accepts it.

## The deployment check

The image has a check mode that reads the same configuration sources as the controller (settings files, environment
variables and `/run/secrets`) and exits without starting the web host or touching the HAT:

```bash
docker run --rm <same --env, --device and --mount options as the controller> hvov9/roof-controller:v4 --validate-deployment
```

It prints a report and exits 0 when the deployment is usable, 1 otherwise. It fails on:

- `RoofControllerOptionsV4` values that the controller would refuse at startup, including
  `IgnorePhysicalLimitSwitches` without `AllowIgnoringLimitSwitchesOnPhysicalHardware` when `/dev/i2c-1` is mapped
  into the container: the HAT library then drives the roof hardware whatever `HVO_FORCE_RASPBERRY_PI` or
  `USE_REAL_GPIO` say. The check only looks for the device; it never opens it.
- a value that cannot be converted in `RoofControllerOptionsV4`, `RoofControllerSecurity`,
  `RoofControllerHostOptionsV4`, `ConsoleLogBuffer`, `BlueIris` or `Telemetry` (the report names the setting, not the
  value), and `Telemetry` options that the controller would refuse
- a `Logging` level (`Logging:LogLevel:*` or `Logging:<provider>:LogLevel:*`) that is not a log level name, such as
  `Info`: the controller would not start
- API key entries that would be ignored, no usable key, or no `RoofOperator`/`RoofAdmin` key
- with `DeploymentCheck__DeployKeySha256` set (the deploy script sets it): no configured key has that SHA-256, or
  that key has neither the `RoofOperator` nor the `RoofAdmin` role
- `RoofControllerSecurity:RequireHttps` in effect when Kestrel would not listen on HTTPS
- no listener on `http://localhost:8080` (for example `Kestrel:Endpoints` that replace the image's
  `ASPNETCORE_URLS`): the container health check and the deploy script's calls use it, so the deployment would be
  rolled back
- a `Kestrel:Endpoints` entry without a `Url`, or an `http` endpoint with HTTPS-only settings (`Certificate`,
  `ClientCertificateMode`, `SslProtocols`, `Sni`): Kestrel would not start
- an HTTPS listener without a certificate, or a certificate that cannot be loaded, has no private key, has an
  Extended Key Usage without Server Authentication, is not yet valid or has expired. `Kestrel:Certificates:Default`
  and any other configured certificate are checked even when every listener is plain HTTP, because Kestrel loads
  them at startup.
- an `AllowedHosts` list without `localhost`: the container health check would get 400. Entries must match exactly,
  with no spaces or ports.
- a configuration that cannot be loaded at all, such as a malformed settings file

It warns on plain HTTP outside Development, a certificate that expires within 30 days, a certificate given only by
store subject, `AllowAnonymousStop`, a misconfigured camera proxy (the roof still works), and `AllowedHosts=*` in
Production. It never prints key values, passwords or other setting values.

The deploy script runs it as its pre-flight, and each compose profile runs it as a one-shot service that the controller
depends on.

## Deploying with the script

```bash
cd src/HVO.RoofControllerV4.RPi
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https REMOTE_CA_CERT=~/roof.crt ./deploy-roofcontroller-rpi.sh --dry-run
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https REMOTE_CA_CERT=~/roof.crt \
  ALLOWED_HOSTS="roof-pi;roof-pi.local;localhost" ./deploy-roofcontroller-rpi.sh
```

`PI_HOST` must be a name in the certificate: the script connects to `https://$PI_HOST:$HTTPS_HOST_PORT` after the
deploy.

| Flag | Effect |
|------|--------|
| `--dry-run` | Checks the Docker context, the key and the HTTPS choice. If the controller is running, it reads `GET Status` over loopback and from this machine at the final URL, and prints the relay state. It then prints the plan. Nothing is built or changed. |
| `--force-unverified-stop` | Lets the deploy continue when the Stop cannot be verified, but only after you type `STOP-UNVERIFIED` at the terminal. See the steps below. |
| `--rollback` | Swaps the running controller with `<name>-previous` instead of deploying. See [Rolling back](#rolling-back). |

| Variable | Default | Meaning |
|----------|---------|---------|
| `PI_HOST` | (required) | Host name of the Pi, used for the remote check |
| `DOCKER_CONTEXT` | `rpi-remote` | Docker context that targets the Pi |
| `IMAGE_TAG` | `hvov9/roof-controller:v4` | Image tag |
| `CONTAINER_NAME` | `roof-controller` | Container name. The previous version is kept as `<name>-previous`. |
| `HTTPS_HOST_PORT` | `8443` | Published HTTPS port (the only published port in HTTPS mode) |
| `HOST_PORT` | `8080` | Published HTTP port, only with `ALLOW_INSECURE_HTTP=true` |
| `SECRETS_DIR` | `/etc/hvo-roof/secrets` | Secrets directory on the Pi |
| `HTTPS_CERT_DIR` | (empty) | Certificate directory on the Pi. Required unless `ALLOW_INSECURE_HTTP=true`. |
| `HTTPS_CERT_FILE` | `roof-controller.pfx` | PFX file name inside `HTTPS_CERT_DIR` |
| `ALLOW_INSECURE_HTTP` | `false` | Plain HTTP on `HOST_PORT` with `RoofControllerSecurity__RequireHttps=false` |
| `ALLOWED_HOSTS` | (empty; image default `*`) | Sets `AllowedHosts`. A list must include `localhost`: the health check and the script's in-container calls use it. |
| `REMOTE_CA_CERT` | (empty) | PEM file on this machine that verifies the Pi's certificate, for the remote check |
| `SKIP_REMOTE_CHECK` | `false` | Skips the remote check (a warning is printed). Use only when this machine cannot reach the Pi's published port. |
| `EXTRA_DOCKER_ARGS` | (empty) | Extra `docker run` options, also applied to the pre-flight container. Split on spaces; quotes are not interpreted and nothing is glob-expanded. `--name`, `-d`/`--detach`, `--rm`, `--restart`, `--cidfile`, `-p`/`--publish` and `-P`/`--publish-all` are refused, since the script sets them, and so are `--stop-timeout` and `--stop-signal`, which could cut the controller's shutdown stop short (use `STOP_TIMEOUT_SECONDS`). |
| `STOP_TIMEOUT_SECONDS` | `30` | Graceful-stop window, used for both `docker stop -t` and `--stop-timeout` |
| `READY_TIMEOUT_SECONDS` | `120` | How long to wait for `/health/ready` |
| `POLL_INTERVAL_SECONDS` | `3` | Readiness poll interval |
| `ROOF_OPERATOR_API_KEY` / `OPERATOR_KEY_FILE` | / `~/.config/hvo-roof/operator.key` | Key for the Stop and Status checks |

`STOP_TIMEOUT_SECONDS` and `READY_TIMEOUT_SECONDS` must be whole numbers from 1 to 86400, and the ports whole numbers
from 1 to 65535. They are read as decimal, so `010` means 10. `POLL_INTERVAL_SECONDS` may have a fraction, such as
`0.5`.

The machine that runs the script needs Docker CLI 20.10 or later (the script reads container state with
`docker ps --format '{{.State}}'`), and `jq` or `python3` to parse the Stop response. Without either, the stop is
treated as unverified. It also needs `setsid` (standard on Linux) or `perl` (macOS): from the old controller's stop on,
docker runs in its own session, detached from the terminal. So the Docker context must connect without prompting; an
SSH context needs a key or `ssh-agent`, not a password prompt.

### What the script does, in order

1. **Checks what it needs.** The script stops at the first failure:
   - the settings above and `EXTRA_DOCKER_ARGS` are valid, and `REMOTE_CA_CERT` is readable. This is checked before
     the script contacts Docker.
   - either an HTTPS certificate directory is set or insecure HTTP was chosen explicitly
   - the Docker context is available
   - an operator key is available
   - Docker reports the state of `<name>` and `<name>-previous`. If the query fails or matches more than one
     container, the script stops and nothing is changed.
   - `<name>-previous` is not running, restarting or paused, even when there is no `<name>`: two controllers must never
     drive the HAT. Stop it and retry.
2. **Builds** the arm64 image and loads it on the Pi.
3. **Runs the pre-flight check** on the Pi: the new image with `--validate-deployment` and exactly the environment,
   devices and mounts the controller will get (see [The deployment check](#the-deployment-check)), plus the SHA-256 of
   the script's key. Docker also fails here on a missing `/dev/gpiomem`, `/dev/i2c-1`, thermal file, secrets directory
   or certificate directory. Any failure stops the deploy while the old controller is still running and untouched.
4. **Requests a verified stop** if the old container is running. The script sends `POST /api/v4.0/RoofControl/Stop`
   from inside the container over loopback: `docker exec ... curl`, with the key passed on stdin so it never shows in
   a process list. The stop counts as verified only when the response is HTTP 200 and the body has:
   - `relayRegisterState` = `Verified`
   - `relayRegisterMask` = `0`
   - `commandedMotion` = `None`

   Anything else aborts the deploy, including 401, 503, an unreachable container or a response that cannot be parsed.
   `--force-unverified-stop` overrides the abort only after the typed confirmation. Before typing it, confirm that
   you can see the roof and that it is not moving, or that the drive is isolated.
5. **Stops the old container gracefully** with `docker stop -t 30` (SIGTERM). The app's shutdown path stops the roof
   again and ends camera streams. The old container is renamed `<name>-previous` with restart policy `no`, so it can
   be restored but never starts by itself. An older, stopped `<name>-previous` is removed only after this stop has
   succeeded, just before the rename, so an aborted deploy keeps it. The script reads the container states again
   after the pre-flight, and a `<name>-previous` that is running by then still aborts before anything is stopped.
6. **Starts the new container** with `--restart unless-stopped` and `--stop-timeout 30`. In HTTPS mode only
   `HTTPS_HOST_PORT` is published; plain HTTP listens on loopback inside the container, for the health check and the
   script's `docker exec` calls.
7. **Verifies the new controller:**
   - `/health/ready` within `READY_TIMEOUT_SECONDS` (from inside the container)
   - an authenticated `GET Status` inside the container returns 200
   - from the machine running the script, at `https://$PI_HOST:$HTTPS_HOST_PORT` (or `http://$PI_HOST:$HOST_PORT` in
     insecure mode): an authenticated `GET Status` returns 200 and `POST Stop` returns a verified stop. This proves the
     published port, the certificate, `AllowedHosts` and the key from a real client's point of view.
8. **Restores the old controller on failure.** Once the old controller's stop begins in step 5, any failure restores
   it. That includes a failed `docker stop` and an interruption: Ctrl-C, SIGTERM, or a closed terminal or SSH
   session. The script:
   - prints the new container's last 60 log lines, then stops (SIGTERM) and removes it
   - renames `<name>-previous` back to `<name>` and starts it if it was running before, then waits for readiness

   It removes only the container it created, found by the ID Docker wrote to the run's `--cidfile`. If another
   container has taken `<name>` meanwhile, the script leaves it alone and keeps the old controller, stopped, as
   `<name>-previous`.

   From the stop on, docker runs in its own session (`setsid`, or `perl` on macOS), so an interrupt never cuts a
   docker call short: the restore starts once the call in progress returns. If the old controller is still running
   by then (its stop never finished, for example because the connection to the Pi dropped while the daemon was still
   stopping it), the restore stops it fully before starting it again. During the restore, further signals are ignored
   and failed writes to the terminal are skipped. The last line is `[deploy] ERROR: Deployment failed (<reason>). <outcome>`; the outcome starts with
   `Rolled back:` when the old controller is back. The script exits 1, or 129, 130 or 143 after SIGHUP, SIGINT or
   SIGTERM. With no previous controller (a first deploy), the outcome says the roof controller is not running.

### Rolling back

```bash
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https REMOTE_CA_CERT=~/roof.crt ./deploy-roofcontroller-rpi.sh --rollback
```

`--rollback` swaps the running controller with `<name>-previous`. It uses the same checks from step 1 and the same
verified stop gate as a deploy. It then swaps the names and restart policies through a temporary `<name>-swap`, starts
the restored controller and runs the checks from step 7 against it. Nothing is built. Run it again to swap back.

Set `HTTPS_CERT_DIR` or `ALLOW_INSECURE_HTTP=true` as for the version being restored, since that decides the URL of
the remote check. Without either, `--rollback` stops before contacting Docker.

If the swap or the start fails, or the script is interrupted before the start, the swap is undone. The original
controller is back as `<name>` and restarted if it was running, `<name>-previous` is unchanged, and the outcome starts
with `Undone:`. Once the start has succeeded, the restored controller is left running, even when the checks after it
fail or the script is interrupted, and the script exits non-zero.

A rollback that could not be undone leaves `<name>-swap` behind. Later rollbacks refuse to run until it is gone, and
change nothing. Find out which version it is (`docker ps -a --filter name=<name>`), then either rename it to whichever
of `<name>` and `<name>-previous` is free (`docker rename <name>-swap <name>`) or remove it if it is not needed.

### First deploy over an older controller

Controllers built before this change have no API keys and no `POST` Stop route. Stop was `GET` and unauthenticated.
Their Stop response also lacks `relayRegisterState` and `commandedMotion`. So the verified stop always fails against
them. For that one deploy:

1. Make sure the roof is closed and idle, and watch it (camera or on site).
2. Run the script with `--force-unverified-stop` and type `STOP-UNVERIFIED` when asked.
3. The old container still gets SIGTERM with the 30-second grace period.

The old controller is kept as `<name>-previous`. An automatic rollback to it restores the unauthenticated controller
on its old port; `--rollback` to it fails the remote check (it has no `POST` Stop), so check it by hand. Swapping
back from it with `--rollback` runs the verified stop gate against that old controller, which always fails, so it
needs `--force-unverified-stop` and the typed confirmation as well.

Before that deploy, provision `/etc/hvo-roof/secrets`, including the operator key used by the script. Then update any
API automation clients (they now need `X-Api-Key` and `POST` commands). Operators sign in to the console with a key
from the secrets directory.

## Deploying with compose

`src/HVO.RoofControllerV4.RPi/docker-compose.yaml` has two profiles. Choose one; enabling both is rejected because they
share the container name `roof-controller` (which also collides with a controller started by the script).

| Profile | Transport | Needs |
|---------|-----------|-------|
| `pi` | HTTPS on `8443` only. Plain HTTP listens on loopback inside the container for the health check. | The certificate directory (`HVO_ROOF_CERT_DIR`, default `/etc/hvo-roof/https`) with `HVO_ROOF_CERT_FILE` (default `roof-controller.pfx`), and its password in the secrets directory |
| `pi-lan-http` | Plain HTTP on `8080` with `RoofControllerSecurity__RequireHttps=false` | An isolated, trusted LAN: keys cross it in clear text |

```bash
cd src/HVO.RoofControllerV4.RPi
export HVO_ROOF_ALLOWED_HOSTS="roof-pi;roof-pi.local;localhost"
docker compose --profile pi build
docker compose --profile pi run --rm roof-controller-check   # the deployment check on its own
docker compose --profile pi up -d                             # only if the check passed
```

Both profiles mirror the script: the secrets directory at `/run/secrets` (`HVO_ROOF_SECRETS_DIR`, default
`/etc/hvo-roof/secrets`), `stop_grace_period: 30s`, and `AllowedHosts` from `HVO_ROOF_ALLOWED_HOSTS`. The secrets and
certificate directories must exist; compose does not create them.

Each profile first runs the [deployment check](#the-deployment-check) as a one-shot service with the same environment
and mounts (`roof-controller-check` or `roof-controller-lan-http-check`), and the new controller starts only if it
exits 0. But `up` stops and replaces a running controller before the check runs, so a check that fails during `up`
leaves **no** controller running. That is why the commands above run the check on its own first, with the same
`HVO_ROOF_*` variables, and run `up` only if it passes. Read the report of the check that `up` ran with
`docker compose --profile pi logs roof-controller-check`.

Compose does **not** perform the verified stop, keep the previous container, check the published URL or roll back.
Before `up` replaces a running controller, stop the roof yourself with `POST .../Stop` and check the response. After
it, check the published URL from another machine (see below). Prefer the script.

## Shutdown timing

| Layer | Value | Purpose |
|-------|-------|---------|
| App `HostOptions.ShutdownTimeout` | 20 s | Time the host gives hosted services to stop. The roof controller's shutdown (Stop, then verify all relays off) runs here. |
| Controller shutdown call | 5 s + 1 s grace, per call | The host requests the verified stop at `ApplicationStopping`, from its `StopAsync` (which shares the first call while it runs) and when the service loop ends. It waits 5 s for each result, then 1 s more before it abandons a call blocked in HAT I/O and logs Critical. After an unverified result the next trigger calls again, so up to three calls run one after another: 18 s, inside the 20 s. |
| Unverified shutdown retry | every 500 ms | Re-runs the all-off sequence until it verifies, the controller is disposed or 15 s pass. Each unverified shutdown call waits up to 5 s on it. Disposal usually ends it, about 10 s after SIGTERM plus the web server's stop time; when all three calls run, the 15 s limit ends it first. |
| Controller disposal | 2 s | Waits for the controller lock and then for its background tasks. If blocked HAT I/O holds the lock, disposal stops waiting (Critical: relay state unknown) rather than block the exit, and runs its all-off stop if the call returns before the process ends. |
| Camera streams | end at `ApplicationStopping` | An open viewer never holds up shutdown |
| Docker `stop_grace_period` / `--stop-timeout` / `docker stop -t` | 30 s | Must exceed the app's timeout. Docker sends SIGKILL after this. |

Keep the Docker value above the app value. If Docker kills the process first, the controller cannot confirm that the
relays are off.

## After deploying: checks on the device

Run these from the deploying machine with the Pi's Docker context selected (`docker context use rpi-remote`). The key
is read from this machine:

```bash
# Container healthy and listening
docker ps --filter name=roof-controller
docker logs --tail 50 roof-controller      # look for "N API key(s) configured" and no Critical/Error security lines

# Loopback API call from inside the container (key on stdin, never on the command line)
printf 'X-Api-Key: %s\n' "$(cat ~/.config/hvo-roof/operator.key)" \
  | docker exec -i roof-controller curl -sS -H @- http://localhost:8080/api/v4.0/RoofControl/Status

# From another machine: the certificate is served and trusted, and the API needs a key
curl -sS https://roof-pi:8443/health/ready
curl -sS -o /dev/null -w '%{http_code}\n' https://roof-pi:8443/api/v4.0/RoofControl/Status      # expect 401
curl -sS -o /dev/null -w '%{http_code}\n' http://roof-pi:8080/api/v4.0/RoofControl/Status       # expect a connection error: 8080 is not published in HTTPS mode

# The previous version, kept for --rollback (stopped, restart policy "no")
docker ps -a --filter name=roof-controller-previous
```

The deploy script already made the authenticated remote Status and Stop calls unless `SKIP_REMOTE_CHECK=true` was set.
