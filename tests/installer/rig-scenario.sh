#!/usr/bin/env bash
# The installer's rig role end to end (#69, #74): install.sh and hvo-roof-install, as a release publishes them, on real
# Docker against the HAT emulator.
#
#   install  The installer's plan for a test rig, as root; then the release installed as one by its install.sh, run as
#            the one-line install runs it (bash reading the script on its standard input, here with no terminal, as
#            cron or CI runs it) with --from (the release's files in a folder) and the installer's --answers: it checks
#            this machine, takes hvo-roof-install from the folder, checks its SHA-256 against SHA256SUMS and, with gh
#            signed in, its attestation, and starts it as root (sudo). Then the folders and files with their modes, the
#            controller running the release's image against the emulator and serving HTTPS with the installer's CA on
#            loopback only, the web UI, the first admin signing in with the password given in a file, and no secret in
#            the output, the log, the record or the controller's environment.
#   again    The same answers again: nothing to change, and nothing is replaced.
#   cli      hvo-roof for the person running the scenario (not root, in a home of this run's own) with the rig as its
#            controller, trusting the rig's CA by its fingerprint (#71): a wrong fingerprint refused with nothing
#            changed, then install.sh installing the program and its connection, with their modes, hvo-roof login with
#            the password on standard input, hvo-roof status reporting the emulated HAT, the same answers again changing
#            nothing, and no secret shown.
#   motion   hvo-roof open, followed to the open limit; close --no-wait at real time, and hvo-roof stop part way: the
#            Stop acknowledged, the drive stopped between the limits with the relays open; then hvo-roof close, followed
#            to the closed limit. The emulator records each direction relay closing, and no violation.
#   change   A new time scale: the emulator is replaced and the controller redeployed against it.
#   cert     cert --renew --redeploy: a new certificate from the same CA, the CA unchanged, and the controller
#            redeployed to serve it; hvo-roof, which trusts the CA, signs in and reads the status from it still.
#   names    A new address (a dummy interface with 10.213.47.1): cert issues the certificate again for it from the same
#            CA, and redeploys the controller, which answers at the address; hvo-roof, its connection unchanged, signs
#            in still. The address gone, cert drops it. Then a new name (controller.hostNames, roof-rig): the CA may not
#            issue for it, so the installer makes a new CA and the controller answers to the name with a certificate
#            from it; hvo-roof is given the new CA's fingerprint to trust in its place, and signs in.
#   backup   backup, into a folder only root reads: one archive, root's and 0600, holding the keys, the CA, the
#            certificate, the people and the record, with no secret in what it prints or logs.
#   restore  uninstall --purge, as a script runs it (--confirm and --no-backup), with the installed hvo-roof-install:
#            the containers, the network, the release's images, the data and the installer go, after a verified Stop.
#            Then restore puts the backup back and installs what its record says: the same CA, certificate and keys,
#            the admin signs in with the same password, and upgrade with the installed hvo-roof-install changes nothing.
#   upgrade  The release before (RIG_PREVIOUS_REF, main's commit by default, built here as 4.0.0-rig.1 with its own
#            images) installed by its own installer, after another purge. Then upgrade, run by this source's installer
#            labelled 4.0.0-rig.1, as the one installed with the release before would be: it shows the upgrade notes,
#            puts release 4.0.0-rig.2's installer in place and hands over to it, which redeploys the controller,
#            keeping the old one as roof-controller-previous, with the data kept. Again: nothing to change.
#   rollback rollback to 4.0.0-rig.1 (the kept controller put back), rollback again (nothing changes), then upgrade
#            forward again.
# Except in motion, the roof does not move: the relay register stays 0, and the emulator records no direction relay
# closing and no violation.
#
# The release comes from one of two places:
#   This source (the default). The release is made here as the release workflow makes one: install.sh with the
#            release's version written in (build/release-assets.py), hvo-roof-install and hvo-roof published for this
#            machine, the controller and the HAT emulator built and pushed, each an index of two platforms, to a
#            registry of this run's own on loopback, release.json naming them by digest, and SHA256SUMS. install.sh
#            does not see gh, so it notes that the attestation was not checked, as on a machine without gh.
#   A release (RIG_RELEASE_DIR). The release's assets as the release workflow drafts them, its images in its registry
#            (GHCR): sudo's Docker must be able to pull them. install.sh checks the installer's attestation with gh,
#            which must be signed in (GH_TOKEN). upgrade and rollback, which need the release before built here, are
#            left out: the run from this source checks them.
#
# Needs docker, curl, jq, openssl, ss, ip, setsid, python3 and sudo without a password: the installer runs as root, as
# a rig on Linux needs. From this source, also docker buildx, git (the release before is built from a worktree of
# RIG_PREVIOUS_REF) and the .NET SDK. It runs only against the local Docker daemon, which sudo must reach too, never on
# a Raspberry Pi, and only on a machine without the rig's folders (/etc/hvo-roof, /var/lib/hvo-roof), the installer's
# log, the installed installer (/usr/local/sbin/hvo-roof-install), the containers (roof-controller,
# roof-controller-previous, hat-emulator), the hvo-emulator network and a network interface named hvorig0: it removes
# them all when it ends, with its backup folder and its worktree. The ports it uses must be free: RIG_HTTPS_PORT,
# RIG_WEB_PORT, 5290 (the emulator's control API) and, from this source, RIG_REGISTRY_PORT. The images it built stay
# (the build cache); each release's images, by digest, go.
#
#   tests/installer/rig-scenario.sh
#
# Settings (environment): RIG_HTTPS_PORT and RIG_WEB_PORT (the controller's API and web UI, default 8443 and 8088, as
# the installer's), RIG_REGISTRY_PORT (the run's registry on loopback, default 15001), RIG_PREVIOUS_REF (the commit
# the release before is built from, default origin/main; a CI checkout needs fetch-depth 0), RIG_RELEASE_DIR (a
# release's assets: see above) and RIG_RESULTS_DIR (writes rig-scenario.md there: each check with its result and
# timing). The backup is never written there.
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
previous_ref=${RIG_PREVIOUS_REF:-origin/main}
release_source=${RIG_RELEASE_DIR:-}
# This source is the release, unless RIG_RELEASE_DIR gives one; the release before is a build of previous_ref, labelled
# as the one before it.
version=4.0.0-rig.2
previous_version=4.0.0-rig.1
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
installed=/usr/local/sbin/hvo-roof-install
ca=/etc/hvo-roof/ca.crt
admin=tester
# The dummy interface that gives this machine a new address, and the address.
interface=hvorig0
new_address=10.213.47.1
case "$(uname -m)" in
  aarch64|arm64) platform=linux/arm64 other_platform=linux/amd64 rid=linux-arm64 ;;
  *) platform=linux/amd64 other_platform=linux/arm64 rid=linux-x64 ;;
esac

work=$(mktemp -d)
# As install.sh names it: the folder's physical path.
work=$(cd "${work}" && pwd -P)
chmod 700 "${work}"
results="${work}/results.md"
: > "${results}"
owns_resources=0
previous_root="${work}/previous"
previous_worktree=0
backup_dir=""
release_dir=""
previous_dir=""
made_interface=0
relay_monitor_pid=""
hide_gh=()
attestation=""
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
    # Each release's images, by digest: the ones the installer pulled.
    local folder
    for folder in "${release_dir}" "${previous_dir}"; do
      [[ -n "${folder}" && -f "${folder}/release.json" ]] || continue
      jq -r '.images[] | "\(.repository)@\(.digest)"' "${folder}/release.json" | xargs -r docker rmi >/dev/null 2>&1
    done
    (( made_interface == 0 )) || sudo -n ip link delete "${interface}" >/dev/null 2>&1
    sudo -n rm -rf /etc/hvo-roof /var/lib/hvo-roof "${install_log}" "${installed}" "${installed}.previous"
    [[ -z "${backup_dir}" ]] || sudo -n rm -rf "${backup_dir}"
  fi
  if (( previous_worktree == 1 )); then
    git -C "${repo_root}" worktree remove --force "${previous_root}" >/dev/null 2>&1
    git -C "${repo_root}" worktree prune >/dev/null 2>&1
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

# relays_still: the relay register was 0 in every sample the monitor took while the emulator was there (an uninstall
# removes it, with its history).
relays_still() {
  stop_relay_monitor
  local samples energized
  samples=$(grep -c . "${work}/relays.log" || true)
  energized=$(grep -vc '^0$' "${work}/relays.log" || true)
  (( samples > 0 )) || fail "the relay monitor took no samples"
  (( energized == 0 )) || fail "the relay register was energized in ${energized} of ${samples} samples: $(sort -u "${work}/relays.log" | tr '\n' ' ')"
  ROOF_SAMPLES=${samples}
}

# ---------------------------------------------------------------------------------------------------------------------
# The installer, as root.

# install <name> <arguments...>: runs the installer with sudo, its output (stdout and stderr) in ${work}/<name>.txt and
# its exit status in INSTALL_STATUS.
INSTALL_STATUS=0
install() {
  install_with "${installer}" "$@"
}

# install_with <program> <name> <arguments...>: install, with another hvo-roof-install: the installed one, or a release's.
install_with() {
  local program=$1 name=$2
  shift 2
  INSTALL_STATUS=0
  sudo -n "${program}" "$@" > "${work}/${name}.txt" 2>&1 || INSTALL_STATUS=$?
  sed 's/^/[install] /' "${work}/${name}.txt"
}

# install_sh <name> <arguments...>: the release's install.sh, run as the one-line install runs it (bash reading the
# script on its standard input), in a session of its own with no terminal, as cron or CI runs it, with --from the
# release's folder and the arguments; it starts the installer as root itself, with sudo. Its output in
# ${work}/<name>.txt and its exit status in INSTALL_STATUS.
install_sh() {
  local name=$1
  shift
  INSTALL_STATUS=0
  ${hide_gh[@]+"${hide_gh[@]}"} setsid -w bash -s -- --from "${release_dir}" "$@" < "${release_dir}/install.sh" \
    > "${work}/${name}.txt" 2>&1 || INSTALL_STATUS=$?
  sed 's/^/[install.sh] /' "${work}/${name}.txt"
}

# expect_installed <name>: the run exited 0.
expect_installed() {
  (( INSTALL_STATUS == 0 )) || fail "hvo-roof-install ${1} exited ${INSTALL_STATUS}"
}

# expect_install_sh <name> <role> <as>: install.sh said what it does, for the role, installed as <as> (root or you),
# took the release's installer from the folder, checked it, and started it.
expect_install_sh() {
  expect_output "$1" "HVO Roof Controller ${version}: install.sh"
  expect_output "$1" "  ok    for $2 (from the answers file), installed as $3"
  expect_output "$1" "Taking hvo-roof-install ${version} for ${rid} from ${release_dir}"
  expect_output "$1" "  ok    hvo-roof-install-${rid}'s SHA-256 is the one SHA256SUMS lists"
  expect_output "$1" "$4"
  if [[ "$3" == root ]]; then
    expect_output "$1" "Starting hvo-roof-install ${version} as root (sudo)"
  else
    expect_output "$1" "Starting hvo-roof-install ${version}"
    ! grep -qF "as root (sudo)" "${work}/$1.txt" || fail "install.sh started hvo-roof-install ${2} as root"
  fi
}

expect_output() {
  grep -qF -- "$2" "${work}/$1.txt" || fail "hvo-roof-install ${1} did not say '$2'"
}

# record_says <jq filter>: the install record matches it.
record_says() {
  sudo -n jq -e "$1" /etc/hvo-roof/install.json >/dev/null \
    || fail "the install record is not $1: $(sudo -n jq -c '{version, previousVersion, rolledBackFrom, roles}' /etc/hvo-roof/install.json 2>&1)"
}

# runs <container> <release folder> <controller|hatEmulator>: the container runs that release's image, by digest.
runs() {
  local expected
  expected=$(jq -r ".images.$3.reference" "$2/release.json")
  [[ "$(docker inspect --format '{{.Config.Image}}' "$1" 2>/dev/null)" == "${expected}" ]] \
    || fail "$1 does not run ${expected}: $(docker inspect --format '{{.Config.Image}}' "$1" 2>&1)"
}

# keys_sha256: the SHA-256 of each key, the CA's files and the certificate (the values are never read here).
keys_sha256() {
  sudo -n find /etc/hvo-roof/secrets /etc/hvo-roof/ca /etc/hvo-roof/https "${ca}" -type f -exec sha256sum {} + | sort -k2
}

# purged: nothing the installer made is left: the containers, the network, the release's images (by digest), the data,
# the record and the installed installer.
purged() {
  [[ -z "$(docker ps -aq --filter "name=^/(${controller}|${controller}-previous|${emulator})$")" ]] \
    || fail "a container is left: $(docker ps -a --format '{{.Names}}' --filter "name=${controller}" --filter "name=${emulator}" | tr '\n' ' ')"
  ! docker network inspect "${network}" >/dev/null 2>&1 || fail "the ${network} network is left"
  local folder reference
  for folder in /etc/hvo-roof /var/lib/hvo-roof "${installed}"; do
    ! sudo -n test -e "${folder}" || fail "${folder} is left"
  done
  for reference in "$@"; do
    ! docker image inspect "${reference}" >/dev/null 2>&1 || fail "the image ${reference} is left"
  done
}

# ---------------------------------------------------------------------------------------------------------------------
# hvo-roof, installed for the person running the scenario: not root, and in a home of this run's own with a clean
# environment, so the person's own ~/.local/bin, ~/.config and HVO_ROOF_URL are neither used nor touched.

person_home=""
person_cli=""

as_person() {
  env -i PATH=/usr/local/bin:/usr/bin:/bin HOME="${person_home}" LANG=C.UTF-8 TERM=dumb "$@"
}

# person_install <name> <arguments...>: the installer as the person; its output in ${work}/<name>.txt and its exit
# status in INSTALL_STATUS.
person_install() {
  local name=$1
  shift
  INSTALL_STATUS=0
  as_person "${installer}" "$@" > "${work}/${name}.txt" 2>&1 || INSTALL_STATUS=$?
  sed 's/^/[install] /' "${work}/${name}.txt"
}

# person_install_sh <name> <arguments...>: install_sh as the person, who has no gh signed in.
person_install_sh() {
  local name=$1
  shift
  INSTALL_STATUS=0
  as_person setsid -w bash -s -- --from "${release_dir}" "$@" < "${release_dir}/install.sh" > "${work}/${name}.txt" 2>&1 \
    || INSTALL_STATUS=$?
  sed 's/^/[install.sh] /' "${work}/${name}.txt"
}

# hvo_roof <name> <arguments...>: the installed hvo-roof as the person, its standard input the caller's; its output in
# ${work}/<name>.txt, which it prints.
hvo_roof() {
  local name=$1 status=0
  shift
  as_person "${person_cli}" "$@" > "${work}/${name}.txt" 2>&1 || status=$?
  sed 's/^/[hvo-roof] /' "${work}/${name}.txt"
  (( status == 0 )) || fail "hvo-roof $* exited ${status}"
}

# hvo_roof_json <name> <arguments...>: hvo-roof as the person, its standard output (the JSON --json writes) in
# ${work}/<name>.txt and its standard error in ${work}/<name>.err.txt, both printed; its exit status in CLI_STATUS.
CLI_STATUS=0
hvo_roof_json() {
  local name=$1
  shift
  CLI_STATUS=0
  as_person "${person_cli}" "$@" > "${work}/${name}.txt" 2> "${work}/${name}.err.txt" </dev/null || CLI_STATUS=$?
  sed 's/^/[hvo-roof] /' "${work}/${name}.txt" "${work}/${name}.err.txt"
}

# cli_signs_in: hvo-roof signs in as the first admin with the password on standard input, and reads the status: the
# emulated HAT. The session it saves is added to the secrets that must not show.
cli_signs_in() {
  hvo_roof cli-login login "${admin}" < "${password_file}"
  grep -qF "Signed in as ${admin}" "${work}/cli-login.txt" || fail "hvo-roof login did not say ${admin} signed in"
  jq -e '.session.token | length > 0' "${person_home}/.config/hvo-roof/credentials.json" >/dev/null \
    || fail "hvo-roof login saved no session"
  jq -r '.session.token' "${person_home}/.config/hvo-roof/credentials.json" >> "${work}/secrets.txt"
  hvo_roof cli-status --json status
  jq -e '.hatMode == "Emulated"' "${work}/cli-status.txt" >/dev/null || fail "hvo-roof status does not report the emulated HAT"
}

# cli_answers <file> <CA fingerprint>: hvo-roof's answers, as docs/install.md has them, with the rig as the controller.
cli_answers() {
  jq -n --arg controller "${roof}" --arg fingerprint "$2" '{ roles: ["cli"], client: { controller: $controller, caSha256: $fingerprint } }' > "$1"
}

# answers <file> <time scale> [<host name>]: a rig's answers, as docs/install.md has them, with the other short name
# clients use for it, if one is given.
answers() {
  jq -n --arg host "$(hostname)" --argjson https "${https_port}" --argjson web "${web_port}" --argjson scale "$2" \
    --arg name "${3:-}" '{
    schema: 1,
    roles: ["rig"],
    controller: ({
      httpsPort: $https,
      webPort: $web,
      firstAdmin: { name: "tester" },
      rig: { timeScale: $scale, cameraFramesPerSecond: 5 }
    } + (if $name == "" then {} else { hostNames: [$name] } end)),
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

# answers_at <address or name> <CA file> [<curl options...>]: the controller, reached at the address or name (on
# loopback, where it listens), is ready over HTTPS with a certificate the CA issued for it, and answers to it (its
# AllowedHosts).
answers_at() {
  local at=$1 authority=$2
  shift 2
  [[ "$(curl -sS --max-time 10 --cacert "${authority}" "$@" -o /dev/null -w '%{http_code}' "https://${at}:${https_port}/health/ready")" == 200 ]]
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

# plant: the emulated roof, as the emulator's control API reports it. plant_is <jq filter>: it matches the filter.
plant() {
  curl -fsS --max-time 10 "${emulator_api}/status" | jq -c '.plant | {positionMeters, velocityMetersPerSecond, travelMeters, relayRegister, outputFrequencyHz, openLimitActuated, closedLimitActuated, timeScale}'
}

plant_is() {
  curl -fsS --max-time 5 "${emulator_api}/status" 2>/dev/null | jq -e ".plant | $1" >/dev/null 2>&1
}

# time_scale <scale>: the emulator runs that many times as fast as real time from now.
time_scale() {
  curl -fsS --max-time 10 -H 'Content-Type: application/json' --data "{\"scale\": $1}" -o /dev/null "${emulator_api}/time-scale" \
    || fail "the emulator's time scale could not be set to $1"
  plant_is ".timeScale == $1" || fail "the emulator does not run at time scale $1: $(plant)"
}

# relay_closings <relay>: how many times the emulator's history has the relay's contact closing.
relay_closings() {
  curl -fsS --max-time 10 "${emulator_api}/history?limit=5000" | jq --arg relay "$1" '[.[] | select(.detail | startswith("\($relay) contact closed"))] | length'
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
  local tool tools=(docker curl jq openssl ss ip setsid python3 sha256sum)
  [[ -n "${release_source}" ]] || tools+=(git dotnet)
  for tool in "${tools[@]}"; do
    command -v "${tool}" >/dev/null || fail "${tool} is required"
  done
  [[ -n "${release_source}" ]] || docker buildx version >/dev/null 2>&1 || fail "docker buildx is required"
  sudo -n true 2>/dev/null || fail "sudo must run without a password: the installer installs a rig as root"
  # The installer, as root, must install the rig on the daemon this run checks and cleans up.
  local daemon
  daemon=$(docker info -f '{{.ID}}') || fail "docker cannot reach this machine's Docker daemon"
  [[ -n "${daemon}" && "${daemon}" == "$(sudo -n docker info -f '{{.ID}}' 2>/dev/null)" ]] \
    || fail "sudo reaches another Docker daemon than this shell's, or none: run the scenario where both reach this machine's"
  if sudo -n test -e /etc/hvo-roof || sudo -n test -e /var/lib/hvo-roof || sudo -n test -e "${install_log}" \
    || sudo -n test -e "${installed}"; then
    fail "/etc/hvo-roof, /var/lib/hvo-roof, ${install_log} or ${installed} exists: this machine has a controller or a rig. Run the scenario where there is none."
  fi
  local previous_commit=""
  if [[ -z "${release_source}" ]]; then
    previous_commit=$(git -C "${repo_root}" rev-parse --verify --quiet "${previous_ref}^{commit}") \
      || fail "${previous_ref} is not a commit here: fetch it (a CI checkout needs fetch-depth 0), or set RIG_PREVIOUS_REF"
  fi
  if [[ -n "$(docker ps -aq --filter "name=^/(${controller}|${controller}-previous|${emulator}|${registry_name})$")" ]] \
    || docker network inspect "${network}" >/dev/null 2>&1; then
    fail "${controller}, ${emulator}, ${registry_name} or the ${network} network exists: remove them first."
  fi
  ! ip link show "${interface}" >/dev/null 2>&1 || fail "this machine has a network interface named ${interface}: remove it first."
  local port out ports=("${https_port}" "${web_port}" 5290)
  [[ -n "${release_source}" ]] || ports+=("${registry_port}")
  for port in "${ports[@]}"; do
    out=$(ss -ltnH "sport = :${port}") || fail "ss could not list the ports in use"
    [[ -z "${out}" ]] || fail "port ${port} is in use"
  done
  owns_resources=1

  cli_asset="hvo-roof-${rid}"
  if [[ -n "${release_source}" ]]; then
    setup_from_release
  else
    setup_from_source "${previous_commit}"
  fi

  password_file="${work}/admin-password"
  (umask 077 && openssl rand -base64 24 > "${password_file}")
  answers "${work}/rig.json" 10
  answers "${work}/rig-faster.json" 20
  answers "${work}/rig-renamed.json" 20 roof-rig
}

# setup_from_release: the release's assets in RIG_RELEASE_DIR, checked against its SHA256SUMS, as the release workflow
# drafts them; its images are in its registry.
setup_from_release() {
  local file
  release_dir=$(cd "${release_source}" && pwd -P) || fail "RIG_RELEASE_DIR (${release_source}) is not a folder"
  for file in release.json SHA256SUMS install.sh "hvo-roof-install-${rid}" "${cli_asset}"; do
    [[ -f "${release_dir}/${file}" ]] || fail "${release_dir} has no ${file}: give it the release's assets, as the release workflow drafts them"
  done
  (cd "${release_dir}" && sha256sum --check --strict --quiet SHA256SUMS) || fail "the files in ${release_dir} are not the ones its SHA256SUMS lists"
  version=$(jq -r .version "${release_dir}/release.json")
  [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || fail "release.json names no version: '${version}'"
  # install.sh checks the installer's attestation with gh.
  gh auth status >/dev/null 2>&1 || fail "gh is not signed in: install.sh checks the installer's attestation with it (give GH_TOKEN)"
  # An artifact's files lose their modes: the installer that runs here directly is a copy that runs.
  mkdir -p "${work}/installer"
  installer="${work}/installer/hvo-roof-install"
  cp "${release_dir}/hvo-roof-install-${rid}" "${installer}"
  chmod 755 "${installer}"
  says_version "${installer}" "${version}"
  cli_sha256=$(sha256sum "${release_dir}/${cli_asset}" | cut -d' ' -f1)
  installer_sha256=$(sha256sum "${installer}" | cut -d' ' -f1)
  attestation="  ok    its attestation says HualapaiValley/HVO.RoofController's release workflow built it"
  pass "release ${version} from ${release_dir}: its files as its SHA256SUMS lists them, its images $(jq -r '[.images[].reference] | join(" and ")' "${release_dir}/release.json")"
}

# setup_from_source <the release before's commit>: the release made here from this source, and the release before from
# the commit.
setup_from_source() {
  local previous_commit=$1
  say "Publishing hvo-roof-install and hvo-roof ${version} for ${rid}"
  publish "${repo_root}" HVO.RoofControllerV4.Installer "${version}" "${work}/installer"
  publish "${repo_root}" HVO.RoofControllerV4.Cli "${version}" "${work}/cli"
  installer="${work}/installer/hvo-roof-install"
  says_version "${installer}" "${version}"
  # This source's installer labelled as the release before: what upgrade runs as on a machine that has that release.
  publish "${repo_root}" HVO.RoofControllerV4.Installer "${previous_version}" "${work}/installer-old"
  old_installer="${work}/installer-old/hvo-roof-install"
  says_version "${old_installer}" "${previous_version}"
  say "Publishing the release before's hvo-roof-install (${previous_ref}, ${previous_commit:0:12}) as ${previous_version}"
  git -C "${repo_root}" worktree add --quiet --detach "${previous_root}" "${previous_commit}" >/dev/null 2>&1 \
    || fail "could not make a worktree of ${previous_ref}"
  previous_worktree=1
  publish "${previous_root}" HVO.RoofControllerV4.Installer "${previous_version}" "${work}/installer-previous"
  previous_installer="${work}/installer-previous/hvo-roof-install"
  says_version "${previous_installer}" "${previous_version}"

  docker run -d --name "${registry_name}" -p "127.0.0.1:${registry_port}:5000" "${registry_image}" >/dev/null \
    || fail "could not start the registry"
  wait_for "the registry" 60 curl -fs --max-time 2 -o /dev/null "http://127.0.0.1:${registry_port}/v2/"

  local started
  started=$(date +%s.%N)
  say "Building and pushing the release's images (${version}) and the release before's (${previous_version})"
  # The release: install.sh, and hvo-roof and hvo-roof-install for this platform, beside its images, as
  # build/release-assets.py names them, with upgrade notes, which upgrade shows, and SHA256SUMS.
  release_dir="${work}/release"
  mkdir -p "${release_dir}"
  install_script "${release_dir}/install.sh"
  cp "${work}/cli/hvo-roof" "${release_dir}/${cli_asset}"
  cp "${installer}" "${release_dir}/hvo-roof-install-${rid}"
  chmod 755 "${release_dir}/install.sh" "${release_dir}/${cli_asset}" "${release_dir}/hvo-roof-install-${rid}"
  cli_sha256=$(sha256sum "${release_dir}/${cli_asset}" | cut -d' ' -f1)
  installer_sha256=$(sha256sum "${installer}" | cut -d' ' -f1)
  release "${release_dir}" "${repo_root}" "${version}" "Nothing to do by hand: the rig scenario's upgrade notes." \
    install.sh install-script "${cli_asset}" cli "hvo-roof-install-${rid}" installer
  (cd "${release_dir}" && sha256sum -- *) > "${work}/SHA256SUMS" || fail "could not list the release's SHA-256s"
  mv "${work}/SHA256SUMS" "${release_dir}/SHA256SUMS"
  # The release before: its images alone, as a release before the installer was released had them.
  previous_dir="${work}/release-previous"
  mkdir -p "${previous_dir}"
  release "${previous_dir}" "${previous_root}" "${previous_version}" ""
  # install.sh does not see gh here, signed in or not: it notes that it checked no attestation, as without gh.
  hide_gh=(env -u GH_TOKEN -u GITHUB_TOKEN -u GH_ENTERPRISE_TOKEN -u GITHUB_ENTERPRISE_TOKEN "GH_CONFIG_DIR=${work}/no-gh")
  attestation="  note  its attestation was not checked: gh "
  pass "published hvo-roof-install ${version}, and ${previous_version} from this source and from ${previous_ref} (${previous_commit:0:12}), for ${rid}; each release's controller and HAT emulator pushed as indexes of ${platform} and ${other_platform} in $(seconds_since "${started}") s"
}

# install_script <file>: install.sh as build/release-assets.py writes a release's, with the release's version.
install_script() {
  python3 - "${repo_root}" "${version}" "$1" <<'PY' || fail "could not write the release's install.sh"
import importlib.util
import pathlib
import sys

root, version, out = sys.argv[1:]
sys.path.insert(0, str(pathlib.Path(root) / "build"))
spec = importlib.util.spec_from_file_location("release_assets", pathlib.Path(root) / "build" / "release-assets.py")
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)
pathlib.Path(out).write_text(assets.install_script(assets.INSTALL_SCRIPT.read_text(encoding="utf-8"), version), encoding="utf-8")
PY
}

# publish <checkout> <project> <version> <folder>: the project, from the checkout, published for this machine with the
# version.
publish() {
  (cd "$1/src" && Version="$3" dotnet publish "$2" -c Release -r "${rid}" -v quiet -nologo -o "$4" >/dev/null) \
    || fail "could not publish $2 ${3} from $1"
}

# says_version <program> <version>: the program's --version is the version, with its commit.
says_version() {
  local actual
  actual=$("$1" --version) || fail "$1 --version failed"
  [[ "${actual}" == "$2+"* ]] || fail "$1 --version printed '${actual}', not $2+<commit>"
}

# release <folder> <checkout> <version> <upgrade notes> [<asset> <kind>]...: the release's images, built from the
# checkout and pushed, and its release.json, with the assets (files in the folder, for this platform) and the notes, as
# its final release's (4.0.0 for 4.0.0-rig.2), as build/release-assets.py gives a release candidate its release's.
release() {
  local folder=$1 checkout=$2 release_version=$3 notes=$4 commit controller_digest emulator_digest assets="[]"
  shift 4
  commit=$(git -C "${checkout}" rev-parse HEAD)
  push_release_image roof-controller "${checkout}" src/HVO.RoofControllerV4.RPi/Dockerfile "${release_version}"
  controller_digest=${PUSHED_INDEX}
  push_release_image roof-hat-emulator "${checkout}" src/HVO.RoofControllerV4.Emulator/Dockerfile "${release_version}"
  emulator_digest=${PUSHED_INDEX}
  while (( $# >= 2 )); do
    assets=$(jq --arg name "$1" --arg kind "$2" --arg rid "${rid}" --argjson size "$(stat -c %s "${folder}/$1")" \
      --arg sha "$(sha256sum "${folder}/$1" | cut -d' ' -f1)" \
      '. + [{ name: $name, kind: $kind, platform: (if $kind == "install-script" then null else $rid end), size: $size, sha256: $sha }]' \
      <<<"${assets}")
    shift 2
  done
  jq -n --arg version "${release_version}" --arg commit "${commit}" --arg registry "${registry}" \
    --arg controller "${controller_digest}" --arg emulator "${emulator_digest}" --arg notes "${notes}" \
    --argjson assets "${assets}" '
    def image($name; $digest): { repository: "\($registry)/\($name)", tag: $version, digest: $digest,
      reference: "\($registry)/\($name):\($version)@\($digest)", platforms: ["linux/amd64", "linux/arm64"] };
    { schemaVersion: 1, product: "HVO Roof Controller", version: $version, tag: "v\($version)",
      prerelease: ($version | contains("-")), commit: $commit,
      images: { controller: image("roof-controller"; $controller), hatEmulator: image("roof-hat-emulator"; $emulator) },
      assets: $assets }
    + (if $notes == "" then {} else { upgradeNotes: [{ version: ($version | split("-")[0]), text: $notes }] } end)' \
    > "${folder}/release.json"
}

# push_release_image <name> <checkout> <Dockerfile> <version>: the image as a release publishes it, an index of two
# platforms: this platform's image built from the checkout's Dockerfile with the version, and an empty image for the
# other one, which builds without emulation. The index's digest is in PUSHED_INDEX.
PUSHED_INDEX=""
push_release_image() {
  local repository="${registry}/$1" checkout=$2 image_version=$4 single other output
  output=$(docker buildx build --quiet --provenance=false --platform "${platform}" -f "${checkout}/$3" \
    --build-arg "ROOF_VERSION=${image_version}" --build-arg "ROOF_REVISION=$(git -C "${checkout}" rev-parse HEAD 2>/dev/null || true)" \
    -t "${repository}:${image_version}-${platform#linux/}" --load "${checkout}" 2>&1) || fail "could not build $1 ${image_version} for ${platform}: $(tail -n 20 <<<"${output}")"
  single=$(push_digest "${repository}:${image_version}-${platform#linux/}")
  mkdir -p "${work}/other-$1-${image_version}"
  printf 'FROM scratch\nLABEL org.opencontainers.image.version=%s\n' "${image_version}" > "${work}/other-$1-${image_version}/Dockerfile"
  docker buildx build --quiet --provenance=false --platform "${other_platform}" -t "${repository}:${image_version}-${other_platform#linux/}" \
    --load "${work}/other-$1-${image_version}" >/dev/null || fail "could not build the empty ${other_platform} image of $1 ${image_version}"
  other=$(push_digest "${repository}:${image_version}-${other_platform#linux/}")
  docker buildx imagetools create --progress quiet -t "${repository}:${image_version}" "${repository}@${single}" "${repository}@${other}" >/dev/null \
    || fail "could not push the index of $1 ${image_version}"
  PUSHED_INDEX=$(docker buildx imagetools inspect --format '{{json .Manifest}}' "${repository}:${image_version}" | jq -r .digest)
  [[ "${PUSHED_INDEX}" =~ ^sha256:[0-9a-f]{64}$ && "${PUSHED_INDEX}" != "${single}" ]] \
    || fail "the registry reported no index digest for ${repository}:${image_version}: '${PUSHED_INDEX}'"
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

  current_check="install: a test rig from the release, by install.sh"
  local started
  started=$(date +%s.%N)
  # The monitor samples once the install has started the emulator.
  start_relay_monitor
  install_sh install --answers "${work}/rig.json" --admin-password-file "${password_file}"
  expect_installed "--answers (by install.sh)"
  expect_install_sh install rig root "${attestation}"
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
  pass "install.sh checked this machine, took hvo-roof-install from ${release_dir} with the SHA-256 SHA256SUMS lists ($(grep -qF '  ok    its attestation' "${work}/install.txt" && echo 'and its attestation' || echo 'no attestation checked, without gh')), and started it as root; installed in ${seconds} s: ${controller} and ${emulator} run the release's images by digest on ${network}, published on loopback only"

  current_check="install: folders and files"
  mode_is 755 /etc/hvo-roof /etc/hvo-roof/config /var/lib/hvo-roof
  mode_is 700 /etc/hvo-roof/secrets /etc/hvo-roof/https /etc/hvo-roof/ca /var/lib/hvo-roof/identity /var/lib/hvo-roof/settings-secrets
  mode_is 644 "${ca}" /etc/hvo-roof/install.json
  local file
  for file in $(sudo -n find /etc/hvo-roof/secrets /etc/hvo-roof/https /etc/hvo-roof/ca -type f); do
    mode_is 600 "${file}"
  done
  sudo -n jq -e '.roles == ["rig"]' /etc/hvo-roof/install.json >/dev/null || fail "the install record does not say the rig role"
  mode_is 755 "${installed}"
  [[ "$(sudo -n sha256sum "${installed}" | cut -d' ' -f1)" == "${installer_sha256}" ]] || fail "${installed} is not the release's installer"
  pass "the folders, the keys, the CA and the certificate have the modes deployment.md gives, the record says the rig role, and ${installed} is the release's installer"

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
  collect_secrets
  # hvo-roof trusts the CA, not the certificate: it signs in and reads the status from the renewed controller.
  cli_signs_in
  roof_still
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/cert.txt" "${work}/install-log.txt" "${work}/cli-login.txt" "${work}/cli-status.txt"
  pass "a new certificate from the same CA, which is unchanged, served by the redeployed controller, and hvo-roof signs in to it trusting the CA; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_cli() {
  person_home="${work}/home"
  mkdir -p "${person_home}"
  person_cli="${person_home}/.local/bin/hvo-roof"
  local fingerprint wrong credentials="${person_home}/.config/hvo-roof/credentials.json"
  fingerprint=$(openssl x509 -in "${ca}" -noout -fingerprint -sha256 | cut -d= -f2)
  [[ "${fingerprint}" =~ ^([0-9A-F]{2}:){31}[0-9A-F]{2}$ ]] || fail "could not read the fingerprint of ${ca}"
  wrong="00${fingerprint:2}"
  [[ "${wrong}" != "${fingerprint}" ]] || wrong="11${fingerprint:2}"
  cli_answers "${work}/cli.json" "${fingerprint}"
  cli_answers "${work}/cli-wrong.json" "${wrong}"
  start_relay_monitor

  current_check="cli: a wrong fingerprint"
  person_install cli-wrong --answers "${work}/cli-wrong.json" --release "${release_dir}"
  (( INSTALL_STATUS == 3 )) || fail "hvo-roof-install with a wrong fingerprint exited ${INSTALL_STATUS}, not 3 (refused)"
  expect_output cli-wrong "is not the one whose fingerprint was given"
  [[ ! -e "${person_cli}" && ! -e "${credentials}" ]] || fail "the refused install left hvo-roof or its connection"
  pass "refused (exit 3): the CA the rig serves is not the one whose fingerprint was given; nothing installed"

  current_check="cli: the plan"
  person_install cli-plan --plan --answers "${work}/cli.json" --release "${release_dir}"
  expect_installed "--plan (hvo-roof)"
  expect_output cli-plan "trusting its CA (${fingerprint:0:11}…)"
  [[ ! -e "${person_cli}" && ! -e "${credentials}" ]] || fail "--plan installed hvo-roof or its connection"
  pass "planned $(grep -oE '^[0-9]+ to create' "${work}/cli-plan.txt") as the person, not root, and changed nothing"

  current_check="cli: installed by install.sh"
  person_install_sh cli-install --answers "${work}/cli.json"
  expect_installed "--answers (hvo-roof, by install.sh)"
  # The person has no gh signed in.
  expect_install_sh cli-install cli you "  note  its attestation was not checked: gh "
  [[ "$(stat -c '%a %U' "${person_cli}")" == "755 $(id -un)" ]] || fail "${person_cli} is $(stat -c '%a %U' "${person_cli}"), not 755 $(id -un)"
  [[ "$(sha256sum "${person_cli}" | cut -d' ' -f1)" == "${cli_sha256}" ]] || fail "${person_cli} is not the release's ${cli_asset}"
  [[ "$(stat -c '%a %U' "${credentials}")" == "600 $(id -un)" ]] || fail "${credentials} is $(stat -c '%a %U' "${credentials}"), not 600 $(id -un)"
  jq -e --arg controller "${roof}" '(.controller | rtrimstr("/")) == $controller and .session == null and .apiKey == null' "${credentials}" >/dev/null \
    || fail "hvo-roof's connection is not the rig's alone: $(jq -c '{controller, signedIn: (.session != null), key: (.apiKey != null)}' "${credentials}")"
  jq -r '.caCertificate' "${credentials}" | openssl x509 -noout -fingerprint -sha256 | grep -qF "=${fingerprint}" \
    || fail "hvo-roof's connection does not hold the rig's CA"
  jq -e '.roles == ["cli"]' "${person_home}/.config/hvo-roof/install.json" >/dev/null || fail "the person's install record does not say hvo-roof"
  pass "install.sh, as the person, started the installer as them: the release's ${cli_asset} in ~/.local/bin (755), and its connection to ${roof} with the rig's CA (600), all the person's"

  current_check="cli: signed in"
  cli_signs_in
  hvo_roof cli-whoami --json whoami
  jq -e --arg name "${admin}" '.caller.name == $name and .caller.role == "RoofAdmin"' "${work}/cli-whoami.txt" >/dev/null \
    || fail "hvo-roof whoami does not say ${admin}, an admin: $(jq -c '.caller | {name, role}' "${work}/cli-whoami.txt")"
  pass "hvo-roof login ${admin} with the password on standard input, then status over HTTPS trusting the rig's CA: hatMode Emulated"

  current_check="cli: again"
  person_install cli-again --answers "${work}/cli.json" --release "${release_dir}"
  expect_installed "--answers (hvo-roof, again)"
  expect_output cli-again "Nothing to change"
  jq -e '.session.token | length > 0' "${credentials}" >/dev/null || fail "the second run removed hvo-roof's session"
  pass "nothing to change, and the session hvo-roof login saved is kept"

  current_check="cli: no secret shown"
  cat "${person_home}/.local/state/hvo-roof/install.log" > "${work}/cli-log.txt" || fail "the person's install log is missing"
  no_secret_in "${work}/cli-wrong.txt" "${work}/cli-plan.txt" "${work}/cli-install.txt" "${work}/cli-login.txt" \
    "${work}/cli-status.txt" "${work}/cli-whoami.txt" "${work}/cli-again.txt" "${work}/cli-log.txt" "${person_home}/.config/hvo-roof/install.json"
  roof_still
  pass "no secret (the password, the keys, the session) in the output, the log or the record; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_motion() {
  # The drive stopped: the roof still, the relays open and the drive's output at 0 Hz.
  local stopped='(.velocityMetersPerSecond == 0 and .relayRegister == 0 and .outputFrequencyHz == 0)'
  local started seconds

  current_check="motion: open"
  plant_is ".closedLimitActuated and (.openLimitActuated | not) and ${stopped}" || fail "the roof is not closed and still before it opens: $(plant)"
  started=$(date +%s.%N)
  hvo_roof_json motion-open --json open
  seconds=$(seconds_since "${started}")
  (( CLI_STATUS == 0 )) || fail "hvo-roof open exited ${CLI_STATUS}"
  jq -e '.status == "Open" and (.isMoving | not)' "${work}/motion-open.txt" >/dev/null \
    || fail "hvo-roof open ended with $(jq -c '{status, isMoving, lastStopReason}' "${work}/motion-open.txt")"
  wait_for "the drive to stop at the open limit" 30 plant_is ".openLimitActuated and (.closedLimitActuated | not) and ${stopped}"
  pass "hvo-roof open followed the roof to the open limit in ${seconds} s at 10 times real time: status Open, the drive stopped and the relays open"

  current_check="motion: Stop part way"
  # At real time the roof takes a while to close: Stop reaches it on the way.
  time_scale 1
  hvo_roof_json motion-close --json close --no-wait
  (( CLI_STATUS == 0 )) || fail "hvo-roof close --no-wait exited ${CLI_STATUS}"
  jq -e '.status == "Closing" and .isMoving' "${work}/motion-close.txt" >/dev/null \
    || fail "hvo-roof close --no-wait answered $(jq -c '{status, isMoving}' "${work}/motion-close.txt")"
  wait_for "the roof to leave the open limit" 20 plant_is "(.openLimitActuated | not) and .velocityMetersPerSecond < 0 and .positionMeters < .travelMeters - 0.05"
  hvo_roof_json motion-stop --json stop
  (( CLI_STATUS == 0 )) || fail "hvo-roof stop exited ${CLI_STATUS}: $(jq -c '{outcome, code}' "${work}/motion-stop.txt" 2>/dev/null)"
  jq -e '.outcome == "Acknowledged" and .exitCode == 0' "${work}/motion-stop.txt" >/dev/null \
    || fail "hvo-roof stop answered $(jq -c '{outcome, exitCode, code}' "${work}/motion-stop.txt")"
  wait_for "the drive to stop" 30 plant_is "${stopped}"
  plant_is '(.openLimitActuated | not) and (.closedLimitActuated | not) and .positionMeters > 0.05' \
    || fail "the roof did not stop between the limits: $(plant)"
  hvo_roof_json motion-stopped --json status
  (( CLI_STATUS == 0 )) || fail "hvo-roof status exited ${CLI_STATUS}"
  jq -e '(.isMoving | not) and IN(.status; "Stopped", "PartiallyOpen", "PartiallyClose")' "${work}/motion-stopped.txt" >/dev/null \
    || fail "hvo-roof status says $(jq -c '{status, isMoving, lastStopReason}' "${work}/motion-stopped.txt") after the Stop"
  pass "hvo-roof stop, acknowledged, stopped a close at real time part way: the drive stopped at $(plant | jq -r '"\(.positionMeters * 100 | round / 100) m of \(.travelMeters) m"'), neither limit reached, the relays open; status $(jq -r .status "${work}/motion-stopped.txt"), last stop reason $(jq -r .lastStopReason "${work}/motion-stopped.txt")"

  current_check="motion: close"
  time_scale 10
  started=$(date +%s.%N)
  hvo_roof_json motion-closed --json close
  seconds=$(seconds_since "${started}")
  (( CLI_STATUS == 0 )) || fail "hvo-roof close exited ${CLI_STATUS}"
  jq -e '.status == "Closed" and (.isMoving | not)' "${work}/motion-closed.txt" >/dev/null \
    || fail "hvo-roof close ended with $(jq -c '{status, isMoving, lastStopReason}' "${work}/motion-closed.txt")"
  wait_for "the drive to stop at the closed limit" 30 plant_is ".closedLimitActuated and (.openLimitActuated | not) and ${stopped}"
  local rly1 rly2 violations
  rly1=$(relay_closings RLY1) || fail "the emulator's history could not be read"
  rly2=$(relay_closings RLY2) || fail "the emulator's history could not be read"
  (( rly1 >= 1 && rly2 >= 1 )) || fail "the emulator recorded ${rly1} RLY1 and ${rly2} RLY2 contact closings, not one of each at least"
  violations=$(curl -fsS --max-time 10 "${emulator_api}/violations" | jq 'length') || fail "the emulator's violations could not be read"
  (( violations == 0 )) || fail "the emulator recorded ${violations} violations: $(curl -fsS --max-time 10 "${emulator_api}/violations")"
  no_secret_in "${work}"/motion-*.txt
  pass "hvo-roof close followed the roof to the closed limit in ${seconds} s: status Closed, the drive stopped; the emulator recorded ${rly1} RLY1 and ${rly2} RLY2 contact closings, and no violation"
}

scenario_names() {
  local controller_id credentials="${person_home}/.config/hvo-roof/credentials.json"
  sudo -n cat "${ca}" > "${work}/ca-names.crt" || fail "could not read ${ca}"
  jq -c '{controller, caCertificate}' "${credentials}" > "${work}/cli-connection.json"

  current_check="names: a new address"
  sudo -n ip link add "${interface}" type dummy || fail "could not add the dummy interface ${interface}"
  made_interface=1
  sudo -n ip address add "${new_address}/32" dev "${interface}" || fail "could not give ${interface} the address ${new_address}"
  sudo -n ip link set "${interface}" up || fail "could not bring ${interface} up"
  ! answers_at "${new_address}" "${work}/ca-names.crt" --connect-to "${new_address}:${https_port}:127.0.0.1:${https_port}" \
    || fail "the controller answers at ${new_address} before its certificate names it"
  install cert-address-plan cert --plan --release "${release_dir}"
  expect_installed "cert --plan (a new address)"
  expect_output cert-address-plan "adds ${new_address}"
  controller_id=$(container_id "${controller}")
  start_relay_monitor
  install cert-address cert --redeploy --release "${release_dir}"
  expect_installed "cert --redeploy (a new address)"
  expect_output cert-address "adds ${new_address}"
  [[ "$(container_id "${controller}")" != "${controller_id}" ]] || fail "${controller} was not redeployed"
  wait_for "the controller" 60 ready
  sudo -n cmp -s "${work}/ca-names.crt" "${ca}" || fail "a new address replaced the CA"
  answers_at "${new_address}" "${work}/ca-names.crt" --connect-to "${new_address}:${https_port}:127.0.0.1:${https_port}" \
    || fail "the controller does not answer at ${new_address} with a certificate from its CA"
  sign_in
  collect_secrets
  [[ "$(jq -c '{controller, caCertificate}' "${credentials}")" == "$(cat "${work}/cli-connection.json")" ]] \
    || fail "hvo-roof's connection changed"
  cli_signs_in
  roof_still
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/cert-address-plan.txt" "${work}/cert-address.txt" "${work}/install-log.txt"
  pass "cert issued the certificate again for ${new_address} from the same CA and redeployed the controller, which answers there; hvo-roof, its connection unchanged, signs in; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="names: the address gone"
  sudo -n ip link delete "${interface}" || fail "could not remove ${interface}"
  made_interface=0
  start_relay_monitor
  install cert-gone cert --redeploy --release "${release_dir}"
  expect_installed "cert --redeploy (the address gone)"
  expect_output cert-gone "drops ${new_address}"
  wait_for "the controller" 60 ready
  sudo -n cmp -s "${work}/ca-names.crt" "${ca}" || fail "an address gone replaced the CA"
  served_certificate > "${work}/served.crt" || fail "could not read the certificate the controller serves"
  ! openssl x509 -in "${work}/served.crt" -noout -ext subjectAltName | grep -qF "${new_address}" \
    || fail "the certificate the controller serves still names ${new_address}"
  cli_signs_in
  roof_still
  pass "cert dropped ${new_address} from the certificate, from the same CA; hvo-roof signs in; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="names: a new name"
  local fingerprint
  start_relay_monitor
  install renamed-plan --plan --answers "${work}/rig-renamed.json" --release "${release_dir}"
  expect_installed "--plan (a new name)"
  expect_output renamed-plan "it may not issue for roof-rig"
  expect_output renamed-plan "a new CA, which every client must trust in place of this one"
  install renamed --answers "${work}/rig-renamed.json" --release "${release_dir}"
  expect_installed "--answers (a new name)"
  wait_for "the controller" 60 ready
  ! sudo -n cmp -s "${work}/ca-names.crt" "${ca}" || fail "the CA was kept, though it may not issue for roof-rig"
  sudo -n cat "${ca}" > "${work}/ca-renamed.crt" || fail "could not read ${ca}"
  answers_at roof-rig "${work}/ca-renamed.crt" --resolve "roof-rig:${https_port}:127.0.0.1" \
    || fail "the controller does not answer at roof-rig with a certificate from the new CA"
  sign_in
  collect_secrets
  fingerprint=$(openssl x509 -in "${work}/ca-renamed.crt" -noout -fingerprint -sha256 | cut -d= -f2)
  cli_answers "${work}/cli-renamed.json" "${fingerprint}"
  person_install cli-renamed --answers "${work}/cli-renamed.json" --release "${release_dir}"
  expect_installed "--answers (hvo-roof, the new CA)"
  jq -r '.caCertificate' "${credentials}" | openssl x509 -noout -fingerprint -sha256 | grep -qF "=${fingerprint}" \
    || fail "hvo-roof's connection does not hold the new CA"
  cli_signs_in
  roof_still
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/renamed-plan.txt" "${work}/renamed.txt" "${work}/cli-renamed.txt" "${work}/install-log.txt" "${work}/cli-login.txt" "${work}/cli-status.txt"
  pass "a new name, which the CA may not issue for: a new CA, and the controller answers at roof-rig with a certificate from it; hvo-roof, given the new CA's fingerprint, trusts it in place of the old one and signs in; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_backup() {
  current_check="backup: one archive only root reads"
  backup_dir=$(sudo -n mktemp -d /var/tmp/hvo-roof-rig-backup.XXXXXX) || fail "could not make a folder for the backup"
  archive="${backup_dir}/rig.tar.gz"
  keys_sha256 > "${work}/keys-before.txt"
  sudo -n cat "${ca}" > "${work}/ca-backup.crt"
  certificate_before=$(served_fingerprint)
  install_with "${installed}" backup backup --output "${archive}"
  expect_installed "backup"
  expect_output backup "Backed up "
  expect_output backup "to ${archive} (0600, root's):"
  expect_output backup "Keep a copy of ${archive} off this machine, where only you can read it"
  mode_is 600 "${archive}"
  sudo -n tar -tzf "${archive}" > "${work}/backup-entries.txt" || fail "the backup is not a tar.gz"
  [[ "$(head -n 1 "${work}/backup-entries.txt")" == hvo-roof-backup.json ]] || fail "the backup's first entry is not its manifest"
  local entry
  for entry in etc/hvo-roof/install.json etc/hvo-roof/ca.crt etc/hvo-roof/https/roof-controller.pfx; do
    grep -qxF "${entry}" "${work}/backup-entries.txt" || fail "the backup lacks ${entry}"
  done
  grep -q '^etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__' "${work}/backup-entries.txt" || fail "the backup lacks the controller's keys"
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/backup.txt" "${work}/install-log.txt" "${work}/backup-entries.txt"
  pass "$(grep -oE 'Backed up [0-9]+ files and [0-9]+ folders' "${work}/backup.txt") by the installed ${installed}: root's, 0600, its manifest first; no secret in the output or the log"
}

scenario_restore() {
  current_check="restore: uninstall --purge"
  local release_images
  release_images=$(jq -r '.images[].reference' "${release_dir}/release.json")
  start_relay_monitor
  install_with "${installed}" purge uninstall --purge --no-backup --confirm "$(hostname)" --yes
  expect_installed "uninstall --purge"
  expect_output purge "and removing its data:"
  # shellcheck disable=SC2086 # one argument per image
  purged ${release_images}
  relays_still
  pass "the containers, the network, the release's images, the data, the record and ${installed} removed after a verified Stop; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="restore: from the backup"
  start_relay_monitor
  install restore restore "${archive}" --release "${release_dir}"
  expect_installed "restore"
  expect_output restore "Put back "
  wait_for "the controller" 60 ready
  keys_sha256 > "${work}/keys-after.txt"
  cmp -s "${work}/keys-before.txt" "${work}/keys-after.txt" || fail "the keys, the CA or the certificate are not the backup's"
  [[ "$(served_fingerprint)" == "${certificate_before}" ]] || fail "the controller does not serve the certificate it had before the backup"
  openssl verify -CAfile "${work}/ca-backup.crt" <(served_certificate) >/dev/null || fail "the certificate served does not verify with the backup's CA"
  sign_in
  emulated_status
  curl -fsS --max-time 10 "${emulator_api}/status" | jq -e '.plant.timeScale == 20' >/dev/null || fail "the emulator does not run as the backup's record says (20 times as fast)"
  runs "${controller}" "${release_dir}" controller
  [[ "$(sudo -n sha256sum "${installed}" | cut -d' ' -f1)" == "${installer_sha256}" ]] || fail "${installed} is not the release's installer"
  roof_still
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  no_secret_in "${work}/purge.txt" "${work}/restore.txt" "${work}/install-log.txt"
  pass "$(grep -oE 'Put back [0-9]+ of the backup.s [0-9]+ files and folders' "${work}/restore.txt"), then installed as its record says: the same keys, CA and certificate, ${admin} signs in with the same password, hatMode Emulated at 20 times real time; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="restore: again"
  local controller_id emulator_id
  controller_id=$(container_id "${controller}")
  emulator_id=$(container_id "${emulator}")
  start_relay_monitor
  install_with "${installed}" restored-again upgrade --release "${release_dir}"
  expect_installed "upgrade (after the restore)"
  expect_output restored-again "${version} is installed here: checking that everything is as it should be."
  expect_output restored-again "Nothing to change"
  [[ "$(container_id "${controller}")" == "${controller_id}" && "$(container_id "${emulator}")" == "${emulator_id}" ]] \
    || fail "a container was replaced"
  roof_still
  pass "upgrade with the installed ${installed} finds nothing to change, and nothing is replaced; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_upgrade() {
  current_check="upgrade: the release before installed"
  local release_images
  release_images=$(jq -r '.images[].reference' "${release_dir}/release.json")
  start_relay_monitor
  install_with "${installed}" purge-again uninstall --purge --no-backup --confirm "$(hostname)" --yes
  expect_installed "uninstall --purge (again)"
  # shellcheck disable=SC2086 # one argument per image
  purged ${release_images}
  relays_still
  start_relay_monitor
  install_with "${previous_installer}" previous --answers "${work}/rig.json" --release "${previous_dir}" --admin-password-file "${password_file}"
  expect_installed "--answers (${previous_version}, from ${previous_ref})"
  runs "${controller}" "${previous_dir}" controller
  runs "${emulator}" "${previous_dir}" hatEmulator
  wait_for "the controller" 60 ready
  sign_in
  emulated_status
  record_says ".version == \"${previous_version}\""
  roof_still
  pass "${previous_ref}'s installer installed ${previous_version} after another purge: its controller and emulator run, and ${admin} signs in; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="upgrade: to ${version}, the installer handing over"
  local started
  started=$(date +%s.%N)
  collect_secrets
  start_relay_monitor
  install_with "${old_installer}" upgrade upgrade --release "${release_dir}"
  expect_installed "upgrade"
  expect_output upgrade "${previous_version} is installed here; ${version} replaces it."
  expect_output upgrade "Upgrade notes for ${version%%-*}:"
  expect_output upgrade "  Nothing to do by hand: the rig scenario's upgrade notes."
  expect_output upgrade "Handing over to release ${version}'s installer."
  local seconds
  seconds=$(seconds_since "${started}")
  [[ "$(sudo -n stat -c '%a %U' "${installed}")" == "755 root" ]] || fail "${installed} is $(sudo -n stat -c '%a %U' "${installed}"), not 755 root"
  [[ "$(sudo -n sha256sum "${installed}" | cut -d' ' -f1)" == "${installer_sha256}" ]] || fail "${installed} is not release ${version}'s installer"
  runs "${controller}" "${release_dir}" controller
  runs "${controller}-previous" "${previous_dir}" controller
  runs "${emulator}" "${release_dir}" hatEmulator
  [[ "$(docker inspect --format '{{.State.Running}}' "${controller}-previous")" == false ]] || fail "${controller}-previous is running"
  wait_for "the controller" 60 ready
  sign_in
  emulated_status
  record_says ".version == \"${version}\" and .previousVersion == \"${previous_version}\" and .rolledBackFrom == null"
  roof_still
  sudo -n cat "${install_log}" > "${work}/install-log.txt"
  sudo -n cat /etc/hvo-roof/install.json > "${work}/record.txt"
  no_secret_in "${work}/previous.txt" "${work}/upgrade.txt" "${work}/install-log.txt" "${work}/record.txt"
  pass "in ${seconds} s: the upgrade notes shown, release ${version}'s installer put in ${installed} and handed over to, the controller redeployed with ${previous_version}'s kept as ${controller}-previous, ${admin} signs in with the same password; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="upgrade: again"
  local controller_id emulator_id
  controller_id=$(container_id "${controller}")
  emulator_id=$(container_id "${emulator}")
  start_relay_monitor
  install_with "${installed}" upgrade-again upgrade --release "${release_dir}"
  expect_installed "upgrade (again)"
  expect_output upgrade-again "Nothing to change"
  [[ "$(container_id "${controller}")" == "${controller_id}" && "$(container_id "${emulator}")" == "${emulator_id}" ]] \
    || fail "a container was replaced"
  roof_still
  pass "nothing to change, and nothing replaced; relay register 0 in ${ROOF_SAMPLES} samples"
}

scenario_rollback() {
  current_check="rollback: the plan"
  local controller_id emulator_id
  controller_id=$(container_id "${controller}")
  install_with "${installed}" rollback-plan rollback --plan --release "${previous_dir}"
  expect_installed "rollback --plan"
  expect_output rollback-plan "${version} is installed here; going back to ${previous_version}, the release before."
  [[ "$(container_id "${controller}")" == "${controller_id}" ]] || fail "rollback --plan replaced ${controller}"
  record_says ".version == \"${version}\""
  pass "planned the rollback to ${previous_version}, and changed nothing"

  current_check="rollback: to ${previous_version}"
  start_relay_monitor
  install_with "${installed}" rollback rollback --release "${previous_dir}"
  expect_installed "rollback"
  runs "${controller}" "${previous_dir}" controller
  runs "${controller}-previous" "${release_dir}" controller
  wait_for "the controller" 60 ready
  sign_in
  emulated_status
  record_says ".version == \"${previous_version}\" and .rolledBackFrom == \"${version}\" and .previousVersion == null"
  [[ "$(sudo -n sha256sum "${installed}" | cut -d' ' -f1)" == "${installer_sha256}" ]] \
    || fail "${installed} was replaced, though release ${previous_version} has no installer"
  roof_still
  no_secret_in "${work}/rollback-plan.txt" "${work}/rollback.txt"
  pass "the kept controller put back by the deploy script's --rollback, ${version}'s kept in its turn; ${admin} signs in; the record says ${previous_version}, rolled back from ${version}; relay register 0 in ${ROOF_SAMPLES} samples"

  current_check="rollback: again"
  controller_id=$(container_id "${controller}")
  emulator_id=$(container_id "${emulator}")
  install_with "${installed}" rollback-again rollback --release "${previous_dir}"
  expect_installed "rollback (again)"
  expect_output rollback-again "rolled back from ${version} already"
  [[ "$(container_id "${controller}")" == "${controller_id}" && "$(container_id "${emulator}")" == "${emulator_id}" ]] \
    || fail "a container was replaced"
  pass "already rolled back: exit 0, nothing replaced"

  current_check="rollback: upgrade forward again"
  start_relay_monitor
  install_with "${installed}" forward upgrade --release "${release_dir}"
  expect_installed "upgrade (forward again)"
  runs "${controller}" "${release_dir}" controller
  runs "${emulator}" "${release_dir}" hatEmulator
  wait_for "the controller" 60 ready
  sign_in
  emulated_status
  record_says ".version == \"${version}\" and .previousVersion == \"${previous_version}\" and .rolledBackFrom == null"
  roof_still
  pass "${version} again, the record saying ${previous_version} is the release before; relay register 0 in ${ROOF_SAMPLES} samples"
}

setup
scenario_install
scenario_again
scenario_cli
scenario_motion
scenario_change
scenario_cert
scenario_names
scenario_backup
scenario_restore
# The release before is built from source.
if [[ -z "${release_source}" ]]; then
  scenario_upgrade
  scenario_rollback
fi
current_check="done"
say "All checks passed."
