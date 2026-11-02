#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2154 # sourced scenario: globals are owned by deploy-scenarios.sh
# Sourced by deploy-scenarios.sh's limits group (#152). Uses only that run's containers and emulated HAT. The retained
# seed controller is killed during emulated travel; a second container must refuse both apps yet verify its relays off.

run_limits_scenarios() {
  current_check="Limits: build and deploy the emulated seed controller"
  deploy
  (( DEPLOY_STATUS == 0 )) || fail "the seed deploy failed"
  run_alpaca_installer_mount_scenario
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

  for mode in default override host-namespace alpaca; do
    current_check="Limits: enforced ${mode} limits permit both real applications"
    actual=(--cpus 1 --memory 2g --memory-swap 2g --pids-limit 512)
    expected=(HVO_ROOF_CPUS=1 HVO_ROOF_MEMORY=2g HVO_ROOF_PIDS_LIMIT=512)
    if [[ "${mode}" == override ]]; then
      actual=(--cpus 2.5 --memory 3g --memory-swap 3g --pids-limit 1024)
      expected=(HVO_ROOF_CPUS=2.5 HVO_ROOF_MEMORY=3072m HVO_ROOF_PIDS_LIMIT=1024)
    elif [[ "${mode}" == host-namespace ]]; then
      actual+=(--cgroupns=host)
    elif [[ "${mode}" == alpaca ]]; then
      actual+=(--mount "type=volume,destination=/var/lib/hvo-roof-alpaca"
        --env Alpaca__Enabled=true --env Alpaca__ListenAddresses__0=0.0.0.0
        --env Alpaca__Discovery__Enabled=false --env Alpaca__ControllerUrl=http://localhost:8080
        --env Alpaca__ControllerApiKeyFile=/run/secrets/RoofControllerSecurity__ApiKeys__0__Key)
    fi
    run_limits_probe "${probe}" "${expected[@]}"
    wait_for "both real applications to start" 60 container_log_has "${probe}" '[supervisor] Started the web UI'
    container_log_has "${probe}" '[supervisor] Started the controller' || fail "controller did not start"
    container_log_has "${probe}" "Verified this supervisor's cgroup v2" || fail "limits were not verified"
    if [[ "${mode}" == alpaca ]]; then
      wait_for "the real Alpaca listener to start" 60 alpaca_probe_ready "${probe}"
      [[ "$(docker exec "${probe}" stat -c '%U:%G %a' /run/hvo-roof/alpaca/controller-key)" == "alpaca:alpaca 400" ]] \
        || fail "Alpaca did not receive its own private key copy"
      [[ "$(docker exec "${probe}" stat -c '%U:%G %a' /var/lib/hvo-roof-alpaca)" == "alpaca:alpaca 755" ]] \
        || fail "Alpaca identity volume is not writable by its own user only"
      docker exec -u alpaca "${probe}" test -s /var/lib/hvo-roof-alpaca/alpaca-devices.json \
        || fail "Alpaca did not write durable identities"
      if docker exec -u app "${probe}" test -r /run/hvo-roof/alpaca/controller-key; then fail "web UI can read Alpaca's key"; fi
      if docker exec -u alpaca "${probe}" test -r /run/secrets/RoofControllerSecurity__ApiKeys__0__Key; then fail "Alpaca can read controller secrets"; fi
      local alpaca_pid controller_pid_before elapsed stopped_at
      alpaca_pid=$(docker exec "${probe}" cat /run/hvo-roof/supervisor.json | jq -r '.alpaca.pid')
      controller_pid_before=$(docker exec "${probe}" cat /run/hvo-roof/supervisor.json | jq -r '.controller.pid')
      docker exec "${probe}" kill -KILL "${alpaca_pid}"
      wait_for "only Alpaca to restart" 30 alpaca_probe_restarted "${probe}"
      [[ "$(docker exec "${probe}" cat /run/hvo-roof/supervisor.json | jq -r '.controller.pid')" == "${controller_pid_before}" ]] \
        || fail "Alpaca's crash restarted the controller"
      alpaca_pid=$(docker exec "${probe}" cat /run/hvo-roof/supervisor.json | jq -r '.alpaca.pid')
      docker exec "${probe}" kill -STOP "${alpaca_pid}"
      stopped_at=$(now)
      docker stop -t 30 "${probe}" >/dev/null
      elapsed=$(seconds_since "${stopped_at}")
      within "${elapsed}" 28 || fail "stop with hung Alpaca took ${elapsed}s"
      container_log_has "${probe}" 'Alpaca server did not stop within 2s; killing it' || fail "hung Alpaca was not killed"
    else
      docker stop -t 30 "${probe}" >/dev/null
    fi
    [[ "$(docker inspect --format '{{.State.ExitCode}}' "${probe}")" == 0 ]] || fail "verified container did not stop cleanly"
    assert_relays_off
    docker rm -v "${probe}" >/dev/null
    pass "${mode}: actual kernel cgroup limits verified, both real applications start, normal stop exits 0"
  done
  # Leave the seed controller available to subsequent scenario groups.
  docker start "${controller}" >/dev/null
  wait_for "seed controller ready again" 120 controller_ready
}

run_alpaca_installer_mount_scenario() {
  current_check="Alpaca: real deploy with an installer-style key filename and read-only secrets directory"
  local address
  # This disposable Docker network is the emulator rig's LAN; no host interface or route is changed.
  address=$(docker network inspect "${network}" --format '{{(index .IPAM.Config 0).Gateway}}')
  python3 - "${address}" <<'PY' >/dev/null 2>&1 || fail "the Alpaca scenario's TCP or UDP port is occupied"
import socket,sys
with socket.socket() as tcp, socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp:
    tcp.bind((sys.argv[1],47680))
    udp.bind(('',47681))
PY
  # The installer creates the bind source before invoking deploy; reproduce that setup in this real-Docker fixture.
  as_root install -d -m 0755 -o 0 -g 0 "${work}/alpaca-data"
  deploy ALPACA_ENABLED=true "ALPACA_LAN_ADDRESS=${address}" ALPACA_PORT=47680 ALPACA_DISCOVERY_PORT=47681 \
    "ALPACA_API_KEY_FILE=${work}/secrets/RoofControllerSecurity__ApiKeys__0__Key" "ALPACA_DATA_DIR=${work}/alpaca-data"
  (( DEPLOY_STATUS == 0 )) || fail "the installer-style Alpaca key could not be mounted or validated"
  [[ "$(docker exec "${controller}" stat -c '%U:%G %a' /run/hvo-roof/alpaca/controller-key)" == 'alpaca:alpaca 400' ]] \
    || fail "the deployed Alpaca key is not a private copy"
  docker exec -u alpaca "${controller}" test -s /var/lib/hvo-roof-alpaca/alpaca-devices.json \
    || fail "the deployed Alpaca process did not persist its identities"
  pass "production deploy and preflight mount the allocated key without nesting below read-only secrets"
  deploy ALPACA_ENABLED=false
  (( DEPLOY_STATUS == 0 )) || fail "the scenario could not return to default-off deployment"
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


alpaca_probe_ready() {
  docker exec "$1" curl -fsS --max-time 2 http://localhost:11111/management/apiversions >/dev/null
}

alpaca_probe_restarted() {
  docker exec "$1" cat /run/hvo-roof/supervisor.json | jq -e '.alpaca.starts >= 2 and .alpaca.state == "running"' >/dev/null &&
    alpaca_probe_ready "$1"
}
