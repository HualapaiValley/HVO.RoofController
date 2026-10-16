#!/usr/bin/env bash
# The roof controller container's supervisor: the controller and the web UI, two processes in one container
# (docs/deployment.md, "The container's two processes").
#
# tini is PID 1. It reaps orphans and passes SIGTERM and SIGINT to this script, which:
# - whenever the controller is not running: turns every relay on the HAT off and verifies the register by reading it
#   back (de_energize_relays), because the HAT holds its relays as last written until it loses power, so a controller
#   that died during a move would leave the drive running. It does this as soon as the controller exits (a crash, a
#   kill, a failed start, a requested restart), before each start, again and again while the controller is not running
#   until it succeeds, and when it stops itself. It never turns a relay on.
# - on docker stop (SIGTERM, also SIGINT, SIGHUP and SIGQUIT): stops the controller first and waits for it, because its
#   shutdown stops the roof and verifies the relays off. It waits up to HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS, then turns
#   the relays off (the controller may have been killed, or have failed to verify them) while the web UI stops, waiting
#   up to HVO_SUPERVISOR_UI_STOP_SECONDS for it, and exits. The controller's wait and the longer of the other two stay
#   below the container's stop timeout, so Docker never kills the controller first. Once a wait runs out, that process is
#   killed (SIGKILL). Any other way this script ends (another signal, an error) stops the same way first (on_exit).
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
# - with arguments (the deployment check, --validate-deployment): runs only the controller with them, in its place. It
#   never turns the relays off then: another controller may be driving the HAT.
#
# The relays are turned off by the controller's own program, with --relays-off (HVO_SUPERVISOR_RELAYS_OFF_EXEC replaces
# it for the tests): the HAT access the controller uses, without the controller's settings checks, so it works when the
# controller cannot start. It must finish within HVO_SUPERVISOR_RELAYS_OFF_SECONDS or is killed.
#
# The kernel ends the process with the highest OOM score when the container (or the Pi) runs out of memory. The
# supervisor must outlive the controller to turn the relays off, so it raises the web UI's oom_score_adj by 500 and the
# controller's by 100 above its own (raising needs no capability): the kernel ends the web UI first, then the controller.
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
# The web UI's data directory when none is set, in directories only root can change (see prepare_ui_data_protection).
readonly DEFAULT_UI_DATA_DIR=/var/lib/hvo-roof-web
# How far the controller's and the web UI's oom_score_adj are raised above the supervisor's (see raise_oom_score_adj).
readonly CONTROLLER_OOM_SCORE_RAISE=100
readonly UI_OOM_SCORE_RAISE=500

APP_DIR=${HVO_SUPERVISOR_APP_DIR:-/app}
RUN_DIR=${HVO_SUPERVISOR_RUN_DIR:-/run/hvo-roof}
SECRETS_DIR=${HVO_SUPERVISOR_SECRETS_DIR:-/run/secrets}
UI_USER=${HVO_SUPERVISOR_UI_USER-app}
UI_DATA_DIR=${HVO_SUPERVISOR_UI_DATA_DIR:-${DEFAULT_UI_DATA_DIR}}
CONTROLLER_STOP_SECONDS=${HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS:-22}
UI_STOP_SECONDS=${HVO_SUPERVISOR_UI_STOP_SECONDS:-2}
RELAYS_OFF_SECONDS=${HVO_SUPERVISOR_RELAYS_OFF_SECONDS:-5}
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
if [[ -n "${HVO_SUPERVISOR_RELAYS_OFF_EXEC:-}" ]]; then
  RELAYS_OFF_EXEC=("${HVO_SUPERVISOR_RELAYS_OFF_EXEC}")
else
  RELAYS_OFF_EXEC=("${CONTROLLER_EXEC[@]}" --relays-off)
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
require_whole_number HVO_SUPERVISOR_RELAYS_OFF_SECONDS "${RELAYS_OFF_SECONDS}" 1
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
# The relays, as the supervisor knows them: unknown (not tried yet), controller (the controller runs and drives them),
# off (turned off and verified since the controller last ran) or unverified (the last try failed; retry_relays_off tries
# again). When and why they were last turned off, failed tries in a row, and when the next is due (microseconds).
RELAYS_STATE=unknown
RELAYS_AT=""
RELAYS_REASON=""
RELAYS_FAILURES=0
RELAYS_RETRY_DUE=0
# When each process's wait for its stop ends (microseconds), from begin_stop.
declare -A STOP_DEADLINE=([controller]=0 [ui]=0)
SHUTDOWN_DONE=0

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

relays_json() {
  printf '{"state":"%s","at":%s,"reason":%s}' "${RELAYS_STATE}" "$(json_string_or_null "${RELAYS_AT}")" \
    "$(json_string_or_null "${RELAYS_REASON}")"
}

write_state() {
  local temp="${STATE_FILE}.tmp"
  {
    printf '{"supervisor":"%s","updatedAt":"%s","crashLimit":%s,"crashWindowSeconds":%s,"forceRestartMinSeconds":%s,' \
      "${SUPERVISOR_STATE}" "$(now_iso)" "${CRASH_LIMIT}" "${CRASH_WINDOW_SECONDS}" "${FORCE_RESTART_MIN_SECONDS}"
    printf '"lastForcedRestart":%s,"relays":%s,"controller":%s,"ui":%s}\n' "$(forced_restart_json)" "$(relays_json)" \
      "$(process_json controller)" "$(process_json ui)"
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

# 1, 2, 4, 8, ... seconds after the first, second, third ... failed try in a row to turn the relays off, at most the
# backoff maximum.
relays_retry_seconds() {
  local count=${RELAYS_FAILURES} delay=1
  while (( count > 1 && delay < BACKOFF_MAX_SECONDS )); do
    delay=$((delay * 2))
    count=$((count - 1))
  done
  (( delay > BACKOFF_MAX_SECONDS )) && delay=${BACKOFF_MAX_SECONDS}
  printf '%s' "${delay}"
}

# Turns every relay on the HAT off and verifies the register by reading it back, with the controller's own program
# (--relays-off: the HAT access the controller uses), within HVO_SUPERVISOR_RELAYS_OFF_SECONDS. Only while the controller
# is not running: it would stop that controller's move. It never turns a relay on. $1 says why, for the log and the
# state. Returns 1 when the relays could not be verified off; retry_relays_off then tries again.
de_energize_relays() {
  local reason=$1 code=0 started elapsed_ms why delay
  started=$(now_us)
  (cd "${APP_DIR}" && exec timeout -s KILL "${RELAYS_OFF_SECONDS}" "${RELAYS_OFF_EXEC[@]}") || code=$?
  elapsed_ms=$(( ($(now_us) - started) / 1000 ))
  RELAYS_AT=$(now_iso)
  RELAYS_REASON=${reason}
  if (( code == 0 )); then
    RELAYS_STATE=off
    RELAYS_FAILURES=0
    log "The relays are off: every relay turned off and the HAT's register verified by read-back, ${elapsed_ms} ms" \
      "(${reason})"
    write_state
    return 0
  fi
  RELAYS_STATE=unverified
  RELAYS_FAILURES=$((RELAYS_FAILURES + 1))
  delay=$(relays_retry_seconds)
  RELAYS_RETRY_DUE=$(( $(now_us) + delay * 1000000 ))
  case ${code} in
    124|137) why="did not finish within ${RELAYS_OFF_SECONDS}s and was killed" ;;
    *) why="ended with $(describe_exit "${code}")" ;;
  esac
  log "WARNING: the relays could not be verified off (${reason}): turning them off ${why}, after ${elapsed_ms} ms." \
    "The supervisor tries again in ${delay}s while the controller is not running, and before it starts it. Until then" \
    "the drive may still be running: if the roof moves, use the independent stop (commissioning.md C1)."
  write_state
  return 1
}

# While the controller is not running (waiting to start again, or left stopped after a crash loop) and the relays are
# not verified off: tries again, after a delay that doubles from 1 s (at most the backoff maximum).
retry_relays_off() {
  [[ "${RELAYS_STATE}" == unverified ]] || return 0
  [[ "${STATE[controller]}" == restarting || "${STATE[controller]}" == crash-loop ]] || return 0
  (( $(now_us) >= RELAYS_RETRY_DUE )) || return 0
  de_energize_relays "trying again while the controller is not running"
}

# Raises this process's oom_score_adj by $1 (at most to 1000), in the subshell that then becomes the controller or the
# web UI (exec keeps it), so that the kernel ends that process before the supervisor when the container or the Pi runs
# out of memory: the supervisor must outlive the controller to turn the relays off. Raising needs no capability. A value
# that cannot be written is logged, and the process starts anyway.
raise_oom_score_adj() {
  local raise=$1 label=$2 current
  if read -r current 2>/dev/null </proc/self/oom_score_adj && [[ "${current}" =~ ^-?[0-9]+$ ]] \
    && printf '%s\n' "$(( current + raise > 1000 ? 1000 : current + raise ))" 2>/dev/null >/proc/self/oom_score_adj; then
    return 0
  fi
  log "WARNING: could not raise the oom_score_adj of ${label}; when memory runs out, the kernel may end the supervisor first"
  return 0
}

start_controller() {
  # Never with the relays as something else left them: at the supervisor's start (a container killed during a move),
  # or after a try that failed.
  [[ "${RELAYS_STATE}" == off ]] || de_energize_relays "before the controller starts"
  (cd "${APP_DIR}" && raise_oom_score_adj "${CONTROLLER_OOM_SCORE_RAISE}" "the controller" && exec "${CONTROLLER_EXEC[@]}") &
  PID[controller]=$!
  STARTED_AT[controller]=$(now_us)
  STARTS[controller]=$((STARTS[controller] + 1))
  STATE[controller]=running
  RELAYS_STATE=controller
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

# A path written plainly: repeated slashes made one and a trailing slash dropped, so the default is known with either.
# "." and ".." are left as they are, so a path with them is not the default: a symbolic link can make ".." name
# somewhere else.
plain_path() {
  local path=$1
  while [[ "${path}" == *//* ]]; do
    path=${path//\/\//\/}
  done
  [[ "${path}" == / ]] || path=${path%/}
  printf '%s\n' "${path}"
}

# The directory for the keys that protect the web UI's sign-in cookie and forms (RoofWeb__DataProtectionPath, or
# HVO_SUPERVISOR_UI_DATA_DIR/keys), owned by the web UI's user and private to it. The default is in the container, so
# it lasts while the container does: a redeploy, which makes a new container, signs everyone out. Root makes the
# default, /var/lib/hvo-roof-web/keys (also when a setting names it, with repeated or trailing slashes), and first
# gives its parent to root (a volume there may have been given to the web UI's user), so only root can change what
# the keys directory is; a parent root cannot change (a share that maps root to nobody) is logged, and the keys are
# kept in memory.
# Any other directory may be anywhere, under a directory the web UI's user can write too, where that user could swap a
# part of the path for a link to, say, the secrets directory; so it is made by the web UI's user, with that user's rights
# alone, and a link gains nothing. Prints the RoofWeb__DataProtectionPath setting for the web UI; nothing when the
# directory cannot be made (the keys are then kept in memory).
prepare_ui_data_protection() {
  local path
  path=$(plain_path "${RoofWeb__DataProtectionPath:-${UI_DATA_DIR}/keys}")
  local -a make=(install -d -m 0700) parent=(install -d -m 0755)
  if [[ "${path}" == "${DEFAULT_UI_DATA_DIR}/keys" ]]; then
    [[ -z "${UI_USER}" ]] || { parent+=(-o root -g root); make+=(-o "${UI_USER}" -g "${UI_GROUP}"); }
    if [[ -L "${DEFAULT_UI_DATA_DIR}" ]] || ! "${parent[@]}" "${DEFAULT_UI_DATA_DIR}"; then
      log "WARNING: cannot use ${DEFAULT_UI_DATA_DIR} for the web UI's keys; the web UI keeps them in memory"
      return 0
    fi
  elif [[ -n "${UI_USER}" ]]; then
    make=(setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs "${make[@]}")
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
    (cd "${APP_DIR}/web" && raise_oom_score_adj "${UI_OOM_SCORE_RAISE}" "the web UI" \
      && exec setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs \
      env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
  else
    ui_env+=("HOME=${HOME:-/tmp}")
    (cd "${APP_DIR}/web" 2>/dev/null || cd "${APP_DIR}" || exit 1; raise_oom_score_adj "${UI_OOM_SCORE_RAISE}" "the web UI"
      exec env -i "${ui_env[@]}" "${UI_EXEC[@]}") &
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

  # Whatever ended it, the relays first: the HAT holds them as the controller left them, possibly during a move.
  if [[ "${name}" == controller ]]; then
    de_energize_relays "the controller exited ($(describe_exit "${code}"))"
  fi

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
    if [[ "${RELAYS_STATE}" == off ]]; then
      log "The controller is left stopped with the relays off (verified by read-back)."
    else
      log "WARNING: the controller is left stopped with the relays NOT verified off; the supervisor keeps trying."
    fi
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
    de_energize_relays "the controller was killed for a forced restart"
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
  retry_relays_off
}

# Sleeps for a tick. A signal ends the sleep at once (the trap runs, then wait returns), and so does the exit of a running
# process, so that the next tick turns the relays off as soon as the controller has exited.
sleep_tick() {
  local name
  local -a running=()
  for name in controller ui; do
    [[ "${STATE[${name}]}" != running || -z "${PID[${name}]}" ]] || running+=("${PID[${name}]}")
  done
  sleep "${TICK_SECONDS}" &
  SLEEP_PID=$!
  wait -n "${SLEEP_PID}" "${running[@]}" 2>/dev/null
  kill "${SLEEP_PID}" 2>/dev/null
  wait "${SLEEP_PID}" 2>/dev/null
  SLEEP_PID=""
}

# Sends $1 SIGTERM, if it runs, and notes when its wait of $2 seconds ends (finish_stop). $3 names it in the log.
begin_stop() {
  local name=$1 seconds=$2 label=$3
  STOP_DEADLINE[${name}]=$(( $(now_us) + seconds * 1000000 ))
  if ! is_running "${name}"; then
    STATE[${name}]=stopped
    return
  fi
  STATE[${name}]=stopping
  write_state
  log "Stopping ${label} (SIGTERM, up to ${seconds}s)"
  kill -TERM "${PID[${name}]}" 2>/dev/null
}

# Waits for $1 until its wait from begin_stop ends, kills it (SIGKILL) if it still runs, and records how it ended.
finish_stop() {
  local name=$1 seconds=$2 label=$3 code
  [[ "${STATE[${name}]}" == stopping ]] || return 0
  while is_running "${name}" && (( $(now_us) < STOP_DEADLINE[${name}] )); do
    sleep 0.1
  done
  if is_running "${name}"; then
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

stop_process() {
  begin_stop "$@"
  finish_stop "$@"
}

# docker stop: the controller first (its shutdown stops the roof), then every relay off and verified while the web UI
# stops, so the relays wait no longer than the controller and the web UI's own wait overlaps them. Also run by on_exit,
# so it may run twice: what is already stopped is left as it is, and the relays are turned off again.
shutdown() {
  SUPERVISOR_STATE=stopping
  log "Stopping: the controller first, then the relays off while the web UI stops"
  stop_process controller "${CONTROLLER_STOP_SECONDS}" "the controller"
  begin_stop ui "${UI_STOP_SECONDS}" "the web UI"
  de_energize_relays "the supervisor is stopping"
  finish_stop ui "${UI_STOP_SECONDS}" "the web UI"
  SUPERVISOR_STATE=stopped
  write_state
  SHUTDOWN_DONE=1
  log "Stopped"
}

# shellcheck disable=SC2317 # called by the trap
on_signal() {
  STOP_REQUESTED=1
  [[ -z "${SLEEP_PID}" ]] || kill "${SLEEP_PID}" 2>/dev/null
}

# The supervisor ends some other way: another signal, or an error in this script. Its processes would end with the
# container, the controller without its shutdown and the relays as they were, so it stops as on docker stop first. A
# signal can end it in the middle of a command whose output is redirected (wait ... 2>/dev/null), and the trap runs with
# that redirection in place, so the container's log is put back first.
# shellcheck disable=SC2317 # called by the trap
on_exit() {
  (( SHUTDOWN_DONE )) && return
  exec 1>&"${LOG_OUT}" 2>&"${LOG_ERR}"
  log "The supervisor is ending unexpectedly; stopping as on docker stop first"
  shutdown
}

# The supervisor's own output and error, the container's log, for on_exit.
exec {LOG_OUT}>&1 {LOG_ERR}>&2
trap on_signal TERM INT HUP QUIT
trap on_exit EXIT
prepare_run_dir
check_secrets_are_private
log "Starting the controller and the web UI (crash limit ${CRASH_LIMIT} within ${CRASH_WINDOW_SECONDS}s;" \
  "backoff at most ${BACKOFF_MAX_SECONDS}s; forced restarts ignored within ${FORCE_RESTART_MIN_SECONDS}s of a start;" \
  "the relays turned off within ${RELAYS_OFF_SECONDS}s whenever the controller is not running;" \
  "stop waits ${CONTROLLER_STOP_SECONDS}s for the controller, then up to ${RELAYS_OFF_SECONDS}s for the relays off, with" \
  "${UI_STOP_SECONDS}s for the web UI at the same time)"
start_controller
start_ui
while (( ! STOP_REQUESTED )); do
  tick
  (( STOP_REQUESTED )) || sleep_tick
done
shutdown
exit 0
