#!/usr/bin/env bash
# Tests for src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh, the container's supervisor of the controller and
# the web UI, and for healthcheck.sh, the container's health check. Both processes are stand-ins (fake-process.sh), and so
# is curl (fake-curl), so no Docker, .NET or hardware is needed; the timings are shortened. The container scenarios
# (tests/emulator/deploy-scenarios.sh, group supervisor) run the real image.
# Requires bash 5, jq and setsid.
# Run: tests/container/supervisor-tests.sh [test-name ...]
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SUPERVISOR="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh"
HEALTHCHECK="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/container/healthcheck.sh"

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

# Both stand-ins started, and the state file says so.
wait_both_running() {
  wait_until 10 "both processes running" state_is '.controller.state + " " + .ui.state' "running running" &&
    wait_until 5 "both stand-ins started" test -s "${FAKE_DIR}/ui.pid" -a -s "${FAKE_DIR}/controller.pid"
}

stop_supervisor() {
  kill -TERM "${SUPERVISOR_PID}"
  local started=${EPOCHREALTIME}
  wait "${SUPERVISOR_PID}"
  STOP_STATUS=$?
  STOP_SECONDS=$(awk -v a="${started}" -v b="${EPOCHREALTIME}" 'BEGIN { printf "%.1f", b - a }')
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
  # 2 s for the controller, then the web UI (which stops at once): within the two stop times, which the container's
  # stop timeout must exceed.
  awk -v s="${STOP_SECONDS}" 'BEGIN { exit !(s >= 1.9 && s < 3.5) }' || fail_test "stop took ${STOP_SECONDS}s, expected 2-3.5s"
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
  grep -q "It is left stopped and the container reports unhealthy" "${WORK}/supervisor.log" \
    || fail_test "the crash loop is not logged"
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
}

test_a_request_left_from_before_the_start_is_discarded() {
  mkdir -p "${WORK}/run/control"
  touch "${WORK}/run/control/force-restart-controller"
  start_supervisor
  wait_both_running || return
  sleep 0.5
  expect_equal "controller starts" "$(state .controller.starts)" 1
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

# A state file as the supervisor writes it, with the controller in state $1 and the web UI in state $2.
write_supervisor_state() {
  mkdir -p "${WORK}/run"
  printf '{"supervisor":"running","updatedAt":"2026-01-01T00:00:00Z","crashLimit":5,"crashWindowSeconds":120,"controller":{"state":"%s","pid":null,"starts":5,"recentCrashes":5,"lastExitCode":1,"lastExitReason":"crashed (exit code 1)","lastExitAt":"2026-01-01T00:00:00Z"},"ui":{"state":"%s","pid":12,"starts":1,"recentCrashes":0,"lastExitCode":null,"lastExitReason":null,"lastExitAt":null}}\n' \
    "$1" "$2" >"${WORK}/run/supervisor.json"
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

# ---------------------------------------------------------------------------------------------------------------------

for tool in jq setsid; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "supervisor-tests: ${tool} is required" >&2; exit 2; }
done
if (( BASH_VERSINFO[0] < 5 )); then
  echo "supervisor-tests: bash 5 or later is required (the supervisor uses EPOCHSECONDS)" >&2
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
