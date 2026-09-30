#!/usr/bin/env bash
# The published hvo-roof (tests/cli/terminal-smoke.sh) against the controller and the HAT emulator run with dotnet run,
# with no Docker: how CI checks hvo-roof on a Mac (#66), and a way to run the terminal smoke test on any machine with the
# SDK. It builds both from src/ (Debug), starts the emulator with the roof four times as fast, then the controller in the
# Development environment with a throwaway admin API key, waits until the controller has the roof closed, runs
# terminal-smoke.sh, and stops both on exit. Their logs are kept beside the screens. Nothing touches hardware: the HAT is
# the emulator's, and both listen on loopback only.
#
#   HVO_ROOF_CLI=path/to/hvo-roof tests/cli/dotnet-run-smoke.sh
#
# Settings (environment): TERMINAL_SMOKE_OUT, as for terminal-smoke.sh; DOTNET_RUN_SMOKE_PORT (default 15395), the
# controller's port, with the emulator's control port and register port the next two. Needs the .NET SDK that
# src/global.json pins, curl, python3, tmux, and bash 4 or later.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
src="${here}/../../src"

: "${HVO_ROOF_CLI:?HVO_ROOF_CLI must name the hvo-roof executable}"
out=${TERMINAL_SMOKE_OUT:-$(mktemp -d)}
mkdir -p "${out}"

# Both as absolute paths: the script works from src/ below.
out=$(cd "${out}" && pwd)
export TERMINAL_SMOKE_OUT="${out}"
case "${HVO_ROOF_CLI}" in
    /*) ;;
    *) HVO_ROOF_CLI="$(cd "$(dirname "${HVO_ROOF_CLI}")" && pwd)/$(basename "${HVO_ROOF_CLI}")" ;;
esac
export HVO_ROOF_CLI

port=${DOTNET_RUN_SMOKE_PORT:-15395}
control_port=$((port + 1))
register_port=$((port + 2))
roof="http://127.0.0.1:${port}"

# A throwaway admin key for this run only. It never reaches a log: it goes to the controller and to terminal-smoke.sh
# in their environments.
HVO_ROOF_API_KEY=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
export HVO_ROOF_API_KEY

fail() {
    echo "[dotnet-run] FAIL: $*" >&2
    exit 1
}

emulator_pid=""
controller_pid=""

# stop <pid>: stops a dotnet run and the program it started (dotnet run does not always pass a signal on to it).
stop() {
    local pid=$1 child
    for child in $(pgrep -P "${pid}" 2>/dev/null || true); do
        kill "${child}" 2>/dev/null || true
    done
    kill "${pid}" 2>/dev/null || true
    wait "${pid}" 2>/dev/null || true
}

cleanup() {
    local status=$?
    [ -z "${controller_pid}" ] || stop "${controller_pid}"
    [ -z "${emulator_pid}" ] || stop "${emulator_pid}"
    if [ "${status}" -ne 0 ]; then
        for log in controller emulator; do
            if [ -f "${out}/${log}.log" ]; then
                echo "[dotnet-run] The end of the ${log}'s log:" >&2
                tail -n 60 "${out}/${log}.log" >&2 || true
            fi
        done
    fi
    exit "${status}"
}
trap cleanup EXIT

command -v dotnet >/dev/null || fail "the .NET SDK is required"
command -v curl >/dev/null || fail "curl is required"
command -v python3 >/dev/null || fail "python3 is required"

# Built one after the other, before either starts: two builds at once would both write the projects they share.
cd "${src}"
for project in HVO.RoofControllerV4.Emulator HVO.RoofControllerV4.RPi; do
    dotnet build "${project}" -v quiet -nologo >"${out}/build-${project}.txt" 2>&1 \
        || fail "dotnet build ${project} failed: $(tail -n 20 "${out}/build-${project}.txt")"
done
echo "[dotnet-run] built the emulator and the controller"

ASPNETCORE_URLS="http://127.0.0.1:${control_port}" \
    Emulator__RegisterPort="${register_port}" \
    Emulator__TimeScale=4 \
    dotnet run --no-build --no-launch-profile --project HVO.RoofControllerV4.Emulator >"${out}/emulator.log" 2>&1 &
emulator_pid=$!

# The Development settings (the HAT emulator, plain HTTP), with the emulator's ports and the key.
ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="${roof}" \
    HatEmulator__Port="${register_port}" \
    BlueIris__BaseUrl="http://127.0.0.1:${control_port}" \
    RoofControllerSecurity__ApiKeys__0__Name=dotnet-run-smoke \
    RoofControllerSecurity__ApiKeys__0__Role=RoofAdmin \
    RoofControllerSecurity__ApiKeys__0__Key="${HVO_ROOF_API_KEY}" \
    dotnet run --no-build --no-launch-profile --project HVO.RoofControllerV4.RPi >"${out}/controller.log" 2>&1 &
controller_pid=$!

# The controller answers once it has read the emulated HAT: initialized, with the roof at the closed limit.
initialized_closed() {
    curl -fsS --max-time 5 -H "X-Api-Key: ${HVO_ROOF_API_KEY}" "${roof}/api/v4.0/RoofControl/Status" 2>/dev/null |
        python3 -c 'import json, sys; status = json.load(sys.stdin); sys.exit(0 if status["isInitialized"] and status["status"] == "Closed" else 1)' \
        2>/dev/null
}

deadline=$((SECONDS + 120))
until initialized_closed; do
    kill -0 "${emulator_pid}" 2>/dev/null || fail "the emulator ended; its log is in ${out}/emulator.log"
    kill -0 "${controller_pid}" 2>/dev/null || fail "the controller ended; its log is in ${out}/controller.log"
    [ "${SECONDS}" -lt "${deadline}" ] || fail "the controller did not have the roof closed within 120 s"
    sleep 0.5
done
echo "[dotnet-run] the controller (${roof}) has the emulated roof closed"

HVO_ROOF_URL="${roof}/" "${here}/terminal-smoke.sh"
