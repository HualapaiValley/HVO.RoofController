#!/usr/bin/env bash
# End-to-end check of the emulator compose profile (src/HVO.RoofControllerV4.RPi/docker-compose.yaml, profile
# emulator): builds the controller and HAT emulator images for this machine, starts them, and drives the emulated roof
# through the controller's API: open to the open limit, close to the closed limit, with no plant violations. It also
# checks that the controller says it is emulated (health and status), that its web UI answers, and that its camera
# proxy relays the emulator's MJPEG camera.
#
# Needs docker with compose v2, curl and jq. It touches no hardware: the controller maps no devices and talks to the
# emulator over the compose network. Everything it starts is removed on exit.
#
#   tests/emulator/compose-smoke-test.sh
#
# Settings (environment): SMOKE_PROJECT (compose project, default hvo-roof-emulator-smoke), HVO_EMULATED_ROOF_PORT
# (default 15195), HVO_EMULATED_WEB_PORT (default 15196), HVO_EMULATOR_CONTROL_PORT (default 15290), HVO_EMULATOR_TIME_SCALE (default 4), SMOKE_NO_BUILD=1 to
# reuse images already built. With HVO_ROOF_CLI naming a published hvo-roof, it also runs tests/cli/terminal-smoke.sh
# against the controller (the command line and its terminal interface in tmux), saving the screens in
# TERMINAL_SMOKE_OUT.
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
compose_file="${repo_root}/src/HVO.RoofControllerV4.RPi/docker-compose.yaml"
project=${SMOKE_PROJECT:-hvo-roof-emulator-smoke}

export HVO_EMULATED_ROOF_PORT=${HVO_EMULATED_ROOF_PORT:-15195}
export HVO_EMULATED_WEB_PORT=${HVO_EMULATED_WEB_PORT:-15196}
export HVO_EMULATOR_CONTROL_PORT=${HVO_EMULATOR_CONTROL_PORT:-15290}
export HVO_EMULATOR_TIME_SCALE=${HVO_EMULATOR_TIME_SCALE:-4}
# A throwaway admin key for this run only.
HVO_EMULATED_ROOF_API_KEY=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
export HVO_EMULATED_ROOF_API_KEY

roof="http://127.0.0.1:${HVO_EMULATED_ROOF_PORT}"
web="http://127.0.0.1:${HVO_EMULATED_WEB_PORT}"
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
# The guarded expansion: bash before 4.4 treats an empty array as unbound under set -u.
compose up -d ${build_flag[@]+"${build_flag[@]}"} --wait --wait-timeout 300

wait_for "the controller is initialized with the roof closed" 60 roof_initialized_closed

status=$(roof_get Status)
jq -e '.hatMode == "Emulated" and .isUsingPhysicalHardware == true and .isIgnoringPhysicalLimitSwitches == false' <<<"${status}" >/dev/null \
    || fail "the status does not report an emulated HAT with the limit switches in use: ${status}"

# The detailed /health needs a key; /health/ready and /health/live are anonymous.
health=$(curl -sS --max-time 10 -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof}/health")
jq -e '.status == "Degraded" and any(.checks[]; .data.HardwareMode == "Emulated")' <<<"${health}" >/dev/null \
    || fail "health is not Degraded with HardwareMode Emulated: ${health}"
echo "[smoke] Health is Degraded and names the emulated HAT"

# The web UI, the container's second process: its sign-in page, with the Stop bar. (Its live pages carry the EMULATED
# HAT banner: the web UI's tests check it.)
curl -fsS --max-time 10 "${web}/signin" | grep -q 'data-testid="stop"' \
    || fail "the web UI's sign-in page does not answer with its Stop bar"
echo "[smoke] The web UI answers, with Stop on its sign-in page"

# The camera proxy relays the emulator's MJPEG camera: three seconds of the stream hold JPEG parts. curl ends the
# endless stream with its time limit (exit 28).
camera_dir=$(mktemp -d)
curl -sS --max-time 3 -D "${camera_dir}/headers" -o "${camera_dir}/body" \
    -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof}/api/v1.0/Camera/2/mjpeg" 2>/dev/null || true
head -n 1 "${camera_dir}/headers" | grep -q ' 200' || fail "the camera proxy did not answer 200: $(head -n 1 "${camera_dir}/headers")"
grep -qi '^content-type: multipart/x-mixed-replace' "${camera_dir}/headers" || fail "the camera stream is not multipart/x-mixed-replace"
[ "$(grep -ac '^Content-Type: image/jpeg' "${camera_dir}/body")" -ge 2 ] || fail "the camera stream held fewer than two JPEG parts"
rm -rf "${camera_dir}"
curl -fsS --max-time 10 -X POST -H 'Content-Type: application/json' -d '{"mode":"Unavailable"}' "${emulator_api}/camera" >/dev/null
camera_status=$(curl -sS --max-time 10 -o /dev/null -w '%{http_code}' -H "X-Api-Key: ${HVO_EMULATED_ROOF_API_KEY}" "${roof}/api/v1.0/Camera/2/mjpeg")
[ "${camera_status}" = 502 ] || fail "the camera proxy answered ${camera_status}, not 502, for an unavailable camera"
curl -fsS --max-time 10 -X POST -H 'Content-Type: application/json' -d '{"mode":"Live"}' "${emulator_api}/camera" >/dev/null
echo "[smoke] The camera proxy relays the emulated camera, and answers 502 when it is unavailable"

if [ -n "${HVO_ROOF_CLI:-}" ]; then
    HVO_ROOF_URL="${roof}/" HVO_ROOF_API_KEY="${HVO_EMULATED_ROOF_API_KEY}" "${repo_root}/tests/cli/terminal-smoke.sh"
    wait_for "the roof is closed again after the terminal check" 90 roof_is Closed
fi

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
