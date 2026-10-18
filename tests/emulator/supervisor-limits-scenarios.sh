#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2154 # sourced scenario: globals are owned by deploy-scenarios.sh
# Sourced by deploy-scenarios.sh's limits group (#152). Uses only that run's containers and emulated HAT. The retained
# seed controller is killed during emulated travel; a second container must refuse both apps yet verify its relays off.

run_limits_scenarios() {
  current_check="Limits: build and deploy the emulated seed controller"
  deploy
  (( DEPLOY_STATUS == 0 )) || fail "the seed deploy failed"
  # The env file can include generated test credentials: private, never printed, and removed with work's fixtures.
  (umask 077; docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${controller}" >"${work}/limits.env")
  local probe="${controller}-swap" mode code
  local -a actual=() expected=(HVO_ROOF_CPUS=1 HVO_ROOF_MEMORY=2g HVO_ROOF_PIDS_LIMIT=512)

  for mode in cpu memory swap pids mismatch; do
    current_check="Limits: ${mode} not enforced, energized emulator relays verified off before refusal exits"
    seed_energized_relays
    actual=(--cpus 1 --memory 2g --memory-swap 2g --pids-limit 512)
    case ${mode} in
      cpu) actual=(--memory 2g --memory-swap 2g --pids-limit 512) ;;
      memory) actual=(--cpus 1 --pids-limit 512) ;;
      swap) actual=(--cpus 1 --memory 2g --memory-swap 3g --pids-limit 512) ;;
      pids) actual=(--cpus 1 --memory 2g --memory-swap 2g) ;;
      mismatch) actual=(--cpus 2 --memory 3g --memory-swap 3g --pids-limit 1024) ;;
    esac
    run_limits_probe "${probe}" "${expected[@]}"
    wait_for "the refused container to exit" 30 probe_stopped "${probe}"
    code=$(docker inspect --format '{{.State.ExitCode}}' "${probe}")
    [[ "${code}" == 2 ]] || fail "${mode}: refusal exited ${code}, not 2"
    assert_limits_probe_never_started_apps "${probe}"
    container_log_has "${probe}" " cgroup_enable=memory" || fail "no cgroup remedy"
    container_log_has "${probe}" "HAT's register verified by read-back" || fail "no verified relay-off"
    assert_relays_off
    docker rm -v "${probe}" >/dev/null
    pass "${mode}: exits 2, neither application starts, retained energized registers verified off by the real helper"
  done

  current_check="Limits: failed relay-off holds both apps stopped, retries, then exits 2 after verification"
  seed_energized_relays
  emulator_post bus '{"failWrites":true}'
  actual=(--cpus 1 --pids-limit 512)
  run_limits_probe "${probe}" "${expected[@]}"
  wait_for "two failed relay-off attempts" 20 container_log_has "${probe}" 'retrying until the relays are verified off'
  container_running "${probe}" || fail "refusal exited before verifying off"
  plant_is '.relayRegister != 0' || fail "injected write failure did not retain the energized registers"
  assert_limits_probe_never_started_apps "${probe}"
  emulator_post bus '{"failWrites":false}'
  wait_for "refusal to exit after verification" 30 probe_stopped "${probe}"
  [[ "$(docker inspect --format '{{.State.ExitCode}}' "${probe}")" == 2 ]] || fail "retry refusal did not exit 2"
  assert_limits_probe_never_started_apps "${probe}"
  assert_relays_off
  docker rm -v "${probe}" >/dev/null
  pass "writes fail: stays alive with both apps held stopped; writes restored: read-back verifies off and exits 2"

  current_check="Limits: SIGTERM during permanent relay-off failure stays bounded and starts neither app"
  seed_energized_relays
  emulator_post bus '{"failWrites":true}'
  run_limits_probe "${probe}" "${expected[@]}"
  wait_for "refusal retry" 20 container_log_has "${probe}" 'retrying until the relays are verified off'
  local start elapsed
  start=$(now)
  docker stop -t 30 "${probe}" >/dev/null
  elapsed=$(seconds_since "${start}")
  within "${elapsed}" 28 || fail "stop took ${elapsed}s (bound 28s including scheduling margin)"
  [[ "$(docker inspect --format '{{.State.ExitCode}}' "${probe}")" == 2 ]] || fail "signal refusal did not exit 2"
  assert_limits_probe_never_started_apps "${probe}"
  docker rm -v "${probe}" >/dev/null
  emulator_post bus '{"failWrites":false}'
  # A subsequent refused start repairs the retained register without ever starting an application.
  run_limits_probe "${probe}" "${expected[@]}"
  wait_for "refusal verified off after writes restored" 30 probe_stopped "${probe}"
  assert_relays_off
  docker rm -v "${probe}" >/dev/null
  pass "SIGTERM exits 2 in ${elapsed}s while writes fail; next refused start verifies retained registers off"

  for mode in default override host-namespace; do
    current_check="Limits: enforced ${mode} limits permit both real applications"
    actual=(--cpus 1 --memory 2g --memory-swap 2g --pids-limit 512)
    expected=(HVO_ROOF_CPUS=1 HVO_ROOF_MEMORY=2g HVO_ROOF_PIDS_LIMIT=512)
    if [[ "${mode}" == override ]]; then
      actual=(--cpus 2.5 --memory 3g --memory-swap 3g --pids-limit 1024)
      expected=(HVO_ROOF_CPUS=2.5 HVO_ROOF_MEMORY=3072m HVO_ROOF_PIDS_LIMIT=1024)
    elif [[ "${mode}" == host-namespace ]]; then
      actual+=(--cgroupns=host)
    fi
    run_limits_probe "${probe}" "${expected[@]}"
    wait_for "both real applications to start" 60 container_log_has "${probe}" '[supervisor] Started the web UI'
    container_log_has "${probe}" '[supervisor] Started the controller' || fail "controller did not start"
    container_log_has "${probe}" "Verified this supervisor's cgroup v2" || fail "limits were not verified"
    docker stop -t 30 "${probe}" >/dev/null
    [[ "$(docker inspect --format '{{.State.ExitCode}}' "${probe}")" == 0 ]] || fail "verified container did not stop cleanly"
    assert_relays_off
    docker rm -v "${probe}" >/dev/null
    pass "${mode}: actual kernel cgroup limits verified, both real applications start, normal stop exits 0"
  done
  # Leave the seed controller available to subsequent scenario groups.
  docker start "${controller}" >/dev/null
  wait_for "seed controller ready again" 120 controller_ready
}

probe_stopped() {
  ! container_running "$1"
}

seed_energized_relays() {
  docker start "${controller}" >/dev/null
  wait_for "the seed controller ready" 120 controller_ready
  wait_for "the seed controller initialized" 60 roof_is '.isInitialized'
  close_roof
  start_travel Open
  docker kill "${controller}" >/dev/null
  plant_is '.relayRegister != 0' || fail "whole-container kill did not retain energized emulator registers"
}

run_limits_probe() {
  local probe=$1
  shift
  local -a environment=()
  local item
  for item in "$@"; do environment+=(--env "${item}"); done
  docker run -d --name "${probe}" --network "${network}" --volumes-from "${controller}" \
    --env-file "${work}/limits.env" "${environment[@]}" "${actual[@]}" \
    --read-only --tmpfs /run:size=16m --tmpfs /tmp:size=64m --tmpfs /root/.aspnet:size=4m,mode=0700 \
    --cap-drop ALL --cap-add CHOWN --cap-add KILL --cap-add SETGID --cap-add SETUID \
    --security-opt no-new-privileges "${image}" >/dev/null
}

assert_limits_probe_never_started_apps() {
  container_log_lacks "$1" '[supervisor] Started the controller' || fail "refusal started controller"
  container_log_lacks "$1" '[supervisor] Started the web UI' || fail "refusal started UI"
}
