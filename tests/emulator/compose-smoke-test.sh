#!/usr/bin/env bash
# End-to-end check of the emulator compose profile (src/HVO.RoofControllerV4.RPi/docker-compose.yaml, profile
# emulator): builds the controller and HAT emulator images for this machine, starts them, and drives the emulated roof
# through the controller's API: open to the open limit, close to the closed limit, with no plant violations. It also
# checks that the controller says it is emulated (health, status and the console banner).
#
# Needs docker with compose v2, curl and jq. It touches no hardware: the controller maps no devices and talks to the
# emulator over the compose network. Everything it starts is removed on exit.
#
#   tests/emulator/compose-smoke-test.sh
#
# Settings (environment): SMOKE_PROJECT (compose project, default hvo-roof-emulator-smoke), HVO_EMULATED_ROOF_PORT
# (default 15195), HVO_EMULATOR_CONTROL_PORT (default 15290), HVO_EMULATOR_TIME_SCALE (default 4), SMOKE_NO_BUILD=1 to
# reuse images already built.
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
compose_file="${repo_root}/src/HVO.RoofControllerV4.RPi/docker-compose.yaml"
project=${SMOKE_PROJECT:-hvo-roof-emulator-smoke}

export HVO_EMULATED_ROOF_PORT=${HVO_EMULATED_ROOF_PORT:-15195}
export HVO_EMULATOR_CONTROL_PORT=${HVO_EMULATOR_CONTROL_PORT:-15290}
export HVO_EMULATOR_TIME_SCALE=${HVO_EMULATOR_TIME_SCALE:-4}
# A throwaway admin key for this run only.
HVO_EMULATED_ROOF_API_KEY=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
export HVO_EMULATED_ROOF_API_KEY

roof="http://127.0.0.1:${HVO_EMULATED_ROOF_PORT}"
roof_api="${roof}/api/v4.0/RoofControl"
emulator_api="http://127.0.0.1:${HVO_EMULATOR_CONTROL_PORT}/api/emulator"

compose() {
    docker compose -f "${compose_file}" -p "${project}" --profile emulator "$@"
}

fail() {
    echo "[smoke] FAIL: $*" >&2
    exit 1
}

cleanup() {
    local status=$?
    if [ "${status}" -ne 0 ]; then
        echo "[smoke] Logs of the failed run:" >&2
        compose logs --no-color --tail 200 >&2 || true
    fi
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    exit "${status}"
}
trap cleanup EXIT

roof_get() {
    curl -fsS --max-time 10 -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof_api}/$1"
}

roof_post() {
    curl -fsS --max-time 10 -X POST -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof_api}/$1" >/dev/null
}

# wait_for <description> <timeout seconds> <command...>: runs the command every half second until it succeeds.
wait_for() {
    local description=$1 timeout=$2
    shift 2
    local deadline=$((SECONDS + timeout))
    until "$@" >/dev/null 2>&1; do
        if [ "${SECONDS}" -ge "${deadline}" ]; then
            fail "timed out after ${timeout} s waiting for ${description}"
        fi
        sleep 0.5
    done
    echo "[smoke] ${description}"
}

roof_is() {
    roof_get Status | jq -e --arg status "$1" '.status == $status and .isMoving == false' >/dev/null
}

roof_initialized_closed() {
    roof_get Status | jq -e '.isInitialized and .status == "Closed"' >/dev/null
}

relays_off() {
    curl -fsS --max-time 10 "${emulator_api}/status" | jq -e '.plant.relayRegister == 0 and .plant.velocityMetersPerSecond == 0' >/dev/null
}

no_violations() {
    local violations
    violations=$(curl -fsS --max-time 10 "${emulator_api}/violations")
    if [ "$(jq 'length' <<<"${violations}")" -ne 0 ]; then
        fail "the emulator recorded plant violations: ${violations}"
    fi
}

command -v jq >/dev/null || fail "jq is required"
command -v curl >/dev/null || fail "curl is required"

build_flag=(--build)
if [ "${SMOKE_NO_BUILD:-0}" = 1 ]; then
    build_flag=()
fi

echo "[smoke] Starting the emulator profile as project ${project} (controller ${roof}, emulator control ${emulator_api})"
compose up -d "${build_flag[@]}" --wait --wait-timeout 300

wait_for "the controller is initialized with the roof closed" 60 roof_initialized_closed

status=$(roof_get Status)
jq -e '.hatMode == "Emulated" and .isUsingPhysicalHardware == true and .isIgnoringPhysicalLimitSwitches == false' <<<"${status}" >/dev/null \
    || fail "the status does not report an emulated HAT with the limit switches in use: ${status}"

# The detailed /health needs a key; /health/ready and /health/live are anonymous.
health=$(curl -sS --max-time 10 -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof}/health")
jq -e '.status == "Degraded" and any(.checks[]; .data.HardwareMode == "Emulated")' <<<"${health}" >/dev/null \
    || fail "health is not Degraded with HardwareMode Emulated: ${health}"
echo "[smoke] Health is Degraded and names the emulated HAT"

curl -fsS --max-time 10 "${roof}/login" | grep -q 'data-testid="emulated-hat-banner"' \
    || fail "the console login page does not carry the EMULATED HAT banner"
echo "[smoke] The console shows the EMULATED HAT banner"

roof_post Open
wait_for "the roof opened to the open limit" 90 roof_is Open
curl -fsS --max-time 10 "${emulator_api}/status" | jq -e '.plant.openLimitActuated == true' >/dev/null \
    || fail "the controller reports Open but the emulated open limit is not actuated"
wait_for "the relays are off and the roof is still" 30 relays_off

roof_post Close
wait_for "the roof closed to the closed limit" 90 roof_is Closed
curl -fsS --max-time 10 "${emulator_api}/status" | jq -e '.plant.closedLimitActuated == true' >/dev/null \
    || fail "the controller reports Closed but the emulated closed limit is not actuated"
wait_for "the relays are off and the roof is still" 30 relays_off

no_violations
echo "[smoke] PASS: the roof opened and closed through the containerized controller and the HAT emulator, with no violations"
