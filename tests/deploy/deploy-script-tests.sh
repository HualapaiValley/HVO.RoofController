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
# A released image (pull mode): the reference with its digest, as a release lists it.
DIGEST="sha256:20533a0149c42d4e814d2bb9c4ead07fc857b51dbf2a4f677240e32829195fc0"
OTHER_DIGEST="sha256:f41408bf6c34db52ab1cbcc6c22f7b1eaae411f854541400fb0aa6833d20507e"
IMAGE_REF="registry.test:5000/hvo/roof-controller:4.0.0@${DIGEST}"

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

# seed_env <name> <NAME=value>...: the --env options the seeded container was run with (docker inspect reports them).
seed_env() {
  local name=$1 options
  shift
  options=$(for pair in "$@"; do printf '%s\n%s\n' --env "${pair}"; done | jq -R . | jq -s .)
  jq --arg name "${name}" --argjson options "${options}" '.containers[$name].options = $options' \
    "${FAKE_STATE_DIR}/state.json" > "${FAKE_STATE_DIR}/state.tmp" && mv "${FAKE_STATE_DIR}/state.tmp" "${FAKE_STATE_DIR}/state.json"
}

# seed_label <name> <key> <value>: a label on the seeded container (docker ps --format '{{.Label "<key>"}}' reads it).
seed_label() {
  jq --arg name "$1" --arg key "$2" --arg value "$3" '.containers[$name].labels[$key] = $value' \
    "${FAKE_STATE_DIR}/state.json" > "${FAKE_STATE_DIR}/state.tmp" && mv "${FAKE_STATE_DIR}/state.tmp" "${FAKE_STATE_DIR}/state.json"
}

# seed_restart_count <name> <count>: how often Docker has restarted the seeded container (docker inspect reports it).
seed_restart_count() {
  jq --arg name "$1" --argjson count "$2" '.containers[$name].restart_count = $count' \
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
    READY_TIMEOUT_SECONDS=1 POLL_INTERVAL_SECONDS=0.1 STOP_TIMEOUT_SECONDS=30
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

# As deploy_on_terminal, with an operator at the keyboard who types <answer> (and Enter) at the prompt for the
# --force-unverified-stop confirmation.
deploy_on_terminal_answering() {
  local answer=$1
  shift
  build_command "$@"
  STATUS=0
  env ON_TERMINAL_PROMPT="Type STOP-UNVERIFIED to continue: " ON_TERMINAL_ANSWER="${answer}" \
    "${TESTS_DIR}/on-terminal" "${FAKE_STATE_DIR}/output.pid" "${WORK}/output.log" "${RUN_CMD[@]}" || STATUS=$?
  OUTPUT=$(tr -d '\r' < "${WORK}/output.log")
}

# As deploy, in a session of its own with no controlling terminal, as from cron or CI: /dev/tty cannot be opened even
# when these tests run in a terminal.
deploy_without_terminal() {
  build_command "$@"
  OUTPUT=$(python3 -c '
import os, sys
child = os.fork()
if child == 0:
    os.setsid()
    os.execvp(sys.argv[1], sys.argv[1:])
_, status = os.waitpid(child, 0)
sys.exit(128 + os.WTERMSIG(status) if os.WIFSIGNALED(status) else os.WEXITSTATUS(status))
' "${RUN_CMD[@]}" 2>&1 </dev/null)
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
      "type=bind,src=/etc/hvo-roof/https,dst=/https,readonly" "type=bind,src=/etc/hvo-roof/secrets,dst=/run/secrets,readonly" \
      "type=bind,src=/var/lib/hvo-roof/identity,dst=/var/lib/hvo-roof/identity" \
      "RoofControllerSecurity__Identity__StorePath=/var/lib/hvo-roof/identity/identity.json" \
      "type=bind,src=/etc/hvo-roof/config,dst=/etc/hvo-roof/config" \
      "RoofControllerSettings__FilePath=/etc/hvo-roof/config/appsettings.Local.json" \
      "type=bind,src=/var/lib/hvo-roof/settings-secrets,dst=/var/lib/hvo-roof/settings-secrets" \
      "RoofControllerSettings__SecretsFilePath=/var/lib/hvo-roof/settings-secrets/secrets.json"; do
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

  # Remote check from this machine: HTTPS with the CA, authenticated Status then Stop, then the web UI's liveness.
  jq -e -s --arg ca "${WORK}/ca.pem" \
    '(map(.[-1]) == ["https://pi.test:8443/api/v4.0/RoofControl/Status", "https://pi.test:8443/api/v4.0/RoofControl/Stop",
                     "https://pi.test:8088/health/live"])
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

test_identity_dir_is_mounted_read_write_for_the_check_and_the_controller() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true IDENTITY_DIR=/srv/roof/identity

  assert_status 0
  local call
  for call in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e 'index("type=bind,src=/srv/roof/identity,dst=/var/lib/hvo-roof/identity")
           and index("RoofControllerSecurity__Identity__StorePath=/var/lib/hvo-roof/identity/identity.json")
           and (map(select(startswith("type=bind,src=/srv/roof/identity") and contains("readonly"))) | length == 0)' \
      <<<"${call}" >/dev/null || fail_test "the identity directory is not mounted read-write with the store path: ${call}"
  done
}

test_an_empty_identity_dir_keeps_the_store_in_memory() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true IDENTITY_DIR=

  assert_status 0
  local call
  for call in "$(preflight_args)" "$(controller_run_args)"; do
    [[ -n "${call}" ]] || fail_test "a docker run is missing"
    jq -e 'map(select(contains("hvo-roof/identity") or startswith("RoofControllerSecurity__Identity__"))) | length == 0' \
      <<<"${call}" >/dev/null || fail_test "an empty IDENTITY_DIR still mounts or configures the identity store: ${call}"
  done
}

test_settings_dirs_are_mounted_read_write_for_the_check_and_the_controller() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true CONFIG_DIR=/srv/roof/config MANAGED_SECRETS_DIR=/srv/roof/settings-secrets

  assert_status 0
  local call
  for call in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e 'index("type=bind,src=/srv/roof/config,dst=/etc/hvo-roof/config")
           and index("RoofControllerSettings__FilePath=/etc/hvo-roof/config/appsettings.Local.json")
           and index("type=bind,src=/srv/roof/settings-secrets,dst=/var/lib/hvo-roof/settings-secrets")
           and index("RoofControllerSettings__SecretsFilePath=/var/lib/hvo-roof/settings-secrets/secrets.json")
           and (map(select(startswith("type=bind,src=/srv/roof/") and contains("readonly"))) | length == 0)' \
      <<<"${call}" >/dev/null || fail_test "the settings directories are not mounted read-write with the file paths: ${call}"
  done
}

test_empty_settings_dirs_keep_the_settings_in_memory() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true CONFIG_DIR= MANAGED_SECRETS_DIR=

  assert_status 0
  local call
  for call in "$(preflight_args)" "$(controller_run_args)"; do
    [[ -n "${call}" ]] || fail_test "a docker run is missing"
    jq -e 'map(select(contains("hvo-roof/config") or contains("settings-secrets") or startswith("RoofControllerSettings__"))) | length == 0' \
      <<<"${call}" >/dev/null || fail_test "empty settings directories still mount or configure the settings files: ${call}"
  done
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
  jq -e -s 'map(.[-1]) == ["http://pi.test:8080/api/v4.0/RoofControl/Status", "http://pi.test:8080/api/v4.0/RoofControl/Stop",
                           "http://pi.test:8088/health/live"]
            and all(.[]; index("--cacert") == null)' "${FAKE_STATE_DIR}/remote.log" >/dev/null \
    || fail_test "unexpected remote calls: $(cat "${FAKE_STATE_DIR}/remote.log")"
  assert_container roof-controller new true
  assert_container roof-controller-previous old false no
}

test_publish_address_keeps_the_published_ports_to_that_address() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" PUBLISH_ADDRESS=127.0.0.1 PI_HOST=localhost

  assert_status 0
  assert_output_contains "Deployment complete and verified at https://localhost:8443"
  local run
  run=$(controller_run_args)
  jq -e 'index("127.0.0.1:8443:8443") and index("127.0.0.1:8088:8088") and (index("8443:8443") | not)' <<<"${run}" >/dev/null \
    || fail_test "the ports are not published on 127.0.0.1 only: ${run}"
  jq -e 'map(select(startswith("127.0.0.1:"))) | length == 0' <<<"$(preflight_args)" >/dev/null \
    || fail_test "the pre-flight container publishes ports"

  : > "${FAKE_STATE_DIR}/calls.log"
  deploy ALLOW_INSECURE_HTTP=true PUBLISH_ADDRESS=127.000.0.1 PI_HOST=localhost
  assert_status 0
  jq -e 'index("127.0.0.1:8080:8080") and index("127.0.0.1:8088:8088")' <<<"$(controller_run_args)" >/dev/null \
    || fail_test "the HTTP ports are not published on 127.0.0.1 only: $(controller_run_args)"

  # Published on loopback, the controller does not answer the remote check at the Pi's name: the deploy is undone.
  deploy "${HTTPS_ENV[@]}" PUBLISH_ADDRESS=127.0.0.1
  assert_status 1
  assert_output_contains "GET Status at https://pi.test:8443 failed from this machine"
}

test_remote_connect_to_checks_the_certificates_name_at_another_address() {
  seed_container roof-controller old true 8443:8443
  # On the Pi itself: the certificate names roofpi.local, which the Pi need not resolve, and not localhost.
  deploy "${HTTPS_ENV[@]}" PUBLISH_ADDRESS=127.0.0.1 PI_HOST=roofpi.local REMOTE_CONNECT_TO=::127.0.0.1: FAKE_CERT_NAMES=roofpi.local
  assert_status 0
  assert_output_contains "Deployment complete and verified at https://roofpi.local:8443"
  grep -q '"--connect-to", "::127.0.0.1:"' "${FAKE_STATE_DIR}/remote.log" \
    || fail_test "the checks from this machine did not use --connect-to: $(cat "${FAKE_STATE_DIR}/remote.log")"

  : > "${FAKE_STATE_DIR}/calls.log"
  deploy "${HTTPS_ENV[@]}" PI_HOST=roofpi.local REMOTE_CONNECT_TO='::127.0.0.1:; rm -rf /'
  assert_status 1
  assert_output_contains "REMOTE_CONNECT_TO must be HOST1:PORT1:HOST2:PORT2"
  assert_no_docker_calls "a malformed REMOTE_CONNECT_TO"
}

test_malformed_publish_address_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local address
  for address in localhost 127.0.0.256 "::1" "127.0.0.1:8443" "-p" "10.0.0"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" PUBLISH_ADDRESS="${address}"
    assert_status 1
    assert_output_contains "PUBLISH_ADDRESS must be an IPv4 address such as 127.0.0.1, or empty for every interface, got '${address}'. Nothing was changed."
    assert_no_docker_calls "PUBLISH_ADDRESS=${address}"
  done
  assert_container roof-controller old true unless-stopped
}

test_empty_otlp_endpoint_turns_export_off() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}"
  assert_status 0
  jq -e 'index("OTEL_EXPORTER_OTLP_ENDPOINT=http://192.168.1.238:4318")' <<<"$(controller_run_args)" >/dev/null \
    || fail_test "an unset OTEL_EXPORTER_OTLP_ENDPOINT does not default to the observatory's collector"

  : > "${FAKE_STATE_DIR}/calls.log"
  deploy "${HTTPS_ENV[@]}" OTEL_EXPORTER_OTLP_ENDPOINT=
  assert_status 0
  jq -e 'index("OTEL_EXPORTER_OTLP_ENDPOINT=") and (map(select(startswith("OTEL_EXPORTER_OTLP_ENDPOINT=http"))) | length == 0)' \
    <<<"$(controller_run_args)" >/dev/null || fail_test "an empty OTEL_EXPORTER_OTLP_ENDPOINT does not turn export off"
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

# The new controller runs with --restart unless-stopped, so Docker restarts it when it exits and shows it stopped only
# when it cannot start it again: a restart, a wait for one, or a stop must fail readiness at once, not at the timeout.
new_controller_that_exits_rolls_back_without_waiting() {
  seed_container roof-controller old true 8080:8080
  local started=${SECONDS}
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_EXITS="$1" FAKE_NEW_READY=false READY_TIMEOUT_SECONDS=60

  assert_status_is 1
  assert_output_contains "roof-controller exited before it became ready (it stopped, or Docker restarted it)"
  assert_output_not_contains "did not become ready within"
  assert_output_contains "fake controller log line"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
  # The restore checks the restarted controller with the same 60s limit, so a wait for the exited one would show.
  (( SECONDS - started < 30 )) || fail_test "the deploy waited for the exited controller ($((SECONDS - started))s)"
}

test_new_controller_that_exits_and_is_restarted_rolls_back_without_waiting() {
  new_controller_that_exits_rolls_back_without_waiting restarted
}

test_new_controller_that_exits_and_waits_to_restart_rolls_back_without_waiting() {
  new_controller_that_exits_rolls_back_without_waiting restarting
}

test_new_controller_that_exits_and_stays_stopped_rolls_back_without_waiting() {
  new_controller_that_exits_rolls_back_without_waiting exited
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

# --- --force-unverified-stop --------------------------------------------------------------------------------------

# assert_old_controller_untouched: the old controller runs as before, and nothing was stopped, renamed or run.
assert_old_controller_untouched() {
  assert_container roof-controller old true unless-stopped
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "the controller was stopped or replaced"
}

test_forced_unverified_stop_without_a_terminal_aborts_without_stopping() {
  seed_container roof-controller old true 8443:8443
  deploy_without_terminal "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified -- --force-unverified-stop

  assert_status 1
  assert_output_contains "--force-unverified-stop needs an interactive terminal for the confirmation."
  assert_output_not_contains "Type STOP-UNVERIFIED"
  assert_old_controller_untouched
}

test_unverified_stop_on_a_terminal_needs_the_flag_before_any_prompt() {
  seed_container roof-controller old true 8443:8443
  deploy_on_terminal_answering STOP-UNVERIFIED "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified

  assert_status 1
  assert_output_contains "rerun with --force-unverified-stop"
  assert_output_not_contains "Type STOP-UNVERIFIED"
  assert_old_controller_untouched
}

test_forced_unverified_stop_with_a_wrong_answer_aborts_without_stopping() {
  seed_container roof-controller old true 8443:8443
  deploy_on_terminal_answering yes "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified -- --force-unverified-stop

  assert_status 1
  assert_output_contains "relayRegisterState=Unknown"
  assert_output_contains "WARNING: the roof stop is NOT verified"
  assert_output_contains "Type STOP-UNVERIFIED to continue: "
  assert_output_contains "Confirmation not given; deployment aborted."
  assert_old_controller_untouched
}

test_forced_stop_that_cannot_reach_the_controller_needs_the_answer_too() {
  seed_container roof-controller old true 8443:8443
  # Enter alone: an empty answer.
  deploy_on_terminal_answering "" "${HTTPS_ENV[@]}" FAKE_OLD_STOP=error -- --force-unverified-stop

  assert_status 1
  assert_output_contains "Could not call Stop inside roof-controller."
  assert_output_contains "Confirmation not given; deployment aborted."
  assert_old_controller_untouched
}

test_confirmed_unverified_stop_replaces_the_controller_gracefully() {
  seed_container roof-controller old true 8443:8443
  deploy_on_terminal_answering STOP-UNVERIFIED "${HTTPS_ENV[@]}" FAKE_OLD_STOP=unverified -- --force-unverified-stop

  assert_status 0
  assert_output_contains "WARNING: the roof stop is NOT verified"
  assert_output_contains "Deployment complete and verified at https://pi.test:8443"
  assert_container roof-controller new true unless-stopped
  assert_container roof-controller-previous old false no
  # The old controller still gets the graceful stop (SIGTERM and the grace period, so its shutdown stops the roof
  # again), after the Stop request, and is kept.
  jq -e -s 'length == 1 and .[0][1] == "-t" and .[0][2] == "30"' <<<"$(docker_calls stop)" >/dev/null \
    || fail_test "expected one graceful docker stop -t 30: $(docker_calls stop)"
  [[ -z "$(docker_calls kill)$(docker_calls rm)" ]] || fail_test "a container was killed or removed"
  local i_stop_request i_stop
  i_stop_request=$(call_index '"http://localhost:8080/api/v4.0/RoofControl/Stop"')
  i_stop=$(call_index '"stop", "-t"')
  (( i_stop_request > 0 && i_stop_request < i_stop )) \
    || fail_test "unexpected order: stop-request=${i_stop_request} stop=${i_stop}"
  assert_key_never_in_argv
}

test_confirmed_unverified_stop_lets_a_rollback_swap() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy_on_terminal_answering STOP-UNVERIFIED "${HTTPS_ENV[@]}" FAKE_CURRENT_STOP=unverified \
    -- --rollback --force-unverified-stop

  assert_status 0
  assert_output_contains "WARNING: the roof stop is NOT verified"
  assert_output_contains "Rolled back. roof-controller is verified at https://pi.test:8443."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no
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

# Docker restarted the old controller before this deploy. With its stop failing, the restore's docker start does nothing
# and keeps that restart count, which must not read as an exit.
test_failed_docker_stop_restores_a_controller_that_docker_restarted_before() {
  seed_container roof-controller old true 8080:8080
  seed_restart_count roof-controller 2
  deploy "${HTTPS_ENV[@]}" FAKE_STOP_FAIL=true

  assert_status_is 1
  assert_output_contains "WARNING: could not stop the original controller; starting it anyway"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_output_not_contains "exited before it became ready"
  assert_container roof-controller old true unless-stopped
}

test_restore_that_cannot_read_docker_does_not_claim_the_controller_is_gone() {
  seed_container roof-controller old true 8443:8443
  # The stop fails (a lost connection), and so does every lookup by ID the restore makes.
  deploy "${HTTPS_ENV[@]}" FAKE_STOP_FAIL=true FAKE_FAIL="--filter id="

  assert_status 1
  assert_output_contains "Could not stop roof-controller; it was not replaced"
  assert_output_contains "Docker could not report the state of the original controller"
  assert_output_contains "it may be running or stopped"
  assert_output_not_contains "could not be found"
  assert_container roof-controller old true unless-stopped
  [[ -z "$(docker_calls start)$(docker_calls rename)$(controller_run_args)" ]] || fail_test "a container was started, renamed or replaced"
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

# --verify-remote checks a controller that Docker Compose runs, from a machine with no Docker context for the Pi.
test_verify_remote_checks_status_and_stop_without_docker() {
  seed_container roof-controller compose true 8443:8443
  seed_label roof-controller com.docker.compose.project roof
  deploy "${HTTPS_ENV[@]}" REMOTE_CA_CERT="${WORK}/ca.pem" FAKE_REQUIRE_CACERT=true -- --verify-remote

  assert_status 0
  assert_output_contains "[verify] Authenticated Status at https://pi.test:8443: HTTP 200, hatMode Physical"
  assert_output_contains "relayRegisterState=Verified relayRegisterMask=0 commandedMotion=None"
  assert_output_contains "[done] Verified at https://pi.test:8443 from this machine: authenticated Status (hatMode Physical) and a verified Stop."
  assert_no_docker_calls "--verify-remote"
  jq -se 'length == 2
          and (.[0] | (index("-X") + 1) as $i | .[$i] == "GET" and (.[-1] == "https://pi.test:8443/api/v4.0/RoofControl/Status"))
          and (.[1] | (index("-X") + 1) as $i | .[$i] == "POST" and (.[-1] == "https://pi.test:8443/api/v4.0/RoofControl/Stop"))
          and all(.[]; index("--cacert") != null)' "${FAKE_STATE_DIR}/remote.log" >/dev/null \
    || fail_test "unexpected remote calls: $(cat "${FAKE_STATE_DIR}/remote.log")"
  assert_container roof-controller compose true unless-stopped
  assert_key_never_in_argv
}

test_verify_remote_expects_the_emulated_hat_in_emulator_mode() {
  seed_container roof-controller compose true 8443:8443
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true FAKE_COMPOSE_HAT_MODE=Emulated \
    -- --verify-remote

  assert_status 0
  assert_output_contains "Expected HAT: HAT EMULATOR at hat-emulator:5291"
  assert_output_contains "authenticated Status (hatMode Emulated) and a verified Stop"
  assert_no_docker_calls "--verify-remote"
}

test_verify_remote_fails_on_the_wrong_hat_before_the_stop() {
  seed_container roof-controller compose true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_COMPOSE_HAT_MODE=Emulated -- --verify-remote

  assert_status 1
  assert_output_contains "the controller at https://pi.test:8443 reports hatMode Emulated, but this deployment is for hatMode Physical"
  assert_output_not_contains "[done]"
  [[ "$(wc -l < "${FAKE_STATE_DIR}/remote.log")" -eq 1 ]] || fail_test "a Stop was sent after the HAT check failed"
  assert_no_docker_calls "--verify-remote"
}

# A controller from before emulator mode reports no hatMode. With isUsingPhysicalHardware true it drives the physical
# HAT; without an I2C bus it fell back to the register simulation, whose verified Stop proves nothing about the roof.
test_verify_remote_takes_a_missing_hat_mode_as_physical_only_on_the_physical_hat() {
  seed_container roof-controller compose true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_COMPOSE_HAT_MODE=absent -- --verify-remote

  assert_status_is 0
  assert_output_contains "authenticated Status (hatMode Physical (no hatMode: a version from before emulator mode)) and a verified Stop"
  [[ "$(wc -l < "${FAKE_STATE_DIR}/remote.log")" -eq 2 ]] || fail_test "expected a Status and a Stop"
  assert_no_docker_calls "--verify-remote"

  : > "${FAKE_STATE_DIR}/remote.log"
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true FAKE_COMPOSE_HAT_MODE=absent \
    -- --verify-remote

  assert_status_is 1
  assert_output_contains "but this deployment is for hatMode Emulated"
  assert_output_not_contains "[done]"
  [[ "$(wc -l < "${FAKE_STATE_DIR}/remote.log")" -eq 1 ]] || fail_test "a Stop was sent after the HAT check failed"
  assert_no_docker_calls "--verify-remote"

  : > "${FAKE_STATE_DIR}/remote.log"
  deploy "${HTTPS_ENV[@]}" FAKE_COMPOSE_HAT_MODE=absent FAKE_COMPOSE_PHYSICAL=false -- --verify-remote

  assert_status_is 1
  assert_output_contains "reports no hatMode and isUsingPhysicalHardware false: a version from before emulator mode on the register simulation, not the physical HAT"
  assert_output_not_contains "[done]"
  [[ "$(wc -l < "${FAKE_STATE_DIR}/remote.log")" -eq 1 ]] || fail_test "a Stop was sent after the HAT check failed"
}

test_verify_remote_fails_on_a_rejected_key_an_unverified_stop_or_no_answer() {
  seed_container roof-controller compose true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true ROOF_OPERATOR_API_KEY=not-the-configured-key -- --verify-remote
  assert_status 1
  assert_output_contains "GET Status at http://pi.test:8080 returned HTTP 401 to this machine"

  deploy ALLOW_INSECURE_HTTP=true FAKE_REMOTE_STOP=unverified -- --verify-remote
  assert_status 1
  assert_output_contains "POST Stop at http://pi.test:8080 did not return a verified stop to this machine"

  deploy "${HTTPS_ENV[@]}" -- --verify-remote
  assert_status 1
  assert_output_contains "GET Status at https://pi.test:8443 failed from this machine"
  assert_output_not_contains "[done]"
  assert_no_docker_calls "--verify-remote"
}

test_verify_remote_refuses_other_modes_and_skip_remote_check() {
  local other
  for other in --dry-run --force-unverified-stop --rollback; do
    deploy "${HTTPS_ENV[@]}" -- --verify-remote "${other}"
    assert_status_is 2
    assert_output_contains "--verify-remote cannot be combined with --dry-run, --force-unverified-stop or --rollback"
  done

  deploy "${HTTPS_ENV[@]}" SKIP_REMOTE_CHECK=true -- --verify-remote
  assert_status_is 1
  assert_output_contains "unset SKIP_REMOTE_CHECK"
  assert_no_docker_calls "--verify-remote"
  [[ ! -s "${FAKE_STATE_DIR}/remote.log" ]] || fail_test "the remote URL was called"
}

test_rollback_swaps_current_and_previous_and_back() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" -- --rollback

  assert_status 0
  assert_output_contains "Rolled back. roof-controller is verified at https://pi.test:8443. HAT: hatMode Physical. The replaced version"
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
  assert_output_contains "identity dir: /var/lib/hvo-roof/identity;"
  assert_output_contains "Settings dir on Pi: /etc/hvo-roof/config; managed secrets dir: /var/lib/hvo-roof/settings-secrets"
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

# --- The web UI and the container's supervisor ------------------------------------------------------------------------

test_web_ui_is_published_on_its_own_port_with_the_controllers_scheme() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" REMOTE_CA_CERT="${WORK}/ca.pem" FAKE_REQUIRE_CACERT=true WEB_HOST_PORT=9443

  assert_status 0
  local run
  run=$(controller_run_args)
  jq -e 'index("9443:8088") and index("RoofWeb__Urls=https://+:8088") and (index("8088:8088") | not)' <<<"${run}" >/dev/null \
    || fail_test "the web UI is not published on WEB_HOST_PORT over HTTPS: ${run}"
  docker_calls exec | jq -e -s 'map(select(index("https://localhost:8088/health/live"))) | length > 0' >/dev/null \
    || fail_test "the web UI was not checked over HTTPS inside the container"
  jq -e -s --arg ca "${WORK}/ca.pem" \
    'map(select(.[-1] == "https://pi.test:9443/health/live" and .[index("--cacert") + 1] == $ca)) | length == 1' \
    "${FAKE_STATE_DIR}/remote.log" >/dev/null || fail_test "unexpected remote calls: $(cat "${FAKE_STATE_DIR}/remote.log")"
  assert_output_contains "[verify] Web UI live inside the container (https)"
  assert_output_contains "[verify] Web UI live at https://pi.test:9443: OK"

  : > "${FAKE_STATE_DIR}/calls.log"
  deploy ALLOW_INSECURE_HTTP=true
  assert_status 0
  run=$(controller_run_args)
  jq -e 'index("8088:8088") and index("RoofWeb__Urls=http://+:8088") and (index("RoofWeb__Urls=https://+:8088") | not)' \
    <<<"${run}" >/dev/null || fail_test "the web UI is not published on 8088 over HTTP: ${run}"
  assert_output_contains "[verify] Web UI live inside the container (http)"
  assert_output_contains "[verify] Web UI live at http://pi.test:8088: OK"
}

test_web_host_port_on_the_controllers_published_port_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" WEB_HOST_PORT=8443
  assert_status 1
  assert_output_contains "WEB_HOST_PORT (8443) is the controller's published port too: give the web UI a port of its own. Nothing was changed."
  assert_no_docker_calls "HTTPS"

  deploy ALLOW_INSECURE_HTTP=true HOST_PORT=8090 WEB_HOST_PORT=8090
  assert_status 1
  assert_output_contains "WEB_HOST_PORT (8090) is the controller's published port too"
  assert_no_docker_calls "HTTP"
  assert_container roof-controller old true unless-stopped

  # Over HTTPS the controller's plain HTTP port is not published, so the web UI may take it.
  deploy "${HTTPS_ENV[@]}" WEB_HOST_PORT=8080
  assert_status 0
  jq -e 'index("8080:8088")' <<<"$(controller_run_args)" >/dev/null || fail_test "the web UI is not published on 8080"
}

test_web_ui_that_never_answers_inside_the_container_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_WEB_LIVE=false

  assert_status_is 1
  assert_output_contains "the web UI in roof-controller did not answer https://localhost:8088/health/live within 1s"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
  [[ ! -s "${FAKE_STATE_DIR}/remote.log" ]] || fail_test "the remote check ran after the in-container check failed"
}

# A web UI that cannot start exits, and its supervisor starts it again with a backoff for ever: that fails at once.
web_ui_that_the_supervisor_restarts_rolls_back_without_waiting() {
  seed_container roof-controller old true 8080:8080
  local started=${SECONDS}
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_SUPERVISOR="$1" FAKE_NEW_WEB_LIVE=false READY_TIMEOUT_SECONDS=60

  assert_status_is 1
  assert_output_contains "the web UI in roof-controller exited and its supervisor is starting it again: it cannot start (see its log lines below)"
  assert_output_contains "fake controller log line"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
  (( SECONDS - started < 30 )) || fail_test "the deploy waited for the web UI ($((SECONDS - started))s)"
}

test_web_ui_waiting_to_start_again_rolls_back_without_waiting() {
  web_ui_that_the_supervisor_restarts_rolls_back_without_waiting ui-restarting
}

test_web_ui_started_again_rolls_back_without_waiting() {
  web_ui_that_the_supervisor_restarts_rolls_back_without_waiting ui-restarted
}

# The supervisor restarts a controller that exits, and leaves it stopped after repeated crashes, all inside a container
# Docker sees running: its state file tells the deploy the controller exited.
controller_that_the_supervisor_restarts_rolls_back_without_waiting() {
  seed_container roof-controller old true 8080:8080
  local started=${SECONDS}
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_SUPERVISOR="$1" FAKE_NEW_READY=false READY_TIMEOUT_SECONDS=60

  assert_status_is 1
  assert_output_contains "roof-controller exited before it became ready (it stopped, or Docker restarted it): the container's supervisor reports the controller $2"
  assert_output_not_contains "did not become ready within"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  [[ "$(kind_count new)" == "0" ]] || fail_test "the new controller was not removed"
  (( SECONDS - started < 30 )) || fail_test "the deploy waited for the exited controller ($((SECONDS - started))s)"
}

test_controller_waiting_for_the_supervisor_to_start_it_again_rolls_back_without_waiting() {
  controller_that_the_supervisor_restarts_rolls_back_without_waiting restarting "restarting after 1 start(s)"
}

test_controller_the_supervisor_left_stopped_rolls_back_without_waiting() {
  controller_that_the_supervisor_restarts_rolls_back_without_waiting crash-loop "crash-loop after 5 start(s)"
}

test_controller_the_supervisor_started_again_rolls_back_without_waiting() {
  controller_that_the_supervisor_restarts_rolls_back_without_waiting restarted "running after 3 start(s)"
}

# An image from before the supervisor has no state file: readiness alone decides, as before.
test_controller_without_a_supervisor_state_file_is_checked_by_readiness_alone() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_NEW_SUPERVISOR=absent

  assert_status 0
  assert_output_contains "Deployment complete and verified at https://pi.test:8443"
  assert_container roof-controller new true unless-stopped
}

test_web_ui_unreachable_from_this_machine_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_REMOTE_WEB_LIVE=false

  assert_status_is 1
  assert_output_contains "[verify] Remote Status and verified Stop at https://pi.test:8443: OK"
  assert_output_contains "the web UI did not answer at https://pi.test:8088/health/live from this machine (network, port or TLS; set REMOTE_CA_CERT for a certificate this machine does not trust)"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous missing false
}

test_web_ui_whose_environment_docker_cannot_report_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL=.Config.Env

  assert_status_is 1
  assert_output_contains "could not read the environment of roof-controller from Docker to find its web UI"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
}

# The scheme is the first URL's, as the web UI listens on each and the check needs one.
test_web_ui_with_several_urls_is_checked_on_the_first() {
  seed_container roof-controller old true 8080:8080
  deploy ALLOW_INSECURE_HTTP=true EXTRA_DOCKER_ARGS="--env RoofWeb__Urls=http://+:8088;https://+:8089"

  assert_status 0
  assert_output_contains "[verify] Web UI live inside the container (http)"
}

# EXTRA_DOCKER_ARGS come last, so a RoofWeb__Urls there wins inside the container; a scheme that is not the one this
# machine is told to use fails the check from this machine, and the deploy rolls back.
test_web_ui_serving_another_scheme_than_the_deploy_expects_rolls_back() {
  seed_container roof-controller old true 8080:8080
  deploy "${HTTPS_ENV[@]}" EXTRA_DOCKER_ARGS="--env RoofWeb__Urls=http://+:8088"

  assert_status_is 1
  assert_output_contains "[verify] Web UI live inside the container (http)"
  assert_output_contains "the web UI did not answer at https://pi.test:8088/health/live from this machine"
  assert_container roof-controller old true unless-stopped
}

test_rollback_to_a_version_from_before_the_web_ui_skips_its_checks() {
  seed_container roof-controller current true 8443:8443 8088:8088
  seed_env roof-controller current RoofWeb__Urls=https://+:8088
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_SUPERVISOR=absent FAKE_OLD_WEB_LIVE=false -- --rollback

  assert_status 0
  assert_output_contains "[verify] roof-controller has no web UI (a version from before it): not checked"
  assert_output_contains "Rolled back. roof-controller is verified at https://pi.test:8443."
  assert_container roof-controller old true unless-stopped
  jq -e -s 'map(select(.[-1] | endswith("/health/live"))) | length == 0' "${FAKE_STATE_DIR}/remote.log" >/dev/null \
    || fail_test "the web UI of a version without one was checked: $(cat "${FAKE_STATE_DIR}/remote.log")"

  # Swapping back to the version with the web UI checks it again.
  : > "${FAKE_STATE_DIR}/remote.log"
  deploy "${HTTPS_ENV[@]}" -- --rollback
  assert_status 0
  assert_output_contains "[verify] Web UI live at https://pi.test:8088: OK"
  assert_container roof-controller current true unless-stopped
}

test_rollback_to_a_version_whose_web_ui_fails_is_left_running() {
  seed_container roof-controller current true 8443:8443 8088:8088
  seed_env roof-controller current RoofWeb__Urls=https://+:8088
  seed_container roof-controller-previous old false 8443:8443 8088:8088
  seed_env roof-controller-previous RoofWeb__Urls=https://+:8088
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_WEB_LIVE=false -- --rollback

  assert_status_is 1
  assert_output_contains "the web UI in roof-controller did not answer https://localhost:8088/health/live within 1s. It is left running. Run --rollback again to swap back."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no
}

# --- Settings (validated before any Docker call) ---------------------------------------------------------------------

test_malformed_numeric_settings_fail_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local setting
  for setting in READY_TIMEOUT_SECONDS=90.5 READY_TIMEOUT_SECONDS=0 READY_TIMEOUT_SECONDS=1234567890123 \
      STOP_TIMEOUT_SECONDS=120s STOP_TIMEOUT_SECONDS=-5 STOP_TIMEOUT_SECONDS=29 HOST_PORT=http HTTPS_HOST_PORT=70000 WEB_HOST_PORT=0 WEB_HOST_PORT=web \
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
  deploy "${HTTPS_ENV[@]}" STOP_TIMEOUT_SECONDS=045 READY_TIMEOUT_SECONDS=09 HTTPS_HOST_PORT=08443

  assert_status 0
  assert_output_contains "Waiting up to 9s"
  assert_output_contains "verified at https://pi.test:8443"
  docker_calls stop | jq -e -s '.[0] | .[index("-t") + 1] == "45"' >/dev/null \
    || fail_test "docker stop was not given -t 45: $(docker_calls stop)"
  controller_run_args | jq -e '.[index("--stop-timeout") + 1] == "45" and index("8443:8443")' >/dev/null \
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

test_extra_docker_args_cannot_override_name_restart_ports_stop_or_platform() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in "--name other" "--name=other" "-d" "--detach" "--detach=true" "--rm" "--restart always" \
      "--restart=always" "--cidfile /tmp/cid" "-p 80:8080" "-p8080:8080" "--publish 80:8080" "--publish=80:8080" \
      "-P" "--publish-all" "-itd" "-tp 80:8080" "--stop-timeout 1" "--stop-timeout=1" "--stop-signal SIGKILL" \
      "--stop-signal=KILL" "--platform linux/amd64" "--platform=linux/amd64"; do
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

test_physical_deploy_maps_the_hat_and_turns_the_emulator_off() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}"

  assert_status 0
  assert_output_contains "HAT: physical HAT (/dev/i2c-1)."
  assert_output_contains "authenticated Status inside the container: HTTP 200, hatMode Physical"
  assert_output_not_contains "HAT emulator mode"
  local args
  for args in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e '(.[index("/dev/i2c-1:/dev/i2c-1") - 1] == "--device") and (.[index("HatEmulator__Enabled=false") - 1] == "--env")
           and (.[index("HatEmulator__AllowOutsideDevelopment=false") - 1] == "--env")
           and (map(select(startswith("HatEmulator__")))
                == ["HatEmulator__Enabled=false", "HatEmulator__AllowOutsideDevelopment=false"])' <<<"${args}" >/dev/null \
      || fail_test "expected the HAT's I2C device and the emulator off and refused: ${args}"
    jq -e '(.[index("/dev/gpiomem:/dev/gpiomem") - 1] == "--device")
           and (.[index("type=bind,src=/sys/class/thermal/thermal_zone0/temp,dst=/sys/class/thermal/thermal_zone0/temp,readonly") - 1] == "--mount")' \
      <<<"${args}" >/dev/null || fail_test "expected /dev/gpiomem and the thermal sensor mapped: ${args}"
  done
  docker_calls buildx | jq -e -s 'map(select(.[1] == "build")) | length == 1 and (.[0] | .[index("--platform") + 1] == "linux/arm64")' \
    >/dev/null || fail_test "expected one build for linux/arm64: $(docker_calls buildx)"
  # The image carries the product version as a -dev build, and the checkout's commit (docs/releasing.md).
  docker_calls buildx | jq -e -s --arg version "ROOF_VERSION=$("${TESTS_DIR}/../../build/version.sh" --dev)" \
      --arg revision "ROOF_REVISION=$(git -C "${TESTS_DIR}" rev-parse HEAD)" '
      (map(select(.[1] == "build")) | .[0]) as $build | [$build | indices("--build-arg")[] | $build[. + 1]] as $args
      | ($args | index($version)) and ($args | index($revision))
        and ($args | any(test("^ROOF_CREATED=[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")))' >/dev/null \
    || fail_test "expected the build to pass the version, commit and creation time: $(docker_calls buildx)"
  assert_output_contains "($("${TESTS_DIR}/../../build/version.sh" --dev), commit $(git -C "${TESTS_DIR}" rev-parse --short=12 HEAD))"
}

test_emulator_endpoint_without_the_flag_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local allow
  for allow in "" "ALLOW_EMULATED_HAT=false" "ALLOW_EMULATED_HAT=yes"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ${allow:+"${allow}"}
    assert_status 1
    assert_output_contains "the controller would use the HAT emulator at hat-emulator:5291, not the physical HAT"
    assert_output_contains "set ALLOW_EMULATED_HAT=true as well. Nothing was changed."
    assert_no_docker_calls "${allow:-no flag}"
  done
  assert_container roof-controller old true unless-stopped
}

test_emulator_mode_with_the_flag_unmaps_the_hat_and_records_the_flag() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:05291 ALLOW_EMULATED_HAT=true

  assert_status 0
  assert_output_contains "[deploy] WARNING: HAT emulator mode: the new controller will use the HAT emulator at hat-emulator:5291 (ALLOW_EMULATED_HAT=true)"
  assert_output_contains "authenticated Status inside the container: HTTP 200, hatMode Emulated"
  assert_output_contains "Deployment complete and verified at https://pi.test:8443. HAT: HAT EMULATOR at hat-emulator:5291 (ALLOW_EMULATED_HAT=true): the physical HAT is not mapped and the roof does not move."
  assert_container roof-controller new true unless-stopped
  local args
  for args in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e '(index("/dev/i2c-1:/dev/i2c-1") | not)
           and (map(select(startswith("HatEmulator__"))) == ["HatEmulator__Enabled=true", "HatEmulator__Host=hat-emulator",
                "HatEmulator__Port=5291", "HatEmulator__AllowOutsideDevelopment=true"])
           and (.[index("HatEmulator__Enabled=true") - 1] == "--env")
           and (map(select(test("gpiomem|thermal"))) == [])' <<<"${args}" >/dev/null \
      || fail_test "unexpected emulator-mode arguments: ${args}"
  done
}

test_build_platform_amd64_is_for_the_emulator_only() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in linux/amd64 linux/arm/v7 arm64 "linux/arm64 --push"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "BUILD_PLATFORM=${value}"
    assert_status 1
    if [[ "${value}" == "linux/amd64" ]]; then
      assert_output_contains "BUILD_PLATFORM=linux/amd64 is for a test rig on the HAT emulator"
    else
      assert_output_contains "BUILD_PLATFORM must be linux/arm64 (the Pi) or linux/amd64 (a test rig on the HAT emulator), got '${value}'. Nothing was changed."
    fi
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped

  deploy "${HTTPS_ENV[@]}" BUILD_PLATFORM=linux/amd64 HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true
  assert_status 0
  assert_output_contains "[build] Building ${IMAGE} ($("${TESTS_DIR}/../../build/version.sh" --dev), commit "
  assert_output_contains ") for linux/amd64..."
  assert_container roof-controller new true unless-stopped
  docker_calls buildx | jq -e -s 'map(select(.[1] == "build")) | length == 1 and (.[0] | .[index("--platform") + 1] == "linux/amd64")' \
    >/dev/null || fail_test "expected one build for linux/amd64: $(docker_calls buildx)"
}

# --- Pull mode --------------------------------------------------------------------------------------------------------

# assert_nothing_changed <label>: no container stopped, renamed, removed or started, and no image built or loaded.
assert_nothing_changed() {
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(docker_calls rm)$(docker_calls run)$(docker_calls buildx)$(docker_calls load)" ]] \
    || fail_test "$1: something changed: $(jq -c 'if .[0] == "--context" then .[2:] else . end' "${FAKE_STATE_DIR}/calls.log" | grep -E '^\["(stop|rename|rm|run|buildx|load)"' | head -n 1)"
  assert_container roof-controller old true unless-stopped
}

test_pull_mode_pulls_checks_and_deploys_the_released_image_without_building() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}"

  assert_status 0
  assert_output_contains "[pull] Pulling ${IMAGE_REF} for linux/arm64 into Docker context 'test-context'..."
  assert_output_contains "[pull] ${DIGEST} for linux/arm64: version 4.0.0, commit 0123456789ab"
  assert_output_contains "Deployment complete and verified at https://pi.test:8443"
  assert_output_not_contains "[build]"
  assert_container roof-controller new true unless-stopped
  assert_container roof-controller-previous old false no
  [[ -z "$(docker_calls buildx)$(docker_calls save)$(docker_calls load)" ]] || fail_test "pull mode built, saved or loaded an image"

  # The Pi's Docker pulls the reference for the Pi's platform, then the image is checked for that platform.
  docker_calls pull | jq -e -s --arg ref "${IMAGE_REF}" 'length == 1 and .[0] == ["pull", "--platform", "linux/arm64", $ref]' \
    >/dev/null || fail_test "expected one pull of ${IMAGE_REF} for linux/arm64: $(docker_calls pull)"
  grep -F '"--context", "test-context", "pull"' "${FAKE_STATE_DIR}/calls.log" >/dev/null \
    || fail_test "the pull did not use the Pi's Docker context"
  docker_calls image | jq -e -s --arg ref "${IMAGE_REF}" \
    'map(select(index("--help") | not)) | length == 3 and all(.[]; .[index("--platform") + 1] == "linux/arm64" and .[-1] == $ref)' \
    >/dev/null || fail_test "expected three image inspects of ${IMAGE_REF} for linux/arm64: $(docker_calls image)"
  (( $(call_index '"pull"') < $(call_index '"--validate-deployment"') )) || fail_test "the pre-flight ran before the pull"

  # The pre-flight and the controller run the pulled image, for the checked platform.
  local args
  for args in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e --arg ref "${IMAGE_REF}" 'index($ref) and .[index("--platform") + 1] == "linux/arm64"' <<<"${args}" >/dev/null \
      || fail_test "expected ${IMAGE_REF} run for linux/arm64: ${args}"
  done
  jq -e --arg ref "${IMAGE_REF}" '.[-2] == $ref and .[-1] == "--validate-deployment"' <<<"$(preflight_args)" >/dev/null \
    || fail_test "the pre-flight does not run ${IMAGE_REF} --validate-deployment: $(preflight_args)"
  jq -e --arg ref "${IMAGE_REF}" '.[-1] == $ref' <<<"$(controller_run_args)" >/dev/null \
    || fail_test "the controller does not run ${IMAGE_REF}: $(controller_run_args)"
  assert_key_never_in_argv
}

test_pull_mode_runs_outside_a_checkout() {
  seed_container roof-controller old true 8443:8443
  # The script as a release ships it: on its own, with no repository around it.
  mkdir -p "${WORK}/release"
  cp "${SCRIPT}" "${WORK}/release/deploy-roofcontroller-rpi.sh"
  local SCRIPT="${WORK}/release/deploy-roofcontroller-rpi.sh"
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}"

  assert_status 0
  assert_output_contains "Deployment complete and verified"
  assert_container roof-controller new true unless-stopped
}

test_image_ref_without_a_digest_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in "registry.test:5000/hvo/roof-controller:4.0.0" "registry.test:5000/hvo/roof-controller:latest" \
      "registry.test:5000/hvo/roof-controller@sha256:20533a01" "registry.test:5000/hvo/roof-controller@$(tr '[:lower:]' '[:upper:]' <<<"${DIGEST}")" \
      "registry.test:5000/hvo/roof-controller@md5:0123456789abcdef0123456789abcdef" "--privileged@${DIGEST}" \
      "registry.test/roof-controller:4.0.0@${DIGEST} --privileged" "registry.test/roof controller@${DIGEST}" \
      "Registry.test/roof-controller@${DIGEST}"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${value}"
    assert_status 1
    assert_output_contains "IMAGE_REF must name a released image by its digest"
    assert_output_contains "got '${value}'. Nothing was changed."
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped
}

test_failed_pull_changes_nothing() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" FAKE_PULL_FAIL=true

  assert_status 1
  assert_output_contains "no such host"
  assert_output_contains "Could not pull ${IMAGE_REF} for linux/arm64 into Docker context 'test-context' (see above). The running controller was not touched."
  assert_nothing_changed "failed pull"
  [[ -z "$(docker_calls exec)" ]] || fail_test "the running controller was asked to stop"
}

test_pulled_image_with_another_digest_changes_nothing() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" "FAKE_PULLED_DIGEST=${OTHER_DIGEST}"

  assert_status 1
  assert_output_contains "The pulled image does not carry the digest ${DIGEST} (Docker reports: registry.test:5000/hvo/roof-controller@${OTHER_DIGEST}"
  assert_output_contains "The running controller was not touched."
  assert_nothing_changed "wrong digest"
}

test_pulled_image_for_another_platform_changes_nothing() {
  seed_container roof-controller old true 8443:8443
  local store
  # The classic image store fails the inspect, the containerd store reports empty fields, and a CLI without
  # `image inspect --platform` reports the platform the image has.
  for store in "FAKE_IMAGE_STORE=classic" "FAKE_IMAGE_STORE=containerd" "FAKE_INSPECT_PLATFORM=false"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" FAKE_PULLED_PLATFORM=linux/amd64 "${store}"
    assert_status 1
    assert_output_contains "The image ${IMAGE_REF} is not for linux/arm64"
    assert_output_contains "The running controller was not touched."
    if [[ "${store}" == "FAKE_INSPECT_PLATFORM=false" ]]; then
      assert_output_contains "(Docker reports 'linux/amd64')"
    else
      assert_output_contains "(Docker reports 'no image for that platform')"
    fi
    assert_nothing_changed "${store}"
  done
}

test_pull_mode_with_a_cli_without_inspect_platform_checks_the_image_it_holds() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" FAKE_INSPECT_PLATFORM=false

  assert_status 0
  assert_output_contains "[pull] ${DIGEST} for linux/arm64: version 4.0.0"
  assert_container roof-controller new true unless-stopped
  docker_calls image | jq -e -s 'map(select(index("--help") | not)) | length == 3 and all(.[]; index("--platform") | not)' \
    >/dev/null || fail_test "expected image inspects without --platform: $(docker_calls image)"
}

test_pull_mode_amd64_is_for_the_emulator_only() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" BUILD_PLATFORM=linux/amd64
  assert_status 1
  assert_output_contains "BUILD_PLATFORM=linux/amd64 is for a test rig on the HAT emulator"
  assert_no_docker_calls "amd64 without the emulator"

  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" BUILD_PLATFORM=linux/amd64 HAT_EMULATOR_ENDPOINT=hat-emulator:5291 \
    ALLOW_EMULATED_HAT=true
  assert_status 0
  assert_output_contains "[pull] ${DIGEST} for linux/amd64: version 4.0.0"
  assert_container roof-controller new true unless-stopped
  docker_calls pull | jq -e -s '.[0] | .[index("--platform") + 1] == "linux/amd64"' >/dev/null \
    || fail_test "expected a pull for linux/amd64: $(docker_calls pull)"
  jq -e '.[index("--platform") + 1] == "linux/amd64" and index("HatEmulator__Enabled=true")' <<<"$(controller_run_args)" \
    >/dev/null || fail_test "expected the emulated controller run for linux/amd64: $(controller_run_args)"
}

test_pull_mode_dry_run_pulls_nothing() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" -- --dry-run

  assert_status 0
  assert_output_contains "[dry-run] Would pull ${IMAGE_REF} for linux/arm64 into Docker context 'test-context' and check its digest and platform, run the pre-flight check"
  [[ -z "$(docker_calls pull)$(docker_calls image)" ]] || fail_test "the dry run pulled or inspected an image"
  assert_nothing_changed "dry run"
}

test_rollback_returns_from_a_pulled_image_to_the_one_before_and_ignores_image_ref() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}"
  assert_status 0
  assert_container roof-controller new true unless-stopped

  : > "${FAKE_STATE_DIR}/calls.log"
  deploy "${HTTPS_ENV[@]}" "IMAGE_REF=${IMAGE_REF}" -- --rollback
  assert_status 0
  assert_output_contains "Rolled back."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous new false no
  [[ -z "$(docker_calls pull)$(docker_calls run)$(docker_calls buildx)" ]] || fail_test "--rollback pulled, built or ran an image"
}

# A reference a deploy would refuse, left in the environment, must not stop a rollback or a remote check.
test_rollback_and_verify_remote_ignore_a_malformed_image_ref() {
  local bad_ref="IMAGE_REF=ghcr.io/hualapaivalley/roof-controller:4.0.0"
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" "${bad_ref}" -- --rollback
  assert_status 0
  assert_output_contains "Rolled back."
  assert_output_not_contains "IMAGE_REF must name"
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no

  teardown
  setup
  seed_container roof-controller compose true 8443:8443
  seed_label roof-controller com.docker.compose.project roof
  deploy "${HTTPS_ENV[@]}" "${bad_ref}" -- --verify-remote
  assert_status 0
  assert_output_contains "[done] Verified at https://pi.test:8443 from this machine"
  assert_no_docker_calls "--verify-remote"
}

test_compose_managed_controller_is_refused_before_anything_changes() {
  local name args
  for name in roof-controller roof-controller-previous; do
    for args in "" --rollback --dry-run; do
      teardown
      setup
      seed_container roof-controller old true 8443:8443
      seed_container roof-controller-previous older false
      seed_label "${name}" com.docker.compose.project hvoroofcontrollerv4rpi
      deploy "${HTTPS_ENV[@]}" -- ${args:+"${args}"}

      assert_status 1
      assert_output_contains "${name} was created by Docker Compose (project hvoroofcontrollerv4rpi): this script replaces or restores only controllers it created. Nothing was changed."
      assert_output_contains "Moving between Compose and the deploy script"
      assert_container roof-controller old true unless-stopped
      assert_container roof-controller-previous older false
      [[ -z "$(docker_calls stop)$(docker_calls rename)$(docker_calls run)$(docker_calls buildx)$(docker_calls exec)" ]] \
        || fail_test "${name} ${args:-deploy}: a controller was stopped, renamed, run, built or called"
    done
  done
}

test_malformed_emulator_endpoint_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in "hat-emulator" "hat-emulator:" ":5291" "hat-emulator:0" "hat-emulator:70000" "hat-emulator:123456" \
      "hat emulator:5291" "hat-emulator:52x1" "http://hat-emulator:5291" "-hat:5291" "[::1]:5291" "hat;rm:5291"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "HAT_EMULATOR_ENDPOINT=${value}" ALLOW_EMULATED_HAT=true
    assert_status 1
    assert_output_contains "HAT_EMULATOR_ENDPOINT must be <host>:<port>"
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped
}

test_extra_docker_args_cannot_set_emulator_settings() {
  seed_container roof-controller old true 8443:8443
  local value
  for value in "--env HatEmulator__Enabled=true" "-e hatemulator__allowoutsidedevelopment=true" \
      "--env=HatEmulator:Enabled=true" "-eHATEMULATOR__PORT=1"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--cpus 2 ${value}"
    assert_status 1
    assert_output_contains "HAT emulator mode is set with HAT_EMULATOR_ENDPOINT and ALLOW_EMULATED_HAT"
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped
}

test_dry_run_reports_the_hat() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=192.0.2.10:5291 ALLOW_EMULATED_HAT=true -- --dry-run

  assert_status 0
  assert_output_contains "[dry-run] HAT: HAT EMULATOR at 192.0.2.10:5291 (ALLOW_EMULATED_HAT=true)"
  [[ -z "$(docker_calls run)$(docker_calls stop)$(docker_calls buildx)" ]] || fail_test "dry run changed something"
}

test_physical_deploy_whose_controller_reports_another_hat_rolls_back() {
  seed_container roof-controller old true 8443:8443
  local mode
  # Emulated: a HatEmulator setting in the secrets directory (read last) overrode the --env pins.
  for mode in Emulated Simulation absent; do
    : > "${FAKE_STATE_DIR}/remote.log"
    deploy "${HTTPS_ENV[@]}" "FAKE_NEW_HAT_MODE=${mode}"
    assert_status 1
    assert_output_contains "reports hatMode ${mode/absent/null}, but this deployment is for hatMode Physical (physical HAT (/dev/i2c-1)); check for HatEmulator settings in the secrets directory"
    assert_output_contains "Rolled back: the previous controller is running and ready."
    assert_container roof-controller old true unless-stopped
    assert_container roof-controller-previous missing false
    [[ ! -s "${FAKE_STATE_DIR}/remote.log" ]] || fail_test "${mode}: the remote check ran after the HAT check failed"
  done
}

test_emulator_deploy_whose_controller_reports_the_physical_hat_rolls_back() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true FAKE_NEW_HAT_MODE=Physical

  assert_status 1
  assert_output_contains "reports hatMode Physical, but this deployment is for hatMode Emulated (HAT EMULATOR at hat-emulator:5291"
  assert_output_contains "Rolled back: the previous controller is running and ready."
  assert_container roof-controller old true unless-stopped
}

test_env_file_with_emulator_settings_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  local setting form
  # .NET reads keys in any case, with ':' or '__', and with the ASPNETCORE_ prefix; a key without '=' takes its value
  # from this machine's environment.
  for setting in "HatEmulator__Enabled=true" "hatemulator__allowoutsidedevelopment=true" "HATEMULATOR:HOST=x" \
      "ASPNETCORE_HatEmulator__Enabled=true" "  HatEmulator__Port=1" "HatEmulator__Enabled"; do
    printf '# settings\n\nTZ=UTC\n%s\n' "${setting}" > "${WORK}/extra.env"
    for form in "--env-file ${WORK}/extra.env" "--env-file=${WORK}/extra.env"; do
      : > "${FAKE_STATE_DIR}/calls.log"
      deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--cpus 2 ${form}"
      assert_status 1
      assert_output_contains "EXTRA_DOCKER_ARGS must not contain '--env-file ${WORK}/extra.env: "
      assert_output_contains "HAT emulator mode is set with HAT_EMULATOR_ENDPOINT and ALLOW_EMULATED_HAT"
      assert_no_docker_calls "${form}: ${setting}"
    done
  done
  assert_container roof-controller old true unless-stopped
}

test_unreadable_env_file_is_refused_before_any_docker_call() {
  seed_container roof-controller old true 8443:8443
  mkdir "${WORK}/env-dir"
  local file
  for file in "${WORK}/missing.env" "${WORK}/env-dir"; do
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--env-file ${file}"
    assert_status 1
    assert_output_contains "EXTRA_DOCKER_ARGS names --env-file '${file}', which cannot be read on this machine. Nothing was changed."
    assert_no_docker_calls "${file}"
  done
  assert_container roof-controller old true unless-stopped
}

test_env_file_without_emulator_settings_is_passed_on() {
  seed_container roof-controller old true 8443:8443
  printf '# HatEmulator settings belong to the deployment script\n\nTZ=UTC\nLogging__LogLevel__Default=Information' \
    > "${WORK}/extra.env"
  deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--env-file ${WORK}/extra.env"

  assert_status 0
  assert_container roof-controller new true unless-stopped
  local args
  for args in "$(preflight_args)" "$(controller_run_args)"; do
    jq -e --arg file "${WORK}/extra.env" '.[index($file) - 1] == "--env-file"' <<<"${args}" >/dev/null \
      || fail_test "the --env-file was not passed on: ${args}"
  done
}

test_emulator_mode_refuses_extra_args_that_reach_the_hat() {
  seed_container roof-controller old true 8443:8443
  local case value expected
  for case in \
      "--device /dev/i2c-1|must not map an I2C device ('--device /dev/i2c-1')" \
      "--device=/dev/I2C-1:/dev/i2c-1|must not map an I2C device ('--device /dev/I2C-1:/dev/i2c-1')" \
      "--privileged|must not contain '--privileged' in HAT emulator mode: it maps every host device" \
      "--privileged=true|must not contain '--privileged=true' in HAT emulator mode" \
      "--privileged=1|must not contain '--privileged=1' in HAT emulator mode" \
      "--privileged=True|must not contain '--privileged=True' in HAT emulator mode" \
      "--privileged=t|must not contain '--privileged=t' in HAT emulator mode" \
      "--privileged=TRUE|must not contain '--privileged=TRUE' in HAT emulator mode" \
      "-v /dev:/dev|must not mount the host's /dev ('--volume /dev:/dev')" \
      "-v/dev/i2c-1:/dev/i2c-1|must not mount the host's /dev/i2c-1 ('--volume /dev/i2c-1:/dev/i2c-1')" \
      "-itv /:/host|must not mount the host's / ('--volume /:/host')" \
      "--volume=/dev:/dev:ro|must not mount the host's /dev ('--volume /dev:/dev:ro')" \
      "--mount type=bind,src=/dev,dst=/dev|must not mount the host's /dev ('--mount type=bind,src=/dev,dst=/dev')" \
      "--mount=type=bind,source=/dev/i2c-1,target=/dev/i2c-1|must not mount the host's /dev/i2c-1"; do
    value=${case%%|*}
    expected=${case#*|}
    : > "${FAKE_STATE_DIR}/calls.log"
    deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true "EXTRA_DOCKER_ARGS=--cpus 2 ${value}"
    assert_status 1
    assert_output_contains "EXTRA_DOCKER_ARGS ${expected}"
    assert_output_contains "HAT emulator mode"
    assert_no_docker_calls "${value}"
  done
  assert_container roof-controller old true unless-stopped
}

test_emulator_mode_allows_privileged_false() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" HAT_EMULATOR_ENDPOINT=hat-emulator:5291 ALLOW_EMULATED_HAT=true \
    "EXTRA_DOCKER_ARGS=--privileged=false --privileged=F --privileged=0"

  assert_status 0
  assert_container roof-controller new true unless-stopped
  jq -e 'index("--privileged=false") and index("--privileged=F") and index("--privileged=0")' <<<"$(controller_run_args)" >/dev/null \
    || fail_test "EXTRA_DOCKER_ARGS not passed on: $(controller_run_args)"
}

test_physical_deploy_allows_extra_devices_and_mounts() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" "EXTRA_DOCKER_ARGS=--device /dev/i2c-0 -v /dev/serial:/dev/serial --mount type=bind,src=/srv,dst=/srv"

  assert_status 0
  assert_container roof-controller new true unless-stopped
  jq -e '(.[index("/dev/i2c-0") - 1] == "--device") and (.[index("/dev/serial:/dev/serial") - 1] == "-v")
         and (.[index("type=bind,src=/srv,dst=/srv") - 1] == "--mount")' <<<"$(controller_run_args)" >/dev/null \
    || fail_test "EXTRA_DOCKER_ARGS not passed on: $(controller_run_args)"
}

test_rollback_to_a_version_deployed_for_the_emulator_needs_the_flag() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  seed_env roof-controller-previous HatEmulator__Enabled=true HatEmulator__Host=hat-emulator HatEmulator__Port=5291
  deploy "${HTTPS_ENV[@]}" -- --rollback

  # Known from how it was deployed, so refused before anything is stopped.
  assert_status 1
  assert_output_contains "roof-controller-previous was deployed for the HAT emulator at hat-emulator:5291, not the physical HAT, and does not operate the roof. Nothing was changed. To roll back to it on a test rig, set ALLOW_EMULATED_HAT=true."
  [[ -z "$(docker_calls stop)$(docker_calls rename)$(docker_calls update)$(docker_calls start)" ]] \
    || fail_test "the refused rollback changed a container: $(cat "${FAKE_STATE_DIR}/calls.log")"
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false

  deploy "${HTTPS_ENV[@]}" ALLOW_EMULATED_HAT=true -- --rollback
  assert_status 0
  assert_output_contains "[rollback] WARNING: roof-controller-previous was deployed for the HAT emulator at hat-emulator:5291 (ALLOW_EMULATED_HAT=true): the restored controller does not operate the roof."
  assert_output_contains "HAT: hatMode Emulated (ALLOW_EMULATED_HAT=true): the roof does not move."
  assert_container roof-controller old true unless-stopped

  # Swapping back to the physical version needs no flag.
  deploy "${HTTPS_ENV[@]}" -- --rollback
  assert_status 0
  assert_output_contains "HAT: hatMode Physical."
  assert_container roof-controller current true unless-stopped
}

test_rollback_to_an_emulator_version_that_does_not_become_ready_names_the_emulator() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  seed_env roof-controller-previous HatEmulator__Enabled=true HatEmulator__Host=hat-emulator HatEmulator__Port=5291
  deploy "${HTTPS_ENV[@]}" ALLOW_EMULATED_HAT=true FAKE_OLD_READY=false -- --rollback

  assert_status 1
  assert_output_contains "roof-controller did not become ready within 1s (it was deployed for the HAT emulator at hat-emulator:5291: check that the emulator is running). It is left running. Run --rollback again to swap back."
}

test_rollback_that_cannot_read_the_previous_environment_changes_nothing() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_FAIL=inspect -- --rollback

  assert_status 1
  assert_output_contains "Could not read the environment of roof-controller-previous from Docker. Nothing was changed; check the Docker context and retry."
  assert_container roof-controller current true unless-stopped
  assert_container roof-controller-previous old false
}

# A HatEmulator setting from elsewhere (the secrets directory) shows only once the restored version runs.
test_rollback_to_a_version_that_reports_the_emulator_needs_the_flag() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=Emulated -- --rollback

  # Like any failed check after the start, it is left running, and --rollback swaps back.
  assert_status 1
  assert_output_contains "roof-controller uses the HAT emulator (hatMode Emulated), not the physical HAT, and does not operate the roof, although it was not deployed for the emulator; check for HatEmulator settings in the secrets directory. It is left running. Run --rollback again to swap back."
  assert_container roof-controller old true unless-stopped
  assert_container roof-controller-previous current false no

  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=Emulated -- --rollback
  assert_status 0
  assert_output_contains "HAT: hatMode Physical."
  assert_container roof-controller current true unless-stopped

  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=Emulated ALLOW_EMULATED_HAT=true -- --rollback
  assert_status 0
  assert_output_contains "[done] Rolled back. roof-controller is verified at https://pi.test:8443. HAT: hatMode Emulated (ALLOW_EMULATED_HAT=true): the roof does not move. The replaced version"
  assert_container roof-controller old true unless-stopped
}

test_rollback_to_a_version_reporting_another_hat_is_refused() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=Simulation ALLOW_EMULATED_HAT=true -- --rollback

  assert_status 1
  assert_output_contains "roof-controller reports hatMode Simulation, not the physical HAT. It is left running."
}

test_rollback_to_a_version_without_hat_mode_is_physical() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=absent -- --rollback

  assert_status 0
  assert_output_contains "HAT: hatMode Physical (no hatMode: a version from before emulator mode)."
  assert_container roof-controller old true unless-stopped
}

test_rollback_to_a_version_without_hat_mode_on_the_register_simulation_fails() {
  seed_container roof-controller current true 8443:8443
  seed_container roof-controller-previous old false 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_OLD_HAT_MODE=absent FAKE_OLD_PHYSICAL=false -- --rollback

  assert_status_is 1
  assert_output_contains "reports no hatMode and isUsingPhysicalHardware false: a version from before emulator mode on the register simulation, not the physical HAT"
  assert_output_not_contains "HAT: hatMode Physical"
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

test_context_that_prompts_fails_before_anything_changes() {
  seed_container roof-controller old true 8443:8443
  deploy "${HTTPS_ENV[@]}" FAKE_CONTEXT_PROMPTS=true

  assert_status 1
  assert_output_contains "is not available without a terminal"
  assert_output_contains "it must connect without prompting"
  assert_output_contains "the context prompts for a password"
  [[ "$(wc -l < "${FAKE_STATE_DIR}/calls.log")" -eq 1 && -n "$(docker_calls info)" ]] \
    || fail_test "docker was called after the context check: $(tail -n +2 "${FAKE_STATE_DIR}/calls.log" | head -n 1)"
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
