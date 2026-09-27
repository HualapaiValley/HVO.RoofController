#!/bin/zsh
# Builds the iPad app and runs it on an iOS simulator.
#
# Usage: ./run-roofcontroller-ipad-sim.sh [--configuration Debug|Release] [--udid <simulator UDID>] [-- <extra dotnet build args>]
#
# The simulator UDID defaults to HVO_IPAD_SIM_UDID, or the observatory development iPad simulator.
# List simulators with: xcrun simctl list devices available
set -euo pipefail

SCRIPT_DIR="${0:A:h}"
REPO_ROOT="${SCRIPT_DIR:h:h}"
PROJECT="${REPO_ROOT}/src/HVO.RoofControllerV4.iPad/HVO.RoofControllerV4.iPad.csproj"
CONFIGURATION="Debug"
UDID="${HVO_IPAD_SIM_UDID:-F878E277-60EC-43CF-90EC-B1C9050549E6}"
EXTRA_ARGS=()

while (( $# > 0 )); do
  case "$1" in
    -c|--configuration)
      CONFIGURATION="${2:?--configuration needs a value}"
      shift 2
      ;;
    --udid)
      UDID="${2:?--udid needs a value}"
      shift 2
      ;;
    --)
      shift
      EXTRA_ARGS=("$@")
      break
      ;;
    -h|--help)
      sed -n '2,7p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
done

if [[ ! -f "${PROJECT}" ]]; then
  echo "Project not found: ${PROJECT}" >&2
  exit 1
fi

# Boot the simulator if needed; "already booted" is not an error.
xcrun simctl boot "${UDID}" 2>/dev/null || true
open -a Simulator --args -CurrentDeviceUDID "${UDID}" || true

# Run from src/ so src/global.json selects the pinned .NET SDK.
cd "${REPO_ROOT}/src"
exec dotnet build "${PROJECT}" \
  -t:Run \
  -f net10.0-ios \
  -c "${CONFIGURATION}" \
  -p:_DeviceName=:v2:udid="${UDID}" \
  "${EXTRA_ARGS[@]}"
