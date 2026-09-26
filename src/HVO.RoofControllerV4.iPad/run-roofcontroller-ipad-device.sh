#!/bin/zsh
# Builds the iPad app and runs it on a connected iPad.
#
# Usage: ./run-roofcontroller-ipad-device.sh --udid <device UDID> [--configuration Debug|Release] [-- <extra dotnet build args>]
#
# The device UDID can also be set with HVO_IPAD_DEVICE_UDID. List devices with: xcrun devicectl list devices
# Signing needs a development certificate and a provisioning profile for org.hvo.roofcontroller.v4.ipad; pass
# -p:CodesignKey=... and -p:CodesignProvision=... after "--" if automatic selection picks the wrong ones.
set -euo pipefail

SCRIPT_DIR="${0:A:h}"
REPO_ROOT="${SCRIPT_DIR:h:h}"
PROJECT="${REPO_ROOT}/src/HVO.RoofControllerV4.iPad/HVO.RoofControllerV4.iPad.csproj"
CONFIGURATION="Debug"
UDID="${HVO_IPAD_DEVICE_UDID:-}"
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
      sed -n '2,9p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "${UDID}" ]]; then
  echo "No device UDID. Pass --udid <UDID> or set HVO_IPAD_DEVICE_UDID (see: xcrun devicectl list devices)." >&2
  exit 2
fi

if [[ ! -f "${PROJECT}" ]]; then
  echo "Project not found: ${PROJECT}" >&2
  exit 1
fi

# Run from src/ so src/global.json selects the pinned .NET SDK.
cd "${REPO_ROOT}/src"
exec dotnet build "${PROJECT}" \
  -t:Run \
  -f net10.0-ios \
  -c "${CONFIGURATION}" \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:_DeviceName="${UDID}" \
  "${EXTRA_ARGS[@]}"
