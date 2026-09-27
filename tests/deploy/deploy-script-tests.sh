#!/usr/bin/env bash
# Tests for src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh. The script runs against fake `docker` and `curl`
# commands (tests/deploy/fakes) that keep the containers in a temporary state file, so no Docker, Pi or network is
# needed. Requires bash, python3 and jq. Run: tests/deploy/deploy-script-tests.sh [test-name ...]
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SCRIPT="${TESTS_DIR}/../../src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh"
KEY="deploy-test-operator-key-3f9c2a7e5b1d4c8a"
IMAGE="test/roof-controller:v4"

PASSED=0
FAILED=0
FAILURES=()
CURRENT_FAILED=false

# ---------------------------------------------------------------------------------------------------------------------
# Harness

setup() {
  WORK=$(mktemp -d)
  export FAKE_STATE_DIR="${WORK}/state"
  mkdir -p "${FAKE_STATE_DIR}" "${WORK}/bin" "${WORK}/home"
  ln -s "${TESTS_DIR}/fakes/docker" "${WORK}/bin/docker"
  ln -s "${TESTS_DIR}/fakes/curl" "${WORK}/bin/curl"
  echo '{"containers": {}}' > "${FAKE_STATE_DIR}/state.json"
  : > "${FAKE_STATE_DIR}/calls.log"
  : > "${FAKE_STATE_DIR}/remote.log"
  printf 'fake ca\n' > "${WORK}/ca.pem"
}

teardown() {
  rm -rf "${WORK}"
}

# seed_container <name> <kind> <running:true|false> [publish...]
seed_container() {
  local name=$1 kind=$2 running=$3
  shift 3
  local publish
  publish=$(printf '%s\n' "$@" | jq -R . | jq -s 'map(select(length > 0))')
  jq --arg name "${name}" --arg kind "${kind}" --argjson running "${running}" --argjson publish "${publish}" \
    '.containers[$name] = {kind: $kind, running: $running, restart: "unless-stopped", publish: $publish}' \
    "${FAKE_STATE_DIR}/state.json" > "${FAKE_STATE_DIR}/state.tmp" && mv "${FAKE_STATE_DIR}/state.tmp" "${FAKE_STATE_DIR}/state.json"
}

# Runs the deploy script with the test environment plus any NAME=value pairs before "--" and script args after it.
deploy() {
  local env_pairs=() script_args=()
  while (( $# > 0 )) && [[ "$1" != "--" ]]; do
    env_pairs+=("$1")
    shift
  done
  [[ "${1:-}" == "--" ]] && shift
  script_args=("$@")

  OUTPUT=$(env -i \
    PATH="${WORK}/bin:${PATH}" HOME="${WORK}/home" TMPDIR="${WORK}" FAKE_STATE_DIR="${FAKE_STATE_DIR}" \
    FAKE_EXPECTED_KEY="${KEY}" PI_HOST=pi.test DOCKER_CONTEXT=test-context IMAGE_TAG="${IMAGE}" ROOF_OPERATOR_API_KEY="${KEY}" \
    READY_TIMEOUT_SECONDS=1 POLL_INTERVAL_SECONDS=0.1 STOP_TIMEOUT_SECONDS=5 \
    ${env_pairs[@]+"${env_pairs[@]}"} \
    bash "${SCRIPT}" ${script_args[@]+"${script_args[@]}"} 2>&1 </dev/null)
  STATUS=$?
}

# Environment for the default HTTPS deployment.
HTTPS_ENV=(HTTPS_CERT_DIR=/etc/hvo-roof/https)

fail_test() {
  CURRENT_FAILED=true
  echo "    FAIL: $*"
}

assert_status() {
  if [[ "$1" == "0" && "${STATUS}" != "0" ]] || [[ "$1" != "0" && "${STATUS}" == "0" ]]; then
    fail_test "expected exit status $1, got ${STATUS}"
  fi
}

assert_output_contains() {
  [[ "${OUTPUT}" == *"$1"* ]] || fail_test "output does not contain: $1"
}

assert_output_not_contains() {
  [[ "${OUTPUT}" != *"$1"* ]] || fail_test "output contains: $1"
}

# container_field <name> <field>: prints the field, or "missing" when the container does not exist.
container_field() {
  jq -r --arg name "$1" --arg field "$2" \
    'if .containers[$name] then (.containers[$name][$field] | tostring) else "missing" end' "${FAKE_STATE_DIR}/state.json"
}

assert_container() {
  local name=$1 kind=$2 running=$3 restart=${4:-}
  local actual_kind actual_running
  actual_kind=$(container_field "${name}" kind)
  actual_running=$(container_field "${name}" running)
  [[ "${actual_kind}" == "${kind}" ]] || fail_test "${name}: expected kind ${kind}, got ${actual_kind}"
  if [[ "${kind}" != "missing" ]]; then
    [[ "${actual_running}" == "${running}" ]] || fail_test "${name}: expected running=${running}, got ${actual_running}"
    if [[ -n "${restart}" ]]; then
      local actual_restart
      actual_restart=$(container_field "${name}" restart)
      [[ "${actual_restart}" == "${restart}" ]] || fail_test "${name}: expected restart ${restart}, got ${actual_restart}"
    fi
  fi
}

# docker_calls <subcommand>: the matching docker calls, one JSON argv per line (the --context prefix removed).
docker_calls() {
  jq -c --arg command "$1" 'if .[0] == "--context" then .[2:] else . end | select(.[0] == $command)' \
    "${FAKE_STATE_DIR}/calls.log"
}

# Index (line number) of the first docker call whose JSON argv contains the given text, or 0.
call_index() {
  local line
  line=$(grep -nF -- "$1" "${FAKE_STATE_DIR}/calls.log" | head -n 1 | cut -d: -f1)
  echo "${line:-0}"
}

preflight_args() {
  docker_calls run | jq -c 'select(index("--rm"))' | head -n 1
}

controller_run_args() {
  docker_calls run | jq -c 'select(index("-d"))' | head -n 1
}

assert_key_never_in_argv() {
  if grep -qF -- "${KEY}" "${FAKE_STATE_DIR}/calls.log" "${FAKE_STATE_DIR}/remote.log"; then
    fail_test "the API key appeared in a docker or curl argument list"
  fi
  assert_output_not_contains "${KEY}"
}

run_test() {
  local name=$1
  CURRENT_FAILED=false
  setup
  echo "  ${name}"
  "${name}"
  if [[ "${CURRENT_FAILED}" == "true" ]]; then
    FAILED=$((FAILED + 1))
    FAILURES+=("${name}")
    echo "    --- script output ---"
    while IFS= read -r line; do
      echo "    | ${line}"
    done <<<"${OUTPUT:-}"
  else
    PASSED=$((PASSED + 1))
  fi
  teardown
}

# ---------------------------------------------------------------------------------------------------------------------
# Tests

test_https_deploy_validates_first_publishes_only_https_and_keeps_previous() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" REMOTE_CA_CERT="${WORK}/ca.pem" FAKE_REQUIRE_CACERT=true

  assert_status 0
  assert_output_contains "Deployment complete and verified at https://pi.test:8443"
  assert_container roof-controller new true unless-stopped
  assert_container roof-controller-previous old false no

  local preflight run expected_sha
  preflight=$(preflight_args)
  run=$(controller_run_args)
  [[ -n "${preflight}" ]] || fail_test "no pre-flight container was run"

  # The pre-flight runs the image's --validate-deployment with exactly the controller's environment and mounts.
  jq -e --arg image "${IMAGE}" '.[-2] == $image and .[-1] == "--validate-deployment"' <<<"${preflight}" >/dev/null \
    || fail_test "pre-flight does not run ${IMAGE} --validate-deployment: ${preflight}"
  local preflight_config run_config
  preflight_config=$(jq -c --arg image "${IMAGE}" \
    '.[(index("--rm") + 1):index($image)] | map(select(startswith("DeploymentCheck__") | not)) | del(.[-1])' <<<"${preflight}")
  run_config=$(jq -c --arg image "${IMAGE}" \
    '.[:index($image)] | . as $a | [range(0; length) as $i | select(($a[$i] == "-p") or ($i > 0 and $a[$i - 1] == "-p") | not) | $a[$i]]
     | .[(index("max-file=5") + 1):]' <<<"${run}")
  [[ "${preflight_config}" == "${run_config}" ]] \
    || fail_test "pre-flight configuration differs from the controller's:"$'\n'"      pre-flight: ${preflight_config}"$'\n'"      controller: ${run_config}"

  expected_sha=$(printf '%s' "${KEY}" | sha256sum | cut -d ' ' -f 1)
  jq -e --arg env "DeploymentCheck__DeployKeySha256=${expected_sha}" 'index($env)' <<<"${preflight}" >/dev/null \
    || fail_test "pre-flight is not given the SHA-256 of the deploy key"

  # HTTPS only: 8443 published, plain HTTP on loopback inside the container, RequireHttps on, certificate mounted.
  jq -e 'index("8443:8443") and (index("8080:8080") | not)' <<<"${run}" >/dev/null \
    || fail_test "expected only 8443 published: ${run}"
  for expected in "ASPNETCORE_URLS=http://localhost:8080;https://+:8443" "RoofControllerSecurity__RequireHttps=true" \
      "Kestrel__Certificates__Default__Path=/https/roof-controller.pfx" \
      "type=bind,src=/etc/hvo-roof/https,dst=/https,readonly" "type=bind,src=/etc/hvo-roof/secrets,dst=/run/secrets,readonly"; do
    jq -e --arg value "${expected}" 'index($value)' <<<"${run}" >/dev/null || fail_test "controller run lacks ${expected}"
  done

  # Order: pre-flight, verified Stop on the old controller, docker stop, rename, then the new controller.
  local i_preflight i_stop_request i_stop i_rename i_run
  i_preflight=$(call_index '"--validate-deployment"')
  i_stop_request=$(call_index '"http://localhost:8080/api/v4.0/RoofControl/Stop"')
  i_stop=$(call_index '"stop", "-t"')
  i_rename=$(call_index '"rename", "roof-controller", "roof-controller-previous"')
  i_run=$(call_index '"run", "-d"')
  if ! (( i_preflight > 0 && i_preflight < i_stop_request && i_stop_request < i_stop && i_stop < i_rename && i_rename < i_run )); then
    fail_test "unexpected order: preflight=${i_preflight} stop-request=${i_stop_request} stop=${i_stop} rename=${i_rename} run=${i_run}"
  fi

  # Remote check from this machine: HTTPS with the CA, authenticated Status then Stop.
  jq -e -s --arg ca "${WORK}/ca.pem" \
    '(map(.[-1]) == ["https://pi.test:8443/api/v4.0/RoofControl/Status", "https://pi.test:8443/api/v4.0/RoofControl/Stop"])
     and all(.[]; index("--cacert") != null and .[index("--cacert") + 1] == $ca)' \
    "${FAKE_STATE_DIR}/remote.log" >/dev/null || fail_test "unexpected remote calls: $(cat "${FAKE_STATE_DIR}/remote.log")"

  assert_key_never_in_argv
}

test_failed_preflight_leaves_running_controller_untouched() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_PREFLIGHT_EXIT=1

  assert_status 1
  assert_output_contains "PROBLEM: fake problem"
  assert_output_contains "The running controller was not touched."
  assert_container roof-controller old true unless-stopped
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was stopped, renamed or replaced"
  [[ "$(call_index '/Stop"')" == "0" ]] || fail_test "a Stop was requested before the pre-flight passed"
}

test_requires_certificate_or_explicit_insecure_opt_in() {
  seed_container roof-controller old true 8080:8080
  deploy

  assert_status 1
  assert_output_contains "Set HTTPS_CERT_DIR"
  [[ -z "$(docker_calls run)$(docker_calls stop)" ]] || fail_test "docker run or stop was called"
  assert_container roof-controller old true
}

test_insecure_http_mode_publishes_8080_with_https_disabled() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true

  assert_status 0
  local run
  run=$(controller_run_args)
  jq -e 'index("8080:8080") and (index("8443:8443") | not)
         and index("ASPNETCORE_URLS=http://+:8080") and index("RoofControllerSecurity__RequireHttps=false")' <<<"${run}" >/dev/null \
    || fail_test "unexpected insecure run arguments: ${run}"
  jq -e -s 'map(.[-1]) == ["http://pi.test:8080/api/v4.0/RoofControl/Status", "http://pi.test:8080/api/v4.0/RoofControl/Stop"]
            and all(.[]; index("--cacert") == null)' "${FAKE_STATE_DIR}/remote.log" >/dev/null \
    || fail_test "unexpected remote calls: $(cat "${FAKE_STATE_DIR}/remote.log")"
  assert_container roof-controller new true
  assert_container roof-controller-previous old false no
}

test_new_controller_never_ready_rolls_back_to_previous() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_READY=false

  assert_status 1
  assert_output_contains "did not become ready within 1s"
  assert_output_contains "fake controller log line"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
}

test_remote_https_rejection_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_REMOTE_STATUS_CODE=403

  assert_status 1
  assert_output_contains "returned HTTP 403 to this machine"
  assert_output_contains "Rolled back"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
}

test_remote_connection_failure_rolls_back() {
  seed_container roof-controller old true 8080:8080
  # The certificate is not trusted by this machine and no REMOTE_CA_CERT is given.
  deploy "${HTTPS_ENV[@]}" FAKE_REQUIRE_CACERT=true

  assert_status 1
  assert_output_contains "failed from this machine"
  assert_output_contains "REMOTE_CA_CERT"
  assert_container roof-controller old true unless-stopped
}

test_unverified_remote_stop_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_REMOTE_STOP=unverified

  assert_status 1
  assert_output_contains "did not return a verified stop"
  assert_container roof-controller old true unless-stopped
}

test_rejected_key_inside_new_container_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_STATUS_CODE=401

  assert_status 1
  assert_output_contains "returned HTTP 401"
  assert_container roof-controller old true unless-stopped
  [[ ! -s "${FAKE_STATE_DIR}/remote.log" ]] || fail_test "the remote check ran after the in-container check failed"
}

test_docker_run_failure_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_RUN_FAIL=true

  assert_status 1
  assert_output_contains "docker run failed"
  assert_container roof-controller old true unless-stopped
}

test_unverified_stop_aborts_without_stopping() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified

  assert_status 1
  assert_output_contains "The roof stop could not be verified"
  assert_container roof-controller old true unless-stopped
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was stopped or replaced"
}

test_failed_docker_stop_does_not_remove_old_controller() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_STOP_FAIL=true

  assert_status 1
  assert_output_contains "Could not stop roof-controller; it was not replaced."
  assert_container roof-controller old true
  [[ -z "$(docker_calls rm)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was removed, renamed or replaced"
}

test_running_previous_container_aborts_before_stop() {
  seed_container roof-controller old true 8080:8080
  seed_container roof-controller-previous older true
  deploy "${HTTPS_ENV[@]}"

  assert_status 1
  assert_output_contains "roof-controller-previous is running"
  assert_container roof-controller old true
  [[ -z "$(docker_calls stop)" ]] || fail_test "a container was stopped"
}

test_older_previous_is_replaced_after_successful_deploy() {
  seed_container roof-controller old true 8080:8080
  seed_container roof-controller-previous older false
  deploy "${HTTPS_ENV[@]}"

  assert_status 0
  assert_container roof-controller new true
  assert_container roof-controller-previous old false no
}

test_stopped_old_controller_is_restored_stopped() {
  seed_container roof-controller old false 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_READY=false

  assert_status 1
  assert_output_contains "stopped as it was before the deploy"
  assert_container roof-controller old false unless-stopped
  [[ -z "$(docker_calls start)" ]] || fail_test "the previously stopped controller was started"
}

test_failure_without_previous_controller_reports_down() {
  # A -previous left from an earlier deploy is not touched when there is no current controller.
  seed_container roof-controller-previous older false
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_READY=false

  assert_status 1
  assert_output_contains "There was no previous controller to restore"
  assert_container roof-controller missing false
  assert_container roof-controller-previous older false
}

test_skip_remote_check_warns_and_does_not_call_remote() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" SKIP_REMOTE_CHECK=true

  assert_status 0
  assert_output_contains "WARNING: SKIP_REMOTE_CHECK=true"
  [[ ! -s "${FAKE_STATE_DIR}/remote.log" ]] || fail_test "the remote URL was called"
}

test_rollback_swaps_current_and_previous_and_back() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" -- --rollback

  assert_status 0
  assert_output_contains "Rolled back. roof-controller is verified at https://pi.test:8443"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no
  [[ -z "$(docker_calls buildx)$(docker_calls run)" ]] || fail_test "--rollback built or ran an image"
  [[ "$(call_index '"http://localhost:8080/api/v4.0/RoofControl/Stop"')" != "0" ]] || fail_test "no verified Stop before the swap"

  : > "${FAKE_STATE_DIR}/remote.log"
  deploy "${HTTPS_ENV[@]}" -- --rollback
  assert_status 0
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false no
  assert_key_never_in_argv
}

test_rollback_without_previous_fails() {
  seed_container roof-controller current true 8443:8443
  deploy "${HTTPS_ENV[@]}" -- --rollback

  assert_status 1
  assert_output_contains "There is no stopped roof-controller-previous to roll back to."
  assert_container roof-controller current true
}

test_dry_run_changes_nothing() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" -- --dry-run

  assert_status 0
  assert_output_contains "[dry-run] relayRegisterState=Verified relayRegisterMask=0 commandedMotion=None"
  assert_output_contains "GET Status at https://pi.test:8443 from this machine -> HTTP 200"
  [[ -z "$(docker_calls run)$(docker_calls stop)$(docker_calls rename)$(docker_calls buildx)" ]] || fail_test "dry run changed something"
  assert_container roof-controller old true
}

test_world_readable_key_file_is_rejected() {
  printf '%s\n' "${KEY}" > "${WORK}/operator.key"
  chmod 644 "${WORK}/operator.key"
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" ROOF_OPERATOR_API_KEY= OPERATOR_KEY_FILE="${WORK}/operator.key"

  assert_status 1
  assert_output_contains "is readable by group/others (mode 644)"
}

test_key_file_is_used_and_never_passed_as_argument() {
  printf '%s\n' "${KEY}" > "${WORK}/operator.key"
  chmod 600 "${WORK}/operator.key"
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" ROOF_OPERATOR_API_KEY= OPERATOR_KEY_FILE="${WORK}/operator.key"

  assert_status 0
  assert_key_never_in_argv
}

# ---------------------------------------------------------------------------------------------------------------------

for tool in python3 jq sha256sum; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "deploy-script-tests: ${tool} is required" >&2; exit 2; }
done

if (( $# > 0 )); then
  tests=("$@")
else
  mapfile -t tests < <(declare -F | awk '{print $3}' | grep '^test_')
fi

echo "deploy-roofcontroller-rpi.sh tests"
for test in "${tests[@]}"; do
  run_test "${test}"
done

echo
echo "${PASSED} passed, ${FAILED} failed"
if (( FAILED > 0 )); then
  printf '  %s\n' "${FAILURES[@]}"
  exit 1
fi
