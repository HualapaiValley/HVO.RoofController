#!/usr/bin/env bash
set -euo pipefail

# Project-local deploy script for Roof Controller V4 (RPi). See docs/deployment.md.
#
# 1. Pre-flight: the new image runs --validate-deployment on the Pi with the final container's environment, devices,
#    secrets and certificate mounts (roof options, a usable RoofOperator/RoofAdmin key, this script's key, the
#    HTTPS listener and certificate). If it fails, the running controller is not touched.
# 2. The running controller is replaced only after a VERIFIED stop: POST /Stop (from inside the container, over
#    loopback) must return 200 with relayRegisterState=Verified, relayRegisterMask=0 and commandedMotion=None.
#    Anything else aborts, unless --force-unverified-stop is given AND the operator types a confirmation. The old
#    container is stopped gracefully (SIGTERM) and kept, not started, as <name>-previous.
# 3. The new controller must become ready, answer an authenticated Status inside the container, and answer an
#    authenticated Status and a verified Stop from this machine at the published URL (HTTPS unless
#    ALLOW_INSECURE_HTTP=true). Otherwise it is stopped and removed, and <name>-previous is restored and started.
#
# Usage: PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh [--dry-run] [--force-unverified-stop] [--rollback]
#   --rollback  swaps the running controller with <name>-previous (after the same verified stop) and checks it
#               as in step 3. Run it again to swap back.

usage() {
  sed -n '/^# Project-local/,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

DRY_RUN=false
FORCE_UNVERIFIED_STOP=false
ROLLBACK=false
for arg in "$@"; do
  case "${arg}" in
    --dry-run) DRY_RUN=true ;;
    --force-unverified-stop) FORCE_UNVERIFIED_STOP=true ;;
    --rollback) ROLLBACK=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: ${arg}" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "${PI_HOST:-}" ]]; then
  echo "PI_HOST environment variable is required" >&2
  exit 1
fi

DOCKER_CONTEXT=${DOCKER_CONTEXT:-rpi-remote}
IMAGE_TAG=${IMAGE_TAG:-hvov9/roof-controller:v4}
CONTAINER_NAME=${CONTAINER_NAME:-roof-controller}
PREVIOUS_CONTAINER_NAME="${CONTAINER_NAME}-previous"
HOST_PORT=${HOST_PORT:-8080}
HTTPS_HOST_PORT=${HTTPS_HOST_PORT:-8443}
# Extra `docker run` arguments for the controller; also applied to the pre-flight container, so do not publish
# ports here (use HOST_PORT / HTTPS_HOST_PORT).
EXTRA_DOCKER_ARGS=${EXTRA_DOCKER_ARGS:-}
HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI:-true}
IGNORE_PHYSICAL_LIMIT_SWITCHES=${IGNORE_PHYSICAL_LIMIT_SWITCHES:-false}
OTEL_SERVICE_NAME=${OTEL_SERVICE_NAME:-hvo-roof-controller}
OTEL_SERVICE_INSTANCE_ID=${OTEL_SERVICE_INSTANCE_ID:-roof-controller-rpi}
OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT:-http://192.168.1.238:4318}
OTEL_EXPORTER_OTLP_PROTOCOL=${OTEL_EXPORTER_OTLP_PROTOCOL:-http/protobuf}
OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL:-10000}

# Directory ON THE PI holding one file per secret setting (API keys, Blue Iris password, certificate password),
# mounted read-only at /run/secrets. See docs/security.md for the file names.
SECRETS_DIR=${SECRETS_DIR:-/etc/hvo-roof/secrets}
# Directory ON THE PI holding the TLS certificate (PFX). Required unless ALLOW_INSECURE_HTTP=true.
HTTPS_CERT_DIR=${HTTPS_CERT_DIR:-}
HTTPS_CERT_FILE=${HTTPS_CERT_FILE:-roof-controller.pfx}
ALLOW_INSECURE_HTTP=${ALLOW_INSECURE_HTTP:-false}
# Semicolon-separated host names/IPs clients use (AllowedHosts). Empty keeps the image default.
ALLOWED_HOSTS=${ALLOWED_HOSTS:-}
STOP_TIMEOUT_SECONDS=${STOP_TIMEOUT_SECONDS:-30}
READY_TIMEOUT_SECONDS=${READY_TIMEOUT_SECONDS:-120}
POLL_INTERVAL_SECONDS=${POLL_INTERVAL_SECONDS:-3}

# Remote check from this machine after the switch. REMOTE_CA_CERT is a PEM file (on this machine) that verifies the
# Pi's certificate when it is not signed by a CA this machine trusts, e.g. the self-signed certificate itself.
REMOTE_CA_CERT=${REMOTE_CA_CERT:-}
SKIP_REMOTE_CHECK=${SKIP_REMOTE_CHECK:-false}

# Key for the Stop and Status checks (normally the operator key). Passed to curl on stdin, never as an argument.
OPERATOR_KEY_FILE=${OPERATOR_KEY_FILE:-${HOME}/.config/hvo-roof/operator.key}

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "${SCRIPT_DIR}/../.." && pwd)
DOCKERFILE_PATH="${SCRIPT_DIR}/Dockerfile"
API_PATH="api/v4.0/RoofControl"

if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  REMOTE_BASE_URL="https://${PI_HOST}:${HTTPS_HOST_PORT}"
else
  REMOTE_BASE_URL="http://${PI_HOST}:${HOST_PORT}"
fi

# While the new controller is not yet verified, what a failure restores: none (no old controller), stopped (rename
# <name>-previous back, leave it stopped) or running (rename it back and start it). Empty outside the switch; the
# EXIT trap restores when the script dies during the switch.
RESTORE_MODE=""
FAILURE=""
TMP_TAR=""

dockerc() {
  docker --context "${DOCKER_CONTEXT}" "$@"
}

fail() {
  echo "[deploy] ERROR: $*" >&2
  exit 1
}

file_mode() {
  stat -c '%a' "$1" 2>/dev/null || stat -f '%Lp' "$1"
}

sha256_hex() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 | cut -d ' ' -f 1
  else
    openssl dgst -sha256 -r | cut -d ' ' -f 1
  fi
}

resolve_operator_key() {
  if [[ -n "${ROOF_OPERATOR_API_KEY:-}" ]]; then
    OPERATOR_KEY=${ROOF_OPERATOR_API_KEY}
    return
  fi

  if [[ -f "${OPERATOR_KEY_FILE}" ]]; then
    local mode
    mode=$(file_mode "${OPERATOR_KEY_FILE}")
    if [[ "${mode: -2}" != "00" ]]; then
      fail "${OPERATOR_KEY_FILE} is readable by group/others (mode ${mode}). Run: chmod 600 '${OPERATOR_KEY_FILE}'"
    fi
    OPERATOR_KEY=$(tr -d '\r\n' < "${OPERATOR_KEY_FILE}")
    [[ -n "${OPERATOR_KEY}" ]] || fail "${OPERATOR_KEY_FILE} is empty"
    return
  fi

  fail "No API key for the Stop and Status checks. Set ROOF_OPERATOR_API_KEY or create ${OPERATOR_KEY_FILE} (mode 600)."
}

container_state() {
  # Prints running | stopped | missing
  local name=${1:-${CONTAINER_NAME}} running
  if ! running=$(dockerc inspect -f '{{.State.Running}}' "${name}" 2>/dev/null); then
    echo missing
  elif [[ "${running}" == "true" ]]; then
    echo running
  else
    echo stopped
  fi
}

# Calls the API from inside the container over loopback (exempt from the HTTPS requirement). The key is passed on
# stdin (curl -H @-) so it never appears in a process list. Prints the body, then the HTTP status on the last line.
container_api() {
  local method=$1 path=$2 name=${3:-${CONTAINER_NAME}}
  printf 'X-Api-Key: %s\n' "${OPERATOR_KEY}" \
    | dockerc exec -i "${name}" \
        curl -sS --max-time 15 -X "${method}" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "http://localhost:8080/${API_PATH}/${path}"
}

# Calls the API from this machine at the published URL, as a remote client would. Same output as container_api.
remote_api() {
  local method=$1 path=$2
  local tls_args=()
  if [[ -n "${REMOTE_CA_CERT}" ]]; then
    tls_args=(--cacert "${REMOTE_CA_CERT}")
  fi
  printf 'X-Api-Key: %s\n' "${OPERATOR_KEY}" \
    | curl -sS --max-time 15 ${tls_args[@]+"${tls_args[@]}"} -X "${method}" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "${REMOTE_BASE_URL}/${API_PATH}/${path}"
}

# Prints "<relayRegisterState>\t<relayRegisterMask>\t<commandedMotion>" from a status JSON on stdin.
parse_status() {
  if command -v jq >/dev/null 2>&1; then
    jq -r '[(.relayRegisterState // "null"), (.relayRegisterMask // "null" | tostring), (.commandedMotion // "null")] | @tsv'
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import json,sys
d=json.load(sys.stdin)
v=lambda k: "null" if d.get(k) is None else str(d.get(k))
print(v("relayRegisterState"), v("relayRegisterMask"), v("commandedMotion"), sep="\t")'
  else
    return 1
  fi
}

# Returns 0 only for HTTP 200 + Verified + mask 0 + commanded motion None.
check_verified_stop() {
  local response=$1 http_status body parsed state mask motion
  http_status=$(tail -n 1 <<<"${response}")
  body=$(sed '$d' <<<"${response}")

  if [[ "${http_status}" != "200" ]]; then
    echo "[deploy] Stop returned HTTP ${http_status:-<none>}: ${body}" >&2
    return 1
  fi

  if ! parsed=$(parse_status <<<"${body}"); then
    echo "[deploy] Cannot parse the Stop response (install jq or python3); treating the stop as unverified." >&2
    return 1
  fi

  IFS=$'\t' read -r state mask motion <<<"${parsed}"
  echo "[deploy] Stop result: relayRegisterState=${state} relayRegisterMask=${mask} commandedMotion=${motion}"
  [[ "${state}" == "Verified" && "${mask}" == "0" && "${motion}" == "None" ]]
}

confirm_unverified_stop() {
  if [[ "${FORCE_UNVERIFIED_STOP}" != "true" ]]; then
    fail "The roof stop could not be verified. Check the roof and relays, then retry. To replace the controller anyway, rerun with --force-unverified-stop (interactive confirmation required)."
  fi

  if ! { exec 3</dev/tty; } 2>/dev/null; then
    fail "--force-unverified-stop needs an interactive terminal for the confirmation."
  fi

  echo "[deploy] WARNING: the roof stop is NOT verified. Relays may still be energized while the controller is replaced." >&2
  echo "[deploy] Confirm you can SEE the roof and it is not moving (or the drive is isolated)." >&2
  local answer
  printf 'Type STOP-UNVERIFIED to continue: ' >&2
  read -r -u 3 answer || answer=""
  exec 3<&-
  [[ "${answer}" == "STOP-UNVERIFIED" ]] || fail "Confirmation not given; deployment aborted."
}

stop_roof_before_replacing() {
  echo "[deploy] Requesting a verified roof stop from ${CONTAINER_NAME}"
  local response
  if ! response=$(container_api POST Stop); then
    echo "[deploy] Could not call Stop inside ${CONTAINER_NAME}." >&2
    confirm_unverified_stop
    return
  fi

  if check_verified_stop "${response}"; then
    echo "[deploy] Roof stop verified (relay register all off)."
  else
    confirm_unverified_stop
  fi
}

# Makes sure <name>-previous can take the current controller: an older stopped one is removed, a running one aborts.
clear_previous_slot() {
  case "$(container_state "${PREVIOUS_CONTAINER_NAME}")" in
    running)
      fail "${PREVIOUS_CONTAINER_NAME} is running: two controllers must never share the HAT. Stop it (docker stop ${PREVIOUS_CONTAINER_NAME}) and retry."
      ;;
    stopped)
      echo "[deploy] Removing the older ${PREVIOUS_CONTAINER_NAME}"
      dockerc rm "${PREVIOUS_CONTAINER_NAME}" >/dev/null
      ;;
  esac
}

wait_ready() {
  local name=$1 deadline=$((SECONDS + READY_TIMEOUT_SECONDS))
  while (( SECONDS < deadline )); do
    if [[ "$(container_state "${name}")" != "running" ]]; then
      return 1
    fi
    if dockerc exec "${name}" curl -fsS --max-time 5 http://localhost:8080/health/ready >/dev/null 2>&1; then
      return 0
    fi
    sleep "${POLL_INTERVAL_SECONDS}"
  done
  return 1
}

# Readiness, an authenticated Status inside the container, then an authenticated Status and a verified Stop from this
# machine. Sets FAILURE and returns 1 on the first check that fails.
verify_controller() {
  local name=$1 response http_status
  echo "[verify] Waiting up to ${READY_TIMEOUT_SECONDS}s for ${name} to report /health/ready"
  if ! wait_ready "${name}"; then
    FAILURE="${name} did not become ready within ${READY_TIMEOUT_SECONDS}s"
    return 1
  fi

  if ! response=$(container_api GET Status "${name}"); then
    FAILURE="the authenticated Status call inside ${name} failed"
    return 1
  fi
  http_status=$(tail -n 1 <<<"${response}")
  if [[ "${http_status}" != "200" ]]; then
    FAILURE="the authenticated Status call inside ${name} returned HTTP ${http_status:-<none>} (is this script's key configured?)"
    return 1
  fi
  echo "[verify] Ready; authenticated Status inside the container: HTTP 200"

  if [[ "${SKIP_REMOTE_CHECK}" == "true" ]]; then
    echo "[verify] WARNING: SKIP_REMOTE_CHECK=true: ${REMOTE_BASE_URL} was not checked from this machine. Check it from a client before relying on remote control." >&2
    return 0
  fi

  if ! response=$(remote_api GET Status); then
    FAILURE="GET Status at ${REMOTE_BASE_URL} failed from this machine (network, port or TLS; set REMOTE_CA_CERT for a certificate this machine does not trust)"
    return 1
  fi
  http_status=$(tail -n 1 <<<"${response}")
  if [[ "${http_status}" != "200" ]]; then
    FAILURE="GET Status at ${REMOTE_BASE_URL} returned HTTP ${http_status:-<none>} to this machine: $(sed '$d' <<<"${response}")"
    return 1
  fi

  if ! response=$(remote_api POST Stop) || ! check_verified_stop "${response}"; then
    FAILURE="POST Stop at ${REMOTE_BASE_URL} did not return a verified stop to this machine"
    return 1
  fi
  echo "[verify] Remote Status and verified Stop at ${REMOTE_BASE_URL}: OK"
}

show_containers() {
  dockerc ps -a --filter "name=${CONTAINER_NAME}" --format "table {{.Names}}\t{{.Status}}\t{{.Image}}" || true
}

# Removes the new controller and brings <name>-previous back as <name>, as it was before the deploy (RESTORE_MODE).
# Always exits non-zero.
restore_previous() {
  local reason=$1 mode=${RESTORE_MODE}
  RESTORE_MODE=""
  echo "[rollback] ${reason}" >&2

  if [[ "$(container_state "${CONTAINER_NAME}")" != "missing" ]]; then
    echo "[rollback] Last log lines of the new controller:" >&2
    dockerc logs --tail 60 "${CONTAINER_NAME}" >&2 || true
    echo "[rollback] Stopping and removing the new controller (SIGTERM first, so it stops the roof)" >&2
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null 2>&1 || true
    dockerc rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  fi

  if [[ "${mode}" == "none" ]]; then
    fail "Deployment failed (${reason}). There was no previous controller to restore: the roof controller is NOT running and the roof cannot be controlled remotely until this is fixed."
  fi

  echo "[rollback] Restoring ${PREVIOUS_CONTAINER_NAME} as ${CONTAINER_NAME}" >&2
  if ! { dockerc rename "${PREVIOUS_CONTAINER_NAME}" "${CONTAINER_NAME}" \
      && dockerc update --restart unless-stopped "${CONTAINER_NAME}" >/dev/null; }; then
    show_containers >&2
    fail "Deployment failed (${reason}) and ${PREVIOUS_CONTAINER_NAME} could not be restored. The roof controller is NOT running."
  fi

  if [[ "${mode}" == "stopped" ]]; then
    show_containers
    fail "Deployment failed (${reason}). Rolled back: the previous controller is restored as ${CONTAINER_NAME}, stopped as it was before the deploy."
  fi

  if ! dockerc start "${CONTAINER_NAME}" >/dev/null; then
    show_containers >&2
    fail "Deployment failed (${reason}) and the previous controller could not be restarted. The roof controller is NOT running."
  fi

  if wait_ready "${CONTAINER_NAME}"; then
    show_containers
    fail "Deployment failed (${reason}). Rolled back: the previous controller is running and ready."
  fi

  dockerc logs --tail 60 "${CONTAINER_NAME}" >&2 || true
  fail "Deployment failed (${reason}). The previous controller was restarted but did not become ready. The roof cannot be controlled remotely until this is fixed."
}

on_exit() {
  local status=$?
  if [[ -n "${TMP_TAR}" ]]; then
    rm -f "${TMP_TAR}"
  fi
  if [[ -n "${RESTORE_MODE}" ]]; then
    restore_previous "the deploy stopped unexpectedly (exit ${status}) while switching controllers"
  fi
}
trap on_exit EXIT

if ! dockerc info >/dev/null 2>&1; then
  fail "Docker context '${DOCKER_CONTEXT}' is not available. Configure it first (e.g. docker context use ${DOCKER_CONTEXT})."
fi

if [[ "${ROLLBACK}" != "true" && -z "${HTTPS_CERT_DIR}" && "${ALLOW_INSECURE_HTTP}" != "true" ]]; then
  fail "Set HTTPS_CERT_DIR (directory on the Pi with ${HTTPS_CERT_FILE}) or ALLOW_INSECURE_HTTP=true. Without HTTPS, API keys cross the network in clear text and LAN clients get 403 https_required unless RequireHttps is disabled. See docs/deployment.md."
fi

if [[ -n "${REMOTE_CA_CERT}" && ! -r "${REMOTE_CA_CERT}" ]]; then
  fail "REMOTE_CA_CERT '${REMOTE_CA_CERT}' is not a readable file on this machine."
fi

resolve_operator_key
STATE=$(container_state)
echo "[deploy] Existing container ${CONTAINER_NAME}: ${STATE}; ${PREVIOUS_CONTAINER_NAME}: $(container_state "${PREVIOUS_CONTAINER_NAME}")"

if [[ "${DRY_RUN}" == "true" ]]; then
  if [[ "${STATE}" == "running" ]]; then
    response=$(container_api GET Status) || fail "Could not read Status inside ${CONTAINER_NAME}."
    http_status=$(tail -n 1 <<<"${response}")
    echo "[dry-run] GET Status inside ${CONTAINER_NAME} -> HTTP ${http_status}"
    if [[ "${http_status}" == "200" ]]; then
      parsed=$(sed '$d' <<<"${response}" | parse_status) || fail "Cannot parse Status (install jq or python3)."
      IFS=$'\t' read -r state mask motion <<<"${parsed}"
      echo "[dry-run] relayRegisterState=${state} relayRegisterMask=${mask} commandedMotion=${motion}"
    fi
    if [[ "${SKIP_REMOTE_CHECK}" != "true" ]]; then
      if response=$(remote_api GET Status); then
        echo "[dry-run] GET Status at ${REMOTE_BASE_URL} from this machine -> HTTP $(tail -n 1 <<<"${response}")"
      else
        echo "[dry-run] GET Status at ${REMOTE_BASE_URL} from this machine failed (expected if the current controller uses another URL)."
      fi
    fi
  fi
  if [[ "${ROLLBACK}" == "true" ]]; then
    echo "[dry-run] Would request a verified Stop, swap ${CONTAINER_NAME} with ${PREVIOUS_CONTAINER_NAME} and verify it at ${REMOTE_BASE_URL}."
  else
    echo "[dry-run] Would build ${IMAGE_TAG}, run the pre-flight check on the Pi, request a verified Stop, stop ${CONTAINER_NAME} (-t ${STOP_TIMEOUT_SECONDS}) and keep it as ${PREVIOUS_CONTAINER_NAME}, start the new container and verify it (ready within ${READY_TIMEOUT_SECONDS}s, then Status and Stop at ${REMOTE_BASE_URL}), rolling back on failure."
  fi
  echo "[dry-run] Secrets dir on Pi: ${SECRETS_DIR}; HTTPS cert dir: ${HTTPS_CERT_DIR:-<none, insecure HTTP>}"
  exit 0
fi

if [[ "${ROLLBACK}" == "true" ]]; then
  [[ "$(container_state "${PREVIOUS_CONTAINER_NAME}")" == "stopped" ]] \
    || fail "There is no stopped ${PREVIOUS_CONTAINER_NAME} to roll back to."

  if [[ "${STATE}" == "running" ]]; then
    stop_roof_before_replacing
    echo "[rollback] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null
  fi

  echo "[rollback] Swapping ${CONTAINER_NAME} and ${PREVIOUS_CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${CONTAINER_NAME}" "${CONTAINER_NAME}-swap"
    dockerc update --restart no "${CONTAINER_NAME}-swap" >/dev/null
  fi
  dockerc rename "${PREVIOUS_CONTAINER_NAME}" "${CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${CONTAINER_NAME}-swap" "${PREVIOUS_CONTAINER_NAME}"
  fi
  dockerc update --restart unless-stopped "${CONTAINER_NAME}" >/dev/null
  dockerc start "${CONTAINER_NAME}" >/dev/null

  if ! verify_controller "${CONTAINER_NAME}"; then
    show_containers
    fail "The rolled-back controller did not pass verification: ${FAILURE}. It is left running. Run --rollback again to swap back."
  fi
  show_containers
  echo "[done] Rolled back. ${CONTAINER_NAME} is verified at ${REMOTE_BASE_URL}; the replaced version is kept as ${PREVIOUS_CONTAINER_NAME}."
  exit 0
fi

if ! docker buildx version >/dev/null 2>&1; then
  fail "docker buildx is required but not available. Install Docker Buildx and try again."
fi

echo "[build] Building ${IMAGE_TAG} for linux/arm64..."
docker buildx build \
  --platform linux/arm64 \
  -f "${DOCKERFILE_PATH}" \
  -t "${IMAGE_TAG}" \
  --load \
  "${REPO_ROOT}"

TMP_TAR=$(mktemp)
docker save "${IMAGE_TAG}" -o "${TMP_TAR}"

echo "[deploy] Loading image into Docker context '${DOCKER_CONTEXT}'"
dockerc load < "${TMP_TAR}"

# Environment, devices and mounts of the controller container. The pre-flight check runs with exactly these.
# shellcheck disable=SC2054 # commas are part of --mount values
container_args=(
  --env "HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI}"
  --env "RoofControllerOptionsV4__IgnorePhysicalLimitSwitches=${IGNORE_PHYSICAL_LIMIT_SWITCHES}"
  --env "OTEL_SERVICE_NAME=${OTEL_SERVICE_NAME}"
  --env "OTEL_RESOURCE_ATTRIBUTES=service.instance.id=${OTEL_SERVICE_INSTANCE_ID}"
  --env "OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT}"
  --env "OTEL_EXPORTER_OTLP_PROTOCOL=${OTEL_EXPORTER_OTLP_PROTOCOL}"
  --env "OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL}"
  --device /dev/gpiomem:/dev/gpiomem
  --device /dev/i2c-1:/dev/i2c-1
  --mount type=bind,src=/sys/class/thermal/thermal_zone0/temp,dst=/sys/class/thermal/thermal_zone0/temp,readonly
  --mount "type=bind,src=${SECRETS_DIR},dst=/run/secrets,readonly"
)

if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  # Remote clients use HTTPS only; plain HTTP listens on loopback inside the container (health check, Stop calls).
  publish_args=(-p "${HTTPS_HOST_PORT}:8443")
  container_args+=(
    --mount "type=bind,src=${HTTPS_CERT_DIR},dst=/https,readonly"
    --env "ASPNETCORE_URLS=http://localhost:8080;https://+:8443"
    --env "Kestrel__Certificates__Default__Path=/https/${HTTPS_CERT_FILE}"
    --env "RoofControllerSecurity__RequireHttps=true"
  )
else
  publish_args=(-p "${HOST_PORT}:8080")
  container_args+=(
    --env "ASPNETCORE_URLS=http://+:8080"
    --env "RoofControllerSecurity__RequireHttps=false"
  )
fi

if [[ -n "${ALLOWED_HOSTS}" ]]; then
  container_args+=(--env "AllowedHosts=${ALLOWED_HOSTS}")
fi

if [[ -n "${EXTRA_DOCKER_ARGS}" ]]; then
  # shellcheck disable=SC2206
  extra_args=(${EXTRA_DOCKER_ARGS})
  container_args+=("${extra_args[@]}")
fi

# Pre-flight on the Pi with the new image and the final container's configuration, before touching the running
# controller. Docker checks the devices and mount sources; --validate-deployment checks the configuration (it does
# not open the HAT). The key's SHA-256 lets it confirm this script's key is configured without passing the key.
echo "[deploy] Pre-flight: validating the new container's configuration on the Pi"
deploy_key_sha256=$(printf '%s' "${OPERATOR_KEY}" | sha256_hex)
if ! dockerc run --rm "${container_args[@]}" --env "DeploymentCheck__DeployKeySha256=${deploy_key_sha256}" \
    "${IMAGE_TAG}" --validate-deployment; then
  fail "Pre-flight failed (see above). The running controller was not touched."
fi

case "${STATE}" in
  running)
    clear_previous_slot
    stop_roof_before_replacing
    echo "[deploy] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    if ! dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null; then
      show_containers >&2
      fail "Could not stop ${CONTAINER_NAME}; it was not replaced. Check its state before retrying."
    fi
    ;;
  stopped)
    clear_previous_slot
    echo "[deploy] ${CONTAINER_NAME} is not running (nothing to stop). The new controller turns all relays off when it initializes."
    ;;
  missing)
    # Any existing <name>-previous is left alone.
    echo "[deploy] No existing container."
    ;;
esac

if [[ "${STATE}" == "missing" ]]; then
  RESTORE_MODE=none
else
  echo "[deploy] Keeping the old controller as ${PREVIOUS_CONTAINER_NAME} (not restarted automatically)"
  if ! dockerc rename "${CONTAINER_NAME}" "${PREVIOUS_CONTAINER_NAME}"; then
    if [[ "${STATE}" == "running" ]]; then
      dockerc start "${CONTAINER_NAME}" >/dev/null 2>&1 || true
    fi
    show_containers >&2
    fail "Could not rename ${CONTAINER_NAME} to ${PREVIOUS_CONTAINER_NAME}; it was not replaced."
  fi
  RESTORE_MODE=${STATE}
  dockerc update --restart no "${PREVIOUS_CONTAINER_NAME}" >/dev/null
fi

echo "[deploy] Starting the new container on ${PI_HOST}"
if ! dockerc run -d --name "${CONTAINER_NAME}" --restart unless-stopped --stop-timeout "${STOP_TIMEOUT_SECONDS}" \
    --log-driver local --log-opt max-size=10m --log-opt max-file=5 \
    "${publish_args[@]}" "${container_args[@]}" "${IMAGE_TAG}" >/dev/null; then
  restore_previous "docker run failed for the new controller"
fi

if ! verify_controller "${CONTAINER_NAME}"; then
  restore_previous "${FAILURE}"
fi
RESTORE_MODE=""

echo "[deploy] Container status"
show_containers
echo "[done] Deployment complete and verified at ${REMOTE_BASE_URL}. The previous version is kept as ${PREVIOUS_CONTAINER_NAME}; run with --rollback to return to it."
