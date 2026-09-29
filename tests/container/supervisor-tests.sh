#!/usr/bin/env bash
# Tests for src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh, the container's supervisor of the controller and
# the web UI, and for healthcheck.sh, the container's health check. Both processes are stand-ins (fake-process.sh), and so
# is curl (fake-curl) and, for the tests that give the web UI its own user, setpriv (fake-setpriv), so no Docker, .NET,
# root or hardware is needed; the timings are shortened. They also check supervisor-state.sample.json, which the web
# UI's and the deploy script's tests read, against the supervisor's own output. The container scenarios
# (tests/emulator/deploy-scenarios.sh, group supervisor) run the real image.
# Requires bash 5, jq and setsid.
# Run: tests/container/supervisor-tests.sh [test-name ...]
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SUPERVISOR="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh"
HEALTHCHECK="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/container/healthcheck.sh"
DEPLOY_SCRIPT="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh"
SAMPLE="${TESTS_DIR}/supervisor-state.sample.json"

PASSED=0
FAILED=0
FAILURES=()
CURRENT_FAILED=false

# ---------------------------------------------------------------------------------------------------------------------
# Harness

setup() {
  WORK=$(mktemp -d)
  # The web UI stand-in gets it as one of the web UI's settings: the supervisor passes it no other variable.
  export FAKE_DIR="${WORK}/fake" RoofWeb__FakeDir="${WORK}/fake"
  mkdir -p "${FAKE_DIR}" "${WORK}/bin" "${WORK}/app/web" "${WORK}/secrets"
  ln -s "${TESTS_DIR}/fake-process.sh" "${WORK}/bin/controller"
  ln -s "${TESTS_DIR}/fake-process.sh" "${WORK}/bin/ui"
  : >"${FAKE_DIR}/events.log"
  SUPERVISOR_PID=""
  SUPERVISOR_ENV=(
    "HVO_SUPERVISOR_APP_DIR=${WORK}/app"
    "HVO_SUPERVISOR_RUN_DIR=${WORK}/run"
    "HVO_SUPERVISOR_SECRETS_DIR=${WORK}/secrets"
    "HVO_SUPERVISOR_CONTROLLER_EXEC=${WORK}/bin/controller"
    "HVO_SUPERVISOR_UI_EXEC=${WORK}/bin/ui"
    "HVO_SUPERVISOR_UI_USER="
    "HVO_SUPERVISOR_UI_DATA_DIR=${WORK}/data"
    "HVO_SUPERVISOR_TICK_SECONDS=0.1"
    "HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=5"
    "HVO_SUPERVISOR_UI_STOP_SECONDS=2"
    "HVO_SUPERVISOR_CRASH_LIMIT=3"
    "HVO_SUPERVISOR_CRASH_WINDOW_SECONDS=60"
    "HVO_SUPERVISOR_BACKOFF_MAX_SECONDS=1"
    "HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS=0"
  )
}

teardown() {
  if [[ -n "${SUPERVISOR_PID}" ]] && kill -0 "${SUPERVISOR_PID}" 2>/dev/null; then
    # Its own session: take the stand-ins down with it.
    kill -KILL -- "-${SUPERVISOR_PID}" 2>/dev/null
    wait "${SUPERVISOR_PID}" 2>/dev/null
  fi
  pkill -KILL -f "${WORK}/bin/" 2>/dev/null
  rm -rf "${WORK}"
}

fail_test() {
  CURRENT_FAILED=true
  echo "    FAIL: $*"
}

run_test() {
  local name=$1
  CURRENT_FAILED=false
  echo "  ${name}"
  setup
  "${name}"
  teardown
  if [[ "${CURRENT_FAILED}" == "true" ]]; then
    FAILED=$((FAILED + 1))
    FAILURES+=("${name}")
  else
    PASSED=$((PASSED + 1))
  fi
}

# start_supervisor [NAME=value...]: runs the supervisor in the background, in its own session, with the test settings
# plus the given variables, logging to supervisor.log.
start_supervisor() {
  setsid env "${SUPERVISOR_ENV[@]}" "$@" "${SUPERVISOR}" >"${WORK}/supervisor.log" 2>&1 &
  SUPERVISOR_PID=$!
}

# start_supervisor_as_ui_user [VAR=value ...]: start_supervisor, with the web UI given its own user (the test's own) and
# a stand-in setpriv that logs its options to setpriv.log, since changing user needs root. Sets UI_USER and UI_GROUP.
start_supervisor_as_ui_user() {
  mkdir -p "${WORK}/setpriv-bin"
  ln -sf "${TESTS_DIR}/fake-setpriv" "${WORK}/setpriv-bin/setpriv"
  UI_USER=$(id -un)
  UI_GROUP=$(id -gn)
  start_supervisor "HVO_SUPERVISOR_UI_USER=${UI_USER}" "PATH=${WORK}/setpriv-bin:${PATH}" "$@"
}

# start_supervisor_with_the_default_data_dir [VAR=value ...]: start_supervisor_as_ui_user, with the web UI's data
# directory left to its default (/var/lib/hvo-roof-web), and a stand-in install that logs its calls to install.log and
# makes nothing there, since that needs root.
start_supervisor_with_the_default_data_dir() {
  mkdir -p "${WORK}/install-bin"
  ln -sf "${TESTS_DIR}/fake-install" "${WORK}/install-bin/install"
  start_supervisor_as_ui_user "HVO_SUPERVISOR_UI_DATA_DIR=" "PATH=${WORK}/install-bin:${WORK}/setpriv-bin:${PATH}" "$@"
}

# wait_until <seconds> <description> <command...>: polls the command every 0.1 s; fails the test at the deadline.
wait_until() {
  local seconds=$1 description=$2 deadline
  shift 2
  deadline=$(awk -v now="$(date +%s%N)" -v s="${seconds}" 'BEGIN { printf "%.0f", now + s * 1e9 }')
  until "$@" 2>/dev/null; do
    if (( $(date +%s%N) > deadline )); then
      fail_test "timed out after ${seconds}s waiting for: ${description}"
      return 1
    fi
    sleep 0.1
  done
}

state() {
  jq -r "$1" "${WORK}/run/supervisor.json"
}

state_is() {
  [[ "$(state "$1")" == "$2" ]]
}

pid_of() {
  cat "${FAKE_DIR}/$1.pid"
}

events() {
  cat "${FAKE_DIR}/events.log"
}

event_count() {
  grep -c "^$1 $2" "${FAKE_DIR}/events.log"
}

# event_times <role> <event>: when the stand-in logged the event (say "start" or "exit 75"), in seconds, one a line.
event_times() {
  grep -E "^[0-9.]+ $1 $2( |\$)" "${FAKE_DIR}/times.log" | cut -d' ' -f1
}

# started <role> <n>: the stand-in has started at least n times (and written its pid, settings and arguments).
started() {
  (( $(event_count "$1" start) >= $2 ))
}

ui_env() {
  tr '\0' '\n' <"${FAKE_DIR}/ui.env"
}

# The state file with what changes from run to run (pids and times) made the same.
normalized_state() {
  sed -E 's/"pid":[0-9]+/"pid":1/g; s/"(updatedAt|lastExitAt|at)":"[^"]+"/"\1":"T"/g' "$1"
}

# Both stand-ins started, and the state file says so.
wait_both_running() {
  wait_until 10 "both processes running" state_is '.controller.state + " " + .ui.state' "running running" &&
    wait_until 5 "both stand-ins started" test -s "${FAKE_DIR}/ui.pid" -a -s "${FAKE_DIR}/controller.pid"
}

stop_supervisor() {
  local started=${EPOCHREALTIME}
  kill -TERM "${SUPERVISOR_PID}"
  wait "${SUPERVISOR_PID}"
  STOP_STATUS=$?
  STOP_SECONDS=$(awk -v a="${started}" -v b="${EPOCHREALTIME}" 'BEGIN { printf "%.3f", b - a }')
  SUPERVISOR_PID=""
}

expect_equal() {
  [[ "$2" == "$3" ]] || fail_test "$1: expected '$3', got '$2'"
}

# ---------------------------------------------------------------------------------------------------------------------
# Tests

test_starts_the_controller_and_the_web_ui() {
  start_supervisor
  wait_both_running || return
  expect_equal "supervisor state" "$(state .supervisor)" running
  expect_equal "controller pid" "$(state .controller.pid)" "$(pid_of controller)"
  expect_equal "web UI pid" "$(state .ui.pid)" "$(pid_of ui)"
  expect_equal "controller starts" "$(state .controller.starts)" 1
  local mode
  mode=$(stat -c %a "${WORK}/run/supervisor.json")
  expect_equal "state file mode" "${mode}" 644
  mode=$(stat -c %a "${WORK}/run/control")
  expect_equal "control directory mode" "${mode}" 700
  expect_equal "forced restart settings" "$(state '[.forceRestartMinSeconds, .lastForcedRestart] | tojson')" "[0,null]"
}

test_the_defaults_are_the_documented_ones() {
  # Only where things are; every timing and limit is the supervisor's own (docs/deployment.md, and CI checks the
  # container's stop_grace_period against the stop times).
  local -a paths=()
  local entry
  for entry in "${SUPERVISOR_ENV[@]}"; do
    case "${entry%%=*}" in
      HVO_SUPERVISOR_APP_DIR|HVO_SUPERVISOR_RUN_DIR|HVO_SUPERVISOR_SECRETS_DIR|HVO_SUPERVISOR_CONTROLLER_EXEC|\
      HVO_SUPERVISOR_UI_EXEC|HVO_SUPERVISOR_UI_USER|HVO_SUPERVISOR_UI_DATA_DIR) paths+=("${entry}") ;;
    esac
  done
  SUPERVISOR_ENV=("${paths[@]}")
  start_supervisor
  wait_both_running || return
  grep -qF "Starting the controller and the web UI (crash limit 5 within 120s; backoff at most 30s; forced restarts ignored within 10s of a start; stop waits 25s for the controller, then 2s for the web UI)" \
    "${WORK}/supervisor.log" || fail_test "the defaults are not the documented ones: $(head -n 1 "${WORK}/supervisor.log")"
  expect_equal "state file settings" "$(state '[.crashLimit, .crashWindowSeconds, .forceRestartMinSeconds] | tojson')" \
    "[5,120,10]"
}

test_docker_stop_stops_the_controller_first_and_waits_for_it() {
  # The controller takes 1.5 s over its roof stop; the web UI must not get SIGTERM before it has exited.
  echo 1.5 >"${FAKE_DIR}/controller.term-delay"
  start_supervisor
  wait_both_running || return
  stop_supervisor
  expect_equal "supervisor exit status" "${STOP_STATUS}" 0
  local order
  order=$(grep -E ' (term|exit)' "${FAKE_DIR}/events.log" | tr '\n' ',')
  expect_equal "stop order" "${order}" "controller term,controller exit 0,ui term,ui exit 0,"
  expect_equal "final state" "$(state '.supervisor + " " + .controller.state + " " + .ui.state')" "stopped stopped stopped"
  expect_equal "controller exit reason" "$(state .controller.lastExitReason)" "stopped with the container"
}

test_docker_stop_kills_a_controller_that_outlasts_its_stop_time() {
  touch "${FAKE_DIR}/controller.ignore-term"
  start_supervisor HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=2 HVO_SUPERVISOR_UI_STOP_SECONDS=1
  wait_both_running || return
  stop_supervisor
  expect_equal "supervisor exit status" "${STOP_STATUS}" 0
  # The full 2 s for the controller, then the web UI (which stops at once): within the two stop times, which the
  # container's stop timeout must exceed.
  awk -v s="${STOP_SECONDS}" 'BEGIN { exit !(s >= 2.0 && s < 3.5) }' || fail_test "stop took ${STOP_SECONDS}s, expected 2-3.5s"
  expect_equal "controller exit code" "$(state .controller.lastExitCode)" 137
  grep -q "did not stop within 2s; killing it" "${WORK}/supervisor.log" || fail_test "the kill is not logged"
  [[ "$(events)" == *"ui term"* ]] || fail_test "the web UI was not stopped after the controller was killed"
}

test_docker_stop_kills_a_web_ui_that_outlasts_its_stop_time() {
  touch "${FAKE_DIR}/ui.ignore-term"
  start_supervisor HVO_SUPERVISOR_UI_STOP_SECONDS=1
  wait_both_running || return
  stop_supervisor
  expect_equal "supervisor exit status" "${STOP_STATUS}" 0
  expect_equal "web UI exit code" "$(state .ui.lastExitCode)" 137
  awk -v s="${STOP_SECONDS}" 'BEGIN { exit !(s < 2.5) }' || fail_test "stop took ${STOP_SECONDS}s"
}

test_a_requested_restart_starts_only_the_controller_again_at_once() {
  start_supervisor HVO_SUPERVISOR_BACKOFF_MAX_SECONDS=30
  wait_both_running || return
  local first_controller ui
  first_controller=$(pid_of controller)
  ui=$(pid_of ui)
  echo 75 >"${FAKE_DIR}/controller.exit-now"
  # No backoff for a requested restart (the backoff here would be at least 1 s).
  wait_until 0.9 "a second controller start" state_is .controller.starts 2 || return
  wait_until 2 "the controller running again" state_is .controller.state running || return
  [[ "$(pid_of controller)" != "${first_controller}" ]] || fail_test "the controller was not started again"
  expect_equal "web UI pid" "$(pid_of ui)" "${ui}"
  expect_equal "web UI starts" "$(state .ui.starts)" 1
  expect_equal "exit reason" "$(state .controller.lastExitReason)" "restart requested"
  expect_equal "recent crashes" "$(state .controller.recentCrashes)" 0
  [[ "$(events)" != *"ui term"* ]] || fail_test "the web UI was stopped"
  local gap
  gap=$(paste -d' ' <(event_times controller "exit 75") <(event_times controller start | sed -n 2p) \
    | awk 'NF == 2 { printf "%.3f", $2 - $1 }')
  awk -v g="${gap}" 'BEGIN { exit !(g != "" && g < 0.5) }' \
    || fail_test "the controller started again ${gap:-(never)}s after its exit, expected under 0.5s (no backoff)"
}

test_a_controller_crash_restarts_only_the_controller_after_a_backoff() {
  start_supervisor
  wait_both_running || return
  local ui
  ui=$(pid_of ui)
  kill -KILL "$(pid_of controller)"
  wait_until 3 "the controller restarting" state_is .controller.state restarting || return
  expect_equal "exit reason" "$(state .controller.lastExitReason)" "crashed (killed by signal 9)"
  expect_equal "recent crashes" "$(state .controller.recentCrashes)" 1
  wait_until 3 "the controller running again" state_is .controller.starts 2 || return
  expect_equal "web UI pid" "$(pid_of ui)" "${ui}"
  expect_equal "web UI starts" "$(state .ui.starts)" 1
}

test_the_backoff_doubles_up_to_its_maximum() {
  echo 1 >"${FAKE_DIR}/controller.exit-at-start"
  start_supervisor HVO_SUPERVISOR_CRASH_LIMIT=10 HVO_SUPERVISOR_BACKOFF_MAX_SECONDS=4
  wait_until 20 "five controller starts (four crashes)" state_is .controller.starts 5 || return
  grep 'The controller stopped' "${WORK}/supervisor.log" | grep -o 'starting it again in [0-9]*s' | head -n 4 \
    | tr '\n' ',' >"${WORK}/delays"
  expect_equal "backoff delays" "$(cat "${WORK}/delays")" \
    "starting it again in 1s,starting it again in 2s,starting it again in 4s,starting it again in 4s,"
  # And it waits that long: each start comes at least its backoff after the crash before it, and within a few ticks of
  # it.
  paste -d' ' <(event_times controller "exit 1" | head -n 4) <(event_times controller start | sed -n 2,5p) \
    | awk 'BEGIN { split("1 2 4 4", delay) }
      NF == 2 { n++; gap = $2 - $1; if (gap < delay[n] || gap > delay[n] + 0.8) printf "backoff %d took %.3fs, expected %d-%.1fs; ", n, gap, delay[n], delay[n] + 0.8 }
      END { if (n != 4) printf "%d backoffs timed, expected 4", n }' >"${WORK}/gap-problems"
  [[ ! -s "${WORK}/gap-problems" ]] || fail_test "$(cat "${WORK}/gap-problems")"
}

test_a_crash_loop_leaves_the_controller_stopped_and_the_web_ui_up() {
  echo 1 >"${FAKE_DIR}/controller.exit-at-start"
  start_supervisor
  wait_until 15 "the crash loop" state_is .controller.state crash-loop || return
  expect_equal "controller starts" "$(state .controller.starts)" 3
  expect_equal "recent crashes" "$(state .controller.recentCrashes)" 3
  expect_equal "controller pid" "$(state .controller.pid)" null
  expect_equal "web UI state" "$(state .ui.state)" running
  # It stays stopped.
  sleep 2.5
  expect_equal "controller starts after the crash loop" "$(event_count controller start)" 3
  grep -q "It is left stopped, and the container's health check fails" "${WORK}/supervisor.log" \
    || fail_test "the crash loop is not logged"
  # The health check reads the supervisor's own state file.
  echo 200 >"${FAKE_DIR}/curl-8088.code"
  run_healthcheck
  expect_equal "health check status" "${STATUS}" 1
  expect_equal "health check output" "${OUTPUT}" \
    "controller: NOT READY (HTTP 000); web UI: live; supervisor: controller crash-loop, web UI running"
  # And docker stop still stops the web UI.
  stop_supervisor
  expect_equal "supervisor exit status" "${STOP_STATUS}" 0
  [[ "$(events)" == *"ui term"* ]] || fail_test "the web UI was not stopped"
}

test_crashes_outside_the_window_do_not_count_towards_a_crash_loop() {
  start_supervisor HVO_SUPERVISOR_CRASH_WINDOW_SECONDS=2 HVO_SUPERVISOR_CRASH_LIMIT=2
  wait_both_running || return
  kill -KILL "$(pid_of controller)"
  wait_until 3 "the second start" state_is .controller.starts 2 || return
  sleep 2.2
  kill -KILL "$(pid_of controller)"
  wait_until 3 "the third start" state_is .controller.starts 3 || return
  expect_equal "recent crashes" "$(state .controller.recentCrashes)" 1
  expect_equal "controller state" "$(state .controller.state)" running
}

test_a_web_ui_crash_restarts_only_the_web_ui() {
  start_supervisor
  wait_both_running || return
  local controller first_ui
  controller=$(pid_of controller)
  first_ui=$(pid_of ui)
  kill -KILL "${first_ui}"
  wait_until 4 "the web UI started again" state_is .ui.starts 2 || return
  wait_until 2 "the web UI running" state_is .ui.state running || return
  [[ "$(pid_of ui)" != "${first_ui}" ]] || fail_test "the web UI was not started again"
  expect_equal "controller pid" "$(pid_of controller)" "${controller}"
  expect_equal "controller starts" "$(state .controller.starts)" 1
  [[ "$(events)" != *"controller term"* ]] || fail_test "the controller was stopped"
}

test_the_web_ui_is_never_given_up() {
  echo 1 >"${FAKE_DIR}/ui.exit-at-start"
  start_supervisor HVO_SUPERVISOR_CRASH_LIMIT=2
  wait_until 10 "five web UI starts" state_is .ui.starts 5 || return
  expect_equal "controller state" "$(state .controller.state)" running
  expect_equal "controller starts" "$(state .controller.starts)" 1
}

test_a_forced_restart_kills_the_controller_and_starts_it_again() {
  echo 5 >"${FAKE_DIR}/controller.term-delay"
  start_supervisor
  wait_both_running || return
  local first ui
  first=$(pid_of controller)
  ui=$(pid_of ui)
  touch "${WORK}/run/control/force-restart-controller"
  wait_until 3 "a second controller start" state_is .controller.starts 2 || return
  [[ "$(pid_of controller)" != "${first}" ]] || fail_test "the controller was not started again"
  kill -0 "${first}" 2>/dev/null && fail_test "the first controller still runs"
  # SIGKILL: the stand-in never saw a SIGTERM (it would have taken 5 s).
  [[ "$(events)" != *"controller term"* ]] || fail_test "the controller got SIGTERM, not SIGKILL"
  expect_equal "exit reason" "$(state .controller.lastExitReason)" "forced restart"
  expect_equal "exit code" "$(state .controller.lastExitCode)" 137
  expect_equal "web UI pid" "$(pid_of ui)" "${ui}"
  [[ ! -e "${WORK}/run/control/force-restart-controller" ]] || fail_test "the request was not removed"
  expect_equal "recorded outcome" "$(state .lastForcedRestart.outcome)" restarted
  [[ "$(state .lastForcedRestart.at)" == 20*Z ]] || fail_test "no time recorded: $(state .lastForcedRestart)"
}

test_a_forced_restart_starts_a_controller_left_stopped_by_a_crash_loop() {
  echo 1 >"${FAKE_DIR}/controller.exit-at-start"
  start_supervisor
  wait_until 15 "the crash loop" state_is .controller.state crash-loop || return
  # The cause is fixed (say, the settings file), and an admin asks for a restart.
  rm -f "${FAKE_DIR}/controller.exit-at-start"
  touch "${WORK}/run/control/force-restart-controller"
  wait_until 3 "the controller running" state_is .controller.state running || return
  expect_equal "recent crashes" "$(state .controller.recentCrashes)" 0
  expect_equal "controller starts" "$(state .controller.starts)" 4
  expect_equal "recorded outcome" "$(state .lastForcedRestart.outcome)" restarted
}

test_a_forced_restart_just_after_a_start_is_ignored() {
  start_supervisor HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS=30
  wait_both_running || return
  touch "${WORK}/run/control/force-restart-controller"
  wait_until 3 "the request removed" test ! -e "${WORK}/run/control/force-restart-controller" || return
  sleep 0.5
  expect_equal "controller starts" "$(state .controller.starts)" 1
  grep -q "Ignored a forced restart request: the controller started" "${WORK}/supervisor.log" \
    || fail_test "the ignored request is not logged"
  expect_equal "recorded outcome" "$(state .lastForcedRestart.outcome)" ignored
}

test_a_request_left_from_before_the_start_is_discarded() {
  mkdir -p "${WORK}/run/control"
  touch "${WORK}/run/control/force-restart-controller"
  start_supervisor
  wait_both_running || return
  sleep 0.5
  expect_equal "controller starts" "$(state .controller.starts)" 1
  expect_equal "recorded request" "$(state .lastForcedRestart)" null
}

test_arguments_run_only_the_controller_with_them() {
  echo 3 >"${FAKE_DIR}/controller.exit-at-start"
  env "${SUPERVISOR_ENV[@]}" "${SUPERVISOR}" --validate-deployment >"${WORK}/supervisor.log" 2>&1
  local status=$?
  expect_equal "exit status (the controller's)" "${status}" 3
  expect_equal "controller arguments" "$(cat "${FAKE_DIR}/controller.args")" "--validate-deployment"
  [[ ! -e "${FAKE_DIR}/ui.pid" ]] || fail_test "the web UI was started"
  [[ ! -e "${WORK}/run/supervisor.json" ]] || fail_test "a state file was written"
}

test_the_web_ui_gets_only_its_own_settings() {
  start_supervisor RoofControllerSecurity__ApiKeys__0__Key=controller-only-value \
    BlueIris__Password=controller-only-value RoofWeb__Urls=http://+:8088 RoofWeb__ControllerUrl=http://localhost:8080 \
    ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://+:8080 TZ=UTC
  wait_both_running || return
  local ui_env controller_env
  ui_env=$(tr '\0' '\n' <"${FAKE_DIR}/ui.env")
  controller_env=$(tr '\0' '\n' <"${FAKE_DIR}/controller.env")
  [[ "${ui_env}" != *controller-only-value* ]] || fail_test "the web UI sees the controller's secrets"
  [[ "${ui_env}" != *ASPNETCORE_URLS* ]] || fail_test "the web UI sees the controller's URLs"
  [[ "${ui_env}" != *HVO_SUPERVISOR_* ]] || fail_test "the web UI sees the supervisor's settings"
  local expected
  for expected in RoofWeb__Urls=http://+:8088 RoofWeb__ControllerUrl=http://localhost:8080 \
      ASPNETCORE_ENVIRONMENT=Production TZ=UTC "RoofWeb__SupervisorStatePath=${WORK}/run/supervisor.json" \
      "RoofWeb__SupervisorControlPath=${WORK}/run/control"; do
    grep -qxF -- "${expected}" <<<"${ui_env}" || fail_test "the web UI does not get ${expected}"
  done
  [[ "${controller_env}" == *RoofControllerSecurity__ApiKeys__0__Key=controller-only-value* ]] \
    || fail_test "the controller does not get its settings"
  grep -qxF -- "ASPNETCORE_URLS=http://+:8080" <<<"${controller_env}" || fail_test "the controller does not get its URLs"
}

test_the_web_ui_gets_a_private_copy_of_the_certificate_for_https() {
  printf 'certificate bytes' >"${WORK}/certificate.pfx"
  printf 'certificate-password-value' >"${WORK}/secrets/Kestrel__Certificates__Default__Password"
  start_supervisor RoofWeb__Urls=https://+:8088 "Kestrel__Certificates__Default__Path=${WORK}/certificate.pfx"
  wait_both_running || return
  local ui_env copy password
  ui_env=$(tr '\0' '\n' <"${FAKE_DIR}/ui.env")
  copy=$(sed -n 's/^RoofWeb__Certificate__Path=//p' <<<"${ui_env}")
  password=$(sed -n 's/^RoofWeb__Certificate__PasswordFile=//p' <<<"${ui_env}")
  expect_equal "certificate copy" "${copy}" "${WORK}/run/web/certificate.pfx"
  expect_equal "password copy" "${password}" "${WORK}/run/web/certificate-password"
  expect_equal "certificate contents" "$(cat "${copy}")" "certificate bytes"
  expect_equal "password contents" "$(cat "${password}")" "certificate-password-value"
  expect_equal "certificate mode" "$(stat -c %a "${copy}")" 400
  expect_equal "password mode" "$(stat -c %a "${password}")" 400
  expect_equal "private directory mode" "$(stat -c %a "${WORK}/run/web")" 700
  [[ "${ui_env}" != *certificate-password-value* ]] || fail_test "the password is in the web UI's environment"
  [[ "${ui_env}" != *Kestrel__* ]] || fail_test "the web UI sees the controller's Kestrel settings"
}

test_plain_http_gives_the_web_ui_no_certificate() {
  printf 'certificate bytes' >"${WORK}/certificate.pfx"
  start_supervisor RoofWeb__Urls=http://+:8088 "Kestrel__Certificates__Default__Path=${WORK}/certificate.pfx"
  wait_both_running || return
  [[ "$(tr '\0' '\n' <"${FAKE_DIR}/ui.env")" != *RoofWeb__Certificate__* ]] || fail_test "the web UI got a certificate"
  [[ ! -e "${WORK}/run/web/certificate.pfx" ]] || fail_test "the certificate was copied"
}

test_a_link_left_where_the_copies_go_is_replaced_not_written_through() {
  printf 'certificate bytes' >"${WORK}/certificate.pfx"
  printf 'untouched' >"${WORK}/target"
  start_supervisor RoofWeb__Urls=https://+:8088 "Kestrel__Certificates__Default__Path=${WORK}/certificate.pfx" \
    Kestrel__Certificates__Default__Password=certificate-password-value
  wait_both_running || return
  # Links where the copies go (in the container the web UI's user cannot write there; the supervisor must not rely on
  # it), then a restart of the web UI, which takes the copies again.
  ln -sf "${WORK}/target" "${WORK}/run/web/certificate.pfx"
  ln -sf "${WORK}/target" "${WORK}/run/web/certificate-password"
  kill -KILL "$(pid_of ui)"
  wait_until 4 "the web UI started again" started ui 2 || return
  expect_equal "the links' target" "$(cat "${WORK}/target")" untouched
  [[ ! -L "${WORK}/run/web/certificate.pfx" && ! -L "${WORK}/run/web/certificate-password" ]] \
    || fail_test "a link is still there"
  expect_equal "certificate contents" "$(cat "${WORK}/run/web/certificate.pfx")" "certificate bytes"
  expect_equal "password contents" "$(cat "${WORK}/run/web/certificate-password")" "certificate-password-value"
  expect_equal "password mode" "$(stat -c %a "${WORK}/run/web/certificate-password")" 400
}

test_the_web_uis_own_certificate_never_gets_the_controllers_password() {
  printf 'web certificate' >"${WORK}/web.pfx"
  printf 'controller certificate' >"${WORK}/certificate.pfx"
  printf 'certificate-password-value' >"${WORK}/secrets/Kestrel__Certificates__Default__Password"
  start_supervisor RoofWeb__Urls=https://+:8088 "RoofWeb__Certificate__Path=${WORK}/web.pfx" \
    "Kestrel__Certificates__Default__Path=${WORK}/certificate.pfx" Kestrel__Certificates__Default__Password=env-password
  wait_both_running || return
  expect_equal "certificate setting" "$(ui_env | grep '^RoofWeb__Certificate__')" \
    "RoofWeb__Certificate__Path=${WORK}/run/web/certificate.pfx"
  expect_equal "certificate contents" "$(cat "${WORK}/run/web/certificate.pfx")" "web certificate"
  [[ ! -e "${WORK}/run/web/certificate-password" ]] || fail_test "the web UI got the controller's password"
}

test_the_web_uis_own_certificate_gets_its_own_password_file() {
  printf 'web certificate' >"${WORK}/web.pfx"
  printf 'web-password-value' >"${WORK}/web-password"
  start_supervisor RoofWeb__Urls=https://+:8088 "RoofWeb__Certificate__Path=${WORK}/web.pfx" \
    "RoofWeb__Certificate__PasswordFile=${WORK}/web-password"
  wait_both_running || return
  expect_equal "password setting" "$(ui_env | grep '^RoofWeb__Certificate__PasswordFile=')" \
    "RoofWeb__Certificate__PasswordFile=${WORK}/run/web/certificate-password"
  expect_equal "password contents" "$(cat "${WORK}/run/web/certificate-password")" "web-password-value"
  if (( EUID == 0 )); then
    return # root reads any file: the unreadable case cannot be made
  fi
  # A password file the supervisor cannot read: a warning, and the certificate without a password.
  chmod 000 "${WORK}/web-password"
  kill -KILL "$(pid_of ui)"
  wait_until 4 "the web UI started again" started ui 2 || return
  grep -qF "WARNING: the web UI's certificate password file ${WORK}/web-password cannot be read" "${WORK}/supervisor.log" \
    || fail_test "no warning for the unreadable password file"
  expect_equal "certificate setting" "$(ui_env | grep '^RoofWeb__Certificate__')" \
    "RoofWeb__Certificate__Path=${WORK}/run/web/certificate.pfx"
  [[ ! -e "${WORK}/run/web/certificate-password" ]] || fail_test "a password copy is left from the first start"
}

test_the_web_ui_gets_a_private_copy_of_its_stop_key() {
  # The Stop key is a Viewer key the controller reads from the secrets directory; the web UI cannot read that directory.
  local key="${WORK}/secrets/RoofControllerSecurity__ApiKeys__3__Key"
  printf 'stop-key-value-for-the-web-ui' >"${key}"
  start_supervisor "RoofWeb__StopKeyFile=${key}"
  wait_both_running || return
  expect_equal "Stop key setting" "$(ui_env | grep '^RoofWeb__StopKeyFile=')" "RoofWeb__StopKeyFile=${WORK}/run/web/stop-key"
  expect_equal "Stop key contents" "$(cat "${WORK}/run/web/stop-key")" "stop-key-value-for-the-web-ui"
  expect_equal "Stop key mode" "$(stat -c %a "${WORK}/run/web/stop-key")" 400
  [[ "$(ui_env)" != *stop-key-value-for-the-web-ui* ]] || fail_test "the Stop key is in the web UI's environment"

  # A rotated key is copied again when the web UI starts again.
  printf 'rotated-stop-key-for-the-web-ui' >"${key}"
  kill -KILL "$(pid_of ui)"
  wait_until 4 "the web UI started again" started ui 2 || return
  expect_equal "rotated Stop key" "$(cat "${WORK}/run/web/stop-key")" "rotated-stop-key-for-the-web-ui"
  if (( EUID == 0 )); then
    return # root reads any file: the unreadable case cannot be made
  fi
  # A key the supervisor cannot read: a warning, and no Stop key (Stop uses the person's session).
  chmod 000 "${key}"
  kill -KILL "$(pid_of ui)"
  wait_until 4 "the web UI started again" started ui 3 || return
  grep -qF "WARNING: the web UI's Stop key ${key} cannot be read" "${WORK}/supervisor.log" \
    || fail_test "no warning for the unreadable Stop key"
  ! ui_env | grep -q '^RoofWeb__StopKeyFile' || fail_test "the web UI was given a Stop key it cannot have"
  [[ ! -e "${WORK}/run/web/stop-key" ]] || fail_test "a Stop key copy is left from the last start"
}

test_the_web_ui_keeps_its_keys_across_its_restarts() {
  start_supervisor
  wait_both_running || return
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" \
    "RoofWeb__DataProtectionPath=${WORK}/data/keys"
  expect_equal "keys directory mode" "$(stat -c %a "${WORK}/data/keys")" 700
  printf 'key ring' >"${WORK}/data/keys/key-1.xml"
  kill -KILL "$(pid_of ui)"
  wait_until 4 "the web UI started again" started ui 2 || return
  expect_equal "keys kept" "$(cat "${WORK}/data/keys/key-1.xml")" "key ring"
}

test_the_web_ui_keeps_its_keys_where_it_is_told() {
  start_supervisor "RoofWeb__DataProtectionPath=${WORK}/volume/web-keys"
  wait_both_running || return
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" \
    "RoofWeb__DataProtectionPath=${WORK}/volume/web-keys"
  expect_equal "keys directory mode" "$(stat -c %a "${WORK}/volume/web-keys")" 700
  [[ ! -e "${WORK}/data/keys" ]] || fail_test "the default keys directory was made as well"
}

test_a_link_where_the_keys_go_is_not_used() {
  local before
  mkdir -p "${WORK}/data" "${WORK}/elsewhere"
  chmod 0755 "${WORK}/elsewhere"
  before=$(stat -c %a "${WORK}/elsewhere")
  ln -s "${WORK}/elsewhere" "${WORK}/data/keys"
  start_supervisor
  wait_both_running || return
  grep -qF "WARNING: cannot use ${WORK}/data/keys for the web UI's keys; the web UI keeps them in memory" \
    "${WORK}/supervisor.log" || fail_test "no warning for a link: $(cat "${WORK}/supervisor.log")"
  ! ui_env | grep -q '^RoofWeb__DataProtectionPath' || fail_test "the web UI was given a link for its keys"
  expect_equal "link target mode" "$(stat -c %a "${WORK}/elsewhere")" "${before}"
}

# A part of the path is a link, as the web UI's user could make one in a directory it can write: the directory is made
# through it by the web UI's user, with that user's rights alone (setpriv), and not by the supervisor's user (root).
test_a_keys_directory_of_the_operators_choosing_is_made_by_the_web_uis_user() {
  local path="${WORK}/home/app/x/web-keys" before
  mkdir -p "${WORK}/home/app" "${WORK}/elsewhere"
  chmod 0755 "${WORK}/elsewhere"
  before=$(stat -c %a "${WORK}/elsewhere")
  ln -s "${WORK}/elsewhere" "${WORK}/home/app/x"
  start_supervisor_as_ui_user "RoofWeb__DataProtectionPath=${path}"
  wait_both_running || return
  grep -qxF -- "--reuid=${UI_USER} --regid=${UI_GROUP} --init-groups --no-new-privs | install -d -m 0700 ${path}" \
    "${FAKE_DIR}/setpriv.log" || fail_test "the keys directory was not made as the web UI's user: $(cat "${FAKE_DIR}/setpriv.log")"
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" "RoofWeb__DataProtectionPath=${path}"
  expect_equal "keys directory" "$(stat -c '%a %U' "${WORK}/elsewhere/web-keys")" "700 ${UI_USER}"
  expect_equal "link target mode" "$(stat -c %a "${WORK}/elsewhere")" "${before}"
}

# The same for a data directory of the operator's choosing (HVO_SUPERVISOR_UI_DATA_DIR): its keys directory is made by
# the web UI's user, through a link in the path, and root makes nothing there.
test_a_data_directory_of_the_operators_choosing_is_made_by_the_web_uis_user() {
  local data="${WORK}/home/app/x/data" before
  mkdir -p "${WORK}/home/app" "${WORK}/elsewhere"
  chmod 0755 "${WORK}/elsewhere"
  before=$(stat -c %a "${WORK}/elsewhere")
  ln -s "${WORK}/elsewhere" "${WORK}/home/app/x"
  start_supervisor_as_ui_user "HVO_SUPERVISOR_UI_DATA_DIR=${data}"
  wait_both_running || return
  grep -qxF -- "--reuid=${UI_USER} --regid=${UI_GROUP} --init-groups --no-new-privs | install -d -m 0700 ${data}/keys" \
    "${FAKE_DIR}/setpriv.log" || fail_test "the keys directory was not made as the web UI's user: $(cat "${FAKE_DIR}/setpriv.log")"
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" "RoofWeb__DataProtectionPath=${data}/keys"
  expect_equal "keys directory" "$(stat -c '%a %U' "${WORK}/elsewhere/data/keys")" "700 ${UI_USER}"
  expect_equal "link target mode" "$(stat -c %a "${WORK}/elsewhere")" "${before}"
}

# expect_the_default_keys_directory_made_by_the_supervisor: the supervisor itself (root, in the container) made
# /var/lib/hvo-roof-web as root's (also when it was the web UI's user's) and a keys directory in it for the web UI's
# user, not through setpriv, and gave it to the web UI.
expect_the_default_keys_directory_made_by_the_supervisor() {
  grep -qxF -- "-d -m 0755 -o root -g root /var/lib/hvo-roof-web" "${FAKE_DIR}/install.log" \
    || fail_test "the data directory was not made root's: $(cat "${FAKE_DIR}/install.log")"
  grep -qxF -- "-d -m 0700 -o ${UI_USER} -g ${UI_GROUP} /var/lib/hvo-roof-web/keys" "${FAKE_DIR}/install.log" \
    || fail_test "the keys directory was not made for the web UI's user: $(cat "${FAKE_DIR}/install.log")"
  ! grep -qE -- '\| install .*/var/lib/hvo-roof-web' "${FAKE_DIR}/setpriv.log" \
    || fail_test "the keys directory was made as the web UI's user: $(cat "${FAKE_DIR}/setpriv.log")"
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" \
    "RoofWeb__DataProtectionPath=/var/lib/hvo-roof-web/keys"
}

# The default keys directory, the one the shipped container uses, is in directories only root can change.
test_the_default_keys_directory_is_made_by_the_supervisor_for_the_web_uis_user() {
  start_supervisor_with_the_default_data_dir
  wait_both_running || return
  expect_the_default_keys_directory_made_by_the_supervisor
}

# A setting that names the default changes nothing: the web UI's user could not make it there.
test_the_default_keys_directory_named_as_the_keys_directory_is_made_the_same_way() {
  start_supervisor_with_the_default_data_dir "RoofWeb__DataProtectionPath=/var/lib/hvo-roof-web/keys"
  wait_both_running || return
  expect_the_default_keys_directory_made_by_the_supervisor
}

test_the_default_keys_directory_named_as_the_data_directory_is_made_the_same_way() {
  start_supervisor_with_the_default_data_dir "HVO_SUPERVISOR_UI_DATA_DIR=/var/lib/hvo-roof-web"
  wait_both_running || return
  expect_the_default_keys_directory_made_by_the_supervisor
}

# However it is written: repeated slashes and a trailing slash name the same directory.
test_the_default_keys_directory_written_with_extra_slashes_is_made_the_same_way() {
  start_supervisor_with_the_default_data_dir "RoofWeb__DataProtectionPath=/var/lib//hvo-roof-web/keys/"
  wait_both_running || return
  expect_the_default_keys_directory_made_by_the_supervisor
}

test_the_default_data_directory_written_with_a_trailing_slash_is_made_the_same_way() {
  start_supervisor_with_the_default_data_dir "HVO_SUPERVISOR_UI_DATA_DIR=/var/lib/hvo-roof-web/"
  wait_both_running || return
  expect_the_default_keys_directory_made_by_the_supervisor
}

# Without a user of its own, the web UI runs as the supervisor does, so there is no one else to give the directories to.
test_the_default_keys_directory_without_a_web_ui_user_is_left_to_the_supervisors_user() {
  mkdir -p "${WORK}/install-bin"
  ln -sf "${TESTS_DIR}/fake-install" "${WORK}/install-bin/install"
  start_supervisor "HVO_SUPERVISOR_UI_DATA_DIR=" "PATH=${WORK}/install-bin:${PATH}"
  wait_both_running || return
  expect_equal "install calls" "$(grep -F /var/lib/hvo-roof-web "${FAKE_DIR}/install.log")" \
    "$(printf '%s\n' "-d -m 0755 /var/lib/hvo-roof-web" "-d -m 0700 /var/lib/hvo-roof-web/keys")"
  expect_equal "keys setting" "$(ui_env | grep '^RoofWeb__DataProtectionPath=')" \
    "RoofWeb__DataProtectionPath=/var/lib/hvo-roof-web/keys"
}

test_the_web_ui_runs_as_its_own_user_without_new_privileges() {
  start_supervisor_as_ui_user
  wait_both_running || return
  grep -F -- "--reuid=${UI_USER} --regid=${UI_GROUP} --init-groups --no-new-privs | env -i " "${FAKE_DIR}/setpriv.log" \
    | grep -q " ${WORK}/bin/ui\$" || fail_test "the web UI was not started with setpriv: $(cat "${FAKE_DIR}/setpriv.log")"
  grep -qxF "USER=${UI_USER}" <(ui_env) || fail_test "the web UI's environment does not name its user"
  expect_equal "control directory" "$(stat -c '%a %U %G' "${WORK}/run/control")" "700 ${UI_USER} ${UI_GROUP}"
  expect_equal "private directory" "$(stat -c '%a %G' "${WORK}/run/web")" "750 ${UI_GROUP}"
  expect_equal "keys directory" "$(stat -c '%a %U %G' "${WORK}/data/keys")" "700 ${UI_USER} ${UI_GROUP}"
  ! grep -q "can read" "${WORK}/supervisor.log" || fail_test "a warning without secrets: $(grep "can read" "${WORK}/supervisor.log")"
}

test_a_warning_when_the_web_uis_user_can_read_a_secret() {
  if (( EUID == 0 )); then
    return # root reads any file: private secrets cannot be made
  fi
  printf 'value' >"${WORK}/secrets/Alpha__Private"
  chmod 000 "${WORK}/secrets/Alpha__Private"
  start_supervisor_as_ui_user
  wait_both_running || return
  # The supervisor lists the files and the web UI's user tries each by name: it may be able to open a file in a
  # directory it cannot list (not reproducible without root, so the call itself is checked).
  grep -F -- "--reuid=${UI_USER} --regid=${UI_GROUP} --init-groups --no-new-privs | bash -c" "${FAKE_DIR}/setpriv.log" \
    | grep -qF " _ ${WORK}/secrets/Alpha__Private" \
    || fail_test "the secrets were not tried by name as the web UI's user: $(cat "${FAKE_DIR}/setpriv.log")"
  ! grep -q "can read" "${WORK}/supervisor.log" || fail_test "a warning for private secrets"

  kill -KILL -- "-${SUPERVISOR_PID}" 2>/dev/null
  wait "${SUPERVISOR_PID}" 2>/dev/null
  printf 'value' >"${WORK}/secrets/Bravo__Readable"
  start_supervisor_as_ui_user
  wait_until 10 "the supervisor started again" grep -q "Started the web UI" "${WORK}/supervisor.log" || return
  grep -qF "WARNING: the web UI's user ${UI_USER} can read ${WORK}/secrets/Bravo__Readable. The secrets are for the controller alone" \
    "${WORK}/supervisor.log" || fail_test "no warning for a readable secret: $(cat "${WORK}/supervisor.log")"
}

test_the_sample_state_file_is_the_supervisors_own_output() {
  # The web UI's tests and the deploy script's fake docker read supervisor-state.sample.json: it must be what the
  # supervisor writes, pids and times aside. It shows a controller that crashed once and was started again, and a
  # forced restart ignored just after.
  start_supervisor HVO_SUPERVISOR_CRASH_LIMIT=5 HVO_SUPERVISOR_CRASH_WINDOW_SECONDS=120 \
    HVO_SUPERVISOR_FORCE_RESTART_MIN_SECONDS=10
  wait_both_running || return
  echo 1 >"${FAKE_DIR}/controller.exit-now"
  wait_until 5 "the controller started again" state_is '.controller.state + " " + (.controller.starts | tostring)' \
    "running 2" || return
  touch "${WORK}/run/control/force-restart-controller"
  wait_until 3 "the request ignored" state_is .lastForcedRestart.outcome ignored || return
  diff -u <(normalized_state "${SAMPLE}") <(normalized_state "${WORK}/run/supervisor.json") >"${WORK}/sample.diff" \
    || fail_test "the sample is not the supervisor's output (update ${SAMPLE}):
$(cat "${WORK}/sample.diff")"
}

test_the_deploy_script_reads_the_supervisors_state_file() {
  local reader
  reader=$(sed -n '/^supervised_process() {$/,/^}$/p' "${DEPLOY_SCRIPT}")
  [[ -n "${reader}" ]] || { fail_test "supervised_process() is not in ${DEPLOY_SCRIPT}"; return; }
  eval "${reader}"
  # What the deploy script runs (dockerc exec <container> cat <file>), answered with the supervisor's own file.
  # shellcheck disable=SC2317 # called by supervised_process, which eval defines
  dockerc() {
    [[ "$1 $3 $4" == "exec cat /run/hvo-roof/supervisor.json" ]] && cat "${WORK}/run/supervisor.json"
  }
  start_supervisor
  wait_both_running || return
  expect_equal "controller" "$(supervised_process roof-controller controller)" "running 1"
  expect_equal "web UI" "$(supervised_process roof-controller ui)" "running 1"
  echo 1 >"${FAKE_DIR}/controller.exit-now"
  wait_until 3 "the controller restarting" state_is .controller.state restarting || return
  expect_equal "controller restarting" "$(supervised_process roof-controller controller)" "restarting 1"
  wait_until 3 "the controller running again" state_is .controller.state running || return
  expect_equal "controller started again" "$(supervised_process roof-controller controller)" "running 2"
  unset -f dockerc supervised_process
}

test_a_signal_during_a_backoff_stops_at_once() {
  start_supervisor HVO_SUPERVISOR_BACKOFF_MAX_SECONDS=30 HVO_SUPERVISOR_CRASH_LIMIT=10
  wait_both_running || return
  echo 1 >"${FAKE_DIR}/controller.exit-at-start"
  kill -KILL "$(pid_of controller)"
  # Crash 1 (1 s), crash 2 (2 s), then a 4 s backoff.
  wait_until 10 "the third backoff" grep -q "starting it again in 4s" "${WORK}/supervisor.log" || return
  stop_supervisor
  expect_equal "supervisor exit status" "${STOP_STATUS}" 0
  awk -v s="${STOP_SECONDS}" 'BEGIN { exit !(s < 1.5) }' || fail_test "stop took ${STOP_SECONDS}s"
  expect_equal "controller state" "$(state .controller.state)" stopped
}

test_invalid_settings_are_refused() {
  local setting
  for setting in HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=abc HVO_SUPERVISOR_CRASH_LIMIT=0 HVO_SUPERVISOR_TICK_SECONDS=1s; do
    env "${SUPERVISOR_ENV[@]}" "${setting}" "${SUPERVISOR}" >"${WORK}/supervisor.log" 2>&1
    local status=$?
    expect_equal "exit status for ${setting}" "${status}" 2
    grep -q "must be" "${WORK}/supervisor.log" || fail_test "no message for ${setting}"
  done
  [[ ! -e "${FAKE_DIR}/controller.pid" ]] || fail_test "the controller was started"
}

# run_healthcheck [NAME=value...]: runs the health check with the fake curl, setting STATUS and OUTPUT.
run_healthcheck() {
  mkdir -p "${WORK}/curl-bin"
  ln -sf "${TESTS_DIR}/fake-curl" "${WORK}/curl-bin/curl"
  OUTPUT=$(env PATH="${WORK}/curl-bin:${PATH}" "HVO_SUPERVISOR_RUN_DIR=${WORK}/run" "$@" "${HEALTHCHECK}" 2>&1)
  STATUS=$?
}

# A state file as the supervisor writes it (the sample, which test_the_sample_state_file_is_the_supervisors_own_output
# checks), with the controller in state $1 and the web UI in state $2.
write_supervisor_state() {
  mkdir -p "${WORK}/run"
  jq -c --arg controller "$1" --arg ui "$2" '.controller.state = $controller | .ui.state = $ui' "${SAMPLE}" \
    >"${WORK}/run/supervisor.json"
}

test_health_is_the_controllers_readiness_with_the_web_ui_alongside() {
  write_supervisor_state running running
  echo 200 >"${FAKE_DIR}/curl-8080.code"
  echo 200 >"${FAKE_DIR}/curl-8088.code"
  run_healthcheck
  expect_equal "status" "${STATUS}" 0
  expect_equal "output" "${OUTPUT}" \
    "controller: ready; web UI: live; supervisor: controller running, web UI running"
  grep -qF "http://localhost:8080/health/ready" "${FAKE_DIR}/curl.log" || fail_test "the controller's readiness was not asked"
  grep -qF "http://localhost:8088/health/live" "${FAKE_DIR}/curl.log" || fail_test "the web UI's liveness was not asked"
}

test_health_fails_when_the_controller_is_not_ready_even_with_the_web_ui_live() {
  write_supervisor_state crash-loop running
  echo 200 >"${FAKE_DIR}/curl-8088.code"
  run_healthcheck
  expect_equal "status" "${STATUS}" 1
  expect_equal "output" "${OUTPUT}" \
    "controller: NOT READY (HTTP 000); web UI: live; supervisor: controller crash-loop, web UI running"
  echo 503 >"${FAKE_DIR}/curl-8080.code"
  run_healthcheck
  expect_equal "status (503)" "${STATUS}" 1
  [[ "${OUTPUT}" == "controller: NOT READY (HTTP 503);"* ]] || fail_test "output: ${OUTPUT}"
}

test_health_stays_healthy_when_only_the_web_ui_is_down() {
  write_supervisor_state running restarting
  echo 200 >"${FAKE_DIR}/curl-8080.code"
  run_healthcheck
  expect_equal "status" "${STATUS}" 0
  expect_equal "output" "${OUTPUT}" \
    "controller: ready; web UI: DOWN (HTTP 000); supervisor: controller running, web UI restarting"
}

test_health_checks_the_web_ui_on_its_own_https_port() {
  echo 200 >"${FAKE_DIR}/curl-8080.code"
  echo 200 >"${FAKE_DIR}/curl-9443.code"
  run_healthcheck "RoofWeb__Urls=https://+:9443;http://localhost:9080"
  expect_equal "status" "${STATUS}" 0
  expect_equal "output" "${OUTPUT}" "controller: ready; web UI: live; supervisor: controller unknown, web UI unknown"
  grep -F "https://localhost:9443/health/live" "${FAKE_DIR}/curl.log" | grep -q -- "-sk" \
    || fail_test "the web UI's HTTPS liveness was not asked on loopback without certificate checks"
}

test_health_asks_both_at_once() {
  # Two slow answers (2 s each) take 2 s, not 4: the check stays within the Compose files' 5 s timeout even when both
  # servers hang up to their limits (4 s and 3 s).
  write_supervisor_state running running
  echo 200 >"${FAKE_DIR}/curl-8080.code"
  echo 200 >"${FAKE_DIR}/curl-8088.code"
  echo 2 >"${FAKE_DIR}/curl-8080.delay"
  echo 2 >"${FAKE_DIR}/curl-8088.delay"
  local started=${EPOCHREALTIME} seconds
  run_healthcheck
  seconds=$(awk -v a="${started}" -v b="${EPOCHREALTIME}" 'BEGIN { printf "%.3f", b - a }')
  expect_equal "status" "${STATUS}" 0
  expect_equal "output" "${OUTPUT}" "controller: ready; web UI: live; supervisor: controller running, web UI running"
  awk -v s="${seconds}" 'BEGIN { exit !(s >= 2 && s < 3.5) }' || fail_test "the check took ${seconds}s, expected 2-3.5s"
}

# ---------------------------------------------------------------------------------------------------------------------

for tool in jq setsid; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "supervisor-tests: ${tool} is required" >&2; exit 2; }
done
if (( BASH_VERSINFO[0] < 5 )); then
  echo "supervisor-tests: bash 5 or later is required (the supervisor uses EPOCHREALTIME)" >&2
  exit 2
fi

tests=()
if (( $# > 0 )); then
  tests=("$@")
else
  while IFS= read -r test; do
    tests+=("${test}")
  done < <(declare -F | awk '{print $3}' | grep '^test_')
fi

echo "roof-supervisor.sh tests"
for test in "${tests[@]}"; do
  run_test "${test}"
done

echo
echo "${PASSED} passed, ${FAILED} failed"
if (( FAILED > 0 )); then
  printf '  %s\n' "${FAILURES[@]}"
  exit 1
fi
