#!/usr/bin/env bash
# Container scenarios on real Docker against the HAT emulator: what the in-process scenario suite cannot reach.
#
#   lifecycle  The deployed container stopped (docker stop, as the script and Compose stop it) with a camera stream
#              open, and killed (the process dies with the relays held), while the roof travels, then started again
#              (C11 steps 2-3 in a container). Then docker stop while relay writes fail: once until a shutdown retry
#              verifies the relays off, and once for good (C11 step 4). A restart through the API, which the container's
#              supervisor handles in the same container (C11 step 5).
#   supervisor The container's two processes (docs/deployment.md, "The container's two processes"): the controller
#              killed during travel inside the container, with the time until the supervisor has turned the relays off
#              and until the roof is still, a crash loop that leaves it stopped with the container unhealthy and the web
#              UI saying why, a forced restart through the web UI's control file, and the web UI killed while the roof
#              moves (C11 steps 6-9); a kill during travel with a settings file the controller refuses, so its starts
#              fail until the supervisor leaves it stopped, the relays off throughout (C11 10-11); docker stop with the
#              controller hung during travel (C11 12); the processes' OOM preference (C11 13); relays that cannot be
#              verified off holding the controller stopped until they are (C11 14); the supervisor itself killed during
#              travel (C11); and the web UI's user, environment and keys directory.
#   c12        commissioning.md C12 with deploy-roofcontroller-rpi.sh: an idle deploy, a deploy while the roof moves,
#              pre-flight failures, a remote-check failure that rolls back, --rollback twice, a Stop that cannot be
#              verified, a new controller that never becomes ready, a new web UI that cannot start, and the relays off
#              throughout.
#   plant_fault First install and upgrade with an unpowered drive: keep the controller, warn, refuse motion until
#              recovery and explicit ClearFault. Runs first by default, before a controller exists (C12 step 12).
#   migration  A controller moved from the Compose `pi` profile to the deploy script and back to the Compose version,
#              following "Moving between Compose and the deploy script" in docs/deployment.md, and the refusals that
#              keep the two from managing the same controller. The script's --verify-remote checks each Compose
#              controller from this machine without Docker, and rejects a key the controller does not know. A person
#              added on the Compose controller, and the session they opened there, still work after each move: both
#              mount the same identity directory. So does a setting changed through the API on the Compose controller:
#              both mount the same settings directory.
#   pull       The deploy script's pull mode (C12 step 11): a released image, an index of two platforms as a release
#              publishes it, pushed to a registry of this run's own on loopback and deployed by the index's digest with
#              no build; a reference without a digest, a digest the registry does not hold and an image with nothing for
#              the other platform each change nothing; --rollback between the pulled and the built version, with an
#              IMAGE_REF it ignores. Then a release offline (#127): its image split into its offline archives by
#              build/release-assets.py, the registry removed, and the archive deployed (IMAGE_ARCHIVE), loaded and run
#              by its ID with nothing pulled; an archive of another image changes nothing; --rollback to the pulled
#              version and back.
#
# The controller runs as the deploy script runs it (docs/deployment.md, "The container's privileges and limits"): a
# read-only root filesystem, only the supervisor's capabilities and the default limits (1 vCPU, 2g of memory with no
# swap, and 512 processes, #149; c12 gives a rollback others, which persist, then the default again). Each group reports
# the container's memory use against its limit; supervisor measures it under load. Without CAP_DAC_OVERRIDE, root in
# the container reaches its mounts only where root owns them, as the installer and the deployment docs make them, so the
# fixtures (secrets, certificate, identity and settings directories) are given to root with sudo -n.
#
# Needs docker (buildx, and compose 2.24 or later), curl, jq, openssl and sudo without a password (sudo -n), as GitHub's
# runners have it. It runs only against the local Docker daemon
# (the default context, with DOCKER_HOST unset or a unix socket), not on a Raspberry Pi, and touches no hardware: the
# controller runs in HAT emulator mode, maps no host device, and reaches the emulator over a network of this run's own.
# The controller is named roof-controller, as the script and the Compose `pi` profile name it, so the run refuses to
# start while a roof-controller container, or this run's emulator container or network, exists. Only a run that got
# past that check removes them on exit; the images stay (the build cache).
#
#   tests/emulator/deploy-scenarios.sh [plant_fault] [lifecycle] [supervisor] [c12] [migration] [pull]
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

# The bounds C11 steps 6 and 10 hold the supervisor to: the seconds from the controller's kill until the relay register
# reads 0 at the emulator (the relay-off helper's own start included; 0.10-0.17 s on the x64 rig), and until the emulated
# roof is still (the drive's coast stop after that; 0.30-0.38 s). They allow for a loaded CI runner, and are below what
# the restarted controller alone can do (its backoff is 1 s, then .NET starts): before #138 that took 2.0-2.4 s. On the
# Pi, .NET starts more slowly; that is not measured here.
relays_off_bound=1
still_bound=2

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

# desired_records: the deploy script's desired-limits records for this run's controller (<name>-limits-g<generation>,
# and the earlier format's <name>-limits), one a line.
desired_records() {
  docker volume ls --quiet --filter "name=${controller}-limits" 2>/dev/null | grep -E "^${controller}-limits(-g[0-9]+)?\$" || true
}

remove_controllers() {
  local record
  docker compose -f "${compose_file}" -f "${work}/compose.override.yaml" -p "${compose_project}" --profile pi \
    down --volumes --remove-orphans >/dev/null 2>&1 || true
  # With their volumes: each controller keeps the web UI's sign-in keys on one of its own. And the deploy script's desired
  # limits' records, each a volume of its own.
  docker rm -f -v "${controller}" "${previous}" "${controller}-swap" >/dev/null 2>&1 || true
  for record in $(desired_records); do
    docker volume rm -f "${record}" >/dev/null 2>&1 || true
  done
}

cleanup() {
  local status=$?
  set +e
  stop_background_deploy
  stop_camera_stream
  if (( owns_resources != 1 )); then
    # Refused before it created anything: what exists belongs to someone else.
    write_results
    rm -rf "${work}" 2>/dev/null || sudo -n rm -rf "${work}"
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
  docker rm -f -v "${emulator_name}" "${registry_name}" >/dev/null 2>&1
  # The pulled image (by its digest); the images the run built stay, as the build cache.
  docker images --digests --format '{{.Repository}}@{{.Digest}}' "${release_repository}" 2>/dev/null \
    | grep '@sha256:' | xargs -r docker rmi >/dev/null 2>&1
  # The image loaded from its offline archive, by its tag: the classic image store gives it no digest.
  docker images --format '{{.Repository}}:{{.Tag}}' "${release_repository}" 2>/dev/null \
    | grep -v ':<none>$' | xargs -r docker rmi >/dev/null 2>&1
  docker network rm "${network}" >/dev/null 2>&1
  write_results
  # The fixtures are root's.
  rm -rf "${work}" 2>/dev/null || sudo -n rm -rf "${work}"
  exit "${status}"
}

# as_root <command...>: a command on a fixture, which is root's as on a Pi.
as_root() {
  sudo -n "$@"
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

# kill_supervised_at <controller|ui>: kill_supervised, printing when the kill was sent, as `now` prints it, from the
# container (which shares this machine's clock).
kill_supervised_at() {
  local pid
  pid=$(supervised_value ".$1.pid // empty")
  [[ "${pid}" =~ ^[0-9]+$ ]] || fail "the supervisor reports no running $1 to kill: $(supervisor_state)"
  docker exec "${controller}" bash -c "kill -KILL ${pid} && date +%s.%N"
}

# seconds_until <since> <jq filter>: polls the emulated plant as fast as it answers until it matches the filter, and
# prints the seconds since <since> (a `now` value), to the hundredth: when it happened, late by at most one poll. Fails
# after 60 s.
seconds_until() {
  local since=$1 filter=$2 deadline=$((SECONDS + 60))
  until plant_is "${filter}"; do
    (( SECONDS < deadline )) || fail "timed out after 60 s waiting for the plant to match ${filter}: $(plant)"
  done
  awk -v s="${since}" -v n="$(now)" 'BEGIN { printf "%.2f", n - s }'
}

# within <seconds> <bound>: the time is under the bound.
within() {
  awk -v t="$1" -v b="$2" 'BEGIN { exit !(t < b) }'
}

# relays_off_before_start <exit text> <start number> <since>: the supervisor's log, since a `now` value, says it turned
# the relays off after the controller's exit (the text in the reason, such as "killed by signal 9") before it started the
# controller for that start.
relays_off_before_start() {
  local log off started
  log=$(docker logs --since "$3" "${controller}" 2>&1)
  off=$(grep -n -m 1 -E "^\[supervisor\] The relays are off: .* \(the controller exited \($1\)\)$" <<<"${log}" | cut -d: -f1 || true)
  started=$(supervisor_log_line ", start $2)" "$3")
  [[ -n "${off}" && -n "${started}" ]] && (( off < started ))
}

# supervised_pid <name>: the pid, in the container, of the supervisor (supervisor), the controller or the web UI.
supervised_pid() {
  if [[ "$1" == supervisor ]]; then
    # tini's child that runs the supervisor's script (docker exec's processes are not tini's; orphans it reaps may be).
    # shellcheck disable=SC2016 # expanded in the container
    docker exec "${controller}" bash -c 'for pid in $(cat /proc/1/task/1/children); do
        if tr "\0" " " <"/proc/${pid}/cmdline" | grep -q roof-supervisor; then echo "${pid}"; break; fi
      done'
  else
    supervised_value ".$1.pid"
  fi
}

# proc_value <pid> <file>: a value from /proc/<pid> in the container.
proc_value() {
  docker exec "${controller}" cat "/proc/$1/$2"
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

# drive_powerup_complete <plant elapsed sampled after power-on>: C4's SMVector start lockout lasts 2 s (SV01J p.27).
# Healthy IN3 and a successful ClearFault do not establish that it has elapsed. Use the plant's clock, including its
# time scale, and require a powered, stopped, untripped drive before handing it to a scenario that may start motion.
drive_powerup_complete() {
  plant | jq -e --arg powered_at "$1" '
    def seconds:
      capture("^(?:(?<d>[0-9]+)\\.)?(?<h>[0-9]{2}):(?<m>[0-9]{2}):(?<s>[0-9]+(?:\\.[0-9]+)?)$")
      | (((.d // "0") | tonumber) * 86400 + (.h | tonumber) * 3600 + (.m | tonumber) * 60 + (.s | tonumber));
    .drivePowered and .driveMode == "Stopped" and .driveTrip == "None"
      and ((.elapsed | seconds) >= (($powered_at | seconds) + 2))' >/dev/null
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

# container_cgroup <container>: its cgroup's cpu.max, memory.max, memory.swap.max and pids.max (cgroup v2), on one line;
# empty where the host has cgroup v1.
container_cgroup() {
  if docker exec "$1" test -e /sys/fs/cgroup/cgroup.controllers; then
    docker exec "$1" cat /sys/fs/cgroup/cpu.max /sys/fs/cgroup/memory.max /sys/fs/cgroup/memory.swap.max /sys/fs/cgroup/pids.max \
      | paste -sd ' ' || fail "could not read the cgroup of $1"
  fi
}

container_running() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null)" == true ]]
}

# The memory limit the scenarios run with: RoofController's default (#149).
default_memory_bytes=2147483648

# memory_report <container>: its memory against its limit, from its cgroup (cgroup v2): the most it has used since it
# started (memory.peak; memory.current on a kernel without it, before 5.19), its limit (memory.max) and the times the
# kernel ended a process at that limit (memory.events oom_kill), with each supervised process's peak resident memory
# (VmHWM: the controller's and the web UI's). Sets MEMORY_TEXT, MEMORY_PEAK and MEMORY_OOM_KILLS; empty text where the
# host has cgroup v1.
MEMORY_TEXT="" MEMORY_PEAK=0 MEMORY_OOM_KILLS=0
memory_report() {
  local name=$1 cgroup peak max kills controller_pid ui_pid controller_hwm ui_hwm controller_text="?" ui_text="?"
  MEMORY_TEXT="" MEMORY_PEAK=0 MEMORY_OOM_KILLS=0
  docker exec "${name}" test -e /sys/fs/cgroup/cgroup.controllers || return 0
  cgroup=$(docker exec "${name}" sh -c 'cat /sys/fs/cgroup/memory.peak 2>/dev/null || cat /sys/fs/cgroup/memory.current; cat /sys/fs/cgroup/memory.max; sed -n "s/^oom_kill //p" /sys/fs/cgroup/memory.events') \
    || fail "could not read the memory of ${name}'s cgroup"
  { read -r peak; read -r max; read -r kills; } <<<"${cgroup}"
  [[ "${peak}" =~ ^[0-9]+$ ]] || fail "${name}'s cgroup reports no memory use: ${cgroup}"
  controller_pid=$(supervised_value '.controller.pid')
  ui_pid=$(supervised_value '.ui.pid')
  controller_hwm=$(docker exec "${name}" sed -n 's/^VmHWM:[[:space:]]*\([0-9]*\) kB/\1/p' "/proc/${controller_pid}/status" 2>/dev/null || true)
  ui_hwm=$(docker exec "${name}" sed -n 's/^VmHWM:[[:space:]]*\([0-9]*\) kB/\1/p' "/proc/${ui_pid}/status" 2>/dev/null || true)
  MEMORY_PEAK=${peak}
  MEMORY_OOM_KILLS=${kills:-0}
  MEMORY_TEXT="container peak $(( peak / 1048576 )) MiB of its ${max} limit"
  if [[ "${max}" =~ ^[0-9]+$ ]]; then
    MEMORY_TEXT="container peak $(( peak / 1048576 )) MiB of its $(( max / 1048576 )) MiB limit ($(( (max - peak) / 1048576 )) MiB headroom, $(( max / (peak > 0 ? peak : 1) ))x)"
  fi
  [[ ! "${controller_hwm}" =~ ^[0-9]+$ ]] || controller_text="$(( controller_hwm / 1024 )) MiB"
  [[ ! "${ui_hwm}" =~ ^[0-9]+$ ]] || ui_text="$(( ui_hwm / 1024 )) MiB"
  # Resident memory counts the pages the two share (the .NET runtime's) in each.
  MEMORY_TEXT+="; peak resident: controller ${controller_text}, web UI ${ui_text}; OOM kills ${MEMORY_OOM_KILLS}"
}

# memory_check <container> <what>: memory_report as a check of its own, with the container at the default memory limit
# and no process ended at it.
memory_check() {
  local name=$1 what=$2 max
  current_check="Memory at the default limit: ${what}"
  max=$(docker inspect --format '{{.HostConfig.Memory}}' "${name}") || fail "could not inspect ${name}"
  (( max == default_memory_bytes )) || fail "${name} runs with a memory limit of ${max}, not the default ${default_memory_bytes}"
  memory_report "${name}"
  [[ -n "${MEMORY_TEXT}" ]] || { pass "the host has cgroup v1: not measured"; return 0; }
  (( MEMORY_OOM_KILLS == 0 )) || fail "the kernel ended ${MEMORY_OOM_KILLS} process(es) at the memory limit: ${MEMORY_TEXT}"
  (( MEMORY_PEAK < default_memory_bytes )) || fail "the container reached its memory limit: ${MEMORY_TEXT}"
  pass "${MEMORY_TEXT}"
}

# load_for <seconds>: 24 clients calling Status, 6 reading the web UI's home page and 2 camera streams at once, through
# the published ports, until the time is up. Each client is a loop of the scenario's own that ends by itself, and is
# waited for by its recorded PID.
load_for() {
  local deadline client pids=()
  deadline=$(( $(date +%s) + $1 ))
  for client in $(seq 24); do
    (
      while (( $(date +%s) < deadline )); do
        printf 'X-Api-Key: %s\n' "${operator_key}" \
          | curl -fsS --max-time 5 --cacert "${work}/ca.pem" -H @- -o /dev/null "${roof_api}/Status" 2>/dev/null || true
      done
    ) &
    pids+=($!)
  done
  for client in $(seq 6); do
    (
      while (( $(date +%s) < deadline )); do
        curl -fsS --max-time 5 --cacert "${work}/ca.pem" -o /dev/null "${web}/" 2>/dev/null || true
      done
    ) &
    pids+=($!)
  done
  for client in 1 2; do
    printf 'X-Api-Key: %s\n' "${operator_key}" \
      | curl -sS -N --max-time "$1" --cacert "${work}/ca.pem" -H @- -o /dev/null "${roof}/api/v1.0/Camera/2/mjpeg" 2>/dev/null &
    pids+=($!)
  done
  for client in "${pids[@]}"; do
    wait "${client}" || true
  done
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
  sudo -n true 2>/dev/null || fail "sudo without a password (sudo -n) is required: the fixtures are given to root, as the installer makes them"
  docker buildx version >/dev/null 2>&1 || fail "docker buildx is required"
  docker compose version >/dev/null 2>&1 || fail "docker compose is required"
  if [[ -n "$(docker ps -aq --filter "name=^/${controller}(-previous|-swap)?$")" ]]; then
    fail "a ${controller} container exists on this Docker host; these scenarios deploy their own under that name. Remove it first."
  fi
  if [[ -n "$(desired_records)" ]]; then
    fail "a ${controller}-limits volume exists on this Docker host ($(desired_records | tr '\n' ' ')): the deploy script's desired-limits records, which these scenarios' deploys write their own in. Remove them first."
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
  # Root's, as the installer and docs/deployment.md make them: the controller, root without CAP_DAC_OVERRIDE, reads and
  # writes them as their owner.
  as_root chown -R 0:0 "${work}/certs" "${work}/secrets" "${work}/secrets-wrong-password" "${work}/secrets-viewer-only" \
    "${work}/identity" "${work}/config" "${work}/settings-secrets" || fail "could not give the fixtures to root"

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
      - type: volume
        target: /var/lib/hvo-roof-web
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
    ! deploy_log_has "The previous version is kept as" || fail "a first deploy said it kept a previous version"
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
  # The supervisor sends SIGTERM to the controller and the web UI at once and waits for both (the controller's shutdown
  # stops the roof): the web UI is told before the controller has stopped (#139 F6).
  local controller_stopping controller_stopped ui_stopping ui_stopped
  controller_stopping=$(supervisor_log_line '[supervisor] Stopping the controller (SIGTERM' "${start}")
  controller_stopped=$(supervisor_log_line '[supervisor] The controller stopped (exit code 0)' "${start}")
  ui_stopping=$(supervisor_log_line '[supervisor] Stopping the web UI (SIGTERM' "${start}")
  ui_stopped=$(supervisor_log_line '[supervisor] The web UI stopped (' "${start}")
  if [[ -z "${controller_stopping}" || -z "${controller_stopped}" || -z "${ui_stopping}" || -z "${ui_stopped}" ]] \
    || (( controller_stopping > controller_stopped || ui_stopping > controller_stopped || ui_stopping > ui_stopped )); then
    fail "the supervisor did not stop the controller (exit code 0) and the web UI at once: lines ${controller_stopping:-none}, ${ui_stopping:-none}, ${controller_stopped:-none}, ${ui_stopped:-none}"
  fi
  # Then, once both had stopped, the supervisor turned the relays off itself, as it does whenever the controller has
  # stopped.
  local relays_off
  relays_off=$(supervisor_log_line '(the supervisor is stopping)' "${start}")
  if [[ -z "${relays_off}" ]] || (( relays_off < controller_stopped || relays_off < ui_stopped )); then
    fail "the supervisor did not turn the relays off after the controller and the web UI stopped (line ${relays_off:-none})"
  fi
  container_log_lacks "${controller}" 'WARNING: the relays could not be verified off' "${start}" \
    || fail "the supervisor could not verify the relays off"
  container_log_lacks "${controller}" 'did not stop within' "${start}" \
    || fail "the supervisor killed a process at the end of its wait"
  wait_for "the camera stream to end" 5 camera_stream_ended
  stream_seconds=$(awk -v s="${start}" -v e="$(cat "${work}/camera.ended")" 'BEGIN { printf "%.1f", e - s }')
  stop_camera_stream
  # Held open, the stream would keep the web server stopping until the host's 20 s shutdown timeout.
  awk -v t="${stream_seconds}" 'BEGIN { exit !(t < 5) }' || fail "the camera stream ended ${stream_seconds} s after docker stop"
  awk -v t="${stop_seconds}" 'BEGIN { exit !(t < 5) }' || fail "docker stop took ${stop_seconds} s with a camera stream open"
  wait_for "the emulated camera's stream closed" 5 camera_is '.openStreams == 0'
  pass "the supervisor stopped the controller and the web UI at once; the controller stopped the roof (relays off, HostShutdown) and exited 0, and the supervisor turned the relays off again once both had stopped; the container exited ${exit_code} in ${stop_seconds} s; the camera stream ended ${stream_seconds} s after docker stop"

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
  # The whole container killed, the supervisor with it: nothing in it can turn the relays off.
  docker kill "${controller}" >/dev/null
  local held
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays were released when the process died; the HAT holds them until something writes the register"
  container_stopped "${controller}" || fail "the killed container restarted by itself (or docker could not say)"
  start=$(now)
  docker start "${controller}" >/dev/null
  local off_seconds relays_off_line started_line
  off_seconds=$(seconds_until "${start}" '.relayRegister == 0')
  # The new supervisor turns them off before it starts the controller.
  wait_for "the new supervisor to start the controller" 60 \
    container_log_has "${controller}" '[supervisor] Started the controller (pid' "${start}"
  relays_off_line=$(supervisor_log_line '(before the controller starts)' "${start}")
  started_line=$(supervisor_log_line '[supervisor] Started the controller (pid' "${start}")
  if [[ -z "${relays_off_line}" || -z "${started_line}" ]] || (( relays_off_line > started_line )); then
    fail "the new supervisor did not turn the relays off before it started the controller (lines ${relays_off_line:-none}, ${started_line:-none})"
  fi
  wait_for "the controller ready" 120 controller_ready
  assert_relays_off
  pass "the relays were held (register ${held}) after docker kill, with the container stopped; once it was started again, its supervisor turned them off ${off_seconds} s after docker start, before it started the controller"

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
  container_log_has "${controller}" '(the supervisor is stopping)' "${start}" \
    || fail "the supervisor did not turn the relays off after the controller stopped"
  container_log_lacks "${controller}" 'WARNING: the relays could not be verified off' "${start}" \
    || fail "the supervisor could not verify the relays off after the controller stopped"
  plant_is '.relayRegister == 0' || fail "the relays are still energized after the container stopped: $(plant)"
  assert_relays_off
  pass "the relay writes failed at the stop, a retry verified the relays off once they worked again, the controller exited 0 and the supervisor verified the relays off too, in ${stop_seconds} s"

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
  # The supervisor tried too, through the same failing writes, and says the relays are not verified off.
  container_log_has "${controller}" 'WARNING: the relays could not be verified off (the supervisor is stopping)' "${start}" \
    || fail "the supervisor did not report the relays unverified"
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays are off although no relay write reached the HAT"
  emulator_post bus '{"failWrites": false}'
  start=$(now)
  docker start "${controller}" >/dev/null
  off_seconds=$(seconds_until "${start}" '.relayRegister == 0')
  wait_for "the new supervisor to turn the relays off before the controller starts" 60 \
    container_log_has "${controller}" '(before the controller starts)' "${start}"
  wait_for "the controller ready" 120 controller_ready
  assert_relays_off
  pass "the controller logged the failed stop and exited ${exit_code} in ${stop_seconds} s with the relays held (register ${held}), and the supervisor reported them unverified; once the writes worked, the new supervisor turned them off ${off_seconds} s after docker start, before the controller started"

  memory_check "${controller}" "the lifecycle group's container, since its last start"
}

# ---------------------------------------------------------------------------------------------------------------------
# supervisor

# The container's two processes, each ended inside the deployed container as a crash ends it. Docker never restarts the
# container: the supervisor restarts the process, or leaves a crash-looping controller stopped.
scenario_supervisor() {
  ensure_deployed
  close_roof
  wait_for "the web UI live" 60 web_ui_live
  local container restarts starts ui_pid ui_starts held start off_seconds killed still_seconds

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

  current_check="Supervisor: the container's privileges and limits"
  local config controller_pid status copies control cgroup
  # As the deploy script gave them, and as Docker kept them.
  config=$(docker inspect --format '{{json .HostConfig}}' "${controller}") || fail "could not inspect ${controller}"
  jq -e '.ReadonlyRootfs == true and .CapDrop == ["ALL"]
    and ([.CapAdd[] | ltrimstr("CAP_")] == ["CHOWN", "KILL", "SETGID", "SETUID"])
    and (.SecurityOpt | index("no-new-privileges") != null)
    and .NanoCpus == 1000000000 and .Memory == 2147483648 and .MemorySwap == 2147483648 and .PidsLimit == 512
    and .Tmpfs == {"/tmp": "size=64m", "/run/hvo-roof": "size=4m,mode=0755", "/root/.aspnet": "size=4m,mode=0700"}' <<<"${config}" >/dev/null \
    || fail "${controller} does not have the deploy script's privileges and limits: $(jq -c '{ReadonlyRootfs, CapDrop, CapAdd, SecurityOpt, NanoCpus, Memory, MemorySwap, PidsLimit, Tmpfs}' <<<"${config}")"
  docker inspect --format '{{range .Mounts}}{{if eq .Destination "/var/lib/hvo-roof-web"}}{{.Type}}{{end}}{{end}}' "${controller}" \
    | grep -qx volume || fail "the web UI's keys are not on a volume of the container's own"
  # Nothing writes the root filesystem, not even root.
  expect_refused root "root can write the container's root filesystem" touch /hvo-scenario-probe
  # The controller keeps the supervisor's capabilities (CHOWN, KILL, SETGID, SETUID: 0xe1), the web UI none, and neither
  # can gain any.
  controller_pid=$(supervised_value '.controller.pid')
  status=$(docker exec "${controller}" grep -E '^(CapEff|NoNewPrivs):' "/proc/${controller_pid}/status" | tr -s '\t ' ' ' | paste -sd ' ') \
    || fail "could not read the controller's status"
  [[ "${status}" == "CapEff: 00000000000000e1 NoNewPrivs: 1" ]] || fail "the controller (pid ${controller_pid}) has ${status}"
  status=$(docker exec -u app "${controller}" grep -E '^(CapEff|NoNewPrivs):' "/proc/${ui_pid}/status" | tr -s '\t ' ' ' | paste -sd ' ') \
    || fail "could not read the web UI's status"
  [[ "${status}" == "CapEff: 0000000000000000 NoNewPrivs: 1" ]] || fail "the web UI (pid ${ui_pid}) has ${status}"
  # Without CAP_FOWNER and CAP_DAC_OVERRIDE the supervisor gives the web UI its copies (app's, 0400) and keeps the control
  # directory, which the web UI writes, its own (root:app 0770).
  copies=$(docker exec "${controller}" stat -c '%U:%G %a' /run/hvo-roof/web/certificate.pfx /run/hvo-roof/web/certificate-password | paste -sd ' ') \
    || fail "no certificate copies in /run/hvo-roof/web"
  [[ "${copies}" == "app:app 400 app:app 400" ]] || fail "the web UI's certificate copies are ${copies}, not app:app 400"
  control=$(docker exec "${controller}" stat -c '%U:%G %a' /run/hvo-roof/control) || fail "no /run/hvo-roof/control"
  [[ "${control}" == "root:app 770" ]] || fail "/run/hvo-roof/control is ${control}, not root:app 770"
  # The kernel enforces the default limits (#149): 1 vCPU, 2g with no swap and 512 processes, in the container's own
  # cgroup (cgroup v2).
  cgroup=$(container_cgroup "${controller}")
  [[ -z "${cgroup}" || "${cgroup}" == "100000 100000 2147483648 0 512" ]] \
    || fail "the container's cgroup has cpu.max, memory.max, memory.swap.max and pids.max ${cgroup}"
  pass "read-only root filesystem (tmpfs /tmp, /run/hvo-roof, /root/.aspnet; a volume for the web UI's keys); the controller has only CHOWN, KILL, SETGID and SETUID, the web UI none, both no-new-privileges; the web UI's copies are app:app 400 and the control directory root:app 770; by default 1 vCPU, 2g with no swap and 512 processes${cgroup:+ (cgroup: ${cgroup})}"

  # The headroom the 2g default leaves the controller and its web UI together (#149): the API, the web UI and camera
  # streams from 32 clients at once for 30 s, with a move.
  local load_pid
  load_for 30 &
  load_pid=$!
  start_travel Open
  wait "${load_pid}" || true
  wait_for "the roof still" 60 roof_is '.isMoving == false'
  close_roof
  memory_check "${controller}" "the controller and the web UI under 32 clients for 30 s (API, web UI, camera streams) with a move"

  container=$(container_id "${controller}")
  restarts=$(docker inspect --format '{{.RestartCount}}' "${controller}")

  # CommissioningCheck("C11", "6")
  current_check="Supervisor: the controller killed during travel inside the container (C11 6)"
  starts=$(supervised_value '.controller.starts')
  start_travel Open
  held=$(plant | jq '.relayRegister')
  start=$(now)
  killed=$(kill_supervised_at controller) || fail "could not kill the controller"
  # The supervisor turns the relays off as soon as the controller has died, before its backoff and the new start.
  off_seconds=$(seconds_until "${killed}" '.relayRegister == 0')
  still_seconds=$(seconds_until "${killed}" '.velocityMetersPerSecond == 0')
  within "${off_seconds}" "${relays_off_bound}" \
    || fail "the relays went off ${off_seconds} s after the kill, not within ${relays_off_bound} s"
  within "${still_seconds}" "${still_bound}" || fail "the roof was still ${still_seconds} s after the kill, not within ${still_bound} s"
  wait_for "the supervisor to start the controller again" 30 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  supervised_is '.controller.lastExitReason == "crashed (killed by signal 9)" and .controller.recentCrashes == 1' \
    || fail "the supervisor does not report one crash: $(supervisor_state)"
  relays_off_before_start "killed by signal 9" "$((starts + 1))" "${start}" \
    || fail "the supervisor did not turn the relays off (verified) before it started the controller again"
  wait_for "the controller ready" 120 controller_ready
  supervised_is ".ui.state == \"running\" and .ui.pid == ${ui_pid}" || fail "the web UI did not keep running: $(supervisor_state)"
  [[ "$(container_id "${controller}")" == "${container}" ]] || fail "the container was replaced"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  assert_relays_off
  pass "the controller was killed while the relays were ${held}; the supervisor turned them off (verified) ${off_seconds} s after the kill (bound ${relays_off_bound} s) and the roof was still ${still_seconds} s after it (bound ${still_bound} s), before it started the controller again after its backoff; the web UI kept running and Docker did not restart the container"

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
  grep -qF 'controller: NOT AVAILABLE' <<<"${health}" || fail "the health check does not report the controller not ready: ${health}"
  grep -qF 'supervisor: controller crash-loop, web UI running' <<<"${health}" \
    || fail "the health check does not report the crash loop: ${health}"
  container_running "${controller}" || fail "the container stopped"
  (( $(docker inspect --format '{{.RestartCount}}' "${controller}") == restarts )) || fail "Docker restarted the container"
  web_ui_live || fail "the web UI is not live"
  wait_for "the web UI to say the controller is stopped after repeated crashes" 15 \
    web_page_has 'The controller is stopped after repeated crashes'
  plant_is '.relayRegister == 0 and .velocityMetersPerSecond == 0' || fail "the relays are energized: $(plant)"
  supervised_is '.relays.state == "off" and .relays.reason == "the controller exited (killed by signal 9)"' \
    || fail "the supervisor does not report the relays verified off after the last crash: $(supervisor_state)"
  container_log_has "${controller}" 'The controller is left stopped with the relays off (verified by read-back).' \
    || fail "the supervisor did not say it left the controller stopped with the relays off"
  no_violations
  pass "after ${limit} crashes within $(supervised_value '.crashWindowSeconds') s the controller is left stopped, with the relays off as the supervisor verified after the last crash; the health check fails (${health}); the web UI is live and says why"

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
  container_log_has "${controller}" '(the controller was killed for a forced restart)' \
    || fail "the supervisor did not turn the relays off after it killed the controller"
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

  # CommissioningCheck("C11", "10")
  current_check="Supervisor: a start that fails after a kill during travel (C11 10)"
  # A settings file the controller refuses at its start (Program.Main), before it touches the HAT: its starts fail
  # until the file is fixed. The running controller does not read the file again. Written in the container, as root,
  # who owns the settings directory on a Pi; any file there before is kept aside and put back.
  local settings_file=/etc/hvo-roof/config/appsettings.Local.json failed
  # shellcheck disable=SC2016 # expanded in the container
  docker exec "${controller}" sh -c 'if [ -e "$1" ]; then cp -p "$1" "$1.scenario-saved"; fi' sh "${settings_file}" \
    || fail "could not keep the settings file aside"
  # shellcheck disable=SC2016 # expanded in the container
  printf '%s\n' '{ "RoofControllerOptionsV4": { "SafetyWatchdogTimeout": "00:00:01" } }' \
    | docker exec -i "${controller}" sh -c 'cat >"$1" && chmod 0644 "$1"' sh "${settings_file}" \
    || fail "could not write a settings file the controller refuses"
  starts=$(supervised_value '.controller.starts')
  start_travel Open
  held=$(plant | jq '.relayRegister')
  start=$(now)
  killed=$(kill_supervised_at controller) || fail "could not kill the controller"
  off_seconds=$(seconds_until "${killed}" '.relayRegister == 0')
  still_seconds=$(seconds_until "${killed}" '.velocityMetersPerSecond == 0')
  within "${off_seconds}" "${relays_off_bound}" \
    || fail "the relays went off ${off_seconds} s after the kill, not within ${relays_off_bound} s"
  within "${still_seconds}" "${still_bound}" || fail "the roof was still ${still_seconds} s after the kill, not within ${still_bound} s"
  wait_for "the restarted controller to refuse its settings" 60 \
    container_log_has "${controller}" 'The roof controller did not start: ' "${start}"
  wait_for "the supervisor to turn the relays off after the failed start" 30 \
    container_log_has "${controller}" '(the controller exited (exit code 1))' "${start}"
  relays_off_before_start "killed by signal 9" "$((starts + 1))" "${start}" \
    || fail "the supervisor did not turn the relays off (verified) before it started the controller again"
  container_log_lacks "${controller}" 'WARNING: the relays could not be verified off' "${start}" \
    || fail "the supervisor could not verify the relays off"
  plant_is '.relayRegister == 0 and .velocityMetersPerSecond == 0' || fail "the relays are energized: $(plant)"
  no_violations
  pass "the controller was killed while the relays were ${held}, with a settings file it refuses; the supervisor turned the relays off (verified) ${off_seconds} s after the kill and the roof was still ${still_seconds} s after it; the next start failed on the settings (exit 1) and the supervisor turned them off again"

  # CommissioningCheck("C11", "11")
  current_check="Supervisor: failed starts until the crash loop, the relays off throughout (C11 11)"
  wait_for "the supervisor to leave the controller stopped" 120 supervised_is '.controller.state == "crash-loop"'
  failed=$(docker logs --since "${start}" "${controller}" 2>&1 | grep -cF 'The roof controller did not start: ' || true)
  local relays_offs
  relays_offs=$(docker logs --since "${start}" "${controller}" 2>&1 \
    | grep -cE '^\[supervisor\] The relays are off: .* \(the controller exited \(exit code 1\)\)$' || true)
  (( failed >= 2 && relays_offs == failed )) \
    || fail "${failed} failed starts and ${relays_offs} verified relay-offs after them; expected one after each"
  supervised_is '.relays.state == "off" and .relays.reason == "the controller exited (exit code 1)"' \
    || fail "the supervisor does not report the relays verified off after the last failed start: $(supervisor_state)"
  container_log_has "${controller}" 'The controller is left stopped with the relays off (verified by read-back).' "${start}" \
    || fail "the supervisor did not say it left the controller stopped with the relays off"
  plant_is '.relayRegister == 0 and .velocityMetersPerSecond == 0' || fail "the relays are energized: $(plant)"
  no_violations
  # The cause fixed, a forced restart starts it again.
  # shellcheck disable=SC2016 # expanded in the container
  docker exec "${controller}" sh -c 'if [ -e "$1.scenario-saved" ]; then mv -f "$1.scenario-saved" "$1"; else rm -f "$1"; fi' \
    sh "${settings_file}" || fail "could not put the settings file back"
  starts=$(supervised_value '.controller.starts')
  request_forced_restart
  wait_for "the forced restart to start the controller" 15 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  assert_relays_off
  pass "${failed} starts failed on the settings file and the supervisor turned the relays off (verified) after each, then left the controller stopped with them off; once the file was fixed, a forced restart started it again"

  # CommissioningCheck("C11", "12")
  current_check="Supervisor: docker stop with the controller hung during travel (C11 12)"
  # SIGSTOP: the controller can neither stop the roof nor exit, as a controller blocked for good would be.
  local controller_stop_seconds exit_code stop_seconds killed_line off_line
  controller_stop_seconds=$(docker exec "${controller}" printenv HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS || echo 22)
  close_roof
  start_travel Open
  docker exec "${controller}" kill -STOP "$(supervised_value '.controller.pid')" || fail "could not stop the controller"
  held=$(plant | jq '.relayRegister')
  (( held != 0 )) || fail "the relays were off before the stop: $(plant)"
  start=$(now)
  docker stop -t 30 "${controller}" >/dev/null
  stop_seconds=$(seconds_since "${start}")
  exit_code=$(docker inspect --format '{{.State.ExitCode}}' "${controller}")
  (( exit_code == 0 )) || fail "the container exited ${exit_code} after docker stop (expected 0, not killed at the end of the grace period)"
  plant_is '.relayRegister == 0' || fail "the relays are energized after the container stopped: $(plant)"
  killed_line=$(supervisor_log_line "[supervisor] The controller did not stop within ${controller_stop_seconds}s; killing it (SIGKILL)" "${start}")
  off_line=$(supervisor_log_line '(the supervisor is stopping)' "${start}")
  if [[ -z "${killed_line}" || -z "${off_line}" ]] || (( killed_line > off_line )); then
    fail "the supervisor did not kill the hung controller and then turn the relays off (lines ${killed_line:-none}, ${off_line:-none})"
  fi
  container_log_lacks "${controller}" 'WARNING: the relays could not be verified off' "${start}" \
    || fail "the supervisor could not verify the relays off"
  assert_relays_off
  docker start "${controller}" >/dev/null
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  wait_for "the web UI live" 60 web_ui_live
  pass "with the controller hung during travel (relays ${held}), docker stop: the supervisor waited ${controller_stop_seconds} s, killed it and turned the relays off (verified); the container exited 0 in ${stop_seconds} s, within Docker's 30 s"

  # CommissioningCheck("C11", "13")
  current_check="Supervisor: the web UI and the controller have a higher oom_score_adj than the supervisor (C11 13)"
  local supervisor_pid controller_pid ui_pid_now adj_supervisor adj_controller adj_ui score_supervisor score_controller score_ui
  supervisor_pid=$(supervised_pid supervisor)
  controller_pid=$(supervised_pid controller)
  ui_pid_now=$(supervised_pid ui)
  [[ "${supervisor_pid}" =~ ^[0-9]+$ && "${controller_pid}" =~ ^[0-9]+$ && "${ui_pid_now}" =~ ^[0-9]+$ ]] \
    || fail "could not find the container's processes (${supervisor_pid:-?}, ${controller_pid:-?}, ${ui_pid_now:-?})"
  adj_supervisor=$(proc_value "${supervisor_pid}" oom_score_adj)
  adj_controller=$(proc_value "${controller_pid}" oom_score_adj)
  adj_ui=$(proc_value "${ui_pid_now}" oom_score_adj)
  (( adj_controller == (adj_supervisor + 100 > 1000 ? 1000 : adj_supervisor + 100) \
    && adj_ui == (adj_supervisor + 500 > 1000 ? 1000 : adj_supervisor + 500) )) \
    || fail "oom_score_adj: supervisor ${adj_supervisor}, controller ${adj_controller}, web UI ${adj_ui}; expected +100 and +500"
  score_supervisor=$(proc_value "${supervisor_pid}" oom_score)
  score_controller=$(proc_value "${controller_pid}" oom_score)
  score_ui=$(proc_value "${ui_pid_now}" oom_score)
  (( score_supervisor < score_controller && score_controller < score_ui )) \
    || fail "oom_score: supervisor ${score_supervisor}, controller ${score_controller}, web UI ${score_ui}; the supervisor must be the lowest and the web UI the highest"
  pass "oom_score_adj: supervisor ${adj_supervisor}, controller ${adj_controller}, web UI ${adj_ui}; oom_score: supervisor ${score_supervisor} < controller ${score_controller} < web UI ${score_ui} at that moment (a snapshot: memory use counts too), so the kernel favours ending the web UI, then the controller, before the supervisor"

  # CommissioningCheck("C11", "14")
  current_check="Supervisor: relays that cannot be verified off hold the controller stopped until they are (C11 14)"
  # The owner's decision (#138): "if the relays cannot be verified off, the controller stays stopped." The emulated HAT
  # fails every relay write, so no relay-off can verify the relays off after the controller is killed during travel.
  local held_health
  close_roof
  starts=$(supervised_value '.controller.starts')
  start_travel Open
  start=$(now)
  emulator_post bus '{"failWrites": true}'
  kill_supervised controller
  wait_for "the supervisor to hold the controller stopped" 30 supervised_is '.controller.state == "relays-unverified"'
  # It stays held while the tries go on (1, 2, 4 s ...), and never starts the controller.
  sleep 8
  supervised_is ".controller.state == \"relays-unverified\" and .controller.starts == ${starts} and .relays.state == \"unverified\"" \
    || fail "the controller was started, or the hold not reported, with the relays not verified off: $(supervisor_state)"
  plant_is '.relayRegister != 0' || fail "the relays are off although every relay write fails: $(plant)"
  container_log_has "${controller}" '[supervisor] WARNING: the controller is HELD STOPPED: the relays could not be verified off' "${start}" \
    || fail "the hold is not logged as a warning"
  container_log_has "${controller}" '(trying again: the controller is held stopped until the relays are verified off)' "${start}" \
    || fail "the tries while held are not logged"
  held_health=$(docker exec "${controller}" /usr/local/bin/roof-healthcheck) && fail "the health check passes with the controller held: ${held_health}"
  grep -qF 'supervisor: controller relays-unverified, web UI running' <<<"${held_health}" \
    || fail "the health check does not report the hold: ${held_health}"
  wait_for "the web UI to say the controller is held stopped" 15 \
    web_page_has 'The controller is held stopped: the relays cannot be verified off'
  # The HAT takes writes again: the next try verifies the relays off, and only then does the controller start.
  emulator_post bus '{"failWrites": false}'
  wait_for "the relays verified off" 60 plant_is '.relayRegister == 0'
  wait_for "the controller started once the relays were verified off" 60 \
    supervised_is ".controller.state == \"running\" and .controller.starts == $((starts + 1))"
  local verified_line started_line
  verified_line=$(supervisor_log_line '[supervisor] The relays are verified off: starting the controller, held stopped until now' "${start}")
  started_line=$(supervisor_log_line ", start $((starts + 1)))" "${start}")
  if [[ -z "${verified_line}" || -z "${started_line}" ]] || (( verified_line > started_line )); then
    fail "the controller did not start only after the relays were verified off (lines ${verified_line:-none}, ${started_line:-none})"
  fi
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized'
  assert_relays_off
  close_roof
  pass "with relay writes failing after a kill during travel, the supervisor held the controller stopped (state relays-unverified, the health check failing, the web UI saying why) and kept trying; once writes worked it verified the relays off and only then started the controller"

  # CommissioningCheck("C11")
  current_check="Supervisor: the supervisor itself killed during travel"
  # As the kernel would end it, or kill -9 inside the container: tini ends, the container with it, and Docker starts it
  # again (its restart policy); the new supervisor turns the relays off before it starts the controller.
  restarts=$(docker inspect --format '{{.RestartCount}}' "${controller}")
  supervisor_pid=$(supervised_pid supervisor)
  close_roof
  start_travel Open
  held=$(plant | jq '.relayRegister')
  start=$(now)
  docker exec "${controller}" kill -KILL "${supervisor_pid}" >/dev/null 2>&1 || true
  off_seconds=$(seconds_until "${start}" '.relayRegister == 0')
  wait_for "Docker to start the container again" 60 restarted_since "${restarts}"
  wait_for "the new supervisor to turn the relays off before the controller starts" 60 \
    container_log_has "${controller}" '(before the controller starts)' "${start}"
  wait_for "the controller ready" 120 controller_ready
  wait_for "the controller initialized" 60 roof_is '.isInitialized and .isMoving == false'
  wait_for "the web UI live" 60 web_ui_live
  assert_relays_off
  restarts=$(docker inspect --format '{{.RestartCount}}' "${controller}")
  pass "the supervisor was killed during travel (relays ${held}); Docker started the container again and its new supervisor turned the relays off ${off_seconds} s after the kill, before it started the controller"

  # CommissioningCheck("C11")
  current_check="Supervisor: the web UI's keys directory is in a directory only root can change"
  # What the links below name: a directory made for this step (in /tmp: the root filesystem is read-only), which only
  # root can change, so that a link followed by mistake changes nothing outside the container.
  local keys_dir=/var/lib/hvo-roof-web target=/tmp/hvo-scenario-target owners target_was setting
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
  docker exec "${controller}" bash -c "rm ${keys_dir}/keys && mv ${keys_dir}/keys.scenario ${keys_dir}/keys" \
    || fail "could not put ${keys_dir}/keys back"
  # ${keys_dir} itself is the volume's mount point, on the read-only root filesystem: not even root can put a link in its
  # place.
  expect_refused root "root can move ${keys_dir} aside" mv "${keys_dir}" "${keys_dir}.scenario"
  expect_refused root "root can make a link beside ${keys_dir}" ln -s "${target}" "${keys_dir}.scenario"
  docker exec "${controller}" rmdir "${target}" || fail "could not remove ${target}"
  restart_web_ui
  setting=$(web_ui_keys_setting) || fail "could not read the web UI's keys setting"
  [[ "${setting}" == "RoofWeb__DataProtectionPath=${keys_dir}/keys" ]] \
    || fail "the web UI was not given ${keys_dir}/keys again once the link was gone (${setting:-no setting})"
  pass "${keys_dir} is root:root 755 and its keys directory app:app 700; the web UI's user cannot make a link there, and a ${keys_dir} given to that user is root's again at the web UI's next start; a link in place of the keys directory (made by the web UI's user) is refused: the web UI keeps its keys in memory and writes none under its home, and ${target}, which the link named, is still ${target_was}; ${keys_dir}, a mount point on the read-only root filesystem, cannot be replaced; the directory is used again once the link is gone"
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

scenario_plant_fault() {
  # CommissioningCheck("C12", "12")
  current_check="C12 step 12: first install and upgrade with an unpowered drive"
  [[ -z "$(container_id "${controller}")" ]] || fail "plant_fault must run before any controller is deployed"
  emulator_post drive/power '{"powered":false}'
  start_relay_monitor
  local stage command response powered_at old_id=""
  for stage in first-install upgrade; do
    deploy
    (( DEPLOY_STATUS == 0 )) || fail "${stage} with drive power off failed (exit ${DEPLOY_STATUS})"
    expect_deploy_log "WARNING: PLANT FAULT DriveFault"
    expect_deploy_log "explicitly ClearFault"
    expect_deploy_log "Deployment complete and verified"
    container_running "${controller}" || fail "${stage} removed the faulted controller"
    if [[ -n "${old_id}" ]]; then
      [[ "$(container_id "${previous}")" == "${old_id}" ]] || fail "upgrade did not keep the previous controller"
      container_stopped "${previous}" || fail "previous controller still running"
    fi
    roof_is '.isFaultLatched and .latchedFaultReason == "DriveFault" and .commandedMotion == "None"' \
      || fail "${stage} did not keep the drive fault latched and stopped"
    local code
    code=$(curl -sS --max-time 5 --cacert "${work}/ca.pem" -o /dev/null -w '%{http_code}' "${roof}/health/available")
    [[ "${code}" == 200 ]] || fail "availability returned ${code}"
    code=$(curl -sS --max-time 5 --cacert "${work}/ca.pem" -o /dev/null -w '%{http_code}' "${roof}/health/ready")
    [[ "${code}" == 503 ]] || fail "operational readiness returned ${code}"
    docker exec "${controller}" /usr/local/bin/roof-healthcheck >/dev/null || fail "Docker health refused the plant fault"
    # ClearFault deliberately pulses RLY3 even when the active input ultimately refuses the clear. Keep that explicit
    # operator action after the deployment all-off sampling; the in-process scenario covers refusal while unpowered.
    for command in Open Close; do
      response=$(roof_call POST "${command}")
      [[ "$(tail -n 1 <<<"${response}")" == 409 ]] || fail "${command} accepted while drive unpowered"
    done
    old_id=$(container_id "${controller}")
  done
  relays_stayed_off
  assert_relays_off
  emulator_post drive/power '{"powered":true}'
  powered_at=$(plant | jq -er '.elapsed')
  wait_for "drive power recovery" 15 roof_is '.isDriveFaultActive == false'
  roof_is '.isFaultLatched' || fail "drive recovery automatically cleared the latch"
  response=$(roof_call POST Open)
  [[ "$(tail -n 1 <<<"${response}")" == 409 ]] || fail "motion accepted before explicit ClearFault"
  roof_post ClearFault >/dev/null
  wait_for "operational readiness after explicit ClearFault" 15 controller_ready
  wait_for "the drive's 2 s power-up start lockout to elapse" 15 drive_powerup_complete "${powered_at}"
  assert_relays_off
  no_violations
  pass "first install and upgrade kept the stopped controller with DriveFault warnings; Docker healthy, operational health 503, no motion before recovery and explicit ClearFault; relays 0 in ${RELAY_SAMPLES} samples; drive stopped after its 2 s power-up start lockout"
}

scenario_c12() {
  ensure_deployed
  close_roof

  # A configured identity path is not persisted until the first identity mutation.
  # Establish it through the API before testing rollback; missing/unreadable stores
  # must continue to fail closed in the deploy script.
  current_check="C12 setup: persisted identity for rollback"
  local identity_response
  openssl rand -hex 16 > "${work}/rollback-password"
  chmod 600 "${work}/rollback-password"
  jq -n --rawfile password "${work}/rollback-password" \
    '{name: "rollback-fixture", role: "RoofViewer", password: ($password | rtrimstr("\n"))}' > "${work}/rollback-person.json"
  identity_response=$(admin_call POST Identity/Users "${work}/rollback-person.json")
  [[ "$(tail -n 1 <<<"${identity_response}")" == 201 ]] \
    || fail "creating rollback fixture identity answered HTTP $(tail -n 1 <<<"${identity_response}")"
  as_root test -f "${work}/identity/identity.json" || fail "the controller did not persist the rollback fixture identity"
  unset identity_response
  rm -f "${work}/rollback-person.json" "${work}/rollback-password"
  pass "the controller persisted an identity before rollback; missing-store recovery remains fail-closed"

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

  # A rollback the roof's idle check refuses changes nothing of the version it would bring back, its limits included:
  # the limits are given only after every check that can refuse it.
  current_check="C12 step 6: a refused rollback leaves the version it would bring back as it was"
  local limits limits_before cgroup
  limits_before=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}}' "${previous}")
  start_travel Open
  deploy REQUIRE_IDLE_ROOF=true CONTAINER_CPUS=2.5 CONTAINER_MEMORY=4g -- --rollback
  (( DEPLOY_STATUS != 0 )) || fail "the rollback went ahead while the roof moved"
  expect_deploy_log "The roof is moving"
  ! deploy_log_has "Giving ${previous} the limits" || fail "the refused rollback gave ${previous} the limits"
  limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}}' "${previous}")
  [[ "${limits}" == "${limits_before}" ]] || fail "the refused rollback changed ${previous}'s limits from ${limits_before} to ${limits}"
  [[ "$(container_id "${controller}")" == "${current_id}" && "$(container_id "${previous}")" == "${previous_id}" ]] \
    || fail "the refused rollback swapped the versions"
  container_running "${controller}" || fail "the refused rollback stopped ${controller}"
  roof_post Stop >/dev/null
  assert_relays_off
  pass "refused while the roof moved; ${previous} kept its limits (${limits})"

  # The limits a rollback is given go to the version it brings back (docker update); the next rollback, given none,
  # keeps them from the controller it replaces (an override persists, #149); and the default given again ends them.
  current_check="C12 step 6: a rollback gives the version it brings back the limits, and they persist"
  deploy CONTAINER_CPUS=2.5 CONTAINER_MEMORY=4g -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback with CPU and memory limits failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[rollback] Giving ${previous} the limits: 2.5 CPUs, 4g of memory and no swap, at most 512 processes and threads"
  limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}} {{.HostConfig.ReadonlyRootfs}}' "${controller}")
  [[ "$(container_id "${controller}")" == "${previous_id}" && "${limits}" == "2500000000 4294967296 4294967296 512 true" ]] \
    || fail "the rolled-back controller runs with ${limits}, not 2.5 CPUs and 4g"
  cgroup=$(container_cgroup "${controller}")
  [[ -z "${cgroup}" || "${cgroup}" == "250000 100000 4294967296 0 512" ]] \
    || fail "the rolled-back controller's cgroup has cpu.max, memory.max, memory.swap.max and pids.max ${cgroup}"
  deploy -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback with no limits given failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[deploy] The container's limits: 2.5 CPUs (kept from the last deploy), 4g of memory (kept from the last deploy) and no swap"
  limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}}' "${controller}")
  [[ "$(container_id "${controller}")" == "${current_id}" && "${limits}" == "2500000000 4294967296 4294967296 512" ]] \
    || fail "the controller swapped back runs with ${limits}, not the 2.5 CPUs and 4g kept from the one it replaced"
  deploy CONTAINER_CPUS=1 CONTAINER_MEMORY=2g -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback with the default limits given failed (exit ${DEPLOY_STATUS})"
  limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}}' "${controller}")
  [[ "$(container_id "${controller}")" == "${previous_id}" && "${limits}" == "1000000000 2147483648 2147483648 512" ]] \
    || fail "the controller rolled back with the default runs with ${limits}, not 1 vCPU and 2g"
  assert_relays_off
  pass "a rollback with 2.5 CPUs and 4g brought back ${previous} with them${cgroup:+ (cgroup: ${cgroup})}; the next, given none, kept them for the version swapped back; the default given again brought back the first with 1 vCPU and 2g; relays off"

  # A rollback on a host whose Docker cannot enforce the memory limit, as on a Pi whose firmware turned the memory
  # controller off after an upgrade: a wrapper of the run's own in front of Docker makes the deploy script's docker info
  # say so, and everything else is real Docker. The rollback is a recovery path, so it goes ahead with the limits Docker
  # still enforces (the CPU and process limits) and warns, in its output and in the controller's log (#149). Then the
  # default given again, on Docker as it is.
  current_check="C12 step 6: a rollback on a host that cannot enforce the memory limit goes ahead and warns"
  local real_docker no_memory="${work}/no-memory-cgroup"
  real_docker=$(command -v docker)
  mkdir -p "${no_memory}"
  cat > "${no_memory}/docker" <<WRAPPER
#!/usr/bin/env bash
# Docker, except that the deploy script's docker info template reports MemoryLimit and SwapLimit false.
if [[ " \$* " == *" info "* && "\$*" == *"{{.MemoryLimit}},"* ]]; then
  "${real_docker}" "\$@" | awk -F, -v OFS=, 'NR == 1 { \$1 = "false"; \$4 = "false" } { print }'
  exit "\${PIPESTATUS[0]}"
fi
exec "${real_docker}" "\$@"
WRAPPER
  chmod 0755 "${no_memory}/docker"
  local swapped_limits rollback_start
  rollback_start=$(now)
  deploy "PATH=${no_memory}:${PATH}" -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback on a host that cannot enforce the memory limit was refused (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[rollback] WARNING: THE ROLLBACK GOES AHEAD, BUT DOCKER ON 127.0.0.1 CANNOT ENFORCE THESE LIMITS, so the version it brings back runs WITHOUT them."
  expect_deploy_log "[rollback] WARNING:   - docker-memory-limit-unsupported: docker info reports MemoryLimit false and SwapLimit false"
  if deploy_log_has "docker-cpu-quota-unsupported" || deploy_log_has "docker-pids-limit-unsupported"; then
    fail "the rollback warned of a limit Docker enforces"
  fi
  expect_deploy_log "enforces only these limits, which the rollback gives: 1 CPU, no memory limit (Docker here cannot enforce one), at most 512 processes and threads"
  expect_deploy_log "[rollback] The warning is in the controller's log too (docker logs ${controller})."
  swapped_limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.PidsLimit}}' "${controller}")
  # The version brought back had 2.5 CPUs and 4g: the CPU limit is given, the memory limit is left as it was.
  [[ "$(container_id "${controller}")" == "${current_id}" && "${swapped_limits}" == "1000000000 4294967296 512" ]] \
    || fail "the rollback gave the version it brought back ${swapped_limits}, not the CPU and process limits alone"
  container_log_has "${controller}" "[rollback] WARNING: from deploy-roofcontroller-rpi.sh --rollback," "${rollback_start}" \
    || fail "the warning is not in ${controller}'s log"
  container_log_has "${controller}" "[rollback] WARNING:   - docker-memory-limit-unsupported:" "${rollback_start}" \
    || fail "${controller}'s log does not name the memory limit"
  container_log_has "${controller}" 'Append " cgroup_enable=memory" to the end of the single line in /boot/firmware/cmdline.txt' "${rollback_start}" \
    || fail "${controller}'s log does not name the fix"
  # The desired limits outlive the rollback that could not enforce them (reviews ROOF-151-R1 F2, R2 F5): one record, the
  # rollback's, written for the version it brought back.
  local desired records
  records=$(desired_records)
  [[ -n "${records}" && "${records}" != *$'\n'* && "${records}" == "${controller}-limits-g"* ]] \
    || fail "the desired limits' records are '${records}', not one ${controller}-limits-g<generation>"
  desired=$(docker volume inspect --format '{{index .Labels "io.hvo.roof.limits"}} {{index .Labels "io.hvo.roof.limits.cpus"}} {{index .Labels "io.hvo.roof.limits.memory"}} {{index .Labels "io.hvo.roof.limits.written-by"}} {{index .Labels "io.hvo.roof.limits.controller"}}' "${records}") \
    || fail "the desired limits' record ${records} cannot be read"
  [[ "${desired}" == "1 1 2g rollback ${current_id:0:12}" ]] \
    || fail "the rollback on a host that cannot enforce the memory limit recorded '${desired}', not 1 CPU and 2g by the rollback for ${current_id:0:12}"
  deploy CONTAINER_CPUS=1 CONTAINER_MEMORY=2g -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback with the default limits, on Docker as it is, failed (exit ${DEPLOY_STATUS})"
  ! deploy_log_has "WARNING: THE ROLLBACK GOES AHEAD" || fail "a rollback on Docker as it is warned of a limit"
  [[ "$(container_id "${controller}")" == "${previous_id}" ]] || fail "the last rollback did not swap back"
  assert_relays_off
  pass "a rollback whose Docker reported no memory limit support went ahead in ${DEPLOY_SECONDS} s with 1 CPU and 512 processes (the memory limit left as it was), and warned in its output and in ${controller}'s log, naming the memory limit and the fix; the next, on Docker as it is, swapped back with no warning; relays off"

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

  memory_check "${controller}" "the controller C12 leaves running, since it started"
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
  as_root test -f "${work}/identity/identity.json" || fail "the Compose controller did not save identity.json in the identity mount"
  [[ "$(as_root stat -c %a "${work}/identity/identity.json")" == 600 ]] \
    || fail "identity.json is mode $(as_root stat -c %a "${work}/identity/identity.json"), not 600"
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
  as_root test -f "${settings_file}" || fail "the Compose controller did not save appsettings.Local.json in the settings mount"
  [[ "$(as_root stat -c %a "${settings_file}")" == 644 ]] \
    || fail "appsettings.Local.json is mode $(as_root stat -c %a "${settings_file}"), not 644"
  as_root cat "${settings_file}" | jq -e '.HvoRoofSettings.Version == 2 and .RoofControllerUi.DefaultCamera == "scenario-camera"
    and .RoofControllerUi.KioskScreenTimeout == "00:10:00"' >/dev/null \
    || fail "appsettings.Local.json does not hold the change and version 2: $(as_root cat "${settings_file}")"
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
  compose down --volumes >/dev/null 2>&1
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
  docker rm -v "${controller}" >/dev/null
  docker rm -v "${previous}" >/dev/null 2>&1 || true
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

  # Compose gives the default limits too (#149), to Docker and to the supervisor, which checks their floor.
  current_check="Migration: the Compose controller runs with the default limits"
  local compose_limits
  compose_limits=$(docker inspect --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{.HostConfig.PidsLimit}}' "${controller}") \
    || fail "could not inspect the Compose controller"
  [[ "${compose_limits}" == "1000000000 2147483648 2147483648 512" ]] || fail "the Compose controller runs with ${compose_limits}, not 1 vCPU and 2g"
  docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${controller}" | grep -qx 'HVO_ROOF_MEMORY=2g' \
    || fail "the Compose controller's supervisor was not given HVO_ROOF_MEMORY=2g"
  memory_report "${controller}"
  pass "NanoCpus, Memory, MemorySwap and PidsLimit ${compose_limits}, the supervisor given HVO_ROOF_CPUS and HVO_ROOF_MEMORY${MEMORY_TEXT:+; ${MEMORY_TEXT}}"

  compose down --volumes >/dev/null 2>&1
  docker rmi "${image}-compose" >/dev/null 2>&1 || true
}

# ---------------------------------------------------------------------------------------------------------------------
# pull

registry_ready() {
  curl -fsS --max-time 2 "http://127.0.0.1:${registry_port}/v2/" >/dev/null
}

# push_image <platform> <context> <tag>: builds the image for that platform and pushes it to the run's registry under
# that tag, with its digest in PUSHED_DIGEST. Only the registry keeps it: the deploy pulls by digest, as from a release.
PUSHED_DIGEST=""
push_image() {
  local output
  docker buildx build --quiet --provenance=false --platform "$1" -t "${release_repository}:$3" --load "$2" >/dev/null \
    || fail "could not build the released image for $1"
  output=$(docker push "${release_repository}:$3") || fail "could not push the released image for $1: ${output}"
  PUSHED_DIGEST=$(sed -n 's/^.*: digest: \(sha256:[0-9a-f]\{64\}\) .*/\1/p' <<<"${output}")
  [[ -n "${PUSHED_DIGEST}" ]] || fail "the push of the image for $1 reported no digest: ${output}"
  docker rmi "${release_repository}:$3" >/dev/null
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
  # A release's image as the release workflow publishes it, in a registry of this run's own on loopback: an index of
  # two platforms, deployed by the index's digest as release.json lists it. This run's platform is its controller image
  # under another version label, so the deploy has something new to pull; the other is an empty image, which builds
  # without emulation.
  local other_platform=linux/arm64
  [[ "${platform}" == linux/amd64 ]] || other_platform=linux/amd64
  docker run -d --name "${registry_name}" -p "127.0.0.1:${registry_port}:5000" "${registry_image}" >/dev/null \
    || fail "could not start the registry"
  wait_for "the registry" 60 registry_ready
  mkdir -p "${work}/release-image" "${work}/other-image"
  printf 'FROM %s\nLABEL org.opencontainers.image.version=4.0.0-scenario\n' "${image}" > "${work}/release-image/Dockerfile"
  printf 'FROM scratch\nLABEL org.opencontainers.image.version=4.0.0-scenario\n' > "${work}/other-image/Dockerfile"
  local tag=4.0.0-scenario single other digest ref
  push_image "${platform}" "${work}/release-image" "${tag}-${platform#linux/}"
  single=${PUSHED_DIGEST}
  push_image "${other_platform}" "${work}/other-image" "${tag}-${other_platform#linux/}"
  other=${PUSHED_DIGEST}
  docker buildx imagetools create --progress quiet -t "${release_repository}:${tag}" \
    "${release_repository}@${single}" "${release_repository}@${other}" >/dev/null \
    || fail "could not push the released image's index"
  digest=$(docker buildx imagetools inspect --format '{{json .Manifest}}' "${release_repository}:${tag}" | jq -r .digest)
  [[ "${digest}" =~ ^sha256:[0-9a-f]{64}$ && "${digest}" != "${single}" ]] \
    || fail "the registry reported no index digest for ${release_repository}:${tag}: '${digest}'"
  ref="${release_repository}:${tag}@${digest}"

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
  local unknown_digest single_ref="${release_repository}:${tag}-${platform#linux/}@${single}"
  unknown_digest="sha256:$(printf '%s' "not-${digest}" | sha256sum | cut -c1-64)"
  start_relay_monitor
  deploy "IMAGE_REF=${release_repository}:${tag}"
  (( DEPLOY_STATUS != 0 )) || fail "a reference without a digest was deployed"
  expect_deploy_log "IMAGE_REF must name a released image by its digest"
  expect_deploy_log "Nothing was changed."
  expect_untouched "${new_id}"
  deploy "IMAGE_REF=${release_repository}:${tag}@${unknown_digest}"
  (( DEPLOY_STATUS != 0 )) || fail "a digest the registry does not hold was deployed"
  expect_deploy_log "Could not pull ${release_repository}:${tag}@${unknown_digest}"
  expect_not_pulled "${new_id}"
  # An image with nothing for the other platform (this platform's own, by its digest): a registry refuses the platform
  # (no matching manifest), or the image the Pi then holds is checked and is not for it.
  deploy "IMAGE_REF=${single_ref}" "BUILD_PLATFORM=${other_platform}"
  (( DEPLOY_STATUS != 0 )) || fail "the image was deployed for ${other_platform}"
  deploy_log_has "Could not pull ${single_ref} for ${other_platform}" \
    || deploy_log_has "The image ${single_ref} is not for ${other_platform}" \
    || fail "the deploy for ${other_platform} did not fail at the pull or the platform check"
  expect_not_pulled "${new_id}"
  relays_stayed_off

  # --rollback swaps the pulled version with the one before (a built image) and back: each container keeps its image.
  # It ignores IMAGE_REF, even one a deploy would refuse.
  start_relay_monitor
  deploy "IMAGE_REF=${release_repository}:${tag}" -- --rollback
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
  pass "deployed the two-platform index ${digest:0:19}... from a registry in ${pull_seconds} s with no build; a reference without a digest, a digest the registry does not hold and an image with nothing for ${other_platform} each changed nothing; --rollback swapped to the built version and back; relays 0 throughout"

  # Still C12 step 11, for a release installed without a registry.
  current_check="C12 step 11: a released image loaded from its offline archive, with no registry"
  # Another release of the same image, split into its offline archives as build/release-assets.py splits the archive
  # the release workflow pushed; then the registry goes, so nothing can be pulled.
  local offline_tag=4.0.0-offline offline_digest offline_ref archive loaded_id
  printf 'FROM %s\nLABEL org.opencontainers.image.version=%s\n' "${image}" "${offline_tag}" > "${work}/release-image/Dockerfile"
  printf 'FROM scratch\nLABEL org.opencontainers.image.version=%s\n' "${offline_tag}" > "${work}/other-image/Dockerfile"
  push_image "${platform}" "${work}/release-image" "${offline_tag}-${platform#linux/}"
  single=${PUSHED_DIGEST}
  push_image "${other_platform}" "${work}/other-image" "${offline_tag}-${other_platform#linux/}"
  other=${PUSHED_DIGEST}
  docker buildx imagetools create --progress quiet -t "${release_repository}:${offline_tag}" \
    "${release_repository}@${single}" "${release_repository}@${other}" >/dev/null \
    || fail "could not push the offline release's index"
  offline_digest=$(docker buildx imagetools inspect --format '{{json .Manifest}}' "${release_repository}:${offline_tag}" | jq -r .digest)
  [[ "${offline_digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "the registry reported no index digest for ${release_repository}:${offline_tag}"
  offline_ref="${release_repository}:${offline_tag}@${offline_digest}"
  mkdir -p "${work}/offline"
  "${repo_root}/tests/releasing/offline-archives-from-registry.py" "127.0.0.1:${registry_port}" "${release_repository#*/}" \
    "${offline_digest}" roof-controller "${release_repository}" "${offline_tag}" "${work}/offline" > "${work}/offline.json" \
    || fail "could not make the offline archives"
  archive="${work}/offline/roof-controller-${offline_tag}-linux-${platform#linux/}.oci.tar"
  [[ -f "${archive}" ]] || fail "there is no offline archive for ${platform}"
  docker rm -f "${registry_name}" >/dev/null || fail "could not remove the registry"
  ! registry_ready || fail "the registry still answers"

  start_relay_monitor
  deploy "IMAGE_REF=${offline_ref}" "IMAGE_ARCHIVE=${archive}"
  (( DEPLOY_STATUS == 0 )) || fail "the deploy from the offline archive failed (exit ${DEPLOY_STATUS})"
  expect_deploy_log "[load] ${archive} holds ${offline_digest}, with its image for ${platform}"
  expect_deploy_log "[load] Checking a copy of ${archive} that only this user can change (SHA-256 "
  expect_deploy_log "[load] Loading the checked copy of ${archive} into Docker context 'default' (nothing is pulled)..."
  expect_deploy_log "[load] ${offline_digest} for ${platform}, image sha256:"
  expect_deploy_log ": version ${offline_tag}"
  expect_deploy_log "[done] Deployment complete and verified at ${roof}"
  ! deploy_log_has "[pull]" || fail "the deploy from the offline archive pulled"
  loaded_id=$(container_image "${controller}")
  jq -e --arg id "${loaded_id}" --arg digest "${offline_digest}" --arg platform "${platform}" \
    '.[] | select(.platform == $platform) | [$digest, .manifestDigest, .configDigest] | index($id) != null' \
    "${work}/offline.json" >/dev/null || fail "${controller} runs ${loaded_id}, which is not one of the archive's IDs"
  [[ "$(docker inspect --format '{{.Config.Image}}' "${controller}")" == "${loaded_id}" ]] \
    || fail "${controller} was not run by its image's ID"
  [[ "$(docker inspect --format '{{index .Config.Labels "io.hvo.roof.release-image"}}' "${controller}")" == "${offline_ref}" ]] \
    || fail "${controller} does not carry the release-image label naming ${offline_ref}"
  [[ "$(container_id "${previous}")" == "${new_id}" ]] || fail "the pulled controller is not kept as ${previous}"
  web_ui_live || fail "the web UI is not live at ${web}"
  relays_stayed_off
  assert_relays_off
  local offline_seconds=${DEPLOY_SECONDS} offline_id
  offline_id=$(container_id "${controller}")

  # An archive that does not hold the image IMAGE_REF names changes nothing, before any Docker call.
  deploy "IMAGE_REF=${ref}" "IMAGE_ARCHIVE=${archive}"
  (( DEPLOY_STATUS != 0 )) || fail "an archive of another image was deployed"
  expect_deploy_log "does not hold the image ${digest} that IMAGE_REF names"
  expect_untouched "${offline_id}"

  # --rollback swaps the loaded version with the pulled one and back, ignoring IMAGE_ARCHIVE.
  start_relay_monitor
  deploy "IMAGE_REF=${offline_ref}" "IMAGE_ARCHIVE=${archive}" -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback from the loaded image failed (exit ${DEPLOY_STATUS})"
  [[ "$(container_id "${controller}")" == "${new_id}" && "$(container_id "${previous}")" == "${offline_id}" ]] \
    || fail "the rollback did not swap the loaded version with the pulled one"
  ! deploy_log_has "[load]" || fail "--rollback loaded the archive"
  deploy -- --rollback
  (( DEPLOY_STATUS == 0 )) || fail "the rollback to the loaded image failed (exit ${DEPLOY_STATUS})"
  [[ "$(container_id "${controller}")" == "${offline_id}" && "$(container_image "${controller}")" == "${loaded_id}" ]] \
    || fail "the second rollback did not return to the loaded version"
  relays_stayed_off
  assert_relays_off
  pass "deployed ${offline_digest:0:19}... from its offline archive in ${offline_seconds} s with the registry gone, run by its ID ${loaded_id:0:19}... with the release-image label; an archive of another image changed nothing; --rollback swapped to the pulled version and back; relays 0 throughout"
}

# ---------------------------------------------------------------------------------------------------------------------

scenarios=("$@")
(( ${#scenarios[@]} > 0 )) || scenarios=(plant_fault lifecycle supervisor c12 migration pull)
for scenario in "${scenarios[@]}"; do
  case "${scenario}" in
    plant_fault|lifecycle|supervisor|c12|migration|pull) ;;
    *) echo "Unknown scenario: ${scenario} (plant_fault, lifecycle, supervisor, c12, migration or pull)" >&2; exit 2 ;;
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
