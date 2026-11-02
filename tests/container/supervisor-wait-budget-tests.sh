#!/usr/bin/env bash
# Sourced by supervisor-tests.sh. Run the real cleanup functions with a virtual clock: SIGKILL cannot release these
# stand-in kernel-stuck tasks, and each reap consumes precisely the allowance it receives. No scheduler or hardware.
# shellcheck disable=SC2034,SC2317,SC2329 # locals/stubs are consumed by production functions loaded with eval

supervisor_wait_budget_case() (
  local scenario=$1 function definition status=0
  for function in room_for_relays_off shutdown note_stop seconds_until begin_stop finish_stop is_running \
      ui_preparation_runs stop_ui_preparation alpaca_preparation_runs stop_alpaca_preparation describe_exit; do
    definition=$(sed -n "/^${function}() {$/,/^}$/p" "${SUPERVISOR}")
    [[ -n "${definition}" ]] || { fail_test "missing supervisor function: ${function}"; exit 1; }
    eval "${definition}" || exit 1
  done

  local clock=1000000 initial=1000000 NOW_US=1000000 KILL_WAIT_US=1000000 NAP_SLICE_US=100000
  local UI_PREPARE_PID=201 ALPACA_PREPARE_PID=202 ALPACA_ENABLED=1 RELAYS_OFF_PID=""
  local CONTROLLER_STOP_SECONDS=22 UI_STOP_SECONDS=2 ALPACA_STOP_SECONDS=2 RELAYS_OFF_SECONDS=5 STOP_AFTER_SECONDS=5
  local STOP_REQUESTED=0 STOP_AT_US=0 STOP_BY_US=0 SHUTDOWN_DONE=0 REAPED_CODE=137
  local BACKOFF_SECONDS=1 ISO_NOW=T EXIT_TEXT="" SECONDS_LEFT=0 TASKS_TEXT="512 of 512 tasks"
  local SUPERVISOR_STATE=running
  local -A PID=([controller]="" [ui]=101 [alpaca]=102)
  local -A STATE=([controller]=stopped [ui]=running [alpaca]=running)
  local -A LAST_EXIT_CODE=() LAST_EXIT_REASON=() LAST_EXIT_AT=() START_DUE=() STOP_DEADLINE=() STOP_WAIT=()
  local -a signals=() reaps=() messages=()

  stamp() { NOW_US=${clock}; }
  iso_now() { ISO_NOW=T; }
  log() { messages+=("$*"); }
  write_state() { :; }
  record_crash() { :; }
  backoff_seconds() { BACKOFF_SECONDS=1; }
  nap() { clock=$(( clock + $1 )); }
  tasks_free() { return 1; }
  # No real signal is ever sent. These synthetic pids remain present after SIGKILL, like an uninterruptible kernel wait.
  kill() {
    [[ "$1" == -0 ]] && return 0
    signals+=("${clock}:$*")
    return 0
  }
  reap_within() {
    reaps+=("$1:$2:${clock}")
    (( clock >= $2 )) || clock=$2
    return 1
  }
  de_energize_relays() {
    [[ -z "${PID[controller]}" ]] || fail_test "relay-off ran while the controller was still admitted"
  }

  case "${scenario}" in
    room)
      room_for_relays_off || status=$?
      expect_equal "no task room" "${status}" 1
      expect_equal "shared cleanup and room allowance" "${clock}" "$(( initial + KILL_WAIT_US ))"
      expect_equal "four task holders reaped" "${#reaps[@]}" 4
      local reap deadline
      for reap in "${reaps[@]}"; do
        deadline=${reap#*:}; deadline=${deadline%%:*}
        expect_equal "same deadline for ${reap%%:*}" "${deadline}" "$(( initial + KILL_WAIT_US ))"
      done
      local pid signal found
      for pid in 101 102 201 202; do
        found=0
        for signal in "${signals[@]}"; do
          [[ "${signal}" == "${initial}:-KILL ${pid}" || "${signal}" == "${initial}:-KILL -- -${pid}" ]] && found=1
        done
        expect_equal "pid ${pid} killed before waiting" "${found}" 1
      done
      ;;
    shutdown)
      PID[controller]=100
      STATE[controller]=running
      shutdown
      local name pid signal found
      for name in controller ui alpaca; do
        case "${name}" in controller) pid=100 ;; ui) pid=101 ;; alpaca) pid=102 ;; esac
        found=0
        for signal in "${signals[@]}"; do [[ "${signal}" == "${initial}:-TERM ${pid}" ]] && found=1; done
        expect_equal "${name} told before preparation waits" "${found}" 1
      done
      expect_equal "controller keeps full shutdown allowance" "${STOP_WAIT[controller]}" 22
      # Each tuple also records its entry time, so compare the deadline separately below.
      local first=${reaps[0]#*:} second=${reaps[1]#*:}
      first=${first%%:*}; second=${second%%:*}
      expect_equal "both preparation reaps end within one second" "${first} ${second}" \
        "$(( initial + KILL_WAIT_US )) $(( initial + KILL_WAIT_US ))"
      (( clock - initial <= 27000000 )) || fail_test "shutdown exceeded 27s on the virtual clock"
      expect_equal "shutdown finished" "${SHUTDOWN_DONE}" 1
      ;;
    *) fail_test "unknown wait-budget case: ${scenario}" ;;
  esac
  [[ "${CURRENT_FAILED}" == false ]]
)

test_stuck_task_holders_are_killed_together_and_share_one_second_for_relay_off_room() {
  supervisor_wait_budget_case room || fail_test "shared relay-off task cleanup allowance"
}

test_stuck_preparations_cannot_delay_application_stop_signals_or_share_more_than_one_second() {
  supervisor_wait_budget_case shutdown || fail_test "prompt application stop and shared preparation allowance"
}

test_startup_and_shutdown_logs_include_the_enabled_alpaca_stop_allowance() {
  start_supervisor Alpaca__Enabled=true HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=1 HVO_SUPERVISOR_UI_STOP_SECONDS=1 \
    HVO_SUPERVISOR_ALPACA_STOP_SECONDS=3 HVO_SUPERVISOR_RELAYS_OFF_SECONDS=1
  wait_all_running || return
  grep -qF 'a stop ends within 4s' "${WORK}/supervisor.log" || fail_test "startup omitted Alpaca's longer stop allowance"
  grep -qF '3s for the Alpaca server' "${WORK}/supervisor.log" || fail_test "startup omitted Alpaca's stop wait"
  stop_supervisor
  grep -qF 'Stopping: the controller, web UI and Alpaca server, then the relays off, all within 4s' \
    "${WORK}/supervisor.log" || fail_test "shutdown omitted Alpaca or the configured bound"
}
