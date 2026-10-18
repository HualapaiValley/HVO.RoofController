#!/usr/bin/env bash
# Sourced by supervisor-tests.sh: fake unified cgroups, no Docker or hardware. The launcher records its own pid before
# exec, so cgroup.procs proves membership just as it does in the container (not a test-only bypass in the supervisor).

fake_cgroup() {
  mkdir -p "${WORK}/proc" "${WORK}/cgroup"
  printf '0::/\n' >"${WORK}/proc/cgroup"
  printf '29 23 0:26 / %s rw - cgroup2 cgroup rw\n' "${WORK}/cgroup" >"${WORK}/proc/mountinfo"
  printf '100000 100000\n' >"${WORK}/cgroup/cpu.max"
  printf '2147483648\n' >"${WORK}/cgroup/memory.max"
  printf '0\n' >"${WORK}/cgroup/memory.swap.max"
  printf '512\n' >"${WORK}/cgroup/pids.max"
  printf '1\n' >"${WORK}/cgroup/pids.current"
  cat >"${WORK}/cgroup-launcher" <<'LAUNCH'
#!/usr/bin/env bash
printf '%s\n' "$$" >"${FAKE_CGROUP}/cgroup.procs"
exec "${FAKE_SUPERVISOR}"
LAUNCH
  chmod +x "${WORK}/cgroup-launcher"
  SUPERVISOR_ENV+=("HVO_SUPERVISOR_PROC_DIR=${WORK}/proc" "FAKE_CGROUP=${WORK}/cgroup" "FAKE_SUPERVISOR=${SUPERVISOR}")
  CGROUP_EXPECTATIONS=(HVO_ROOF_CPUS=1 HVO_ROOF_MEMORY=2g HVO_ROOF_PIDS_LIMIT=512)
}

start_cgroup_supervisor() {
  SUPERVISOR_COMMAND="${WORK}/cgroup-launcher" start_supervisor "${CGROUP_EXPECTATIONS[@]}" "$@"
}

expect_limit_refusal() {
  wait_supervisor_exit 8
  expect_equal "refusal exit" "${EXIT_STATUS}" 2
  [[ ! -e "${FAKE_DIR}/controller.pid" && ! -e "${FAKE_DIR}/ui.pid" ]] || fail_test "an application started on refusal"
  expect_equal "relays verified off" "$(state .relays.state)" off
  grep -qF 'relays-off done 0' "${FAKE_DIR}/events.log" || fail_test "no verified relay-off on refusal"
  grep -qF ' cgroup_enable=memory' "${WORK}/supervisor.log" || fail_test "no memory cgroup remedy"
  grep -qF '/boot/firmware/cmdline.txt' "${WORK}/supervisor.log" || fail_test "no cmdline remedy"
}

test_cgroup_each_missing_unlimited_unreadable_malformed_or_mismatched_limit_refuses_safely() {
  fake_cgroup
  local file mode value
  for file in cpu.max memory.max memory.swap.max pids.max; do
    cp "${WORK}/cgroup/${file}" "${WORK}/saved-limit"
    for mode in missing unlimited unreadable malformed overflow multiline mismatch tighter fifo; do
      rm -f "${WORK}/cgroup/${file}"
      cp "${WORK}/saved-limit" "${WORK}/cgroup/${file}"
      case ${mode} in
        missing) rm "${WORK}/cgroup/${file}" ;;
        unlimited) value=max; [[ "${file}" != cpu.max ]] || value='max 100000'; printf '%s\n' "${value}" >"${WORK}/cgroup/${file}" ;;
        unreadable) chmod 000 "${WORK}/cgroup/${file}" ;;
        malformed) printf 'not-a-limit\n' >"${WORK}/cgroup/${file}" ;;
        overflow) printf '9999999999999999999999999999999\n' >"${WORK}/cgroup/${file}" ;;
        multiline) printf '512\n512\n' >"${WORK}/cgroup/${file}" ;;
        mismatch) value=1024
          case ${file} in cpu.max) value='200000 100000' ;; memory.max) value=4294967296 ;; esac
          printf '%s\n' "${value}" >"${WORK}/cgroup/${file}" ;;
        tighter) value=1
          case ${file} in cpu.max) value='50000 100000' ;; memory.max) value=536870912 ;; esac
          printf '%s\n' "${value}" >"${WORK}/cgroup/${file}" ;;
        fifo) rm "${WORK}/cgroup/${file}"; mkfifo "${WORK}/cgroup/${file}" ;;
      esac
      # Root with DAC_OVERRIDE cannot model an unreadable regular file with chmod; the other cases still run there.
      if [[ "${mode}" == unreadable && -r "${WORK}/cgroup/${file}" ]]; then continue; fi
      : >"${FAKE_DIR}/events.log"
      start_cgroup_supervisor
      expect_limit_refusal
      grep -qF "${file}" "${WORK}/supervisor.log" || fail_test "${file}/${mode}: reason not named"
    done
    rm -f "${WORK}/cgroup/${file}"
    cp "${WORK}/saved-limit" "${WORK}/cgroup/${file}"
  done
}

test_cgroup_defaults_and_overrides_start_both_applications() {
  fake_cgroup
  start_cgroup_supervisor
  wait_both_running || return
  stop_supervisor
  printf '125000 50000\n' >"${WORK}/cgroup/cpu.max"
  printf '6442450944\n' >"${WORK}/cgroup/memory.max"
  printf '1024\n' >"${WORK}/cgroup/pids.max"
  start_cgroup_supervisor HVO_ROOF_CPUS=2.50 HVO_ROOF_MEMORY=6144M HVO_ROOF_PIDS_LIMIT=1024
  wait_both_running || return
  stop_supervisor
}

test_cgroup_unset_expectations_preserve_degraded_recovery() {
  # Even an unverifiable/v1/unlimited hierarchy is allowed when the deploy script leaves all expectations unset.
  fake_cgroup
  printf '4:memory:/legacy\n' >"${WORK}/proc/cgroup"
  rm "${WORK}/cgroup/memory.max"
  start_supervisor HVO_ROOF_CPUS= HVO_ROOF_MEMORY= HVO_ROOF_PIDS_LIMIT=
  wait_both_running || return
  stop_supervisor
}

test_cgroup_v1_unresolvable_and_wrong_membership_refuse() {
  fake_cgroup
  local mode
  for mode in v1 missing-proc malformed-path wrong-member no-mount; do
    printf '0::/\n' >"${WORK}/proc/cgroup"
    printf '29 23 0:26 / %s rw - cgroup2 cgroup rw\n' "${WORK}/cgroup" >"${WORK}/proc/mountinfo"
    case ${mode} in
      v1) printf '4:memory:/legacy\n' >"${WORK}/proc/cgroup" ;;
      missing-proc) rm "${WORK}/proc/cgroup" ;;
      malformed-path) printf '0::/../other\n' >"${WORK}/proc/cgroup" ;;
      wrong-member) printf '0::/wrong\n' >"${WORK}/proc/cgroup" ;;
      no-mount) : >"${WORK}/proc/mountinfo" ;;
    esac
    start_cgroup_supervisor
    expect_limit_refusal
  done
}

test_cgroup_namespace_mount_roots_and_escaped_mounts_find_only_own_limits() {
  fake_cgroup
  local mode root path escaped
  mkdir -p "${WORK}/cgroup/containers/own" "${WORK}/cgroup/own" "${WORK}/cgroup with space"
  for mode in host nested private escaped; do
    root=/ path=/containers/own
    case ${mode} in
      nested) root=/containers ;;
      private) root=/containers/own; path=/ ;;
      escaped) path=/ ;;
    esac
    escaped="${WORK}/cgroup"
    case ${mode} in
      host) cp "${WORK}/cgroup/"*.* "${WORK}/cgroup/containers/own/" ;;
      nested) cp "${WORK}/cgroup/"*.* "${WORK}/cgroup/own/" ;;
      escaped) cp "${WORK}/cgroup/"*.* "${WORK}/cgroup with space/"; escaped="${WORK}/cgroup\\040with\\040space" ;;
    esac
    printf '0::%s\n' "${path}" >"${WORK}/proc/cgroup"
    printf '29 23 0:26 %s %s rw - cgroup2 cgroup rw\n' "${root}" "${escaped}" >"${WORK}/proc/mountinfo"
    local own="${WORK}/cgroup${path%/}"
    [[ "${mode}" != nested ]] || own="${WORK}/cgroup/own"
    [[ "${mode}" != escaped ]] || own="${WORK}/cgroup with space"
    start_cgroup_supervisor "FAKE_CGROUP=${own}" HVO_SUPERVISOR_PIDS_DIR=
    wait_both_running || return
    stop_supervisor
  done
}

test_cgroup_refusal_retries_relays_and_starts_neither_application() {
  fake_cgroup
  rm "${WORK}/cgroup/memory.max"
  printf '%s\n' 1 1 0 >"${FAKE_DIR}/relays-off.exit-codes"
  start_cgroup_supervisor
  wait_until 4 "held stopped while resource limits are refused" state_is .controller.state relays-unverified || return
  [[ ! -e "${FAKE_DIR}/controller.pid" && ! -e "${FAKE_DIR}/ui.pid" ]] || fail_test "application started during relay retries"
  wait_supervisor_exit 8
  expect_equal "refusal exit after retries" "${EXIT_STATUS}" 2
  expect_equal "relay tries including shutdown" "$(event_count relays-off run)" 4
  expect_equal "verified off" "$(state .relays.state)" off
}

test_cgroup_signal_during_refusal_is_bounded_even_when_relay_off_hangs() {
  fake_cgroup
  rm "${WORK}/cgroup/cpu.max"
  echo 60 >"${FAKE_DIR}/relays-off.delay"
  start_cgroup_supervisor HVO_SUPERVISOR_RELAYS_OFF_SECONDS=1 HVO_SUPERVISOR_CONTROLLER_STOP_SECONDS=1
  wait_until 3 "relay-off during refusal" test -e "${FAKE_DIR}/relays-off.pid" || return
  kill -TERM "${SUPERVISOR_PID}"
  wait_supervisor_exit 4
  expect_equal "refusal status despite signal" "${EXIT_STATUS}" 2
  [[ ! -e "${FAKE_DIR}/controller.pid" && ! -e "${FAKE_DIR}/ui.pid" ]] || fail_test "application started after signal"
  expect_equal "unverified is retained" "$(state .relays.state)" unverified
}

test_cgroup_invalid_requested_limits_still_turn_relays_off_on_supervised_start() {
  fake_cgroup
  local setting
  for setting in HVO_ROOF_CPUS=0.99 HVO_ROOF_MEMORY=0g HVO_ROOF_PIDS_LIMIT=0 HVO_ROOF_PIDS_LIMIT=bad; do
    start_cgroup_supervisor "${setting}"
    expect_limit_refusal
  done
}

test_cgroup_one_shot_never_reads_limits_or_touches_the_hat() {
  fake_cgroup
  mkfifo "${WORK}/proc-blocked"
  echo 77 >"${FAKE_DIR}/controller.exit-at-start"
  env "${SUPERVISOR_ENV[@]}" "${CGROUP_EXPECTATIONS[@]}" "HVO_SUPERVISOR_PROC_DIR=${WORK}/proc-blocked" \
    "${SUPERVISOR}" --validate-deployment >"${WORK}/supervisor.log" 2>&1
  expect_equal "one-shot result" "$?" 77
  [[ ! -e "${FAKE_DIR}/relays-off.pid" ]] || fail_test "one-shot touched the HAT"
}
