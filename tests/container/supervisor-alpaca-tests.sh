#!/usr/bin/env bash
# Sourced by supervisor-tests.sh: the optional third process cannot delay the relay-off or container stop.

wait_all_running() {
  wait_both_running && wait_until 5 "Alpaca running" state_is .alpaca.state running &&
    wait_until 5 "Alpaca signal handlers ready" stand_in_ready alpaca
}

alpaca_env() { tr '\0' '\n' <"${FAKE_DIR}/alpaca.env"; }

test_alpaca_is_off_unless_explicitly_enabled() {
  start_supervisor
  wait_both_running || return
  expect_equal "Alpaca state" "$(state .alpaca.state)" disabled
  expect_equal "Alpaca starts" "$(event_count alpaca start)" 0
  [[ ! -d "${WORK}/run/alpaca" ]] || fail_test "disabled Alpaca got a private directory"
}

test_alpaca_isolated_environment_and_private_key_are_prepared_each_start() {
  printf 'fake-alpaca-key-for-tests' >"${WORK}/secrets/alpaca-key"
  start_supervisor Alpaca__Enabled=true "Alpaca__ControllerApiKeyFile=${WORK}/secrets/alpaca-key" \
    Alpaca__Location=TestAlpaca RoofControllerSecurity__NeverPass=controller-secret \
    RoofWeb__NeverPass=web-secret ArbitrarySecret=other-secret
  wait_all_running || return
  expect_equal "own setting" "$(alpaca_env | grep '^Alpaca__Location=')" Alpaca__Location=TestAlpaca
  expect_equal "copied key path" "$(alpaca_env | grep '^Alpaca__ControllerApiKeyFile=')" \
    "Alpaca__ControllerApiKeyFile=${WORK}/run/alpaca/controller-key"
  cmp -s "${WORK}/secrets/alpaca-key" "${WORK}/run/alpaca/controller-key" || fail_test "key copy differs"
  expect_equal "private key mode" "$(stat -c %a "${WORK}/run/alpaca/controller-key")" 400
  expect_equal "environment mode" "$(stat -c %a "${WORK}/run/alpaca-settings")" 600
  expect_equal "data directory mode" "$(stat -c %a "${WORK}/alpaca-data")" 755
  expect_equal "data setting" "$(alpaca_env | grep '^Alpaca__DataPath=')" "Alpaca__DataPath=${WORK}/alpaca-data"
  alpaca_env | grep -Eq '^(RoofWeb__|RoofController|ArbitrarySecret|HVO_|FAKE_DIR=)' && fail_test "unrelated environment leaked"
  ui_env | grep -q '^Alpaca__' && fail_test "Alpaca settings leaked to web UI"
  printf 'rotated-fake-alpaca-key' >"${WORK}/secrets/alpaca-key"
  kill -KILL "$(pid_of alpaca)"
  wait_until 5 "Alpaca restarted" started alpaca 2 || return
  cmp -s "${WORK}/secrets/alpaca-key" "${WORK}/run/alpaca/controller-key" || fail_test "rotated key not copied"
  expect_equal "controller unaffected" "$(state .controller.starts)" 1
  expect_equal "web UI unaffected" "$(state .ui.starts)" 1
}

test_alpaca_runs_as_its_own_user() {
  mkdir -p "${WORK}/setpriv-bin"
  ln -s "${TESTS_DIR}/fake-setpriv" "${WORK}/setpriv-bin/setpriv"
  local user group
  user=$(id -un); group=$(id -gn)
  start_supervisor Alpaca__Enabled=true "HVO_SUPERVISOR_ALPACA_USER=${user}" "PATH=${WORK}/setpriv-bin:${PATH}"
  wait_all_running || return
  grep -qF -- "--reuid=${user} --regid=${group} --init-groups --no-new-privs | env -i" "${FAKE_DIR}/setpriv.log" \
    || fail_test "Alpaca did not drop privileges with an empty environment"
  expect_equal "Alpaca HOME" "$(alpaca_env | grep '^HOME=')" "HOME=$(getent passwd "${user}" | cut -d: -f6)"
}

test_a_crashing_alpaca_never_restarts_controller_or_relays() {
  echo 1 >"${FAKE_DIR}/alpaca.exit-at-start"
  start_supervisor Alpaca__Enabled=true HVO_SUPERVISOR_CRASH_LIMIT=2
  wait_until 7 "Alpaca restarted beyond the controller crash limit" started alpaca 3 || return
  expect_equal "controller starts" "$(state .controller.starts)" 1
  expect_equal "web UI starts" "$(state .ui.starts)" 1
  expect_equal "relay off runs" "$(event_count relays-off run)" 1
  expect_relays_never_turned_off_under_a_running_controller
}

test_a_hung_alpaca_does_not_delay_the_stop_or_final_relay_off() {
  touch "${FAKE_DIR}/alpaca.ignore-term" "${FAKE_DIR}/controller.ignore-term" "${FAKE_DIR}/ui.ignore-term"
  start_supervisor Alpaca__Enabled=true HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=2 HVO_SUPERVISOR_UI_STOP_SECONDS=1 \
    HVO_SUPERVISOR_ALPACA_STOP_SECONDS=1 HVO_SUPERVISOR_RELAYS_OFF_SECONDS=2
  wait_all_running || return
  local signalled=${EPOCHREALTIME}
  kill -TERM "${SUPERVISOR_PID}"
  expect_the_stop_within "${signalled}" 4 8
  expect_the_last_relay_off_to_begin_within "${signalled}" 3
  expect_the_controller_told_at_once "${signalled}"
  expect_equal "Alpaca killed" "$(state .alpaca.lastExitCode)" 137
  expect_equal "final Alpaca state" "$(state .alpaca.state)" stopped
  expect_relays_never_turned_off_under_a_running_controller
}

test_an_alpaca_preparation_stall_never_delays_controller_exit_or_stop() {
  mkdir -p "${WORK}/install-bin"
  ln -s "${TESTS_DIR}/fake-install" "${WORK}/install-bin/install"
  echo 30 >"${FAKE_DIR}/install.delay"
  start_supervisor Alpaca__Enabled=true HVO_SUPERVISOR_ALPACA_PREPARE_SECONDS=60 \
    "PATH=${WORK}/install-bin:${PATH}" HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=2 HVO_SUPERVISOR_RELAYS_OFF_SECONDS=2
  wait_until 5 "Alpaca preparation stalled" test -s "${FAKE_DIR}/install.pid" || return
  local killed=${EPOCHREALTIME}
  kill -KILL "$(pid_of controller)"
  wait_until 2 "relay-off while Alpaca preparation stalls" relay_off_runs_at_least 2 || return
  local began
  began=$(event_times relays-off run | sed -n 2p)
  awk -v k="${killed}" -v r="${began}" 'BEGIN { exit !(r-k < 1) }' || fail_test "stalled preparation delayed relays"
  local signalled=${EPOCHREALTIME}
  kill -TERM "${SUPERVISOR_PID}"
  expect_the_stop_within "${signalled}" 4 8
  expect_equal "Alpaca never started" "$(event_count alpaca start)" 0
  wait_until 1 "stalled preparation ended" process_has_ended "$(<"${FAKE_DIR}/install.pid")"
}

test_an_alpaca_preparation_timeout_retries_without_starting_it() {
  mkdir -p "${WORK}/install-bin"
  ln -s "${TESTS_DIR}/fake-install" "${WORK}/install-bin/install"
  echo 30 >"${FAKE_DIR}/install.delay"
  start_supervisor Alpaca__Enabled=true HVO_SUPERVISOR_ALPACA_PREPARE_SECONDS=1 "PATH=${WORK}/install-bin:${PATH}" \
    "RoofWeb__DataProtectionPath=${WORK}/web-data"
  wait_until 5 "Alpaca preparation timeout logged" grep -q "Alpaca.*preparation.*did not finish within 1s" "${WORK}/supervisor.log" || return
  expect_equal "Alpaca starts after timeout" "$(event_count alpaca start)" 0
  rm -f "${FAKE_DIR}/install.delay"
  wait_all_running || return
  expect_equal "controller starts" "$(state .controller.starts)" 1
}

test_alpaca_tasks_are_freed_before_a_stop_relay_off_at_the_process_limit() {
  touch "${FAKE_DIR}/alpaca.ignore-term" "${FAKE_DIR}/controller.ignore-term"
  start_supervisor_with_pids Alpaca__Enabled=true HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=2 \
    HVO_SUPERVISOR_ALPACA_STOP_SECONDS=1 HVO_SUPERVISOR_RELAYS_OFF_SECONDS=2 || return
  wait_all_running || return
  exhaust
  local signalled=${EPOCHREALTIME}
  kill -TERM "${SUPERVISOR_PID}"
  recover_when_gone "$(pid_of alpaca)" "$(pid_of controller)" "$(pid_of ui)"
  expect_the_stop_within "${signalled}" 4 8
  expect_the_last_relay_off_to_begin_within "${signalled}" 3
  expect_no_fork_at_the_limit
}

test_alpaca_is_killed_to_make_room_for_relay_off_after_controller_exit() {
  start_supervisor_with_pids Alpaca__Enabled=true || return
  wait_all_running || return
  local alpaca ui
  alpaca=$(pid_of alpaca); ui=$(pid_of ui)
  exhaust
  recover_when_gone "${alpaca}" "${ui}"
  kill -KILL "$(pid_of controller)"
  wait_until 4 "relay-off after freeing Alpaca tasks" relay_off_runs_at_least 2 || return
  process_has_ended "${alpaca}" || fail_test "Alpaca still holds tasks"
  expect_no_fork_at_the_limit
  wait_until 7 "controller restarted after verified off" started controller 2
  expect_relays_never_turned_off_under_a_running_controller
}


test_alpaca_deployment_preflight_runs_read_only_after_controller_validation() {
  cat >"${WORK}/bin/preflight-controller" <<'APP'
#!/usr/bin/env bash
printf 'controller %s\n' "$*" >>"${FAKE_DIR}/preflight-events"
exit "${FAKE_CONTROLLER_EXIT:-0}"
APP
  cat >"${WORK}/bin/preflight-alpaca" <<'APP'
#!/usr/bin/env bash
printf 'alpaca %s\n' "$*" >>"${Alpaca__FakeDir}/preflight-events"
env -0 >"${Alpaca__FakeDir}/preflight-env"
exit 0
APP
  chmod +x "${WORK}/bin/preflight-controller" "${WORK}/bin/preflight-alpaca"
  local code=0
  env "${SUPERVISOR_ENV[@]}" Alpaca__Enabled=true \
    "HVO_SUPERVISOR_CONTROLLER_EXEC=${WORK}/bin/preflight-controller" \
    "HVO_SUPERVISOR_ALPACA_EXEC=${WORK}/bin/preflight-alpaca" \
    HVO_SUPERVISOR_ALPACA_USER=no-such-user RoofControllerSecurity__NeverPass=not-for-alpaca \
    "${SUPERVISOR_COMMAND:-${SUPERVISOR}}" --validate-deployment >"${WORK}/supervisor.log" 2>&1 || code=$?
  expect_equal "preflight result" "${code}" 0
  expect_equal "preflight order" "$(cat "${FAKE_DIR}/preflight-events")" $'controller --validate-deployment\nalpaca --validate-settings'
  [[ ! -d "${WORK}/run" && ! -d "${WORK}/alpaca-data" ]] || fail_test "preflight wrote supervised runtime/data directories"
  tr '\0' '\n' <"${FAKE_DIR}/preflight-env" | grep -Eq '^(Roof|HVO_|FAKE_DIR=)' && fail_test "preflight inherited controller settings"
  : >"${FAKE_DIR}/preflight-events"
  code=0
  env "${SUPERVISOR_ENV[@]}" Alpaca__Enabled=true FAKE_CONTROLLER_EXIT=2 \
    "HVO_SUPERVISOR_CONTROLLER_EXEC=${WORK}/bin/preflight-controller" \
    "HVO_SUPERVISOR_ALPACA_EXEC=${WORK}/bin/preflight-alpaca" \
    "${SUPERVISOR_COMMAND:-${SUPERVISOR}}" --validate-deployment >"${WORK}/supervisor.log" 2>&1 || code=$?
  expect_equal "controller validation failure" "${code}" 2
  expect_equal "Alpaca skipped after failed controller check" "$(cat "${FAKE_DIR}/preflight-events")" 'controller --validate-deployment'
}

test_a_stalled_alpaca_preparation_at_the_process_limit_is_killed_for_relay_off() {
  mkdir -p "${WORK}/install-bin"
  ln -s "${TESTS_DIR}/fake-install" "${WORK}/install-bin/install"
  echo 30 >"${FAKE_DIR}/install.delay"
  start_supervisor_with_pids Alpaca__Enabled=true HVO_SUPERVISOR_ALPACA_PREPARE_SECONDS=60 \
    "PATH=${WORK}/install-bin:${PATH}" "RoofWeb__DataProtectionPath=${WORK}/web-data" || return
  wait_both_running || return
  wait_until 5 "Alpaca preparation stalled" test -s "${FAKE_DIR}/install.pid" || return
  local installer ui killed
  installer=$(<"${FAKE_DIR}/install.pid"); ui=$(pid_of ui)
  exhaust
  (
    until process_has_ended "${installer}" && process_has_ended "${ui}"; do sleep 0.05; done
    recover
  ) &
  # shellcheck disable=SC2034 # supervisor-tests.sh teardown joins this fixture recovery
  RECOVERY_PID=$!
  killed=${EPOCHREALTIME}
  kill -KILL "$(pid_of controller)"
  wait_until 3 "relay-off after abandoned Alpaca preparation at limit" relay_off_runs_at_least 2 || return
  local began
  began=$(event_times relays-off run | sed -n 2p)
  awk -v k="${killed}" -v r="${began}" 'BEGIN { exit !(r-k < 1) }' || fail_test "stalled preparation at limit delayed relay-off"
  process_has_ended "${installer}" || fail_test "Alpaca preparation still holds tasks"
  expect_equal "Alpaca never started" "$(event_count alpaca start)" 0
  expect_no_fork_at_the_limit
  expect_relays_never_turned_off_under_a_running_controller
}
