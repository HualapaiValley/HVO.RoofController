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
# - when the controller exits otherwise (a crash): starts it again after a backoff that doubles from 1 s (1, 2, 4, 8 s,
#   ...), at most HVO_SUPERVISOR_BACKOFF_MAX_SECONDS. The HVO_SUPERVISOR_CRASH_LIMIT-th crash within
#   HVO_SUPERVISOR_CRASH_WINDOW_SECONDS leaves the controller stopped instead of looping while the roof may need
#   attention (with the defaults, the fifth: so its delays are 1, 2, 4 and 8 s). The container's health check (the
#   controller's readiness) then fails, and the web UI says why.
# - when the web UI exits: starts only the web UI again, with the same backoff, and never gives up. The controller is
#   not touched.
# - on a forced restart: <run dir>/control/force-restart-controller appears (docker exec ... touch, or the web UI's
#   ControllerForcedRestart) when the controller does not answer. The controller is killed (SIGKILL, as docker kill
#   does, with the same guarantees: commissioning.md C11) and started again at once. This also starts a controller
#   left stopped after a crash loop. A request that arrives within HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS of the
#   controller's start is ignored, so a repeated request cannot kill a controller that is still starting.
#   supervisor.json records what was done with the last request.
# - with arguments (the deployment check, --validate-deployment): runs only the controller with them, in its place.
#
# The controller runs with the container's environment. The web UI runs as HVO_SUPERVISOR_UI_USER (the image's
# unprivileged app user), with only the RoofWeb__* settings and a few general variables (PATH, TZ, the locale,
# DOTNET_* and ASPNETCORE_ENVIRONMENT), so it never sees the controller's keys or reads its secrets. For HTTPS the
# supervisor gives it a private copy of the certificate it serves and that certificate's password (by default the
# controller's; see prepare_ui_certificate), and a private copy of its Stop key (RoofWeb__StopKeyFile). It keeps the keys
# that protect its sign-in cookie in HVO_SUPERVISOR_UI_DATA_DIR/keys, or in RoofWeb__DataProtectionPath when that is set,
# so the people signed in stay signed in when the web UI or the container restarts.
#
# Times are kept in microseconds (EPOCHREALTIME), so every wait is as long as configured, not up to a second shorter.
#
# <run dir>/supervisor.json says what the supervisor is doing, for the web UI and the health check. It is rewritten
# (atomically) at each change.

set -uo pipefail

readonly RESTART_EXIT_CODE=75

APP_DIR=${HVO_SUPERVISOR_APP_DIR:-/app}
RUN_DIR=${HVO_SUPERVISOR_RUN_DIR:-/run/hvo-roof}
SECRETS_DIR=${HVO_SUPERVISOR_SECRETS_DIR:-/run/secrets}
UI_USER=${HVO_SUPERVISOR_UI_USER-app}
UI_DATA_DIR=${HVO_SUPERVISOR_UI_DATA_DIR:-/var/lib/hvo-roof-web}
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

# The time in microseconds since the epoch (EPOCHREALTIME without its decimal separator, which follows the locale).
now_us() {
  printf '%s' "${EPOCHREALTIME//[!0-9]/}"
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

# The web UI's group: its user's primary group.
UI_GROUP=""
if [[ -n "${UI_USER}" ]]; then
  UI_GROUP=$(id -gn "${UI_USER}" 2>/dev/null) || { log "HVO_SUPERVISOR_UI_USER names no user: '${UI_USER}'."; exit 2; }
fi

# The state of each process: running, restarting (waiting to start again), crash-loop (left stopped: controller only),
# stopping or stopped.
declare -A STATE=([controller]=starting [ui]=starting)
declare -A PID=([controller]="" [ui]="")
# Microseconds since the epoch.
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
# The last forced restart request: when it was handled, and whether the controller was restarted or the request ignored.
FORCED_RESTART_AT=""
FORCED_RESTART_OUTCOME=""
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

forced_restart_json() {
  if [[ -z "${FORCED_RESTART_AT}" ]]; then
    printf 'null'
  else
    printf '{"at":"%s","outcome":"%s"}' "${FORCED_RESTART_AT}" "${FORCED_RESTART_OUTCOME}"
  fi
}

write_state() {
  local temp="${STATE_FILE}.tmp"
  {
    printf '{"supervisor":"%s","updatedAt":"%s","crashLimit":%s,"crashWindowSeconds":%s,"forceRestartMinSeconds":%s,' \
      "${SUPERVISOR_STATE}" "$(now_iso)" "${CRASH_LIMIT}" "${CRASH_WINDOW_SECONDS}" "${FORCE_RESTART_MIN_SECONDS}"
    printf '"lastForcedRestart":%s,"controller":%s,"ui":%s}\n' "$(forced_restart_json)" "$(process_json controller)" \
      "$(process_json ui)"
  } >"${temp}" || { log "WARNING: could not write ${STATE_FILE}"; return; }
  chmod 0644 "${temp}"
  mv -f "${temp}" "${STATE_FILE}" || log "WARNING: could not write ${STATE_FILE}"
}

# Creates the run directory: supervisor.json readable by all, the control directory writable only by the web UI's
# user (and root), and the web UI's private directory for its certificate copy. The private directory belongs to the
# supervisor's user (root), with the web UI's group allowed to read it: the web UI can read its copies but cannot
# replace them, or plant a link that the supervisor would then write through.
prepare_run_dir() {
  mkdir -p "${RUN_DIR}" || { log "cannot create ${RUN_DIR}"; exit 1; }
  chmod 0755 "${RUN_DIR}"
  rm -rf "${CONTROL_DIR}" "${UI_PRIVATE_DIR}"
  if [[ -n "${UI_USER}" ]]; then
    if ! install -d -m 0700 -o "${UI_USER}" -g "${UI_GROUP}" "${CONTROL_DIR}" \
      || ! install -d -m 0750 -g "${UI_GROUP}" "${UI_PRIVATE_DIR}"; then
      log "cannot create the web UI's directories for user ${UI_USER}"
      exit 1
    fi
  else
    install -d -m 0700 "${CONTROL_DIR}" "${UI_PRIVATE_DIR}" || { log "cannot create ${CONTROL_DIR}"; exit 1; }
  fi
}

# The controller's secrets are for the controller alone. Warns when the web UI's user could read one of them (for
# example after a chown of the secrets directory to the image's app user). The files are listed here, as root, and
# each is tried by name as the web UI's user, who may be able to open a file in a directory it cannot list.
check_secrets_are_private() {
  local readable file
  local -a files=()
  [[ -n "${UI_USER}" && -d "${SECRETS_DIR}" ]] || return 0
  for file in "${SECRETS_DIR}"/*; do
    [[ -f "${file}" ]] && files+=("${file}")
  done
  (( ${#files[@]} > 0 )) || return 0
  # shellcheck disable=SC2016 # expanded by the inner bash
  readable=$(setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs \
    bash -c 'for f; do [[ -r "$f" ]] && { printf "%s" "$f"; exit 0; }; done' _ "${files[@]}" \
    2>/dev/null)
  if [[ -n "${readable}" ]]; then
    log "WARNING: the web UI's user ${UI_USER} can read ${readable}. The secrets are for the controller alone: keep" \
      "${SECRETS_DIR} (on the Pi, /etc/hvo-roof/secrets) root:root, mode 0700, and its files 0600 (docs/security.md)."
  fi
}

is_running() {
  local pid=${PID[$1]}
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

start_controller() {
  (cd "${APP_DIR}" && exec "${CONTROLLER_EXEC[@]}") &
  PID[controller]=$!
  STARTED_AT[controller]=$(now_us)
  STARTS[controller]=$((STARTS[controller] + 1))
  STATE[controller]=running
  log "Started the controller (pid ${PID[controller]}, start ${STARTS[controller]})"
  write_state
}

# The web UI's certificate, for HTTPS: either its own (RoofWeb__Certificate__Path, with the password in
# RoofWeb__Certificate__PasswordFile or none), or else the controller's (Kestrel__Certificates__Default__Path, with the
# controller's certificate password from the secrets directory or the environment). The controller's password is never
# given with a certificate of the web UI's own. The web UI's user cannot read the secrets directory or, usually, the
# certificate mount, so it gets its own copies (owner-only) in its private directory, taken again at each start so a
# renewed certificate is used. Every copy is written with install, which replaces whatever is at the path and never
# writes through a link. Prints the RoofWeb__Certificate__* settings for the web UI, one per line.
prepare_ui_certificate() {
  local source password_file="" password=""
  if [[ -n "${RoofWeb__Certificate__Path:-}" ]]; then
    source=${RoofWeb__Certificate__Path}
    password_file=${RoofWeb__Certificate__PasswordFile:-}
  else
    source=${Kestrel__Certificates__Default__Path:-}
    password_file="${SECRETS_DIR}/Kestrel__Certificates__Default__Password"
    [[ -r "${password_file}" ]] || password_file=""
    password=${Kestrel__Certificates__Default__Password:-}
  fi
  [[ "${RoofWeb__Urls:-}" == *https://* && -n "${source}" ]] || return 0
  if [[ ! -r "${source}" ]]; then
    log "WARNING: the web UI's certificate ${source} cannot be read; the web UI cannot serve HTTPS"
    return 0
  fi
  local copy="${UI_PRIVATE_DIR}/certificate.pfx" password_copy="${UI_PRIVATE_DIR}/certificate-password"
  local -a owner=()
  [[ -z "${UI_USER}" ]] || owner=(-o "${UI_USER}" -g "${UI_GROUP}")
  rm -f "${copy}" "${password_copy}"
  install -m 0400 "${owner[@]}" "${source}" "${copy}" || return 0
  printf 'RoofWeb__Certificate__Path=%s\n' "${copy}"
  if [[ -n "${password_file}" && ! -r "${password_file}" ]]; then
    log "WARNING: the web UI's certificate password file ${password_file} cannot be read; the web UI gets no password" \
      "for its certificate"
    return 0
  fi
  if [[ -n "${password_file}" ]]; then
    install -m 0400 "${owner[@]}" "${password_file}" "${password_copy}" || return 0
  elif [[ -n "${password}" ]]; then
    printf '%s' "${password}" | install -m 0400 "${owner[@]}" /dev/stdin "${password_copy}" || return 0
  else
    return 0
  fi
  printf 'RoofWeb__Certificate__PasswordFile=%s\n' "${password_copy}"
}

# The web UI's own API key for Stop (RoofWeb__StopKeyFile): a file only root may read, such as a Viewer key's file in
# the secrets directory. The web UI gets its own copy (owner-only) in its private directory, taken again at each start
# so a rotated key is used. Prints the RoofWeb__StopKeyFile setting for the web UI; nothing without a key.
prepare_ui_stop_key() {
  local source=${RoofWeb__StopKeyFile:-} copy="${UI_PRIVATE_DIR}/stop-key"
  local -a owner=()
  [[ -n "${source}" ]] || return 0
  rm -f "${copy}"
  if [[ ! -r "${source}" ]]; then
    log "WARNING: the web UI's Stop key ${source} cannot be read; the web UI's Stop uses the person's session alone"
    return 0
  fi
  [[ -z "${UI_USER}" ]] || owner=(-o "${UI_USER}" -g "${UI_GROUP}")
  install -m 0400 "${owner[@]}" "${source}" "${copy}" || return 0
  printf 'RoofWeb__StopKeyFile=%s\n' "${copy}"
}

# The directory for the keys that protect the web UI's sign-in cookie and forms (RoofWeb__DataProtectionPath, or
# HVO_SUPERVISOR_UI_DATA_DIR/keys), owned by the web UI's user and private to it. The default is in the container, so
# it lasts while the container does: a redeploy, which makes a new container, signs everyone out. Root makes the
# default, /var/lib/hvo-roof-web/keys, in a directory only root can change. A directory of the operator's choosing
# (RoofWeb__DataProtectionPath or HVO_SUPERVISOR_UI_DATA_DIR) may be anywhere, under a directory the web UI's user can
# write too, where that user could swap a part of the path for a link to, say, the secrets directory; so it is made by
# the web UI's user, with that user's rights alone, and a link gains nothing. Prints the RoofWeb__DataProtectionPath
# setting for the web UI; nothing when the directory cannot be made (the keys are then kept in memory).
prepare_ui_data_protection() {
  local path=${RoofWeb__DataProtectionPath:-}
  local -a make=(install -d -m 0700)
  if [[ -z "${path}" && -z "${HVO_SUPERVISOR_UI_DATA_DIR:-}" ]]; then
    path="${UI_DATA_DIR}/keys"
    install -d -m 0755 "${UI_DATA_DIR}" || { log "WARNING: cannot create ${UI_DATA_DIR}; the web UI keeps its keys in memory"; return 0; }
    [[ -z "${UI_USER}" ]] || make+=(-o "${UI_USER}" -g "${UI_GROUP}")
  else
    path=${path:-${UI_DATA_DIR}/keys}
    [[ -z "${UI_USER}" ]] || make=(setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs "${make[@]}")
  fi
  if [[ -L "${path}" ]] || ! "${make[@]}" "${path}"; then
    log "WARNING: cannot use ${path} for the web UI's keys; the web UI keeps them in memory, so everyone signs in" \
      "again when it restarts"
    return 0
  fi
  printf 'RoofWeb__DataProtectionPath=%s\n' "${path}"
}

start_ui() {
  local -a ui_env=("PATH=${PATH}" "TZ=${TZ:-UTC}")
  local entry name home
  # Only the web UI's settings and a few general variables; never the controller's keys or settings.
  while IFS= read -r -d '' entry; do
    name=${entry%%=*}
    case "${name}" in
      RoofWeb__Certificate__Path|RoofWeb__Certificate__PasswordFile|RoofWeb__StopKeyFile|RoofWeb__DataProtectionPath) ;;
      RoofWeb__*|DOTNET_*|ASPNETCORE_ENVIRONMENT|LANG|LC_*) ui_env+=("${entry}") ;;
    esac
  done < <(env -0)
  while IFS= read -r entry; do
    [[ -n "${entry}" ]] && ui_env+=("${entry}")
  done < <(prepare_ui_certificate; prepare_ui_stop_key; prepare_ui_data_protection)
  ui_env+=("RoofWeb__SupervisorStatePath=${STATE_FILE}" "RoofWeb__SupervisorControlPath=${CONTROL_DIR}")

  if [[ -n "${UI_USER}" ]]; then
    home=$(getent passwd "${UI_USER}" | cut -d: -f6)
    ui_env+=("HOME=${home:-/tmp}" "USER=${UI_USER}")
    (cd "${APP_DIR}/web" && exec setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs \
      env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
  else
    ui_env+=("HOME=${HOME:-/tmp}")
    (cd "${APP_DIR}/web" 2>/dev/null || cd "${APP_DIR}" || exit 1; exec env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
  fi
  PID[ui]=$!
  STARTED_AT[ui]=$(now_us)
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
    START_DUE[${name}]=$(now_us)
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
      "It is left stopped, and the container's health check fails (Docker marks it unhealthy after three failed" \
      "checks). Find the cause in the log above; a forced restart (${FORCE_RESTART_REQUEST}), or a restart of the" \
      "container, starts it again."
    write_state
    return
  fi
  delay=$(backoff_seconds "${name}")
  START_DUE[${name}]=$(( $(now_us) + delay * 1000000 ))
  STATE[${name}]=restarting
  log "${label^} stopped ($(describe_exit "${code}")); starting it again in ${delay}s (crash ${count} within ${CRASH_WINDOW_SECONDS}s)"
  write_state
}

handle_force_restart_request() {
  local running_us
  [[ -e "${FORCE_RESTART_REQUEST}" || -L "${FORCE_RESTART_REQUEST}" ]] || return 0
  rm -f "${FORCE_RESTART_REQUEST}"
  FORCED_RESTART_AT=$(now_iso)
  running_us=$(( $(now_us) - STARTED_AT[controller] ))
  if [[ "${STATE[controller]}" == running ]] && (( running_us < FORCE_RESTART_MIN_SECONDS * 1000000 )); then
    FORCED_RESTART_OUTCOME=ignored
    log "Ignored a forced restart request: the controller started $((running_us / 1000000))s ago, less than" \
      "${FORCE_RESTART_MIN_SECONDS}s"
    write_state
    return 0
  fi
  FORCED_RESTART_OUTCOME=restarted
  log "Forced restart requested"
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
        (( $(now_us) >= START_DUE[${name}] )) && start "${name}" ;;
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
  local name=$1 deadline
  deadline=$(( $(now_us) + $2 * 1000000 ))
  while is_running "${name}"; do
    (( $(now_us) < deadline )) || return 1
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
check_secrets_are_private
log "Starting the controller and the web UI (crash limit ${CRASH_LIMIT} within ${CRASH_WINDOW_SECONDS}s;" \
  "backoff at most ${BACKOFF_MAX_SECONDS}s; forced restarts ignored within ${FORCE_RESTART_MIN_SECONDS}s of a start;" \
  "stop waits ${CONTROLLER_STOP_SECONDS}s for the controller, then ${UI_STOP_SECONDS}s for the web UI)"
start_controller
start_ui
while (( ! STOP_REQUESTED )); do
  tick
  (( STOP_REQUESTED )) || sleep_tick
done
shutdown
