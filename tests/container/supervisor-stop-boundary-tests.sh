#!/usr/bin/env bash
# Sourced by supervisor-tests.sh. Exercise the real launch/exec functions with builtin fake dependencies and a short
# application that only records a start. DEBUG/functrace fixes the stop at command boundaries, without timing races.

# One isolated shell per case: fake dependencies and inherited DEBUG traps cannot leak into the other supervisor tests.
# shellcheck disable=SC2034 # locals consumed by the production functions loaded with eval
stop_boundary_case() (
  local role=$1 boundary=$2 function definition status child_status=0
  local parent_function=start_controller child_function=exec_controller
  if [[ "${role}" == ui ]]; then parent_function=launch_ui; child_function=exec_ui; fi
  for function in "${parent_function}" "${child_function}"; do
    definition=$(sed -n "/^${function}() {$/,/^}$/p" "${SUPERVISOR}")
    [[ -n "${definition}" ]] || { echo "missing supervisor function: ${function}"; exit 1; }
    eval "${definition}" || exit 1
  done

  # These are the functions' dependencies, never the supervisor's top-level startup, HAT access or UI preparation.
  # shellcheck disable=SC2317 # called by the production functions loaded with eval above
  tasks_free() { return 0; }
  # shellcheck disable=SC2317
  raise_oom_score_adj() { return 0; }
  # shellcheck disable=SC2317
  stamp() { NOW_US=1; }
  # shellcheck disable=SC2317
  log() { :; }
  # shellcheck disable=SC2317
  write_state() { :; }

  # The real exec functions must reach this executable on the no-stop path, including exec_ui's env -i invocation.
  cat >"${WORK}/boundary-app" <<'APP'
#!/usr/bin/env bash
printf 'application %s\n' "$1" >>"$2"
APP
  chmod +x "${WORK}/boundary-app"
  local events="${WORK}/boundary-events" parent_pid=${BASHPID}
  : >"${events}"
  local STOP_REQUESTED=0 RELAYS_STATE=off NOW_US=0 UI_USER="" UI_PREPARE_PID=""
  local APP_DIR="${WORK}/app" UI_SETTINGS_FILE="${WORK}/boundary-settings"
  local CONTROLLER_OOM_SCORE_RAISE=100 UI_OOM_SCORE_RAISE=500
  local -a CONTROLLER_EXEC=("${WORK}/boundary-app" "${role}" "${events}")
  local -a UI_EXEC=("${CONTROLLER_EXEC[@]}")
  local -A PID=([controller]="" [ui]="") STARTS=([controller]=0 [ui]=0)
  local -A WAITING_FOR_ROOM=() STARTED_AT=() STATE=()
  : >"${UI_SETTINGS_FILE}"
  if [[ "${role}" == ui ]]; then
    # launch_ui waits for its preparation; an actual successful child makes that wait real and deterministic.
    : &
    UI_PREPARE_PID=$!
  fi

  # The parent guard case stops in the last preparation call, after all earlier stop checks. Its absence must be
  # detected by the fork/start counters even though the child's intact check would still suppress the application.
  # The prefork case stops AFTER the parent's final guard; only the child's inherited flag can suppress execution.
  # The child-entry case changes only the forked shell's flag, so it independently exercises the child guard too.
  # shellcheck disable=SC2317 # called by the inherited DEBUG trap
  stop_boundary_debug() {
    local command=$1 caller=$2
    if [[ "${BASHPID}" == "${parent_pid}" ]]; then
      if [[ "${boundary}" == parent-check && "${caller}" == tasks_free && "${command}" == 'return 0' ]] ||
        [[ "${boundary}" == prefork && "${caller}" == "${parent_function}" && "${command}" == "${child_function}" ]]; then
        STOP_REQUESTED=1
        printf 'stop %s\n' "${boundary}" >>"${events}"
      fi
    elif [[ "${caller}" == "${child_function}" && "${command}" == "${child_function}" ]]; then
      printf 'child entry\n' >>"${events}"
      if [[ "${boundary}" == child-entry ]]; then
        STOP_REQUESTED=1
        printf 'stop %s\n' "${boundary}" >>"${events}"
      fi
    fi
    return 0
  }

  set -T
  trap 'stop_boundary_debug "${BASH_COMMAND}" "${FUNCNAME[0]:-}"' DEBUG
  "${parent_function}"
  status=$?
  trap - DEBUG
  set +T
  # Joining the launched shell is the only synchronization needed: it has either exited at the guard or recorded its
  # application start. A nonzero child status is a harness/setup failure, not proof of start suppression.
  if [[ -n "${PID[${role}]}" ]]; then wait "${PID[${role}]}"; child_status=$?; fi

  local expected_status=0 expected_starts=1 expected_entries=1 expected_apps=0 expected_stops=1 expected_flag=1
  case ${boundary} in
    none) expected_apps=1; expected_stops=0; expected_flag=0 ;;
    parent-check) expected_status=1; expected_starts=0; expected_entries=0 ;;
    prefork) : ;;
    child-entry) expected_flag=0 ;;
    *) echo "unknown stop boundary: ${boundary}"; exit 1 ;;
  esac
  expect_equal "${role}/${boundary}: launch status" "${status}" "${expected_status}"
  expect_equal "${role}/${boundary}: child status (setup must succeed)" "${child_status}" 0
  expect_equal "${role}/${boundary}: parent stop flag" "${STOP_REQUESTED}" "${expected_flag}"
  expect_equal "${role}/${boundary}: fork count" "${STARTS[${role}]}" "${expected_starts}"
  if (( expected_starts == 0 )); then
    expect_equal "${role}/${boundary}: no child pid" "${PID[${role}]}" ""
  else
    [[ -n "${PID[${role}]}" ]] || fail_test "${role}/${boundary}: missing child pid"
  fi
  expect_equal "${role}/${boundary}: child entry count" "$(grep -c '^child entry$' "${events}")" "${expected_entries}"
  expect_equal "${role}/${boundary}: injection count" "$(grep -c '^stop ' "${events}")" "${expected_stops}"
  expect_equal "${role}/${boundary}: application start count" "$(grep -c "^application ${role}$" "${events}")" "${expected_apps}"
  [[ "${CURRENT_FAILED}" == false ]]
)

test_controller_starts_without_a_stop_at_launch_boundaries() {
  stop_boundary_case controller none || fail_test "controller no-stop launch boundary case"
}

test_controller_parent_stop_check_prevents_the_fork() {
  stop_boundary_case controller parent-check || fail_test "controller parent stop guard"
}

test_controller_child_honours_a_stop_between_parent_check_and_fork() {
  stop_boundary_case controller prefork || fail_test "controller inherited stop guard"
}

test_controller_child_honours_a_stop_at_child_entry() {
  stop_boundary_case controller child-entry || fail_test "controller child-entry stop guard"
}

test_ui_starts_without_a_stop_at_launch_boundaries() {
  stop_boundary_case ui none || fail_test "UI no-stop launch boundary case"
}

test_ui_parent_stop_check_prevents_the_fork() {
  stop_boundary_case ui parent-check || fail_test "UI parent stop guard"
}

test_ui_child_honours_a_stop_between_parent_check_and_fork() {
  stop_boundary_case ui prefork || fail_test "UI inherited stop guard"
}

test_ui_child_honours_a_stop_at_child_entry() {
  stop_boundary_case ui child-entry || fail_test "UI child-entry stop guard"
}
