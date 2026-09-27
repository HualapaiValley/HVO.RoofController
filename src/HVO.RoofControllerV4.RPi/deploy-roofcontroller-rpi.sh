#!/usr/bin/env bash
set -euo pipefail

# Project-local deploy script for Roof Controller V4 (RPi). See docs/deployment.md.
#
# 0. Settings are checked before any Docker call (numbers, EXTRA_DOCKER_ARGS, the HTTPS choice). If Docker cannot
#    report the containers' state, the script stops without changing anything.
# 1. Pre-flight: the new image runs --validate-deployment on the Pi with the final container's environment, devices,
#    secrets and certificate mounts (roof options, a usable RoofOperator/RoofAdmin key, this script's key, the
#    HTTPS listener and certificate). If it fails, the running controller is not touched.
# 2. The running controller is replaced only after a VERIFIED stop: POST /Stop (from inside the container, over
#    loopback) must return 200 with relayRegisterState=Verified, relayRegisterMask=0 and commandedMotion=None.
#    Anything else aborts, unless --force-unverified-stop is given AND the operator types a confirmation. The old
#    container is stopped gracefully (SIGTERM) and kept, not started, as <name>-previous; an older stopped
#    <name>-previous is removed only once that stop has succeeded.
# 3. The new controller must become ready, answer an authenticated Status inside the container, and answer an
#    authenticated Status and a verified Stop from this machine at the published URL (HTTPS unless
#    ALLOW_INSECURE_HTTP=true). Otherwise it is stopped and removed, and <name>-previous is restored as <name> and
#    started again if it was running. A failure or an interrupt (Ctrl-C, SIGTERM, a lost terminal) anywhere after the
#    old controller's stop began restores it the same way. Only the container this run created is ever removed.
#    From that stop on, docker runs in its own session (setsid, or perl on macOS), so an interrupt cannot cut a docker
#    call short: the restore begins once the call in progress returns.
#
# Usage: PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh [--dry-run] [--force-unverified-stop] [--rollback]
#   --rollback  swaps the running controller with <name>-previous (after the same verified stop) and checks it
#               as in step 3. Run it again to swap back. If the swap or the start fails or is interrupted, the swap
#               is undone and the original controller restarted. It refuses to run while <name>-swap (left by a
#               rollback that could not be undone) exists.

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
# Holds the current controller for a moment while --rollback swaps the names.
SWAP_CONTAINER_NAME="${CONTAINER_NAME}-swap"
HOST_PORT=${HOST_PORT:-8080}
HTTPS_HOST_PORT=${HTTPS_HOST_PORT:-8443}
# Extra `docker run` options for the controller, split on whitespace (no quoting). Also applied to the pre-flight
# container. Options the script sets itself (name, detach, --rm, restart policy, cidfile, published ports, graceful
# stop) are refused: use CONTAINER_NAME, HOST_PORT, HTTPS_HOST_PORT and STOP_TIMEOUT_SECONDS.
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
# Semicolon-separated host names/IPs clients use (AllowedHosts); include localhost (the health check and this script's
# in-container calls use it). Empty keeps the image default.
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

# What a failure or an interrupt during the switch must undo (restore_original; the EXIT trap runs it when the script
# dies). RESTORE_MODE is the state the original controller was in before the switch: none (there was none), stopped
# or running. It is set before the first change and cleared only when the switch is complete or the restore has run.
# The restore finds the original controller by ID (ORIGINAL_ID) wherever the switch got to and puts it back under
# CONTAINER_NAME. A deploy removes only the container it created (NEW_CONTAINER_ID, or the ID docker wrote to
# NEW_CIDFILE when `docker run` failed after creating it); a rollback stops the version it was bringing back
# (ROLLBACK_TARGET_ID) and puts it back as <name>-previous.
RESTORE_MODE=""
RESTORING=false
ORIGINAL_ID=""
NEW_CONTAINER_ID=""
NEW_CIDFILE=""
ROLLBACK_TARGET_ID=""
RESTORE_OUTCOME=""
FAILURE=""
WORK_DIR=""

# From the first change of the switch on (and during the restore), docker runs in its own session, outside the
# terminal's process group, so a Ctrl-C or a hangup at the terminal cannot reach it; bash runs its trap once the call
# returns. Docker CLI 27 and later gives up its request on SIGINT even while this script ignores the signal, and the
# daemon still finishes a stop by itself: an interrupted `docker stop` would leave the old controller stopping just
# as the restore tries to start it, and a second Ctrl-C would cut the restore's own calls short.
dockerc() {
  if [[ -n "${RESTORE_MODE}" || "${RESTORING}" == "true" ]]; then
    "${OWN_SESSION[@]}" docker --context "${DOCKER_CONTEXT}" "$@"
  else
    docker --context "${DOCKER_CONTEXT}" "$@"
  fi
}

# Output that must never stop the script: during a restore the terminal or the pipe may be gone (EIO/EPIPE).
log_err() {
  echo "$*" >&2 || true
}

fail() {
  log_err "[deploy] ERROR: $*"
  exit 1
}

# ---------------------------------------------------------------------------------------------------------------------
# Settings, checked before any Docker call. A malformed number must stop the script here: in a bash arithmetic
# expression it aborts the whole surrounding command without tripping set -e, which could skip a check or a restore.

# require_number <variable> <min> <max>: a whole number in range, normalized to plain decimal (so 010 is 10, not 8).
require_number() {
  local name=$1 min=$2 max=$3 value=${!1}
  case "${value}" in
    ''|*[!0-9]*) fail "${name} must be a whole number from ${min} to ${max}, got '${value}'." ;;
  esac
  if (( ${#value} > 9 || 10#${value} < min || 10#${value} > max )); then
    fail "${name} must be a whole number from ${min} to ${max}, got '${value}'."
  fi
  printf -v "${name}" '%d' "$((10#${value}))"
}

require_number READY_TIMEOUT_SECONDS 1 86400
require_number STOP_TIMEOUT_SECONDS 1 86400
require_number HOST_PORT 1 65535
require_number HTTPS_HOST_PORT 1 65535
case "${POLL_INTERVAL_SECONDS}" in
  ''|.|*[!0-9.]*|*.*.*) fail "POLL_INTERVAL_SECONDS must be a number of seconds such as 3 or 0.5, got '${POLL_INTERVAL_SECONDS}'." ;;
esac

# A second --name would make Docker run the controller under that name: the checks and the restore would then act on
# the wrong container while the new one drives the HAT. --rm, --detach, --restart, --cidfile and published ports would
# break the restore, the restart policy or the pre-flight container, and --stop-timeout or --stop-signal could cut the
# controller's shutdown stop short. Short options may be combined (-itd, -p8443:8443).
extra_args=()
if [[ -n "${EXTRA_DOCKER_ARGS}" ]]; then
  read -r -d '' -a extra_args <<<"${EXTRA_DOCKER_ARGS}" || true
fi
for arg in ${extra_args[@]+"${extra_args[@]}"}; do
  case "${arg}" in
    --name|--name=*|--detach|--detach=*|--rm|--rm=*|--restart|--restart=*|--cidfile|--cidfile=*|--publish|--publish=*|--publish-all|--publish-all=*)
      reserved=true ;;
    --stop-timeout|--stop-timeout=*|--stop-signal|--stop-signal=*)
      reserved=true ;;
    *)
      reserved=false
      if [[ "${arg}" =~ ^-[ditPq]*[dPp] ]]; then reserved=true; fi
      ;;
  esac
  if [[ "${reserved}" == "true" ]]; then
    fail "EXTRA_DOCKER_ARGS must not contain '${arg}': the script sets the container name, --detach, --rm, --restart, --cidfile, the published ports, --stop-timeout and --stop-signal itself (use CONTAINER_NAME, HOST_PORT, HTTPS_HOST_PORT and STOP_TIMEOUT_SECONDS)."
  fi
done

# Applies to --rollback too: its checks send the key to the published URL.
if [[ -z "${HTTPS_CERT_DIR}" && "${ALLOW_INSECURE_HTTP}" != "true" ]]; then
  fail "Set HTTPS_CERT_DIR (directory on the Pi with ${HTTPS_CERT_FILE}) or ALLOW_INSECURE_HTTP=true (for --rollback, as for the version being restored). Without HTTPS, API keys cross the network in clear text and LAN clients get 403 https_required unless RequireHttps is disabled. See docs/deployment.md."
fi

# What dockerc runs docker under during the switch. macOS has no setsid(1); perl's POSIX::setsid does the same there.
if command -v setsid >/dev/null 2>&1; then
  OWN_SESSION=(setsid -w)
elif command -v perl >/dev/null 2>&1; then
  # As setsid -w does: a process group leader cannot start a session, so it waits (ignoring signals) for a child that
  # does and passes on its exit status.
  # shellcheck disable=SC2016 # perl code
  OWN_SESSION=(perl -MPOSIX -e '
    if (getpgrp() == $$) {
      my %old = map { $_ => $SIG{$_} } qw(HUP INT QUIT TERM);
      $SIG{$_} = "IGNORE" for keys %old;
      defined(my $pid = fork) or die "fork: $!\n";
      if ($pid) { waitpid($pid, 0); exit($? & 127 ? 128 + ($? & 127) : $? >> 8) }
      $SIG{$_} = $old{$_} // "DEFAULT" for keys %old;
    }
    POSIX::setsid() > 0 or die "setsid: $!\n";
    exec { $ARGV[0] } @ARGV or die "$ARGV[0]: $!\n"' --)
else
  fail "setsid or perl is required: the switch runs docker in its own session so that an interrupt cannot cut it short."
fi

if [[ -n "${REMOTE_CA_CERT}" && ! -r "${REMOTE_CA_CERT}" ]]; then
  fail "REMOTE_CA_CERT '${REMOTE_CA_CERT}' is not a readable file on this machine."
fi

if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  REMOTE_BASE_URL="https://${PI_HOST}:${HTTPS_HOST_PORT}"
else
  REMOTE_BASE_URL="http://${PI_HOST}:${HOST_PORT}"
fi

# ---------------------------------------------------------------------------------------------------------------------

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

# docker ps filter for exactly one container name (Docker matches the regex against "/<name>").
name_filter() {
  printf 'name=^/%s$' "${1//./\\.}"
}

# lookup_container <docker ps filter>: sets CSTATE (running | stopped | missing), CID (full ID) and CNAME for the one
# container matching the filter. Returns 1 when Docker cannot be asked or the answer is ambiguous; callers must then
# stop, because treating an unknown container as missing could start a second controller next to it or remove it while
# it runs. Anything but exited, created or dead counts as running: a restarting or paused container still owns its
# name and can drive the HAT.
lookup_container() {
  local lines state
  CSTATE="" CID="" CNAME=""
  lines=$(dockerc ps -a --no-trunc --filter "$1" --format '{{.State}} {{.ID}} {{.Names}}') || return 1
  if [[ -z "${lines}" ]]; then
    CSTATE=missing
    return 0
  fi
  [[ "${lines}" != *$'\n'* ]] || return 1
  read -r state CID CNAME <<<"${lines}"
  [[ -n "${CID}" ]] || return 1
  case "${state}" in
    exited|created|dead) CSTATE=stopped ;;
    *) CSTATE=running ;;
  esac
}

# Prints running | stopped | missing for a container name; returns 1 when Docker cannot be asked.
container_state() {
  lookup_container "$(name_filter "${1:-${CONTAINER_NAME}}")" && echo "${CSTATE}"
}

# Sets STATE/CURRENT_ID and PREVIOUS_STATE/PREVIOUS_ID, or stops the script (nothing has been changed when it runs).
read_container_states() {
  lookup_container "$(name_filter "${CONTAINER_NAME}")" \
    || fail "Could not read the state of ${CONTAINER_NAME} from Docker (docker ps failed or matched more than one container). Nothing was changed; check the Docker context and retry."
  STATE=${CSTATE} CURRENT_ID=${CID}
  lookup_container "$(name_filter "${PREVIOUS_CONTAINER_NAME}")" \
    || fail "Could not read the state of ${PREVIOUS_CONTAINER_NAME} from Docker (docker ps failed or matched more than one container). Nothing was changed; check the Docker context and retry."
  PREVIOUS_STATE=${CSTATE} PREVIOUS_ID=${CID}
}

# Two controllers must never drive the HAT, so a running <name>-previous stops a deploy or a rollback before anything
# is stopped.
refuse_running_previous() {
  if [[ "${PREVIOUS_STATE}" == "running" ]]; then
    fail "${PREVIOUS_CONTAINER_NAME} is running: two controllers must never share the HAT. Stop it (docker stop ${PREVIOUS_CONTAINER_NAME}) and retry."
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
    log_err "[deploy] Stop returned HTTP ${http_status:-<none>}: ${body}"
    return 1
  fi

  if ! parsed=$(parse_status <<<"${body}"); then
    log_err "[deploy] Cannot parse the Stop response (install jq or python3); treating the stop as unverified."
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

  log_err "[deploy] WARNING: the roof stop is NOT verified. Relays may still be energized while the controller is replaced."
  log_err "[deploy] Confirm you can SEE the roof and it is not moving (or the drive is isolated)."
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
    log_err "[deploy] Could not call Stop inside ${CONTAINER_NAME}."
    confirm_unverified_stop
    return
  fi

  if check_verified_stop "${response}"; then
    echo "[deploy] Roof stop verified (relay register all off)."
  else
    confirm_unverified_stop
  fi
}

wait_ready() {
  local name=$1 deadline=$((SECONDS + READY_TIMEOUT_SECONDS)) state
  while (( SECONDS < deadline )); do
    # A container that has stopped will not become ready. If Docker cannot be asked, keep polling until the deadline.
    state=$(container_state "${name}") || state=unknown
    if [[ "${state}" == "stopped" || "${state}" == "missing" ]]; then
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
    log_err "[verify] WARNING: SKIP_REMOTE_CHECK=true: ${REMOTE_BASE_URL} was not checked from this machine. Check it from a client before relying on remote control."
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

# ---------------------------------------------------------------------------------------------------------------------
# Restore. Everything here runs without errexit, and every step checks its own result.

# The ID of the container this deploy created: from `docker run -d`, or from the cidfile docker writes on creation
# (a run that failed after creating the container, or was interrupted, prints no ID).
new_container_id() {
  if [[ -n "${NEW_CONTAINER_ID}" ]]; then
    echo "${NEW_CONTAINER_ID}"
  elif [[ -n "${NEW_CIDFILE}" && -s "${NEW_CIDFILE}" ]]; then
    tr -d '[:space:]' < "${NEW_CIDFILE}"
  fi
}

# rename_back <id> <name>: sets RESTORE_OUTCOME and returns 1 on failure.
rename_back() {
  log_err "[rollback] Renaming container ${1:0:12} to $2"
  if ! dockerc rename "$1" "$2" >/dev/null; then
    RESTORE_OUTCOME="Could not rename container ${1:0:12} to $2. The roof controller is NOT running; check the containers."
    return 1
  fi
}

# Makes sure no container this run started can run next to the original controller: a deploy stops and removes the
# container it created (by ID; a container it did not create is never touched), a rollback stops the version it was
# bringing back. Sets RESTORE_OUTCOME and returns 1 when that cannot be confirmed.
retire_switch_container() {
  local id
  if [[ "${ROLLBACK}" == "true" ]]; then
    id=${ROLLBACK_TARGET_ID}
    if lookup_container "id=${id}" && [[ "${CSTATE}" == "running" ]]; then
      log_err "[rollback] Stopping the version being rolled back to (SIGTERM first, so it stops the roof)"
      dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${id}" >/dev/null
    fi
    if ! lookup_container "id=${id}" || [[ "${CSTATE}" == "running" ]]; then
      RESTORE_OUTCOME="The version being rolled back to (container ${id:0:12}) could not be stopped, so the original controller was NOT restarted: two controllers must never drive the HAT. Stop it by hand, then check the containers."
      return 1
    fi
    return 0
  fi

  id=$(new_container_id)
  [[ -n "${id}" ]] || return 0
  log_err "[rollback] Last log lines of the new controller:"
  dockerc logs --tail 60 "${id}" >&2 2>&1 || true
  log_err "[rollback] Stopping and removing the new controller (SIGTERM first, so it stops the roof)"
  dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${id}" >/dev/null 2>&1
  dockerc rm -f "${id}" >/dev/null 2>&1
  if ! lookup_container "id=${id}" || [[ "${CSTATE}" != "missing" ]]; then
    RESTORE_OUTCOME="The new controller (container ${id:0:12}) could not be removed, so the previous controller was NOT restarted: two controllers must never drive the HAT. Remove it (docker rm -f ${id:0:12}), then restore ${PREVIOUS_CONTAINER_NAME} by hand."
    return 1
  fi
}

# Puts the rollback target back as <name>-previous (restart policy no) and the original controller back as <name>
# (restart policy unless-stopped), wherever the switch got to. Sets RESTORE_OUTCOME and returns 1 on failure.
put_names_back() {
  local original_name="" target_name=""
  if [[ -n "${ORIGINAL_ID}" ]]; then
    if ! lookup_container "id=${ORIGINAL_ID}"; then
      RESTORE_OUTCOME="Docker could not report the state of the original controller (container ${ORIGINAL_ID:0:12}), so it was NOT restored; it may be running or stopped. Check the containers."
      return 1
    fi
    if [[ "${CSTATE}" == "missing" ]]; then
      RESTORE_OUTCOME="The original controller (container ${ORIGINAL_ID:0:12}) could not be found. The roof controller is NOT running; check the containers."
      return 1
    fi
    original_name=${CNAME}
  fi
  if [[ -n "${ROLLBACK_TARGET_ID}" ]]; then
    if ! lookup_container "id=${ROLLBACK_TARGET_ID}"; then
      RESTORE_OUTCOME="Docker could not report the state of the version being rolled back to (container ${ROLLBACK_TARGET_ID:0:12}); the swap was NOT undone. Check the containers."
      return 1
    fi
    if [[ "${CSTATE}" == "missing" ]]; then
      RESTORE_OUTCOME="The version being rolled back to (container ${ROLLBACK_TARGET_ID:0:12}) could not be found; the swap was NOT undone. Check the containers."
      return 1
    fi
    target_name=${CNAME}
  fi

  if [[ -n "${target_name}" && "${target_name}" != "${PREVIOUS_CONTAINER_NAME}" ]]; then
    if [[ "${original_name}" == "${PREVIOUS_CONTAINER_NAME}" ]]; then
      rename_back "${ORIGINAL_ID}" "${SWAP_CONTAINER_NAME}" || return 1
      original_name=${SWAP_CONTAINER_NAME}
    fi
    rename_back "${ROLLBACK_TARGET_ID}" "${PREVIOUS_CONTAINER_NAME}" || return 1
  fi
  if [[ -n "${ROLLBACK_TARGET_ID}" ]]; then
    dockerc update --restart no "${ROLLBACK_TARGET_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not set restart policy no on ${PREVIOUS_CONTAINER_NAME}"
  fi

  if [[ -n "${original_name}" && "${original_name}" != "${CONTAINER_NAME}" ]]; then
    if ! lookup_container "$(name_filter "${CONTAINER_NAME}")"; then
      RESTORE_OUTCOME="Docker could not report whether ${CONTAINER_NAME} is free; the original controller was NOT restored (it is kept, stopped, as ${original_name})."
      return 1
    fi
    if [[ "${CSTATE}" != "missing" ]]; then
      RESTORE_OUTCOME="${CONTAINER_NAME} is taken by a container this run did not create (${CID:0:12}); it was left alone, and the original controller was NOT restored: it is kept, stopped, as ${original_name}. Check the containers."
      return 1
    fi
    rename_back "${ORIGINAL_ID}" "${CONTAINER_NAME}" || return 1
    dockerc update --restart unless-stopped "${ORIGINAL_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not set restart policy unless-stopped on ${CONTAINER_NAME}"
  fi
}

# Starts the original controller again if it was running before the switch. Sets RESTORE_OUTCOME.
restart_original() {
  local mode=$1
  case "${mode}" in
    none)
      if [[ "${ROLLBACK}" == "true" ]]; then
        RESTORE_OUTCOME="Undone: ${PREVIOUS_CONTAINER_NAME} is back, stopped. There was no ${CONTAINER_NAME} before the rollback, so no controller is running."
      else
        RESTORE_OUTCOME="There was no previous controller to restore: the roof controller is NOT running and the roof cannot be controlled remotely until this is fixed."
      fi
      return
      ;;
    stopped)
      if [[ "${ROLLBACK}" == "true" ]]; then
        RESTORE_OUTCOME="Undone: the original controller is back as ${CONTAINER_NAME}, stopped as it was before; ${PREVIOUS_CONTAINER_NAME} is unchanged."
      else
        RESTORE_OUTCOME="Rolled back: the previous controller is restored as ${CONTAINER_NAME}, stopped as it was before the deploy."
      fi
      return
      ;;
  esac

  # Still running means its stop never began, or the daemon is still finishing one whose client was cut off (a lost
  # connection to the Pi). Starting it would then do nothing and it would exit moments later, so stop it fully first;
  # the roof was verified stopped before the switch began.
  if lookup_container "id=${ORIGINAL_ID}" && [[ "${CSTATE}" == "running" ]]; then
    log_err "[rollback] The original controller is still running; stopping it fully (SIGTERM first) before starting it again"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${ORIGINAL_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not stop the original controller; starting it anyway"
  fi
  log_err "[rollback] Starting the original controller as ${CONTAINER_NAME}"
  if ! dockerc start "${ORIGINAL_ID}" >/dev/null; then
    RESTORE_OUTCOME="The previous controller could not be restarted. The roof controller is NOT running."
    return
  fi
  if wait_ready "${CONTAINER_NAME}"; then
    if [[ "${ROLLBACK}" == "true" ]]; then
      RESTORE_OUTCOME="Undone: the original controller is running and ready as ${CONTAINER_NAME}; ${PREVIOUS_CONTAINER_NAME} is unchanged."
    else
      RESTORE_OUTCOME="Rolled back: the previous controller is running and ready."
    fi
    return
  fi
  dockerc logs --tail 60 "${CONTAINER_NAME}" >&2 2>&1 || true
  RESTORE_OUTCOME="The previous controller was restarted but did not become ready. The roof cannot be controlled remotely until this is fixed."
}

# Undoes an unfinished switch (RESTORE_MODE) and reports the outcome on the script's own stdout and stderr; the caller
# exits non-zero. Further signals are ignored, docker runs in its own session (dockerc) and failed writes do not
# matter, so a second Ctrl-C or a lost terminal cannot cut the restore short.
restore_original() {
  local reason=$1 mode=${RESTORE_MODE} what=Deployment
  set +e
  trap '' HUP INT TERM PIPE
  exec 1>&8 2>&9
  RESTORING=true
  [[ "${ROLLBACK}" != "true" ]] || what=Rollback
  log_err "[rollback] ${reason}"

  RESTORE_OUTCOME=""
  if retire_switch_container && put_names_back; then
    restart_original "${mode}"
  fi

  show_containers >&2
  log_err "[deploy] ERROR: ${what} failed (${reason}). ${RESTORE_OUTCOME}"
  RESTORE_MODE=""
}

# Restores the original controller and exits non-zero. For failures during the switch.
abort_switch() {
  restore_original "$1"
  exit 1
}

on_exit() {
  local status=$?
  # This runs on every exit, including a signal or a lost terminal: no errexit, no further interruptions, and the
  # script's own output back in place of the interrupted call's redirections.
  set +e
  trap '' HUP INT TERM PIPE
  exec 1>&8 2>&9
  if [[ -n "${RESTORE_MODE}" ]]; then
    if [[ "${RESTORING}" == "true" ]]; then
      log_err "[deploy] ERROR: the restore did not finish; check the containers (docker ps -a --filter name=${CONTAINER_NAME})."
    else
      restore_original "the script stopped unexpectedly (exit ${status}) while switching controllers"
    fi
    [[ "${status}" != "0" ]] || status=1
  fi
  if [[ -n "${WORK_DIR}" ]]; then
    rm -rf "${WORK_DIR}"
  fi
  exit "${status}"
}
# The script's stdout and stderr, kept on fds 8 and 9. A signal's trap runs inside the call it interrupted, with that
# call's redirections still in place: a Ctrl-C during the readiness probe (dockerc exec ... >/dev/null 2>&1) would
# otherwise send the whole restore report to /dev/null.
exec 8>&1 9>&2
trap on_exit EXIT
# Signals end the script through the EXIT trap (which restores during the switch) with the usual 128+N status.
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 141' PIPE
trap 'exit 143' TERM

# ---------------------------------------------------------------------------------------------------------------------

# Checked as the switch runs docker: in its own session, with no terminal to prompt on. A context that asks for a
# password or a host key confirmation would otherwise pass here and fail at the old controller's stop.
if ! context_errors=$("${OWN_SESSION[@]}" docker --context "${DOCKER_CONTEXT}" info </dev/null 2>&1 >/dev/null); then
  [[ -z "${context_errors}" ]] || log_err "${context_errors}"
  fail "Docker context '${DOCKER_CONTEXT}' is not available without a terminal. Configure it first (e.g. docker context use ${DOCKER_CONTEXT}); it must connect without prompting (for an ssh:// context: an SSH key or ssh-agent, and a known host key). Nothing was changed."
fi

resolve_operator_key
read_container_states
echo "[deploy] Existing container ${CONTAINER_NAME}: ${STATE}; ${PREVIOUS_CONTAINER_NAME}: ${PREVIOUS_STATE}"

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

# The dry-run block above and the rollback block below always exit. A top-level command that bash abandons (an
# expansion error does that without tripping set -e) must never fall through into a swap, a build or a deploy.
[[ "${DRY_RUN}" != "true" ]] || fail "internal error: the --dry-run path did not finish; nothing was changed."

refuse_running_previous

if [[ "${ROLLBACK}" == "true" ]]; then
  lookup_container "$(name_filter "${SWAP_CONTAINER_NAME}")" \
    || fail "Could not read the state of ${SWAP_CONTAINER_NAME} from Docker. Nothing was changed; check the Docker context and retry."
  if [[ "${CSTATE}" != "missing" ]]; then
    fail "${SWAP_CONTAINER_NAME} exists: an earlier --rollback stopped halfway and could not be undone. Nothing was changed. Find out which version it is (docker ps -a --filter name=${CONTAINER_NAME}), rename it to whichever of ${CONTAINER_NAME} and ${PREVIOUS_CONTAINER_NAME} is free (docker rename ${SWAP_CONTAINER_NAME} <name>) or remove it if it is not needed, then retry."
  fi
  [[ "${PREVIOUS_STATE}" == "stopped" ]] \
    || fail "There is no stopped ${PREVIOUS_CONTAINER_NAME} to roll back to."

  if [[ "${STATE}" == "running" ]]; then
    stop_roof_before_replacing
  fi

  # From here until the restored version has started, a failure or an interrupt undoes the swap (restore_original).
  ORIGINAL_ID=${CURRENT_ID}
  ROLLBACK_TARGET_ID=${PREVIOUS_ID}
  if [[ "${STATE}" == "missing" ]]; then
    RESTORE_MODE=none
  else
    RESTORE_MODE=${STATE}
  fi

  if [[ "${STATE}" == "running" ]]; then
    echo "[rollback] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not stop ${CONTAINER_NAME}"
  fi

  echo "[rollback] Swapping ${CONTAINER_NAME} and ${PREVIOUS_CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${CONTAINER_NAME}" "${SWAP_CONTAINER_NAME}" \
      || abort_switch "Could not rename ${CONTAINER_NAME} to ${SWAP_CONTAINER_NAME}"
    dockerc update --restart no "${SWAP_CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not set restart policy no on ${SWAP_CONTAINER_NAME}"
  fi
  dockerc rename "${PREVIOUS_CONTAINER_NAME}" "${CONTAINER_NAME}" \
    || abort_switch "Could not rename ${PREVIOUS_CONTAINER_NAME} to ${CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${SWAP_CONTAINER_NAME}" "${PREVIOUS_CONTAINER_NAME}" \
      || abort_switch "Could not rename ${SWAP_CONTAINER_NAME} to ${PREVIOUS_CONTAINER_NAME}"
  fi
  dockerc update --restart unless-stopped "${CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not set restart policy unless-stopped on ${CONTAINER_NAME}"
  echo "[rollback] Starting ${CONTAINER_NAME}"
  dockerc start "${CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not start the rolled-back controller ${CONTAINER_NAME}"
  # Started: whatever the checks say, it is left running (run --rollback again to swap back).
  RESTORE_MODE=""

  if ! verify_controller "${CONTAINER_NAME}"; then
    show_containers
    fail "The rolled-back controller did not pass verification: ${FAILURE}. It is left running. Run --rollback again to swap back."
  fi
  show_containers
  echo "[done] Rolled back. ${CONTAINER_NAME} is verified at ${REMOTE_BASE_URL}; the replaced version is kept as ${PREVIOUS_CONTAINER_NAME}."
  exit 0
fi

[[ "${ROLLBACK}" != "true" ]] || fail "internal error: the --rollback path did not finish; nothing was built or deployed."

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

WORK_DIR=$(mktemp -d)
docker save "${IMAGE_TAG}" -o "${WORK_DIR}/image.tar"

echo "[deploy] Loading image into Docker context '${DOCKER_CONTEXT}'"
dockerc load < "${WORK_DIR}/image.tar"

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

container_args+=(${extra_args[@]+"${extra_args[@]}"})

# Pre-flight on the Pi with the new image and the final container's configuration, before touching the running
# controller. Docker checks the devices and mount sources; --validate-deployment checks the configuration (it does
# not open the HAT). The key's SHA-256 lets it confirm this script's key is configured without passing the key.
echo "[deploy] Pre-flight: validating the new container's configuration on the Pi"
deploy_key_sha256=$(printf '%s' "${OPERATOR_KEY}" | sha256_hex)
if ! dockerc run --rm "${container_args[@]}" --env "DeploymentCheck__DeployKeySha256=${deploy_key_sha256}" \
    "${IMAGE_TAG}" --validate-deployment; then
  fail "Pre-flight failed (see above). The running controller was not touched."
fi

# The build and the pre-flight take minutes: decide on the containers as they are now.
read_container_states
refuse_running_previous

# From the first change on, a failure or an interrupt restores the original controller (restore_original).
ORIGINAL_ID=${CURRENT_ID}
case "${STATE}" in
  running)
    stop_roof_before_replacing
    RESTORE_MODE=running
    echo "[deploy] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not stop ${CONTAINER_NAME}; it was not replaced"
    ;;
  stopped)
    RESTORE_MODE=stopped
    echo "[deploy] ${CONTAINER_NAME} is not running (nothing to stop). The new controller turns all relays off when it initializes."
    ;;
  missing)
    # Any existing <name>-previous is left alone.
    RESTORE_MODE=none
    echo "[deploy] No existing container."
    ;;
esac

if [[ "${STATE}" != "missing" ]]; then
  # Only now, once the old controller has stopped, is an older stopped <name>-previous removed (by ID).
  if [[ "${PREVIOUS_STATE}" == "stopped" ]]; then
    echo "[deploy] Removing the older ${PREVIOUS_CONTAINER_NAME}"
    dockerc rm "${PREVIOUS_ID}" >/dev/null \
      || abort_switch "Could not remove the older ${PREVIOUS_CONTAINER_NAME}; ${CONTAINER_NAME} was not replaced"
  fi
  echo "[deploy] Keeping the old controller as ${PREVIOUS_CONTAINER_NAME} (not restarted automatically)"
  dockerc rename "${CONTAINER_NAME}" "${PREVIOUS_CONTAINER_NAME}" \
    || abort_switch "Could not rename ${CONTAINER_NAME} to ${PREVIOUS_CONTAINER_NAME}; it was not replaced"
  dockerc update --restart no "${PREVIOUS_CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not set restart policy no on ${PREVIOUS_CONTAINER_NAME}"
fi

echo "[deploy] Starting the new container on ${PI_HOST}"
# Docker writes the new container's ID to the cidfile as soon as it is created, so the restore can remove it even
# when `docker run` fails after creating it (e.g. a port already in use).
NEW_CIDFILE="${WORK_DIR}/new-controller.cid"
NEW_CONTAINER_ID=$(dockerc run -d --cidfile "${NEW_CIDFILE}" --name "${CONTAINER_NAME}" --restart unless-stopped \
    --stop-timeout "${STOP_TIMEOUT_SECONDS}" --log-driver local --log-opt max-size=10m --log-opt max-file=5 \
    "${publish_args[@]}" "${container_args[@]}" "${IMAGE_TAG}") \
  || abort_switch "docker run failed for the new controller"

verify_controller "${CONTAINER_NAME}" || abort_switch "${FAILURE}"
RESTORE_MODE=""

echo "[deploy] Container status"
show_containers
echo "[done] Deployment complete and verified at ${REMOTE_BASE_URL}. The previous version is kept as ${PREVIOUS_CONTAINER_NAME}; run with --rollback to return to it."
