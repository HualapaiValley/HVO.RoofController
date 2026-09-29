# Deploying Roof Controller V4 to the Raspberry Pi

This page covers:

- preparing the Pi (secrets, TLS certificate, identity store, settings directories)
- deploying with `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh` or `docker-compose.yaml`
- the deployment check (`--validate-deployment`) both of them run first
- how the controller is stopped safely during a deploy, verified afterwards and rolled back
- the container's two processes, the controller and the web UI, and the supervisor that runs them

For API keys, roles and the Blue Iris credentials, see [security.md](security.md).

## One-time preparation on the Pi

### 1. Secrets directory

Both the deploy script and compose bind-mount `/etc/hvo-roof/secrets` read-only at `/run/secrets`. The app reads one
file per setting from there. The file name is the setting name with `__` in place of `:`.

```
/etc/hvo-roof/secrets/                                      (mode 700)
  RoofControllerSecurity__ApiKeys__0__Name                  e.g. observatory-operator
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

The web UI serves the same certificate on its own port (8088): the container's supervisor gives it a private copy of
the certificate and its password (see [The container's two processes](#the-containers-two-processes)).

Install `roof.crt`, or your CA certificate, as trusted on browsers that use the web UI. Keep a copy on the machine
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

### 3. The identity store

People, their sessions and the API keys added through the API live in the identity store, a file the controller writes
(see [security.md](security.md#the-identity-store)). The deploy script and both Pi compose profiles mount
`/var/lib/hvo-roof/identity` read-write at the same path in the container, and set
`RoofControllerSecurity__Identity__StorePath` to `identity.json` inside it. Create the directory once:

```bash
sudo install -d -m 0700 /var/lib/hvo-roof/identity
```

The controller runs as root in its image, so root owning the directory is enough. Compose does not create it, and the
[deployment check](#the-deployment-check) fails when it is missing or not writable. Back the directory up with the
secrets directory: without it, every person, session and managed key is gone. To use another directory, set
`IDENTITY_DIR` for the script or `HVO_ROOF_IDENTITY_DIR` for compose. The script and Compose use the same default, so
people and sessions carry over when you [move between them](#moving-between-compose-and-the-deploy-script).

### 4. The settings directories

Settings changed through the API are saved in the settings file, and secrets set through the API (the Blue Iris user and
password) in the managed secrets file (see [security.md](security.md#settings)). The deploy script and both Pi compose
profiles mount two directories read-write, at the same paths in the container:

| Directory on the Pi | File | Script / compose variable |
|---------------------|------|---------------------------|
| `/etc/hvo-roof/config` | `appsettings.Local.json` (`RoofControllerSettings__FilePath`) | `CONFIG_DIR` / `HVO_ROOF_CONFIG_DIR` |
| `/var/lib/hvo-roof/settings-secrets` | `secrets.json` (`RoofControllerSettings__SecretsFilePath`) | `MANAGED_SECRETS_DIR` / `HVO_ROOF_MANAGED_SECRETS_DIR` |

The directory is mounted, not the file, so the controller can write a new file and rename it over the old one. Create
both once:

```bash
sudo install -d -m 0755 /etc/hvo-roof/config
sudo install -d -m 0700 /var/lib/hvo-roof/settings-secrets
```

The controller runs as root in its image, so root owning them is enough. It creates the files at the first save.
Neither the deploy script nor Compose creates them. Docker refuses to start the deployment check and the controller
when either is missing, and the [deployment check](#the-deployment-check) fails when either is not writable. The
secrets directory, `/etc/hvo-roof/secrets`, stays read-only.

A setting given with `--env`, in `EXTRA_DOCKER_ARGS` or in the secrets directory overrides the settings file, and the
API cannot change it (`GET /api/v4.0/Settings` names where it comes from). Keep settings that people should change
through the API out of those places. The deploy script and both Pi compose profiles set two such settings themselves,
so they are read-only through the API on the Pi:

- `RoofControllerSecurity__RequireHttps`, from `ALLOW_INSECURE_HTTP` or the profile (`pi` or `pi-lan-http`);
- `RoofControllerOptionsV4__IgnorePhysicalLimitSwitches`, from `IGNORE_PHYSICAL_LIMIT_SWITCHES` (always `false` in
  compose).

Change either by redeploying. Editing the file by hand, and backing it up, are covered in
[commissioning.md](commissioning.md#the-settings-file).

#### Upgrading to the settings file

A controller from before the settings file (#42) runs without these directories. Create both, as above, before the
first deploy of this version: the deploy script and Compose mount them, and Docker refuses to start the controller while
either is missing. The first deploy then starts with no settings file, so every setting is where it was.

A version from before the settings file ignores it. After a [rollback](#rolling-back) to one, the settings changed
through the API are no longer in effect: check `GET /api/v4.0/RoofControl/Configuration` against the wiring before
moving the roof. The file stays in place, so rolling forward again restores them.

### 5. Operator key for the deploy script

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
  `RoofControllerHostOptionsV4`, `BlueIris`, `Telemetry` or `HatEmulator` (the report names the setting, not the
  value), and `Telemetry` options that the controller would refuse
- a `Logging` level (`Logging:LogLevel:*` or `Logging:<provider>:LogLevel:*`, nested categories included) that is not
  a log level name, such as `Info`: the controller would not start
- API key entries that would be ignored, no usable key, or no `RoofOperator`/`RoofAdmin` key
- `RoofControllerSecurity:Identity` settings that the controller would refuse, or an identity store that cannot be
  used: its directory is missing, a probe file cannot be written next to it, or the store file is not valid. The
  check reads the file without changing it, so it is safe to run next to a running controller.
- a settings file or managed secrets file whose directory is missing or not writable (checked with a probe file, as
  for the identity store), a file that is not valid or holds a secret it must not hold, or settings the controller
  would refuse at startup. The report names the setting, never its value.
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
store subject, no identity store outside Development (people and sessions would be lost at each restart), no
settings file or managed secrets file outside Development (settings changed through the API would be lost),
`AllowAnonymousStop`, a misconfigured camera proxy (the roof still works), `AllowedHosts=*` in
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

`PI_HOST` must be a name in the certificate: the script connects to `https://$PI_HOST:$HTTPS_HOST_PORT` and to the
web UI at `https://$PI_HOST:$WEB_HOST_PORT` after the deploy.

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
| `HTTPS_HOST_PORT` | `8443` | Published HTTPS port of the controller's API (with `WEB_HOST_PORT`, the only published ports in HTTPS mode) |
| `HOST_PORT` | `8080` | Published HTTP port, only with `ALLOW_INSECURE_HTTP=true` |
| `WEB_HOST_PORT` | `8088` | Published port of the [web UI](#the-containers-two-processes): HTTPS with the controller's certificate, or plain HTTP with `ALLOW_INSECURE_HTTP=true`. It must differ from the controller's published port. |
| `SECRETS_DIR` | `/etc/hvo-roof/secrets` | Secrets directory on the Pi |
| `HTTPS_CERT_DIR` | (empty) | Certificate directory on the Pi. Required unless `ALLOW_INSECURE_HTTP=true`. |
| `HTTPS_CERT_FILE` | `roof-controller.pfx` | PFX file name inside `HTTPS_CERT_DIR` |
| `IDENTITY_DIR` | `/var/lib/hvo-roof/identity` | [Identity store](#3-the-identity-store) directory on the Pi, mounted read-write for the pre-flight and the controller. Empty keeps the store in memory: people, sessions and managed keys are lost at each restart. |
| `CONFIG_DIR` | `/etc/hvo-roof/config` | [Settings](#4-the-settings-directories) directory on the Pi, mounted read-write for the pre-flight and the controller. Empty keeps settings changed through the API in memory: they are lost at each restart. |
| `MANAGED_SECRETS_DIR` | `/var/lib/hvo-roof/settings-secrets` | [Managed secrets](#4-the-settings-directories) directory on the Pi, mounted the same way. Empty keeps secrets set through the API in memory. |
| `ALLOW_INSECURE_HTTP` | `false` | Plain HTTP on `HOST_PORT` with `RoofControllerSecurity__RequireHttps=false` |
| `ALLOWED_HOSTS` | (empty; image default `*`) | Sets `AllowedHosts`. A list must include `localhost`: the health check and the script's in-container calls use it. |
| `REMOTE_CA_CERT` | (empty) | PEM file on this machine that verifies the Pi's certificate, for the remote check |
| `SKIP_REMOTE_CHECK` | `false` | Skips the remote check (a warning is printed). Use only when this machine cannot reach the Pi's published port. |
| `EXTRA_DOCKER_ARGS` | (empty) | Extra `docker run` options, also applied to the pre-flight container. Split on spaces; quotes are not interpreted and nothing is glob-expanded. `--name`, `-d`/`--detach`, `--rm`, `--restart`, `--cidfile`, `-p`/`--publish` and `-P`/`--publish-all` are refused, since the script sets them (use `HTTPS_HOST_PORT`, `HOST_PORT` and `WEB_HOST_PORT` for the ports), and so are `--stop-timeout` and `--stop-signal`, which could cut the controller's shutdown stop short (use `STOP_TIMEOUT_SECONDS`). `HatEmulator` settings are refused, given directly or in an `--env-file` (read on this machine, so it must be readable here). In HAT emulator mode, an I2C `--device`, `--privileged` and a mount of the host's `/` or `/dev` are refused as well. |
| `STOP_TIMEOUT_SECONDS` | `30` | Graceful-stop window, used for both `docker stop -t` and `--stop-timeout`. Keep it at 30 or more: the container's supervisor waits up to 25 s for the controller's shutdown, then 2 s for the web UI ([Shutdown timing](#shutdown-timing)). |
| `READY_TIMEOUT_SECONDS` | `120` | How long to wait for `/health/ready` |
| `POLL_INTERVAL_SECONDS` | `3` | Readiness poll interval |
| `ROOF_OPERATOR_API_KEY` / `OPERATOR_KEY_FILE` | / `~/.config/hvo-roof/operator.key` | Key for the Stop and Status checks |
| `HAT_EMULATOR_ENDPOINT` | (empty) | Test rigs only: `<host>:<port>` of a HAT emulator the container can reach. The controller uses it in place of the physical HAT. See [HAT emulator mode (test rigs)](#hat-emulator-mode-test-rigs). |
| `ALLOW_EMULATED_HAT` | `false` | Must be `true` for `HAT_EMULATOR_ENDPOINT` to be accepted, and for `--rollback` to restore a version that uses the HAT emulator |

`READY_TIMEOUT_SECONDS` must be a whole number from 1 to 86400, `STOP_TIMEOUT_SECONDS` one from 30 to 86400 (the
supervisor's 25 s and 2 s, with a margin), and the ports whole numbers from 1 to 65535. They are read as decimal, so
`045` means 45. `POLL_INTERVAL_SECONDS` may have a fraction, such as `0.5`.

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
5. **Stops the old container gracefully** with `docker stop -t 30` (SIGTERM). The container's supervisor stops the
   controller first, whose shutdown path stops the roof again and ends camera streams, then the web UI. The old container is renamed `<name>-previous` with restart policy `no`, so it can
   be restored but never starts by itself. An older, stopped `<name>-previous` is removed only after this stop has
   succeeded, just before the rename, so an aborted deploy keeps it. The script reads the container states again
   after the pre-flight, and a `<name>-previous` that is running by then still aborts before anything is stopped.
6. **Starts the new container** with `--restart unless-stopped` and `--stop-timeout 30`. Inside it, the supervisor
   starts the controller and the web UI, and starts the controller again at once after
   `POST /api/v4.0/System/Restart`, which exits with code 75 ([The container's two processes](#the-containers-two-processes)).
   In HTTPS mode only `HTTPS_HOST_PORT` and `WEB_HOST_PORT` are published; plain HTTP listens on loopback inside the
   container, for the health check, the web UI's calls to the controller and the script's `docker exec` calls.
7. **Verifies the new controller:**
   - `/health/ready` within `READY_TIMEOUT_SECONDS` (from inside the container). A new controller that exits before
     it is ready fails this check at once, without waiting for the timeout: whether its container stopped or Docker
     restarted it (`--restart unless-stopped` restarts a container that exits), or the container's supervisor is
     starting it again or has left it stopped after repeated crashes (its `supervisor.json` says so). One that was
     restarted before the first check is caught when it exits again.
   - an authenticated `GET Status` inside the container returns 200 and reports the HAT this run deploys: `hatMode`
     `Physical`, or `Emulated` in [HAT emulator mode](#hat-emulator-mode-test-rigs). The secrets directory is read
     after the script's `--env` settings, so a `HatEmulator` file there could switch the HAT; this check catches it.
   - from the machine running the script, at `https://$PI_HOST:$HTTPS_HOST_PORT` (or `http://$PI_HOST:$HOST_PORT` in
     insecure mode): an authenticated `GET Status` returns 200 and `POST Stop` returns a verified stop. This proves the
     published port, the certificate, `AllowedHosts` and the key from a real client's point of view.
   - the web UI answers `/health/live` inside the container (`[verify] Web UI live inside the container (https)`),
     then from the machine running the script at `https://$PI_HOST:$WEB_HOST_PORT`, verified with `REMOTE_CA_CERT`
     (`[verify] Web UI live at ...: OK`). A web UI that cannot start fails this at once: the supervisor starts it
     again, which the script reads as an exit. A version from before the web UI (which a rollback can restore) is
     not checked for one.
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

A version from before the settings file ignores it, so the settings changed through the API are not in effect after a
rollback to one. Check them as [Upgrading to the settings file](#upgrading-to-the-settings-file) describes.

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
API automation clients (they now need `X-Api-Key` and `POST` commands). People sign in to the web UI with a name and
password ([People, sessions and managed API keys](security.md#people-sessions-and-managed-api-keys)).

## Deploying with compose

`src/HVO.RoofControllerV4.RPi/docker-compose.yaml` has two Pi profiles. Choose one; enabling both is rejected because
they share the container name `roof-controller` (which also collides with a controller started by the script).

| Profile | Transport | Needs |
|---------|-----------|-------|
| `pi` | HTTPS on `8443` (the API) and `8088` (the web UI) only. Plain HTTP listens on loopback inside the container for the health check and the web UI. | The certificate directory (`HVO_ROOF_CERT_DIR`, default `/etc/hvo-roof/https`) with `HVO_ROOF_CERT_FILE` (default `roof-controller.pfx`), and its password in the secrets directory |
| `pi-lan-http` | Plain HTTP on `8080` (the API) and `8088` (the web UI) with `RoofControllerSecurity__RequireHttps=false` | An isolated, trusted LAN: keys cross it in clear text |

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
`/etc/hvo-roof/secrets`), the identity store directory (`HVO_ROOF_IDENTITY_DIR`, default `/var/lib/hvo-roof/identity`),
the [settings directories](#4-the-settings-directories) (`HVO_ROOF_CONFIG_DIR` and `HVO_ROOF_MANAGED_SECRETS_DIR`),
`stop_grace_period: 30s`, `restart: unless-stopped`, the [supervisor](#the-containers-two-processes) with the
controller and the web UI, and `AllowedHosts` from `HVO_ROOF_ALLOWED_HOSTS`. The secrets,
certificate, identity and settings
directories must exist; compose does not create them.

Each Pi profile first runs the [deployment check](#the-deployment-check) as a one-shot service with the same environment
and mounts (`roof-controller-check` or `roof-controller-lan-http-check`), and the new controller starts only if it
exits 0. But `up` stops and replaces a running controller before the check runs, so a check that fails during `up`
leaves **no** controller running. That is why the commands above run the check on its own first, with the same
`HVO_ROOF_*` variables, and run `up` only if it passes. Read the report of the check that `up` ran with
`docker compose --profile pi logs roof-controller-check`.

Compose does **not** perform the verified stop, keep the previous container, check the published URLs or roll back.
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
with no devices, on `http://127.0.0.1:5195` (`HVO_EMULATED_ROOF_PORT`), with its web UI on `http://127.0.0.1:5196`
(`HVO_EMULATED_WEB_PORT`). It builds both images, maps no devices,
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

Both sides mount the same identity and settings directories, so people, managed keys, open sessions and settings
carry over in both directions.

`tests/emulator/deploy-scenarios.sh migration` runs both moves against the HAT emulator, with the refusals on each
side and a person whose session carries over (see [Container scenarios](emulator.md#container-scenarios)).

## The container's two processes

The container runs two processes: the controller (`HVO.RoofControllerV4.RPi`: the API, the status hub and the roof
itself) and the web UI (`HVO.RoofControllerV4.Web`, on port 8088). The web UI is a client of the controller: it
calls the controller's API over the container's loopback (`RoofWeb__ControllerUrl`, default `http://localhost:8080`),
as any other client does, and never drives the HAT itself.

A small supervisor, `/usr/local/bin/roof-supervisor` (`container/roof-supervisor.sh` in the source), starts and stops
both. `tini` is PID 1: it reaps orphaned processes and passes Docker's signals to the supervisor.

| Event | What the supervisor does |
|-------|--------------------------|
| `docker stop` (SIGTERM) | Stops the controller first and waits up to 25 s for it (`HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS`), because its shutdown stops the roof and verifies the relays off. Then it stops the web UI, waiting up to 2 s (`HVO_SUPERVISOR_UI_STOP_SECONDS`), and exits. A process that is still running when its wait runs out is killed (SIGKILL). The two waits, plus a second, must stay within the container's stop timeout (30 s: `STOP_TIMEOUT_SECONDS`, and the Compose files' `stop_grace_period`), or Docker kills the controller first. |
| The controller exits with 75 (`POST /api/v4.0/System/Restart`) | Starts it again at once. The web UI and the container keep running, and Docker's restart count does not change. |
| The controller exits otherwise (a crash) | Starts it again after 1, 2, 4 and 8 s for successive crashes (the delay doubles, at most 30 s: `HVO_SUPERVISOR_BACKOFF_MAX_SECONDS`). The fifth crash within 120 s (`HVO_SUPERVISOR_CRASH_LIMIT`, `HVO_SUPERVISOR_CRASH_WINDOW_SECONDS`) leaves the controller stopped (`crash-loop`) instead of looping while the roof may need attention. The container keeps running and its health check fails (Docker marks it unhealthy after three failed checks); the web UI says why. |
| The web UI exits | Starts only the web UI again, with the same backoff, and never gives up. The controller is not touched. |
| A forced restart (`/run/hvo-roof/control/force-restart-controller` appears) | Kills the controller (SIGKILL) and starts it again at once, even from `crash-loop`. Earlier crashes stop counting. A request within 10 s of the controller's start (`HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS`) is ignored, so repeated requests cannot kill a controller that is still starting. |

The container's own restart policy (`unless-stopped`) now applies only when the supervisor itself exits: on
`docker stop`, or at its start when a `HVO_SUPERVISOR_*` setting is invalid (exit code 2) or its run directory cannot be
created (exit code 1). Docker then starts the container again, and the supervisor's log line says why. A controller in
`crash-loop` is not restarted by Docker: a forced restart, or `docker restart roof-controller`, starts it again once the
cause is found in its log.

A forced restart kills the controller as `docker kill` would, so it carries the same guarantees
([commissioning.md C11](commissioning.md#c11-container-stop-with-an-active-camera-stream-and-the-containers-supervisor)):
the relays are held as they were until the new controller starts, which turns them all off before anything else. It is
for a controller that does not answer. It is asked for by creating `/run/hvo-roof/control/force-restart-controller`; the
control directory is writable only by the web UI's user (and root), and the supervisor checks it every second. From the
Pi:

```bash
docker exec roof-controller touch /run/hvo-roof/control/force-restart-controller
```

The web UI's control for it is on the System page, for admins only, after confirming what a kill means for the roof
([web.md](web.md#system)). The supervisor records what it did with the last request in its state
(`lastForcedRestart`: `restarted`, or `ignored` within 10 s of a start).

### The web UI's user and settings

The controller runs with the container's environment and the secrets directory, as before. The web UI does not: it
runs as the image's unprivileged `app` user, with a new environment that has only:

- its own settings, `RoofWeb__*`
- `PATH`, `TZ`, `HOME`, `USER`, the locale (`LANG`, `LC_*`), `DOTNET_*` and `ASPNETCORE_ENVIRONMENT`

So it never sees the controller's API keys, the Blue Iris credentials or any other setting, and the `app` user cannot
read the secrets directory (keep it `root:root`, mode `0700`:
[security.md](security.md#on-the-pi-docker-secrets-directory); the supervisor warns at start when `app` can read a file
there). For HTTPS, the supervisor gives the web UI private copies (mode `0400`, owned by `app`, in a directory only
root can write) of the certificate it serves and of that certificate's password:

- by default the controller's certificate (`Kestrel__Certificates__Default__Path`) and the controller's certificate
  password, from the secrets directory or the environment. The web UI then holds the controller's TLS private key: a
  compromised web UI could impersonate the controller to its clients.
- with `RoofWeb__Certificate__Path`, a certificate of the web UI's own, with the password in
  `RoofWeb__Certificate__PasswordFile` (or none). The controller's password is never given with it. Use this to keep the
  controller's key out of the web UI.

It takes them again at each start of the web UI, so a renewed certificate is used after a restart of the web UI or the
container.

The web UI's own key for Stop ([web.md](web.md#stop)) is a file as well, `RoofWeb__StopKeyFile`, so the key never
enters the web UI's environment. Point it at a Viewer key's file in the secrets directory, such as
`/run/secrets/RoofControllerSecurity__ApiKeys__2__Key` (the same file the controller reads), with
`HVO_ROOF_WEB_STOP_KEY_FILE` for Compose or `EXTRA_DOCKER_ARGS="--env RoofWeb__StopKeyFile=..."` for the deploy script.
The supervisor gives the web UI a private copy at each start of the web UI, as it does the certificate; a key file it
cannot read is logged, and the web UI's Stop then uses the person's session alone.

The web UI keeps the keys that protect its sign-in cookie and its forms in a directory only its user can read (mode
`0700`): `/var/lib/hvo-roof-web/keys` (`HVO_SUPERVISOR_UI_DATA_DIR`), or `RoofWeb__DataProtectionPath` when that is
set. The default is in the container, so people stay signed in when the web UI or the container restarts, and sign in
again after a redeploy. The supervisor makes the default as root, also when a setting names it. It makes any other
directory (named in `RoofWeb__DataProtectionPath`, or the `keys` directory under `HVO_SUPERVISOR_UI_DATA_DIR`) as the
web UI's user, not as root, so a symbolic link along the path gains that user nothing: the directory must be where
that user (the image's `app`, UID 1654) can make it, or already be one it owns (for a volume, give it to 1654 once). A
directory that cannot be made, or a symbolic link, is logged, and the web UI keeps the keys in memory, so everyone signs
in again when it restarts.

| Setting | Default | Meaning |
|---------|---------|---------|
| `RoofWeb__Urls` | `http://+:8088` | Where the web UI listens. The deploy script and the `pi` profile set `https://+:8088`, and `ALLOW_INSECURE_HTTP=true` and `pi-lan-http` set `http://+:8088`. |
| `RoofWeb__ControllerUrl` | `http://localhost:8080` | The controller's API, over loopback |
| `RoofWeb__StatusRefreshSeconds` | `2` | How often the pages check the controller's readiness and the supervisor's state, from 1 to 60 |
| `RoofWeb__Certificate__Path`, `RoofWeb__Certificate__PasswordFile` | the controller's | A certificate of the web UI's own (a `.pfx` file), and a file with its password (none when unset). A password file that cannot be read is logged, and no password is given. |
| `RoofWeb__StopKeyFile` | none | A file with the web UI's own API key for Stop, such as a Viewer key's file in the secrets directory. The web UI gets a private copy. |
| `RoofWeb__DataProtectionPath` | `/var/lib/hvo-roof-web/keys` | Where the web UI keeps the keys that protect its sign-in cookie and forms. A directory set here, or under `HVO_SUPERVISOR_UI_DATA_DIR`, is made by the web UI's user (UID 1654). |
| `RoofWeb__AllowedOrigins__N`, `RoofWeb__SignInAttemptsPerMinute`, `RoofWeb__CameraIds__N` | none, `10`, camera 2 | Other origins that may post the web UI's forms, sign-in attempts from one address a minute, and the cameras the roof page shows ([web.md](web.md#settings)) |

The web UI checks its settings at start. An invalid one stops it with
`The roof controller's web UI did not start: ...`, and the supervisor starts it again with the backoff, so a deploy
whose web UI cannot start fails its verification and rolls back.

### The supervisor's state

The supervisor writes what it is doing to `/run/hvo-roof/supervisor.json` at each change. The web UI shows it, and the
health check and the deploy script read it:

```json
{"supervisor":"running","updatedAt":"2026-09-29T12:00:00Z","crashLimit":5,"crashWindowSeconds":120,
 "forceRestartMinSeconds":10,"lastForcedRestart":{"at":"2026-09-29T11:40:12Z","outcome":"restarted"},
 "controller":{"state":"running","pid":7,"starts":2,"recentCrashes":0,"lastExitCode":75,
               "lastExitReason":"restart requested","lastExitAt":"2026-09-29T11:59:58Z"},
 "ui":{"state":"running","pid":8,"starts":1,"recentCrashes":0,"lastExitCode":null,"lastExitReason":null,"lastExitAt":null}}
```

Each process is `running`, `restarting` (waiting to start again), `crash-loop` (the controller only: left stopped),
`stopping` or `stopped`. The last exit reason is `restart requested`, `crashed (exit code N)`,
`crashed (killed by signal N)`, `forced restart` or `stopped with the container`. `lastForcedRestart` is `null` until a
forced restart is asked for; its outcome is `restarted` or `ignored` (within `forceRestartMinSeconds` of the
controller's start).

Its log lines, in `docker logs`, start with `[supervisor]`:

```text
[supervisor] Starting the controller and the web UI (crash limit 5 within 120s; backoff at most 30s; forced restarts ignored within 10s of a start; stop waits 25s for the controller, then 2s for the web UI)
[supervisor] Started the controller (pid 7, start 1)
[supervisor] Started the web UI (pid 8, start 1)
[supervisor] The controller asked to be restarted (exit code 75); starting it again
[supervisor] Stopping: the controller first, then the web UI
[supervisor] Stopping the controller (SIGTERM, up to 25s)
[supervisor] The controller stopped (exit code 0)
[supervisor] Stopping the web UI (SIGTERM, up to 2s)
[supervisor] The web UI stopped (exit code 0)
[supervisor] Stopped
```

With arguments, such as the [deployment check](#the-deployment-check)'s `--validate-deployment`, the image runs only
the controller with them, in the supervisor's place, and exits with its exit code.

## Health and readiness

One health check, `roof_controller`, backs three endpoints:

| Endpoint | Access | Answers |
|----------|--------|---------|
| `/health/live` | anonymous | 200 whenever the process answers. No check runs. |
| `/health/ready` | anonymous, status text only | 200 for Healthy or Degraded, 503 for Unhealthy. The container's health check and the deploy script's readiness wait use it, from inside the container. |
| `/health` | Viewer key or a person's session | The same result with its description and data (`HardwareMode`, `IgnorePhysicalLimitSwitches`, `HatEmulatorEndpoint` and more). 503 for Unhealthy. |

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
| Pi, Compose `pi` or `pi-lan-http` | Healthy | 200 | The deployment check before start, then `--verify-remote` from another machine ([Checking a Compose controller](#checking-a-compose-controller-from-another-machine)), which also requires `hatMode` `Physical` (or, from a version before emulator mode, no `hatMode` and `isUsingPhysicalHardware` `true`) |
| Test rig with the HAT emulator (script with `HAT_EMULATOR_ENDPOINT`, or the compose `emulator` profile) | Degraded, naming the emulator | 200 | `hatMode` `Emulated` and the `EMULATED HAT` banner ([HAT emulator mode](#hat-emulator-mode-test-rigs)) |
| No I²C bus and emulator mode off (a development machine) | Degraded, "simulation mode" | 200 | `hatMode` `Simulation`. Not a deployment: the deploy script and `--verify-remote` refuse it. |

The container's health check, `/usr/local/bin/roof-healthcheck` (the image's `HEALTHCHECK` and the Compose health
checks), is healthy exactly when the controller's `/health/ready` answers 200. It also reports the web UI's
`/health/live` (port 8088) and the [supervisor's](#the-containers-two-processes) view of both processes on the same
line, which `docker inspect` shows:

```text
controller: ready; web UI: live; supervisor: controller running, web UI running
controller: NOT READY (HTTP 000); web UI: live; supervisor: controller crash-loop, web UI running
```

The web UI never changes the result: a web UI that is down does not make the container unhealthy, and it never hides
the controller's state. Read the output with
`docker inspect --format '{{range .State.Health.Log}}{{.Output}}{{end}}' roof-controller`.

The web UI has its own anonymous `/health/live` on its port, which answers 200 whenever the web UI runs. It says
nothing about the controller: the web UI's pages show the controller's readiness and the supervisor's state.

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
| Supervisor, the controller (`HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS`) | 25 s | The [supervisor](#the-containers-two-processes) passes SIGTERM to the controller first and waits this long for it, above the app's 20 s. Then it kills it (SIGKILL). |
| Supervisor, the web UI (`HVO_SUPERVISOR_UI_STOP_SECONDS`) | 2 s | Only once the controller has exited: SIGTERM to the web UI, then SIGKILL. |
| Docker `stop_grace_period` / `--stop-timeout` / `docker stop -t` | 30 s | Must exceed the supervisor's two waits together (27 s). Docker sends SIGKILL to everything in the container after this. |

Keep each layer above the one inside it: the app's 20 s inside the supervisor's 25 s, and the supervisor's 25 s + 2 s
inside Docker's 30 s. If Docker or the supervisor kills the controller first, the controller cannot confirm that the
relays are off; they stay as they were until the next start turns them all off
([commissioning.md C11](commissioning.md#c11-container-stop-with-an-active-camera-stream-and-the-containers-supervisor)).

## After deploying: checks on the device

Run these from the deploying machine with the Pi's Docker context selected (`docker context use rpi-remote`). The key
is read from this machine:

```bash
# Container healthy and listening
docker ps --filter name=roof-controller
docker logs --tail 50 roof-controller      # look for "N API key(s) configured" and no Critical/Error security lines
docker inspect --format '{{range .State.Health.Log}}{{.Output}}{{end}}' roof-controller   # controller: ready; web UI: live; ...
docker exec roof-controller cat /run/hvo-roof/supervisor.json   # both processes running, no recent crashes

# Loopback API call from inside the container (key on stdin, never on the command line)
printf 'X-Api-Key: %s\n' "$(cat ~/.config/hvo-roof/operator.key)" \
  | docker exec -i roof-controller curl -sS -H @- http://localhost:8080/api/v4.0/RoofControl/Status

# From another machine: the certificate is served and trusted, and the API needs a key
curl -sS https://roof-pi:8443/health/ready
curl -sS -o /dev/null -w '%{http_code}\n' https://roof-pi:8443/api/v4.0/RoofControl/Status      # expect 401
curl -sS -o /dev/null -w '%{http_code}\n' http://roof-pi:8080/api/v4.0/RoofControl/Status       # expect a connection error: 8080 is not published in HTTPS mode
curl -sS https://roof-pi:8088/health/live                                                     # the web UI, with the same certificate

# The previous version, kept for --rollback (stopped, restart policy "no")
docker ps -a --filter name=roof-controller-previous
```

The deploy script already made the authenticated remote Status and Stop calls unless `SKIP_REMOTE_CHECK=true` was set.
For a Compose controller, or once the port is reachable after a deploy with `SKIP_REMOTE_CHECK=true`, make them with
`--verify-remote` ([Checking a Compose controller](#checking-a-compose-controller-from-another-machine)).
`--verify-remote` does not check the web UI: open `https://roof-pi:8088/` in a browser, or use the `curl` line above.
