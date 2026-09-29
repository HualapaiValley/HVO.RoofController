#!/usr/bin/env bash
# The container's health check (the image's HEALTHCHECK and the Compose health checks). Healthy exactly when the
# controller reports ready (/health/ready). The web UI's liveness and the supervisor's view of both processes are
# printed on the same line (docker inspect shows the output of the last checks), but they never change the result: a
# web UI failure never hides the controller's state, and a web UI that is down does not make the container unhealthy.
# The two requests run at once, so the check takes at most 4 s: within the Compose files' 5 s timeout (the image's is
# 10 s), even when both hang.
set -uo pipefail

CONTROLLER_URL=${HVO_HEALTH_CONTROLLER_URL:-http://localhost:8080/health/ready}
STATE_FILE="${HVO_SUPERVISOR_RUN_DIR:-/run/hvo-roof}/supervisor.json"

# The web UI's liveness URL on loopback, from the first of its URLs (RoofWeb__Urls, default http://+:8088).
ui_live_url() {
  local first=${RoofWeb__Urls:-http://+:8088} scheme address port
  first=${first%%;*}
  scheme=${first%%://*}
  address=${first#*://}
  address=${address%%/*}
  if [[ "${address}" == *:* && "${address}" != *']' ]]; then
    port=${address##*:}
  elif [[ "${scheme}" == https ]]; then
    port=443
  else
    port=80
  fi
  printf '%s://localhost:%s/health/live' "${scheme}" "${port}"
}

# The state of $1 (controller or ui) in the supervisor's state file, or "unknown".
supervised_state() {
  local found
  found=$(grep -o "\"$1\":{\"state\":\"[a-z-]*\"" "${STATE_FILE}" 2>/dev/null | head -n 1)
  found=${found##*:\"}
  printf '%s' "${found%\"}"
  [[ -n "${found}" ]] || printf 'unknown'
}

# The web UI's request runs in the background while the controller's runs; reading its answer waits for it.
exec 3< <(curl -sk -o /dev/null -w '%{http_code}' --max-time 3 "$(ui_live_url)" 2>/dev/null)
controller_code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "${CONTROLLER_URL}" 2>/dev/null)
ui_code=$(cat <&3)
exec 3<&-

if [[ "${controller_code}" == 200 ]]; then
  controller="ready"
else
  controller="NOT READY (HTTP ${controller_code:-000})"
fi
if [[ "${ui_code}" == 200 ]]; then
  ui="live"
else
  ui="DOWN (HTTP ${ui_code:-000})"
fi

printf 'controller: %s; web UI: %s; supervisor: controller %s, web UI %s\n' \
  "${controller}" "${ui}" "$(supervised_state controller)" "$(supervised_state ui)"
[[ "${controller_code}" == 200 ]]
