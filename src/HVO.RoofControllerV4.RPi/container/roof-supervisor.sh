#!/usr/bin/env bash
# The roof controller container's supervisor: the controller and the web UI, two processes in one container
# (docs/deployment.md, "The container's two processes").
#
# tini is PID 1. It reaps orphans and passes SIGTERM and SIGINT to this script, which:
# - on docker stop (SIGTERM): stops the controller first and waits for it, because its shutdown stops the roof and
#   verifies the relays off. It waits up to HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS, then stops the web UI, waiting up to
#   HVO_SUPERVISOR_UI_STOP_SECONDS, and exits. The two waits together stay below the container's stop timeout, so
#   Docker never kills the controller first. Once a wait runs out, that process is killed (SIGKILL).
# - when the controller exits with 75 (a restart requested through POST System/Restart): starts it again at once.
# - when the controller exits otherwise (a crash): starts it again after a backoff of 1, 2, 4, 8 and 16 s, at most
#   HVO_SUPERVISOR_BACKOFF_MAX_SECONDS. After HVO_SUPERVISOR_CRASH_LIMIT crashes within
#   HVO_SUPERVISOR_CRASH_WINDOW_SECONDS it leaves the controller stopped instead of looping while the roof may need
#   attention. The container then reports unhealthy (the health check is the controller's readiness), and the web UI
#   says why.
# - when the web UI exits: starts only the web UI again, with the same backoff, and never gives up. The controller is
#   not touched.
# - on a forced restart: the web UI creates <run dir>/control/force-restart-controller when the controller does not
#   answer. The controller is killed (SIGKILL, as docker kill does, with the same guarantees: commissioning.md C11)
#   and started again at once. This also starts a controller left stopped after a crash loop. A request that arrives
#   within HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS of the controller's start is ignored, so a repeated request cannot
#   kill a controller that is still starting.
# - with arguments (the deployment check, --validate-deployment): runs only the controller with them, in its place.
#
# The controller runs with the container's environment. The web UI runs as HVO_SUPERVISOR_UI_USER (the image's
# unprivileged app user), with only the RoofWeb__* settings and a few general variables (PATH, TZ, the locale,
# DOTNET_* and ASPNETCORE_ENVIRONMENT), so it never sees the controller's keys or reads its secrets. For HTTPS the
# supervisor gives it a private copy of the certificate and its password (see prepare_ui_certificate).
#
# <run dir>/supervisor.json says what the supervisor is doing, for the web UI and the health check. It is rewritten
# (atomically) at each change.

set -uo pipefail

readonly RESTART_EXIT_CODE=75

APP_DIR=${HVO_SUPERVISOR_APP_DIR:-/app}
RUN_DIR=${HVO_SUPERVISOR_RUN_DIR:-/run/hvo-roof}
SECRETS_DIR=${HVO_SUPERVISOR_SECRETS_DIR:-/run/secrets}
UI_USER=${HVO_SUPERVISOR_UI_USER-app}
CONTROLLER_STOP_SECONDS=${HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS:-25}
UI_STOP_SECONDS=${HVO_SUPERVISOR_UI_STOP_SECONDS:-2}
CRASH_LIMIT=${HVO_SUPERVISOR_CRASH_LIMIT:-5}
CRASH_WINDOW_SECONDS=${HVO_SUPERVISOR_CRASH_WINDOW_SECONDS:-120}
BACKOFF_MAX_SECONDS=${HVO_SUPERVISOR_BACKOFF_MAX_SECONDS:-30}
FORCE_RESTART_MIN_SECONDS=${HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS:-10}
TICK_SECONDS=${HVO_SUPERVISOR_TICK_SECONDS:-1}

if [[ -n "${HVO_SUPERVISOR_CONTROLLER_EXEC:-}" ]]; then
  CONTROLLER_EXEC=("${HVO_SUPERVISOR_CONTROLLER_EXEC}")
else
  CONTROLLER_EXEC=(dotnet "${APP_DIR}/HVO.RoofControllerV4.RPi.dll")
fi
if [[ -n "${HVO_SUPERVISOR_UI_EXEC:-}" ]]; then
  UI_EXEC=("${HVO_SUPERVISOR_UI_EXEC}")
else
  UI_EXEC=(dotnet "${APP_DIR}/web/HVO.RoofControllerV4.Web.dll")
fi

STATE_FILE="${RUN_DIR}/supervisor.json"
CONTROL_DIR="${RUN_DIR}/control"
FORCE_RESTART_REQUEST="${CONTROL_DIR}/force-restart-controller"
UI_PRIVATE_DIR="${RUN_DIR}/web"

log() {
  printf '[supervisor] %s\n' "$*" >&2
}

now_iso() {
  date -u +%Y-%m-%dT%H:%M:%SZ
}

require_whole_number() {
  local name=$1 value=$2 min=$3
  if [[ ! "${value}" =~ ^[0-9]+$ ]] || (( value < min )); then
    log "${name} must be a whole number of at least ${min}, got '${value}'."
    exit 2
  fi
}

# With arguments this is the deployment check (or another one-shot run of the controller): nothing is supervised.
if (( $# > 0 )); then
  cd "${APP_DIR}" || exit 1
  exec "${CONTROLLER_EXEC[@]}" "$@"
fi

require_whole_number HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS "${CONTROLLER_STOP_SECONDS}" 1
require_whole_number HVO_SUPERVISOR_UI_STOP_SECONDS "${UI_STOP_SECONDS}" 1
require_whole_number HVO_SUPERVISOR_CRASH_LIMIT "${CRASH_LIMIT}" 1
require_whole_number HVO_SUPERVISOR_CRASH_WINDOW_SECONDS "${CRASH_WINDOW_SECONDS}" 1
require_whole_number HVO_SUPERVISOR_BACKOFF_MAX_SECONDS "${BACKOFF_MAX_SECONDS}" 1
require_whole_number HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS "${FORCE_RESTART_MIN_SECONDS}" 0
if [[ ! "${TICK_SECONDS}" =~ ^([0-9]+|[0-9]*\.[0-9]+)$ ]]; then
  log "HVO_SUPERVISOR_TICK_SECONDS must be a number of seconds such as 1 or 0.2, got '${TICK_SECONDS}'."
  exit 2
fi

# The state of each process: running, restarting (waiting to start again), crash-loop (left stopped: controller only),
# stopping or stopped.
declare -A STATE=([controller]=starting [ui]=starting)
declare -A PID=([controller]="" [ui]="")
declare -A STARTED_AT=([controller]=0 [ui]=0)
declare -A START_DUE=([controller]=0 [ui]=0)
declare -A STARTS=([controller]=0 [ui]=0)
declare -A LAST_EXIT_CODE=([controller]="" [ui]="")
declare -A LAST_EXIT_REASON=([controller]="" [ui]="")
declare -A LAST_EXIT_AT=([controller]="" [ui]="")
# Crash times (epoch seconds, space-separated) within the crash window.
declare -A CRASHES=([controller]="" [ui]="")
SUPERVISOR_STATE=running
STOP_REQUESTED=0
SLEEP_PID=""

json_string_or_null() {
  if [[ -z "$1" ]]; then printf 'null'; else printf '"%s"' "$1"; fi
}

json_number_or_null() {
  if [[ -z "$1" ]]; then printf 'null'; else printf '%s' "$1"; fi
}

process_json() {
  local name=$1 crash_count
  crash_count=$(wc -w <<<"${CRASHES[${name}]}")
  printf '{"state":"%s","pid":%s,"starts":%s,"recentCrashes":%s,"lastExitCode":%s,"lastExitReason":%s,"lastExitAt":%s}' \
    "${STATE[${name}]}" "$(json_number_or_null "${PID[${name}]}")" "${STARTS[${name}]}" "${crash_count}" \
    "$(json_number_or_null "${LAST_EXIT_CODE[${name}]}")" "$(json_string_or_null "${LAST_EXIT_REASON[${name}]}")" \
    "$(json_string_or_null "${LAST_EXIT_AT[${name}]}")"
}

write_state() {
  local temp="${STATE_FILE}.tmp"
  {
    printf '{"supervisor":"%s","updatedAt":"%s","crashLimit":%s,"crashWindowSeconds":%s,' \
      "${SUPERVISOR_STATE}" "$(now_iso)" "${CRASH_LIMIT}" "${CRASH_WINDOW_SECONDS}"
    printf '"controller":%s,"ui":%s}\n' "$(process_json controller)" "$(process_json ui)"
  } >"${temp}" || { log "WARNING: could not write ${STATE_FILE}"; return; }
  chmod 0644 "${temp}"
  mv -f "${temp}" "${STATE_FILE}" || log "WARNING: could not write ${STATE_FILE}"
}

# Creates the run directory: supervisor.json readable by all, the control directory writable only by the web UI's
# user (and root), and the web UI's private directory for its certificate copy.
prepare_run_dir() {
  mkdir -p "${RUN_DIR}" || { log "cannot create ${RUN_DIR}"; exit 1; }
  chmod 0755 "${RUN_DIR}"
  rm -rf "${CONTROL_DIR}" "${UI_PRIVATE_DIR}"
  if [[ -n "${UI_USER}" ]]; then
    install -d -m 0700 -o "${UI_USER}" -g "${UI_USER}" "${CONTROL_DIR}" "${UI_PRIVATE_DIR}" \
      || { log "cannot create the web UI's directories for user ${UI_USER}"; exit 1; }
  else
    install -d -m 0700 "${CONTROL_DIR}" "${UI_PRIVATE_DIR}" || { log "cannot create ${CONTROL_DIR}"; exit 1; }
  fi
}

is_running() {
  local pid=${PID[$1]}
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

start_controller() {
  (cd "${APP_DIR}" && exec "${CONTROLLER_EXEC[@]}") &
  PID[controller]=$!
  STARTED_AT[controller]=${EPOCHSECONDS}
  STARTS[controller]=$((STARTS[controller] + 1))
  STATE[controller]=running
  log "Started the controller (pid ${PID[controller]}, start ${STARTS[controller]})"
  write_state
}

# The web UI's certificate, for HTTPS: RoofWeb__Certificate__Path if given, else the controller's
# (Kestrel__Certificates__Default__Path), and the controller's certificate password from the secrets directory or the
# environment. The web UI's user cannot read the secrets directory or, usually, the certificate mount, so it gets its
# own copies (owner-only) in its private directory, taken again at each start so a renewed certificate is used.
# Prints the RoofWeb__Certificate__* settings for the web UI, one per line.
prepare_ui_certificate() {
  local source=${RoofWeb__Certificate__Path:-${Kestrel__Certificates__Default__Path:-}}
  local password_file="${SECRETS_DIR}/Kestrel__Certificates__Default__Password"
  [[ "${RoofWeb__Urls:-}" == *https://* && -n "${source}" ]] || return 0
  if [[ ! -r "${source}" ]]; then
    log "WARNING: the web UI's certificate ${source} cannot be read; the web UI cannot serve HTTPS"
    return 0
  fi
  local copy="${UI_PRIVATE_DIR}/certificate.pfx" password_copy="${UI_PRIVATE_DIR}/certificate-password"
  local -a owner=()
  [[ -z "${UI_USER}" ]] || owner=(-o "${UI_USER}" -g "${UI_USER}")
  rm -f "${copy}" "${password_copy}"
  install -m 0400 "${owner[@]}" "${source}" "${copy}" || return 0
  printf 'RoofWeb__Certificate__Path=%s\n' "${copy}"
  if [[ -n "${RoofWeb__Certificate__PasswordFile:-}" && -r "${RoofWeb__Certificate__PasswordFile}" ]]; then
    password_file=${RoofWeb__Certificate__PasswordFile}
  fi
  if [[ -r "${password_file}" ]]; then
    install -m 0400 "${owner[@]}" "${password_file}" "${password_copy}" || return 0
  elif [[ -n "${Kestrel__Certificates__Default__Password:-}" ]]; then
    (umask 0277 && printf '%s' "${Kestrel__Certificates__Default__Password}" >"${password_copy}") || return 0
    [[ -z "${UI_USER}" ]] || chown "${UI_USER}:${UI_USER}" "${password_copy}"
  else
    return 0
  fi
  printf 'RoofWeb__Certificate__PasswordFile=%s\n' "${password_copy}"
}

start_ui() {
  local -a ui_env=("PATH=${PATH}" "TZ=${TZ:-UTC}")
  local entry name home
  # Only the web UI's settings and a few general variables; never the controller's keys or settings.
  while IFS= read -r -d '' entry; do
    name=${entry%%=*}
    case "${name}" in
      RoofWeb__Certificate__Path|RoofWeb__Certificate__PasswordFile) ;;
      RoofWeb__*|DOTNET_*|ASPNETCORE_ENVIRONMENT|LANG|LC_*) ui_env+=("${entry}") ;;
    esac
  done < <(env -0)
  while IFS= read -r entry; do
    [[ -n "${entry}" ]] && ui_env+=("${entry}")
  done < <(prepare_ui_certificate)
  ui_env+=("RoofWeb__SupervisorStatePath=${STATE_FILE}" "RoofWeb__SupervisorControlPath=${CONTROL_DIR}")

  if [[ -n "${UI_USER}" ]]; then
    home=$(getent passwd "${UI_USER}" | cut -d: -f6)
    ui_env+=("HOME=${home:-/tmp}" "USER=${UI_USER}")
    (cd "${APP_DIR}/web" && exec setpriv --reuid="${UI_USER}" --regid="${UI_USER}" --init-groups --no-new-privs \
      env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
  else
    ui_env+=("HOME=${HOME:-/tmp}")
    (cd "${APP_DIR}/web" 2>/dev/null || cd "${APP_DIR}" || exit 1; exec env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
  fi
  PID[ui]=$!
  STARTED_AT[ui]=${EPOCHSECONDS}
  STARTS[ui]=$((STARTS[ui] + 1))
  STATE[ui]=running
  log "Started the web UI (pid ${PID[ui]}, start ${STARTS[ui]})"
  write_state
}

start() {
  if [[ "$1" == controller ]]; then start_controller; else start_ui; fi
}

# Records a crash of $1 and prints how many there were within the crash window, this one included.
record_crash() {
  local name=$1 time kept=""
  for time in ${CRASHES[${name}]} "${EPOCHSECONDS}"; do
    (( EPOCHSECONDS - time < CRASH_WINDOW_SECONDS )) && kept+="${time} "
  done
  CRASHES[${name}]=${kept% }
}

# 1, 2, 4, 8, ... seconds for the first, second, third ... crash within the window, at most the backoff maximum.
backoff_seconds() {
  local count delay=1
  count=$(wc -w <<<"${CRASHES[$1]}")
  while (( count > 1 && delay < BACKOFF_MAX_SECONDS )); do
    delay=$((delay * 2))
    count=$((count - 1))
  done
  (( delay > BACKOFF_MAX_SECONDS )) && delay=${BACKOFF_MAX_SECONDS}
  printf '%s' "${delay}"
}

describe_exit() {
  local code=$1
  if (( code > 128 )); then
    printf 'killed by signal %s' "$((code - 128))"
  else
    printf 'exit code %s' "${code}"
  fi
}

# $1 has exited: reap it, record why, and decide when (or whether) it starts again.
handle_exit() {
  local name=$1 code=0 label delay count
  wait "${PID[${name}]}" 2>/dev/null
  code=$?
  PID[${name}]=""
  LAST_EXIT_CODE[${name}]=${code}
  LAST_EXIT_AT[${name}]=$(now_iso)
  label="the controller"
  [[ "${name}" == ui ]] && label="the web UI"

  if [[ "${name}" == controller && ${code} -eq ${RESTART_EXIT_CODE} ]]; then
    LAST_EXIT_REASON[${name}]="restart requested"
    log "The controller asked to be restarted (exit code ${RESTART_EXIT_CODE}); starting it again"
    START_DUE[${name}]=${EPOCHSECONDS}
    STATE[${name}]=restarting
    write_state
    return
  fi

  LAST_EXIT_REASON[${name}]="crashed ($(describe_exit "${code}"))"
  record_crash "${name}"
  count=$(wc -w <<<"${CRASHES[${name}]}")
  if [[ "${name}" == controller ]] && (( count >= CRASH_LIMIT )); then
    STATE[${name}]=crash-loop
    log "The controller stopped ($(describe_exit "${code}")): ${count} crashes within ${CRASH_WINDOW_SECONDS}s." \
      "It is left stopped and the container reports unhealthy. Find the cause in the log above; a forced restart" \
      "from the web UI, or a restart of the container, starts it again."
    write_state
    return
  fi
  delay=$(backoff_seconds "${name}")
  START_DUE[${name}]=$((EPOCHSECONDS + delay))
  STATE[${name}]=restarting
  log "${label^} stopped ($(describe_exit "${code}")); starting it again in ${delay}s (crash ${count} within ${CRASH_WINDOW_SECONDS}s)"
  write_state
}

handle_force_restart_request() {
  [[ -e "${FORCE_RESTART_REQUEST}" || -L "${FORCE_RESTART_REQUEST}" ]] || return 0
  rm -f "${FORCE_RESTART_REQUEST}"
  if [[ "${STATE[controller]}" == running ]] && (( EPOCHSECONDS - STARTED_AT[controller] < FORCE_RESTART_MIN_SECONDS )); then
    log "Ignored a forced restart request: the controller started $((EPOCHSECONDS - STARTED_AT[controller]))s ago"
    return 0
  fi
  log "Forced restart requested through the web UI"
  if is_running controller; then
    log "Killing the controller (SIGKILL, pid ${PID[controller]})"
    kill -KILL "${PID[controller]}" 2>/dev/null
    wait "${PID[controller]}" 2>/dev/null
    LAST_EXIT_CODE[controller]=137
    LAST_EXIT_AT[controller]=$(now_iso)
    PID[controller]=""
  fi
  LAST_EXIT_REASON[controller]="forced restart"
  # A deliberate restart: earlier crashes no longer count towards the crash loop.
  CRASHES[controller]=""
  start_controller
}

tick() {
  local name
  handle_force_restart_request
  for name in controller ui; do
    case "${STATE[${name}]}" in
      running)
        is_running "${name}" || handle_exit "${name}" ;;
      restarting)
        (( EPOCHSECONDS >= START_DUE[${name}] )) && start "${name}" ;;
    esac
  done
}

# Sleeps for a tick; a signal ends the sleep at once (the trap runs, then wait returns).
sleep_tick() {
  sleep "${TICK_SECONDS}" &
  SLEEP_PID=$!
  wait "${SLEEP_PID}" 2>/dev/null
  kill "${SLEEP_PID}" 2>/dev/null
  wait "${SLEEP_PID}" 2>/dev/null
  SLEEP_PID=""
}

# Waits up to $2 seconds for $1 to exit. Returns 1 if it is still running.
wait_for_exit() {
  local name=$1 deadline=$((EPOCHSECONDS + $2))
  while is_running "${name}"; do
    (( EPOCHSECONDS < deadline )) || return 1
    sleep 0.1
  done
  return 0
}

stop_process() {
  local name=$1 seconds=$2 label=$3 code
  if ! is_running "${name}"; then
    STATE[${name}]=stopped
    return
  fi
  STATE[${name}]=stopping
  write_state
  log "Stopping ${label} (SIGTERM, up to ${seconds}s)"
  kill -TERM "${PID[${name}]}" 2>/dev/null
  if ! wait_for_exit "${name}" "${seconds}"; then
    log "${label^} did not stop within ${seconds}s; killing it (SIGKILL)"
    kill -KILL "${PID[${name}]}" 2>/dev/null
  fi
  wait "${PID[${name}]}" 2>/dev/null
  code=$?
  log "${label^} stopped ($(describe_exit "${code}"))"
  PID[${name}]=""
  LAST_EXIT_CODE[${name}]=${code}
  LAST_EXIT_REASON[${name}]="stopped with the container"
  LAST_EXIT_AT[${name}]=$(now_iso)
  STATE[${name}]=stopped
}

# docker stop: the controller first (its shutdown stops the roof), then the web UI.
shutdown() {
  SUPERVISOR_STATE=stopping
  log "Stopping: the controller first, then the web UI"
  stop_process controller "${CONTROLLER_STOP_SECONDS}" "the controller"
  stop_process ui "${UI_STOP_SECONDS}" "the web UI"
  SUPERVISOR_STATE=stopped
  write_state
  log "Stopped"
  exit 0
}

# shellcheck disable=SC2317 # called by the trap
on_signal() {
  STOP_REQUESTED=1
  [[ -z "${SLEEP_PID}" ]] || kill "${SLEEP_PID}" 2>/dev/null
}

trap on_signal TERM INT
prepare_run_dir
log "Starting the controller and the web UI (crash limit ${CRASH_LIMIT} within ${CRASH_WINDOW_SECONDS}s;" \
  "stop waits ${CONTROLLER_STOP_SECONDS}s for the controller, then ${UI_STOP_SECONDS}s for the web UI)"
start_controller
start_ui
while (( ! STOP_REQUESTED )); do
  tick
  (( STOP_REQUESTED )) || sleep_tick
done
shutdown
