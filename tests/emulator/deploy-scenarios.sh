#!/usr/bin/env bash
# Container scenarios on real Docker against the HAT emulator: what the in-process scenario suite cannot reach.
#
#   lifecycle  The deployed container stopped (docker stop, as the script and Compose stop it) and killed (the process
#              dies with the relays held) while the roof travels, then started again (C11 steps 2-3 in a container).
#   c12        commissioning.md C12 with deploy-roofcontroller-rpi.sh: an idle deploy, a deploy while the roof moves,
#              pre-flight failures, a remote-check failure that rolls back, --rollback twice, and the relays off
#              throughout.
#   migration  A controller moved from the Compose `pi` profile to the deploy script and back to the Compose version,
#              following "Moving between Compose and the deploy script" in docs/deployment.md, and the refusals that
#              keep the two from managing the same controller.
#
# Needs docker (buildx, and compose 2.24 or later), curl, jq and openssl. It runs only against the local Docker daemon
# (the default context, with DOCKER_HOST unset or a unix socket), not on a Raspberry Pi, and touches no hardware: the
# controller runs in HAT emulator mode, maps no host device, and reaches the emulator over a network of this run's own.
# The controller is named roof-controller, as the script and the Compose `pi` profile name it, so the run refuses to
# start while a roof-controller container, or this run's emulator container or network, exists. Only a run that got
# past that check removes them on exit; the images stay (the build cache).
#
#   tests/emulator/deploy-scenarios.sh [lifecycle] [c12] [migration]     (all three by default, in that order)
#
# Settings (environment): SCN_HTTPS_PORT (the controller's published HTTPS port, default 18443), SCN_EMULATOR_PORT (the
# emulator's control API on loopback, default 15390), SCN_RESULTS_DIR (writes deploy-scenarios.md there: each check
# with its result and timings), SCN_NO_BUILD=1 (reuse the HAT emulator image; the deploy script always builds the
# controller, from the build cache after the first time).
#
# A `# CommissioningCheck("C12", "3")` line names the commissioning check and step the lines after it cover, as the
# attribute does on an in-process scenario; ScenarioCoverageTests reads them, and docs/commissioning.md lists them.
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
deploy_script="${repo_root}/src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh"
compose_file="${repo_root}/src/HVO.RoofControllerV4.RPi/docker-compose.yaml"

export DOCKER_CONTEXT=default
# The default context's endpoint is DOCKER_HOST when that is set, so it could be another machine (ssh://, tcp://). The
# value is not printed: it can name a user and a host.
if [[ -n "${DOCKER_HOST:-}" && "${DOCKER_HOST}" != unix://* ]]; then
  echo "[scenarios] FAIL: DOCKER_HOST points at a Docker daemon that is not local; unset it to run against this machine's." >&2
  exit 1
fi
# The roof's Pi runs the real roof-controller. (A PC can have /dev/i2c-1 too, for its graphics, so that is no sign.)
if [[ -e /dev/gpiomem ]] || grep -qs 'Raspberry Pi' /proc/device-tree/model; then
  echo "[scenarios] FAIL: this host is a Raspberry Pi; run the scenarios on a PC or a CI runner." >&2
  exit 1
fi
https_port=${SCN_HTTPS_PORT:-18443}
emulator_port=${SCN_EMULATOR_PORT:-15390}
network=hvo-deploy-scenarios
emulator_name=hvo-deploy-scenarios-hat
emulator_image=hvo/roof-hat-emulator:dev
image=hvo/roof-controller:deploy-scenarios
compose_project=hvo-deploy-scenarios
controller="roof-controller"
previous="${controller}-previous"
case "$(uname -m)" in
  aarch64|arm64) platform=linux/arm64 ;;
  *) platform=linux/amd64 ;;
esac

roof="https://127.0.0.1:${https_port}"
roof_api="${roof}/api/v4.0/RoofControl"
emulator_api="http://127.0.0.1:${emulator_port}/api/emulator"

work=$(mktemp -d)
results="${work}/results.md"
: > "${results}"

now() {
  date +%s.%N
}

# seconds_since <now value>: the seconds since then, to a tenth.
seconds_since() {
  awk -v s="$1" -v n="$(date +%s.%N)" 'BEGIN { printf "%.1f", n - s }'
}

say() {
  echo "[scenarios] $*"
}

fail() {
  echo "[scenarios] FAIL: $*" >&2
  record "FAIL" "$*"
  exit 1
}

# record <result> <text>: one line of the results table.
record() {
  printf '| %s | %s | %s |\n' "${current_check:-setup}" "$1" "$2" >> "${results}"
}

pass() {
  say "PASS ${current_check}: $*"
  record "pass" "$*"
}

write_results() {
  [[ -n "${SCN_RESULTS_DIR:-}" ]] || return 0
  mkdir -p "${SCN_RESULTS_DIR}"
  {
    echo "# Deploy and container scenarios"
    echo
    echo "HAT emulator mode on real Docker, controller image for ${platform}. Timings are wall-clock seconds."
    echo
    echo "| Check | Result | Detail |"
    echo "|---|---|---|"
    cat "${results}"
  } > "${SCN_RESULTS_DIR}/deploy-scenarios.md"
}

remove_controllers() {
  docker compose -f "${compose_file}" -f "${work}/compose.override.yaml" -p "${compose_project}" --profile pi \
    down --remove-orphans >/dev/null 2>&1 || true
  docker rm -f "${controller}" "${previous}" "${controller}-swap" >/dev/null 2>&1 || true
}

cleanup() {
  local status=$?
  set +e
  stop_background_deploy
  if (( owns_resources != 1 )); then
    # Refused before it created anything: what exists belongs to someone else.
    write_results
    rm -rf "${work}"
    exit "${status}"
  fi
  if (( status != 0 )); then
    echo "[scenarios] Containers:" >&2
    docker ps -a --filter "name=${controller}" --filter "name=${emulator_name}" >&2
    # Without the exporter's failures to reach the collector this run does not have, and stack frames.
    echo "[scenarios] Last log lines of ${controller}:" >&2
    docker logs --tail 600 "${controller}" 2>&1 \
      | grep -v -e 'OtlpMetricExporter' -e 'OtlpTraceExporter' -e 'OtlpLogExporter' -e '^ *at ' -e 'Connection refused' \
      | tail -n 60 >&2
    echo "[scenarios] Emulator violations and recent history:" >&2
    curl -sS --max-time 5 "${emulator_api}/violations" >&2
    curl -sS --max-time 5 "${emulator_api}/history?limit=40" >&2
    echo >&2
  fi
  stop_relay_monitor
  remove_controllers
  docker rm -f "${emulator_name}" >/dev/null 2>&1
  docker network rm "${network}" >/dev/null 2>&1
  write_results
  rm -rf "${work}"
  exit "${status}"
}

# ---------------------------------------------------------------------------------------------------------------------
# The controller's API over HTTPS with the scenario's CA, and the emulator's control API.

# roof_call <method> <path> [key]: prints the body, then the HTTP status on the last line. The key goes on stdin.
roof_call() {
  printf 'X-Api-Key: %s\n' "${3:-${operator_key}}" \
    | curl -sS --max-time 15 --cacert "${work}/ca.pem" -X "$1" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "${roof_api}/$2"
}

roof_get() {
  local response
  response=$(roof_call GET "$1") || return 1
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || return 1
  sed '$d' <<<"${response}"
}

roof_post() {
  local response
  response=$(roof_call POST "$1") || fail "POST $1 failed"
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || fail "POST $1 answered HTTP $(tail -n 1 <<<"${response}"): $(sed '$d' <<<"${response}")"
  sed '$d' <<<"${response}"
}

controller_ready() {
  curl -fsS --max-time 5 --cacert "${work}/ca.pem" "${roof}/health/ready" >/dev/null
}

plant() {
  curl -fsS --max-time 10 "${emulator_api}/status" | jq -c '.plant'
}

emulator_post() {
  curl -fsS --max-time 10 -X POST -H 'Content-Type: application/json' -d "$2" "${emulator_api}/$1" >/dev/null
}

plant_is() {
  plant | jq -e "$1" >/dev/null
}

roof_is() {
  roof_get Status | jq -e "$1" >/dev/null
}

# wait_for <description> <timeout seconds> <command...>: runs the command every quarter second until it succeeds.
wait_for() {
  local description=$1 timeout=$2
  shift 2
  local deadline=$((SECONDS + timeout))
  until "$@" >/dev/null 2>&1; do
    (( SECONDS < deadline )) || fail "timed out after ${timeout} s waiting for ${description}"
    sleep 0.25
  done
}

no_violations() {
  local violations
  violations=$(curl -fsS --max-time 10 "${emulator_api}/violations")
  [[ "$(jq 'length' <<<"${violations}")" == 0 ]] || fail "the emulator recorded plant violations: ${violations}"
}

relays_off_and_still() {
  plant_is '.relayRegister == 0 and .velocityMetersPerSecond == 0'
}

assert_relays_off() {
  wait_for "the relays off and the roof still" 30 relays_off_and_still
  no_violations
}

# The relay register sampled every 0.1 s while a check runs (C12 step 7).
relay_monitor_pid=""
start_relay_monitor() {
  : > "${work}/relays.log"
  (
    while true; do
      curl -fsS --max-time 2 "${emulator_api}/status" | jq -r '.plant.relayRegister' >> "${work}/relays.log" 2>/dev/null || true
      sleep 0.1
    done
  ) &
  relay_monitor_pid=$!
}

stop_relay_monitor() {
  if [[ -n "${relay_monitor_pid}" ]]; then
    kill "${relay_monitor_pid}" 2>/dev/null || true
    wait "${relay_monitor_pid}" 2>/dev/null || true
    relay_monitor_pid=""
  fi
}

# Stops the monitor and fails unless every sample was 0; sets RELAY_SAMPLES to the number of samples.
relays_stayed_off() {
  stop_relay_monitor
  local energized
  RELAY_SAMPLES=$(grep -c . "${work}/relays.log" || true)
  energized=$(grep -vc '^0$' "${work}/relays.log" || true)
  (( RELAY_SAMPLES > 0 )) || fail "the relay monitor took no samples"
  (( energized == 0 )) || fail "the relay register was energized in ${energized} of ${RELAY_SAMPLES} samples: $(sort -u "${work}/relays.log" | tr '\n' ' ')"
}

container_id() {
  docker inspect --format '{{.Id}}' "$1" 2>/dev/null || true
}

container_running() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null)" == true ]]
}

# container_log_has <name> <text>. The log is read whole first: grep -q would end a pipe early, and pipefail would then
# report docker's SIGPIPE as a failure.
container_log_has() {
  local log
  log=$(docker logs "$1" 2>&1)
  grep -qF -- "$2" <<<"${log}"
}

# container_log_count <name> <text>: how many lines of the container's log have the text.
container_log_count() {
  local log
  log=$(docker logs "$1" 2>&1)
  grep -cF -- "$2" <<<"${log}" || true
}

container_image() {
  docker inspect --format '{{.Image}}' "$1"
}

image_id() {
  docker image inspect --format '{{.Id}}' "$1"
}

# ---------------------------------------------------------------------------------------------------------------------
# The deploy script, run as an operator would, with this run's fixtures and HAT emulator mode.

# deploy [NAME=value...] [-- script args...]: sets DEPLOY_STATUS, DEPLOY_SECONDS and DEPLOY_LOG (each line prefixed
# with the seconds since the start). The output is also shown, indented.
deploy() {
  DEPLOY_LOG="${work}/deploy-$((++deploy_runs)).log"
  run_deploy "${DEPLOY_LOG}" "$@"
}

run_deploy() {
  local log=$1 env_pairs=() start
  shift
  while (( $# > 0 )) && [[ "$1" != "--" ]]; do
    env_pairs+=("$1")
    shift
  done
  [[ "${1:-}" == "--" ]] && shift
  : > "${log}"
  start=$(date +%s.%N)
  set +e
  env PI_HOST=127.0.0.1 DOCKER_CONTEXT=default IMAGE_TAG="${image}" BUILD_PLATFORM="${platform}" \
    HTTPS_HOST_PORT="${https_port}" HTTPS_CERT_DIR="${work}/certs" REMOTE_CA_CERT="${work}/ca.pem" \
    SECRETS_DIR="${work}/secrets" ROOF_OPERATOR_API_KEY="${operator_key}" \
    HAT_EMULATOR_ENDPOINT="${emulator_name}:5291" ALLOW_EMULATED_HAT=true EXTRA_DOCKER_ARGS="--network ${network}" \
    OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318 POLL_INTERVAL_SECONDS=0.5 \
    ${env_pairs[@]+"${env_pairs[@]}"} \
    bash "${deploy_script}" "$@" </dev/null 2>&1 \
    | while IFS= read -r line; do
        printf '%s %s\n' "$(awk -v s="${start}" -v n="$(date +%s.%N)" 'BEGIN { printf "%.2f", n - s }')" "${line}" \
          | tee -a "${log}" | sed 's/^/    | /'
      done
  DEPLOY_STATUS=${PIPESTATUS[0]}
  set -e
  DEPLOY_SECONDS=$(awk -v s="${start}" -v n="$(date +%s.%N)" 'BEGIN { printf "%.1f", n - s }')
}
deploy_runs=0

deploy_log_has() {
  grep -qF -- "$1" "${DEPLOY_LOG}"
}

expect_deploy_log() {
  deploy_log_has "$1" || fail "the deploy output lacks: $1"
}

# expect_untouched <id>: the running controller is the same container, still running, and the script never asked it to
# stop the roof.
expect_untouched() {
  [[ "$(container_id "${controller}")" == "$1" ]] || fail "${controller} was replaced"
  container_running "${controller}" || fail "${controller} is not running"
  ! deploy_log_has "Requesting a verified roof stop" || fail "the script asked the running controller to stop the roof"
  controller_ready || fail "${controller} is not ready"
}

# ---------------------------------------------------------------------------------------------------------------------
# Fixtures and the emulator.

setup() {
  for tool in docker curl jq openssl; do
    command -v "${tool}" >/dev/null || fail "${tool} is required"
  done
  docker buildx version >/dev/null 2>&1 || fail "docker buildx is required"
  docker compose version >/dev/null 2>&1 || fail "docker compose is required"
  if [[ -n "$(docker ps -aq --filter "name=^/${controller}(-previous|-swap)?$")" ]]; then
    fail "a ${controller} container exists on this Docker host; these scenarios deploy their own under that name. Remove it first."
  fi
  if [[ -n "$(docker ps -aq --filter "name=^/${emulator_name}$")" ]] || docker network inspect "${network}" >/dev/null 2>&1; then
    fail "${emulator_name} or the ${network} network exists: another run is using them, or one was killed. Remove them first."
  fi
  # From here on, every roof-controller container, the emulator and the network are this run's, and cleanup removes them.
  owns_resources=1

  operator_key=$(openssl rand -hex 24)
  other_key=$(openssl rand -hex 24)
  pfx_password=$(openssl rand -hex 16)

  # The certificate the controller serves: self-signed for 127.0.0.1, which is PI_HOST here, so it is its own CA.
  mkdir -p "${work}/certs"
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=roof-controller-scenarios" \
    -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
    -keyout "${work}/key.pem" -out "${work}/ca.pem" 2>/dev/null
  openssl pkcs12 -export -inkey "${work}/key.pem" -in "${work}/ca.pem" -out "${work}/certs/roof-controller.pfx" \
    -passout "pass:${pfx_password}"
  rm -f "${work}/key.pem"

  # secrets_dir <dir> <role> <certificate password>: one file per setting, as docs/security.md describes.
  secrets_dir() {
    mkdir -p "$1"
    chmod 700 "$1"
    printf '%s' scenario-operator > "$1/RoofControllerSecurity__ApiKeys__0__Name"
    printf '%s' "$2" > "$1/RoofControllerSecurity__ApiKeys__0__Role"
    printf '%s' "${operator_key}" > "$1/RoofControllerSecurity__ApiKeys__0__Key"
    printf '%s' "$3" > "$1/Kestrel__Certificates__Default__Password"
    chmod 600 "$1"/*
  }
  secrets_dir "${work}/secrets" RoofOperator "${pfx_password}"
  secrets_dir "${work}/secrets-wrong-password" RoofOperator "not-the-${pfx_password}"
  secrets_dir "${work}/secrets-viewer-only" RoofViewer "${pfx_password}"

  if [[ "${SCN_NO_BUILD:-0}" != 1 ]]; then
    say "Building the HAT emulator image"
    docker buildx build --platform "${platform}" -f "${repo_root}/src/HVO.RoofControllerV4.Emulator/Dockerfile" \
      -t "${emulator_image}" --load "${repo_root}" >/dev/null
  fi
  docker network create "${network}" >/dev/null
  docker run -d --name "${emulator_name}" --network "${network}" -p "127.0.0.1:${emulator_port}:5290" \
    -e Emulator__TimeScale=1 "${emulator_image}" >/dev/null
  wait_for "the HAT emulator" 60 plant
  say "HAT emulator ${emulator_name} on network ${network}, control API ${emulator_api}"

  write_compose_override
}

# The Compose `pi` profile on this rig: this run's image, secrets and certificate, no host device or Pi file, the HAT
# emulator in place of the HAT, and the published port and network of this run.
write_compose_override() {
  cat > "${work}/compose.override.yaml" <<YAML
x-emulated-hat: &emulated-hat
  HatEmulator__Enabled: "true"
  HatEmulator__Host: ${emulator_name}
  HatEmulator__Port: "5291"
  HatEmulator__AllowOutsideDevelopment: "true"

services:
  roof-controller-check:
    image: ${image}
    devices: !reset []
    environment: *emulated-hat
  roof-controller:
    image: ${image}
    pull_policy: never
    build: !reset null
    devices: !reset []
    ports: !override
      - "${https_port}:8443"
    environment: *emulated-hat
    volumes: !override
      - type: bind
        source: ${work}/secrets
        target: /run/secrets
        read_only: true
      - type: bind
        source: ${work}/certs
        target: /https
        read_only: true
    networks:
      - hat
networks:
  hat:
    name: ${network}
    external: true
YAML
}

compose() {
  HVO_ROOF_SECRETS_DIR="${work}/secrets" HVO_ROOF_CERT_DIR="${work}/certs" OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318 \
    docker compose -f "${compose_file}" -f "${work}/compose.override.yaml" -p "${compose_project}" --profile pi "$@"
}

# Closes the roof to its limit through the controller, so a check starts from a known place.
close_roof() {
  if ! roof_is '.status == "Closed" and .isMoving == false'; then
    roof_post Close >/dev/null
    wait_for "the roof closed" 90 roof_is '.status == "Closed" and .isMoving == false'
  fi
  assert_relays_off
}

# Starts a move and waits until the emulated roof travels, well clear of its limits.
start_travel() {
  roof_post "$1" >/dev/null
  wait_for "the roof travelling" 30 plant_is '.velocityMetersPerSecond != 0 and .positionMeters > 0.3 and .positionMeters < 1.7'
}

ensure_deployed() {
  if [[ -z "$(container_id "${controller}")" ]]; then
    current_check="First deploy (no existing container)"
    deploy
    (( DEPLOY_STATUS == 0 )) || fail "the first deploy failed (exit ${DEPLOY_STATUS})"
    expect_deploy_log "[deploy] No existing container."
    expect_deploy_log "[done] Deployment complete and verified at ${roof}"
    pass "deployed and verified in ${DEPLOY_SECONDS} s (the build included)"
  fi
  container_running "${controller}" || docker start "${controller}" >/dev/null
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized'
}

# ---------------------------------------------------------------------------------------------------------------------
# lifecycle

scenario_lifecycle() {
  ensure_deployed
  close_roof

  # CommissioningCheck("C11", "2")
  # CommissioningCheck("C11", "3")
  current_check="Lifecycle: docker stop during travel (C11 2-3)"
  start_travel Open
  local start stop_seconds exit_code
  start=$(now)
  docker stop -t 30 "${controller}" >/dev/null
  stop_seconds=$(seconds_since "${start}")
  exit_code=$(docker inspect --format '{{.State.ExitCode}}' "${controller}")
  (( exit_code != 137 )) || fail "the container was killed after the grace period (exit 137)"
  (( exit_code == 0 )) || fail "the controller exited ${exit_code} after docker stop (expected 0)"
  plant_is '.relayRegister == 0' || fail "the relays are still energized after the container stopped: $(plant)"
  assert_relays_off
  container_log_has "${controller}" 'stopped: HostShutdown' || fail "the controller's log has no HostShutdown stop"
  pass "the controller stopped the roof (relays off, HostShutdown) and exited ${exit_code} in ${stop_seconds} s"

  current_check="Lifecycle: restart after a stop"
  docker start "${controller}" >/dev/null
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  assert_relays_off
  pass "the restarted controller is ready, not moving, relays off"

  # CommissioningCheck("C11")
  current_check="Lifecycle: process killed during travel, then restarted"
  start_travel Open
  docker kill "${controller}" >/dev/null
  local held
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays were released when the process died; the HAT holds them until something writes the register"
  container_running "${controller}" && fail "the killed container restarted by itself"
  start=$(now)
  docker start "${controller}" >/dev/null
  wait_for "the restarted controller to turn the relays off" 60 plant_is '.relayRegister == 0'
  local off_seconds
  off_seconds=$(seconds_since "${start}")
  wait_for "the controller ready" 120 controller_ready
  assert_relays_off
  pass "the relays were held (register ${held}) after the kill; the restarted controller turned them off ${off_seconds} s after docker start"
}

# ---------------------------------------------------------------------------------------------------------------------
# c12

scenario_c12() {
  ensure_deployed
  close_roof

  # CommissioningCheck("C12", "1")
  current_check="C12 step 1: deploy with the roof idle"
  local old_id new_id
  old_id=$(container_id "${controller}")
  start_relay_monitor
  deploy
  (( DEPLOY_STATUS == 0 )) || fail "the deploy failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[deploy] Roof stop verified (relay register all off)."
  expect_deploy_log "[verify] Remote Status and verified Stop at ${roof}: OK"
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  new_id=$(container_id "${controller}")
  [[ "${new_id}" != "${old_id}" ]] || fail "the controller was not replaced"
  [[ "$(container_id "${previous}")" == "${old_id}" ]] || fail "the old controller is not kept as ${previous}"
  ! container_running "${previous}" || fail "${previous} is running"
  relays_stayed_off
  assert_relays_off
  pass "deployed in ${DEPLOY_SECONDS} s; the old controller is kept stopped as ${previous}; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "2")
  current_check="C12 step 2: deploy while the roof moves"
  # The roof waits between its limits, and the move starts when the pre-flight does (after the build): the roof travels
  # while the pre-flight container runs, and is still between its limits (a full travel takes about 20 s) when the
  # script's stop gate runs, a few seconds later.
  start_travel Open
  roof_post Stop >/dev/null
  assert_relays_off
  old_id=$(container_id "${controller}")
  # That Stop logged a NormalStop too: only one more, and no HostShutdown, shows the script's stop gate stopped the move.
  local normal_stops shutdown_stops
  normal_stops=$(container_log_count "${controller}" 'stopped: NormalStop')
  shutdown_stops=$(container_log_count "${controller}" 'stopped: HostShutdown')
  deploy_in_background
  wait_for "the deploy script's pre-flight" 900 background_deploy_reached "[deploy] Pre-flight"
  roof_post Open >/dev/null
  wait_for "the roof moving" 15 plant_is '.velocityMetersPerSecond != 0'
  wait_for_background_deploy
  (( DEPLOY_STATUS == 0 )) || fail "the deploy failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[deploy] Roof stop verified (relay register all off)."
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  local stop_line stopping_line
  stop_line=$(grep -n 'Roof stop verified' "${DEPLOY_LOG}" | cut -d: -f1)
  stopping_line=$(grep -n "Stopping ${controller} gracefully" "${DEPLOY_LOG}" | cut -d: -f1)
  (( stop_line < stopping_line )) || fail "the old controller was stopped before its roof stop was verified"
  [[ "$(container_id "${previous}")" == "${old_id}" ]] || fail "the old controller is not kept as ${previous}"
  plant_is '.openLimitActuated == false and .closedLimitActuated == false' \
    || fail "the roof reached a limit: the stop gate did not stop it mid-travel: $(plant)"
  (( $(container_log_count "${previous}" 'stopped: NormalStop') == normal_stops + 1 )) \
    || fail "the old controller's log does not show one NormalStop of the moving roof by the script's Stop"
  (( $(container_log_count "${previous}" 'stopped: HostShutdown') == shutdown_stops )) \
    || fail "the old controller stopped the moving roof at its shutdown (HostShutdown), not at the script's stop gate"
  assert_relays_off
  pass "the script stopped the moving roof (NormalStop, verified all-off) before replacing the controller; the roof stopped between its limits at $(plant | jq '.openPercent | floor')% open"

  # CommissioningCheck("C12", "6")
  current_check="C12 step 6: --rollback twice"
  local current_id previous_id
  current_id=$(container_id "${controller}")
  previous_id=$(container_id "${previous}")
  start_relay_monitor
  deploy -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the first rollback failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[done] Rolled back. ${controller} is verified at ${roof}. HAT: hatMode Emulated (ALLOW_EMULATED_HAT=true): the roof does not move."
  [[ "$(container_id "${controller}")" == "${previous_id}" && "$(container_id "${previous}")" == "${current_id}" ]] \
    || fail "the first rollback did not swap the versions"
  local first_seconds=${DEPLOY_SECONDS}
  deploy -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the second rollback failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[done] Rolled back. ${controller} is verified at ${roof}. HAT: hatMode Emulated (ALLOW_EMULATED_HAT=true): the roof does not move."
  [[ "$(container_id "${controller}")" == "${current_id}" && "$(container_id "${previous}")" == "${previous_id}" ]] \
    || fail "the second rollback did not swap the versions back"
  relays_stayed_off
  assert_relays_off
  pass "swapped in ${first_seconds} s and back in ${DEPLOY_SECONDS} s, each verified from this machine; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "3")
  current_check="C12 step 3: an operator key that is not configured"
  current_id=$(container_id "${controller}")
  start_relay_monitor
  deploy "ROOF_OPERATOR_API_KEY=${other_key}"
  (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded"
  expect_deploy_log "The deploy script's API key is not one of the configured keys"
  expect_deploy_log "Pre-flight failed (see above). The running controller was not touched."
  expect_untouched "${current_id}"
  relays_stayed_off
  pass "the pre-flight failed and the running controller was untouched; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "4")
  local case_name setting expected
  for case_name in "a renamed certificate file" "a wrong certificate password" "no RoofOperator key"; do
    case "${case_name}" in
      "a renamed certificate file")
        setting=HTTPS_CERT_FILE=renamed-roof-controller.pfx
        expected="certificate file '/https/renamed-roof-controller.pfx' does not exist" ;;
      "a wrong certificate password")
        setting="SECRETS_DIR=${work}/secrets-wrong-password"
        expected="could not be loaded" ;;
      "no RoofOperator key")
        setting="SECRETS_DIR=${work}/secrets-viewer-only"
        expected="No API key has the RoofOperator or RoofAdmin role" ;;
    esac
    current_check="C12 step 4: ${case_name}"
    start_relay_monitor
    deploy "${setting}"
    (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded"
    expect_deploy_log "${expected}"
    expect_deploy_log "Pre-flight failed (see above). The running controller was not touched."
    expect_untouched "${current_id}"
    relays_stayed_off
    pass "the pre-flight failed and the running controller was untouched; relays 0 in ${RELAY_SAMPLES} samples"
  done

  # CommissioningCheck("C12", "5")
  current_check="C12 step 5: a remote-check failure rolls back"
  start_relay_monitor
  deploy ALLOWED_HOSTS=localhost
  (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded although the remote check must fail"
  expect_deploy_log "Rolled back:"
  [[ "$(container_id "${controller}")" == "${current_id}" ]] || fail "the old controller is not back as ${controller}"
  container_running "${controller}" || fail "the old controller is not running"
  wait_for "the old controller ready" 120 controller_ready
  roof_is '.isInitialized' || fail "the old controller did not answer Status at ${roof}"
  local leftovers
  leftovers=$(docker ps -a --filter "ancestor=${image}" --format '{{.Names}}' | grep -vx -e "${controller}" -e "${previous}" || true)
  [[ -z "${leftovers}" ]] || fail "containers of the failed deploy remain: ${leftovers}"
  local rollback_started rollback_seconds
  rollback_started=$(grep -m 1 '\[rollback\]' "${DEPLOY_LOG}" | cut -d' ' -f1)
  rollback_seconds=$(awk -v a="${rollback_started}" -v b="${DEPLOY_SECONDS}" 'BEGIN { printf "%.1f", b - a }')
  relays_stayed_off
  assert_relays_off
  pass "exit ${DEPLOY_STATUS}; the previous controller is running and ready again under ${controller}; rollback time ${rollback_seconds} s (from the failed check to the end), whole run ${DEPLOY_SECONDS} s; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "7")
  current_check="C12 step 7: the roof de-energized throughout steps 3-6"
  no_violations
  pass "every relay-register sample in steps 3-6 was 0, and the emulator recorded no violations"
}

# The deploy runs in a process group of its own (job control on for the fork), so a failed run can stop all of it.
deploy_in_background() {
  DEPLOY_LOG="${work}/deploy-$((++deploy_runs)).log"
  : > "${DEPLOY_LOG}"
  rm -f "${work}/background.status"
  set -m
  (
    run_deploy "${DEPLOY_LOG}"
    echo "${DEPLOY_STATUS} ${DEPLOY_SECONDS}" > "${work}/background.status"
  ) &
  background_deploy_pid=$!
  set +m
}

# background_deploy_reached <text>: the background deploy printed the text, or it ended (its checks then fail).
background_deploy_reached() {
  deploy_log_has "$1" || ! kill -0 "${background_deploy_pid}" 2>/dev/null
}

wait_for_background_deploy() {
  wait "${background_deploy_pid}" || true
  background_deploy_pid=""
  [[ -s "${work}/background.status" ]] || fail "the background deploy ended without writing its status"
  read -r DEPLOY_STATUS DEPLOY_SECONDS < "${work}/background.status"
  rm -f "${work}/background.status"
}

# Stops a background deploy that is still running (a check failed while it ran), before cleanup removes its containers.
stop_background_deploy() {
  [[ -n "${background_deploy_pid:-}" ]] || return 0
  if kill -0 "${background_deploy_pid}" 2>/dev/null; then
    kill -- "-${background_deploy_pid}" 2>/dev/null || kill "${background_deploy_pid}" 2>/dev/null
    wait "${background_deploy_pid}" 2>/dev/null
  fi
  background_deploy_pid=""
}

# ---------------------------------------------------------------------------------------------------------------------
# migration

scenario_migration() {
  # CommissioningCheck("C12")
  remove_controllers
  emulator_post reset '{}'

  current_check="Migration: a Compose controller"
  # The Compose version: the controller image with a label of its own, so going back to it is visible by image ID.
  say "Building ${image} for ${platform}"
  docker buildx build --platform "${platform}" -f "${repo_root}/src/HVO.RoofControllerV4.RPi/Dockerfile" \
    -t "${image}" --load "${repo_root}" >/dev/null
  printf 'FROM %s\nLABEL org.hvo.roof-controller.scenario=compose-version\n' "${image}" \
    | docker build -q -t "${image}" - >/dev/null
  local compose_image
  compose_image=$(image_id "${image}")
  compose run --rm roof-controller-check >/dev/null 2>"${work}/compose.err" \
    || fail "the Compose deployment check failed: $(cat "${work}/compose.err")"
  compose up -d --no-build --wait --wait-timeout 180 roof-controller >/dev/null 2>"${work}/compose.err" \
    || fail "docker compose up failed: $(cat "${work}/compose.err")"
  wait_for "the Compose controller initialized" 60 roof_is '.isInitialized and .hatMode == "Emulated"'
  local compose_id
  compose_id=$(container_id "${controller}")
  [[ "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${controller}")" == "${compose_project}" ]] \
    || fail "${controller} is not a Compose container"
  assert_relays_off
  pass "the pi profile runs ${controller} on the emulator (project ${compose_project})"

  current_check="Migration: the script refuses a Compose controller"
  local args
  for args in "" --rollback; do
    deploy -- ${args:+"${args}"}
    (( DEPLOY_STATUS != 0 )) || fail "the script ${args:-deploy} succeeded over a Compose controller"
    expect_deploy_log "${controller} was created by Docker Compose (project ${compose_project})"
    expect_untouched "${compose_id}"
  done
  assert_relays_off
  pass "deploy and --rollback refused before changing anything; the Compose controller runs on"

  current_check="Migration: Compose to the deploy script"
  local stop
  stop=$(roof_post Stop)
  jq -e '.relayRegisterState == "Verified" and .relayRegisterMask == 0 and .commandedMotion == "None"' <<<"${stop}" >/dev/null \
    || fail "the Stop before the move was not verified: ${stop}"
  docker tag "${image}" "${image}-compose"
  compose down >/dev/null 2>&1
  [[ -z "$(container_id "${controller}")" ]] || fail "docker compose down left ${controller}"
  deploy
  (( DEPLOY_STATUS == 0 )) || fail "the first script deploy failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[deploy] No existing container."
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  local script_id
  script_id=$(container_id "${controller}")
  [[ -z "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${controller}")" ]] \
    || fail "the script's controller carries a Compose label"
  [[ "$(container_image "${controller}")" != "${compose_image}" ]] || fail "the script's controller runs the Compose version"
  assert_relays_off
  pass "verified Stop, the Compose version tagged ${image}-compose, compose down, then the script deployed in ${DEPLOY_SECONDS} s"

  current_check="Migration: Compose refuses while the script's controller exists"
  if compose up -d --no-build roof-controller >/dev/null 2>"${work}/compose.err"; then
    fail "docker compose up started while the script's controller exists"
  fi
  grep -qi 'already in use' "${work}/compose.err" || fail "docker compose up failed for another reason: $(cat "${work}/compose.err")"
  if [[ "$(container_id "${controller}")" != "${script_id}" ]] || ! container_running "${controller}"; then
    fail "the script's controller was replaced or stopped"
  fi
  assert_relays_off
  pass "docker compose up failed on the container name and left the script's controller running"

  current_check="Migration: back to the Compose version"
  stop=$(roof_post Stop)
  jq -e '.relayRegisterState == "Verified" and .relayRegisterMask == 0 and .commandedMotion == "None"' <<<"${stop}" >/dev/null \
    || fail "the Stop before the move was not verified: ${stop}"
  docker stop -t 30 "${controller}" >/dev/null
  docker rm "${controller}" >/dev/null
  docker rm "${previous}" >/dev/null 2>&1 || true
  docker tag "${image}-compose" "${image}"
  compose run --rm roof-controller-check >/dev/null 2>"${work}/compose.err" \
    || fail "the Compose deployment check failed: $(cat "${work}/compose.err")"
  compose up -d --no-build --wait --wait-timeout 180 roof-controller >/dev/null 2>"${work}/compose.err" \
    || fail "docker compose up failed: $(cat "${work}/compose.err")"
  wait_for "the Compose controller initialized" 60 roof_is '.isInitialized and .hatMode == "Emulated"'
  [[ "$(container_image "${controller}")" == "${compose_image}" ]] || fail "Compose does not run the Compose version again"
  assert_relays_off
  pass "verified Stop, docker stop and rm, the Compose version tagged back, then the check and compose up: the Compose version runs again"

  compose down >/dev/null 2>&1
  docker rmi "${image}-compose" >/dev/null 2>&1 || true
}

# ---------------------------------------------------------------------------------------------------------------------

scenarios=("$@")
(( ${#scenarios[@]} > 0 )) || scenarios=(lifecycle c12 migration)
for scenario in "${scenarios[@]}"; do
  case "${scenario}" in
    lifecycle|c12|migration) ;;
    *) echo "Unknown scenario: ${scenario} (lifecycle, c12 or migration)" >&2; exit 2 ;;
  esac
done

current_check=""
owns_resources=0
background_deploy_pid=""
trap cleanup EXIT
setup
for scenario in "${scenarios[@]}"; do
  say "Scenario ${scenario}"
  "scenario_${scenario}"
done
current_check=""
say "PASS: ${scenarios[*]}"
