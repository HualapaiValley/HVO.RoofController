#!/usr/bin/env bash
# The installer's rig role end to end (#69): hvo-roof-install, as published, on real Docker against the HAT emulator,
# installing a release as the release workflow publishes one.
#
#   install  The release (the controller and the HAT emulator, each an index of two platforms in a registry of this
#            run's own on loopback, and the release.json that names them by digest) installed as a test rig with
#            --answers and --release, as root: the plan, then the install. Then the folders and files with their modes,
#            the controller running the release's image against the emulator and serving HTTPS with the installer's CA
#            on loopback only, the web UI, the first admin signing in with the password given in a file, and no secret
#            in the output, the log, the record or the controller's environment.
#   again    The same answers again: nothing to change, and nothing is replaced.
#   change   A new time scale: the emulator is replaced and the controller redeployed against it.
#   cert     cert --renew --redeploy: a new certificate from the same CA, the CA unchanged, and the controller
#            redeployed to serve it.
# Throughout, the roof does not move: the relay register stays 0, and the emulator records no direction relay closing
# and no violation.
#
# Needs docker (buildx), the .NET SDK (to publish the installer), curl, jq, openssl, ss and sudo without a password: the
# installer runs as root, as a rig on Linux needs. It runs only against the local Docker daemon, which sudo must reach
# too, never on a Raspberry Pi, and only on a machine without the rig's folders (/etc/hvo-roof, /var/lib/hvo-roof), the
# installer's log, the containers (roof-controller, roof-controller-previous, hat-emulator) and the hvo-emulator
# network: it removes them all when it ends. The ports it uses must be free: RIG_HTTPS_PORT, RIG_WEB_PORT, 5290 (the
# emulator's control API) and RIG_REGISTRY_PORT. The images it built stay (the build cache); the ones pulled from its
# registry go.
#
#   tests/installer/rig-scenario.sh
#
# Settings (environment): RIG_HTTPS_PORT and RIG_WEB_PORT (the controller's API and web UI, default 8443 and 8088, as
# the installer's), RIG_REGISTRY_PORT (the run's registry on loopback, default 15001) and RIG_RESULTS_DIR (writes
# rig-scenario.md there: each check with its result and timing).
# The installer runs as root; what it prints, and the files read with sudo, go to this run's own files.
# shellcheck disable=SC2024
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

export DOCKER_CONTEXT=default
# The value is not printed: it can name a user and a host.
if [[ -n "${DOCKER_HOST:-}" && "${DOCKER_HOST}" != unix://* ]]; then
  echo "[rig] FAIL: DOCKER_HOST points at a Docker daemon that is not local; unset it to run against this machine's." >&2
  exit 1
fi
if [[ -e /dev/gpiomem ]] || grep -qs 'Raspberry Pi' /proc/device-tree/model; then
  echo "[rig] FAIL: this host is a Raspberry Pi; run the rig scenario on a PC or a CI runner." >&2
  exit 1
fi

https_port=${RIG_HTTPS_PORT:-8443}
web_port=${RIG_WEB_PORT:-8088}
registry_port=${RIG_REGISTRY_PORT:-15001}
registry_name=hvo-rig-scenario-registry
registry_image=registry:2@sha256:a3d8aaa63ed8681a604f1dea0aa03f100d5895b6a58ace528858a7b332415373
registry="127.0.0.1:${registry_port}/hualapaivalley"
controller="roof-controller"
emulator="hat-emulator"
network="hvo-emulator"
emulator_api="http://127.0.0.1:5290/api/emulator"
roof="https://localhost:${https_port}"
roof_api="${roof}/api/v4.0"
web="https://localhost:${web_port}"
install_log=/var/log/hvo-roof-install.log
ca=/etc/hvo-roof/ca.crt
admin=tester
case "$(uname -m)" in
  aarch64|arm64) platform=linux/arm64 other_platform=linux/amd64 rid=linux-arm64 ;;
  *) platform=linux/amd64 other_platform=linux/arm64 rid=linux-x64 ;;
esac

work=$(mktemp -d)
chmod 700 "${work}"
results="${work}/results.md"
: > "${results}"
owns_resources=0
relay_monitor_pid=""
current_check="setup"

say() {
  echo "[rig] $*"
}

record() {
  printf '| %s | %s | %s |\n' "${current_check}" "$1" "$2" >> "${results}"
}

fail() {
  echo "[rig] FAIL ${current_check}: $*" >&2
  record "FAIL" "$*"
  exit 1
}

pass() {
  say "PASS ${current_check}: $*"
  record "pass" "$*"
}

seconds_since() {
  awk -v s="$1" -v n="$(date +%s.%N)" 'BEGIN { printf "%.1f", n - s }'
}

write_results() {
  [[ -n "${RIG_RESULTS_DIR:-}" ]] || return 0
  mkdir -p "${RIG_RESULTS_DIR}"
  {
    echo "# The installer's rig role end to end"
    echo
    echo "hvo-roof-install for ${rid}, a release in a registry on loopback, the HAT emulator on real Docker. Timings are wall-clock seconds."
    echo
    echo "| Check | Result | Detail |"
    echo "|---|---|---|"
    cat "${results}"
  } > "${RIG_RESULTS_DIR}/rig-scenario.md"
}

cleanup() {
  local status=$?
  set +e
  stop_relay_monitor
  if (( owns_resources == 1 )); then
    if (( status != 0 )); then
      echo "[rig] Containers:" >&2
      docker ps -a --filter "name=${controller}" --filter "name=${emulator}" >&2
      # The installer's log holds no secret; the controller's lines are filtered of the exporter's and stack frames.
      echo "[rig] The installer's log:" >&2
      sudo -n tail -n 80 "${install_log}" >&2
      echo "[rig] Last log lines of ${controller}:" >&2
      docker logs --tail 400 "${controller}" 2>&1 \
        | grep -v -e 'OtlpMetricExporter' -e 'OtlpTraceExporter' -e 'OtlpLogExporter' -e '^ *at ' -e 'Connection refused' \
        | tail -n 40 >&2
    fi
    docker rm -f "${controller}" "${controller}-previous" "${emulator}" "${registry_name}" >/dev/null 2>&1
    docker network rm "${network}" >/dev/null 2>&1
    docker images --digests --format '{{.Repository}}@{{.Digest}}' 2>/dev/null \
      | grep "^${registry}/.*@sha256:" | xargs -r docker rmi >/dev/null 2>&1
    sudo -n rm -rf /etc/hvo-roof /var/lib/hvo-roof "${install_log}"
  fi
  write_results
  rm -rf "${work}"
  exit "${status}"
}
trap cleanup EXIT

# wait_for <what> <seconds> <command...>
wait_for() {
  local what=$1 seconds=$2
  shift 2
  local deadline=$((SECONDS + seconds))
  until "$@"; do
    (( SECONDS < deadline )) || fail "${what} was not ready within ${seconds} s"
    sleep 1
  done
}

container_id() {
  docker inspect --format '{{.Id}}' "$1" 2>/dev/null || true
}

# ---------------------------------------------------------------------------------------------------------------------
# The roof does not move.

start_relay_monitor() {
  : > "${work}/relays.log"
  (
    while true; do
      curl -fsS --max-time 2 "${emulator_api}/status" 2>/dev/null | jq -r '.plant.relayRegister' >> "${work}/relays.log" 2>/dev/null || true
      sleep 0.2
    done
  ) &
  relay_monitor_pid=$!
}

stop_relay_monitor() {
  if [[ -n "${relay_monitor_pid}" ]]; then
    kill "${relay_monitor_pid}" 2>/dev/null || true
    wait "${relay_monitor_pid}" 2>/dev/null || true
    relay_monitor_pid=""
  fi
}

# roof_still: the relay register was 0 in every sample the monitor took (the emulator is away while it is replaced),
# and the emulator's history has no direction relay closing, nor a violation; the drive is stopped.
roof_still() {
  stop_relay_monitor
  local samples energized status closed violations
  samples=$(grep -c . "${work}/relays.log" || true)
  energized=$(grep -vc '^0$' "${work}/relays.log" || true)
  (( samples > 0 )) || fail "the relay monitor took no samples"
  (( energized == 0 )) || fail "the relay register was energized in ${energized} of ${samples} samples: $(sort -u "${work}/relays.log" | tr '\n' ' ')"
  closed=$(curl -fsS --max-time 10 "${emulator_api}/history?limit=5000" \
    | jq '[.[] | select(.detail | test("^RLY[12] contact closed"))] | length') || fail "the emulator's history could not be read"
  (( closed == 0 )) || fail "the emulator recorded ${closed} direction relay closings"
  violations=$(curl -fsS --max-time 10 "${emulator_api}/violations" | jq 'length') || fail "the emulator's violations could not be read"
  (( violations == 0 )) || fail "the emulator recorded ${violations} violations: $(curl -fsS --max-time 10 "${emulator_api}/violations")"
  status=$(curl -fsS --max-time 10 "${emulator_api}/status") || fail "the emulator's status could not be read"
  jq -e '.plant.outputFrequencyHz == 0' <<<"${status}" >/dev/null || fail "the drive is running: $(jq -c '.plant' <<<"${status}")"
  ROOF_SAMPLES=${samples}
}

# ---------------------------------------------------------------------------------------------------------------------
# The installer, as root.

# install <name> <arguments...>: runs the installer with sudo, its output (stdout and stderr) in ${work}/<name>.txt and
# its exit status in INSTALL_STATUS.
INSTALL_STATUS=0
install() {
  local name=$1
  shift
  INSTALL_STATUS=0
  sudo -n "${installer}" "$@" > "${work}/${name}.txt" 2>&1 || INSTALL_STATUS=$?
  sed 's/^/[install] /' "${work}/${name}.txt"
}

# expect_installed <name>: the run exited 0.
expect_installed() {
  (( INSTALL_STATUS == 0 )) || fail "hvo-roof-install ${1} exited ${INSTALL_STATUS}"
}

expect_output() {
  grep -qF -- "$2" "${work}/$1.txt" || fail "hvo-roof-install ${1} did not say '$2'"
}

# answers <file> <time scale>: a rig's answers, as docs/install.md has them.
answers() {
  jq -n --arg host "$(hostname)" --argjson https "${https_port}" --argjson web "${web_port}" --argjson scale "$2" '{
    schema: 1,
    roles: ["rig"],
    controller: {
      httpsPort: $https,
      webPort: $web,
      firstAdmin: { name: "tester" },
      rig: { timeScale: $scale, cameraFramesPerSecond: 5 }
    },
    rigConfirmation: $host
  }' > "$1"
}

# ---------------------------------------------------------------------------------------------------------------------
# The controller, from this machine.

# roof_call <method> <path> [body file]: prints the body, then the HTTP status on the last line. The session's token
# goes on stdin, not the command line.
roof_call() {
  local body=()
  [[ -z "${3:-}" ]] || body=(-H 'Content-Type: application/json' --data-binary "@$3")
  printf 'Authorization: Bearer %s\n' "${token:-none}" \
    | curl -sS --max-time 15 --cacert "${ca}" -X "$1" -H @- -H 'Accept: application/json' ${body[@]+"${body[@]}"} \
        -w '\n%{http_code}' "${roof_api}/$2"
}

ready() {
  [[ "$(curl -sS --max-time 5 --cacert "${ca}" -o /dev/null -w '%{http_code}' "${roof}/health/ready" 2>/dev/null)" == 200 ]]
}

# served_certificate: the certificate the controller serves, as PEM.
served_certificate() {
  openssl s_client -connect "127.0.0.1:${https_port}" -servername localhost </dev/null 2>/dev/null | openssl x509
}

served_fingerprint() {
  served_certificate | openssl x509 -noout -fingerprint -sha256 | cut -d= -f2
}

# sign_in: the first admin signs in with the password given in the file, and is an admin; the token is in ${token}.
token=""
sign_in() {
  local response
  jq -n --arg name "${admin}" --rawfile password "${password_file}" '{name: $name, password: ($password | rtrimstr("\n"))}' \
    > "${work}/sign-in.json"
  response=$(roof_call POST Auth/Session "${work}/sign-in.json")
  rm -f "${work}/sign-in.json"
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || fail "${admin} could not sign in with the password in the file (HTTP $(tail -n 1 <<<"${response}"))"
  token=$(sed '$d' <<<"${response}" | jq -r '.token')
  [[ -n "${token}" && "${token}" != null ]] || fail "the sign-in answered no token"
  response=$(roof_call GET Auth/Me)
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || fail "Auth/Me answered HTTP $(tail -n 1 <<<"${response}")"
  sed '$d' <<<"${response}" | jq -e --arg name "${admin}" '.name == $name and .role == "RoofAdmin"' >/dev/null \
    || fail "${admin} is not an admin: $(sed '$d' <<<"${response}" | jq -c '{name, role, kind}')"
}

# emulated_status: the controller reports the emulated HAT, and the roof as idle.
emulated_status() {
  local response
  response=$(roof_call GET RoofControl/Status)
  [[ "$(tail -n 1 <<<"${response}")" == 200 ]] || fail "Status answered HTTP $(tail -n 1 <<<"${response}")"
  sed '$d' <<<"${response}" | jq -e '.hatMode == "Emulated"' >/dev/null \
    || fail "the controller does not report the emulated HAT: $(sed '$d' <<<"${response}" | jq -c '{hatMode, isUsingPhysicalHardware}')"
}

# no_secret_in <file...>: none of the run's secrets (the password, the keys and the certificate's password) is in them.
# Nothing here prints a secret: grep only says whether one matched.
no_secret_in() {
  local file
  for file in "$@"; do
    if grep -qF -f "${work}/secrets.txt" "${file}"; then
      fail "a secret is in $(basename "${file}")"
    fi
  done
}

collect_secrets() {
  local file
  : > "${work}/secrets.txt"
  head -n 1 "${password_file}" >> "${work}/secrets.txt"
  for file in $(sudo -n find /etc/hvo-roof/secrets -type f \( -name 'RoofControllerSecurity__ApiKeys__*__Key' -o -name 'Kestrel__Certificates__Default__Password' \)); do
    sudo -n head -n 1 "${file}" >> "${work}/secrets.txt"
    echo >> "${work}/secrets.txt"
  done
  # An empty pattern would match every line.
  sed -i '/^$/d' "${work}/secrets.txt"
  (( $(wc -l < "${work}/secrets.txt") >= 5 )) || fail "the installer wrote fewer keys than it should: $(( $(wc -l < "${work}/secrets.txt") - 1 ))"
}

# mode_is <mode> <path...>: each path has that mode and is root's.
mode_is() {
  local mode=$1 path actual
  shift
  for path in "$@"; do
    actual=$(sudo -n stat -c '%a %U' "${path}") || fail "there is no ${path}"
    [[ "${actual}" == "${mode} root" ]] || fail "${path} is ${actual}, not ${mode} root"
  done
}

# loopback_only <container>: the container publishes ports, each on loopback only.
loopback_only() {
  local ports
  ports=$(docker port "$1") || fail "could not read the ports $1 publishes"
  [[ -n "${ports}" ]] || fail "$1 publishes nothing"
  if grep -vqE '(^| )127\.0\.0\.1:' <<<"${ports}"; then
    fail "$1 publishes a port beyond loopback: $(tr '\n' ' ' <<<"${ports}")"
  fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Setup: the installer, the release in a registry of this run's own, and the answers.

setup() {
  local tool
  for tool in docker curl jq openssl dotnet ss; do
    command -v "${tool}" >/dev/null || fail "${tool} is required"
  done
  docker buildx version >/dev/null 2>&1 || fail "docker buildx is required"
  sudo -n true 2>/dev/null || fail "sudo must run without a password: the installer installs a rig as root"
  # The installer, as root, must install the rig on the daemon this run checks and cleans up.
  local daemon
  daemon=$(docker info -f '{{.ID}}') || fail "docker cannot reach this machine's Docker daemon"
  [[ -n "${daemon}" && "${daemon}" == "$(sudo -n docker info -f '{{.ID}}' 2>/dev/null)" ]] \
    || fail "sudo reaches another Docker daemon than this shell's, or none: run the scenario where both reach this machine's"
  if sudo -n test -e /etc/hvo-roof || sudo -n test -e /var/lib/hvo-roof || sudo -n test -e "${install_log}"; then
    fail "/etc/hvo-roof, /var/lib/hvo-roof or ${install_log} exists: this machine has a controller or a rig. Run the scenario where there is none."
  fi
  if [[ -n "$(docker ps -aq --filter "name=^/(${controller}|${controller}-previous|${emulator}|${registry_name})$")" ]] \
    || docker network inspect "${network}" >/dev/null 2>&1; then
    fail "${controller}, ${emulator}, ${registry_name} or the ${network} network exists: remove them first."
  fi
  local port out
  for port in "${https_port}" "${web_port}" 5290 "${registry_port}"; do
    out=$(ss -ltnH "sport = :${port}") || fail "ss could not list the ports in use"
    [[ -z "${out}" ]] || fail "port ${port} is in use"
  done
  owns_resources=1

  say "Publishing hvo-roof-install for ${rid}"
  (cd "${repo_root}/src" && dotnet publish HVO.RoofControllerV4.Installer -c Release -r "${rid}" -v quiet -nologo \
    -o "${work}/installer" >/dev/null) || fail "could not publish hvo-roof-install"
  installer="${work}/installer/hvo-roof-install"
  version=$("${installer}" --version)
  version=${version%%+*}
  [[ -n "${version}" ]] || fail "hvo-roof-install printed no version"

  docker run -d --name "${registry_name}" -p "127.0.0.1:${registry_port}:5000" "${registry_image}" >/dev/null \
    || fail "could not start the registry"
  wait_for "the registry" 60 curl -fs --max-time 2 -o /dev/null "http://127.0.0.1:${registry_port}/v2/"

  local started
  started=$(date +%s.%N)
  say "Building and pushing the release's images (${version})"
  push_release_image roof-controller "${repo_root}/src/HVO.RoofControllerV4.RPi/Dockerfile"
  controller_digest=${PUSHED_INDEX}
  push_release_image roof-hat-emulator "${repo_root}/src/HVO.RoofControllerV4.Emulator/Dockerfile"
  emulator_digest=${PUSHED_INDEX}

  release_dir="${work}/release"
  mkdir -p "${release_dir}"
  jq -n --arg version "${version}" --arg commit "$(git -C "${repo_root}" rev-parse HEAD 2>/dev/null || echo unknown)" \
    --arg registry "${registry}" --arg controller "${controller_digest}" --arg emulator "${emulator_digest}" '
    def image($name; $digest): { repository: "\($registry)/\($name)", tag: $version, digest: $digest,
      reference: "\($registry)/\($name):\($version)@\($digest)", platforms: ["linux/amd64", "linux/arm64"] };
    { schemaVersion: 1, product: "HVO Roof Controller", version: $version, tag: "v\($version)",
      prerelease: ($version | contains("-")), commit: $commit,
      images: { controller: image("roof-controller"; $controller), hatEmulator: image("roof-hat-emulator"; $emulator) } }' \
    > "${release_dir}/release.json"

  password_file="${work}/admin-password"
  (umask 077 && openssl rand -base64 24 > "${password_file}")
  answers "${work}/rig.json" 10
  answers "${work}/rig-faster.json" 20
  current_check="setup"
  pass "published hvo-roof-install ${version} for ${rid}; the controller and the HAT emulator pushed as indexes of ${platform} and ${other_platform} in $(seconds_since "${started}") s"
}

# push_release_image <name> <Dockerfile>: the image as a release publishes it, an index of two platforms: this
# platform's image built from the Dockerfile with the release's version, and an empty image for the other one, which
# builds without emulation. The index's digest is in PUSHED_INDEX.
PUSHED_INDEX=""
push_release_image() {
  local repository="${registry}/$1" single other output
  output=$(docker buildx build --quiet --provenance=false --platform "${platform}" -f "$2" \
    --build-arg "ROOF_VERSION=${version}" --build-arg "ROOF_REVISION=$(git -C "${repo_root}" rev-parse HEAD 2>/dev/null || true)" \
    -t "${repository}:${version}-${platform#linux/}" --load "${repo_root}" 2>&1) || fail "could not build $1 for ${platform}: $(tail -n 20 <<<"${output}")"
  single=$(push_digest "${repository}:${version}-${platform#linux/}")
  mkdir -p "${work}/other-$1"
  printf 'FROM scratch\nLABEL org.opencontainers.image.version=%s\n' "${version}" > "${work}/other-$1/Dockerfile"
  docker buildx build --quiet --provenance=false --platform "${other_platform}" -t "${repository}:${version}-${other_platform#linux/}" \
    --load "${work}/other-$1" >/dev/null || fail "could not build the empty ${other_platform} image of $1"
  other=$(push_digest "${repository}:${version}-${other_platform#linux/}")
  docker buildx imagetools create --progress quiet -t "${repository}:${version}" "${repository}@${single}" "${repository}@${other}" >/dev/null \
    || fail "could not push the index of $1"
  PUSHED_INDEX=$(docker buildx imagetools inspect --format '{{json .Manifest}}' "${repository}:${version}" | jq -r .digest)
  [[ "${PUSHED_INDEX}" =~ ^sha256:[0-9a-f]{64}$ && "${PUSHED_INDEX}" != "${single}" ]] \
    || fail "the registry reported no index digest for ${repository}:${version}: '${PUSHED_INDEX}'"
}

# push_digest <tag>: pushes the tag, removes it here (the installer pulls the release's images), and prints its digest.
push_digest() {
  local output digest
  output=$(docker push "$1") || fail "could not push $1: ${output}"
  digest=$(sed -n 's/^.*: digest: \(sha256:[0-9a-f]\{64\}\) .*/\1/p' <<<"${output}")
  [[ -n "${digest}" ]] || fail "the push of $1 reported no digest: ${output}"
  docker rmi "$1" >/dev/null
  echo "${digest}"
}

# ---------------------------------------------------------------------------------------------------------------------
# The scenario.

scenario_install() {
  current_check="install: the plan"
  install plan --plan --answers "${work}/rig.json" --release "${release_dir}"
  expect_installed "--plan"
  expect_output plan "The plan for a test rig on $(hostname), ${version}:"
  expect_output plan "0 to change, 0 unchanged."
  sudo -n test ! -e /etc/hvo-roof || fail "--plan made /etc/hvo-roof"
  [[ -z "$(docker ps -aq --filter "name=^/(${controller}|${emulator})$")" ]] || fail "--plan started a container"
  pass "planned $(grep -oE '^[0-9]+ to create' "${work}/plan.txt"), and changed nothing"

  current_check="install: a test rig from the release"
  local started
  started=$(date +%s.%N)
  # The monitor samples once the install has started the emulator.
  start_relay_monitor
  install install --answers "${work}/rig.json" --release "${release_dir}" --admin-password-file "${password_file}"
  expect_installed "--answers"
  expect_output install "nothing here moves a roof."
  local seconds
  seconds=$(seconds_since "${started}")
  [[ "$(docker inspect --format '{{.Config.Image}}' "${controller}")" == "$(jq -r .images.controller.reference "${release_dir}/release.json")" ]] \
    || fail "${controller} does not run the release's image"
  [[ "$(docker inspect --format '{{.Config.Image}}' "${emulator}")" == "$(jq -r .images.hatEmulator.reference "${release_dir}/release.json")" ]] \
    || fail "${emulator} does not run the release's image"
  [[ "$(docker inspect --format '{{.HostConfig.NetworkMode}}' "${controller}")" == "${network}" ]] || fail "${controller} is not on ${network}"
  loopback_only "${controller}"
  loopback_only "${emulator}"
  pass "installed in ${seconds} s: ${controller} and ${emulator} run the release's images by digest on ${network}, published on loopback only"

  current_check="install: folders and files"
  mode_is 755 /etc/hvo-roof /etc/hvo-roof/config /var/lib/hvo-roof
  mode_is 700 /etc/hvo-roof/secrets /etc/hvo-roof/https /etc/hvo-roof/ca /var/lib/hvo-roof/identity /var/lib/hvo-roof/settings-secrets
  mode_is 644 "${ca}" /etc/hvo-roof/install.json
  local file
  for file in $(sudo -n find /etc/hvo-roof/secrets /etc/hvo-roof/https /etc/hvo-roof/ca -type f); do
    mode_is 600 "${file}"
  done
  sudo -n jq -e '.roles == ["rig"]' /etc/hvo-roof/install.json >/dev/null || fail "the install record does not say the rig role"
  pass "the folders, the keys, the CA and the certificate have the modes deployment.md gives, and the record says the rig role"

  current_check="install: the controller"
  wait_for "the controller" 60 ready
  sign_in
  emulated_status
  [[ "$(curl -sS --max-time 10 --cacert "${ca}" -o /dev/null -w '%{http_code}' "${web}/")" =~ ^(200|302)$ ]] || fail "the web UI does not answer at ${web}"
  openssl verify -CAfile "${ca}" <(openssl s_client -connect "127.0.0.1:${https_port}" -servername localhost </dev/null 2>/dev/null | openssl x509) >/dev/null \
    || fail "the controller's certificate does not verify with the installer's CA"
  pass "ready over HTTPS with the installer's CA, the web UI answers, ${admin} signs in as an admin with the password in the file, and Status says hatMode Emulated"

  current_check="install: no secret shown"
  collect_secrets
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  sudo -n cat /etc/hvo-roof/install.json > "${work}/record.txt"
  docker inspect "${controller}" "${emulator}" > "${work}/containers.txt"
  no_secret_in "${work}/plan.txt" "${work}/install.txt" "${work}/install-log.txt" "${work}/record.txt" "${work}/containers.txt"
  pass "none of the $(wc -l < "${work}/secrets.txt") secrets is in the plan, the output, the log, the record or the containers' configuration"

  current_check="install: the roof"
  roof_still
  pass "relay register 0 in ${ROOF_SAMPLES} samples; no direction relay closed; no violation"
}

scenario_again() {
  current_check="again: nothing to change"
  local controller_id emulator_id
  controller_id=$(container_id "${controller}")
  emulator_id=$(container_id "${emulator}")
  start_relay_monitor
  install again --answers "${work}/rig.json" --release "${release_dir}"
  expect_installed "--answers (again)"
  expect_output again "Nothing to change"
  [[ "$(container_id "${controller}")" == "${controller_id}" ]] || fail "${controller} was replaced"
  [[ "$(container_id "${emulator}")" == "${emulator_id}" ]] || fail "${emulator} was replaced"
  roof_still
  no_secret_in "${work}/again.txt"
  pass "nothing to change and nothing replaced, with no password given; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_change() {
  current_check="change: a new time scale"
  local controller_id emulator_id
  controller_id=$(container_id "${controller}")
  emulator_id=$(container_id "${emulator}")
  install change-plan --plan --answers "${work}/rig-faster.json" --release "${release_dir}"
  expect_installed "--plan (faster)"
  expect_output change-plan "replaced to run 20 times as fast as real time"
  start_relay_monitor
  install change --answers "${work}/rig-faster.json" --release "${release_dir}"
  expect_installed "--answers (faster)"
  [[ "$(container_id "${emulator}")" != "${emulator_id}" ]] || fail "${emulator} was not replaced"
  [[ "$(container_id "${controller}")" != "${controller_id}" ]] || fail "${controller} was not redeployed"
  curl -fsS --max-time 10 "${emulator_api}/status" | jq -e '.plant.timeScale == 20' >/dev/null || fail "the emulator does not run 20 times as fast"
  wait_for "the controller" 60 ready
  emulated_status
  roof_still
  no_secret_in "${work}/change-plan.txt" "${work}/change.txt"
  pass "the emulator replaced at 20 times real time, and the controller redeployed against it; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_cert() {
  current_check="cert: renewed and served"
  local controller_id before after
  controller_id=$(container_id "${controller}")
  before=$(served_fingerprint)
  [[ -n "${before}" ]] || fail "the controller serves no certificate"
  sudo -n cat "${ca}" > "${work}/ca-before.crt" || fail "could not read ${ca}"
  start_relay_monitor
  install cert cert --renew --redeploy --release "${release_dir}"
  expect_installed "cert --renew --redeploy"
  [[ "$(container_id "${controller}")" != "${controller_id}" ]] || fail "${controller} was not redeployed"
  wait_for "the controller" 60 ready
  after=$(served_fingerprint)
  [[ -n "${after}" && "${after}" != "${before}" ]] || fail "the controller still serves the old certificate"
  sudo -n cmp -s "${work}/ca-before.crt" "${ca}" || fail "the renewal replaced the CA"
  served_certificate > "${work}/served.crt" || fail "could not read the certificate the controller serves"
  openssl verify -CAfile "${work}/ca-before.crt" "${work}/served.crt" >/dev/null \
    || fail "the certificate the controller serves is not from the CA it had before the renewal"
  sign_in
  roof_still
  collect_secrets
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/cert.txt" "${work}/install-log.txt"
  pass "a new certificate from the same CA, which is unchanged, served by the redeployed controller; relay register 0 in ${ROOF_SAMPLES} samples"
}

setup
scenario_install
scenario_again
scenario_change
scenario_cert
current_check="done"
say "All checks passed."
