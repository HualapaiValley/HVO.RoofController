#!/bin/zsh
set -euo pipefail

# Shim: moved to src/HVO.iOS/scripts/run-roofcontroller-ipad-sim.sh
SCRIPT_DIR="${0:A:h}"
REPO_ROOT="${SCRIPT_DIR}/../../.."
NEW_SCRIPT="${REPO_ROOT}/src/HVO.iOS/scripts/run-roofcontroller-ipad-sim.sh"
echo "[shim] Delegating to ${NEW_SCRIPT}" >&2
exec "${NEW_SCRIPT}" "$@"
