# Deploying Roof Controller V4 to the Raspberry Pi

This page covers:

- preparing the Pi (secrets, TLS certificate)
- deploying with `src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh` or `docker-compose.yaml`
- how the controller is stopped safely during a deploy

For API keys, roles and the Blue Iris credentials, see [security.md](security.md).

## One-time preparation on the Pi

### 1. Secrets directory

Both the deploy script and compose bind-mount `/etc/hvo-roof/secrets` read-only at `/run/secrets`. The app reads one
file per setting from there. The file name is the setting name with `__` in place of `:`.

```
/etc/hvo-roof/secrets/                                      (mode 700)
  RoofControllerSecurity__ApiKeys__0__Name                  e.g. ipad-dome
  RoofControllerSecurity__ApiKeys__0__Role                  RoofOperator
  RoofControllerSecurity__ApiKeys__0__Key                   <random, >= 24 chars>
  RoofControllerSecurity__ApiKeys__1__...                   more keys (viewer, admin, deploy, console)
  BlueIris__UserName                                        dedicated view-only Blue Iris user
  BlueIris__Password
  Kestrel__Certificates__Default__Password                  PFX password (when using HTTPS)
```

Write each value with `printf '%s'` so that no trailing newline becomes part of it. Make the files mode `600`. See
[security.md](security.md#provisioning-api-keys) for commands. The directory must exist even if it is empty; the
pre-flight check refuses to deploy without it.

> The Blue Iris credential that used to be hard-coded in `CameraController.cs` is still in the git history. Rotate it
> before provisioning `BlueIris__UserName`/`BlueIris__Password` (see security.md).

### 2. TLS certificate (recommended)

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

Install `roof.crt`, or your CA certificate, as trusted on the iPad and on the browsers that use the console. Keep a
copy of the certificate for that purpose.

HSTS is sent only when an HTTPS endpoint is configured.

To stay on plain HTTP on an isolated, trusted network, set `RoofControllerSecurity__RequireHttps=false`. The deploy
script does this when you pass `ALLOW_INSECURE_HTTP=true`. A warning is logged at every start, because keys then
cross the network in clear text.

### 3. Operator key for the deploy script

Before replacing the running controller, the deploy script sends a Stop. That needs a key; the script uses an operator
key. Provide it on the machine that runs the script, in one of two ways:

- an environment variable, `ROOF_OPERATOR_API_KEY`
- a file, `~/.config/hvo-roof/operator.key`, with mode `600`

The script refuses a key file that is readable by group or others.

## Deploying with the script

```bash
cd src/HVO.RoofControllerV4.RPi
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https ./deploy-roofcontroller-rpi.sh --dry-run
PI_HOST=roof-pi HTTPS_CERT_DIR=/etc/hvo-roof/https ALLOWED_HOSTS="roof-pi;roof-pi.local;localhost" \
  ./deploy-roofcontroller-rpi.sh
```

| Flag | Effect |
|------|--------|
| `--dry-run` | Checks the Docker context, the key and the HTTPS choice. If the controller is running, it reads `GET Status` over loopback and prints the relay state. It then prints the plan. Nothing is built or changed. |
| `--force-unverified-stop` | Lets the deploy continue when the Stop cannot be verified, but only after you type `STOP-UNVERIFIED` at the terminal. See the steps below. |

| Variable | Default | Meaning |
|----------|---------|---------|
| `PI_HOST` | (required) | Used in messages |
| `DOCKER_CONTEXT` | `rpi-remote` | Docker context that targets the Pi |
| `IMAGE_TAG` | `hvov9/roof-controller:v4` | Image tag |
| `CONTAINER_NAME` | `roof-controller` | Container name |
| `HOST_PORT` / `HTTPS_HOST_PORT` | `8080` / `8443` | Published ports |
| `SECRETS_DIR` | `/etc/hvo-roof/secrets` | Secrets directory on the Pi |
| `HTTPS_CERT_DIR` | (empty) | Certificate directory on the Pi. Required unless `ALLOW_INSECURE_HTTP=true`. |
| `HTTPS_CERT_FILE` | `roof-controller.pfx` | PFX file name inside `HTTPS_CERT_DIR` |
| `ALLOW_INSECURE_HTTP` | `false` | Sets `RoofControllerSecurity__RequireHttps=false` |
| `ALLOWED_HOSTS` | (empty; image default `*`) | Sets `AllowedHosts` |
| `STOP_TIMEOUT_SECONDS` | `30` | Graceful-stop window, used for both `docker stop -t` and `--stop-timeout` |
| `READY_TIMEOUT_SECONDS` | `120` | How long to wait for `/health/ready` |
| `ROOF_OPERATOR_API_KEY` / `OPERATOR_KEY_FILE` | / `~/.config/hvo-roof/operator.key` | Key for the pre-deploy Stop |

### What the script does, in order

1. **Checks what it needs.** The script stops at the first failure:
   - the Docker context is available
   - either an HTTPS certificate directory is set or insecure HTTP was chosen explicitly
   - an operator key is available
2. **Builds** the arm64 image and loads it on the Pi.
3. **Runs a pre-flight check** with the new image (`docker run --rm --entrypoint true` with the same devices and
   mounts). A missing `/dev/gpiomem`, `/dev/i2c-1`, secrets directory or certificate directory fails here, while the
   old controller is still running and untouched.
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
   again and ends camera streams. A stopped container is only removed; the new controller turns all relays off when
   it initializes.
6. **Starts the new container** with `--restart unless-stopped` and `--stop-timeout 30`.
7. **Waits for readiness.** The script polls `/health/ready` from inside the container. If the controller is not ready
   within `READY_TIMEOUT_SECONDS`, the script prints the last 60 log lines and exits non-zero. That means the roof
   cannot be controlled remotely until the fault is fixed.

`jq` or `python3` is needed on the machine that runs the script to parse the Stop response. Without either, the stop
is treated as unverified.

### First deploy over an older controller

Controllers built before this change have no API keys and no `POST` Stop route. Stop was `GET` and unauthenticated.
Their Stop response also lacks `relayRegisterState` and `commandedMotion`. So the verified stop always fails against
them. For that one deploy:

1. Make sure the roof is closed and idle, and watch it (camera or on site).
2. Run the script with `--force-unverified-stop` and type `STOP-UNVERIFIED` when asked.
3. The old container still gets SIGTERM with the 30-second grace period.

Before that deploy, provision `/etc/hvo-roof/secrets`, including the operator key used by the script. Then update the
iPad and any other clients, because they now need keys and `POST` commands.

## Deploying with compose

`src/HVO.RoofControllerV4.RPi/docker-compose.yaml` (profile `pi`) mirrors the script:

- the secrets directory is mounted at `/run/secrets` (`HVO_ROOF_SECRETS_DIR`, default `/etc/hvo-roof/secrets`)
- `stop_grace_period: 30s`
- `AllowedHosts` comes from `HVO_ROOF_ALLOWED_HOSTS`
- `RoofControllerSecurity__RequireHttps` comes from `HVO_ROOF_REQUIRE_HTTPS`, default `true`

To enable HTTPS, uncomment these lines in the file:

- the `8443` port
- `ASPNETCORE_URLS`
- `Kestrel__Certificates__Default__Path`
- the certificate mount (`HVO_ROOF_CERT_DIR`, default `/etc/hvo-roof/https`)

Compose does **not** perform the verified stop. Before `docker compose --profile pi up -d --build` replaces a running
controller, stop the roof yourself with `POST .../Stop` and check the response.

## Shutdown timing

| Layer | Value | Purpose |
|-------|-------|---------|
| App `HostOptions.ShutdownTimeout` | 20 s | Time the host gives hosted services to stop. The roof controller's shutdown (Stop, then verify all relays off) runs here. |
| Camera streams | end at `ApplicationStopping` | An open viewer never holds up shutdown |
| Docker `stop_grace_period` / `--stop-timeout` / `docker stop -t` | 30 s | Must exceed the app's timeout. Docker sends SIGKILL after this. |

Keep the Docker value above the app value. If Docker kills the process first, the controller cannot confirm that the
relays are off.

## After deploying: checks on the device

Run these checks on the Pi, or through the Docker context:

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
curl -sS -o /dev/null -w '%{http_code}\n' http://roof-pi:8080/api/v4.0/RoofControl/Status       # expect 403 (https_required)
```
