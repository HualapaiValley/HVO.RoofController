#!/usr/bin/env bash
set -euo pipefail

# Project-local deploy script for Roof Controller V4 (RPi). See docs/deployment.md.
#
# The running controller is only replaced after a VERIFIED stop: POST /Stop (from inside the container, over
# loopback) must return 200 with relayRegisterState=Verified, relayRegisterMask=0 and commandedMotion=None.
# Anything else aborts the deploy, unless --force-unverified-stop is given AND the operator types a confirmation.
# The old container is then stopped gracefully (SIGTERM, 30 s) so the host's shutdown path can also stop the roof.
#
# Usage: PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh [--dry-run] [--force-unverified-stop]

usage() {
  sed -n '4,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

DRY_RUN=false
FORCE_UNVERIFIED_STOP=false
for arg in "$@"; do
  case "${arg}" in
    --dry-run) DRY_RUN=true ;;
    --force-unverified-stop) FORCE_UNVERIFIED_STOP=true ;;
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
HOST_PORT=${HOST_PORT:-8080}
HTTPS_HOST_PORT=${HTTPS_HOST_PORT:-8443}
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

# Key used for the pre-deploy Stop (any role works for Stop; normally the operator key).
OPERATOR_KEY_FILE=${OPERATOR_KEY_FILE:-${HOME}/.config/hvo-roof/operator.key}

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "${SCRIPT_DIR}/../.." && pwd)
DOCKERFILE_PATH="${SCRIPT_DIR}/Dockerfile"
API_BASE="http://localhost:8080/api/v4.0/RoofControl"

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

  fail "No API key for the pre-deploy Stop. Set ROOF_OPERATOR_API_KEY or create ${OPERATOR_KEY_FILE} (mode 600)."
}

container_state() {
  # Prints running | stopped | missing
  local running
  if ! running=$(dockerc inspect -f '{{.State.Running}}' "${CONTAINER_NAME}" 2>/dev/null); then
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
  local method=$1 path=$2
  printf 'X-Api-Key: %s\n' "${OPERATOR_KEY}" \
    | dockerc exec -i "${CONTAINER_NAME}" \
        curl -sS --max-time 15 -X "${method}" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "${API_BASE}/${path}"
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

if ! dockerc info >/dev/null 2>&1; then
  fail "Docker context '${DOCKER_CONTEXT}' is not available. Configure it first (e.g. docker context use ${DOCKER_CONTEXT})."
fi

if [[ -z "${HTTPS_CERT_DIR}" && "${ALLOW_INSECURE_HTTP}" != "true" ]]; then
  fail "Set HTTPS_CERT_DIR (directory on the Pi with ${HTTPS_CERT_FILE}) or ALLOW_INSECURE_HTTP=true. Without HTTPS, API keys cross the network in clear text and LAN clients get 403 https_required unless RequireHttps is disabled. See docs/deployment.md."
fi

resolve_operator_key
STATE=$(container_state)
echo "[deploy] Existing container ${CONTAINER_NAME}: ${STATE}"

if [[ "${DRY_RUN}" == "true" ]]; then
  if [[ "${STATE}" == "running" ]]; then
    response=$(container_api GET Status) || fail "Could not read Status inside ${CONTAINER_NAME}."
    http_status=$(tail -n 1 <<<"${response}")
    echo "[dry-run] GET Status -> HTTP ${http_status}"
    if [[ "${http_status}" == "200" ]]; then
      parsed=$(sed '$d' <<<"${response}" | parse_status) || fail "Cannot parse Status (install jq or python3)."
      IFS=$'\t' read -r state mask motion <<<"${parsed}"
      echo "[dry-run] relayRegisterState=${state} relayRegisterMask=${mask} commandedMotion=${motion}"
    fi
  fi
  echo "[dry-run] Would build ${IMAGE_TAG}, request a verified Stop, stop ${CONTAINER_NAME} (-t ${STOP_TIMEOUT_SECONDS}), start the new container and wait up to ${READY_TIMEOUT_SECONDS}s for /health/ready."
  echo "[dry-run] Secrets dir on Pi: ${SECRETS_DIR}; HTTPS cert dir: ${HTTPS_CERT_DIR:-<none, insecure HTTP>}"
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
trap 'rm -f "${TMP_TAR}"' EXIT

docker save "${IMAGE_TAG}" -o "${TMP_TAR}"

echo "[deploy] Loading image into Docker context '${DOCKER_CONTEXT}'"
dockerc load < "${TMP_TAR}"

# shellcheck disable=SC2054 # commas are part of --mount values
run_cmd=(
  dockerc run -d
  --name "${CONTAINER_NAME}"
  --restart unless-stopped
  --stop-timeout "${STOP_TIMEOUT_SECONDS}"
  -p "${HOST_PORT}:8080"
  --log-driver local
  --log-opt max-size=10m
  --log-opt max-file=5
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
  run_cmd+=(
    -p "${HTTPS_HOST_PORT}:8443"
    --mount "type=bind,src=${HTTPS_CERT_DIR},dst=/https,readonly"
    --env "ASPNETCORE_URLS=http://+:8080;https://+:8443"
    --env "Kestrel__Certificates__Default__Path=/https/${HTTPS_CERT_FILE}"
  )
else
  run_cmd+=(--env "RoofControllerSecurity__RequireHttps=false")
fi

if [[ -n "${ALLOWED_HOSTS}" ]]; then
  run_cmd+=(--env "AllowedHosts=${ALLOWED_HOSTS}")
fi

if [[ -n "${EXTRA_DOCKER_ARGS}" ]]; then
  # shellcheck disable=SC2206
  extra_args=(${EXTRA_DOCKER_ARGS})
  run_cmd+=("${extra_args[@]}")
fi

# Pre-flight on the Pi with the new image, before touching the running controller: the devices and bind-mount
# sources must exist, otherwise `docker run` would fail after the old controller is gone.
echo "[deploy] Pre-flight: checking devices and mounts on the Pi"
preflight=(
  dockerc run --rm --entrypoint true
  --device /dev/gpiomem:/dev/gpiomem
  --device /dev/i2c-1:/dev/i2c-1
  --mount "type=bind,src=${SECRETS_DIR},dst=/run/secrets,readonly"
)
if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  preflight+=(--mount "type=bind,src=${HTTPS_CERT_DIR},dst=/https,readonly")
fi
"${preflight[@]}" "${IMAGE_TAG}" || fail "Pre-flight failed (missing device, ${SECRETS_DIR} or ${HTTPS_CERT_DIR:-cert dir} on the Pi). The running controller was not touched."

case "${STATE}" in
  running)
    stop_roof_before_replacing
    echo "[deploy] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null
    dockerc rm "${CONTAINER_NAME}" >/dev/null
    ;;
  stopped)
    echo "[deploy] ${CONTAINER_NAME} is not running (nothing to stop); removing it. The new controller turns all relays off when it initializes."
    dockerc rm "${CONTAINER_NAME}" >/dev/null
    ;;
  missing)
    echo "[deploy] No existing container."
    ;;
esac

echo "[deploy] Starting container on ${PI_HOST}"
run_cmd+=("${IMAGE_TAG}")
"${run_cmd[@]}"

echo "[deploy] Waiting up to ${READY_TIMEOUT_SECONDS}s for /health/ready"
deadline=$((SECONDS + READY_TIMEOUT_SECONDS))
ready=false
while (( SECONDS < deadline )); do
  if [[ "$(container_state)" != "running" ]]; then
    break
  fi
  if dockerc exec "${CONTAINER_NAME}" curl -fsS --max-time 5 http://localhost:8080/health/ready >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 3
done

echo "[deploy] Container status"
dockerc ps -a --filter "name=${CONTAINER_NAME}" --format "table {{.Names}}\t{{.Status}}\t{{.Image}}"

if [[ "${ready}" != "true" ]]; then
  echo "[deploy] Last log lines:" >&2
  dockerc logs --tail 60 "${CONTAINER_NAME}" >&2 || true
  fail "The roof controller did NOT become ready. The roof cannot be controlled remotely until this is fixed."
fi

scheme_note="http://${PI_HOST}:${HOST_PORT}"
if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  scheme_note="https://${PI_HOST}:${HTTPS_HOST_PORT}"
fi
echo "[done] Deployment complete and ready. Roof controller is reachable on ${scheme_note}"
