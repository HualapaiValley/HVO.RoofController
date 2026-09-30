#!/usr/bin/env bash
# Container scenarios on real Docker against the HAT emulator: what the in-process scenario suite cannot reach.
#
#   lifecycle  The deployed container stopped (docker stop, as the script and Compose stop it) with a camera stream
#              open, and killed (the process dies with the relays held), while the roof travels, then started again
#              (C11 steps 2-3 in a container). Then docker stop while relay writes fail: once until a shutdown retry
#              verifies the relays off, and once for good (C11 step 4). A restart through the API, which the container's
#              supervisor handles in the same container (C11 step 5).
#   supervisor The container's two processes (docs/deployment.md, "The container's two processes"): the controller
#              killed during travel inside the container, a crash loop that leaves it stopped with the container
#              unhealthy and the web UI saying why, a forced restart through the web UI's control file, and the web UI
#              killed while the roof moves (C11 steps 6-9); and the web UI's user, environment and keys directory.
#   c12        commissioning.md C12 with deploy-roofcontroller-rpi.sh: an idle deploy, a deploy while the roof moves,
#              pre-flight failures, a remote-check failure that rolls back, --rollback twice, a Stop that cannot be
#              verified, a new controller that never becomes ready, a new web UI that cannot start, and the relays off
#              throughout.
#   migration  A controller moved from the Compose `pi` profile to the deploy script and back to the Compose version,
#              following "Moving between Compose and the deploy script" in docs/deployment.md, and the refusals that
#              keep the two from managing the same controller. The script's --verify-remote checks each Compose
#              controller from this machine without Docker, and rejects a key the controller does not know. A person
#              added on the Compose controller, and the session they opened there, still work after each move: both
#              mount the same identity directory. So does a setting changed through the API on the Compose controller:
#              both mount the same settings directory.
#   pull       The deploy script's pull mode (C12 step 11): a released image, pushed to a registry of this run's own on
#              loopback, deployed by its digest with no build; a reference without a digest, a digest the registry does
#              not hold and the other platform each change nothing; --rollback between the pulled and the built version.
#
# Needs docker (buildx, and compose 2.24 or later), curl, jq and openssl. It runs only against the local Docker daemon
# (the default context, with DOCKER_HOST unset or a unix socket), not on a Raspberry Pi, and touches no hardware: the
# controller runs in HAT emulator mode, maps no host device, and reaches the emulator over a network of this run's own.
# The controller is named roof-controller, as the script and the Compose `pi` profile name it, so the run refuses to
# start while a roof-controller container, or this run's emulator container or network, exists. Only a run that got
# past that check removes them on exit; the images stay (the build cache).
#
#   tests/emulator/deploy-scenarios.sh [lifecycle] [supervisor] [c12] [migration] [pull]   (all five by default, in that order)
#
# Settings (environment): SCN_HTTPS_PORT (the controller's published HTTPS port, default 18443), SCN_WEB_PORT (the web
# UI's published HTTPS port, default 18088), SCN_EMULATOR_PORT (the emulator's control API on loopback, default 15390),
# SCN_RESULTS_DIR (writes deploy-scenarios.md there: each check
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
web_port=${SCN_WEB_PORT:-18088}
emulator_port=${SCN_EMULATOR_PORT:-15390}
network=hvo-deploy-scenarios
emulator_name=hvo-deploy-scenarios-hat
emulator_image=hvo/roof-hat-emulator:dev
image=hvo/roof-controller:deploy-scenarios
compose_project=hvo-deploy-scenarios
# The pull scenario's registry, on loopback (Docker pulls from 127.0.0.1 over plain HTTP without configuration).
registry_name=hvo-deploy-scenarios-registry
registry_port=${SCN_REGISTRY_PORT:-15000}
registry_image=registry:2@sha256:a3d8aaa63ed8681a604f1dea0aa03f100d5895b6a58ace528858a7b332415373
release_repository="127.0.0.1:${registry_port}/hvo/roof-controller"
controller="roof-controller"
previous="${controller}-previous"
case "$(uname -m)" in
  aarch64|arm64) platform=linux/arm64 ;;
  *) platform=linux/amd64 ;;
esac

roof="https://127.0.0.1:${https_port}"
roof_api="${roof}/api/v4.0/RoofControl"
web="https://127.0.0.1:${web_port}"
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
  stop_camera_stream
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
  docker rm -f "${emulator_name}" "${registry_name}" >/dev/null 2>&1
  # The pulled image (by its digest); the images the run built stay, as the build cache.
  docker images --digests --format '{{.Repository}}@{{.Digest}}' "${release_repository}" 2>/dev/null \
    | grep '@sha256:' | xargs -r docker rmi >/dev/null 2>&1
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

# admin_call <method> <path under api/v4.0> [JSON body file]: as the scenario's admin key, the key on stdin. Prints the
# body, then the HTTP status on the last line.
admin_call() {
  local body=()
  [[ -n "${3:-}" ]] && body=(-H 'Content-Type: application/json' --data-binary "@$3")
  printf 'X-Api-Key: %s\n' "${admin_key}" \
    | curl -sS --max-time 15 --cacert "${work}/ca.pem" -X "$1" -H @- -H 'Accept: application/json' ${body[@]+"${body[@]}"} \
        -w '\n%{http_code}' "${roof}/api/v4.0/$2"
}

# person_signs_in: the scenario's person signs in with a name and password (no key) and GET Auth/Me knows them.
person_signs_in() {
  local response token
  response=$(curl -sS --max-time 15 --cacert "${work}/ca.pem" -X POST -H 'Content-Type: application/json' \
    --data-binary "@${work}/sign-in.json" -w '\n%{http_code}' "${roof}/api/v4.0/Auth/Session") || return 1
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || return 1
  token=$(sed '$d' <<<"${response}" | jq -r '.token')
  session_knows_person "${token}"
}

# default_camera_is <name>: GET Settings, as the scenario's admin, shows <name> as the clients' default camera, from the
# settings file.
default_camera_is() {
  local response
  response=$(admin_call GET Settings) || return 1
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || return 1
  sed '$d' <<<"${response}" | jq -e --arg name "$1" '[.settings[] | select(.key == "RoofControllerUi:DefaultCamera")]
    | length == 1 and .[0].value == $name and .[0].source == "settings file"' >/dev/null
}

# logging_is_writable: GET Settings, as the scenario's admin, shows both log levels in the logging group as writable,
# from the shipped defaults. The image once pinned them in its environment, which made them read-only.
logging_is_writable() {
  local response
  response=$(admin_call GET Settings) || return 1
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || return 1
  sed '$d' <<<"${response}" | jq -e '[.settings[] | select(.key == "Logging:LogLevel:Default" or .key == "Logging:LogLevel:Microsoft.AspNetCore")]
    | length == 2 and all(.canWrite and .source == "shipped defaults")' >/dev/null
}

# restarted_since <count>: Docker has restarted the controller's container more than <count> times, and it runs.
restarted_since() {
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") > $1 )) && container_running "${controller}"
}

# session_knows_person <token>: GET Auth/Me with the session's bearer token (on stdin) answers as the scenario's person.
session_knows_person() {
  printf 'Authorization: Bearer %s\n' "$1" \
    | curl -fsS --max-time 15 --cacert "${work}/ca.pem" -H @- "${roof}/api/v4.0/Auth/Me" \
    | jq -e '.name == "scenario-person" and .kind == "Session"' >/dev/null
}

controller_ready() {
  curl -fsS --max-time 5 --cacert "${work}/ca.pem" "${roof}/health/ready" >/dev/null
}

# ---------------------------------------------------------------------------------------------------------------------
# The container's supervisor and web UI (docs/deployment.md, "The container's two processes").

# supervisor_state: the supervisor's state file in the controller's container (supervisor.json), on one line.
supervisor_state() {
  docker exec "${controller}" cat /run/hvo-roof/supervisor.json | jq -c .
}

# supervised_is <jq filter>: the supervisor's state file matches the filter.
supervised_is() {
  supervisor_state | jq -e "$1" >/dev/null
}

# supervised_value <jq filter>: a value from the supervisor's state file.
supervised_value() {
  supervisor_state | jq -r "$1"
}

# web_ui_live: the web UI answers its liveness check at its published port, over HTTPS with the scenario's CA.
web_ui_live() {
  curl -fsS --max-time 5 --cacert "${work}/ca.pem" "${web}/health/live" >/dev/null
}

# web_page_has <text>: the web UI's home page, as the server renders it for someone not signed in, has the text: the
# sign-in page it redirects to, which says whether the controller is running and ready (nobody can sign in while the
# controller is stopped).
web_page_has() {
  local page
  page=$(curl -fsSL --max-time 10 --cacert "${work}/ca.pem" "${web}/") || return 1
  grep -qF -- "$1" <<<"${page}"
}

# kill_supervised <controller|ui>: kills that process inside the container (SIGKILL), as a crash ends it. The supervisor
# and the other process stay.
kill_supervised() {
  local pid
  pid=$(supervised_value ".$1.pid // empty")
  [[ "${pid}" =~ ^[0-9]+$ ]] || fail "the supervisor reports no running $1 to kill: $(supervisor_state)"
  docker exec "${controller}" bash -c "kill -KILL ${pid}"
}

# restart_web_ui: kills the web UI, as a crash ends it, and waits until the supervisor has started it again and it is live.
# The wait allows for the supervisor's longest backoff (HVO_SUPERVISOR_BACKOFF_MAX_SECONDS, 30 s) and more: the web UI is
# killed several times within the supervisor's crash window.
restart_web_ui() {
  local starts
  starts=$(supervised_value '.ui.starts') || fail "could not read the supervisor's state"
  [[ "${starts}" =~ ^[0-9]+$ ]] || fail "the supervisor reports no web UI starts: $(supervisor_state)"
  kill_supervised ui || fail "could not kill the web UI"
  wait_for "the supervisor to start the web UI again" 45 \
    supervised_is ".ui.state == \"running\" and .ui.starts == $((starts + 1))"
  wait_for "the web UI live again" 60 web_ui_live
}

# expect_absent <user> <path> <message>: the path does not exist, as that user in the container sees it. Fails with the
# message when it does, and says so when docker could not check. The answer is printed in the container, because
# docker exec exits 1 for its own errors (a container that is not running, for example), as test -e does.
expect_absent() {
  local answer
  answer=$(docker exec -u "$1" "${controller}" \
    sh -c 'if [ -e "$1" ]; then echo present; else echo absent; fi' sh "$2") \
    || fail "could not check for $2 (docker exec failed)"
  case ${answer} in
    absent) ;;
    present) fail "$3" ;;
    *) fail "could not check for $2 (${answer:-no answer})" ;;
  esac
}

# expect_refused <user> <message> <command...>: the command is refused (exit status 1, as cat and ln exit when they are
# denied) as that user in the container. Fails with the message when it works, and says so when docker or the command
# could not check. The answer is printed in the container, as in expect_absent.
expect_refused() {
  local user=$1 message=$2 answer
  shift 2
  answer=$(docker exec -u "${user}" "${controller}" sh -c \
    '"$@" >/dev/null 2>&1; s=$?; case $s in 0) echo allowed ;; 1) echo refused ;; *) echo "exit $s" ;; esac' sh "$@") \
    || fail "could not check whether ${user} can run: $* (docker exec failed)"
  case ${answer} in
    refused) ;;
    allowed) fail "${message}" ;;
    *) fail "could not check whether ${user} can run: $* (${answer:-no answer})" ;;
  esac
}

# web_ui_keys_setting: the RoofWeb__DataProtectionPath setting the supervisor started the running web UI with; nothing
# when it has none. Only that line of its environment is read, as the web UI's user: root in the container has no
# CAP_SYS_PTRACE, so it cannot read another user's environment.
web_ui_keys_setting() {
  local pid
  pid=$(supervised_value '.ui.pid // empty')
  [[ "${pid}" =~ ^[0-9]+$ ]] || fail "the supervisor reports no running web UI: $(supervisor_state)"
  docker exec -u app "${controller}" bash -c \
    "set -o pipefail; tr '\\0' '\\n' < /proc/${pid}/environ | { grep '^RoofWeb__DataProtectionPath=' || true; }"
}

# request_forced_restart: what the web UI does for a forced restart, as the web UI's user: it creates the request file
# in the supervisor's control directory.
request_forced_restart() {
  docker exec -u app "${controller}" touch /run/hvo-roof/control/force-restart-controller
}

# supervisor_log_line <text> [since]: the number of the first line of the container's log (since a `now` value) that has
# the text, or nothing.
supervisor_log_line() {
  local log
  log=$(docker logs ${2:+--since "$2"} "${controller}" 2>&1)
  grep -n -m 1 -F -- "$1" <<<"${log}" | cut -d: -f1 || true
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

camera_is() {
  curl -fsS --max-time 10 "${emulator_api}/status" | jq -e ".camera | $1" >/dev/null
}

# Camera 2 through the controller's proxy, read in the background in a process group of its own; camera.ended gets the
# time the stream ended.
camera_stream_pid=""
start_camera_stream() {
  : > "${work}/camera.mjpg"
  rm -f "${work}/camera.ended"
  set -m
  (
    printf 'X-Api-Key: %s\n' "${operator_key}" \
      | curl -fsS -N --max-time 300 --cacert "${work}/ca.pem" -H @- -o "${work}/camera.mjpg" \
          "${roof}/api/v1.0/Camera/2/mjpeg" 2>"${work}/camera.err" || true
    now > "${work}/camera.ended"
  ) &
  camera_stream_pid=$!
  set +m
}

camera_streaming() {
  [[ -s "${work}/camera.mjpg" ]] && camera_is '.openStreams >= 1'
}

camera_stream_ended() {
  [[ -s "${work}/camera.ended" ]]
}

stop_camera_stream() {
  [[ -n "${camera_stream_pid}" ]] || return 0
  kill -- "-${camera_stream_pid}" 2>/dev/null || kill "${camera_stream_pid}" 2>/dev/null || true
  wait "${camera_stream_pid}" 2>/dev/null || true
  camera_stream_pid=""
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

# The relay register sampled every 0.1 s while a check runs (C12 step 10).
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

# container_stopped <name>: Docker says the container is not running. A docker error is neither running nor stopped.
container_stopped() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null)" == false ]]
}

# container_log_has <name> <text> [since]: the log, or the part since a `now` value (the container keeps the log of
# every run), has the text. The log is read whole first: grep -q would end a pipe early, and pipefail would then report
# docker's SIGPIPE as a failure.
container_log_has() {
  local log
  log=$(docker logs ${3:+--since "$3"} "$1" 2>&1)
  grep -qF -- "$2" <<<"${log}"
}

# container_log_lacks <name> <text> [since]: the log, or the part since a `now` value, was read and lacks the text. A
# log docker cannot read fails the check: docker logs exits 1 for its own errors, as grep does when nothing matches.
container_log_lacks() {
  local log
  log=$(docker logs ${3:+--since "$3"} "$1" 2>&1) || fail "could not read the log of $1: ${log}"
  ! grep -qF -- "$2" <<<"${log}"
}

# container_log_count <name> <text>: how many lines of the container's log have the text. Fails (prints no count) when
# docker cannot read the log.
container_log_count() {
  local log
  log=$(docker logs "$1" 2>&1) || return 1
  grep -cF -- "$2" <<<"${log}" || true
}

# expect_no_leftovers <which deploy>: no container of this run's image is left but the controller and its previous
# version. A list docker cannot give fails the check.
expect_no_leftovers() {
  local names leftovers
  names=$(docker ps -a --filter "ancestor=${image}" --format '{{.Names}}') \
    || fail "could not list the containers of ${image}"
  leftovers=$(grep -vx -e "${controller}" -e "${previous}" <<<"${names}" || true)
  [[ -z "${leftovers}" ]] || fail "containers of the $1 deploy remain: ${leftovers}"
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
    HTTPS_HOST_PORT="${https_port}" WEB_HOST_PORT="${web_port}" HTTPS_CERT_DIR="${work}/certs" REMOTE_CA_CERT="${work}/ca.pem" \
    SECRETS_DIR="${work}/secrets" IDENTITY_DIR="${work}/identity" ROOF_OPERATOR_API_KEY="${operator_key}" \
    CONFIG_DIR="${work}/config" MANAGED_SECRETS_DIR="${work}/settings-secrets" \
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
  if [[ -n "$(docker ps -aq --filter "name=^/(${emulator_name}|${registry_name})$")" ]] \
      || docker network inspect "${network}" >/dev/null 2>&1; then
    fail "${emulator_name}, ${registry_name} or the ${network} network exists: another run is using them, or one was killed. Remove them first."
  fi
  # From here on, every roof-controller container, the emulator and the network are this run's, and cleanup removes them.
  owns_resources=1

  operator_key=$(openssl rand -hex 24)
  admin_key=$(openssl rand -hex 24)
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
  # An admin key for the identity checks, in the main secrets directory only: the pre-flight cases that use the other
  # directories must find no usable key but the operator's.
  printf '%s' scenario-admin > "${work}/secrets/RoofControllerSecurity__ApiKeys__1__Name"
  printf '%s' RoofAdmin > "${work}/secrets/RoofControllerSecurity__ApiKeys__1__Role"
  printf '%s' "${admin_key}" > "${work}/secrets/RoofControllerSecurity__ApiKeys__1__Key"
  chmod 600 "${work}/secrets"/RoofControllerSecurity__ApiKeys__1__*
  # The identity store's directory, as docs/deployment.md has it created: the controller (root in its image) writes
  # identity.json there.
  mkdir -p "${work}/identity"
  chmod 700 "${work}/identity"
  # The settings directories, as docs/deployment.md has them created: the settings file (0755) and the managed secrets
  # file (0700).
  mkdir -p "${work}/config" "${work}/settings-secrets"
  chmod 755 "${work}/config"
  chmod 700 "${work}/settings-secrets"
  # The camera proxy reads the emulator's camera, as it reads Blue Iris on the Pi (C11).
  printf '%s' "http://${emulator_name}:5290" > "${work}/secrets/BlueIris__BaseUrl"
  chmod 600 "${work}/secrets/BlueIris__BaseUrl"

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
      - "${web_port}:8088"
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
      - type: bind
        source: ${work}/identity
        target: /var/lib/hvo-roof/identity
      - type: bind
        source: ${work}/config
        target: /etc/hvo-roof/config
      - type: bind
        source: ${work}/settings-secrets
        target: /var/lib/hvo-roof/settings-secrets
    networks:
      - hat
networks:
  hat:
    name: ${network}
    external: true
YAML
}

compose() {
  HVO_ROOF_SECRETS_DIR="${work}/secrets" HVO_ROOF_CERT_DIR="${work}/certs" HVO_ROOF_IDENTITY_DIR="${work}/identity" \
    HVO_ROOF_CONFIG_DIR="${work}/config" HVO_ROOF_MANAGED_SECRETS_DIR="${work}/settings-secrets" \
    OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318 docker compose -f "${compose_file}" -f "${work}/compose.override.yaml" -p "${compose_project}" --profile pi "$@"
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

  # CommissioningCheck("C11", "1")
  # CommissioningCheck("C11", "2")
  # CommissioningCheck("C11", "3")
  current_check="Lifecycle: docker stop during travel, a camera stream open (C11 2-3)"
  start_camera_stream
  wait_for "a camera stream through the proxy" 30 camera_streaming
  start_travel Open
  local start stop_seconds exit_code stream_seconds
  start=$(now)
  docker stop -t 30 "${controller}" >/dev/null
  stop_seconds=$(seconds_since "${start}")
  exit_code=$(docker inspect --format '{{.State.ExitCode}}' "${controller}")
  (( exit_code != 137 )) || fail "the container was killed after the grace period (exit 137)"
  (( exit_code == 0 )) || fail "the container exited ${exit_code} after docker stop (expected 0)"
  plant_is '.relayRegister == 0' || fail "the relays are still energized after the container stopped: $(plant)"
  assert_relays_off
  container_log_has "${controller}" 'stopped: HostShutdown' "${start}" || fail "the controller's log has no HostShutdown stop"
  # The supervisor stops the controller first and waits for it (its shutdown stops the roof), then the web UI.
  local controller_stopping controller_stopped ui_stopping
  controller_stopping=$(supervisor_log_line '[supervisor] Stopping the controller (SIGTERM' "${start}")
  controller_stopped=$(supervisor_log_line '[supervisor] The controller stopped (exit code 0)' "${start}")
  ui_stopping=$(supervisor_log_line '[supervisor] Stopping the web UI (SIGTERM' "${start}")
  if [[ -z "${controller_stopping}" || -z "${controller_stopped}" || -z "${ui_stopping}" ]] \
    || (( controller_stopping > controller_stopped || controller_stopped > ui_stopping )); then
    fail "the supervisor did not stop the controller (exit code 0) before the web UI: lines ${controller_stopping:-none}, ${controller_stopped:-none}, ${ui_stopping:-none}"
  fi
  container_log_lacks "${controller}" 'did not stop within' "${start}" \
    || fail "the supervisor killed a process at the end of its wait"
  wait_for "the camera stream to end" 5 camera_stream_ended
  stream_seconds=$(awk -v s="${start}" -v e="$(cat "${work}/camera.ended")" 'BEGIN { printf "%.1f", e - s }')
  stop_camera_stream
  # Held open, the stream would keep the web server stopping until the host's 20 s shutdown timeout.
  awk -v t="${stream_seconds}" 'BEGIN { exit !(t < 5) }' || fail "the camera stream ended ${stream_seconds} s after docker stop"
  awk -v t="${stop_seconds}" 'BEGIN { exit !(t < 5) }' || fail "docker stop took ${stop_seconds} s with a camera stream open"
  wait_for "the emulated camera's stream closed" 5 camera_is '.openStreams == 0'
  pass "the controller stopped the roof (relays off, HostShutdown) and exited 0 before the supervisor stopped the web UI; the container exited ${exit_code} in ${stop_seconds} s; the camera stream ended ${stream_seconds} s after docker stop"

  current_check="Lifecycle: restart after a stop"
  docker start "${controller}" >/dev/null
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  assert_relays_off
  wait_for "the web UI live" 60 web_ui_live
  pass "the restarted controller is ready, not moving, relays off, and the web UI is live"

  # CommissioningCheck("C11", "5")
  current_check="Lifecycle: a restart through the API exits 75, and the supervisor starts the controller again (C11 5)"
  local restarts container response starts ui_pid
  restarts=$(docker inspect --format '{{.RestartCount}}' "${controller}")
  container=$(container_id "${controller}")
  starts=$(supervised_value '.controller.starts')
  ui_pid=$(supervised_value '.ui.pid')
  start=$(now)
  response=$(admin_call POST System/Restart)
  [[ "$(tail -n 1 <<<"${response}")" == 202 ]] \
    || fail "POST System/Restart answered HTTP $(tail -n 1 <<<"${response}"): $(sed '$d' <<<"${response}")"
  sed '$d' <<<"${response}" | jq -e '.exitCode == 75' >/dev/null || fail "the restart answer does not name exit code 75: $(sed '$d' <<<"${response}")"
  wait_for "the supervisor to start the controller again" 60 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  supervised_is '.controller.lastExitCode == 75 and .controller.lastExitReason == "restart requested"' \
    || fail "the supervisor does not report the controller's exit 75 as a requested restart: $(supervisor_state)"
  supervised_is ".ui.state == \"running\" and .ui.pid == ${ui_pid}" || fail "the web UI did not keep running: $(supervisor_state)"
  [[ "$(container_id "${controller}")" == "${container}" ]] || fail "the restart replaced the container"
  container_running "${controller}" || fail "the container stopped"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) \
    || fail "Docker restarted the container; the supervisor restarts the controller inside it"
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  container_log_has "${controller}" 'AUDIT controller restart requested by' "${start}" \
    || fail "the controller's log has no AUDIT entry for the restart"
  web_ui_live || fail "the web UI is not live"
  assert_relays_off
  pass "POST System/Restart answered 202; the controller exited 75 and the supervisor started it again in the same container (start ${starts} -> $((starts + 1)), Docker restart count still ${restarts}), ready with the relays off; the web UI kept running (pid ${ui_pid})"

  # CommissioningCheck("C11")
  current_check="Lifecycle: process killed during travel, then restarted"
  start_travel Open
  docker kill "${controller}" >/dev/null
  local held
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays were released when the process died; the HAT holds them until something writes the register"
  container_stopped "${controller}" || fail "the killed container restarted by itself (or docker could not say)"
  start=$(now)
  docker start "${controller}" >/dev/null
  wait_for "the restarted controller to turn the relays off" 60 plant_is '.relayRegister == 0'
  local off_seconds
  off_seconds=$(seconds_since "${start}")
  wait_for "the controller ready" 120 controller_ready
  assert_relays_off
  pass "the relays were held (register ${held}) after the kill; the restarted controller turned them off ${off_seconds} s after docker start"

  # CommissioningCheck("C11", "4")
  current_check="Lifecycle: a shutdown stop that a retry verifies (C11 4)"
  close_roof
  start_travel Open
  emulator_post bus '{"failWrites": true}'
  start=$(now)
  docker stop -t 30 "${controller}" >/dev/null &
  local docker_stop=$!
  wait_for "the controller to report its shutdown stop unverified" 10 \
    container_log_has "${controller}" 'Shutdown could not verify the relay register all-off state' "${start}"
  emulator_post bus '{"failWrites": false}'
  wait "${docker_stop}" || fail "docker stop failed"
  stop_seconds=$(seconds_since "${start}")
  exit_code=$(docker inspect --format '{{.State.ExitCode}}' "${controller}")
  (( exit_code == 0 )) || fail "the container exited ${exit_code} after docker stop (expected 0)"
  container_log_has "${controller}" '[supervisor] The controller stopped (exit code 0)' "${start}" \
    || fail "the supervisor's log does not show the controller exited 0"
  container_log_has "${controller}" 'verified the relay register all-off' "${start}" \
    || fail "the controller's log has no shutdown retry that verified the relays off"
  container_log_has "${controller}" 'Roof controller shutdown stop completed' "${start}" \
    || fail "the controller's log does not show its shutdown stop completed"
  plant_is '.relayRegister == 0' || fail "the relays are still energized after the container stopped: $(plant)"
  assert_relays_off
  pass "the relay writes failed at the stop, a retry verified the relays off once they worked again, and the controller exited 0 in ${stop_seconds} s"

  current_check="Lifecycle: a shutdown stop that cannot be verified (C11 4)"
  docker start "${controller}" >/dev/null
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  close_roof
  start_travel Open
  emulator_post bus '{"failWrites": true}'
  start=$(now)
  docker stop -t 30 "${controller}" >/dev/null
  stop_seconds=$(seconds_since "${start}")
  exit_code=$(docker inspect --format '{{.State.ExitCode}}' "${controller}")
  (( exit_code != 137 )) || fail "the container was killed after the grace period (exit 137)"
  container_log_has "${controller}" 'Shutdown could not verify the relay register all-off state' "${start}" \
    || fail "the controller's log does not report its shutdown stop unverified"
  container_log_has "${controller}" 'Roof controller shutdown stop FAILED' "${start}" \
    || fail "the controller's log does not report its shutdown stop failed"
  container_log_lacks "${controller}" 'did not stop within' "${start}" \
    || fail "the supervisor killed the controller at the end of its wait"
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays are off although no relay write reached the HAT"
  emulator_post bus '{"failWrites": false}'
  start=$(now)
  docker start "${controller}" >/dev/null
  wait_for "the restarted controller to turn the relays off" 60 plant_is '.relayRegister == 0'
  off_seconds=$(seconds_since "${start}")
  wait_for "the controller ready" 120 controller_ready
  assert_relays_off
  pass "the controller logged the failed stop and exited ${exit_code} in ${stop_seconds} s with the relays held (register ${held}); once the writes worked, the restarted controller turned them off ${off_seconds} s after docker start"
}

# ---------------------------------------------------------------------------------------------------------------------
# supervisor

# The container's two processes, each ended inside the deployed container as a crash ends it. Docker never restarts the
# container: the supervisor restarts the process, or leaves a crash-looping controller stopped.
scenario_supervisor() {
  ensure_deployed
  close_roof
  wait_for "the web UI live" 60 web_ui_live
  local container restarts starts ui_pid ui_starts held start off_seconds

  # CommissioningCheck("C11")
  current_check="Supervisor: the web UI runs as the app user, without the controller's settings or secrets"
  local names leaked
  ui_pid=$(supervised_value '.ui.pid')
  [[ "$(docker exec "${controller}" stat -c %U "/proc/${ui_pid}")" == app ]] || fail "the web UI does not run as the app user"
  # Only the names: the values are not printed. Read as app, the process's own user: root in the container has no
  # CAP_SYS_PTRACE, so it cannot read another user's environment.
  names=$(docker exec -u app "${controller}" bash -c "set -o pipefail; tr '\\0' '\\n' < /proc/${ui_pid}/environ | cut -d= -f1") \
    || fail "could not read the web UI's environment (pid ${ui_pid})"
  leaked=$(grep -E '^(RoofControllerSecurity__|Kestrel__|BlueIris__|HatEmulator__|ASPNETCORE_URLS$)' <<<"${names}" | paste -sd ' ' || true)
  [[ -z "${leaked}" ]] || fail "the web UI's environment has the controller's settings: ${leaked}"
  grep -qx 'RoofWeb__Urls' <<<"${names}" || fail "the web UI's environment has no RoofWeb__Urls"
  # The key file exists (root reads it), so a refusal to app is the permission, not a missing file.
  docker exec "${controller}" test -r /run/secrets/RoofControllerSecurity__ApiKeys__0__Key \
    || fail "the controller's API key file is not at /run/secrets/RoofControllerSecurity__ApiKeys__0__Key"
  expect_refused app "the web UI's user can read the controller's API keys" \
    cat /run/secrets/RoofControllerSecurity__ApiKeys__0__Key
  # The web UI's private directory (its certificate copies) is root's: the web UI reads it but cannot plant a link there
  # for the supervisor, running as root, to write through.
  local private
  private=$(docker exec "${controller}" stat -c '%U:%G %a' /run/hvo-roof/web) || fail "no /run/hvo-roof/web"
  [[ "${private}" == "root:app 750" ]] || fail "/run/hvo-roof/web is ${private}, not root:app 750"
  expect_refused app "the web UI's user can create a link in /run/hvo-roof/web" ln -s /app/x /run/hvo-roof/web/probe
  pass "the web UI (pid ${ui_pid}) runs as app with its RoofWeb__* settings and none of the controller's, cannot read /run/secrets, and cannot write its private directory (root:app 750)"

  container=$(container_id "${controller}")
  restarts=$(docker inspect --format '{{.RestartCount}}' "${controller}")

  # CommissioningCheck("C11", "6")
  current_check="Supervisor: the controller killed during travel inside the container (C11 6)"
  starts=$(supervised_value '.controller.starts')
  start_travel Open
  kill_supervised controller
  start=$(now)
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays were released when the controller died; the HAT holds them until something writes the register"
  wait_for "the supervisor to start the controller again" 30 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  supervised_is '.controller.lastExitReason == "crashed (killed by signal 9)" and .controller.recentCrashes == 1' \
    || fail "the supervisor does not report one crash: $(supervisor_state)"
  wait_for "the restarted controller to turn the relays off" 60 plant_is '.relayRegister == 0'
  off_seconds=$(seconds_since "${start}")
  wait_for "the controller ready" 120 controller_ready
  supervised_is ".ui.state == \"running\" and .ui.pid == ${ui_pid}" || fail "the web UI did not keep running: $(supervisor_state)"
  [[ "$(container_id "${controller}")" == "${container}" ]] || fail "the container was replaced"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  assert_relays_off
  pass "the relays were held (register ${held}) after the kill; the supervisor started the controller again after its backoff, and it turned the relays off ${off_seconds} s after the kill; the web UI kept running and Docker did not restart the container"

  # CommissioningCheck("C11", "7")
  current_check="Supervisor: a crash loop leaves the controller stopped and the container unhealthy (C11 7)"
  local limit kills=0 health health_status
  limit=$(supervised_value '.crashLimit')
  # Each start is killed at once, until the supervisor stops starting it. The step 6 crash counts too.
  while ! supervised_is '.controller.state == "crash-loop"'; do
    (( kills < limit )) || fail "the supervisor still starts the controller after ${kills} more crashes: $(supervisor_state)"
    starts=$(supervised_value '.controller.starts')
    kill_supervised controller
    kills=$((kills + 1))
    # Started again after its backoff (1, 2, 4 or 8 s with the defaults), or left stopped.
    wait_for "the supervisor to start the controller again or leave it stopped" 60 supervised_is \
      ".controller.state == \"crash-loop\" or (.controller.state == \"running\" and .controller.starts == $((starts + 1)))"
  done
  supervised_is ".controller.pid == null and .controller.recentCrashes == ${limit}" \
    || fail "the crash-looping controller is not left stopped after ${limit} crashes: $(supervisor_state)"
  sleep 3
  supervised_is '.controller.state == "crash-loop"' || fail "the supervisor started the crash-looping controller again: $(supervisor_state)"
  ! controller_ready || fail "the controller answers although it is stopped"
  # The image's health check, as Docker runs it (Docker reports unhealthy after its retries, 90 s at the image's interval).
  health_status=0
  health=$(docker exec "${controller}" /usr/local/bin/roof-healthcheck) || health_status=$?
  (( health_status != 0 )) || fail "the health check passes with the controller stopped: ${health}"
  grep -qF 'controller: NOT READY' <<<"${health}" || fail "the health check does not report the controller not ready: ${health}"
  grep -qF 'supervisor: controller crash-loop, web UI running' <<<"${health}" \
    || fail "the health check does not report the crash loop: ${health}"
  container_running "${controller}" || fail "the container stopped"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  web_ui_live || fail "the web UI is not live"
  wait_for "the web UI to say the controller is stopped after repeated crashes" 15 \
    web_page_has 'The controller is stopped after repeated crashes'
  plant_is '.relayRegister == 0 and .velocityMetersPerSecond == 0' || fail "the relays are energized: $(plant)"
  no_violations
  pass "after ${limit} crashes within $(supervised_value '.crashWindowSeconds') s the controller is left stopped; the health check fails (${health}); the web UI is live and says why; relays off"

  # CommissioningCheck("C11", "8")
  current_check="Supervisor: a forced restart through the web UI's control file (C11 8)"
  starts=$(supervised_value '.controller.starts')
  request_forced_restart
  wait_for "the forced restart to start the controller" 15 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  start=$(now)
  supervised_is '.controller.lastExitReason == "forced restart" and .controller.recentCrashes == 0' \
    || fail "the supervisor does not report a forced restart that clears the crashes: $(supervisor_state)"
  # A second request while the controller starts is ignored, so a repeated one cannot kill a controller still starting.
  request_forced_restart
  wait_for "the supervisor to ignore the early request" 10 \
    container_log_has "${controller}" '[supervisor] Ignored a forced restart request' "${start}"
  supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))" \
    || fail "a forced restart right after the start killed the controller: $(supervisor_state)"
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  wait_for "the web UI to say the controller is ready" 15 web_page_has 'The controller is ready'
  # Past the supervisor's 10 s guard, a request kills the running controller and starts it again at once.
  while awk -v t="$(seconds_since "${start}")" 'BEGIN { exit !(t < 11) }'; do sleep 0.5; done
  starts=$((starts + 1))
  request_forced_restart
  wait_for "the forced restart to start the controller again" 15 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  supervised_is '.controller.lastExitCode == 137 and .controller.lastExitReason == "forced restart"' \
    || fail "the supervisor does not report the running controller killed for a forced restart: $(supervisor_state)"
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  supervised_is ".ui.state == \"running\" and .ui.pid == ${ui_pid}" || fail "the web UI did not keep running: $(supervisor_state)"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  assert_relays_off
  pass "a forced restart started the crash-looping controller (crashes cleared), a second request within 10 s of its start was ignored, and a later one killed the running controller and started it again; ready each time, relays off, the web UI running throughout"

  # CommissioningCheck("C11", "9")
  current_check="Supervisor: the web UI killed while the roof moves (C11 9)"
  local controller_pid controller_starts
  close_roof
  controller_pid=$(supervised_value '.controller.pid')
  controller_starts=$(supervised_value '.controller.starts')
  ui_starts=$(supervised_value '.ui.starts')
  start_travel Open
  kill_supervised ui
  wait_for "the supervisor to start the web UI again" 30 \
    supervised_is ".ui.state == \"running\" and .ui.starts == $((ui_starts + 1))"
  supervised_is '.ui.lastExitReason == "crashed (killed by signal 9)"' || fail "the supervisor does not report the web UI's crash: $(supervisor_state)"
  roof_is '.isMoving or .status == "Open"' || fail "the roof stopped when the web UI died: $(roof_get Status)"
  wait_for "the web UI live again" 60 web_ui_live
  supervised_is ".controller.state == \"running\" and .controller.pid == ${controller_pid} and .controller.starts == ${controller_starts}" \
    || fail "the controller was touched when the web UI died: $(supervisor_state)"
  controller_ready || fail "the controller is not ready"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  roof_post Stop >/dev/null
  assert_relays_off
  close_roof
  pass "the supervisor started only the web UI again (start ${ui_starts} -> $((ui_starts + 1))) and it is live; the controller (pid ${controller_pid}) kept running and the move went on; relays off after Stop"

  # CommissioningCheck("C11")
  current_check="Supervisor: the web UI's keys directory is in a directory only root can change"
  # What the links below name: a directory made for this step, which only root can change, so that a link followed by
  # mistake changes nothing outside the container.
  local keys_dir=/var/lib/hvo-roof-web target=/var/lib/hvo-scenario-target owners target_was setting
  docker exec "${controller}" install -d -m 0750 -o root -g root "${target}" || fail "could not make ${target}"
  target_was=$(docker exec "${controller}" stat -c '%U:%G %a' "${target}") || fail "no ${target}"
  owners=$(docker exec "${controller}" stat -c '%U:%G %a' "${keys_dir}" "${keys_dir}/keys" | paste -sd ' ') \
    || fail "no ${keys_dir}/keys"
  [[ "${owners}" == "root:root 755 app:app 700" ]] || fail "${keys_dir} and its keys directory are ${owners}, not root:root 755 and app:app 700"
  expect_refused app "the web UI's user can create a link in ${keys_dir}" ln -s "${target}" "${keys_dir}/probe"
  # A volume there given to the web UI's user, as the docs once advised: at the web UI's next start, root takes it back
  # before it makes the keys directory in it.
  docker exec "${controller}" chown app:app "${keys_dir}" || fail "could not give ${keys_dir} to the web UI's user"
  restart_web_ui
  owners=$(docker exec "${controller}" stat -c '%U:%G %a' "${keys_dir}" "${keys_dir}/keys" | paste -sd ' ') \
    || fail "no ${keys_dir}/keys after the web UI's restart"
  [[ "${owners}" == "root:root 755 app:app 700" ]] \
    || fail "after the web UI's restart, ${keys_dir} and its keys directory are ${owners}, not root:root 755 and app:app 700"
  # A symbolic link in place of the keys directory is refused at the web UI's next start: the web UI keeps its keys in
  # memory, and what the link names is left as it was. The web UI's user makes this one, while ${keys_dir} is its own,
  # as it could in a volume given to it. Then the same for a link in place of ${keys_dir}, which only root can make.
  docker exec "${controller}" chown app:app "${keys_dir}" || fail "could not give ${keys_dir} to the web UI's user"
  docker exec -u app "${controller}" bash -c "mv ${keys_dir}/keys ${keys_dir}/keys.scenario && ln -s ${target} ${keys_dir}/keys" \
    || fail "the web UI's user could not put a link in place of ${keys_dir}/keys"
  expect_keys_link_refused "${keys_dir}/keys" "${target}" "${target_was}"
  docker exec "${controller}" bash -c "rm ${keys_dir}/keys && mv ${keys_dir}/keys.scenario ${keys_dir}/keys \
    && mv ${keys_dir} ${keys_dir}.scenario && ln -s ${target} ${keys_dir}" || fail "could not put a link in place of ${keys_dir}"
  expect_keys_link_refused "${keys_dir}" "${target}" "${target_was}"
  docker exec "${controller}" bash -c "rm ${keys_dir} && mv ${keys_dir}.scenario ${keys_dir} && rmdir ${target}" \
    || fail "could not put ${keys_dir} back, or remove ${target}"
  restart_web_ui
  setting=$(web_ui_keys_setting) || fail "could not read the web UI's keys setting"
  [[ "${setting}" == "RoofWeb__DataProtectionPath=${keys_dir}/keys" ]] \
    || fail "the web UI was not given ${keys_dir}/keys again once the links were gone (${setting:-no setting})"
  pass "${keys_dir} is root:root 755 and its keys directory app:app 700; the web UI's user cannot make a link there, and a ${keys_dir} given to that user is root's again at the web UI's next start; a link in place of the keys directory (made by the web UI's user) or of ${keys_dir} is refused: the web UI keeps its keys in memory and writes none under its home, and ${target}, which the links named, is still ${target_was}; the directory is used again once the link is gone"
}

# expect_keys_link_refused <link> <target> <target's owners and mode before>: at the web UI's next start, the supervisor
# refuses the link (a WARNING), gives the web UI no keys directory, and leaves what the link names as it was; the web UI
# keeps its keys in memory, not in ASP.NET Core's default directory under its home.
expect_keys_link_refused() {
  local link=$1 target=$2 target_was=$3 start setting target_now
  start=$(now)
  restart_web_ui
  [[ -n "$(supervisor_log_line "WARNING: cannot use ${link} for the web UI's keys" "${start}")" ]] \
    || fail "the supervisor did not refuse the link at ${link}; its last lines: $(docker logs --since "${start}" "${controller}" 2>&1 \
      | grep -F '[supervisor]' | tail -n 3 | paste -sd ' ')"
  setting=$(web_ui_keys_setting) || fail "could not read the web UI's keys setting"
  [[ -z "${setting}" ]] || fail "the web UI was given a keys directory through the link at ${link} (${setting})"
  target_now=$(docker exec "${controller}" stat -c '%U:%G %a' "${target}") || fail "no ${target} after the web UI's restart"
  [[ "${target_now}" == "${target_was}" ]] \
    || fail "${target}, named by the link at ${link}, was changed from ${target_was} to ${target_now}"
  expect_absent root "${target}/keys" "a keys directory was made in ${target}, through the link at ${link}"
  # The sign-in page's form token needs the keys.
  web_page_has '__RequestVerificationToken' || fail "the web UI's sign-in page has no form token"
  expect_absent app /home/app/.aspnet "the web UI wrote its keys under /home/app/.aspnet instead of keeping them in memory"
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
  expect_deploy_log "[verify] Web UI live inside the container (https)"
  expect_deploy_log "[verify] Web UI live at ${web}: OK"
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  new_id=$(container_id "${controller}")
  [[ "${new_id}" != "${old_id}" ]] || fail "the controller was not replaced"
  [[ "$(container_id "${previous}")" == "${old_id}" ]] || fail "the old controller is not kept as ${previous}"
  container_stopped "${previous}" || fail "${previous} is running (or docker could not say)"
  web_ui_live || fail "the web UI is not live at ${web}"
  relays_stayed_off
  assert_relays_off
  pass "deployed in ${DEPLOY_SECONDS} s; the web UI is live at its published port over HTTPS; the old controller is kept stopped as ${previous}; relays 0 in ${RELAY_SAMPLES} samples"

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
  local normal_stops shutdown_stops old_normal_stops old_shutdown_stops
  normal_stops=$(container_log_count "${controller}" 'stopped: NormalStop') \
    || fail "could not read the log of ${controller}"
  shutdown_stops=$(container_log_count "${controller}" 'stopped: HostShutdown') \
    || fail "could not read the log of ${controller}"
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
  old_normal_stops=$(container_log_count "${previous}" 'stopped: NormalStop') \
    || fail "could not read the log of ${previous}"
  old_shutdown_stops=$(container_log_count "${previous}" 'stopped: HostShutdown') \
    || fail "could not read the log of ${previous}"
  (( old_normal_stops == normal_stops + 1 )) \
    || fail "the old controller's log does not show one NormalStop of the moving roof by the script's Stop"
  (( old_shutdown_stops == shutdown_stops )) \
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
  expect_no_leftovers failed
  local rollback_started rollback_seconds
  rollback_started=$(grep -m 1 '\[rollback\]' "${DEPLOY_LOG}" | cut -d' ' -f1)
  rollback_seconds=$(awk -v a="${rollback_started}" -v b="${DEPLOY_SECONDS}" 'BEGIN { printf "%.1f", b - a }')
  relays_stayed_off
  assert_relays_off
  pass "exit ${DEPLOY_STATUS}; the previous controller is running and ready again under ${controller}; rollback time ${rollback_seconds} s (from the failed check to the end), whole run ${DEPLOY_SECONDS} s; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "7")
  current_check="C12 step 7: a Stop that cannot be verified aborts the deploy"
  # Relay-register reads fail, so the Stop's all-off cannot be read back. The writes still work, and the roof is idle.
  start_relay_monitor
  emulator_post bus '{"failReads": true}'
  deploy
  emulator_post bus '{"failReads": false}'
  (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded although the roof stop could not be verified"
  expect_deploy_log "[deploy] Requesting a verified roof stop from ${controller}"
  expect_deploy_log "The roof stop could not be verified."
  ! deploy_log_has "Roof stop verified" || fail "the script reported the roof stop verified"
  ! deploy_log_has "Stopping ${controller} gracefully" || fail "the script stopped ${controller}"
  [[ "$(container_id "${controller}")" == "${current_id}" ]] || fail "${controller} was replaced"
  container_running "${controller}" || fail "${controller} is not running"
  expect_no_leftovers aborted
  relays_stayed_off
  local stop_answer
  stop_answer=$(grep -m 1 -o -e 'Stop returned HTTP [0-9]*' -e 'Stop result: relayRegisterState=[A-Za-z]*' "${DEPLOY_LOG}" || true)
  # The unverified Stop latched RelayVerificationFailed. With the reads back, supervision verifies the all-off again,
  # and ClearFault (its pulse energizes RLY3, so after the monitor) releases the latch.
  wait_for "the relay register verified again" 30 roof_is '.relayRegisterState == "Verified" and .relayRegisterMask == 0'
  roof_is '.isFaultLatched and .latchedFaultReason == "RelayVerificationFailed"' \
    || fail "the unverified Stop did not latch RelayVerificationFailed: $(roof_get Status)"
  roof_post ClearFault >/dev/null
  wait_for "the fault cleared" 30 roof_is '.isFaultLatched == false'
  wait_for "the controller ready" 60 controller_ready
  assert_relays_off
  pass "the Stop was not verified (${stop_answer:-no answer}), the deploy aborted in ${DEPLOY_SECONDS} s before stopping ${controller}, and the running controller latched RelayVerificationFailed until ClearFault; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "8")
  current_check="C12 step 8: a new controller that never becomes ready rolls back"
  # A HAT endpoint that does not resolve passes the pre-flight, which checks the settings, not the HAT. The new
  # controller keeps retrying its HAT and never becomes ready.
  start_relay_monitor
  deploy HAT_EMULATOR_ENDPOINT=hvo-deploy-scenarios-nohat:5291 READY_TIMEOUT_SECONDS=45
  (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded although the new controller cannot reach the HAT"
  expect_deploy_log "[deploy] Roof stop verified (relay register all off)."
  expect_deploy_log "${controller} did not become ready within 45s"
  expect_deploy_log "Rolled back: the previous controller is running and ready."
  [[ "$(container_id "${controller}")" == "${current_id}" ]] || fail "the old controller is not back as ${controller}"
  container_running "${controller}" || fail "the old controller is not running"
  wait_for "the old controller ready" 120 controller_ready
  roof_is '.isInitialized' || fail "the old controller did not answer Status at ${roof}"
  expect_no_leftovers failed
  relays_stayed_off
  assert_relays_off
  pass "the new controller was not ready within 45 s; exit ${DEPLOY_STATUS} after ${DEPLOY_SECONDS} s with the previous controller running and ready again; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "9")
  current_check="C12 step 9: a new version whose web UI cannot start rolls back"
  # A web UI setting out of range passes the pre-flight, which runs only the controller. The new controller becomes ready,
  # but its web UI exits at each start and the supervisor keeps starting it again.
  start_relay_monitor
  deploy "EXTRA_DOCKER_ARGS=--network ${network} --env RoofWeb__StatusRefreshSeconds=0"
  (( DEPLOY_STATUS != 0 )) || fail "the deploy succeeded although the new web UI cannot start"
  expect_deploy_log "[deploy] Roof stop verified (relay register all off)."
  expect_deploy_log "the web UI in ${controller} exited and its supervisor is starting it again"
  expect_deploy_log "Rolled back: the previous controller is running and ready."
  [[ "$(container_id "${controller}")" == "${current_id}" ]] || fail "the old controller is not back as ${controller}"
  container_running "${controller}" || fail "the old controller is not running"
  wait_for "the old controller ready" 120 controller_ready
  roof_is '.isInitialized' || fail "the old controller did not answer Status at ${roof}"
  wait_for "the old version's web UI live" 60 web_ui_live
  expect_no_leftovers failed
  relays_stayed_off
  assert_relays_off
  pass "the new web UI could not start; exit ${DEPLOY_STATUS} after ${DEPLOY_SECONDS} s with the previous controller and its web UI running again; relays 0 in ${RELAY_SAMPLES} samples"

  # CommissioningCheck("C12", "10")
  current_check="C12 step 10: the roof de-energized throughout steps 3-9"
  no_violations
  pass "every relay-register sample in steps 3-9 was 0 (in step 7, until the monitor stopped before ClearFault), and the emulator recorded no violations"
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

  current_check="Migration: a person added on the Compose controller"
  local password response compose_session
  password=$(openssl rand -hex 16)
  jq -n --arg password "${password}" '{name: "scenario-person", role: "RoofOperator", password: $password}' > "${work}/person.json"
  jq -n --arg password "${password}" '{name: "scenario-person", password: $password}' > "${work}/sign-in.json"
  response=$(admin_call POST Identity/Users "${work}/person.json")
  [[ "$(tail -n 1 <<<"${response}")" == 201 ]] || fail "adding a person answered HTTP $(tail -n 1 <<<"${response}"): $(sed '$d' <<<"${response}")"
  response=$(curl -sS --max-time 15 --cacert "${work}/ca.pem" -X POST -H 'Content-Type: application/json' \
    --data-binary "@${work}/sign-in.json" "${roof}/api/v4.0/Auth/Session")
  compose_session=$(jq -r '.token // empty' <<<"${response}")
  [[ -n "${compose_session}" ]] || fail "the person could not sign in on the Compose controller"
  session_knows_person "${compose_session}" || fail "the Compose controller does not know the person's session"
  [[ -f "${work}/identity/identity.json" ]] || fail "the Compose controller did not save identity.json in the identity mount"
  [[ "$(stat -c %a "${work}/identity/identity.json")" == 600 ]] \
    || fail "identity.json is mode $(stat -c %a "${work}/identity/identity.json"), not 600"
  pass "an admin added a person, who signed in; identity.json is saved in the identity mount, mode 600"

  current_check="Migration: a setting changed on the Compose controller"
  local settings_file="${work}/config/appsettings.Local.json"
  response=$(admin_call GET Settings)
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || fail "GET Settings answered HTTP $(tail -n 1 <<<"${response}"): $(sed '$d' <<<"${response}")"
  sed '$d' <<<"${response}" | jq '{expectedVersion: .version, confirmSafetyCriticalChange: false,
    values: {"RoofControllerUi:DefaultCamera": "scenario-camera", "RoofControllerUi:KioskScreenTimeout": 600}}' \
    > "${work}/settings.json"
  response=$(admin_call POST Settings/ui "${work}/settings.json")
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] \
    || fail "changing the ui settings answered HTTP $(tail -n 1 <<<"${response}"): $(sed '$d' <<<"${response}")"
  default_camera_is scenario-camera || fail "the Compose controller does not show the changed setting from the settings file"
  [[ -f "${settings_file}" ]] || fail "the Compose controller did not save appsettings.Local.json in the settings mount"
  [[ "$(stat -c %a "${settings_file}")" == 644 ]] \
    || fail "appsettings.Local.json is mode $(stat -c %a "${settings_file}"), not 644"
  jq -e '.HvoRoofSettings.Version == 2 and .RoofControllerUi.DefaultCamera == "scenario-camera"
    and .RoofControllerUi.KioskScreenTimeout == "00:10:00"' "${settings_file}" >/dev/null \
    || fail "appsettings.Local.json does not hold the change and version 2: $(cat "${settings_file}")"
  logging_is_writable || fail "the Compose controller does not let the API change the log levels: $(admin_call GET Settings \
    | sed '$d' | jq -c '[.settings[] | select(.group == "logging") | {key, canWrite, source}]')"
  pass "an admin changed the ui settings; appsettings.Local.json is saved in the settings mount, mode 644, at version 2; the log levels are writable, from the shipped defaults"

  # The remote check of a Compose deployment. The Docker context does not exist, so any Docker call would fail.
  current_check="Migration: --verify-remote checks the Compose controller"
  deploy DOCKER_CONTEXT=hvo-deploy-scenarios-no-context -- --verify-remote
  (( DEPLOY_STATUS == 0 )) || fail "--verify-remote failed on the Compose controller (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[verify] Authenticated Status at ${roof}: HTTP 200, hatMode Emulated"
  expect_deploy_log "[deploy] Stop result: relayRegisterState=Verified relayRegisterMask=0 commandedMotion=None"
  expect_deploy_log "[done] Verified at ${roof} from this machine"
  local verify_seconds=${DEPLOY_SECONDS}
  deploy DOCKER_CONTEXT=hvo-deploy-scenarios-no-context ROOF_OPERATOR_API_KEY=not-a-configured-key -- --verify-remote
  (( DEPLOY_STATUS != 0 )) || fail "--verify-remote passed with a key the controller does not know"
  expect_deploy_log "GET Status at ${roof} returned HTTP 401 to this machine"
  expect_untouched "${compose_id}"
  assert_relays_off
  pass "Status (hatMode Emulated) and a verified Stop at ${roof} in ${verify_seconds} s, with no Docker context; an unknown key got 401"

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
  deploy DOCKER_CONTEXT=hvo-deploy-scenarios-no-context -- --verify-remote
  (( DEPLOY_STATUS == 0 )) || fail "the Stop before the move was not verified (--verify-remote exit ${DEPLOY_STATUS})"
  docker tag "${image}" "${image}-compose"
  compose down >/dev/null 2>&1
  [[ -z "$(container_id "${controller}")" ]] || fail "docker compose down left ${controller}"
  deploy
  (( DEPLOY_STATUS == 0 )) || fail "the first script deploy failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[deploy] No existing container."
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  local script_id
  script_id=$(container_id "${controller}")
  local script_label script_image
  script_label=$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${controller}") \
    || fail "could not inspect ${controller}"
  [[ -z "${script_label}" ]] || fail "the script's controller carries a Compose label"
  script_image=$(container_image "${controller}") || fail "could not inspect ${controller}"
  [[ "${script_image}" != "${compose_image}" ]] || fail "the script's controller runs the Compose version"
  session_knows_person "${compose_session}" || fail "the session opened on the Compose controller does not work on the script's"
  person_signs_in || fail "the person added on the Compose controller cannot sign in on the script's"
  default_camera_is scenario-camera || fail "the setting changed on the Compose controller is not in effect on the script's"
  logging_is_writable || fail "the script's controller does not let the API change the log levels"
  assert_relays_off
  pass "a verified Stop (--verify-remote), the Compose version tagged ${image}-compose, compose down, then the script deployed in ${DEPLOY_SECONDS} s; the person, their session and the changed setting carried over"

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
  deploy DOCKER_CONTEXT=hvo-deploy-scenarios-no-context -- --verify-remote
  (( DEPLOY_STATUS == 0 )) || fail "the Stop before the move was not verified (--verify-remote exit ${DEPLOY_STATUS})"
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
  deploy DOCKER_CONTEXT=hvo-deploy-scenarios-no-context -- --verify-remote
  (( DEPLOY_STATUS == 0 )) || fail "--verify-remote failed on the Compose version (exit ${DEPLOY_STATUS})"
  person_signs_in || fail "the person cannot sign in on the Compose version again"
  default_camera_is scenario-camera || fail "the changed setting is not in effect on the Compose version again"
  assert_relays_off
  pass "a verified Stop (--verify-remote), docker stop and rm, the Compose version tagged back, then the check, compose up and --verify-remote: the Compose version runs again, the person still signs in, and the changed setting is still in effect"

  compose down >/dev/null 2>&1
  docker rmi "${image}-compose" >/dev/null 2>&1 || true
}

# ---------------------------------------------------------------------------------------------------------------------
# pull

registry_ready() {
  curl -fsS --max-time 2 "http://127.0.0.1:${registry_port}/v2/" >/dev/null
}

# expect_not_pulled <id>: expect_untouched, and the controller was not stopped or replaced for a pull that failed.
expect_not_pulled() {
  expect_deploy_log "The running controller was not touched."
  expect_untouched "$1"
  ! deploy_log_has "Pre-flight: validating" || fail "the script ran the pre-flight after a pull that failed its checks"
}

scenario_pull() {
  ensure_deployed
  close_roof

  # CommissioningCheck("C12", "11")
  current_check="C12 step 11: a released image pulled by its digest"
  # A release's image as the release workflow publishes it, in a registry of this run's own on loopback: this run's
  # controller image under another version label, so the deploy has something new to pull.
  docker run -d --name "${registry_name}" -p "127.0.0.1:${registry_port}:5000" "${registry_image}" >/dev/null \
    || fail "could not start the registry"
  wait_for "the registry" 60 registry_ready
  mkdir -p "${work}/release-image"
  printf 'FROM %s\nLABEL org.opencontainers.image.version=4.0.0-scenario\n' "${image}" > "${work}/release-image/Dockerfile"
  docker buildx build --quiet --platform "${platform}" -t "${release_repository}:4.0.0-scenario" --load \
    "${work}/release-image" >/dev/null || fail "could not build the released image"
  local push digest ref
  push=$(docker push "${release_repository}:4.0.0-scenario") || fail "could not push the released image: ${push}"
  digest=$(sed -n 's/^4\.0\.0-scenario: digest: \(sha256:[0-9a-f]\{64\}\) .*/\1/p' <<<"${push}")
  [[ -n "${digest}" ]] || fail "the push reported no digest: ${push}"
  # Only the tag goes: the deploy pulls the image by its digest, as from a release.
  docker rmi "${release_repository}:4.0.0-scenario" >/dev/null
  ref="${release_repository}:4.0.0-scenario@${digest}"

  local old_id new_id
  old_id=$(container_id "${controller}")
  start_relay_monitor
  deploy "IMAGE_REF=${ref}"
  (( DEPLOY_STATUS == 0 )) || fail "the deploy of the pulled image failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[pull] Pulling ${ref} for ${platform} into Docker context 'default'..."
  expect_deploy_log "[pull] ${digest} for ${platform}: version 4.0.0-scenario"
  expect_deploy_log "[deploy] Roof stop verified (relay register all off)."
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  ! deploy_log_has "[build]" || fail "the deploy of a pulled image built one"
  new_id=$(container_id "${controller}")
  [[ "${new_id}" != "${old_id}" ]] || fail "the controller was not replaced"
  [[ "$(container_image "${controller}")" == "$(image_id "${ref}")" ]] || fail "${controller} does not run the pulled image"
  [[ "$(container_id "${previous}")" == "${old_id}" ]] || fail "the old controller is not kept as ${previous}"
  container_stopped "${previous}" || fail "${previous} is running (or docker could not say)"
  web_ui_live || fail "the web UI is not live at ${web}"
  relays_stayed_off
  assert_relays_off
  local pull_seconds=${DEPLOY_SECONDS}

  # Refusals: each changes nothing, and the running controller is never asked to stop the roof.
  local other_platform=linux/arm64 unknown_digest
  [[ "${platform}" == linux/amd64 ]] || other_platform=linux/amd64
  unknown_digest="sha256:$(printf '%s' "not-${digest}" | sha256sum | cut -c1-64)"
  start_relay_monitor
  deploy "IMAGE_REF=${release_repository}:4.0.0-scenario"
  (( DEPLOY_STATUS != 0 )) || fail "a reference without a digest was deployed"
  expect_deploy_log "IMAGE_REF must name a released image by its digest"
  expect_deploy_log "Nothing was changed."
  expect_untouched "${new_id}"
  deploy "IMAGE_REF=${release_repository}:4.0.0-scenario@${unknown_digest}"
  (( DEPLOY_STATUS != 0 )) || fail "a digest the registry does not hold was deployed"
  expect_deploy_log "Could not pull ${release_repository}:4.0.0-scenario@${unknown_digest}"
  expect_not_pulled "${new_id}"
  # A registry refuses the platform (no matching manifest), or the image the Pi holds is checked and is not for it.
  deploy "IMAGE_REF=${ref}" "BUILD_PLATFORM=${other_platform}"
  (( DEPLOY_STATUS != 0 )) || fail "the image was deployed for ${other_platform}"
  deploy_log_has "Could not pull ${ref} for ${other_platform}" || deploy_log_has "The image ${ref} is not for ${other_platform}" \
    || fail "the deploy for ${other_platform} did not fail at the pull or the platform check"
  expect_not_pulled "${new_id}"
  relays_stayed_off

  # --rollback swaps the pulled version with the one before (a built image) and back: each container keeps its image.
  start_relay_monitor
  deploy "IMAGE_REF=${ref}" -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback from the pulled image failed (exit ${DEPLOY_STATUS})"
  [[ "$(container_id "${controller}")" == "${old_id}" && "$(container_id "${previous}")" == "${new_id}" ]] \
    || fail "the rollback did not swap the pulled version with the one before"
  ! deploy_log_has "[pull]" || fail "--rollback pulled the image"
  deploy -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback to the pulled image failed (exit ${DEPLOY_STATUS})"
  [[ "$(container_id "${controller}")" == "${new_id}" ]] || fail "the second rollback did not return to the pulled version"
  [[ "$(container_image "${controller}")" == "$(image_id "${ref}")" ]] || fail "${controller} does not run the pulled image again"
  relays_stayed_off
  assert_relays_off
  pass "deployed ${digest:0:19}... from a registry in ${pull_seconds} s with no build; a reference without a digest, a digest the registry does not hold and ${other_platform} each changed nothing; --rollback swapped to the built version and back; relays 0 throughout"
}

# ---------------------------------------------------------------------------------------------------------------------

scenarios=("$@")
(( ${#scenarios[@]} > 0 )) || scenarios=(lifecycle supervisor c12 migration pull)
for scenario in "${scenarios[@]}"; do
  case "${scenario}" in
    lifecycle|supervisor|c12|migration|pull) ;;
    *) echo "Unknown scenario: ${scenario} (lifecycle, supervisor, c12, migration or pull)" >&2; exit 2 ;;
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
