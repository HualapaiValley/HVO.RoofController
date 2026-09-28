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
  `USE_REAL_GPIO` say. The check only looks for the device; it never opens it. HAT emulator mode counts as hardware
  here too.
- HAT emulator mode (`HatEmulator:Enabled`) outside Development without `HatEmulator:AllowOutsideDevelopment`, or with
  invalid `HatEmulator` settings: the controller would refuse to start. See
  [HAT emulator mode (test rigs)](#hat-emulator-mode-test-rigs).
- HAT emulator mode with `/dev/i2c-1` mapped. No supported deployment has both, so this is emulator settings left on a
  physical deployment, usually a `HatEmulator` file in the secrets directory: it is read last, so it overrides the
  `HatEmulator__Enabled=false` that the deploy script and the Pi compose profiles set.
- a value that cannot be converted in `RoofControllerOptionsV4`, `RoofControllerSecurity`,
  `RoofControllerHostOptionsV4`, `ConsoleLogBuffer`, `BlueIris`, `Telemetry` or `HatEmulator` (the report names the setting, not the
  value), and `Telemetry` options that the controller would refuse
- a `Logging` level (`Logging:LogLevel:*` or `Logging:<provider>:LogLevel:*`, nested categories included) that is not
  a log level name, such as `Info`: the controller would not start
- API key entries that would be ignored, no usable key, or no `RoofOperator`/`RoofAdmin` key
- with `DeploymentCheck__DeployKeySha256` set (the deploy script sets it): no configured key has that SHA-256, or
  that key has neither the `RoofOperator` nor the `RoofAdmin` role
- `RoofControllerSecurity:RequireHttps` in effect when Kestrel would not listen on HTTPS
- no listener on `http://localhost:8080` (for example `Kestrel:Endpoints` that replace the image's
  `ASPNETCORE_URLS`): the container health check and the deploy script's calls use it, so the deployment would be
  rolled back. An http listener on port 8080 serves it when its host is `localhost`, `127.0.0.1`, `[::1]`, an
  any-address (`0.0.0.0`, `[::]`) or a name (`*`, `+` or a host name), which Kestrel binds to all addresses; any
  other IP address does not.
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
store subject, `AllowAnonymousStop`, a misconfigured camera proxy (the roof still works), `AllowedHosts=*` in
Production, and allowed HAT emulator mode (the roof will not move). It never prints key values, passwords or other
setting values.

The deploy script runs it as its pre-flight, and each Pi compose profile (`pi`, `pi-lan-http`) runs it as a one-shot
service that the controller depends on. The `emulator` profile, a test rig, does not run it.

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
| `--verify-remote` | Deploys nothing and makes no Docker call. It runs only the remote check of step 7 against the controller that answers at the published URL: an authenticated `GET Status` that must report the expected `hatMode`, then a `POST Stop` that must be verified. The Stop stops the roof. This is the remote check for a [Compose deployment](#deploying-with-compose). It cannot be combined with the other flags or with `SKIP_REMOTE_CHECK=true`. |

| Variable | Default | Meaning |
|----------|---------|---------|
| `PI_HOST` | (required) | Host name of the Pi, used for the remote check |
| `DOCKER_CONTEXT` | `rpi-remote` | Docker context that targets the Pi |
| `IMAGE_TAG` | `hvov9/roof-controller:v4` | Image tag |
| `BUILD_PLATFORM` | `linux/arm64` | Platform the image is built for: the Pi's. `linux/amd64` is accepted only in [HAT emulator mode](#hat-emulator-mode-test-rigs), for a test rig on a PC. |
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
| `EXTRA_DOCKER_ARGS` | (empty) | Extra `docker run` options, also applied to the pre-flight container. Split on spaces; quotes are not interpreted and nothing is glob-expanded. `--name`, `-d`/`--detach`, `--rm`, `--restart`, `--cidfile`, `-p`/`--publish` and `-P`/`--publish-all` are refused, since the script sets them, and so are `--stop-timeout` and `--stop-signal`, which could cut the controller's shutdown stop short (use `STOP_TIMEOUT_SECONDS`). `HatEmulator` settings are refused, given directly or in an `--env-file` (read on this machine, so it must be readable here). In HAT emulator mode, an I2C `--device`, `--privileged` and a mount of the host's `/` or `/dev` are refused as well. |
| `STOP_TIMEOUT_SECONDS` | `30` | Graceful-stop window, used for both `docker stop -t` and `--stop-timeout` |
| `READY_TIMEOUT_SECONDS` | `120` | How long to wait for `/health/ready` |
| `POLL_INTERVAL_SECONDS` | `3` | Readiness poll interval |
| `ROOF_OPERATOR_API_KEY` / `OPERATOR_KEY_FILE` | / `~/.config/hvo-roof/operator.key` | Key for the Stop and Status checks |
| `HAT_EMULATOR_ENDPOINT` | (empty) | Test rigs only: `<host>:<port>` of a HAT emulator the container can reach. The controller uses it in place of the physical HAT. See [HAT emulator mode (test rigs)](#hat-emulator-mode-test-rigs). |
| `ALLOW_EMULATED_HAT` | `false` | Must be `true` for `HAT_EMULATOR_ENDPOINT` to be accepted, and for `--rollback` to restore a version that uses the HAT emulator |

`STOP_TIMEOUT_SECONDS` and `READY_TIMEOUT_SECONDS` must be whole numbers from 1 to 86400, and the ports whole numbers
from 1 to 65535. They are read as decimal, so `010` means 10. `POLL_INTERVAL_SECONDS` may have a fraction, such as
`0.5`.

The machine that runs the script needs Docker CLI 20.10 or later (the script reads container state with
`docker ps --format '{{.State}}'`), and `jq` or `python3` to parse the Stop response. Without either, the stop is
treated as unverified. It also needs `setsid` (standard on Linux) or `perl` (macOS): from the old controller's stop on,
docker runs in its own session, detached from the terminal. So the Docker context must connect without prompting; an
SSH context needs a key or `ssh-agent` and a known host key, not a prompt. The script checks this before it changes
anything.

### What the script does, in order

1. **Checks what it needs.** The script stops at the first failure:
   - the settings above and `EXTRA_DOCKER_ARGS` are valid, and `REMOTE_CA_CERT` is readable. This is checked before
     the script contacts Docker.
   - either an HTTPS certificate directory is set or insecure HTTP was chosen explicitly
   - the Docker context is available and connects without prompting (checked in its own session, as the switch runs
     docker)
   - an operator key is available
   - Docker reports the state of `<name>` and `<name>-previous`. If the query fails or matches more than one
     container, the script stops and nothing is changed.
   - `<name>-previous` is not running, restarting or paused, even when there is no `<name>`: two controllers must never
     drive the HAT. Stop it and retry.
   - neither `<name>` nor `<name>-previous` was created by Docker Compose. The script replaces and restores only
     controllers it created; see [Moving between Compose and the deploy script](#moving-between-compose-and-the-deploy-script).
2. **Builds** the image for `BUILD_PLATFORM` (the Pi's, `linux/arm64`) and loads it on the Pi.
3. **Runs the pre-flight check** on the Pi: the new image with `--validate-deployment` and exactly the environment,
   devices and mounts the controller will get (see [The deployment check](#the-deployment-check)), plus the SHA-256 of
   the script's key. Docker also fails here on a missing `/dev/gpiomem`, `/dev/i2c-1` or thermal file (none of them
   mapped in HAT emulator mode), secrets directory or certificate directory. Any failure stops the deploy while the old
   controller is still running and untouched.
4. **Requests a verified stop** if the old container is running. The script sends `POST /api/v4.0/RoofControl/Stop`
   from inside the container over loopback: `docker exec ... curl`, with the key passed on stdin so it never shows in
   a process list. The stop counts as verified only when the response is HTTP 200 and the body has:
   - `relayRegisterState` = `Verified`
   - `relayRegisterMask` = `0`
   - `commandedMotion` = `None`

   Anything else aborts the deploy, including 401, 503, an unreachable container or a response that cannot be parsed.
   `--force-unverified-stop` overrides the abort only after the typed confirmation. Before typing it, confirm that
   you can see the roof and that it is not moving, or that the drive is isolated. The script reads the confirmation
   from the terminal (`/dev/tty`), not from standard input. Without a terminal, or with any other answer, the deploy
   aborts and the old controller keeps running.
5. **Stops the old container gracefully** with `docker stop -t 30` (SIGTERM). The app's shutdown path stops the roof
   again and ends camera streams. The old container is renamed `<name>-previous` with restart policy `no`, so it can
   be restored but never starts by itself. An older, stopped `<name>-previous` is removed only after this stop has
   succeeded, just before the rename, so an aborted deploy keeps it. The script reads the container states again
   after the pre-flight, and a `<name>-previous` that is running by then still aborts before anything is stopped.
6. **Starts the new container** with `--restart unless-stopped` and `--stop-timeout 30`. In HTTPS mode only
   `HTTPS_HOST_PORT` is published; plain HTTP listens on loopback inside the container, for the health check and the
   script's `docker exec` calls.
7. **Verifies the new controller:**
   - `/health/ready` within `READY_TIMEOUT_SECONDS` (from inside the container). A new controller that exits before
     it is ready fails this check at once, without waiting for the timeout, whether its container stopped or Docker
     restarted it (`--restart unless-stopped` restarts a controller that exits). One that Docker restarted before the
     first check is caught when it exits again.
   - an authenticated `GET Status` inside the container returns 200 and reports the HAT this run deploys: `hatMode`
     `Physical`, or `Emulated` in [HAT emulator mode](#hat-emulator-mode-test-rigs). The secrets directory is read
     after the script's `--env` settings, so a `HatEmulator` file there could switch the HAT; this check catches it.
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

### HAT emulator mode (test rigs)

A test Pi can run the production image and settings with only the HAT emulated: the
[HAT emulator](emulator.md) answers the HAT's registers, and an emulated roof, drive and limit switches stand behind
it. **Never use this on the observatory Pi:** the controller then does not operate the roof.

The script does not start the emulator, so set it up on the test Pi first. Build its image for `linux/arm64` as the
script builds the controller's, load it into the Pi's Docker context (the script's `DOCKER_CONTEXT`, default
`rpi-remote`), and run it on a Docker network of its own, with its unauthenticated control API on the Pi's loopback
only. From the repository root:

```bash
docker buildx build --platform linux/arm64 -f src/HVO.RoofControllerV4.Emulator/Dockerfile \
  -t hvo/roof-hat-emulator:dev --load .
docker save hvo/roof-hat-emulator:dev | docker --context rpi-remote load
docker --context rpi-remote network create hvo-emulator
docker --context rpi-remote run -d --name hat-emulator --network hvo-emulator --restart unless-stopped \
  -p 127.0.0.1:5290:5290 hvo/roof-hat-emulator:dev
```

Then deploy the controller onto the same network, pointing it at the emulator's register port:

```bash
PI_HOST=test-pi HTTPS_CERT_DIR=/etc/hvo-roof/https REMOTE_CA_CERT=~/test-pi.crt \
  HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true \
  EXTRA_DOCKER_ARGS="--network hvo-emulator" ./deploy-roofcontroller-rpi.sh
```

- `HAT_EMULATOR_ENDPOINT` without `ALLOW_EMULATED_HAT=true` is refused before anything changes. So is a
  `HatEmulator` setting in `EXTRA_DOCKER_ARGS`, given directly or in an `--env-file` (the script reads the file, which
  must be readable on the machine running it): emulator mode is chosen only through these two variables, so that the
  deployment records it.
- The container gets `HatEmulator__Enabled=true`, the host and port, and `HatEmulator__AllowOutsideDevelopment=true`.
  No host device or Pi file is mapped: **not** `/dev/i2c-1`, and `EXTRA_DOCKER_ARGS` may not map an I2C device, use
  `--privileged` or mount the host's `/` or `/dev`, so the controller cannot reach a physical HAT whatever its settings
  say. Nor `/dev/gpiomem` or the thermal sensor, so the rig need not be a Pi: on a PC, add
  `BUILD_PLATFORM=linux/amd64` (accepted in emulator mode only) and a Docker context for that PC.
- Without `HAT_EMULATOR_ENDPOINT`, the script maps `/dev/gpiomem`, the thermal sensor and `/dev/i2c-1` and sets
  `HatEmulator__Enabled=false` and `HatEmulator__AllowOutsideDevelopment=false`. The secrets directory is read after these settings and could still
  override them. The pre-flight [deployment check](#the-deployment-check) then fails, because emulator mode with
  `/dev/i2c-1` mapped is refused, and nothing is stopped. The check in step 7 is the backstop: the new controller
  must report `hatMode` `Physical` (or `Emulated` in emulator mode), or the deploy is rolled back.
- The emulator must be reachable from the controller's container, here over the `hvo-emulator` network given in
  `EXTRA_DOCKER_ARGS`. The pre-flight check runs with the same options, so it fails if the network is missing. If
  the emulator is not running, the new controller latches `RelayVerificationFailed`, fails readiness and is rolled
  back.
- The script prints a warning before it changes anything, and the dry-run and the final report name the HAT the
  controller uses.

A controller in emulator mode shows an `EMULATED HAT` banner on every page and reports Degraded health, or worse, with
every health description naming the emulator. `tests/emulator/deploy-scenarios.sh` deploys this way on a PC, with the
default Docker context, to test the script (see [Container scenarios](emulator.md#container-scenarios)). To run the
controller against the emulator without the script, use the compose `emulator` profile below.

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

The restored version's HAT is checked twice. Before anything is stopped, the script reads the environment
`<name>-previous` was deployed with: a version deployed for the HAT emulator is refused, with nothing changed, unless
`ALLOW_EMULATED_HAT=true` (a test rig). With the flag, a warning names the emulator, and so does a readiness failure.
Once the restored version runs, the checks include the `hatMode` it reports. `Physical` is accepted, and so is a Status
without `hatMode` (a version from before HAT emulator mode) that reports `isUsingPhysicalHardware` `true`. With `false`,
such a version ran on the register simulation, as it did without an I2C bus, and the check fails. `Emulated` is
accepted only with `ALLOW_EMULATED_HAT=true`;
a version that reports it without being deployed for the emulator takes it from elsewhere, such as the secrets
directory. Without the flag that check fails, the restored version is left running as above, and `--rollback` again
swaps back. The final line names the `hatMode`.

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

`src/HVO.RoofControllerV4.RPi/docker-compose.yaml` has two Pi profiles. Choose one; enabling both is rejected because
they share the container name `roof-controller` (which also collides with a controller started by the script).

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

Before `up` replaces a running controller, and again after `up`, check the controller from the deploying machine (see
[below](#checking-a-compose-controller-from-another-machine)):

```bash
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https REMOTE_CA_CERT=~/roof.crt ./deploy-roofcontroller-rpi.sh --verify-remote
```

Both Pi profiles mirror the script: the secrets directory at `/run/secrets` (`HVO_ROOF_SECRETS_DIR`, default
`/etc/hvo-roof/secrets`), `stop_grace_period: 30s`, and `AllowedHosts` from `HVO_ROOF_ALLOWED_HOSTS`. The secrets and
certificate directories must exist; compose does not create them.

Each Pi profile first runs the [deployment check](#the-deployment-check) as a one-shot service with the same environment
and mounts (`roof-controller-check` or `roof-controller-lan-http-check`), and the new controller starts only if it
exits 0. But `up` stops and replaces a running controller before the check runs, so a check that fails during `up`
leaves **no** controller running. That is why the commands above run the check on its own first, with the same
`HVO_ROOF_*` variables, and run `up` only if it passes. Read the report of the check that `up` ran with
`docker compose --profile pi logs roof-controller-check`.

Compose does **not** perform the verified stop, keep the previous container, check the published URL or roll back.
Prefer the script. With Compose, the script's `--verify-remote` does the stop and the URL check, as described below.
Nothing keeps the previous version or rolls back.

### Checking a Compose controller from another machine

The Compose health check only proves that the controller answers on loopback inside its container. That says nothing
about whether clients can reach it with a key. `--verify-remote` checks this from the deploying machine, as the script's
own deploy does in step 7. It makes no Docker call, so it needs no Docker context for the Pi, and it changes no
container:

1. An authenticated `GET Status` at the published URL must return 200 with `hatMode` `Physical` (`Emulated` with
   `HAT_EMULATOR_ENDPOINT` and `ALLOW_EMULATED_HAT=true`). The Pi profiles pin the HAT emulator off, so any other
   value points to a `HatEmulator` setting in the secrets directory. A controller from before emulator mode reports
   no `hatMode`. It passes when `Physical` is expected and it reports `isUsingPhysicalHardware` `true`; with `false`
   it ran on the register simulation, and the check fails before the Stop.
2. A `POST Stop` must return 200 with `relayRegisterState` `Verified`, `relayRegisterMask` `0` and `commandedMotion`
   `None`. This stops the roof.

It exits 0 only if both checks pass. It reads the key as a deploy does (`ROOF_OPERATOR_API_KEY` or `OPERATOR_KEY_FILE`),
sends it on standard input and never prints it. The URL is built from the deploy settings:

| Profile | Settings | URL checked |
|---------|----------|-------------|
| `pi` | `PI_HOST`, `HTTPS_CERT_DIR` (any value selects HTTPS; it is not read here), and `REMOTE_CA_CERT` if this machine does not trust the certificate | `https://$PI_HOST:8443` |
| `pi-lan-http` | `PI_HOST`, `ALLOW_INSECURE_HTTP=true` | `http://$PI_HOST:8080` |

Run it before `up` replaces a running controller: the verified Stop is the stop the script would request, and a
failure means the roof is not known to be stopped. Run it again after `up`: until it passes, do not rely on remote
control. A 401 means the controller does not know the key (check the secrets directory), and a 400 that `PI_HOST` is
not in `HVO_ROOF_ALLOWED_HOSTS`. A connection or TLS failure points to the port, the network or the certificate (set
`REMOTE_CA_CERT`). The check does not prove the key's role: any key may send a Stop, so a viewer key passes too. Only
a deploy's pre-flight refuses a key that cannot operate the roof; `--rollback` and `--verify-remote` run no pre-flight.

A third profile, `emulator`, runs the production settings against the [HAT emulator](emulator.md) on any machine,
with no devices, on `http://127.0.0.1:5195` (`HVO_EMULATED_ROOF_PORT`). It builds both images, maps no devices,
publishes on loopback only and uses a network of its own, so it can run next to a Pi profile and a Pi-profile
controller cannot reach its emulator. It does not run the deployment check. It is for
testing, not for the observatory:

```bash
cd src/HVO.RoofControllerV4.RPi
HVO_EMULATED_ROOF_API_KEY=$(openssl rand -hex 24) docker compose --profile emulator up -d --build
```

`tests/emulator/compose-smoke-test.sh` opens and closes the emulated roof through it.

## Moving between Compose and the deploy script

The script and the Compose Pi profiles must never manage the same controller. Both name it `roof-controller`, so
Docker refuses to create one while the other's container exists: `docker compose up` fails with the name already in
use and changes nothing. The script, for its part, refuses a `<name>` or `<name>-previous` that Compose created (it
carries Compose's project label), for a deploy, `--rollback` and `--dry-run` alike, before anything changes. Renamed
by the script, such a container would keep the label, and a later `docker compose up` or `down` could start it next
to the new controller or remove the version kept for rollback.

So a move removes one side's controller before the other starts one. Run the commands on the Pi (or with a Docker
context that targets it), and the `docker compose` commands in `src/HVO.RoofControllerV4.RPi` with the same `HVO_ROOF_*`
variables as for `up`. Both use the image tag `hvov9/roof-controller:v4`, so the script's build replaces the image
Compose ran; keep it under a tag of its own to return to it.

**From Compose to the script:**

1. Stop the roof and check that the stop is verified: run `./deploy-roofcontroller-rpi.sh --verify-remote` with the
   Compose profile's settings ([above](#checking-a-compose-controller-from-another-machine)), or check by hand that
   `POST .../Stop` returns 200 with `relayRegisterState` `Verified`, `relayRegisterMask` `0` and `commandedMotion` `None`.
2. Keep the Compose version: `docker tag hvov9/roof-controller:v4 hvov9/roof-controller:v4-compose`.
3. `docker compose --profile pi down`. The controller stops the roof again on SIGTERM, as in step 5 of the script.
4. Run the script. It finds no `<name>` and deploys as a first deploy (`[deploy] No existing container.`), so there is
   no `<name>-previous` for `--rollback` until the next deploy. To return to the Compose version before then, follow
   the steps below.

**From the script to Compose:**

1. Stop the roof and check that the stop is verified, as above.
2. `docker stop -t 30 roof-controller`, then `docker rm roof-controller roof-controller-previous` (Compose keeps no
   previous version).
3. To run the Compose version kept above: `docker tag hvov9/roof-controller:v4-compose hvov9/roof-controller:v4`.
   Otherwise `docker compose --profile pi build` builds the current source.
4. `docker compose --profile pi run --rm roof-controller-check`, then, only if it passed,
   `docker compose --profile pi up -d --no-build`.
5. Check the published URL from another machine with `--verify-remote`
   ([above](#checking-a-compose-controller-from-another-machine)) and the checks in
   [After deploying](#after-deploying-checks-on-the-device).

`tests/emulator/deploy-scenarios.sh migration` runs both moves against the HAT emulator, with the refusals on each
side (see [Container scenarios](emulator.md#container-scenarios)).

## Health and readiness

One health check, `roof_controller`, backs three endpoints:

| Endpoint | Access | Answers |
|----------|--------|---------|
| `/health/live` | anonymous | 200 whenever the process answers. No check runs. |
| `/health/ready` | anonymous, status text only | 200 for Healthy or Degraded, 503 for Unhealthy. The Docker `HEALTHCHECK` and the deploy script's readiness wait use it, from inside the container. |
| `/health` | Viewer key or signed-in console | The same result with its description and data (`HardwareMode`, `IgnorePhysicalLimitSwitches`, `HatEmulatorEndpoint` and more). 503 for Unhealthy. |

The check reports the first of these that applies:

- **Unhealthy:** the service is disposed, shutting down or not initialized; the relay register state is unverified; a
  safety fault is latched; the safety inputs are not healthy; relay register reads are failing or stale; the
  controller is in its error state.
- **Degraded:** the status is unknown; the physical limit switches are ignored; the HAT is the emulator; there is no
  I²C bus (the register simulation); digital input polling is off.
- **Healthy:** none of the above.

What each deployment should report:

| Deployment | `/health` | `/health/ready` | What proves the rest |
|------------|-----------|-----------------|----------------------|
| Pi, deploy script | Healthy | 200 | Step 7: an in-container Status reporting `hatMode` `Physical`, then an authenticated Status and a verified Stop from the deploying machine |
| Pi, Compose `pi` or `pi-lan-http` | Healthy | 200 | The deployment check before start, then `--verify-remote` from another machine ([Checking a Compose controller](#checking-a-compose-controller-from-another-machine)), which also requires `hatMode` `Physical` |
| Test rig with the HAT emulator (script with `HAT_EMULATOR_ENDPOINT`, or the compose `emulator` profile) | Degraded, naming the emulator | 200 | `hatMode` `Emulated` and the `EMULATED HAT` banner ([HAT emulator mode](#hat-emulator-mode-test-rigs)) |
| No I²C bus and emulator mode off (a development machine) | Degraded, "simulation mode" | 200 | `hatMode` `Simulation`. Not a deployment: the deploy script and `--verify-remote` refuse it. |

Readiness does not prove that the controller is usable remotely. `/health/ready` needs no key and is exempt from
`RequireHttps`, and the deploy script polls it inside the container. The port, the certificate, `AllowedHosts` and a
key the controller accepts are proven only by the authenticated remote check: step 7 of the script, or
`--verify-remote` for Compose. That the key can operate the roof is checked only by a deploy's pre-flight, not by
`--rollback` or `--verify-remote`.

Readiness also passes while Degraded. On a Pi with the physical HAT, Degraded means limit switches ignored (allowed
only with `AllowIgnoringLimitSwitchesOnPhysicalHardware`), input polling off, or a status not yet known. After a deploy,
read `/health` with a Viewer key and expect Healthy.

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
For a Compose controller, or once the port is reachable after a deploy with `SKIP_REMOTE_CHECK=true`, make them with
`--verify-remote` ([Checking a Compose controller](#checking-a-compose-controller-from-another-machine)).
