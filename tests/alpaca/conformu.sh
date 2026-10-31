#!/usr/bin/env bash
# ConformU, ASCOM's conformance checker, in CLI mode against the roof's Alpaca server (#220), Dome 0 and SafetyMonitor 0,
# with the roof controller and the HAT emulator behind it: all three built from this checkout and run on this machine, on
# loopback. No hardware is involved. The controller's weather source is the emulator's Alpaca safety monitor, so the
# server's SafetyMonitor reads safe weather end to end, and its blocking telescope is the emulator's Alpaca mount.
#
# Phase 3 (CloseShutter): the controller allows Alpaca Close (AllowCommands CloseOnly, in this run's settings only, never
# a shipped default), the Alpaca server reaches it with its own account's key (RoofAlpacaServer), and ConformU sends the
# External Alpaca account's key (RoofAlpacaCommander) as HTTP Basic, both parts the key. Before anything may command the
# roof, the chain guard (tests/alpaca/alpaca-ci.py guard) checks that ConformU's target is loopback, that the Alpaca
# server reaches this run's controller, and that the controller drives the HAT emulator; otherwise the run stops. A CI
# person (an Operator, made with a run-only admin key) clears the Stop hold set at the controller's start, through the
# session API, as a person would in a UI. ConformU then runs from tests/alpaca/conformu-settings.json, whose
# DomeOpenShutter is false: the roof starts Closed, so its CloseShutter is a no-op success and OpenShutter is refused; the
# script checks the emulated roof did not move. Then the scripted Close runs (alpaca-ci.py close-runs) start the roof part
# way open through the emulator and check the hold, the credential, an unparked mount, a Close grant, a new reason after
# a grant, and unsafe weather, each against what the emulated roof and relays did.
#
# Every key is made for this run in a directory only this user can read, kept out of the artifacts, and deleted at the
# end; the logs and results are checked not to hold any of them before they are kept, and a file that does is deleted.
#
# Never point ConformU at an Alpaca server in front of the real roof: `conformu conformance <url>` and
# `conformu alpacaprotocol <url>` turn DomeOpenShutter on, and this server closes the roof (docs/alpaca.md).
#
# The ConformU release is pinned by version and SHA-256 below. Every check must report no error and no issue, and only
# the information messages and configuration alerts tests/alpaca/conformu-expected.json lists, each with why
# (tests/alpaca/check-conformu-results.py). The protocol check sends hundreds of requests a second, so the server's
# per-address limit (100 a second by default) is raised for it here.
#
# Usage: tests/alpaca/conformu.sh [--no-build]
# Environment:
#   HVO_ALPACA_TEST_PORT_BASE  the first of the ports used, default 47200: +10 the emulator's control API, +11 its
#                              registers, +20 the controller, +30 the Alpaca server
#   HVO_ALPACA_TEST_DIR        where the logs, settings and results go (default: a new temporary directory, kept)
#   HVO_CONFORMU_DIR           a ConformU already unpacked (default: downloaded and checked against its SHA-256)
#   CONFIGURATION              Debug (default) or Release
set -euo pipefail

readonly CONFORMU_VERSION=4.5.3
readonly CONFORMU_SHA256_X64=47aa2a6da3e25bca77723efcaa5cd619f2b728c8e060bfeb098da6b5d0a64600
readonly CONFORMU_SHA256_ARM64=8c01fce9447f78e7f076c2279d6f38c7b8f78ef8d3c9f92e78566a61fb778bbc

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
configuration=${CONFIGURATION:-Debug}
base=${HVO_ALPACA_TEST_PORT_BASE:-47200}
control_port=$((base + 10))
register_port=$((base + 11))
controller_port=$((base + 20))
alpaca_port=$((base + 30))
build=true
if [[ "${1:-}" == "--no-build" ]]; then
  build=false
elif [[ $# -gt 0 ]]; then
  echo "Usage: $0 [--no-build]" >&2
  exit 2
fi

work=${HVO_ALPACA_TEST_DIR:-$(mktemp -d -t hvo-alpaca-conformu-XXXXXX)}
mkdir -p "${work}"
echo "Work directory: ${work}"
# The run's secrets: below the work directory, never in the artifacts (which keep only its top level), deleted at the end.
private="${work}/private/run"
(umask 077 && mkdir -p "${private}")

pids=()
secret_args=()
for secret in server-key external-key admin-key person-password session; do
  secret_args+=(--secret "${private}/${secret}")
done
cleanup() {
  local pid
  for pid in "${pids[@]}"; do
    kill -TERM "${pid}" 2>/dev/null || true
  done
  for pid in "${pids[@]}"; do
    wait "${pid}" 2>/dev/null || true
  done
  # Whatever failed, no file kept as an artifact may hold a run secret: each one that does is deleted. Then the keys, the
  # password and the session, made for this run only, go too.
  if [[ -d "${private}" ]]; then
    python3 "${repo}/tests/alpaca/alpaca-ci.py" no-secret --remove "${secret_args[@]}" "${work}"/* >/dev/null 2>&1 \
      || echo "::warning::A file holding a run secret was deleted from the results" >&2
  fi
  rm -rf "${work}/private"
}
trap cleanup EXIT

fail() {
  echo "::error::$*" >&2
  exit 1
}

# Waits up to 60 s for a URL to answer with a success status.
wait_for() {
  local url=$1 what=$2 deadline=$((SECONDS + 60))
  until curl -fsS -o /dev/null --max-time 2 "${url}"; do
    if ((SECONDS > deadline)); then
      fail "${what} did not answer ${url} within 60 s (logs in ${work})"
    fi
    sleep 0.5
  done
}

for port in "${control_port}" "${register_port}" "${controller_port}" "${alpaca_port}"; do
  if (exec 3<>"/dev/tcp/127.0.0.1/${port}") 2>/dev/null; then
    fail "Port ${port} is in use; set HVO_ALPACA_TEST_PORT_BASE to a free range"
  fi
done

if [[ "${build}" == true ]]; then
  (cd "${repo}/src" && for project in HVO.RoofControllerV4.Emulator HVO.RoofControllerV4.RPi HVO.RoofControllerV4.Alpaca; do
    dotnet build "${project}/${project}.csproj" -c "${configuration}" -v quiet -nologo || exit 1
  done)
fi
output() { echo "${repo}/src/$1/bin/${configuration}/net10.0"; }

# ConformU, pinned and checked.
if [[ -n "${HVO_CONFORMU_DIR:-}" ]]; then
  conformu_dir=${HVO_CONFORMU_DIR}
else
  case "$(uname -m)" in
    x86_64) rid=linux-x64 sha=${CONFORMU_SHA256_X64} ;;
    aarch64 | arm64) rid=linux-arm64 sha=${CONFORMU_SHA256_ARM64} ;;
    *) fail "No ConformU for $(uname -m)" ;;
  esac
  archive="${work}/conformu.${CONFORMU_VERSION}.${rid}.tar.gz"
  curl -fsSL --retry 3 -o "${archive}" \
    "https://github.com/ASCOMInitiative/ConformU/releases/download/v${CONFORMU_VERSION}/conformu.${CONFORMU_VERSION}.${rid}.tar.gz"
  echo "${sha}  ${archive}" | sha256sum -c - || fail "ConformU ${CONFORMU_VERSION} (${rid}) does not match its pinned SHA-256"
  conformu_dir="${work}/conformu"
  mkdir -p "${conformu_dir}"
  tar -xzf "${archive}" -C "${conformu_dir}"
fi
[[ -x "${conformu_dir}/conformu" ]] || fail "No conformu program in ${conformu_dir}"

# The Alpaca server's devices' identities, made here so the controller can list them as its own: its SafetyMonitor says
# safe only while the controller does, which then never reads it back as its weather.
mkdir -p "${work}/alpaca-data"
dome_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
safetymonitor_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
printf '{"dome/0":"%s","safetymonitor/0":"%s"}\n' "${dome_id}" "${safetymonitor_id}" > "${work}/alpaca-data/alpaca-devices.json"

# The run's keys and the CI person's password: made here, in files only this user can read; the controller is given the
# keys' hashes. The Alpaca server's own account, the External Alpaca account (ConformU's and the scripted runs'), and an
# admin key that only makes the CI person.
new_secret() { (umask 077 && python3 -c 'import secrets; print(secrets.token_hex(24))' > "${private}/$1"); }
hash_of() { tr -d '\n' < "${private}/$1" | sha256sum | cut -d' ' -f1; }
new_secret server-key
new_secret external-key
new_secret admin-key
new_secret person-password

echo "Starting the HAT emulator (registers ${register_port}, control ${control_port})"
(cd "$(output HVO.RoofControllerV4.Emulator)" && exec env Emulator__RegisterPort="${register_port}" \
  dotnet HVO.RoofControllerV4.Emulator.dll --urls "http://127.0.0.1:${control_port}") > "${work}/emulator.log" 2>&1 &
pids+=("$!")
wait_for "http://127.0.0.1:${control_port}/api/emulator/status" "The HAT emulator"

echo "Starting the controller (${controller_port}) against it"
(cd "$(output HVO.RoofControllerV4.RPi)" && exec env \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="http://127.0.0.1:${controller_port}" \
  HatEmulator__Host=127.0.0.1 \
  HatEmulator__Port="${register_port}" \
  BlueIris__BaseUrl="http://127.0.0.1:${control_port}" \
  RoofControllerSecurity__ApiKeys__0__Name=alpaca-server \
  RoofControllerSecurity__ApiKeys__0__Role=RoofAlpacaServer \
  RoofControllerSecurity__ApiKeys__0__KeySha256="$(hash_of server-key)" \
  RoofControllerSecurity__ApiKeys__1__Name=alpaca-external \
  RoofControllerSecurity__ApiKeys__1__Role=RoofAlpacaCommander \
  RoofControllerSecurity__ApiKeys__1__KeySha256="$(hash_of external-key)" \
  RoofControllerSecurity__ApiKeys__2__Name=ci-admin \
  RoofControllerSecurity__ApiKeys__2__Role=RoofAdmin \
  RoofControllerSecurity__ApiKeys__2__KeySha256="$(hash_of admin-key)" \
  RoofController__Alpaca__AllowCommands=CloseOnly \
  RoofSafety__Telescopes__0__Name=Main \
  RoofSafety__Telescopes__0__Url="http://127.0.0.1:${control_port}" \
  RoofSafety__WeatherSources__0__Name="Emulated weather" \
  RoofSafety__WeatherSources__0__Url="http://127.0.0.1:${control_port}" \
  RoofSafety__OwnAlpacaUniqueIds__0="${dome_id}" \
  RoofSafety__OwnAlpacaUniqueIds__1="${safetymonitor_id}" \
  dotnet HVO.RoofControllerV4.RPi.dll) > "${work}/controller.log" 2>&1 &
pids+=("$!")
wait_for "http://127.0.0.1:${controller_port}/health/live" "The controller"

echo "Starting the Alpaca server (${alpaca_port})"
(cd "$(output HVO.RoofControllerV4.Alpaca)" && exec env \
  Alpaca__Enabled=true \
  Alpaca__ListenAddresses__0=127.0.0.1 \
  Alpaca__Port="${alpaca_port}" \
  Alpaca__ControllerUrl="http://127.0.0.1:${controller_port}" \
  Alpaca__ControllerApiKeyFile="${private}/server-key" \
  Alpaca__DataPath="${work}/alpaca-data" \
  Alpaca__Discovery__Enabled=false \
  Alpaca__RequestsPerSecondPerAddress=10000 \
  dotnet HVO.RoofControllerV4.Alpaca.dll) > "${work}/alpaca.log" 2>&1 &
pids+=("$!")
wait_for "http://127.0.0.1:${alpaca_port}/management/apiversions" "The Alpaca server"

# The server serves the roof once the controller's first status has reached it.
deadline=$((SECONDS + 60))
until curl -fsS --max-time 2 -X PUT --data 'Connected=true&ClientID=1' "http://127.0.0.1:${alpaca_port}/api/v1/dome/0/connected" > /dev/null \
  && curl -fsS --max-time 2 "http://127.0.0.1:${alpaca_port}/api/v1/dome/0/shutterstatus?ClientID=1" | grep -q '"ErrorNumber":0'; do
  ((SECONDS > deadline)) && fail "The Alpaca server did not report the roof's status within 60 s (logs in ${work})"
  sleep 0.5
done
# And the weather: the controller has read the emulator's safety monitor, and the server's SafetyMonitor says safe.
deadline=$((SECONDS + 60))
until curl -fsS --max-time 2 -X PUT --data 'Connected=true&ClientID=1' "http://127.0.0.1:${alpaca_port}/api/v1/safetymonitor/0/connected" > /dev/null \
  && curl -fsS --max-time 2 "http://127.0.0.1:${alpaca_port}/api/v1/safetymonitor/0/issafe?ClientID=1" | grep -q '"Value":true,'; do
  ((SECONDS > deadline)) && fail "The Alpaca server's SafetyMonitor did not read the emulated weather as safe within 60 s (logs in ${work})"
  sleep 0.5
done
controller="http://127.0.0.1:${controller_port}"
alpaca="http://127.0.0.1:${alpaca_port}"
ci=(python3 "${repo}/tests/alpaca/alpaca-ci.py")

# The CI person, the chain guard, then the start-up Stop hold cleared as a person would.
"${ci[@]}" session --controller "${controller}" --admin-key "${private}/admin-key" --password "${private}/person-password" --token "${private}/session"
"${ci[@]}" guard --controller "${controller}" --alpaca "${alpaca}" --token "${private}/session"
"${ci[@]}" clear-hold --controller "${controller}" --token "${private}/session"
before=$(curl -fsS "http://127.0.0.1:${control_port}/api/emulator/status")

# The Dome's settings with this run's port and the External key as HTTP Basic (kept with the other secrets), and the
# same for SafetyMonitor 0.
python3 - "${repo}/tests/alpaca/conformu-settings.json" "${private}" "${alpaca_port}" "${private}/external-key" <<'EOF'
import json, sys
settings = json.load(open(sys.argv[1]))
assert settings["DomeOpenShutter"] is False, "DomeOpenShutter must stay false"
settings["AlpacaDevice"]["IpPort"] = int(sys.argv[3])
key = open(sys.argv[4]).read().strip()
settings["AlpacaConfiguration"].update(AccessUserName=key, AccessPassword=key)
json.dump(settings, open(f"{sys.argv[2]}/conformu-settings.json", "w"), indent=2)
settings["DeviceType"] = "SafetyMonitor"
settings["AlpacaDevice"].update(AscomDeviceType="SafetyMonitor", AscomDeviceName="HVO observatory weather", AlpacaDeviceNumber=0)
json.dump(settings, open(f"{sys.argv[2]}/conformu-settings-safetymonitor.json", "w"), indent=2)
EOF

run_conformu() {
  local command=$1 name=$2 settings=${3:-conformu-settings.json} status=0
  echo "ConformU ${CONFORMU_VERSION}: ${command} (${settings})"
  HOME="${work}/home" "${conformu_dir}/conformu" "${command}" \
    --settingsfile "${private}/${settings}" \
    --logfilename "${work}/${name}.log" \
    --resultsfile "${work}/${name}-results.json" > "${work}/${name}-console.txt" 2>&1 || status=$?
  echo "ConformU ${command} exited with ${status}"
  tail -n 25 "${work}/${name}-console.txt"
}

mkdir -p "${work}/home"
run_conformu alpacaprotocol-settings protocol
run_conformu conformance-settings conformance
run_conformu alpacaprotocol-settings safetymonitor-protocol conformu-settings-safetymonitor.json
run_conformu conformance-settings safetymonitor-conformance conformu-settings-safetymonitor.json

after=$(curl -fsS "http://127.0.0.1:${control_port}/api/emulator/status")
python3 - "${before}" "${after}" <<'EOF'
import json, sys
before, after = json.loads(sys.argv[1])["plant"], json.loads(sys.argv[2])["plant"]
moved = abs(after["positionMeters"] - before["positionMeters"])
print(f"The emulated roof: {before['positionMeters']:.4f} m before, {after['positionMeters']:.4f} m after")
if moved > 0.001 or after["relayRegister"] != 0:
    sys.exit(f"::error::The emulated roof moved ({moved:.4f} m) or a relay is on ({after['relayRegister']}): ConformU must never move it (the roof starts Closed)")
EOF

# Nothing kept may hold a run secret: ConformU's logs and results, the processes' logs.
"${ci[@]}" no-secret "${secret_args[@]}" "${work}"/*.log "${work}"/*.json "${work}"/*.txt

# The scripted Close runs, against the real processes.
"${ci[@]}" close-runs --controller "${controller}" --alpaca "${alpaca}" --emulator "http://127.0.0.1:${control_port}" \
  --token "${private}/session" --external-key "${private}/external-key"
"${ci[@]}" no-secret "${secret_args[@]}" "${work}"/*.log "${work}"/*.json "${work}"/*.txt

# The protocol check writes no results file: its log ends with its summary.
python3 "${repo}/tests/alpaca/check-conformu-results.py" \
  --expected "${repo}/tests/alpaca/conformu-expected.json" \
  "protocol=${work}/protocol.log" \
  "conformance=${work}/conformance-results.json" \
  "safetymonitor-protocol=${work}/safetymonitor-protocol.log" \
  "safetymonitor-conformance=${work}/safetymonitor-conformance-results.json"
