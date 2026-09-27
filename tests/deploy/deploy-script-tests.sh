#!/usr/bin/env bash
# Tests for src/HVO.RoofControllerV4.RPi/deploy-roofcontroller-rpi.sh. The script runs against fake `docker` and `curl`
# commands (tests/deploy/fakes) that keep the containers in a temporary state file, so no Docker, Pi or network is
# needed. Requires bash (3.2 or later), python3, jq and sha256sum or shasum.
# Run: tests/deploy/deploy-script-tests.sh [test-name ...]
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

# seed_container <name> <kind> <true|false|paused|restarting|created> [publish...]. paused and restarting count as
# running (as in Docker), created as not running; each container gets a random 64-hex ID.
seed_container() {
  local name=$1 kind=$2 running=$3 docker_state="" id publish
  shift 3
  case "${running}" in
    paused|restarting) docker_state=${running}; running=true ;;
    created) docker_state=${running}; running=false ;;
  esac
  id=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
  publish=$(printf '%s\n' "$@" | jq -R . | jq -s 'map(select(length > 0))')
  jq --arg name "${name}" --arg kind "${kind}" --argjson running "${running}" --argjson publish "${publish}" \
    --arg state "${docker_state}" --arg id "${id}" \
    '.containers[$name] = ({kind: $kind, running: $running, restart: "unless-stopped", publish: $publish, id: $id}
                           + (if $state == "" then {} else {state: $state} end))' \
    "${FAKE_STATE_DIR}/state.json" > "${FAKE_STATE_DIR}/state.tmp" && mv "${FAKE_STATE_DIR}/state.tmp" "${FAKE_STATE_DIR}/state.json"
}

# build_command [NAME=value...] [-- script args...]: sets RUN_CMD to run the deploy script with the test environment
# plus the given variables. The script runs in ${WORK} and records its PID in script.pid (for FAKE_SIGNAL_ON).
build_command() {
  local env_pairs=()
  while (( $# > 0 )) && [[ "$1" != "--" ]]; do
    env_pairs+=("$1")
    shift
  done
  [[ "${1:-}" == "--" ]] && shift

  # shellcheck disable=SC2016 # expanded by the inner shell
  RUN_CMD=(env -i
    PATH="${WORK}/bin:${PATH}" HOME="${WORK}/home" TMPDIR="${WORK}" FAKE_STATE_DIR="${FAKE_STATE_DIR}"
    FAKE_EXPECTED_KEY="${KEY}" PI_HOST=pi.test DOCKER_CONTEXT=test-context IMAGE_TAG="${IMAGE}" ROOF_OPERATOR_API_KEY="${KEY}"
    READY_TIMEOUT_SECONDS=1 POLL_INTERVAL_SECONDS=0.1 STOP_TIMEOUT_SECONDS=5
    ${env_pairs[@]+"${env_pairs[@]}"}
    bash -c 'cd "$1" && echo "$$" > "${FAKE_STATE_DIR}/script.pid" && shift && exec bash "$@"'
    deploy-test "${WORK}" "${SCRIPT}" "$@")
}

# Runs the deploy script with the test environment plus any NAME=value pairs before "--" and script args after it.
# Sets OUTPUT (stdout and stderr) and STATUS.
deploy() {
  build_command "$@"
  OUTPUT=$("${RUN_CMD[@]}" 2>&1 </dev/null)
  STATUS=$?
}

# As deploy, with the script's stderr closed (writes to it fail with EBADF). OUTPUT is stdout only.
deploy_without_stderr() {
  build_command "$@"
  OUTPUT=$("${RUN_CMD[@]}" 2>&- </dev/null)
  STATUS=$?
}

# As deploy, on a pseudo-terminal (tests/deploy/on-terminal) whose reader process (pid in output.pid) the fake docker
# can kill (FAKE_SIGNAL_KILLS_OUTPUT), as when the terminal window or the SSH session goes away: the script gets SIGHUP
# and its later writes to the terminal fail with EIO. OUTPUT is what reached the terminal before that.
deploy_on_terminal() {
  build_command "$@"
  STATUS=0
  "${TESTS_DIR}/on-terminal" "${FAKE_STATE_DIR}/output.pid" "${WORK}/output.log" "${RUN_CMD[@]}" || STATUS=$?
  OUTPUT=$(tr -d '\r' < "${WORK}/output.log")
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

assert_status_is() {
  [[ "${STATUS}" == "$1" ]] || fail_test "expected exit status $1, got ${STATUS}"
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

# Lets the fake daemon finish any stop it was still completing after its client was cut off, as time would.
settle_containers() {
  python3 - "${FAKE_STATE_DIR}/state.json" <<'PY'
import json, sys, time
path = sys.argv[1]
with open(path) as f:
    state = json.load(f)
until = max([c.get("stopping_until") or 0 for c in state["containers"].values()] + [0])
time.sleep(max(0.0, until - time.time()))
for c in state["containers"].values():
    if c.pop("stopping_until", None) is not None:
        c["running"] = False
        c.pop("state", None)
with open(path, "w") as f:
    json.dump(state, f, indent=2)
PY
}

# kind_count <kind>: how many containers of that kind exist.
kind_count() {
  jq --arg kind "$1" '[.containers[] | select(.kind == $kind)] | length' "${FAKE_STATE_DIR}/state.json"
}

assert_no_docker_calls() {
  [[ ! -s "${FAKE_STATE_DIR}/calls.log" ]] || fail_test "$1: docker was called: $(head -n 1 "${FAKE_STATE_DIR}/calls.log")"
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

sha256_hex() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  else
    shasum -a 256 | cut -d ' ' -f 1
  fi
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
  OUTPUT=""
  echo "  ${name}"
  if ! declare -F "${name}" >/dev/null; then
    echo "    FAIL: no such test"
    FAILED=$((FAILED + 1))
    FAILURES+=("${name}")
    return
  fi
  setup
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

  expected_sha=$(printf '%s' "${KEY}" | sha256_hex)
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
  seed_container roof-controller-previous older false
  deploy "${HTTPS_ENV[@]}" FAKE_STOP_FAIL=true

  assert_status 1
  assert_output_contains "Could not stop roof-controller; it was not replaced"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous older false
  [[ -z "$(docker_calls rm)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "a container was removed, renamed or replaced"
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
  local older_id
  older_id=$(container_field roof-controller-previous id)
  deploy "${HTTPS_ENV[@]}"

  assert_status 0
  assert_container roof-controller new true
  assert_container roof-controller-previous old false no
  [[ "$(kind_count older)" == "0" ]] || fail_test "the older roof-controller-previous was kept"

  # The older -previous is removed (by ID) only after the old controller has stopped, just before the rename.
  local i_stop i_rm i_rename
  i_stop=$(call_index '"stop", "-t"')
  i_rm=$(call_index "\"rm\", \"${older_id}\"")
  i_rename=$(call_index '"rename", "roof-controller", "roof-controller-previous"')
  if ! (( i_stop > 0 && i_stop < i_rm && i_rm < i_rename )); then
    fail_test "unexpected order: stop=${i_stop} rm=${i_rm} rename=${i_rename}"
  fi
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

# --- Settings (validated before any Docker call) ---------------------------------------------------------------------

test_malformed_numeric_settings_fail_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local setting
  for setting in READY_TIMEOUT_SECONDS=90.5 READY_TIMEOUT_SECONDS=0 READY_TIMEOUT_SECONDS=1234567890123 \
      STOP_TIMEOUT_SECONDS=120s STOP_TIMEOUT_SECONDS=-5 HOST_PORT=http HTTPS_HOST_PORT=70000 \
      POLL_INTERVAL_SECONDS=1.2.3 POLL_INTERVAL_SECONDS=. POLL_INTERVAL_SECONDS=1s; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "${setting}"
    assert_status 1
    assert_output_contains "${setting%%=*} must be"
    assert_no_docker_calls "${setting}"
  done
  assert_container roof-controller old true unless-stopped
}

test_malformed_setting_with_rollback_does_not_build() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" READY_TIMEOUT_SECONDS=2m -- --rollback

  assert_status 1
  assert_output_contains "READY_TIMEOUT_SECONDS must be a whole number"
  assert_no_docker_calls "--rollback"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false
}

test_numbers_with_leading_zeros_are_decimal() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" STOP_TIMEOUT_SECONDS=010 READY_TIMEOUT_SECONDS=09 HTTPS_HOST_PORT=08443

  assert_status 0
  assert_output_contains "Waiting up to 9s"
  assert_output_contains "verified at https://pi.test:8443"
  docker_calls stop | jq -e -s '.[0] | .[index("-t") + 1] == "10"' >/dev/null \
    || fail_test "docker stop was not given -t 10: $(docker_calls stop)"
  controller_run_args | jq -e '.[index("--stop-timeout") + 1] == "10" and index("8443:8443")' >/dev/null \
    || fail_test "unexpected run arguments: $(controller_run_args)"
}

test_rollback_path_cannot_fall_through_into_build() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  # An expansion error in the rollback block (here injected into echo) makes bash abandon the whole block without
  # tripping set -e, after the current controller has been stopped. The script must not go on to build and deploy.
  # shellcheck disable=SC2016 # a function definition for the script's environment
  deploy "${HTTPS_ENV[@]}" \
    'BASH_FUNC_echo%%=() { if [[ "$*" == "[rollback] Swapping"* ]]; then local x=$((1/0)); fi; builtin echo "$@"; }' \
    -- --rollback

  assert_status 1
  assert_output_contains "internal error: the --rollback path did not finish; nothing was built or deployed."
  assert_output_contains "Undone: the original controller is running and ready as roof-controller"
  [[ -z "$(docker_calls buildx)$(docker_calls load)$(docker_calls run)" ]] || fail_test "the rollback went on into a build or deploy"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false
}

test_extra_docker_args_cannot_override_name_restart_ports_or_stop() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in "--name other" "--name=other" "-d" "--detach" "--detach=true" "--rm" "--restart always" \
      "--restart=always" "--cidfile /tmp/cid" "-p 80:8080" "-p8080:8080" "--publish 80:8080" "--publish=80:8080" \
      "-P" "--publish-all" "-itd" "-tp 80:8080" "--stop-timeout 1" "--stop-timeout=1" "--stop-signal SIGKILL" \
      "--stop-signal=KILL"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--cpus 2 ${value}"
    assert_status 1
    assert_output_contains "EXTRA_DOCKER_ARGS must not contain '${value%% *}'"
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped
}

test_extra_docker_args_are_passed_without_glob_expansion() {
  seed_container roof-controller old true 8443:8443
  : > "${WORK}/GLOB=expanded"
  deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--cpus 2   --env GLOB=*"

  assert_status 0
  local args
  for args in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e '(.[index("--cpus") + 1] == "2") and (.[index("GLOB=*") - 1] == "--env") and (index("GLOB=expanded") | not)' \
      <<<"${args}" >/dev/null || fail_test "EXTRA_DOCKER_ARGS not passed as given: ${args}"
  done
}

test_rollback_requires_certificate_or_explicit_insecure_opt_in() {
  seed_container roof-controller current true 8080:8080
  seed_container roof-controller-previous old false 8080:8080
  deploy -- --rollback

  assert_status 1
  assert_output_contains "Set HTTPS_CERT_DIR"
  assert_no_docker_calls "--rollback without HTTPS"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false

  deploy ALLOW_INSECURE_HTTP=true -- --rollback
  assert_status 0
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no
}

# --- Container state (fail closed) ------------------------------------------------------------------------------------

test_docker_state_query_failure_fails_closed() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL="ps -a --no-trunc@1"

  assert_status 1
  assert_output_contains "Could not read the state of roof-controller from Docker"
  assert_output_contains "Nothing was changed"
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(docker_calls rm)$(docker_calls buildx)$(docker_calls run)" ]] \
    || fail_test "the script went on after the state query failed"
  assert_container roof-controller old true unless-stopped
}

test_state_query_failure_after_preflight_changes_nothing() {
  seed_container roof-controller old true 8443:8443
  # Queries 1 and 2 read roof-controller and roof-controller-previous; query 3 reads them again after the pre-flight.
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL="ps -a --no-trunc@3"

  assert_status 1
  assert_output_contains "Could not read the state of roof-controller from Docker"
  [[ -n "$(preflight_args)" ]] || fail_test "the pre-flight did not run"
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was stopped or replaced"
  [[ "$(call_index '/Stop"')" == "0" ]] || fail_test "a Stop was requested"
  assert_container roof-controller old true unless-stopped
}

test_paused_controller_is_not_treated_as_missing() {
  seed_container roof-controller old paused 8443:8443
  deploy "${HTTPS_ENV[@]}"

  assert_status 1
  assert_output_contains "Existing container roof-controller: running"
  assert_output_contains "The roof stop could not be verified"
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was stopped or replaced"
  assert_container roof-controller old true unless-stopped
}

test_restarting_previous_aborts_before_stop() {
  seed_container roof-controller old true 8443:8443
  seed_container roof-controller-previous older restarting
  deploy "${HTTPS_ENV[@]}"

  assert_status 1
  assert_output_contains "roof-controller-previous is running: two controllers must never share the HAT"
  [[ -z "$(docker_calls stop)$(docker_calls rm)$(docker_calls buildx)" ]] || fail_test "a container was stopped or removed, or an image built"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous older true
}

test_running_previous_without_current_controller_aborts() {
  seed_container roof-controller-previous older true 8443:8443
  deploy "${HTTPS_ENV[@]}"

  assert_status 1
  assert_output_contains "roof-controller-previous is running"
  [[ -z "$(docker_calls run)$(docker_calls buildx)" ]] || fail_test "a second controller was built or started"
  assert_container roof-controller missing false
}

test_failed_run_that_left_a_created_container_is_cleaned_up() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_RUN_FAIL=created

  assert_status 1
  assert_output_contains "docker run failed for the new controller"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the container left by the failed run was not removed"
  # Removed by the ID docker wrote to the --cidfile, not by name.
  jq -e 'index("--cidfile")' <<<"$(controller_run_args)" >/dev/null || fail_test "docker run was not given a --cidfile"
  docker_calls rm | jq -e -s 'length == 1 and (.[0][-1] | test("^[0-9a-f]{64}$"))' >/dev/null \
    || fail_test "unexpected rm calls: $(docker_calls rm)"
}

test_restore_never_touches_a_container_it_did_not_create() {
  seed_container roof-controller old true 8443:8443
  # Another client creates a container named roof-controller between the rename and docker run.
  deploy "${HTTPS_ENV[@]}" FAKE_RUN_FAIL=foreign

  assert_status 1
  assert_output_contains "roof-controller is taken by a container this run did not create"
  assert_output_contains "kept, stopped, as roof-controller-previous"
  assert_container roof-controller foreign true always
  assert_container roof-controller-previous old false
  [[ -z "$(docker_calls rm)" ]] || fail_test "a container was removed: $(docker_calls rm)"
  [[ "$(docker_calls stop | wc -l | tr -d ' ')" == "1" ]] || fail_test "unexpected stop calls: $(docker_calls stop)"
}

# --- Restore under failure and interruption ---------------------------------------------------------------------------

test_restore_completes_when_stderr_is_closed() {
  seed_container roof-controller old true 8443:8443
  deploy_without_stderr "${HTTPS_ENV[@]}" FAKE_NEW_READY=false

  assert_status 1
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
}

test_hangup_with_terminal_gone_still_restores_previous() {
  seed_container roof-controller old true 8443:8443
  # The terminal goes away right after the old controller was renamed: SIGHUP, and every later write fails with EIO.
  deploy_on_terminal "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON="update --restart no roof-controller-previous" FAKE_SIGNAL=HUP \
    FAKE_SIGNAL_KILLS_OUTPUT=true

  assert_status_is 129
  assert_output_contains "Keeping the old controller as roof-controller-previous"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ -z "$(controller_run_args)" ]] || fail_test "the new controller was started after the hangup"
}

test_hangup_while_verifying_removes_new_controller() {
  seed_container roof-controller old true 8443:8443
  deploy_on_terminal "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON=/health/ready FAKE_SIGNAL=HUP FAKE_SIGNAL_KILLS_OUTPUT=true

  assert_status_is 129
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
}

test_interrupt_during_docker_stop_restarts_old_controller() {
  seed_container roof-controller old true 8443:8443
  seed_container roof-controller-previous older false
  # A Ctrl-C at the terminal reaches the whole process group. A docker stop cut short by it would leave the daemon
  # finishing the stop while the restore "started" the container that was still running.
  deploy "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON="stop -t" FAKE_SIGNAL=INT FAKE_SIGNAL_GROUP=true
  settle_containers

  assert_status_is 130
  assert_output_contains "the script stopped unexpectedly (exit 130) while switching controllers"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous older false
  [[ -z "$(controller_run_args)$(docker_calls rm)" ]] || fail_test "the deploy went on after the interrupt"
}

test_second_interrupt_does_not_cut_the_restore_short() {
  seed_container roof-controller old true 8443:8443
  local old_id
  old_id=$(container_field roof-controller id)
  # Ctrl-C while the new controller is verified, and again while the restore starts the old controller.
  deploy "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON="/health/ready;start ${old_id}" FAKE_SIGNAL=INT FAKE_SIGNAL_GROUP=true
  settle_containers

  assert_status_is 130
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
}

test_restore_waits_for_a_stop_the_daemon_is_still_finishing() {
  seed_container roof-controller old true 8443:8443
  # The connection to the Pi drops during docker stop: the client fails, the daemon completes the stop a moment later.
  deploy "${HTTPS_ENV[@]}" FAKE_STOP_CUT_OFF=true
  settle_containers

  assert_status 1
  assert_output_contains "Could not stop roof-controller; it was not replaced"
  assert_output_contains "The original controller is still running; stopping it fully"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  [[ -z "$(controller_run_args)" ]] || fail_test "the new controller was started"
}

test_interrupt_after_rename_restores_old_controller() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON="rename roof-controller roof-controller-previous" FAKE_SIGNAL=TERM

  assert_status_is 143
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ -z "$(controller_run_args)" ]] || fail_test "the new controller was started after the interrupt"
}

test_stop_gate_abort_keeps_older_previous() {
  seed_container roof-controller old true 8443:8443
  seed_container roof-controller-previous older false
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified

  assert_status 1
  assert_output_contains "The roof stop could not be verified"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous older false
  [[ -z "$(docker_calls rm)$(docker_calls stop)" ]] || fail_test "a container was stopped or removed"
}

# --- Rollback recovery ------------------------------------------------------------------------------------------------

test_rollback_refuses_when_swap_container_exists() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  seed_container roof-controller-swap older false 8443:8443
  deploy "${HTTPS_ENV[@]}" -- --rollback

  assert_status 1
  assert_output_contains "roof-controller-swap exists"
  assert_output_contains "docker rename roof-controller-swap <name>"
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(docker_calls start)" ]] || fail_test "a container was changed"
  [[ "$(call_index '/Stop"')" == "0" ]] || fail_test "a Stop was requested"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false
  assert_container roof-controller-swap older false
}

test_rollback_failed_rename_restores_original() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL="rename roof-controller-previous roof-controller" -- --rollback

  assert_status 1
  assert_output_contains "Rollback failed (Could not rename roof-controller-previous to roof-controller)"
  assert_output_contains "Undone: the original controller is running and ready as roof-controller"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false no
  assert_container roof-controller-swap missing false
}

test_rollback_failed_start_restores_original() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL="start roof-controller@1" -- --rollback

  assert_status 1
  assert_output_contains "Could not start the rolled-back controller roof-controller"
  assert_output_contains "Undone: the original controller is running and ready as roof-controller"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false no
  assert_container roof-controller-swap missing false
}

test_interrupted_rollback_is_undone() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_SIGNAL_ON="update --restart no roof-controller-swap" FAKE_SIGNAL=TERM -- --rollback

  assert_status_is 143
  assert_output_contains "Undone: the original controller is running and ready as roof-controller"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false no
  assert_container roof-controller-swap missing false
}

test_rollback_without_current_controller_is_undone_on_failed_start() {
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL="start roof-controller" -- --rollback

  assert_status 1
  assert_output_contains "Undone: roof-controller-previous is back, stopped"
  assert_container roof-controller missing false
  assert_container roof-controller-previous old false no
}

# --- Harness ----------------------------------------------------------------------------------------------------------

test_unknown_test_name_counts_as_failure() {
  OUTPUT=$(bash "${TESTS_DIR}/deploy-script-tests.sh" test_no_such_test 2>&1)
  STATUS=$?

  assert_status 1
  assert_output_contains "FAIL: no such test"
  assert_output_contains "0 passed, 1 failed"
}

# ---------------------------------------------------------------------------------------------------------------------

for tool in python3 jq; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "deploy-script-tests: ${tool} is required" >&2; exit 2; }
done
if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
  echo "deploy-script-tests: sha256sum or shasum is required" >&2
  exit 2
fi

tests=()
if (( $# > 0 )); then
  tests=("$@")
else
  while IFS= read -r test; do
    tests+=("${test}")
  done < <(declare -F | awk '{print $3}' | grep '^test_')
fi

echo "deploy-roofcontroller-rpi.sh tests"
for test in ${tests[@]+"${tests[@]}"}; do
  run_test "${test}"
done

echo
echo "${PASSED} passed, ${FAILED} failed"
if (( FAILED > 0 )); then
  printf '  %s\n' "${FAILURES[@]}"
  exit 1
fi
