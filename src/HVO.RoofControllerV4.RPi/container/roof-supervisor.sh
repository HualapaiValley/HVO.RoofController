#!/usr/bin/env bash
# The roof controller container's supervisor: the controller and the web UI, two processes in one container
# (docs/deployment.md, "The container's two processes").
#
# tini is PID 1. It reaps orphans and passes SIGTERM and SIGINT to this script, which:
# - whenever the controller is not running: turns every relay on the HAT off and verifies the register by reading it
#   back (de_energize_relays), because the HAT holds its relays as last written until it loses power, so a controller
#   that died during a move would leave the drive running. It does this as soon as the controller exits (a crash, a
#   kill, a failed start, a requested restart), before each start, again and again while the controller is not running
#   until it succeeds, and when it stops itself. It never turns a relay on. The controller starts only once a read-back
#   has shown every relay off: until then it is held stopped (state relays-unverified) and the relay-off is tried again,
#   at the initial start, before every restart, after a crash and on a forced restart (the owner's decision, #138).
# - on docker stop (SIGTERM, also SIGINT, SIGHUP and SIGQUIT): starts nothing more, and stops the controller and the
#   web UI, each with SIGTERM: the controller waited for up to HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS, because its
#   shutdown stops the roof and verifies the relays off, the web UI up to HVO_SUPERVISOR_UI_STOP_SECONDS, at the same
#   time. Once both have ended, and so freed their tasks, it turns the relays off (the controller may have been killed,
#   or have failed to verify them), and exits. The whole stop is timed from the signal (note_stop, in the trap, without
#   a fork): work already running when it came (a relay-off) counts against it, and each later wait is cut to what is
#   left, so the stop ends within the controller's wait and the longer of the other two (27 s with the defaults), below
#   the container's stop timeout: Docker never kills the controller first, and the last relay-off begins and ends in
#   time, also at the container's process limit (it forks nothing before then: see "Forks" below). Once a wait runs out,
#   that process is killed (SIGKILL), and given at most 1 s more to end: a process stuck in the kernel (a hung
#   filesystem or bus) cannot run its own code again, and the stop goes on without it. A web UI preparation still
#   running is abandoned at once (its processes are killed), and nothing starts after it. Any other way this script
#   ends (another signal, an error, a fork that failed for good) stops the same way first (on_exit).
# - when the controller exits with 75 (a restart requested through POST System/Restart): starts it again at once.
# - when the controller exits otherwise (a crash): starts it again after a backoff that doubles from 1 s (1, 2, 4, 8 s,
#   ...), at most HVO_SUPERVISOR_BACKOFF_MAX_SECONDS. The HVO_SUPERVISOR_CRASH_LIMIT-th crash within
#   HVO_SUPERVISOR_CRASH_WINDOW_SECONDS leaves the controller stopped instead of looping while the roof may need
#   attention (with the defaults, the fifth: so its delays are 1, 2, 4 and 8 s). The container's health check (the
#   controller's readiness) then fails, and the web UI says why.
# - when the web UI exits: starts only the web UI again, with the same backoff, and never gives up. The controller is
#   not touched.
# - before each start of the web UI: prepares it (its copies of the certificate and the Stop key, its keys directory,
#   the check that it cannot read the controller's secrets, and its whole environment) in a process of its own, in the
#   background, which must finish within HVO_SUPERVISOR_UI_PREPARE_SECONDS or is killed (and tried again later, with
#   the backoff). Nothing the preparation waits for (a slow or hung filesystem, a stalled environment) holds up the
#   supervisor: a controller that exits meanwhile has its relays turned off at once, a stop abandons the preparation,
#   and the web UI starts only once it is done, when no stop has been asked for.
# - Nothing starts once a stop is asked for: the last thing the supervisor does before it starts the controller or the
#   web UI is to check for a stop, with nothing that can wait in between, and the new process checks its copy of that
#   flag again before it becomes the controller or the web UI. What the new web UI process reads (its environment) it
#   reads itself, so that a read that waits holds up only that process, which a stop ends.
# - on a forced restart: <run dir>/control/force-restart-controller appears (docker exec ... touch, or the web UI's
#   ControllerForcedRestart) when the controller does not answer. The controller is killed (SIGKILL, as docker kill
#   does), the relays are turned off, and it is started again at once, once they are verified off (otherwise it is
#   held stopped). This also starts a controller left stopped after a crash loop. A request that arrives within
#   HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS of the controller's start is ignored, so a repeated request cannot kill a
#   controller that is still starting. supervisor.json records what was done with the last request (restarted, held
#   or ignored).
# - with arguments (the deployment check, --validate-deployment): runs only the controller with them, in its place. It
#   never turns the relays off then: another controller may be driving the HAT. (--prepare-ui is the supervisor's own:
#   the web UI's preparation, which start_ui runs as a process of its own.)
#
# The relays are turned off by the controller's own program, with --relays-off (HVO_SUPERVISOR_RELAYS_OFF_EXEC replaces
# it for the tests): the HAT access the controller uses, without the controller's settings checks, so it works when the
# controller cannot start. It must finish within HVO_SUPERVISOR_RELAYS_OFF_SECONDS or is killed.
#
# The kernel ends the process with the highest OOM score when the container (or the Pi) runs out of memory; the score
# counts memory use and oom_score_adj. The supervisor must outlive the controller to turn the relays off, so it raises
# the web UI's oom_score_adj by 500 and the controller's by 100 above its own (raising needs no capability): the kernel
# favours ending them first. A preference, not a fixed order: a controller grown large enough can go before the web UI.
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
# Forks and the container's process limit (#139 review F6). At the container's limit (pids.max: 512 tasks by default), a
# fork fails, and bash retries it for about 15 s and then ends this script: the stop's trap would wait for it, and the
# relays would be left as they are. So once it has begun, the supervisor forks only to start a process (the controller,
# the web UI, its preparation, the relay-off helper), to replace its state file or to remove a forced restart's request,
# and only when tasks_free finds room for it; all else (the time, its waits and naps, the text of its state and log)
# uses builtins. When there is no room, a relay-off first kills the web UI to make some (never the relay-off, and the
# controller is not running then), and a start waits. The stop does not fork at all until the controller, the web UI and
# the preparation have ended, which frees their tasks; only then does it start the last relay-off. The container's bash
# is 5.2: EPOCHREALTIME, printf -v with %(...)T, and read -t with a fraction of a second.
#
# <run dir>/supervisor.json says what the supervisor is doing, for the web UI and the health check. It is rewritten
# (atomically, except at the container's process limit: write_state) at each change.

set -uo pipefail
# The state file is readable by all (write_state), without a chmod, which would fork. (The web UI's preparation, a new
# bash, sets its own: owner-only.)
umask 022

readonly RESTART_EXIT_CODE=75
# The web UI's data directory when none is set, in directories only root can change (see prepare_ui_data_protection).
readonly DEFAULT_UI_DATA_DIR=/var/lib/hvo-roof-web
# How far the controller's and the web UI's oom_score_adj are raised above the supervisor's (see raise_oom_score_adj).
readonly CONTROLLER_OOM_SCORE_RAISE=100
readonly UI_OOM_SCORE_RAISE=500
# How long a process sent SIGKILL is waited for before the supervisor goes on without it (see reap_within).
readonly KILL_WAIT_US=1000000
# How many tasks (processes and threads) must be free in the container before the supervisor starts a process (see
# tasks_free): the relay-off helper, a .NET program, uses up to 24 (measured), so 40 leaves a margin.
readonly TASKS_FOR_A_START=40
# How long the supervisor waits at a time (nap) while it watches for an exit or a stop: how soon it notices one.
readonly NAP_SLICE_US=100000
# This script, which runs the web UI's preparation as a process of its own (start_ui).
readonly SELF=${BASH_SOURCE[0]}

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
UI_PREPARE_SECONDS=${HVO_SUPERVISOR_UI_PREPARE_SECONDS:-10}
TICK_SECONDS=${HVO_SUPERVISOR_TICK_SECONDS:-1}
# The container's cgroup, with its process limit (pids.max) and the tasks in it (pids.current): see tasks_free.
PIDS_DIR=${HVO_SUPERVISOR_PIDS_DIR:-/sys/fs/cgroup}

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

# The time now in UTC (2026-09-29T12:00:00Z), in ISO_NOW: printf's own clock, with no fork.
iso_now() {
  local -x TZ=UTC0
  printf -v ISO_NOW '%(%Y-%m-%dT%H:%M:%SZ)T' -1
}

# The time now, in microseconds since the epoch, in NOW_US (EPOCHREALTIME without its decimal separator, which follows
# the locale).
stamp() {
  NOW_US=${EPOCHREALTIME//[!0-9]/}
}

require_whole_number() {
  local name=$1 value=$2 min=$3
  if [[ ! "${value}" =~ ^[0-9]+$ ]] || (( value < min )); then
    log "${name} must be a whole number of at least ${min}, got '${value}'."
    exit 2
  fi
}

# Compose passes its explicit limits here as well as to Docker. Refuse a value below RoofController's floor before
# any controller process (including --validate-deployment), filesystem setup or HAT access. Unset means no limit until a
# follow-up (#138 item 6) enables the 1 vCPU / 2 GiB default. The deploy script validates its own inputs before Docker.
check_compose_limits() {
  local cpus=${HVO_ROOF_CPUS:-} memory=${HVO_ROOF_MEMORY:-} pattern fraction bytes
  if [[ -n "${cpus}" ]]; then
    pattern='^([0-9]{1,3})(\.([0-9]{1,2}))?$'
    if [[ ! "${cpus}" =~ ${pattern} ]]; then
      log "HVO_ROOF_CPUS must be a number of CPUs with at most two decimals, at least RoofController's floor of 1 vCPU."
      exit 2
    fi
    fraction="${BASH_REMATCH[3]:-}00"
    if (( 10#${BASH_REMATCH[1]} * 100 + 10#${fraction:0:2} < 100 )); then
      log "HVO_ROOF_CPUS=${cpus} is below RoofController's floor of 1 vCPU."
      exit 2
    fi
  fi
  if [[ -n "${memory}" ]]; then
    pattern='^([0-9]{1,6})([mMgG])$'
    if [[ ! "${memory}" =~ ${pattern} ]]; then
      log "HVO_ROOF_MEMORY must be a whole number of MiB or GiB (1024m or 1g), at least RoofController's floor of 1 GiB."
      exit 2
    fi
    bytes=$(( 10#${BASH_REMATCH[1]} * 1048576 ))
    [[ "${BASH_REMATCH[2]}" == [gG] ]] && bytes=$(( bytes * 1024 ))
    if (( bytes < 1073741824 )); then
      log "HVO_ROOF_MEMORY=${memory} is below RoofController's floor of 1 GiB."
      exit 2
    fi
  fi
}
check_compose_limits

# With arguments this is the deployment check (or another one-shot run of the controller): nothing is supervised.
# --prepare-ui is the web UI's preparation, run near the end of this script.
if (( $# > 0 )) && [[ "$1" != --prepare-ui ]]; then
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
require_whole_number HVO_SUPERVISOR_UI_PREPARE_SECONDS "${UI_PREPARE_SECONDS}" 1
if [[ ! "${TICK_SECONDS}" =~ ^([0-9]+|[0-9]*\.[0-9]+)$ ]]; then
  log "HVO_SUPERVISOR_TICK_SECONDS must be a number of seconds such as 1 or 0.2, got '${TICK_SECONDS}'."
  exit 2
fi
# The tick in microseconds.
TICK_FRACTION="${TICK_SECONDS#"${TICK_SECONDS%%.*}"}"
TICK_FRACTION="${TICK_FRACTION#.}000000"
TICK_US=$(( 10#0${TICK_SECONDS%%.*} * 1000000 + 10#${TICK_FRACTION:0:6} ))

# The web UI's group (its user's primary group) and home. Looked up once, here, before anything is started.
UI_GROUP=""
UI_HOME=""
if [[ -n "${UI_USER}" ]]; then
  UI_GROUP=$(id -gn "${UI_USER}" 2>/dev/null) || { log "HVO_SUPERVISOR_UI_USER names no user: '${UI_USER}'."; exit 2; }
  UI_HOME=$(getent passwd "${UI_USER}" | cut -d: -f6)
fi

# The state of each process: starting (the web UI only, while it is prepared), running, restarting (waiting to start
# again), crash-loop (left stopped: controller only), relays-unverified (held stopped until the relays are verified off:
# controller only), stopping or stopped.
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
# What the builtin helpers hand back (stamp, iso_now, describe_exit, crash_count, relays_retry_seconds, backoff_seconds,
# seconds_until, tasks_free), instead of printing it for a command substitution, which would fork.
NOW_US=0
ISO_NOW=""
EXIT_TEXT=""
CRASH_COUNT=0
RETRY_SECONDS=1
BACKOFF_SECONDS=1
SECONDS_LEFT=1
TASKS_TEXT=""
NAP_SINK=""
# The processes a start waits for room for (start_later), so that the wait is logged once.
declare -A WAITING_FOR_ROOM=([controller]="" [ui]="")
# The last forced restart request: when it was handled, and whether the controller was restarted or the request ignored.
FORCED_RESTART_AT=""
FORCED_RESTART_OUTCOME=""
# The relays, as the supervisor knows them: unknown (not tried yet), controller (the controller runs and drives them),
# off (turned off and verified since the controller last ran) or unverified (the last try failed; retry_relays_off tries
# again). When and why they were last turned off, failed tries in a row, and when the next is due (microseconds).
RELAYS_STATE=unknown
RELAYS_AT=""
RELAYS_REASON=""
RELAYS_FAILURES=0
RELAYS_RETRY_DUE=0
# When each process's wait for its stop ends (microseconds), from begin_stop, and how long that wait was, for the log.
declare -A STOP_DEADLINE=([controller]=0 [ui]=0)
declare -A STOP_WAIT=([controller]="" [ui]="")
SHUTDOWN_DONE=0
# The signals the traps have had, when the stop was asked for, and when it must be done by (microseconds: note_stop).
SIGNALS=0
STOP_AT_US=0
STOP_BY_US=0
# The relay-off helper while it runs.
RELAYS_OFF_PID=""
# The web UI's preparation while it runs (start_ui): the pid of its timeout, which leads a process group of its own made
# only of the preparation's processes, and the file it writes the web UI's whole environment to (ui_environment; root's,
# mode 0600).
UI_PREPARE_PID=""
UI_SETTINGS_FILE="${RUN_DIR}/web-settings"
# How the last process reap_within waited for ended (its exit status).
REAPED_CODE=0

# json_text <variable> <text>: sets the variable to the text as a JSON string, or to null when it is empty.
json_text() {
  if [[ -z "$2" ]]; then printf -v "$1" 'null'; else printf -v "$1" '"%s"' "$2"; fi
}

# json_number <variable> <number>: sets the variable to the number, or to null when it is empty.
json_number() {
  if [[ -z "$2" ]]; then printf -v "$1" 'null'; else printf -v "$1" '%s' "$2"; fi
}

# process_json <variable> <name>: sets the variable to $2's object in the state file.
process_json() {
  local name=$2 _pid _code _reason _at
  crash_count "${name}"
  json_number _pid "${PID[${name}]}"
  json_number _code "${LAST_EXIT_CODE[${name}]}"
  json_text _reason "${LAST_EXIT_REASON[${name}]}"
  json_text _at "${LAST_EXIT_AT[${name}]}"
  printf -v "$1" '{"state":"%s","pid":%s,"starts":%s,"recentCrashes":%s,"lastExitCode":%s,"lastExitReason":%s,"lastExitAt":%s}' \
    "${STATE[${name}]}" "${_pid}" "${STARTS[${name}]}" "${CRASH_COUNT}" "${_code}" "${_reason}" "${_at}"
}

# forced_restart_json <variable>: sets the variable to the last forced restart's object, or to null.
forced_restart_json() {
  if [[ -z "${FORCED_RESTART_AT}" ]]; then
    printf -v "$1" 'null'
  else
    printf -v "$1" '{"at":"%s","outcome":"%s"}' "${FORCED_RESTART_AT}" "${FORCED_RESTART_OUTCOME}"
  fi
}

# relays_json <variable>: sets the variable to the relays' object.
relays_json() {
  local _at _reason
  json_text _at "${RELAYS_AT}"
  json_text _reason "${RELAYS_REASON}"
  printf -v "$1" '{"state":"%s","at":%s,"reason":%s}' "${RELAYS_STATE}" "${_at}" "${_reason}"
}

# Writes supervisor.json (mode 0644: umask 022): into a new file that then replaces it (mv), so that a reader never sees
# half of it; or, when the container has no room for mv's process (tasks_free), over it in place, which a reader may then
# find half written for a moment. Its text is made with builtins.
write_state() {
  local temp="${STATE_FILE}.tmp" text _forced _relays _controller _ui
  iso_now
  forced_restart_json _forced
  relays_json _relays
  process_json _controller controller
  process_json _ui ui
  printf -v text '{"supervisor":"%s","updatedAt":"%s","crashLimit":%s,"crashWindowSeconds":%s,"forceRestartMinSeconds":%s,%s}\n' \
    "${SUPERVISOR_STATE}" "${ISO_NOW}" "${CRASH_LIMIT}" "${CRASH_WINDOW_SECONDS}" "${FORCE_RESTART_MIN_SECONDS}" \
    "\"lastForcedRestart\":${_forced},\"relays\":${_relays},\"controller\":${_controller},\"ui\":${_ui}"
  if tasks_free; then
    printf '%s' "${text}" >"${temp}" && mv -f "${temp}" "${STATE_FILE}" && return 0
  else
    printf '%s' "${text}" >"${STATE_FILE}" && return 0
  fi
  log "WARNING: could not write ${STATE_FILE}"
}

# Creates the run directory: supervisor.json readable by all, the control directory writable only by the web UI's
# user (its group) and root, and the web UI's private directory for its certificate copy. Both directories belong to the
# supervisor's user (root): root reaches what the web UI writes in the control directory without CAP_DAC_OVERRIDE, which
# the container does not have (docs/deployment.md, "The container's privileges and limits"). The web UI's group may read
# the private directory: the web UI can read its copies but cannot replace them, or plant a link that the supervisor
# would then write through.
prepare_run_dir() {
  mkdir -p "${RUN_DIR}" || { log "cannot create ${RUN_DIR}"; exit 1; }
  chmod 0755 "${RUN_DIR}"
  rm -rf "${CONTROL_DIR}" "${UI_PRIVATE_DIR}"
  if [[ -n "${UI_USER}" ]]; then
    if ! install -d -m 0770 -g "${UI_GROUP}" "${CONTROL_DIR}" \
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

# reap_within <pid> <until, microseconds>: waits for that child to end (usually after SIGKILL), at most until the given
# time, and sets REAPED_CODE to its exit status. Returns 1 if it still runs then: a process stuck in the kernel (on a hung
# filesystem or bus) ends only when it leaves it, and can no longer run code of its own once it has been sent SIGKILL, so
# the supervisor goes on without it. Each pause is waited for, so that a stop signal is handled at once.
reap_within() {
  local pid=$1 until=$2
  stamp
  while kill -0 "${pid}" 2>/dev/null && (( NOW_US < until )); do
    nap "$(( until - NOW_US < 50000 ? until - NOW_US : 50000 ))"
    stamp
  done
  kill -0 "${pid}" 2>/dev/null && return 1
  wait "${pid}" 2>/dev/null
  REAPED_CODE=$?
  return 0
}

# Waits $1 microseconds without a fork: a read with a time limit on NAP_FD, a pipe that never has data (opened at the
# start). A trapped signal runs its trap at once but does not end the read, so the callers wait in short slices.
nap() {
  local seconds
  printf -v seconds '%d.%06d' "$(( $1 / 1000000 ))" "$(( $1 % 1000000 ))"
  # shellcheck disable=SC2034 # read needs a variable; nothing is ever read
  read -r -t "${seconds}" -u "${NAP_FD}" NAP_SINK
}

# Whether the container has room for a new process: at least TASKS_FOR_A_START tasks free under its process limit
# (pids.max, less pids.current, in PIDS_DIR: the container's own cgroup), read with builtins. No limit, or files that
# cannot be read, count as room. Sets TASKS_TEXT ("120 of 512 tasks") for the log.
tasks_free() {
  local max current
  TASKS_TEXT="no process limit"
  { read -r max <"${PIDS_DIR}/pids.max" && read -r current <"${PIDS_DIR}/pids.current"; } 2>/dev/null || return 0
  [[ "${max}" =~ ^[0-9]+$ && "${current}" =~ ^[0-9]+$ ]] || return 0
  TASKS_TEXT="${current} of ${max} tasks"
  (( max - current >= TASKS_FOR_A_START ))
}

# Room for the relay-off helper outside a stop (tasks_free). At the container's process limit a fork would wait about
# 15 s and then end the supervisor, leaving the relays as they are. So the web UI and its preparation are killed first,
# which frees their tasks (never the relay-off; the controller is not running then), and are started again later with
# the backoff; then it waits at most 1 s for the room. Returns 1 when there is still none: the relays then stay
# unverified, so the controller stays stopped (start_with_unverified_relays), and the relay-off is tried again later.
room_for_relays_off() {
  local until
  tasks_free && return 0
  log "WARNING: the container is at its process limit (${TASKS_TEXT}): killing the web UI to make room for the relay-off"
  stop_ui_preparation
  if [[ -n "${UI_PREPARE_PID}" ]]; then
    stamp
    reap_within "${UI_PREPARE_PID}" "$(( NOW_US + KILL_WAIT_US ))"
    UI_PREPARE_PID=""
  fi
  if is_running ui; then
    kill -KILL "${PID[ui]}" 2>/dev/null
    stamp
    reap_within "${PID[ui]}" "$(( NOW_US + KILL_WAIT_US ))"
    LAST_EXIT_CODE[ui]=137
    LAST_EXIT_REASON[ui]="killed to make room for the relay-off"
    iso_now
    LAST_EXIT_AT[ui]=${ISO_NOW}
  fi
  if [[ "${STATE[ui]}" == running || "${STATE[ui]}" == starting ]]; then
    PID[ui]=""
    record_crash ui
    backoff_seconds ui
    stamp
    START_DUE[ui]=$(( NOW_US + BACKOFF_SECONDS * 1000000 ))
    STATE[ui]=restarting
  fi
  stamp
  until=$(( NOW_US + KILL_WAIT_US ))
  until tasks_free; do
    stamp
    (( NOW_US < until )) || return 1
    nap 50000
  done
  return 0
}

# $1 is not started now: the container is at its process limit (tasks_free), where a fork would wait and then end the
# supervisor. Tried again in a second, without a fork meanwhile; logged once.
start_later() {
  local label="the controller"
  [[ "$1" == ui ]] && label="the web UI"
  stamp
  START_DUE[$1]=$(( NOW_US + 1000000 ))
  STATE[$1]=restarting
  if [[ -z "${WAITING_FOR_ROOM[$1]}" ]]; then
    WAITING_FOR_ROOM[$1]=1
    log "WARNING: ${label} is not started: the container is at its process limit (${TASKS_TEXT}); it starts once" \
      "there is room"
    write_state
  fi
}

# 1, 2, 4, 8, ... seconds after the first, second, third ... failed try in a row to turn the relays off, at most the
# backoff maximum, in RETRY_SECONDS.
relays_retry_seconds() {
  local count=${RELAYS_FAILURES} delay=1
  while (( count > 1 && delay < BACKOFF_MAX_SECONDS )); do
    delay=$((delay * 2))
    count=$((count - 1))
  done
  (( delay > BACKOFF_MAX_SECONDS )) && delay=${BACKOFF_MAX_SECONDS}
  RETRY_SECONDS=${delay}
}

# Turns every relay on the HAT off and verifies the register by reading it back, with the controller's own program
# (--relays-off: the HAT access the controller uses), within HVO_SUPERVISOR_RELAYS_OFF_SECONDS, or within $2 seconds for
# the stop's own (shutdown). Only while the controller is not running: it would stop that controller's move. It never
# turns a relay on. $1 says why, for the log and the state. Returns 1 when the relays could not be verified off;
# retry_relays_off then tries again. Once a stop is asked for, only the stop's own runs.
de_energize_relays() {
  local reason=$1 seconds=${2:-} code=0 started elapsed_ms why signals
  if [[ -z "${seconds}" ]]; then
    (( ! STOP_REQUESTED )) || return 1
    seconds=${RELAYS_OFF_SECONDS}
    # At the container's process limit, room first (the web UI goes); without it, the relays stay unverified.
    if ! room_for_relays_off; then
      relays_not_verified "${reason}" "could not start: the container is at its process limit (${TASKS_TEXT})"
      return 1
    fi
    # The last stop check before the fork.
    (( ! STOP_REQUESTED )) || return 1
  fi
  stamp
  started=${NOW_US}
  # In the background, so that a stop signal is handled at once (its trap notes when it came) while the helper runs on
  # to its end, within its time.
  (cd "${APP_DIR}" && exec timeout -s KILL "${seconds}" "${RELAYS_OFF_EXEC[@]}") &
  RELAYS_OFF_PID=$!
  while kill -0 "${RELAYS_OFF_PID}" 2>/dev/null; do
    signals=${SIGNALS}
    wait "${RELAYS_OFF_PID}"
    # wait returns early only for a signal a trap handles; otherwise the helper has ended.
    (( SIGNALS != signals )) || break
  done
  wait "${RELAYS_OFF_PID}"
  code=$?
  RELAYS_OFF_PID=""
  stamp
  elapsed_ms=$(( (NOW_US - started) / 1000 ))
  if (( code == 0 )); then
    iso_now
    RELAYS_AT=${ISO_NOW}
    RELAYS_REASON=${reason}
    RELAYS_STATE=off
    RELAYS_FAILURES=0
    log "The relays are off: every relay turned off and the HAT's register verified by read-back, ${elapsed_ms} ms" \
      "(${reason})"
    write_state
    return 0
  fi
  case ${code} in
    124|137) why="did not finish within ${seconds}s and was killed" ;;
    *) describe_exit "${code}"; why="ended with ${EXIT_TEXT}" ;;
  esac
  relays_not_verified "${reason}" "${why}, after ${elapsed_ms} ms"
  return 1
}

# The relays could not be verified off ($1 says why they were being turned off, $2 how that went): recorded, logged as
# a WARNING, and tried again after a delay that doubles (relays_retry_seconds).
relays_not_verified() {
  stamp
  iso_now
  RELAYS_AT=${ISO_NOW}
  RELAYS_REASON=$1
  RELAYS_STATE=unverified
  RELAYS_FAILURES=$((RELAYS_FAILURES + 1))
  relays_retry_seconds
  RELAYS_RETRY_DUE=$(( NOW_US + RETRY_SECONDS * 1000000 ))
  log "WARNING: the relays could not be verified off ($1): turning them off $2." \
    "The supervisor tries again in ${RETRY_SECONDS}s, and does not start the controller until they are verified off." \
    "Until then the drive may still be running: if the roof moves, use the independent stop (commissioning.md C1)."
  write_state
}

# While the controller is not running (waiting to start again, or left stopped after a crash loop) and the relays are
# not verified off: tries again, after a delay that doubles from 1 s (at most the backoff maximum).
retry_relays_off() {
  (( ! STOP_REQUESTED )) || return 0
  [[ "${RELAYS_STATE}" == unverified ]] || return 0
  [[ "${STATE[controller]}" == restarting || "${STATE[controller]}" == crash-loop ]] || return 0
  stamp
  (( NOW_US >= RELAYS_RETRY_DUE )) || return 0
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

# What the supervisor does when the relays could not be verified off just before a start: the policy for that case, and
# only here. The owner's decision (#138, 2026-10-17): "if the relays cannot be verified off, the controller stays
# stopped." So it does not start: the controller is held stopped (state relays-unverified, logged as a WARNING), and
# retry_held_controller tries the relays again after a delay that doubles from 1 s to HVO_SUPERVISOR_BACKOFF_MAX_SECONDS
# (30 s), for as long as it takes, and starts the controller once a read-back shows every relay off. A stop request is
# still honoured meanwhile (note_stop, shutdown). Returns 1: do not start. (Returning 0 would start it anyway, the
# earlier policy.)
start_with_unverified_relays() {
  local delay
  stamp
  delay=$(( (RELAYS_RETRY_DUE - NOW_US + 500000) / 1000000 ))
  (( delay >= 0 )) || delay=0
  STATE[controller]=relays-unverified
  PID[controller]=""
  log "WARNING: the controller is HELD STOPPED: the relays could not be verified off (${RELAYS_REASON}), and it starts" \
    "only once a read-back shows every relay off (#138). The supervisor tries again in ${delay}s, the delay doubling to" \
    "at most ${BACKOFF_MAX_SECONDS}s, for as long as it takes. Until then the drive may still be running: if the roof" \
    "moves, use the independent stop (commissioning.md C1)."
  write_state
  return 1
}

# The controller is held stopped because the relays could not be verified off: tries them again (tick, when due), and
# starts the controller once a read-back shows every relay off.
retry_held_controller() {
  de_energize_relays "trying again: the controller is held stopped until the relays are verified off" || return 0
  log "The relays are verified off: starting the controller, held stopped until now"
  start_controller
}

# The controller's own process (start_controller starts it): ends at once if a stop was handled just before the fork
# (its copy of STOP_REQUESTED), and becomes the controller, with a raised oom_score_adj.
exec_controller() {
  (( ! STOP_REQUESTED )) || exit 0
  cd "${APP_DIR}" || exit 1
  raise_oom_score_adj "${CONTROLLER_OOM_SCORE_RAISE}" "the controller"
  exec "${CONTROLLER_EXEC[@]}"
}

start_controller() {
  # Nothing starts once a stop is asked for.
  (( ! STOP_REQUESTED )) || return 1
  # Never with the relays as something else left them: at the supervisor's start (a container killed during a move),
  # or after a try that failed.
  [[ "${RELAYS_STATE}" == off ]] || de_energize_relays "before the controller starts"
  # Held stopped unless they are verified off; but not for a stop asked for while they were turned off, which the check
  # below ends.
  (( STOP_REQUESTED )) || [[ "${RELAYS_STATE}" == off ]] || start_with_unverified_relays || return 1
  # At the container's process limit, later (start_later): a fork would wait and then end the supervisor.
  (( STOP_REQUESTED )) || tasks_free || { start_later controller; return 1; }
  # The last stop check, immediately before the fork: nothing runs between them (exec_controller checks again).
  (( ! STOP_REQUESTED )) || return 1
  exec_controller &
  PID[controller]=$!
  WAITING_FOR_ROOM[controller]=""
  stamp
  STARTED_AT[controller]=${NOW_US}
  STARTS[controller]=$((STARTS[controller] + 1))
  STATE[controller]=running
  RELAYS_STATE=controller
  log "Started the controller (pid ${PID[controller]}, start ${STARTS[controller]})"
  write_state
}

# copy_for_ui <source> <copy>: a copy of <source> (or /dev/stdin) for the web UI in its private directory, mode 0400 and
# owned by the web UI's user. install writes it as root, replacing whatever is at the path and never writing through a
# link, and root then gives it to the web UI's user. (install -o would change the owner first and the mode after it,
# which needs CAP_FOWNER once root no longer owns the file; the container does not have it: docs/deployment.md, "The
# container's privileges and limits".) Only root can change the private directory, so nothing can put a link in place of
# the copy between the two. A copy that cannot be given to the web UI is removed.
copy_for_ui() {
  install -m 0400 "$1" "$2" || return 1
  [[ -z "${UI_USER}" ]] || chown -h "${UI_USER}:${UI_GROUP}" "$2" || { rm -f "$2"; return 1; }
}

# The web UI's certificate, for HTTPS: either its own (RoofWeb__Certificate__Path, with the password in
# RoofWeb__Certificate__PasswordFile or none), or else the controller's (Kestrel__Certificates__Default__Path, with the
# controller's certificate password from the secrets directory or the environment). The controller's password is never
# given with a certificate of the web UI's own. The web UI's user cannot read the secrets directory or, usually, the
# certificate mount, so it gets its own copies (owner-only) in its private directory, taken again at each start so a
# renewed certificate is used. Every copy is written by copy_for_ui. Prints the RoofWeb__Certificate__* settings for the
# web UI, one per line.
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
  rm -f "${copy}" "${password_copy}"
  copy_for_ui "${source}" "${copy}" || return 0
  printf 'RoofWeb__Certificate__Path=%s\n' "${copy}"
  if [[ -n "${password_file}" && ! -r "${password_file}" ]]; then
    log "WARNING: the web UI's certificate password file ${password_file} cannot be read; the web UI gets no password" \
      "for its certificate"
    return 0
  fi
  if [[ -n "${password_file}" ]]; then
    copy_for_ui "${password_file}" "${password_copy}" || return 0
  elif [[ -n "${password}" ]]; then
    printf '%s' "${password}" | copy_for_ui /dev/stdin "${password_copy}" || return 0
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
  [[ -n "${source}" ]] || return 0
  rm -f "${copy}"
  if [[ ! -r "${source}" ]]; then
    log "WARNING: the web UI's Stop key ${source} cannot be read; the web UI's Stop uses the person's session alone"
    return 0
  fi
  copy_for_ui "${source}" "${copy}" || return 0
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

# The web UI's environment, each NAME=value ended by a NUL: only its own settings and a few general variables (never the
# controller's keys or settings), the settings for its copies and its keys directory (prepare_ui_certificate,
# prepare_ui_stop_key, prepare_ui_data_protection), where the supervisor's state and control directory are, and its HOME
# (and USER). The preparation writes it, so that reading the container's environment is bounded and abandoned at a stop
# like the rest of it (#139 review F5); the web UI's own process reads it (exec_ui).
ui_environment() {
  local entry name
  local -a ui_env=("PATH=${PATH}" "TZ=${TZ:-UTC}")
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
    ui_env+=("HOME=${UI_HOME:-/tmp}" "USER=${UI_USER}")
  else
    ui_env+=("HOME=${HOME:-/tmp}")
  fi
  printf '%s\0' "${ui_env[@]}"
}

# Starts the web UI, in two steps. First its preparation (check_secrets_are_private and ui_environment), which this
# script runs itself (--prepare-ui) as a process of its own, in the background, under timeout: when
# HVO_SUPERVISOR_UI_PREPARE_SECONDS run out, timeout kills its process group, which holds only the preparation's
# processes. The supervisor never waits for it, so nothing it waits for (a slow or hung filesystem, a stalled
# environment) holds up the relays, the controller or a stop: tick starts the web UI (launch_ui) once it has ended, and
# a stop abandons it (stop_ui_preparation). It writes the web UI's environment to a file only root can read.
start_ui() {
  (( ! STOP_REQUESTED )) || return 1
  tasks_free || { start_later ui; return 1; }
  # The last stop check, immediately before the fork.
  (( ! STOP_REQUESTED )) || return 1
  (umask 077; exec timeout -s KILL "${UI_PREPARE_SECONDS}" "${BASH}" "${SELF}" --prepare-ui) &
  UI_PREPARE_PID=$!
  STATE[ui]=starting
  write_state
}

# The web UI's preparation runs.
ui_preparation_runs() {
  [[ -n "${UI_PREPARE_PID}" ]] && kill -0 "${UI_PREPARE_PID}" 2>/dev/null
}

# Abandons the web UI's preparation, if it runs (a stop): kills its process group, and the timeout that leads it (in
# case it has not made the group yet). Never waits: also called from on_signal. shutdown then waits for it, at most 1 s.
stop_ui_preparation() {
  ui_preparation_runs || return 0
  kill -KILL -- "-${UI_PREPARE_PID}" 2>/dev/null
  kill -KILL "${UI_PREPARE_PID}" 2>/dev/null
}

# The web UI's own process (launch_ui starts it): ends at once if a stop was handled just before the fork (its copy of
# STOP_REQUESTED), reads the environment its preparation wrote, and becomes the web UI with only that environment, as
# its own user, with a raised oom_score_adj. The read is here, in the new process, so that whatever it may wait for
# holds up only this process (which a stop ends), never the supervisor.
exec_ui() {
  local -a ui_env=()
  (( ! STOP_REQUESTED )) || exit 0
  mapfile -d '' -t ui_env <"${UI_SETTINGS_FILE}" || exit 1
  if [[ -n "${UI_USER}" ]]; then
    cd "${APP_DIR}/web" || exit 1
    raise_oom_score_adj "${UI_OOM_SCORE_RAISE}" "the web UI"
    exec setpriv --reuid="${UI_USER}" --regid="${UI_GROUP}" --init-groups --no-new-privs \
      env -i "${ui_env[@]}" "${UI_EXEC[@]}"
  fi
  cd "${APP_DIR}/web" 2>/dev/null || cd "${APP_DIR}" || exit 1
  raise_oom_score_adj "${UI_OOM_SCORE_RAISE}" "the web UI"
  exec env -i "${ui_env[@]}" "${UI_EXEC[@]}"
}

# The web UI's preparation has ended: starts the web UI (exec_ui). A preparation that failed (killed when its time ran
# out) counts as a failed start: the web UI is prepared again later, with the backoff.
launch_ui() {
  local code why delay
  wait "${UI_PREPARE_PID}" 2>/dev/null
  code=$?
  UI_PREPARE_PID=""
  if (( code != 0 )); then
    case ${code} in
      124|137) why="did not finish within ${UI_PREPARE_SECONDS}s and was killed" ;;
      *) describe_exit "${code}"; why="ended with ${EXIT_TEXT}" ;;
    esac
    record_crash ui
    backoff_seconds ui
    delay=${BACKOFF_SECONDS}
    stamp
    START_DUE[ui]=$(( NOW_US + delay * 1000000 ))
    STATE[ui]=restarting
    log "WARNING: the web UI's preparation (its copies of the certificate and the Stop key, its keys directory) ${why};" \
      "the web UI was not started. Preparing it again in ${delay}s"
    write_state
    return 1
  fi
  # At the container's process limit, later: prepared again then.
  tasks_free || { start_later ui; return 1; }
  # The last stop check, immediately before the fork: the preparation has gathered the web UI's environment and the new
  # process reads it itself, so nothing runs between them (exec_ui checks again).
  (( ! STOP_REQUESTED )) || return 1
  exec_ui &
  PID[ui]=$!
  WAITING_FOR_ROOM[ui]=""
  stamp
  STARTED_AT[ui]=${NOW_US}
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

# 1, 2, 4, 8, ... seconds for the first, second, third ... crash within the window, at most the backoff maximum, in
# BACKOFF_SECONDS.
backoff_seconds() {
  local count delay=1
  crash_count "$1"
  count=${CRASH_COUNT}
  while (( count > 1 && delay < BACKOFF_MAX_SECONDS )); do
    delay=$((delay * 2))
    count=$((count - 1))
  done
  (( delay > BACKOFF_MAX_SECONDS )) && delay=${BACKOFF_MAX_SECONDS}
  BACKOFF_SECONDS=${delay}
}

# How many crashes of $1 are within the crash window, in CRASH_COUNT (the times are separated by single spaces).
crash_count() {
  local spaces=${CRASHES[$1]//[^ ]/}
  CRASH_COUNT=0
  [[ -z "${CRASHES[$1]}" ]] || CRASH_COUNT=$(( ${#spaces} + 1 ))
}

# How an exit status ($1) ended a process, in EXIT_TEXT: "exit code N" or "killed by signal N".
describe_exit() {
  if (( $1 > 128 )); then
    printf -v EXIT_TEXT 'killed by signal %s' "$(( $1 - 128 ))"
  else
    printf -v EXIT_TEXT 'exit code %s' "$1"
  fi
}

# $1 has exited: reap it, record why, and decide when (or whether) it starts again.
handle_exit() {
  local name=$1 code=0 label delay count how
  wait "${PID[${name}]}" 2>/dev/null
  code=$?
  PID[${name}]=""
  LAST_EXIT_CODE[${name}]=${code}
  iso_now
  LAST_EXIT_AT[${name}]=${ISO_NOW}
  describe_exit "${code}"
  how=${EXIT_TEXT}
  label="the controller"
  [[ "${name}" == ui ]] && label="the web UI"

  # Whatever ended it, the relays first: the HAT holds them as the controller left them, possibly during a move.
  if [[ "${name}" == controller ]]; then
    de_energize_relays "the controller exited (${how})"
  fi

  if [[ "${name}" == controller && ${code} -eq ${RESTART_EXIT_CODE} ]]; then
    LAST_EXIT_REASON[${name}]="restart requested"
    log "The controller asked to be restarted (exit code ${RESTART_EXIT_CODE}); starting it again"
    stamp
    START_DUE[${name}]=${NOW_US}
    STATE[${name}]=restarting
    write_state
    return
  fi

  LAST_EXIT_REASON[${name}]="crashed (${how})"
  record_crash "${name}"
  crash_count "${name}"
  count=${CRASH_COUNT}
  if [[ "${name}" == controller ]] && (( count >= CRASH_LIMIT )); then
    STATE[${name}]=crash-loop
    log "The controller stopped (${how}): ${count} crashes within ${CRASH_WINDOW_SECONDS}s." \
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
  backoff_seconds "${name}"
  delay=${BACKOFF_SECONDS}
  stamp
  START_DUE[${name}]=$(( NOW_US + delay * 1000000 ))
  STATE[${name}]=restarting
  log "${label^} stopped (${how}); starting it again in ${delay}s (crash ${count} within ${CRASH_WINDOW_SECONDS}s)"
  write_state
}

handle_force_restart_request() {
  (( ! STOP_REQUESTED )) || return 0
  local running_us
  [[ -e "${FORCE_RESTART_REQUEST}" || -L "${FORCE_RESTART_REQUEST}" ]] || return 0
  # Its steps need forks (removing the request, the relay-off, the new controller): at the container's process limit
  # the request waits for room. docker stop or restart still works then (shutdown does not fork until it has room).
  if ! tasks_free; then
    if [[ "${FORCED_RESTART_OUTCOME}" != waiting ]]; then
      FORCED_RESTART_OUTCOME=waiting
      log "WARNING: a forced restart waits: the container is at its process limit (${TASKS_TEXT}); docker restart" \
        "works now"
    fi
    return 0
  fi
  rm -f "${FORCE_RESTART_REQUEST}"
  iso_now
  FORCED_RESTART_AT=${ISO_NOW}
  stamp
  running_us=$(( NOW_US - STARTED_AT[controller] ))
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
    stamp
    reap_within "${PID[controller]}" "$(( NOW_US + KILL_WAIT_US ))" \
      || log "WARNING: the controller (pid ${PID[controller]}) has not ended 1s after SIGKILL (stuck in the kernel?); it" \
        "can no longer run its own code, and the relays are turned off now"
    LAST_EXIT_CODE[controller]=137
    iso_now
    LAST_EXIT_AT[controller]=${ISO_NOW}
    PID[controller]=""
    STATE[controller]=restarting
    stamp
    START_DUE[controller]=${NOW_US}
    de_energize_relays "the controller was killed for a forced restart"
  fi
  LAST_EXIT_REASON[controller]="forced restart"
  # A deliberate restart: earlier crashes no longer count towards the crash loop.
  CRASHES[controller]=""
  # Not when a stop was asked for meanwhile, and not with the relays unverified (start_controller checks both).
  if ! start_controller && [[ "${STATE[controller]}" == relays-unverified ]]; then
    FORCED_RESTART_OUTCOME=held
    write_state
  fi
}

tick() {
  local name
  handle_force_restart_request
  for name in controller ui; do
    # A stop asked for meanwhile (handle_exit turns the relays off, which takes time): the stop comes next.
    (( ! STOP_REQUESTED )) || return 0
    case "${STATE[${name}]}" in
      running)
        is_running "${name}" || handle_exit "${name}" ;;
      restarting)
        stamp
        (( NOW_US >= START_DUE[${name}] )) && start "${name}" ;;
      relays-unverified)
        stamp
        (( NOW_US >= RELAYS_RETRY_DUE )) && retry_held_controller ;;
      starting)
        # The web UI while it is prepared: started once its preparation has ended.
        [[ "${name}" == ui && -n "${UI_PREPARE_PID}" ]] && ! ui_preparation_runs && launch_ui ;;
    esac
  done
  retry_relays_off
}

# Sleeps for a tick, in naps of at most 0.1 s (NAP_SLICE_US), and ends early when a stop is asked for or a running process
# (or the web UI's preparation) has exited, so that the next tick turns the relays off as soon as the controller has
# exited. It never forks (see "Forks" above).
sleep_tick() {
  local name until
  stamp
  until=$(( NOW_US + TICK_US ))
  while (( ! STOP_REQUESTED )); do
    for name in controller ui; do
      [[ "${STATE[${name}]}" == running ]] && ! is_running "${name}" && return 0
    done
    [[ -n "${UI_PREPARE_PID}" ]] && ! ui_preparation_runs && return 0
    stamp
    (( NOW_US < until )) || return 0
    nap "$(( until - NOW_US < NAP_SLICE_US ? until - NOW_US : NAP_SLICE_US ))"
  done
  return 0
}

# Notes when the stop was asked for (the first time only, at the signal: a builtin, no fork) and when it must be done by:
# the controller's wait, then the longer of the relay-off's and the web UI's (27 s with the defaults). The container's
# stop timeout (Docker's grace period, at least 30 s) is longer.
note_stop() {
  local after
  (( STOP_AT_US == 0 )) || return 0
  after=$(( RELAYS_OFF_SECONDS > UI_STOP_SECONDS ? RELAYS_OFF_SECONDS : UI_STOP_SECONDS ))
  stamp
  STOP_AT_US=${NOW_US}
  STOP_BY_US=$(( STOP_AT_US + (CONTROLLER_STOP_SECONDS + after) * 1000000 ))
}

# seconds_until <microseconds> <at most>: the seconds from now to that time, to the millisecond, at most the second
# number and at least 1 (a time for timeout), in SECONDS_LEFT.
seconds_until() {
  local us most=$(( $2 * 1000000 ))
  stamp
  us=$(( $1 - NOW_US ))
  (( us <= most )) || us=${most}
  (( us >= 1000000 )) || us=1000000
  printf -v SECONDS_LEFT '%d.%03d' "$(( us / 1000000 ))" "$(( us % 1000000 / 1000 ))"
}

# Sends $1 SIGTERM, if it runs; finish_stop waits for it until $2 (microseconds) and then kills it. $3 names it in the
# log. No fork (the state is written once the stop has freed the tasks).
begin_stop() {
  local name=$1 deadline=$2 label=$3
  stamp
  STOP_DEADLINE[${name}]=${deadline}
  STOP_WAIT[${name}]=$(( (deadline - NOW_US + 500000) / 1000000 ))
  if ! is_running "${name}"; then
    STATE[${name}]=stopped
    return
  fi
  STATE[${name}]=stopping
  log "Stopping ${label} (SIGTERM, up to ${STOP_WAIT[${name}]}s)"
  kill -TERM "${PID[${name}]}" 2>/dev/null
}

# Waits for $1 until the time begin_stop set, kills it (SIGKILL) if it still runs, gives it at most 1 s more to end
# (reap_within), and records how it ended. $2 names it in the log.
finish_stop() {
  local name=$1 label=$2 code
  [[ "${STATE[${name}]}" == stopping ]] || return 0
  stamp
  while is_running "${name}" && (( NOW_US < STOP_DEADLINE[${name}] )); do
    nap "$(( STOP_DEADLINE[${name}] - NOW_US < NAP_SLICE_US ? STOP_DEADLINE[${name}] - NOW_US : NAP_SLICE_US ))"
    stamp
  done
  if is_running "${name}"; then
    log "${label^} did not stop within ${STOP_WAIT[${name}]}s; killing it (SIGKILL)"
    kill -KILL "${PID[${name}]}" 2>/dev/null
  fi
  stamp
  if reap_within "${PID[${name}]}" "$(( NOW_US + KILL_WAIT_US ))"; then
    code=${REAPED_CODE}
    describe_exit "${code}"
    log "${label^} stopped (${EXIT_TEXT})"
  else
    code=""
    log "WARNING: ${label} (pid ${PID[${name}]}) has not ended 1s after SIGKILL (stuck in the kernel?); it can no longer" \
      "run its own code, and the stop goes on without it"
  fi
  PID[${name}]=""
  LAST_EXIT_CODE[${name}]=${code}
  LAST_EXIT_REASON[${name}]="stopped with the container"
  iso_now
  LAST_EXIT_AT[${name}]=${ISO_NOW}
  STATE[${name}]=stopped
}

# docker stop, or any other end: with no fork until every process that holds tasks has ended, so that it works, and in
# time, also at the container's process limit (#139 review F6). The web UI's preparation is killed (on_signal has
# already done so). The controller (whose shutdown stops the roof) and the web UI are sent SIGTERM at once, and each is
# killed (SIGKILL) when its own wait runs out: the web UI's after HVO_SUPERVISOR_UI_STOP_SECONDS, the controller's early
# enough to leave the last relay-off its time before the stop's deadline (note_stop, counted from the signal). Only
# then, with their tasks freed, does it start the last relay-off, with the time left, and write its state. Also run by
# on_exit, so it may run twice: what is already stopped is left as it is, and the relays are turned off again.
shutdown() {
  local relays_by stop_until
  STOP_REQUESTED=1
  note_stop
  SUPERVISOR_STATE=stopping
  log "Stopping: the controller and the web UI, then the relays off, all within" \
    "$(( (STOP_BY_US - STOP_AT_US) / 1000000 ))s of the stop request"
  # The web UI's preparation, if it runs, is abandoned (on_signal has already killed it): nothing starts after it.
  if [[ -n "${UI_PREPARE_PID}" ]]; then
    stop_ui_preparation
    stamp
    reap_within "${UI_PREPARE_PID}" "$(( NOW_US + KILL_WAIT_US ))" \
      || log "WARNING: the web UI's preparation (pid ${UI_PREPARE_PID}) has not ended 1s after SIGKILL"
    log "Abandoned the web UI's preparation: the web UI is not started"
    UI_PREPARE_PID=""
    STATE[ui]=stopped
  fi
  # A relay-off still running (begun before the signal, or on_exit in the middle of one) ends first, within its time.
  [[ -z "${RELAYS_OFF_PID}" ]] || wait "${RELAYS_OFF_PID}" 2>/dev/null
  # Both stopped at once; the latest either may run is when the last relay-off must begin to have its whole time.
  relays_by=$(( STOP_BY_US - RELAYS_OFF_SECONDS * 1000000 ))
  stamp
  stop_until=$(( NOW_US + CONTROLLER_STOP_SECONDS * 1000000 ))
  begin_stop controller "$(( stop_until < relays_by ? stop_until : relays_by ))" "the controller"
  stop_until=$(( NOW_US + UI_STOP_SECONDS * 1000000 ))
  begin_stop ui "$(( stop_until < relays_by ? stop_until : relays_by ))" "the web UI"
  # Each is killed at its own deadline: the earlier one first.
  if (( STOP_DEADLINE[ui] <= STOP_DEADLINE[controller] )); then
    finish_stop ui "the web UI"
    finish_stop controller "the controller"
  else
    finish_stop controller "the controller"
    finish_stop ui "the web UI"
  fi
  # Every process that holds tasks has ended, so there is room for the relay-off's process even at the container's
  # process limit. Should something else hold the tasks, it waits for room while the stop allows: a fork without room
  # would wait about 15 s and then end the supervisor, past the deadline.
  stamp
  stop_until=$(( STOP_BY_US - 1000000 ))
  until tasks_free || (( NOW_US >= stop_until )); do
    nap 50000
    stamp
  done
  if tasks_free; then
    seconds_until "${STOP_BY_US}" "${RELAYS_OFF_SECONDS}"
    de_energize_relays "the supervisor is stopping" "${SECONDS_LEFT}"
  else
    log "WARNING: the relays could not be turned off at the stop: the container is still at its process limit" \
      "(${TASKS_TEXT}) with the controller and the web UI ended. Until it starts again the drive may still be running:" \
      "if the roof moves, use the independent stop (commissioning.md C1)."
  fi
  SUPERVISOR_STATE=stopped
  write_state
  SHUTDOWN_DONE=1
  stamp
  log "Stopped, $(( (NOW_US - STOP_AT_US) / 1000 )) ms after the stop request"
}

# shellcheck disable=SC2317 # called by the trap
on_signal() {
  SIGNALS=$((SIGNALS + 1))
  STOP_REQUESTED=1
  note_stop
  stop_ui_preparation
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

# The web UI's preparation (start_ui runs this script with --prepare-ui, as a process of its own): writes the web UI's
# environment, with its copies and its keys directory, and ends.
if [[ "${1:-}" == --prepare-ui ]]; then
  # Owner-only (#139 review F7): this is a new bash, to which the script's umask 022 above would apply. The web UI's
  # environment file is root's, mode 0600: one left by an earlier preparation is removed, and the new one created with
  # noclobber (O_EXCL), so it is never readable by others, even for a moment, nor written through a link.
  umask 077
  check_secrets_are_private
  rm -f "${UI_SETTINGS_FILE}" || exit 1
  set -o noclobber
  ui_environment >"${UI_SETTINGS_FILE}" || exit 1
  exit 0
fi

# The supervisor's own output and error, the container's log, for on_exit; and NAP_FD, a pipe that never has data, for
# nap's reads with a time limit (the supervisor's waits, without a fork). Opening it forks once, here, at the start.
exec {LOG_OUT}>&1 {LOG_ERR}>&2
exec {NAP_FD}<> <(:)
trap on_signal TERM INT HUP QUIT
trap on_exit EXIT
prepare_run_dir
log "Starting the controller and the web UI (crash limit ${CRASH_LIMIT} within ${CRASH_WINDOW_SECONDS}s;" \
  "backoff at most ${BACKOFF_MAX_SECONDS}s; forced restarts ignored within ${FORCE_RESTART_MIN_SECONDS}s of a start;" \
  "the relays turned off within ${RELAYS_OFF_SECONDS}s whenever the controller is not running; a stop ends within" \
  "$(( CONTROLLER_STOP_SECONDS + (RELAYS_OFF_SECONDS > UI_STOP_SECONDS ? RELAYS_OFF_SECONDS : UI_STOP_SECONDS) ))s of" \
  "its request: up to ${CONTROLLER_STOP_SECONDS}s for the controller, with ${UI_STOP_SECONDS}s for the web UI at the same" \
  "time, then up to ${RELAYS_OFF_SECONDS}s for the relays off)"
start_controller
start_ui
while (( ! STOP_REQUESTED )); do
  tick
  (( STOP_REQUESTED )) || sleep_tick
done
shutdown
exit 0
