#!/usr/bin/env bash
set -euo pipefail

# Project-local deploy script for Roof Controller V4 (RPi). See docs/deployment.md.
#
# 0. Settings are checked before any Docker call (numbers, EXTRA_DOCKER_ARGS, the HTTPS choice). If Docker cannot
#    report the containers' state, the script stops without changing anything.
# 1. Pre-flight: the new image runs --validate-deployment on the Pi with the final container's environment, devices,
#    secrets, identity, settings and certificate mounts (roof options, a usable RoofOperator/RoofAdmin key, this
#    script's key, a readable and writable identity store, readable settings files in writable directories, the HTTPS
#    listener and certificate). If it fails, the running controller is not touched.
# 2. The running controller is replaced only after a VERIFIED stop: POST /Stop (from inside the container, over
#    loopback) must return 200 with relayRegisterState=Verified, relayRegisterMask=0 and commandedMotion=None.
#    Anything else aborts, unless --force-unverified-stop is given AND the operator types a confirmation. With
#    REQUIRE_IDLE_ROOF=true, an authenticated Status inside the container must first report the roof idle (isMoving
#    false, commandedMotion None); a moving roof, or a Status that says nothing, aborts with nothing changed. The old
#    container is stopped gracefully (SIGTERM) and kept, not started, as <name>-previous; an older stopped
#    <name>-previous is removed only once that stop has succeeded.
# 3. The new controller must become ready, answer an authenticated Status inside the container, and answer an
#    authenticated Status and a verified Stop from this machine at the published URL (HTTPS unless
#    ALLOW_INSECURE_HTTP=true), and report the HAT this run deploys (hatMode Physical, or Emulated in HAT emulator
#    mode). The container runs the controller and the web UI under a supervisor (docs/deployment.md, "The container's
#    two processes"): a controller the supervisor had to start again, or left stopped after repeated crashes, has
#    exited, and the web UI must answer its liveness check inside the container and from this machine on
#    WEB_HOST_PORT. Otherwise it is stopped and removed, and <name>-previous is restored as <name> and
#    started again if it was running. A failure or an interrupt (Ctrl-C, SIGTERM, a lost terminal) anywhere after the
#    old controller's stop began restores it the same way. Only the container this run created is ever removed.
#    From that stop on, docker runs in its own session (setsid, or perl on macOS), so an interrupt cannot cut a docker
#    call short: the restore begins once the call in progress returns.
#
# The container (docs/deployment.md, "The container's privileges and limits"): a read-only root filesystem (tmpfs for
#   /tmp, /run/hvo-roof and /root/.aspnet, and a volume of the container's own for the web UI's sign-in keys), no Linux
#   capabilities but CHOWN, KILL, SETGID and SETUID (the supervisor's, to run the web UI as its own user),
#   no-new-privileges, and limits: CONTAINER_PIDS_LIMIT (default 512), CONTAINER_CPUS and CONTAINER_MEMORY (no swap),
#   always, at least 1 vCPU and 1g. One not set keeps the running controller's, so an override persists across
#   upgrades and rollbacks, and is otherwise RoofController's default of 1 vCPU and 2g. Before anything changes, one
#   docker info on the Pi must report that Docker enforces memory, CPU and process limits, and at least as many CPUs as
#   the CPU limit: a host that cannot is refused, with every finding and the remedy, and there is no override (a memory
#   limit above its memory is a cap, not a reservation: a warning says so). A rollback, a recovery path, is not refused
#   for a limit Docker cannot enforce: it gives the ones it can and warns loudly, here and in the controller's log. The new controller's container must then
#   carry exactly them. The pre-flight container runs with the same. --rollback gives the version it brings back the
#   same limits (docker update), only after the roof's verified stop; its privileges stay as it was deployed with.
#
# Usage: PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh [--dry-run] [--force-unverified-stop] [--rollback]
#        PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh --stop [--force-unverified-stop]
#        PI_HOST=<pi> ./deploy-roofcontroller-rpi.sh --verify-remote
# The image: by default the script builds IMAGE_TAG from this checkout for BUILD_PLATFORM and loads it into the Pi's
#   Docker. With IMAGE_REF=<registry>/<name>[:<tag>]@sha256:<digest> (a release's image, from its release.json) the Pi's
#   Docker pulls that image instead; it is deployed only if Docker reports that digest and BUILD_PLATFORM for it, and
#   nothing is built, so the script runs without a checkout. Everything from the pre-flight on is the same. On the Pi
#   itself, set DOCKER_CONTEXT=default: the remote check then runs from the Pi, so check from another machine afterwards
#   too (--verify-remote). With IMAGE_ARCHIVE=<file> as well (the release's offline archive of that image for
#   BUILD_PLATFORM, <image>-<version>-linux-<arch>.oci.tar), nothing is pulled: the archive is copied once into a folder
#   only this user (and root) can change, in TMPDIR, and Docker loads that copy, never the file itself. Before Docker
#   reads it, the copy must hold IMAGE_REF's digest and its image for BUILD_PLATFORM and nothing else (index.json and
#   manifest.json name that one image, under IMAGE_REF's name or none, and every blob is the one its digest names), and
#   with IMAGE_ARCHIVE_SHA256=<hex> (the archive's SHA-256 in release.json) it must have that SHA-256; the image Docker
#   then holds is deployed by its ID (docker run --pull never), only if that ID is the digest's or that image's, and the
#   container carries the label io.hvo.roof.release-image=<IMAGE_REF>. --rollback, --stop and --verify-remote ignore
#   IMAGE_REF and IMAGE_ARCHIVE, and do not check them.
#   --rollback  swaps the running controller with <name>-previous (after the same verified stop) and checks it
#               as in step 3. Run it again to swap back. If the swap or the start fails or is interrupted, the swap
#               is undone and the original controller restarted. It refuses to run while <name>-swap (left by a
#               rollback that could not be undone) exists.
#   --stop      stops the running controller after the same verified stop of the roof (and, with
#               REQUIRE_IDLE_ROOF=true, only while the roof is idle), and keeps it: docker start <name> runs it again.
#               It deploys nothing and leaves <name>-previous as it is. The installer's uninstall uses it.
#   --verify-remote  runs only the checks from this machine of step 3, against whatever controller answers at the
#               published URL: an authenticated Status that reports the HAT this run expects, then a verified Stop (it
#               stops the roof). It makes no Docker call and changes no container, so it also checks a controller that
#               Docker Compose runs, before `docker compose up` replaces it and after.
# HAT emulator mode: HAT_EMULATOR_ENDPOINT=<host>:<port> deploys a controller that uses the HAT emulator
#   (HVO.RoofControllerV4.Emulator) in place of the physical HAT; the HAT's I2C device is not mapped and the roof does
#   not move. It is refused unless ALLOW_EMULATED_HAT=true, and the report says which HAT the controller uses. A
#   rollback checks the restored version's HAT too, before anything is stopped and again once it runs: the emulator is
#   accepted only with ALLOW_EMULATED_HAT=true. The container then maps no host device or Pi file (no /dev/gpiomem, no
#   thermal sensor), so a test rig need not be a Pi, and BUILD_PLATFORM=linux/amd64 builds the image for a PC.
#   PUBLISH_ADDRESS=127.0.0.1 publishes the ports on that address only, for a rig that is not open to the network;
#   PI_HOST is then localhost, or a name its certificate has with REMOTE_CONNECT_TO=::127.0.0.1:.
# Compose: the script replaces only controllers it created. A <name> or <name>-previous that Docker Compose created is
#   refused before anything changes; docs/deployment.md describes moving between Compose and the script. Check a
#   Compose controller from this machine with --verify-remote.

usage() {
  sed -n '/^# Project-local/,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

DRY_RUN=false
FORCE_UNVERIFIED_STOP=false
ROLLBACK=false
STOP=false
VERIFY_REMOTE=false
for arg in "$@"; do
  case "${arg}" in
    --dry-run) DRY_RUN=true ;;
    --force-unverified-stop) FORCE_UNVERIFIED_STOP=true ;;
    --rollback) ROLLBACK=true ;;
    --stop) STOP=true ;;
    --verify-remote) VERIFY_REMOTE=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: ${arg}" >&2; usage >&2; exit 2 ;;
  esac
done
if [[ "${STOP}" == "true" && ( "${DRY_RUN}" == "true" || "${ROLLBACK}" == "true" || "${VERIFY_REMOTE}" == "true" ) ]]; then
  echo "--stop cannot be combined with --dry-run, --rollback or --verify-remote" >&2
  exit 2
fi
if [[ "${VERIFY_REMOTE}" == "true" && ( "${DRY_RUN}" == "true" || "${FORCE_UNVERIFIED_STOP}" == "true" || "${ROLLBACK}" == "true" ) ]]; then
  echo "--verify-remote cannot be combined with --dry-run, --force-unverified-stop or --rollback" >&2
  exit 2
fi

if [[ -z "${PI_HOST:-}" ]]; then
  echo "PI_HOST environment variable is required" >&2
  exit 1
fi

DOCKER_CONTEXT=${DOCKER_CONTEXT:-rpi-remote}
IMAGE_TAG=${IMAGE_TAG:-hvov9/roof-controller:v4}
# A released image to pull on the Pi in place of building IMAGE_TAG here: <registry>/<name>[:<tag>]@sha256:<digest>.
IMAGE_REF=${IMAGE_REF:-}
# The release's offline archive of IMAGE_REF's image, on this machine, loaded in place of the pull: nothing is pulled.
IMAGE_ARCHIVE=${IMAGE_ARCHIVE:-}
# Its SHA-256 from release.json (the installer gives it), which the copy the script checks and loads must have; empty
# to check what the archive holds alone.
IMAGE_ARCHIVE_SHA256=${IMAGE_ARCHIVE_SHA256:-}
# The label that names the released image a container loaded from IMAGE_ARCHIVE runs (it runs by its ID).
RELEASE_IMAGE_LABEL=io.hvo.roof.release-image
# Platform the image is built (or pulled) for: the Pi's. linux/amd64 is for a test rig on a PC, in HAT emulator mode
# only.
BUILD_PLATFORM=${BUILD_PLATFORM:-linux/arm64}
CONTAINER_NAME=${CONTAINER_NAME:-roof-controller}
PREVIOUS_CONTAINER_NAME="${CONTAINER_NAME}-previous"
# Holds the current controller for a moment while --rollback swaps the names.
SWAP_CONTAINER_NAME="${CONTAINER_NAME}-swap"
HOST_PORT=${HOST_PORT:-8080}
HTTPS_HOST_PORT=${HTTPS_HOST_PORT:-8443}
# The web UI's published port: HTTPS with the controller's certificate, or plain HTTP with ALLOW_INSECURE_HTTP=true.
WEB_HOST_PORT=${WEB_HOST_PORT:-8088}
# The host address the ports are published on: empty for every interface, or an IPv4 address such as 127.0.0.1 to
# keep them to this machine (a test rig that is not open to the network).
PUBLISH_ADDRESS=${PUBLISH_ADDRESS:-}
# Extra `docker run` options for the controller, split on whitespace (no quoting). Also applied to the pre-flight
# container. Options the script sets itself (name, detach, --rm, restart policy, cidfile, published ports, graceful
# stop, platform) are refused: use CONTAINER_NAME, HOST_PORT, HTTPS_HOST_PORT, WEB_HOST_PORT, STOP_TIMEOUT_SECONDS and
# BUILD_PLATFORM.
EXTRA_DOCKER_ARGS=${EXTRA_DOCKER_ARGS:-}
# The controller container's resource limits (docs/deployment.md, "The container's privileges and limits"), for the
# pre-flight container too, always: the most processes and threads at once (256 to 32768), CPUs as `docker run --cpus`
# takes them (2, 2.5) and memory as a whole number of MiB or GiB (1024m, 1g; the container gets no swap beyond it). A
# CPU or memory limit that is not set (or empty) keeps the one the running controller (CONTAINER_NAME) has, so an
# override persists across upgrades and rollbacks; with no controller, or one without that limit, it is
# RoofController's default of 1 vCPU and 2g. Neither is ever below RoofController's floor of 1 vCPU and 1g: less is
# refused, with no exception, and there is no setting for no limit. The kernel ends a controller that reaches its
# memory limit, and its supervisor then turns the relays off (docs/deployment.md, "The relays when the controller
# exits"). --rollback gives them to the version it brings back.
CONTAINER_CPUS=${CONTAINER_CPUS:-}
CONTAINER_MEMORY=${CONTAINER_MEMORY:-}
CONTAINER_PIDS_LIMIT=${CONTAINER_PIDS_LIMIT:-512}
DEFAULT_CONTAINER_CPUS=1
DEFAULT_CONTAINER_MEMORY=2g
# Where the refusal of a Docker that cannot enforce the limits reads the kernel's cgroup v2 controllers, when Docker
# runs on this machine (the tests point it at a file of their own).
CGROUP_CONTROLLERS_FILE=${HVO_DEPLOY_CGROUP_CONTROLLERS_FILE:-/sys/fs/cgroup/cgroup.controllers}
HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI:-true}
IGNORE_PHYSICAL_LIMIT_SWITCHES=${IGNORE_PHYSICAL_LIMIT_SWITCHES:-false}
OTEL_SERVICE_NAME=${OTEL_SERVICE_NAME:-hvo-roof-controller}
OTEL_SERVICE_INSTANCE_ID=${OTEL_SERVICE_INSTANCE_ID:-roof-controller-rpi}
# The OTLP collector. Unset, the observatory's collector; set to an empty value, export is off.
OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT-http://192.168.1.238:4318}
OTEL_EXPORTER_OTLP_PROTOCOL=${OTEL_EXPORTER_OTLP_PROTOCOL:-http/protobuf}
OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL:-10000}

# HAT emulator mode (docs/emulator.md): <host>:<port> of a HAT emulator the container can reach. Test rigs only: the
# controller then drives an emulated roof, not the observatory's. Requires ALLOW_EMULATED_HAT=true.
HAT_EMULATOR_ENDPOINT=${HAT_EMULATOR_ENDPOINT:-}
ALLOW_EMULATED_HAT=${ALLOW_EMULATED_HAT:-false}

# Directory ON THE PI holding one file per secret setting (API keys, Blue Iris password, certificate password),
# mounted read-only at /run/secrets. See docs/security.md for the file names.
SECRETS_DIR=${SECRETS_DIR:-/etc/hvo-roof/secrets}
# Optional host authentication: only its controller socket directory and assertion key are exposed.
LINUX_AUTH_ENABLED=${LINUX_AUTH_ENABLED:-false}
LINUX_AUTH_CONTROLLER_DIR=${LINUX_AUTH_CONTROLLER_DIR:-/run/hvo-roof-linux-auth/controller}
LINUX_AUTH_KEY_FILE=${LINUX_AUTH_KEY_FILE:-/etc/hvo-roof/linux-auth/assertion.key}
# Directory ON THE PI holding the identity store (people, sessions and managed API keys), mounted read-write at
# /var/lib/hvo-roof/identity. It must exist (sudo install -d -m 0700 /var/lib/hvo-roof/identity). Set it to an empty
# value to keep them in memory instead, where a restart forgets them (test rigs only; the pre-flight check warns).
IDENTITY_DIR=${IDENTITY_DIR-/var/lib/hvo-roof/identity}
# Directory ON THE PI holding the settings file (appsettings.Local.json: the settings changed through the API, or by
# hand), mounted read-write at /etc/hvo-roof/config. It must exist (sudo install -d -m 0755 /etc/hvo-roof/config). Set it
# to an empty value to keep settings changed through the API in memory instead (test rigs only; the pre-flight check
# warns).
CONFIG_DIR=${CONFIG_DIR-/etc/hvo-roof/config}
# Directory ON THE PI holding the managed secrets file (secrets set through the API), mounted read-write at
# /var/lib/hvo-roof/settings-secrets. It must exist (sudo install -d -m 0700 /var/lib/hvo-roof/settings-secrets). Empty
# keeps them in memory, as for CONFIG_DIR.
MANAGED_SECRETS_DIR=${MANAGED_SECRETS_DIR-/var/lib/hvo-roof/settings-secrets}
# Directory ON THE PI holding the TLS certificate (PFX). Required unless ALLOW_INSECURE_HTTP=true.
HTTPS_CERT_DIR=${HTTPS_CERT_DIR:-}
HTTPS_CERT_FILE=${HTTPS_CERT_FILE:-roof-controller.pfx}
ALLOW_INSECURE_HTTP=${ALLOW_INSECURE_HTTP:-false}
# Semicolon-separated host names/IPs clients use (AllowedHosts); include localhost (the health check and this script's
# in-container calls use it). Empty keeps the image default.
ALLOWED_HOSTS=${ALLOWED_HOSTS:-}
# docker stop's grace period. The supervisor waits up to 22 s for the controller's shutdown (itself up to 20 s), then up
# to 5 s to turn the relays off while the web UI gets 2 s, so it must be 30 or more: a shorter one would let Docker kill
# the controller before its shutdown ends.
STOP_TIMEOUT_SECONDS=${STOP_TIMEOUT_SECONDS:-30}
# true: a running controller is replaced only while its roof is idle. Before the verified Stop, an authenticated Status
# inside the container must report isMoving false and commandedMotion None, or nothing is changed. The installer sets
# it, so a roof that began to move after the installer last looked is not stopped halfway by the Stop.
REQUIRE_IDLE_ROOF=${REQUIRE_IDLE_ROOF:-false}
READY_TIMEOUT_SECONDS=${READY_TIMEOUT_SECONDS:-120}
POLL_INTERVAL_SECONDS=${POLL_INTERVAL_SECONDS:-3}

# Remote check from this machine after the switch. REMOTE_CA_CERT is a PEM file (on this machine) that verifies the
# Pi's certificate when it is not signed by a CA this machine trusts, e.g. the self-signed certificate itself.
REMOTE_CA_CERT=${REMOTE_CA_CERT:-}
# REMOTE_CONNECT_TO goes to curl's --connect-to for those checks (HOST1:PORT1:HOST2:PORT2): '::127.0.0.1:' checks PI_HOST's
# name and certificate at this machine's loopback, for a name this machine cannot resolve, such as the Pi's own on the Pi.
REMOTE_CONNECT_TO=${REMOTE_CONNECT_TO:-}
SKIP_REMOTE_CHECK=${SKIP_REMOTE_CHECK:-false}

# Key for the Stop and Status checks (normally the operator key). Passed to curl on stdin, never as an argument.
OPERATOR_KEY_FILE=${OPERATOR_KEY_FILE:-${HOME}/.config/hvo-roof/operator.key}

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "${SCRIPT_DIR}/../.." && pwd)
DOCKERFILE_PATH="${SCRIPT_DIR}/Dockerfile"
API_PATH="api/v4.0/RoofControl"

# What a failure or an interrupt during the switch must undo (restore_original; the EXIT trap runs it when the script
# dies). RESTORE_MODE is the state the original controller was in before the switch: none (there was none), stopped
# or running. It is set before the first change and cleared only when the switch is complete or the restore has run.
# The restore finds the original controller by ID (ORIGINAL_ID) wherever the switch got to and puts it back under
# CONTAINER_NAME. A deploy removes only the container it created (NEW_CONTAINER_ID, or the ID docker wrote to
# NEW_CIDFILE when `docker run` failed after creating it); a rollback stops the version it was bringing back
# (ROLLBACK_TARGET_ID) and puts it back as <name>-previous.
RESTORE_MODE=""
RESTORING=false
ORIGINAL_ID=""
NEW_CONTAINER_ID=""
NEW_CIDFILE=""
ROLLBACK_TARGET_ID=""
RESTORE_OUTCOME=""
FAILURE=""
# The hatMode verify_controller found (check_hat_mode).
VERIFIED_HAT_MODE=""
# Why wait_ready found the controller exited, when the container's supervisor said so.
EXIT_DETAIL=""
WORK_DIR=""
# `docker run --platform` for a pulled image (check_pulled_image), with --pull never and the release-image label for a
# loaded one (check_loaded_image), and `image inspect --platform` where the CLI has it.
image_platform_args=()
inspect_platform_args=()

# From the first change of the switch on (and during the restore), docker runs in its own session, outside the
# terminal's process group, so a Ctrl-C or a hangup at the terminal cannot reach it; bash runs its trap once the call
# returns. Docker CLI 27 and later gives up its request on SIGINT even while this script ignores the signal, and the
# daemon still finishes a stop by itself: an interrupted `docker stop` would leave the old controller stopping just
# as the restore tries to start it, and a second Ctrl-C would cut the restore's own calls short.
dockerc() {
  if [[ -n "${RESTORE_MODE}" || "${RESTORING}" == "true" ]]; then
    "${OWN_SESSION[@]}" docker --context "${DOCKER_CONTEXT}" "$@"
  else
    docker --context "${DOCKER_CONTEXT}" "$@"
  fi
}

# Output that must never stop the script: during a restore the terminal or the pipe may be gone (EIO/EPIPE).
log_err() {
  echo "$*" >&2 || true
}

fail() {
  log_err "[deploy] ERROR: $*"
  exit 1
}

# ---------------------------------------------------------------------------------------------------------------------
# Settings, checked before any Docker call. A malformed number must stop the script here: in a bash arithmetic
# expression it aborts the whole surrounding command without tripping set -e, which could skip a check or a restore.

# require_number <variable> <min> <max>: a whole number in range, normalized to plain decimal (so 010 is 10, not 8).
require_number() {
  local name=$1 min=$2 max=$3 value=${!1}
  case "${value}" in
    ''|*[!0-9]*) fail "${name} must be a whole number from ${min} to ${max}, got '${value}'." ;;
  esac
  if (( ${#value} > 9 || 10#${value} < min || 10#${value} > max )); then
    fail "${name} must be a whole number from ${min} to ${max}, got '${value}'."
  fi
  printf -v "${name}" '%d' "$((10#${value}))"
}

require_number READY_TIMEOUT_SECONDS 1 86400
# At least the supervisor's 22 s for the controller and 5 s for the relays (the web UI's 2 s at the same time), with a
# margin.
require_number STOP_TIMEOUT_SECONDS 30 86400
require_number HOST_PORT 1 65535
require_number HTTPS_HOST_PORT 1 65535
require_number WEB_HOST_PORT 1 65535
case "${REQUIRE_IDLE_ROOF}" in
  true|false) ;;
  *) fail "REQUIRE_IDLE_ROOF must be true or false, got '${REQUIRE_IDLE_ROOF}'. Nothing was changed." ;;
esac
publish_prefix=""
if [[ -n "${PUBLISH_ADDRESS}" ]]; then
  address_pattern='^([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})$'
  if [[ ! "${PUBLISH_ADDRESS}" =~ ${address_pattern} ]] \
      || (( 10#${BASH_REMATCH[1]} > 255 || 10#${BASH_REMATCH[2]} > 255 || 10#${BASH_REMATCH[3]} > 255 || 10#${BASH_REMATCH[4]} > 255 )); then
    fail "PUBLISH_ADDRESS must be an IPv4 address such as 127.0.0.1, or empty for every interface, got '${PUBLISH_ADDRESS}'. Nothing was changed."
  fi
  PUBLISH_ADDRESS="$((10#${BASH_REMATCH[1]})).$((10#${BASH_REMATCH[2]})).$((10#${BASH_REMATCH[3]})).$((10#${BASH_REMATCH[4]}))"
  publish_prefix="${PUBLISH_ADDRESS}:"
fi
case "${POLL_INTERVAL_SECONDS}" in
  ''|.|*[!0-9.]*|*.*.*) fail "POLL_INTERVAL_SECONDS must be a number of seconds such as 3 or 0.5, got '${POLL_INTERVAL_SECONDS}'." ;;
esac

# The limits, held for the checks in hundredths of a CPU (CPU_HUNDREDTHS) and in bytes (MEMORY_BYTES), and written
# back plainly (2.50 as 2.5, 4G as 4g). Docker counts CPUs in billionths, so two decimals are given to it exactly. None
# is ever below RoofController's floor of 1 vCPU and 1g: there is no exception. CPUS_SOURCE and MEMORY_SOURCE say where
# each came from: given (CONTAINER_CPUS, CONTAINER_MEMORY), kept from the running controller, or the default.
FLOOR_TEXT="the minimum (RoofController's; docs/deployment.md, \"The container's privileges and limits\")"
CPU_HUNDREDTHS=0 MEMORY_BYTES=0 CPUS_SOURCE="" MEMORY_SOURCE=""

# set_cpus <value> <source> <how to change it>: CONTAINER_CPUS and CPU_HUNDREDTHS from a value, or the script stops.
set_cpus() {
  local value=$1 source=$2 change=$3 pattern='^([0-9]{1,3})(\.([0-9]{1,2}))?$' fraction note=""
  [[ "${source}" == given ]] || note=" (${source})"
  [[ "${value}" =~ ${pattern} ]] \
    || fail "CONTAINER_CPUS must be a number of CPUs such as 2 or 2.5 (at most two decimals), at least 1, got '${value}'${note}. Nothing was changed."
  fraction="${BASH_REMATCH[3]}00"
  CPU_HUNDREDTHS=$(( 10#${BASH_REMATCH[1]} * 100 + 10#${fraction:0:2} ))
  CONTAINER_CPUS=$(( CPU_HUNDREDTHS / 100 ))
  if (( CPU_HUNDREDTHS % 100 != 0 )); then
    fraction=$(printf '%02d' $(( CPU_HUNDREDTHS % 100 )))
    CONTAINER_CPUS+=".${fraction%0}"
  fi
  CPUS_SOURCE=${source}
  if (( CPU_HUNDREDTHS < 100 )); then
    fail "CONTAINER_CPUS=${CONTAINER_CPUS}${note} is less than 1 vCPU, ${FLOOR_TEXT}: ${change}. Nothing was changed."
  fi
}

# set_memory <value> <source> <how to change it>: CONTAINER_MEMORY and MEMORY_BYTES from a value, or the script stops.
set_memory() {
  local value=$1 source=$2 change=$3 pattern='^([0-9]{1,6})([mMgG])$' note=""
  [[ "${source}" == given ]] || note=" (${source})"
  [[ "${value}" =~ ${pattern} ]] \
    || fail "CONTAINER_MEMORY must be a whole number of MiB or GiB such as 1024m or 1g, at least 1g, got '${value}'${note}. Nothing was changed."
  case "${BASH_REMATCH[2]}" in
    m|M) CONTAINER_MEMORY="$(( 10#${BASH_REMATCH[1]} ))m" MEMORY_BYTES=$(( 10#${BASH_REMATCH[1]} * 1048576 )) ;;
    *) CONTAINER_MEMORY="$(( 10#${BASH_REMATCH[1]} ))g" MEMORY_BYTES=$(( 10#${BASH_REMATCH[1]} * 1073741824 )) ;;
  esac
  MEMORY_SOURCE=${source}
  if (( MEMORY_BYTES < 1073741824 )); then
    fail "CONTAINER_MEMORY=${CONTAINER_MEMORY}${note} is less than 1g, ${FLOOR_TEXT}: ${change}. Nothing was changed."
  fi
}

# The limits given are checked here, before any Docker call; one not given is settled by resolve_limits, once the
# running controller's are known.
require_number CONTAINER_PIDS_LIMIT 256 32768
[[ -z "${CONTAINER_CPUS}" ]] || set_cpus "${CONTAINER_CPUS}" given "give at least 1"
[[ -z "${CONTAINER_MEMORY}" ]] || set_memory "${CONTAINER_MEMORY}" given "give at least 1g"

# Emulator mode needs the explicit flag: a controller on the HAT emulator does not operate the roof.
HAT_EMULATOR_HOST="" HAT_EMULATOR_PORT=""
if [[ -n "${HAT_EMULATOR_ENDPOINT}" ]]; then
  endpoint_pattern='^([A-Za-z0-9][A-Za-z0-9.-]*):([0-9]{1,5})$'
  if [[ ! "${HAT_EMULATOR_ENDPOINT}" =~ ${endpoint_pattern} ]] \
      || (( 10#${BASH_REMATCH[2]} < 1 || 10#${BASH_REMATCH[2]} > 65535 )); then
    fail "HAT_EMULATOR_ENDPOINT must be <host>:<port> with a host name or IPv4 address and a port from 1 to 65535 (e.g. hat-emulator:5291), got '${HAT_EMULATOR_ENDPOINT}'."
  fi
  HAT_EMULATOR_HOST=${BASH_REMATCH[1]}
  HAT_EMULATOR_PORT=$((10#${BASH_REMATCH[2]}))
  HAT_EMULATOR_ENDPOINT="${HAT_EMULATOR_HOST}:${HAT_EMULATOR_PORT}"
  if [[ "${ALLOW_EMULATED_HAT}" != "true" ]]; then
    fail "HAT_EMULATOR_ENDPOINT is set: the controller would use the HAT emulator at ${HAT_EMULATOR_ENDPOINT}, not the physical HAT, and would not operate the roof. For a test rig, set ALLOW_EMULATED_HAT=true as well. Nothing was changed."
  fi
  HAT_SUMMARY="HAT EMULATOR at ${HAT_EMULATOR_ENDPOINT} (ALLOW_EMULATED_HAT=true): the physical HAT is not mapped and the roof does not move"
else
  HAT_SUMMARY="physical HAT (/dev/i2c-1)"
fi

# The physical HAT is on a Raspberry Pi (arm64): an image for another platform is for a test rig on the HAT emulator.
case "${BUILD_PLATFORM}" in
  linux/arm64) ;;
  linux/amd64)
    [[ -n "${HAT_EMULATOR_ENDPOINT}" ]] \
      || fail "BUILD_PLATFORM=linux/amd64 is for a test rig on the HAT emulator (set HAT_EMULATOR_ENDPOINT and ALLOW_EMULATED_HAT=true): the physical HAT is on a Raspberry Pi (linux/arm64). Nothing was changed."
    ;;
  *) fail "BUILD_PLATFORM must be linux/arm64 (the Pi) or linux/amd64 (a test rig on the HAT emulator), got '${BUILD_PLATFORM}'. Nothing was changed." ;;
esac

# Pull mode: a released image, named by its digest, so the Pi runs exactly the image the release lists. Checked here, so
# that a malformed reference changes nothing, and never starts with '-', so docker cannot take it for an option.
# --rollback, --stop and --verify-remote deploy no image: they ignore it, so a reference left in the environment cannot
# stop a rollback.
IMAGE_DIGEST=""
if [[ -n "${IMAGE_REF}" && "${ROLLBACK}" != "true" && "${STOP}" != "true" && "${VERIFY_REMOTE}" != "true" ]]; then
  image_ref_pattern='^[a-z0-9][A-Za-z0-9._/:-]*@(sha256:[0-9a-f]{64})$'
  [[ "${IMAGE_REF}" =~ ${image_ref_pattern} ]] \
    || fail "IMAGE_REF must name a released image by its digest, <registry>/<name>[:<tag>]@sha256:<64 hex digits> (e.g. ghcr.io/hualapaivalley/roof-controller:4.0.0@sha256:...), got '${IMAGE_REF}'. Nothing was changed."
  IMAGE_DIGEST=${BASH_REMATCH[1]}
  DEPLOY_IMAGE=${IMAGE_REF}
else
  DEPLOY_IMAGE=${IMAGE_TAG}
fi
# Offline: the release's archive of IMAGE_REF's image, loaded in place of the pull. What it holds is checked before any
# Docker call (check_image_archive); --rollback, --stop and --verify-remote load nothing, and ignore it.
if [[ -n "${IMAGE_ARCHIVE}" && "${ROLLBACK}" != "true" && "${STOP}" != "true" && "${VERIFY_REMOTE}" != "true" ]]; then
  [[ -n "${IMAGE_DIGEST}" ]] \
    || fail "IMAGE_ARCHIVE is a release's offline archive of an image, and needs IMAGE_REF, that image by its digest (from release.json), to check it against. Nothing was changed."
  [[ "${IMAGE_ARCHIVE}" != -* && -f "${IMAGE_ARCHIVE}" && -r "${IMAGE_ARCHIVE}" ]] \
    || fail "IMAGE_ARCHIVE '${IMAGE_ARCHIVE}' is not a file this script can read. Nothing was changed."
  [[ -z "${IMAGE_ARCHIVE_SHA256}" || "${IMAGE_ARCHIVE_SHA256}" =~ ^[0-9a-f]{64}$ ]] \
    || fail "IMAGE_ARCHIVE_SHA256 must be the archive's SHA-256 from release.json: 64 lower-case hex digits. Nothing was changed."
else
  IMAGE_ARCHIVE=""
  IMAGE_ARCHIVE_SHA256=""
fi

# A second --name would make Docker run the controller under that name: the checks and the restore would then act on
# the wrong container while the new one drives the HAT. --rm, --detach, --restart, --cidfile and published ports would
# break the restore, the restart policy or the pre-flight container, --stop-timeout or --stop-signal could cut the
# controller's shutdown stop short, and --platform could run another platform's image than the one checked. The
# container's privileges and limits are the script's too: --privileged, the read-only root filesystem and tmpfs mounts,
# capabilities, --security-opt and the CPU, memory and process limits. Short options may be combined (-itd,
# -p8443:8443, -m4g). HatEmulator settings (in any case, as .NET reads them), given directly or in an --env-file, would
# switch the HAT without HAT_EMULATOR_ENDPOINT and ALLOW_EMULATED_HAT, and the report. In emulator mode the HAT's bus
# stays unmapped: no I2C device and no mount of the host's /dev.
EMULATOR_MODE_SETTING="HAT emulator mode is set with HAT_EMULATOR_ENDPOINT and ALLOW_EMULATED_HAT, so that the deployment records it."
RELEASE_IMAGE_SETTING="the script sets it on a controller it loads from IMAGE_ARCHIVE, once it has checked the image, and the installer reads the release the controller runs from it."

refuse_emulator_setting() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    *hatemulator__*|*hatemulator:*)
      fail "EXTRA_DOCKER_ARGS must not contain '${2:-$1}': ${EMULATOR_MODE_SETTING}" ;;
  esac
}

# check_extra_option <option> <value>: the value of an EXTRA_DOCKER_ARGS option, given after it or after '='.
check_extra_option() {
  local option=$1 value=$2 line source field fields=()
  case "${option}" in
    --env-file)
      # The docker CLI reads the file on this machine, so it can be checked here.
      [[ -f "${value}" && -r "${value}" ]] \
        || fail "EXTRA_DOCKER_ARGS names --env-file '${value}', which cannot be read on this machine. Nothing was changed."
      while IFS= read -r line || [[ -n "${line}" ]]; do
        [[ "${line}" =~ ^[[:space:]]*(#|$) ]] && continue
        refuse_emulator_setting "${line%%=*}" "--env-file ${value}: ${line%%=*}"
      done < "${value}"
      ;;
    --label-file)
      [[ -f "${value}" && -r "${value}" ]] \
        || fail "EXTRA_DOCKER_ARGS names --label-file '${value}', which cannot be read on this machine. Nothing was changed."
      ! grep -qF -- "${RELEASE_IMAGE_LABEL}" "${value}" \
        || fail "EXTRA_DOCKER_ARGS must not set the label ${RELEASE_IMAGE_LABEL} (--label-file ${value}): ${RELEASE_IMAGE_SETTING}"
      ;;
    --device)
      if [[ -n "${HAT_EMULATOR_ENDPOINT}" && "$(printf '%s' "${value}" | tr '[:upper:]' '[:lower:]')" == *i2c* ]]; then
        fail "EXTRA_DOCKER_ARGS must not map an I2C device ('--device ${value}') in HAT emulator mode: the controller must not reach the physical HAT."
      fi
      ;;
    --volume|--mount)
      source=${value%%:*}
      if [[ "${option}" == "--mount" ]]; then
        source=""
        IFS=',' read -r -a fields <<<"${value}"
        for field in ${fields[@]+"${fields[@]}"}; do
          case "${field}" in source=*|src=*) source=${field#*=} ;; esac
          if [[ "$(printf '%s' "${field}" | tr '[:upper:]' '[:lower:]')" == "type=tmpfs" ]]; then
            fail "EXTRA_DOCKER_ARGS must not mount a tmpfs ('--mount ${value}'): the script gives the container's read-only root filesystem its writable places itself (docs/deployment.md, \"The container's privileges and limits\"). Nothing was changed."
          fi
        done
      fi
      if [[ -n "${HAT_EMULATOR_ENDPOINT}" && ( "${source}" == "/" || "${source}" == /dev || "${source}" == /dev/* ) ]]; then
        fail "EXTRA_DOCKER_ARGS must not mount the host's ${source} ('${option} ${value}') in HAT emulator mode: the controller must not reach the physical HAT."
      fi
      ;;
  esac
}

extra_args=()
if [[ -n "${EXTRA_DOCKER_ARGS}" ]]; then
  read -r -d '' -a extra_args <<<"${EXTRA_DOCKER_ARGS}" || true
fi
pending_option=""
for arg in ${extra_args[@]+"${extra_args[@]}"}; do
  if [[ -n "${pending_option}" ]]; then
    check_extra_option "${pending_option}" "${arg}"
    pending_option=""
  else
    case "${arg}" in
      --env-file|--label-file|--device|--volume|--mount) pending_option=${arg} ;;
      --env-file=*|--label-file=*|--device=*|--volume=*|--mount=*) check_extra_option "${arg%%=*}" "${arg#*=}" ;;
      --privileged|--privileged=*)
        # Docker reads the value as Go's strconv.ParseBool does, so 1, t and True turn it on as well as true. It gives the
        # container every capability and every host device: never, and in HAT emulator mode the HAT's bus with them.
        case "$(printf '%s' "${arg#--privileged}" | tr '[:upper:]' '[:lower:]')" in
          =false|=f|=0) ;;
          *)
            [[ -z "${HAT_EMULATOR_ENDPOINT}" ]] \
              || fail "EXTRA_DOCKER_ARGS must not contain '${arg}' in HAT emulator mode: it maps every host device, the HAT's I2C bus included."
            fail "EXTRA_DOCKER_ARGS must not contain '${arg}': it gives the controller every capability and every host device, undoing the container's privileges (docs/deployment.md, \"The container's privileges and limits\"). Nothing was changed."
            ;;
        esac
        ;;
      *)
        # -v and boolean short options combined with it (-itv), with the volume next or attached (-v/dev:/dev).
        if [[ "${arg}" =~ ^-[ditPq]*v(.*)$ ]]; then
          if [[ -n "${BASH_REMATCH[1]}" ]]; then
            check_extra_option --volume "${BASH_REMATCH[1]#=}"
          else
            pending_option=--volume
          fi
        fi
        ;;
    esac
  fi
  case "${arg}" in
    --name|--name=*|--detach|--detach=*|--rm|--rm=*|--restart|--restart=*|--cidfile|--cidfile=*|--publish|--publish=*|--publish-all|--publish-all=*)
      reserved=true ;;
    --stop-timeout|--stop-timeout=*|--stop-signal|--stop-signal=*|--platform|--platform=*)
      reserved=true ;;
    *)
      reserved=false
      if [[ "${arg}" =~ ^-[ditPq]*[dPp] ]]; then reserved=true; fi
      ;;
  esac
  if [[ "${reserved}" == "true" ]]; then
    fail "EXTRA_DOCKER_ARGS must not contain '${arg}': the script sets the container name, --detach, --rm, --restart, --cidfile, the published ports, --stop-timeout, --stop-signal and --platform itself (use CONTAINER_NAME, HOST_PORT, HTTPS_HOST_PORT, WEB_HOST_PORT, STOP_TIMEOUT_SECONDS and BUILD_PLATFORM)."
  fi
  # A loaded image runs with --pull never, and its label names the release's image only once the script has checked it.
  case "${arg}" in
    --pull|--pull=*)
      fail "EXTRA_DOCKER_ARGS must not contain '${arg}': the script pulls a released image itself (IMAGE_REF), and never pulls one it loads (IMAGE_ARCHIVE)." ;;
    *"${RELEASE_IMAGE_LABEL}"*)
      fail "EXTRA_DOCKER_ARGS must not set the label ${RELEASE_IMAGE_LABEL} ('${arg}'): ${RELEASE_IMAGE_SETTING}" ;;
  esac
  case "${arg}" in
    --read-only|--read-only=*|--tmpfs|--tmpfs=*|--cap-add|--cap-add=*|--cap-drop|--cap-drop=*|--security-opt|--security-opt=*)
      confining=true ;;
    --cpus|--cpus=*|--cpu-period|--cpu-period=*|--cpu-quota|--cpu-quota=*|--cpu-rt-period|--cpu-rt-period=*|--cpu-rt-runtime|--cpu-rt-runtime=*)
      confining=true ;;
    --memory|--memory=*|--memory-swap|--memory-swap=*|--kernel-memory|--kernel-memory=*|--oom-kill-disable|--oom-kill-disable=*|--pids-limit|--pids-limit=*)
      confining=true ;;
    *)
      confining=false
      # -m (the memory limit), alone or after boolean short options (-itm 1g, -m1g).
      if [[ "${arg}" =~ ^-[ditPq]*m ]]; then confining=true; fi
      ;;
  esac
  if [[ "${confining}" == "true" ]]; then
    fail "EXTRA_DOCKER_ARGS must not contain '${arg}': the script sets the container's privileges and limits itself (the read-only root filesystem and its tmpfs mounts, --cap-drop and --cap-add, --security-opt, --cpus, --memory, --memory-swap and --pids-limit; use CONTAINER_CPUS, CONTAINER_MEMORY and CONTAINER_PIDS_LIMIT). See docs/deployment.md, \"The container's privileges and limits\". Nothing was changed."
  fi
  refuse_emulator_setting "${arg}"
done

# Applies to --rollback too: its checks send the key to the published URL.
if [[ -z "${HTTPS_CERT_DIR}" && "${ALLOW_INSECURE_HTTP}" != "true" ]]; then
  fail "Set HTTPS_CERT_DIR (directory on the Pi with ${HTTPS_CERT_FILE}) or ALLOW_INSECURE_HTTP=true (for --rollback, as for the version being restored). Without HTTPS, API keys cross the network in clear text and LAN clients get 403 https_required unless RequireHttps is disabled. See docs/deployment.md."
fi

# What dockerc runs docker under during the switch. macOS has no setsid(1); perl's POSIX::setsid does the same there.
if command -v setsid >/dev/null 2>&1; then
  OWN_SESSION=(setsid -w)
elif command -v perl >/dev/null 2>&1; then
  # As setsid -w does: a process group leader cannot start a session, so it waits (ignoring signals) for a child that
  # does and passes on its exit status.
  # shellcheck disable=SC2016 # perl code
  OWN_SESSION=(perl -MPOSIX -e '
    if (getpgrp() == $$) {
      my %old = map { $_ => $SIG{$_} } qw(HUP INT QUIT TERM);
      $SIG{$_} = "IGNORE" for keys %old;
      defined(my $pid = fork) or die "fork: $!\n";
      if ($pid) { waitpid($pid, 0); exit($? & 127 ? 128 + ($? & 127) : $? >> 8) }
      $SIG{$_} = $old{$_} // "DEFAULT" for keys %old;
    }
    POSIX::setsid() > 0 or die "setsid: $!\n";
    exec { $ARGV[0] } @ARGV or die "$ARGV[0]: $!\n"' --)
else
  fail "setsid or perl is required: the switch runs docker in its own session so that an interrupt cannot cut it short."
fi

if [[ -n "${REMOTE_CA_CERT}" && ! -r "${REMOTE_CA_CERT}" ]]; then
  fail "REMOTE_CA_CERT '${REMOTE_CA_CERT}' is not a readable file on this machine."
fi
if [[ -n "${REMOTE_CONNECT_TO}" && ! "${REMOTE_CONNECT_TO}" =~ ^[A-Za-z0-9.-]*:[0-9]*:[A-Za-z0-9.-]*:[0-9]*$ ]]; then
  fail "REMOTE_CONNECT_TO must be HOST1:PORT1:HOST2:PORT2 as curl's --connect-to takes it (such as ::127.0.0.1:), got '${REMOTE_CONNECT_TO}'. Nothing was changed."
fi

if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  REMOTE_BASE_URL="https://${PI_HOST}:${HTTPS_HOST_PORT}"
  REMOTE_WEB_URL="https://${PI_HOST}:${WEB_HOST_PORT}"
  controller_host_port=${HTTPS_HOST_PORT}
else
  REMOTE_BASE_URL="http://${PI_HOST}:${HOST_PORT}"
  REMOTE_WEB_URL="http://${PI_HOST}:${WEB_HOST_PORT}"
  controller_host_port=${HOST_PORT}
fi
if (( WEB_HOST_PORT == controller_host_port )); then
  fail "WEB_HOST_PORT (${WEB_HOST_PORT}) is the controller's published port too: give the web UI a port of its own. Nothing was changed."
fi

# ---------------------------------------------------------------------------------------------------------------------

file_mode() {
  stat -c '%a' "$1" 2>/dev/null || stat -f '%Lp' "$1"
}

sha256_hex() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d ' ' -f 1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 | cut -d ' ' -f 1
  else
    openssl dgst -sha256 -r | cut -d ' ' -f 1
  fi
}

resolve_operator_key() {
  if [[ -n "${ROOF_OPERATOR_API_KEY:-}" ]]; then
    OPERATOR_KEY=${ROOF_OPERATOR_API_KEY}
    return
  fi

  if [[ -f "${OPERATOR_KEY_FILE}" ]]; then
    local mode
    mode=$(file_mode "${OPERATOR_KEY_FILE}")
    if [[ "${mode: -2}" != "00" ]]; then
      fail "${OPERATOR_KEY_FILE} is readable by group/others (mode ${mode}). Run: chmod 600 '${OPERATOR_KEY_FILE}'"
    fi
    OPERATOR_KEY=$(tr -d '\r\n' < "${OPERATOR_KEY_FILE}")
    [[ -n "${OPERATOR_KEY}" ]] || fail "${OPERATOR_KEY_FILE} is empty"
    return
  fi

  fail "No API key for the Stop and Status checks. Set ROOF_OPERATOR_API_KEY or create ${OPERATOR_KEY_FILE} (mode 600)."
}

# docker ps filter for exactly one container name (Docker matches the regex against "/<name>").
name_filter() {
  printf 'name=^/%s$' "${1//./\\.}"
}

# Docker output that one value could forge more of is read as JSON, one string a line (review ROOF-151-R3 F8): a value
# holding a newline, a delimiter or a control character cannot pass for another. A string that holds no escape (no
# control character, quote or backslash) is plain, and its text is between the quotes.
PLAIN_JSON_STRING='^"[^"\\]*"$'
ENV_FORMAT='{{range .Config.Env}}{{json .}}{{println}}{{end}}'

# lookup_container <docker ps filter>: sets CSTATE (running | stopped | missing), CID (full ID), CNAME and CPROJECT (the
# Docker Compose project that created it, or empty) for the one container matching the filter. Returns 1 when Docker cannot be asked or the answer is ambiguous; callers must then
# stop, because treating an unknown container as missing could start a second controller next to it or remove it while
# it runs. Anything but exited, created or dead counts as running: a restarting or paused container still owns its
# name and can drive the HAT.
lookup_container() {
  local lines state
  CSTATE="" CID="" CNAME="" CPROJECT=""
  # The Compose project label as JSON (review ROOF-151-R3 F8): one line, whatever it holds, and present even when it is
  # only spaces.
  lines=$(dockerc ps -a --no-trunc --filter "$1" \
    --format '{{.State}} {{.ID}} {{.Names}} {{json (.Label "com.docker.compose.project")}}') || return 1
  if [[ -z "${lines}" ]]; then
    CSTATE=missing
    return 0
  fi
  [[ "${lines}" != *$'\n'* ]] || return 1
  read -r state CID CNAME CPROJECT <<<"${lines}"
  [[ -n "${CID}" ]] || return 1
  # None is "" (or null); a plain one is its text; one holding an escape is kept as JSON, which names it safely.
  case "${CPROJECT}" in
    '""'|null|"") CPROJECT="" ;;
    *) [[ ! "${CPROJECT}" =~ ${PLAIN_JSON_STRING} ]] || CPROJECT=${CPROJECT:1:${#CPROJECT}-2} ;;
  esac
  case "${state}" in
    exited|created|dead) CSTATE=stopped ;;
    *) CSTATE=running ;;
  esac
}

# Prints running | stopped | missing for a container name; returns 1 when Docker cannot be asked.
container_state() {
  lookup_container "$(name_filter "${1:-${CONTAINER_NAME}}")" && echo "${CSTATE}"
}

# Sets PREVIOUS_EMULATOR to the emulator's host:port when the stopped previous version was deployed in HAT emulator mode,
# otherwise to empty; returns 1 when Docker cannot be asked, 2 when the settings it reads cannot be read with certainty
# (HAT_PROBLEM says which). The keys are matched as the controller matches them (review ROOF-151-R4 F12): in any case,
# '__' for ':', compared in the C locale; HatEmulator:Enabled true or false in any case, ASCII spaces around it; with it
# on, Host and Port, 127.0.0.1 and 5291 when not set. Only those are used: an unrelated key, and Host and Port while the
# emulator is off, are ignored whatever they hold, as the controller ignores them. This is not .NET's configuration
# parser, so what it would read ambiguously or unusually is refused for the operator to fix (review ROOF-151-R5): a
# recognised key given more than once, even with the same value; an Enabled given but empty; a value it uses that is not
# plain, holds a character outside ASCII (spaces outside ASCII included), or is an Enabled that is not true or false.
# Each entry is one JSON string, framed by Docker's encoder, which escapes only control characters, quotes and
# backslashes, and never '=': its key is the text before its first '=', and a value holding lines is one entry, never
# more settings.
HAT_PROBLEM=""
read_previous_hat() {
  local env line key name value enabled="" host="" port="" seen=" " setting
  PREVIOUS_EMULATOR="" HAT_PROBLEM=""
  env=$(dockerc inspect --format "${ENV_FORMAT}" "${PREVIOUS_ID}") || return 1
  while IFS= read -r line; do
    [[ "${line}" == '"'*'"' && "${line}" == *=* ]] || continue
    key=${line:1}
    key=${key%%=*}
    # A key holding an escape (a control character, a quote or a backslash) is none of these; nor one not starting so.
    [[ "${key}" == [Hh]* && "${key}" != *\\* ]] || continue
    name=$(printf '%s' "${key//__/:}" | LC_ALL=C tr '[:upper:]' '[:lower:]')
    # The value as Docker framed it: still JSON, so one holding an escape still shows it.
    value=${line#*=}
    value=${value%\"}
    case "${name}" in
      hatemulator:enabled) setting=Enabled enabled=${value} ;;
      hatemulator:host) setting=Host host=${value} ;;
      hatemulator:port) setting=Port port=${value} ;;
      *) continue ;;
    esac
    if [[ "${seen}" == *" ${setting} "* ]]; then
      HAT_PROBLEM="its HatEmulator:${setting} is given more than once (keys are read in any case, with __ as :), which is ambiguous"
      return 2
    fi
    seen+="${setting} "
  done <<<"${env}"
  [[ "${seen}" == *" Enabled "* ]] || return 0
  hat_value_problem Enabled "${enabled}" || return 2
  enabled=$(printf '%s' "${enabled}" | LC_ALL=C tr '[:upper:]' '[:lower:]')
  enabled=${enabled#"${enabled%%[! ]*}"}
  enabled=${enabled%"${enabled##*[! ]}"}
  case "${enabled}" in
    "")
      HAT_PROBLEM="its HatEmulator:Enabled is given, but empty"
      return 2 ;;
    false) return 0 ;;
    true) ;;
    *)
      HAT_PROBLEM="its HatEmulator:Enabled is '${enabled}', which the controller cannot read as true or false"
      return 2 ;;
  esac
  hat_value_problem Host "${host}" || return 2
  hat_value_problem Port "${port}" || return 2
  PREVIOUS_EMULATOR="${host:-127.0.0.1}:${port:-5291}"
}

# hat_value_problem <setting> <value as Docker framed it>: false, with HAT_PROBLEM, when a value the rollback uses is not
# plain (it holds an escape) or holds a byte outside printable ASCII (a character outside ASCII, a space outside ASCII).
hat_value_problem() {
  if [[ "$2" == *\\* ]]; then
    HAT_PROBLEM="its HatEmulator:$1 holds a control character, a quote or a backslash"
    return 1
  fi
  if [[ -n "$(printf '%s' "$2" | LC_ALL=C tr -d ' -~')" ]]; then
    HAT_PROBLEM="its HatEmulator:$1 holds a character outside ASCII"
    return 1
  fi
}

# Sets STATE/CURRENT_ID and PREVIOUS_STATE/PREVIOUS_ID, or stops the script (nothing has been changed when it runs).
read_container_states() {
  lookup_container "$(name_filter "${CONTAINER_NAME}")" \
    || fail "Could not read the state of ${CONTAINER_NAME} from Docker (docker ps failed or matched more than one container). Nothing was changed; check the Docker context and retry."
  STATE=${CSTATE} CURRENT_ID=${CID} CURRENT_PROJECT=${CPROJECT}
  lookup_container "$(name_filter "${PREVIOUS_CONTAINER_NAME}")" \
    || fail "Could not read the state of ${PREVIOUS_CONTAINER_NAME} from Docker (docker ps failed or matched more than one container). Nothing was changed; check the Docker context and retry."
  PREVIOUS_STATE=${CSTATE} PREVIOUS_ID=${CID} PREVIOUS_PROJECT=${CPROJECT}
  refuse_compose_managed "${CONTAINER_NAME}" "${CURRENT_PROJECT}"
  refuse_compose_managed "${PREVIOUS_CONTAINER_NAME}" "${PREVIOUS_PROJECT}"
}

# A container keeps its Compose labels under any name. Renamed to <name>-previous, a Compose controller would still be
# Compose's: a later `docker compose up` could start it next to this script's controller, and `down` could remove the
# rollback target. So the script replaces or restores only containers it created.
refuse_compose_managed() {
  local name=$1 project=$2
  if [[ -n "${project}" ]]; then
    fail "${name} was created by Docker Compose (project ${project}): this script replaces or restores only controllers it created. Nothing was changed. To move this controller to the script, follow \"Moving between Compose and the deploy script\" in docs/deployment.md."
  fi
}

# Two controllers must never drive the HAT, so a running <name>-previous stops a deploy or a rollback before anything
# is stopped.
refuse_running_previous() {
  if [[ "${PREVIOUS_STATE}" == "running" ]]; then
    fail "${PREVIOUS_CONTAINER_NAME} is running: two controllers must never share the HAT. Stop it (docker stop ${PREVIOUS_CONTAINER_NAME}) and retry."
  fi
}

# Calls the API from inside the container over loopback (exempt from the HTTPS requirement). The key is passed on
# stdin (curl -H @-) so it never appears in a process list. Prints the body, then the HTTP status on the last line.
container_api() {
  local method=$1 path=$2 name=${3:-${CONTAINER_NAME}}
  printf 'X-Api-Key: %s\n' "${OPERATOR_KEY}" \
    | dockerc exec -i "${name}" \
        curl -sS --max-time 15 -X "${method}" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "http://localhost:8080/${API_PATH}/${path}"
}

# curl's options for the checks from this machine: REMOTE_CA_CERT and REMOTE_CONNECT_TO, in the caller's remote_args.
set_remote_args() {
  remote_args=()
  if [[ -n "${REMOTE_CA_CERT}" ]]; then
    remote_args+=(--cacert "${REMOTE_CA_CERT}")
  fi
  if [[ -n "${REMOTE_CONNECT_TO}" ]]; then
    remote_args+=(--connect-to "${REMOTE_CONNECT_TO}")
  fi
}

# Calls the API from this machine at the published URL, as a remote client would. Same output as container_api.
remote_api() {
  local method=$1 path=$2
  local remote_args
  set_remote_args
  printf 'X-Api-Key: %s\n' "${OPERATOR_KEY}" \
    | curl -sS --max-time 15 ${remote_args[@]+"${remote_args[@]}"} -X "${method}" -H @- -H 'Accept: application/json' \
        -w '\n%{http_code}' "${REMOTE_BASE_URL}/${API_PATH}/${path}"
}

# Prints "<relayRegisterState>\t<relayRegisterMask>\t<commandedMotion>\t<hatMode>\t<isUsingPhysicalHardware>" from a
# status JSON on stdin, with "null" for a missing field.
parse_status() {
  if command -v jq >/dev/null 2>&1; then
    jq -r '[(.relayRegisterState // "null"), (.relayRegisterMask // "null" | tostring), (.commandedMotion // "null"), (.hatMode // "null"),
           (.isUsingPhysicalHardware | if . == null then "null" else tostring end)] | @tsv'
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import json,sys
d=json.load(sys.stdin)
v=lambda k: "null" if d.get(k) is None else str(d.get(k))
b=lambda k: "null" if d.get(k) is None else json.dumps(d.get(k))
print(v("relayRegisterState"), v("relayRegisterMask"), v("commandedMotion"), v("hatMode"), b("isUsingPhysicalHardware"), sep="\t")'
  else
    return 1
  fi
}

# Returns 0 only for HTTP 200 + Verified + mask 0 + commanded motion None.
check_verified_stop() {
  local response=$1 http_status body parsed state mask motion hat_mode
  http_status=$(tail -n 1 <<<"${response}")
  body=$(sed '$d' <<<"${response}")

  if [[ "${http_status}" != "200" ]]; then
    log_err "[deploy] Stop returned HTTP ${http_status:-<none>}: ${body}"
    return 1
  fi

  if ! parsed=$(parse_status <<<"${body}"); then
    log_err "[deploy] Cannot parse the Stop response (install jq or python3); treating the stop as unverified."
    return 1
  fi

  IFS=$'\t' read -r state mask motion hat_mode _ <<<"${parsed}"
  echo "[deploy] Stop result: relayRegisterState=${state} relayRegisterMask=${mask} commandedMotion=${motion}"
  [[ "${state}" == "Verified" && "${mask}" == "0" && "${motion}" == "None" ]]
}

confirm_unverified_stop() {
  if [[ "${FORCE_UNVERIFIED_STOP}" != "true" ]]; then
    fail "The roof stop could not be verified. Check the roof and relays, then retry. To replace the controller anyway, rerun with --force-unverified-stop (interactive confirmation required)."
  fi

  if ! { exec 3</dev/tty; } 2>/dev/null; then
    fail "--force-unverified-stop needs an interactive terminal for the confirmation."
  fi

  log_err "[deploy] WARNING: the roof stop is NOT verified. Relays may still be energized while the controller is replaced."
  log_err "[deploy] Confirm you can SEE the roof and it is not moving (or the drive is isolated)."
  local answer
  printf 'Type STOP-UNVERIFIED to continue: ' >&2
  read -r -u 3 answer || answer=""
  exec 3<&-
  [[ "${answer}" == "STOP-UNVERIFIED" ]] || fail "Confirmation not given; deployment aborted."
}

# Prints "<isMoving>\t<commandedMotion>" from a status JSON on stdin, with "null" for a missing field.
parse_motion() {
  if command -v jq >/dev/null 2>&1; then
    jq -r '[(.isMoving | if . == null then "null" else tostring end), (.commandedMotion // "null")] | @tsv'
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import json,sys
d=json.load(sys.stdin)
print("null" if d.get("isMoving") is None else json.dumps(d.get("isMoving")), "null" if d.get("commandedMotion") is None else str(d.get("commandedMotion")), sep="\t")'
  else
    return 1
  fi
}

# REQUIRE_IDLE_ROOF=true: fails, with nothing changed, unless an authenticated Status inside the container reports the
# roof idle (isMoving false and commandedMotion None). Anything it cannot read counts as not idle.
require_idle_roof() {
  local response http_status body parsed moving motion
  [[ "${REQUIRE_IDLE_ROOF}" == "true" ]] || return 0
  if ! response=$(container_api GET Status); then
    fail "REQUIRE_IDLE_ROOF=true: could not ask ${CONTAINER_NAME} for its Status, so the roof is not known to be idle. Nothing was changed; run again once the roof is idle."
  fi
  http_status=$(tail -n 1 <<<"${response}")
  body=$(sed '$d' <<<"${response}")
  if [[ "${http_status}" != "200" ]]; then
    fail "REQUIRE_IDLE_ROOF=true: ${CONTAINER_NAME}'s Status returned HTTP ${http_status:-<none>}, so the roof is not known to be idle. Nothing was changed; run again once the roof is idle."
  fi
  if ! parsed=$(parse_motion <<<"${body}"); then
    fail "REQUIRE_IDLE_ROOF=true: cannot parse ${CONTAINER_NAME}'s Status (install jq or python3), so the roof is not known to be idle. Nothing was changed; run again once the roof is idle."
  fi
  IFS=$'\t' read -r moving motion <<<"${parsed}"
  if [[ "${moving}" == "true" || ( "${motion}" != "None" && "${motion}" != "null" ) ]]; then
    fail "The roof is moving (commandedMotion=${motion}, isMoving=${moving}), and REQUIRE_IDLE_ROOF=true, so ${CONTAINER_NAME} was neither stopped nor replaced. Nothing was changed; run again once the roof is idle."
  fi
  if [[ "${moving}" != "false" || "${motion}" != "None" ]]; then
    fail "REQUIRE_IDLE_ROOF=true: ${CONTAINER_NAME}'s Status does not say whether the roof is moving (isMoving=${moving}, commandedMotion=${motion}). Nothing was changed; run again once the roof is idle."
  fi
  echo "[deploy] The roof is idle (isMoving=false, commandedMotion=None)."
}

stop_roof_before_replacing() {
  require_idle_roof
  echo "[deploy] Requesting a verified roof stop from ${CONTAINER_NAME}"
  local response
  if ! response=$(container_api POST Stop); then
    log_err "[deploy] Could not call Stop inside ${CONTAINER_NAME}."
    confirm_unverified_stop
    return
  fi

  if check_verified_stop "${response}"; then
    echo "[deploy] Roof stop verified (relay register all off)."
  else
    confirm_unverified_stop
  fi
}

# Prints a container's raw state and restart count, as "running 0"; returns 1 when Docker cannot be asked.
container_restarts() {
  dockerc inspect --format '{{.State.Status}} {{.RestartCount}}' "$1" 2>/dev/null
}

# The container supervisor's view of $2 (controller or ui) in container $1, as "<state> <starts>" (running 1,
# restarting 3, crash-loop 5, ...). Returns 1 when there is none: an image from before the supervisor, or a supervisor
# that has not written its state yet. The supervisor writes one fixed format, so a pattern reads it.
supervised_process() {
  local json pattern="\"$2\":\\{\"state\":\"([a-z-]+)\",\"pid\":[0-9a-z]+,\"starts\":([0-9]+)"
  json=$(dockerc exec "$1" cat /run/hvo-roof/supervisor.json 2>/dev/null) || return 1
  [[ "${json}" =~ ${pattern} ]] || return 1
  printf '%s %s\n' "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}"
}

# Returns 0 once the container reports ready, 2 as soon as it has exited (it will not become ready), 1 at the deadline.
# With --restart unless-stopped, Docker restarts a controller that exits instead of leaving it stopped, so a wait for a
# restart, or a restart count above its first reading, means it exited. The count is compared with that first reading,
# not with 0: a docker start of a container that is still running (a rollback whose stop failed) does not reset it. A
# controller that exits and is restarted before the first reading is caught at its next restart. Inside the container
# the supervisor restarts a controller that exits, and leaves it stopped after repeated crashes: a controller waiting to
# start again, left stopped, or started more often than at the first reading has exited in the same way. A first
# reading taken before the supervisor has started the controller (it turns the relays off first: 0 starts) counts its
# first start as the one expected, not as a restart.
wait_ready() {
  local name=$1 deadline=$((SECONDS + READY_TIMEOUT_SECONDS)) state raw restarts first_restarts="" supervised starts
  local first_starts="" expected_starts
  while (( SECONDS < deadline )); do
    # If Docker cannot be asked, keep polling until the deadline.
    state=$(container_state "${name}") || state=unknown
    if [[ "${state}" == "stopped" || "${state}" == "missing" ]]; then
      return 2
    fi
    if read -r raw restarts < <(container_restarts "${name}") && [[ "${restarts}" =~ ^[0-9]+$ ]]; then
      first_restarts=${first_restarts:-${restarts}}
      if [[ "${raw}" == "restarting" ]] || (( restarts > first_restarts )); then
        return 2
      fi
    fi
    if read -r supervised starts < <(supervised_process "${name}" controller); then
      first_starts=${first_starts:-${starts}}
      expected_starts=${first_starts}
      (( expected_starts > 0 )) || expected_starts=1
      if [[ "${supervised}" == "restarting" || "${supervised}" == "crash-loop" ]] || (( starts > expected_starts )); then
        EXIT_DETAIL="the container's supervisor reports the controller ${supervised} after ${starts} start(s)"
        return 2
      fi
    fi
    if dockerc exec "${name}" curl -fsS --max-time 5 http://localhost:8080/health/ready >/dev/null 2>&1; then
      return 0
    fi
    sleep "${POLL_INTERVAL_SECONDS}"
  done
  return 1
}

# The web UI's scheme in container $1 (http or https), from the first of its URLs (RoofWeb__Urls, the last one given,
# as in .NET). Returns 1 when the container has no web UI: a version from before the web UI, whose image does not set
# RoofWeb__Urls; or 2 when Docker cannot be asked, or its RoofWeb__Urls is not plain. Each entry is one JSON string.
web_ui_scheme() {
  local env line urls=""
  env=$(dockerc inspect --format "${ENV_FORMAT}" "$1") || return 2
  while IFS= read -r line; do
    [[ "${line}" == '"RoofWeb__Urls='* ]] || continue
    [[ "${line}" =~ ${PLAIN_JSON_STRING} ]] || return 2
    urls=${line:1:${#line}-2}
    urls=${urls#*=}
  done <<<"${env}"
  [[ -n "${urls}" ]] || return 1
  printf '%s\n' "${urls%%://*}"
}

# Returns 0 once the web UI in container $1 answers its liveness check over the container's loopback (scheme $2), 2 as
# soon as the supervisor has had to start it again (it exits: a web UI that cannot start is restarted with a backoff,
# for ever), 1 at the deadline. It starts next to the controller, so it is normally live by the time the controller is
# ready. The certificate names the Pi, not localhost, so it is not verified on loopback.
wait_web_ui() {
  local name=$1 scheme=$2 deadline=$((SECONDS + READY_TIMEOUT_SECONDS)) supervised starts first_starts=""
  while (( SECONDS < deadline )); do
    if read -r supervised starts < <(supervised_process "${name}" ui); then
      first_starts=${first_starts:-${starts}}
      if [[ "${supervised}" == "restarting" ]] || (( starts > first_starts )); then
        return 2
      fi
    fi
    if dockerc exec "${name}" curl -fsSk --max-time 5 "${scheme}://localhost:8088/health/live" >/dev/null 2>&1; then
      return 0
    fi
    sleep "${POLL_INTERVAL_SECONDS}"
  done
  return 1
}

# The web UI's liveness from this machine at REMOTE_WEB_URL, as a browser would reach it (anonymous).
remote_web_ui_live() {
  local remote_args
  set_remote_args
  curl -fsS --max-time 15 ${remote_args[@]+"${remote_args[@]}"} -o /dev/null "${REMOTE_WEB_URL}/health/live"
}

# The HAT a Status body on $2 reports, against the one this run deploys: Physical, or Emulated in HAT emulator mode.
# The --env settings are not enough on their own: a HatEmulator file in the secrets directory (read last) would
# override them. A rollback restores whatever the previous version used: the emulator only with ALLOW_EMULATED_HAT=true,
# and a missing hatMode (a version from before emulator mode) as the physical HAT. --verify-remote checks a controller
# this run did not deploy, so it too takes a missing hatMode as the physical HAT when that is the one expected. A version
# from before emulator mode fell back to the register simulation without an I2C bus, so, as the health check does, a
# missing hatMode counts as the physical HAT only with isUsingPhysicalHardware true. Sets VERIFIED_HAT_MODE, or FAILURE
# and returns 1.
check_hat_mode() {
  local name=$1 parsed state mask motion hat_mode physical expected
  if ! parsed=$(parse_status <<<"$2"); then
    FAILURE="cannot parse the Status from ${name} to check its HAT (install jq or python3)"
    return 1
  fi
  IFS=$'\t' read -r state mask motion hat_mode physical <<<"${parsed}"
  if [[ "${hat_mode}" == "null" && "${physical}" != "true" ]]; then
    FAILURE="${name} reports no hatMode and isUsingPhysicalHardware ${physical}: a version from before emulator mode on the register simulation, not the physical HAT"
    return 1
  fi
  if [[ "${ROLLBACK}" == "true" ]]; then
    case "${hat_mode}" in
      Physical) VERIFIED_HAT_MODE=Physical; return 0 ;;
      null) VERIFIED_HAT_MODE="Physical (no hatMode: a version from before emulator mode)"; return 0 ;;
      Emulated)
        if [[ "${ALLOW_EMULATED_HAT}" == "true" ]]; then
          VERIFIED_HAT_MODE="Emulated (ALLOW_EMULATED_HAT=true): the roof does not move"
          return 0
        fi
        FAILURE="${name} uses the HAT emulator (hatMode Emulated), not the physical HAT, and does not operate the roof, although it was not deployed for the emulator; check for HatEmulator settings in the secrets directory" ;;
      *) FAILURE="${name} reports hatMode ${hat_mode}, not the physical HAT" ;;
    esac
    return 1
  fi

  expected=Physical
  [[ -z "${HAT_EMULATOR_ENDPOINT}" ]] || expected=Emulated
  if [[ "${VERIFY_REMOTE}" == "true" && "${expected}" == "Physical" && "${hat_mode}" == "null" ]]; then
    VERIFIED_HAT_MODE="Physical (no hatMode: a version from before emulator mode)"
    return 0
  fi
  if [[ "${hat_mode}" != "${expected}" ]]; then
    FAILURE="${name} reports hatMode ${hat_mode}, but this deployment is for hatMode ${expected} (${HAT_SUMMARY}); check for HatEmulator settings in the secrets directory"
    return 1
  fi
  VERIFIED_HAT_MODE=${hat_mode}
}

# Readiness, an authenticated Status inside the container and its HAT, then an authenticated Status and a verified Stop
# from this machine. Sets FAILURE and returns 1 on the first check that fails.
verify_controller() {
  local name=$1 response http_status ready=0 web_scheme web=0
  echo "[verify] Waiting up to ${READY_TIMEOUT_SECONDS}s for ${name} to report /health/ready"
  EXIT_DETAIL=""
  wait_ready "${name}" || ready=$?
  if (( ready == 2 )); then
    FAILURE="${name} exited before it became ready (it stopped, or Docker restarted it)"
    [[ -z "${EXIT_DETAIL}" ]] || FAILURE+=": ${EXIT_DETAIL}"
    return 1
  elif (( ready != 0 )); then
    FAILURE="${name} did not become ready within ${READY_TIMEOUT_SECONDS}s"
    return 1
  fi

  if ! response=$(container_api GET Status "${name}"); then
    FAILURE="the authenticated Status call inside ${name} failed"
    return 1
  fi
  http_status=$(tail -n 1 <<<"${response}")
  if [[ "${http_status}" != "200" ]]; then
    FAILURE="the authenticated Status call inside ${name} returned HTTP ${http_status:-<none>} (is this script's key configured?)"
    return 1
  fi
  check_hat_mode "${name}" "$(sed '$d' <<<"${response}")" || return 1
  echo "[verify] Ready; authenticated Status inside the container: HTTP 200, hatMode ${VERIFIED_HAT_MODE}"

  # The web UI, in a version that has one (a rollback can restore a version from before it).
  web_scheme=$(web_ui_scheme "${name}") || web=$?
  if (( web == 2 )); then
    FAILURE="could not read the environment of ${name} from Docker to find its web UI"
    return 1
  elif (( web == 0 )); then
    wait_web_ui "${name}" "${web_scheme}" || web=$?
    if (( web == 2 )); then
      FAILURE="the web UI in ${name} exited and its supervisor is starting it again: it cannot start (see its log lines below)"
      return 1
    elif (( web != 0 )); then
      FAILURE="the web UI in ${name} did not answer ${web_scheme}://localhost:8088/health/live within ${READY_TIMEOUT_SECONDS}s"
      return 1
    fi
    echo "[verify] Web UI live inside the container (${web_scheme})"
  else
    echo "[verify] ${name} has no web UI (a version from before it): not checked"
  fi

  if [[ "${SKIP_REMOTE_CHECK}" == "true" ]]; then
    log_err "[verify] WARNING: SKIP_REMOTE_CHECK=true: ${REMOTE_BASE_URL} was not checked from this machine. Check it from a client before relying on remote control."
    return 0
  fi

  verify_remote false || return 1
  if (( web == 0 )); then
    if ! remote_web_ui_live; then
      FAILURE="the web UI did not answer at ${REMOTE_WEB_URL}/health/live from this machine (network, port or TLS; set REMOTE_CA_CERT for a certificate this machine does not trust)"
      return 1
    fi
    echo "[verify] Web UI live at ${REMOTE_WEB_URL}: OK"
  fi
}

# An authenticated Status, then a verified Stop, from this machine at the published URL. With check_hat=true the
# Status must also report the HAT this run expects (--verify-remote: nothing checked it inside the container). Sets
# FAILURE and returns 1 on the first check that fails.
verify_remote() {
  local check_hat=$1 response http_status
  if ! response=$(remote_api GET Status); then
    FAILURE="GET Status at ${REMOTE_BASE_URL} failed from this machine (network, port or TLS; set REMOTE_CA_CERT for a certificate this machine does not trust)"
    return 1
  fi
  http_status=$(tail -n 1 <<<"${response}")
  if [[ "${http_status}" != "200" ]]; then
    FAILURE="GET Status at ${REMOTE_BASE_URL} returned HTTP ${http_status:-<none>} to this machine: $(sed '$d' <<<"${response}")"
    return 1
  fi
  if [[ "${check_hat}" == "true" ]]; then
    check_hat_mode "the controller at ${REMOTE_BASE_URL}" "$(sed '$d' <<<"${response}")" || return 1
    echo "[verify] Authenticated Status at ${REMOTE_BASE_URL}: HTTP 200, hatMode ${VERIFIED_HAT_MODE}"
  fi

  if ! response=$(remote_api POST Stop) || ! check_verified_stop "${response}"; then
    FAILURE="POST Stop at ${REMOTE_BASE_URL} did not return a verified stop to this machine"
    return 1
  fi
  echo "[verify] Remote Status and verified Stop at ${REMOTE_BASE_URL}: OK"
}

show_containers() {
  dockerc ps -a --filter "name=${CONTAINER_NAME}" --format "table {{.Names}}\t{{.Status}}\t{{.Image}}" || true
}

# ---------------------------------------------------------------------------------------------------------------------
# The container's privileges and limits

# The CPU and memory limits a deploy or a rollback gives are kept as the controller's desired limits, whether or not the
# host could enforce them (#149; reviews ROOF-151-R1 F2 and R2 F5, F6): a record that the script, the installer and
# install.sh read the same way, with the same precedence (a limit given, then the record's, then the running
# controller's, then the default). Each record is a Docker volume of its own that nothing mounts, named
# <name>-limits-g<generation>, with the values in its labels: the script reaches it through any Docker context and before
# any image is built, and Docker keeps it across container replacements.
# A volume's labels cannot change, so a record is never changed in place: an update creates the next generation, which
# commits it (Docker creates a volume whole or not at all), and only then removes the older ones. A failed or interrupted
# update therefore leaves the last good record; older generations that could not be removed stay beside the newest; and
# the newest generation is always the one read, never an older one. Each record says which run wrote it (a deploy or a
# rollback, the controller's container and the time), and whether each value was set (given, or kept) or is the default,
# which follows RoofController's default. Every read validates the newest record whole (its format, its generation and
# the floor): one that fails is never applied. A run that needs a value from it is refused, and one given both is warned
# and records its own in its place. The installer's uninstall and purge remove every record, whether or not the
# container is there. A <name>-limits volume of an earlier format (this change's first version, never released) is not
# read; it goes with the next record written, or the uninstall.
DESIRED_PREFIX="${CONTAINER_NAME}-limits-g"
DESIRED_LEGACY="${CONTAINER_NAME}-limits"
DESIRED_LABEL=io.hvo.roof.limits
# The record's labels, in the order every reader reads and checks them (this script, install.sh and the installer, held
# to one table: tests/deploy/desired-limits-records.json). docker volume inspect writes each as JSON on a line of its
# own, so no label's value can pass for another's, or for more of the record (review ROOF-151-R3 F8).
DESIRED_LABELS=("${DESIRED_LABEL}" "${DESIRED_LABEL}.generation" "${DESIRED_LABEL}.cpus" "${DESIRED_LABEL}.cpus-source"
  "${DESIRED_LABEL}.memory" "${DESIRED_LABEL}.memory-source" "${DESIRED_LABEL}.written-by" "${DESIRED_LABEL}.written-at"
  "${DESIRED_LABEL}.controller")
DESIRED_FORMAT=""
for desired_label in "${DESIRED_LABELS[@]}"; do
  DESIRED_FORMAT+="${DESIRED_FORMAT:+$'\n'}{{json (index .Labels \"${desired_label}\")}}"
done
# A label's JSON as a record holds it: at most 40 letters, digits, '.', ':', '_' or '-', spelt out rather than as ranges,
# which some locales stretch beyond ASCII. Anything else (a space, a control character, a newline, a delimiter, a quote,
# a backslash, a character outside ASCII) is not a record's.
DESIRED_PLAIN='^"[0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.:_-]{0,40}"$'
# What read_desired_limits finds: DESIRED_STATE none, valid or invalid (DESIRED_PROBLEM says why); the newest record
# (DESIRED_NEWEST, its generation DESIRED_GENERATION), its values and sources, and who wrote it (DESIRED_WRITTEN, and the
# controller's container, DESIRED_CONTROLLER); the highest generation there is (DESIRED_HIGHEST), and every record
# volume, the earlier format's included (DESIRED_VOLUMES), which a write removes once the next record is committed.
DESIRED_STATE=none DESIRED_PROBLEM="" DESIRED_NEWEST="" DESIRED_GENERATION=0 DESIRED_HIGHEST=0 DESIRED_WRITTEN=""
DESIRED_CPUS="" DESIRED_CPUS_SOURCE="" DESIRED_MEMORY="" DESIRED_MEMORY_SOURCE="" DESIRED_CONTROLLER=""
DESIRED_VOLUMES=()

# desired_problem <format> <generation> <cpus> <cpu source> <memory> <memory source> <written by> <written at>
# <controller>: why a record's labels, each its whole value, are not the ones a deploy writes (nothing before or after,
# no other spelling: no leading zero, no trailing zero in the CPUs, the unit in lower case), or nothing. install.sh and
# the installer give the same reasons, in the same order.
desired_problem() {
  local cpus='^(0|[123456789][0123456789]{0,2})(\.[0123456789]?[123456789])?$'
  local memory='^([123456789][0123456789]{0,5})([mg])$' controller='^[0123456789abcdef]{12}$'
  local time='^[0123456789]{4}-[0123456789]{2}-[0123456789]{2}T[0123456789]{2}:[0123456789]{2}:[0123456789]{2}Z$'
  if [[ "$1" != 1 ]]; then
    echo "its format (${DESIRED_LABEL}) is '$1', not 1"
  elif [[ "$2" != "${DESIRED_GENERATION}" ]]; then
    echo "its generation label '$2' is not the ${DESIRED_GENERATION} of its name"
  elif [[ "$7" != deploy && "$7" != rollback ]] || [[ ! "$9" =~ ${controller} ]]; then
    echo "it does not say which run wrote it, and for which container ('$7', '$9')"
  elif [[ ! "$8" =~ ${time} ]]; then
    echo "its time '$8' is not a UTC time such as 2026-10-17T18:04:11Z"
  elif [[ ! "$3" =~ ${cpus} ]]; then
    echo "its CPUs '$3' are not a number of CPUs as the deploy script writes one (such as 1, 1.5 or 2.05)"
  elif [[ "$3" == 0* ]]; then
    echo "its CPUs '$3' are less than 1 vCPU, the minimum"
  elif [[ ! "$5" =~ ${memory} ]]; then
    echo "its memory '$5' is not a memory limit as the deploy script writes one (such as 1024m or 2g)"
  elif [[ "${BASH_REMATCH[2]}" == m ]] && (( 10#${BASH_REMATCH[1]} < 1024 )); then
    echo "its memory '$5' is less than 1g, the minimum"
  elif [[ "$4" != set && "$4" != default ]] || [[ "$6" != set && "$6" != default ]]; then
    echo "its sources '$4' and '$6' are not set or default"
  fi
}

# read_desired_limits: the records there are, and the newest one, read and validated (the variables above). Returns 1
# when Docker cannot be asked.
read_desired_limits() {
  local names name generation fields line index values=()
  DESIRED_STATE=none DESIRED_PROBLEM="" DESIRED_NEWEST="" DESIRED_GENERATION=0 DESIRED_HIGHEST=0 DESIRED_WRITTEN=""
  DESIRED_CPUS="" DESIRED_CPUS_SOURCE="" DESIRED_MEMORY="" DESIRED_MEMORY_SOURCE="" DESIRED_CONTROLLER=""
  DESIRED_VOLUMES=()
  names=$(dockerc volume ls --quiet --filter "name=${DESIRED_LEGACY}") || return 1
  while IFS= read -r name; do
    if [[ "${name}" == "${DESIRED_LEGACY}" ]]; then
      DESIRED_VOLUMES+=("${name}")
      continue
    fi
    [[ "${name}" == "${DESIRED_PREFIX}"* ]] || continue
    generation=${name#"${DESIRED_PREFIX}"}
    [[ "${generation}" =~ ^[123456789][0123456789]{0,8}$ ]] || continue
    DESIRED_VOLUMES+=("${name}")
    if (( generation > DESIRED_HIGHEST )); then
      DESIRED_HIGHEST=${generation}
      DESIRED_NEWEST=${name}
    fi
  done <<<"${names}"
  [[ -n "${DESIRED_NEWEST}" ]] || return 0
  DESIRED_GENERATION=${DESIRED_HIGHEST}
  fields=$(dockerc volume inspect --format "${DESIRED_FORMAT}" "${DESIRED_NEWEST}") || return 1
  # Each label is one JSON string on a line of its own; each must be plain, and is then checked whole.
  while IFS= read -r line; do
    values+=("${line}")
  done <<<"${fields}"
  if (( ${#values[@]} != ${#DESIRED_LABELS[@]} )); then
    DESIRED_PROBLEM="its labels could not be read as one JSON string each"
  else
    for (( index = 0; index < ${#values[@]}; index++ )); do
      if [[ ! "${values[index]}" =~ ${DESIRED_PLAIN} ]]; then
        DESIRED_PROBLEM="its label ${DESIRED_LABELS[index]} is not plain: at most 40 letters, digits, '.', ':', '_' or '-'"
        break
      fi
      values[index]=${values[index]:1:${#values[index]}-2}
    done
  fi
  if [[ -z "${DESIRED_PROBLEM}" ]]; then
    DESIRED_CPUS=${values[2]} DESIRED_CPUS_SOURCE=${values[3]} DESIRED_MEMORY=${values[4]} DESIRED_MEMORY_SOURCE=${values[5]}
    DESIRED_CONTROLLER=${values[8]}
    DESIRED_WRITTEN="written by a ${values[6]:-?} at ${values[7]:-?} for container ${DESIRED_CONTROLLER:-?}"
    DESIRED_PROBLEM=$(desired_problem "${values[@]}")
  fi
  if [[ -n "${DESIRED_PROBLEM}" ]]; then
    DESIRED_STATE=invalid
  else
    DESIRED_STATE=valid
  fi
}

# desired_source <the source resolve_limits gave a limit>: set or default, as a record keeps it.
desired_source() {
  case "$1" in
    "the default") echo default ;;
    *) echo set ;;
  esac
}

# write_desired_limits <deploy|rollback> <container>: the limits this run gives (CONTAINER_CPUS and CONTAINER_MEMORY,
# enforced or not, and whether each was set or is the default) as the next record, once the controller runs and has
# passed its checks (or the version a rollback brings back has started); then the older records go. A record that
# cannot be created leaves the last good one, and is a warning; older ones that cannot be removed stay beside it. Docker
# answers a create for a name it has with that volume, as it is, so the record is read back: one that is not this run's
# (another run took the generation) counts as not created.
write_desired_limits() {
  local by=$1 container=$2 next name old failed=() cpus_source memory_source at labels
  cpus_source=$(desired_source "${CPUS_SOURCE}")
  memory_source=$(desired_source "${MEMORY_SOURCE}")
  next=$(( DESIRED_HIGHEST + 1 ))
  name="${DESIRED_PREFIX}${next}"
  at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # As docker volume inspect writes the record back: each label as JSON (each plain) on a line of its own.
  labels=$(printf '"%s"\n' 1 "${next}" "${CONTAINER_CPUS}" "${cpus_source}" "${CONTAINER_MEMORY}" "${memory_source}" "${by}" \
    "${at}" "${container:0:12}")
  if ! dockerc volume create --label "${DESIRED_LABEL}=1" --label "${DESIRED_LABEL}.generation=${next}" \
      --label "${DESIRED_LABEL}.cpus=${CONTAINER_CPUS}" --label "${DESIRED_LABEL}.cpus-source=${cpus_source}" \
      --label "${DESIRED_LABEL}.memory=${CONTAINER_MEMORY}" --label "${DESIRED_LABEL}.memory-source=${memory_source}" \
      --label "${DESIRED_LABEL}.written-by=${by}" --label "${DESIRED_LABEL}.written-at=${at}" \
      --label "${DESIRED_LABEL}.controller=${container:0:12}" "${name}" >/dev/null 2>&1; then
    case "${DESIRED_STATE}" in
      valid)
        log_err "[deploy] WARNING: could not record the desired limits (CPUs ${CONTAINER_CPUS}, memory ${CONTAINER_MEMORY}) as ${name}: the last record, ${DESIRED_NEWEST} (CPUs ${DESIRED_CPUS}, memory ${DESIRED_MEMORY}), stays the desired one, and the next deploy keeps it unless CONTAINER_CPUS and CONTAINER_MEMORY are given." ;;
      invalid)
        log_err "[deploy] WARNING: could not record the desired limits (CPUs ${CONTAINER_CPUS}, memory ${CONTAINER_MEMORY}) as ${name}: the newest record, ${DESIRED_NEWEST}, is not valid, so the next deploy is refused unless CONTAINER_CPUS and CONTAINER_MEMORY are given." ;;
      *)
        log_err "[deploy] WARNING: could not record the desired limits (CPUs ${CONTAINER_CPUS}, memory ${CONTAINER_MEMORY}) as ${name}: there is no other record, so the next deploy keeps the running controller's limits, or the default, unless CONTAINER_CPUS and CONTAINER_MEMORY are given." ;;
    esac
    return 0
  fi
  if [[ "$(dockerc volume inspect --format "${DESIRED_FORMAT}" "${name}" 2>/dev/null)" != "${labels}" ]]; then
    log_err "[deploy] WARNING: ${name} is not this run's record (another run wrote it after this one read the records, or it cannot be read back): the desired limits (CPUs ${CONTAINER_CPUS}, memory ${CONTAINER_MEMORY}) are not recorded, no record is removed, and the next deploy reads the newest."
    return 0
  fi
  echo "[deploy] The desired limits are recorded for the next deploy (${name}): CPUs ${CONTAINER_CPUS} (${cpus_source}), memory ${CONTAINER_MEMORY} (${memory_source})"
  for old in ${DESIRED_VOLUMES[@]+"${DESIRED_VOLUMES[@]}"}; do
    dockerc volume rm --force "${old}" >/dev/null 2>&1 || failed+=("${old}")
  done
  if (( ${#failed[@]} > 0 )); then
    log_err "[deploy] WARNING: could not remove the older records (${failed[*]}): they stay, and the newest, ${name}, is the one the next deploy reads."
  fi
}

# resolve_limits, once the containers' states are known and before anything changes: a CPU or memory limit not given
# keeps its desired value (the newest record's, read_desired_limits), so that an override persists across upgrades and
# rollbacks, a rollback on a host that could not enforce it included; a record that says the default gives the default.
# Without a record (a controller deployed before them), it keeps the one the running controller (CONTAINER_NAME, in any
# state) has, or is RoofController's default when it has none (a controller deployed before the limits) or there is no
# controller. A kept one is checked against the floor too. Then what the run gives (limit_args, for docker run and
# the rollback's docker update), says it gives (LIMITS_SUMMARY), and expects Docker to record (WANT_NANO, WANT_MEMORY
# and WANT_SWAP).
LIMITS_SUMMARY="" WANT_NANO=0 WANT_MEMORY=0 WANT_SWAP=0
limit_args=()
resolve_limits() {
  local kept="kept from ${CONTAINER_NAME}" desired="kept from the last deploy" mib memory cpus_text
  read_desired_limits \
    || fail "Could not read the desired limits' records from Docker (volumes ${DESIRED_PREFIX}<generation>; a limit not set keeps them). Nothing was changed; check the Docker context and retry."
  if [[ "${DESIRED_STATE}" == "invalid" ]]; then
    if [[ -z "${CONTAINER_CPUS}" || -z "${CONTAINER_MEMORY}" ]]; then
      fail "The newest desired-limits record, ${DESIRED_NEWEST}, is not valid: ${DESIRED_PROBLEM}. It is not applied, and nothing was changed: give CONTAINER_CPUS and CONTAINER_MEMORY (the deploy then records them in its place), or remove it (docker volume rm ${DESIRED_NEWEST})."
    fi
    log_err "[deploy] WARNING: the newest desired-limits record, ${DESIRED_NEWEST}, is not valid (${DESIRED_PROBLEM}): it is not applied, and this run records the limits it is given in its place."
  elif [[ "${DESIRED_STATE}" == "valid" ]]; then
    echo "[deploy] The desired limits' record, ${DESIRED_NEWEST} (${DESIRED_WRITTEN}): CPUs ${DESIRED_CPUS} (${DESIRED_CPUS_SOURCE}), memory ${DESIRED_MEMORY} (${DESIRED_MEMORY_SOURCE})"
    # One written for another container than the running controller is older than it (the record of the deploy that
    # started it could not be written, or something other than this script replaced it): it still comes first, and
    # says so.
    if [[ -n "${CONTAINER_CPUS}" && -n "${CONTAINER_MEMORY}" ]]; then
      :
    elif [[ "${STATE}" == "missing" ]]; then
      log_err "[deploy] WARNING: the desired limits' record, ${DESIRED_NEWEST}, was written for container ${DESIRED_CONTROLLER}, and there is no ${CONTAINER_NAME} now: a limit not given still takes its value."
    elif [[ "${CURRENT_ID:0:12}" != "${DESIRED_CONTROLLER}" ]]; then
      log_err "[deploy] WARNING: the desired limits' record, ${DESIRED_NEWEST}, was written for container ${DESIRED_CONTROLLER}, not for the running ${CONTAINER_NAME} (${CURRENT_ID:0:12}): a limit not given still takes the record's value, not the running controller's."
    fi
    # A value it records as set is kept; one it records as the default is the default, not the running controller's.
    if [[ -z "${CONTAINER_CPUS}" ]]; then
      if [[ "${DESIRED_CPUS_SOURCE}" == set ]]; then
        set_cpus "${DESIRED_CPUS}" "${desired}" "set CONTAINER_CPUS to at least 1"
      else
        set_cpus "${DEFAULT_CONTAINER_CPUS}" "the default" "give at least 1"
      fi
    fi
    if [[ -z "${CONTAINER_MEMORY}" ]]; then
      if [[ "${DESIRED_MEMORY_SOURCE}" == set ]]; then
        set_memory "${DESIRED_MEMORY}" "${desired}" "set CONTAINER_MEMORY to at least 1g"
      else
        set_memory "${DEFAULT_CONTAINER_MEMORY}" "the default" "give at least 1g"
      fi
    fi
  fi
  if [[ ( -z "${CONTAINER_CPUS}" || -z "${CONTAINER_MEMORY}" ) && "${STATE}" != "missing" ]]; then
    read_confinement "${CURRENT_ID}" \
      || fail "Could not read the CPU and memory limits of ${CONTAINER_NAME} from Docker (one not set keeps the running controller's). Nothing was changed; check the Docker context and retry."
    if [[ -z "${CONTAINER_CPUS}" ]] && (( CONF_NANO > 0 )); then
      (( CONF_NANO % 10000000 == 0 )) \
        || fail "${CONTAINER_NAME} has a CPU limit of ${CONF_NANO} billionths of a CPU (NanoCpus), not a number of CPUs with at most two decimals, so it cannot be kept: set CONTAINER_CPUS. Nothing was changed."
      set_cpus "$(( CONF_NANO / 1000000000 )).$(printf '%02d' $(( CONF_NANO % 1000000000 / 10000000 )))" "${kept}" \
        "set CONTAINER_CPUS to at least 1"
    fi
    if [[ -z "${CONTAINER_MEMORY}" ]] && (( CONF_MEMORY > 0 )); then
      (( CONF_MEMORY % 1048576 == 0 )) \
        || fail "${CONTAINER_NAME} has a memory limit of ${CONF_MEMORY} bytes, not a whole number of MiB, so it cannot be kept: set CONTAINER_MEMORY. Nothing was changed."
      mib=$(( CONF_MEMORY / 1048576 ))
      if (( mib % 1024 == 0 )); then memory="$(( mib / 1024 ))g"; else memory="${mib}m"; fi
      set_memory "${memory}" "${kept}" "set CONTAINER_MEMORY to at least 1g"
    fi
  fi
  [[ -n "${CONTAINER_CPUS}" ]] || set_cpus "${DEFAULT_CONTAINER_CPUS}" "the default" "give at least 1"
  [[ -n "${CONTAINER_MEMORY}" ]] || set_memory "${DEFAULT_CONTAINER_MEMORY}" "the default" "give at least 1g"

  limit_args=(--cpus "${CONTAINER_CPUS}" --memory "${CONTAINER_MEMORY}" --memory-swap "${CONTAINER_MEMORY}"
    --pids-limit "${CONTAINER_PIDS_LIMIT}")
  if (( CPU_HUNDREDTHS == 100 )); then cpus_text="1 CPU"; else cpus_text="${CONTAINER_CPUS} CPUs"; fi
  LIMITS_SUMMARY="${cpus_text}, ${CONTAINER_MEMORY} of memory and no swap, at most ${CONTAINER_PIDS_LIMIT} processes and threads"
  WANT_NANO=$(( CPU_HUNDREDTHS * 10000000 ))
  WANT_MEMORY=${MEMORY_BYTES}
  WANT_SWAP=${MEMORY_BYTES}
  echo "[deploy] The container's limits: ${cpus_text} (${CPUS_SOURCE}), ${CONTAINER_MEMORY} of memory (${MEMORY_SOURCE}) and no swap, at most ${CONTAINER_PIDS_LIMIT} processes and threads"
}

# reported <docker info field>: true or false, or "not reported" for anything else (empty, <no value>).
reported() {
  case "$1" in
    true|false) echo "$1" ;;
    *) echo "not reported" ;;
  esac
}

# docker_runs_here <the name Docker gives its host>: true when the Docker this run uses runs on this machine (its
# context's endpoint is a Unix socket, and Docker names this machine's host name), so that this machine's cgroup files
# are its host's.
docker_runs_here() {
  local endpoint
  endpoint=$(docker context inspect --format '{{.Endpoints.docker.Host}}' "${DOCKER_CONTEXT}" 2>/dev/null) || return 1
  [[ "${endpoint}" == unix://* && -n "$1" && "$1" == "$(uname -n)" ]]
}

# Docker drops a limit it cannot enforce with only a warning, and runs the controller without it: the Raspberry Pi's
# firmware adds cgroup_disable=memory to the kernel's command line, for one (it is not in /boot/firmware/cmdline.txt).
# RoofController refuses such a host for a deploy, as SkyMonitor does (#149; SkyMonitor #1285), before anything changes:
# one docker info must report MemoryLimit and SwapLimit (the memory limit, and no swap beyond it), CPUCfsQuota and
# PidsLimit true, a field it does not report counting as unsupported, and at least the CPUs of the CPU limit. Every
# finding is listed at once, with the cgroup version, the kernel's cgroup v2 controllers when Docker runs on this
# machine, Docker's own warnings about limits, and the remedy; there is no override.
# A rollback is a recovery path, so a limit Docker cannot enforce does not refuse it (the owner's decision on #149): it
# goes ahead with the limits Docker can still enforce (ENFORCE_CPU, ENFORCE_MEMORY and ENFORCE_PIDS, and limit_args and
# LIMITS_SUMMARY say only those), and warns loudly of each one it cannot, with the remedy, here and in the controller's
# log (UNENFORCED_WARNING, which warn_in_container_log writes there once it runs). Every other refusal still holds for a
# rollback: the floor, more CPUs than Docker reports for a CPU limit it gives, and a Docker that does not answer.
# A memory limit is a cap, not a reservation: one above the memory Docker reports is kept, with a warning. Docker 29's
# template field is CPUCfsQuota (its JSON key CpuCfsQuota).
# check_docker_limits <deploy|dry-run|rollback>: dry-run reports what a deploy would refuse (exit 1), or what a rollback
# (--dry-run --rollback) would warn of and go ahead with.
DOCKER_INFO_FORMAT=$'{{.MemoryLimit}},{{.CPUCfsQuota}},{{.PidsLimit}},{{.SwapLimit}},{{.NCPU}},{{.MemTotal}},{{.CgroupVersion}},{{.Name}}\n{{range .Warnings}}{{println .}}{{end}}'
ENFORCE_CPU=true ENFORCE_MEMORY=true ENFORCE_SWAP=true ENFORCE_PIDS=true
UNENFORCED_WARNING=()
check_docker_limits() {
  local prefix="[$1]" info memory cpu pids swap ncpu total cgroup_version name line controllers memory_text="?"
  local capabilities=() findings=() host="" refused="the deploy is refused" cpus_note="" memory_note="" remedy
  local rollback=false without="so Docker would run the controller without" goes="THE ROLLBACK GOES AHEAD"
  [[ "${ROLLBACK}" != "true" ]] || { rollback=true; refused="the rollback is refused"; without="so the version the rollback brings back runs without"; }
  if [[ "$1" == "dry-run" ]]; then
    refused="a deploy would be refused"
    [[ "${rollback}" != "true" ]] || { refused="a rollback would be refused"; goes="A ROLLBACK WOULD GO AHEAD"; }
  fi
  [[ "${CPUS_SOURCE}" == given ]] || cpus_note=" (${CPUS_SOURCE})"
  [[ "${MEMORY_SOURCE}" == given ]] || memory_note=" (${MEMORY_SOURCE})"
  info=$(dockerc info --format "${DOCKER_INFO_FORMAT}") \
    || fail "Could not ask Docker (context '${DOCKER_CONTEXT}') which limits it enforces. Nothing was changed."
  # Commas, so that a field Docker leaves empty stays in its place; Docker's warnings follow, one a line.
  IFS=, read -r memory cpu pids swap ncpu total cgroup_version name <<<"${info%%$'\n'*}"
  # Memory and swap are separate controls (review ROOF-151-R1 F3): without swap control the memory limit still holds,
  # and only the swap beyond it is not limited.
  if [[ "${memory}" != "true" ]]; then
    ENFORCE_MEMORY=false ENFORCE_SWAP=false
    capabilities+=("docker-memory-limit-unsupported: docker info reports MemoryLimit $(reported "${memory}") and SwapLimit $(reported "${swap}"), ${without} its memory limit (${CONTAINER_MEMORY}, and no swap beyond it).")
  elif [[ "${swap}" != "true" ]]; then
    ENFORCE_SWAP=false
    if [[ "${rollback}" == "true" ]]; then
      capabilities+=("docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit $(reported "${swap}"), so the version the rollback brings back keeps its memory limit (${CONTAINER_MEMORY}) but may swap beyond it.")
    else
      capabilities+=("docker-swap-limit-unsupported: docker info reports MemoryLimit true and SwapLimit $(reported "${swap}"), so Docker would let the controller swap beyond its memory limit (${CONTAINER_MEMORY}).")
    fi
  fi
  if [[ "${cpu}" != "true" ]]; then
    ENFORCE_CPU=false
    capabilities+=("docker-cpu-quota-unsupported: docker info reports CPUCfsQuota $(reported "${cpu}"), ${without} its CPU limit (${CONTAINER_CPUS}).")
  fi
  if [[ "${pids}" != "true" ]]; then
    ENFORCE_PIDS=false
    capabilities+=("docker-pids-limit-unsupported: docker info reports PidsLimit $(reported "${pids}"), ${without} its process limit (${CONTAINER_PIDS_LIMIT}).")
  fi
  # The CPUs count for a CPU limit Docker gives; a rollback gives none it cannot enforce.
  if [[ "${rollback}" != "true" || "${ENFORCE_CPU}" == "true" ]]; then
    if [[ ! "${ncpu}" =~ ^[0-9]+$ ]]; then
      findings+=("docker-host-cpus-unreported: docker info reports NCPU '${ncpu}', so the CPU limit of ${CONTAINER_CPUS} cannot be checked against the CPUs.")
    elif (( CPU_HUNDREDTHS > 10#${ncpu} * 100 )); then
      if (( 10#${ncpu} >= 1 )); then
        findings+=("docker-host-cpus-insufficient: docker info reports ${ncpu} CPUs, fewer than the CPU limit of ${CONTAINER_CPUS}${cpus_note}: give CONTAINER_CPUS from 1 to ${ncpu}.")
      else
        findings+=("docker-host-cpus-insufficient: docker info reports ${ncpu} CPUs, and the CPU limit is at least 1.")
      fi
    fi
  fi
  if (( ${#capabilities[@]} > 0 )); then
    if [[ "${cgroup_version}" =~ ^[0-9]+$ ]]; then host="cgroup v${cgroup_version}"; else host="cgroup version not reported"; fi
    if docker_runs_here "${name}" && controllers=$(head -c 512 "${CGROUP_CONTROLLERS_FILE}" 2>/dev/null); then
      host+="; ${CGROUP_CONTROLLERS_FILE}: \"${controllers//[[:cntrl:]]/ }\""
    else
      host+="; /sys/fs/cgroup/cgroup.controllers on ${PI_HOST} lists the kernel's cgroup v2 controllers (Docker needs memory, cpu and pids)"
    fi
    while IFS= read -r line; do
      case "${line}" in
        *[Ll][Ii][Mm][Ii][Tt]*|*[Cc][Ff][Ss]*|*[Cc][Gg][Rr][Oo][Uu][Pp]*) host+="; Docker warns \"${line//[[:cntrl:]]/ }\"" ;;
      esac
    done < <(tail -n +2 <<<"${info}")
    remedy="To fix it on a Raspberry Pi: its firmware adds cgroup_disable=memory to the kernel's command line itself (it is not in the file). Append \" cgroup_enable=memory\" to the end of the single line in /boot/firmware/cmdline.txt (on that line, not a new one; the later argument wins), reboot the Pi with the roof idle, and check that docker info --format '{{.MemoryLimit}} {{.SwapLimit}} {{.CPUCfsQuota}} {{.PidsLimit}}' prints true four times. On another host, give the Docker daemon the memory, cpu and pids cgroup controllers."
  fi
  # A deploy is refused for every finding; a rollback only for the ones that are not a limit Docker cannot enforce.
  [[ "${rollback}" == "true" ]] || findings=(${capabilities[@]+"${capabilities[@]}"} ${findings[@]+"${findings[@]}"})
  if (( ${#findings[@]} > 0 )); then
    log_err "${prefix} ERROR: Docker on ${PI_HOST} cannot give the controller its limits (${LIMITS_SUMMARY}), so ${refused}. Nothing was changed. Every finding:"
    for line in "${findings[@]}"; do
      log_err "${prefix}   - ${line}"
    done
    if [[ "${rollback}" != "true" && ${#capabilities[@]} -gt 0 ]]; then
      log_err "${prefix}   Docker's host: ${host}."
      log_err "${prefix}   ${remedy} There is no override (docs/deployment.md, \"The container's privileges and limits\")."
    fi
    exit 1
  fi
  if (( ${#capabilities[@]} > 0 )); then
    UNENFORCED_WARNING=("**********************************************************************************************************"
      "${goes}, BUT DOCKER ON ${PI_HOST} CANNOT ENFORCE THESE LIMITS, so the version it brings back runs WITHOUT them. A rollback is a recovery path, so it is not refused (#149); an install, an upgrade or a deploy is refused here until this is fixed:")
    for line in "${capabilities[@]}"; do
      UNENFORCED_WARNING+=("  - ${line}")
    done
    UNENFORCED_WARNING+=("  Docker's host: ${host}."
      "  ${remedy} Then deploy again (docs/deployment.md, \"The container's privileges and limits\")."
      "**********************************************************************************************************")
    for line in "${UNENFORCED_WARNING[@]}"; do
      log_err "${prefix} WARNING: ${line}"
    done
    enforceable_limits
  fi
  [[ ! "${total}" =~ ^[0-9]+$ ]] || memory_text="$(( 10#${total} / 1048576 ))m"
  if [[ "${ENFORCE_MEMORY}" == "true" && "${total}" =~ ^[0-9]+$ ]] && (( MEMORY_BYTES > 10#${total} )); then
    log_err "${prefix} WARNING: CONTAINER_MEMORY=${CONTAINER_MEMORY}${memory_note} is more than the ${memory_text} of memory Docker reports on ${PI_HOST}. It is kept: a memory limit is a cap, not a reservation, so the machine runs out of memory before the container reaches it."
  fi
  if (( ${#capabilities[@]} > 0 )); then
    echo "${prefix} Docker on ${PI_HOST} (${ncpu:-?} CPUs, ${memory_text} of memory) enforces only these limits, which the rollback gives: ${LIMITS_SUMMARY}"
  else
    echo "${prefix} Docker on ${PI_HOST} (${ncpu} CPUs, ${memory_text} of memory) enforces the limits: ${LIMITS_SUMMARY}"
  fi
}

# enforceable_limits: for a rollback on a Docker that cannot enforce some limits, the ones it can (limit_args), and what
# they are (LIMITS_SUMMARY): the version brought back runs without the others.
enforceable_limits() {
  local parts=()
  limit_args=()
  if [[ "${ENFORCE_CPU}" == "true" ]]; then
    limit_args+=(--cpus "${CONTAINER_CPUS}")
    if (( CPU_HUNDREDTHS == 100 )); then parts+=("1 CPU"); else parts+=("${CONTAINER_CPUS} CPUs"); fi
  else
    parts+=("no CPU limit (Docker here cannot enforce one)")
  fi
  if [[ "${ENFORCE_MEMORY}" == "true" && "${ENFORCE_SWAP}" == "true" ]]; then
    limit_args+=(--memory "${CONTAINER_MEMORY}" --memory-swap "${CONTAINER_MEMORY}")
    parts+=("${CONTAINER_MEMORY} of memory and no swap")
  elif [[ "${ENFORCE_MEMORY}" == "true" ]]; then
    limit_args+=(--memory "${CONTAINER_MEMORY}")
    parts+=("${CONTAINER_MEMORY} of memory (swap beyond it not limited: Docker here cannot)")
  else
    parts+=("no memory limit (Docker here cannot enforce one)")
  fi
  if [[ "${ENFORCE_PIDS}" == "true" ]]; then
    limit_args+=(--pids-limit "${CONTAINER_PIDS_LIMIT}")
    parts+=("at most ${CONTAINER_PIDS_LIMIT} processes and threads")
  else
    parts+=("no process limit (Docker here cannot enforce one)")
  fi
  LIMITS_SUMMARY="${parts[0]}, ${parts[1]}, ${parts[2]}"
}

# warn_in_container_log <container>: UNENFORCED_WARNING in the restored controller's own log (docker logs), as well as
# in this output, so that it stays where the controller's are: written to the standard error of the container's first
# process (tini), which is the container's log. One that cannot be written is a warning of its own: the rollback does
# not depend on it.
warn_in_container_log() {
  local line when
  (( ${#UNENFORCED_WARNING[@]} > 0 )) || return 0
  when=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  if { echo "[rollback] WARNING: from deploy-roofcontroller-rpi.sh --rollback, ${when}:"
       for line in "${UNENFORCED_WARNING[@]}"; do echo "[rollback] WARNING: ${line}"; done
     } | dockerc exec -i "$1" sh -c 'cat > /proc/1/fd/2' >/dev/null 2>&1; then
    echo "[rollback] The warning is in the controller's log too (docker logs ${CONTAINER_NAME})."
  else
    log_err "[rollback] WARNING: could not write the warning above to the controller's log (docker exec failed): it is only in this output."
  fi
}

# read_confinement <container>: what Docker reports of a container's privileges and limits, in CONF_READ_ONLY,
# CONF_NANO, CONF_MEMORY, CONF_SWAP, CONF_PIDS (0 for none: Docker reports null, or 0 once a limit was taken off),
# CONF_DROP, CONF_ADD and CONF_OPTIONS. Returns 1 when Docker cannot be asked.
CONF_READ_ONLY="" CONF_NANO="" CONF_MEMORY="" CONF_SWAP="" CONF_PIDS="" CONF_DROP="" CONF_ADD="" CONF_OPTIONS=""
read_confinement() {
  local config
  config=$(dockerc inspect --format '{{.HostConfig.ReadonlyRootfs}} {{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.MemorySwap}} {{json .HostConfig.PidsLimit}} {{json .HostConfig.CapDrop}} {{json .HostConfig.CapAdd}} {{json .HostConfig.SecurityOpt}}' "$1") \
    || return 1
  read -r CONF_READ_ONLY CONF_NANO CONF_MEMORY CONF_SWAP CONF_PIDS CONF_DROP CONF_ADD CONF_OPTIONS <<<"${config}"
  [[ "${CONF_PIDS}" != "null" ]] || CONF_PIDS=0
}

# check_limits <container> <name>: the limits Docker gave the container (it can drop one with only a warning), from
# read_confinement: exactly the ones this run gives, for a new controller and for the version a rollback brings back
# (on a Docker that cannot enforce one, the ones it can). Sets FAILURE and returns 1 when they differ.
check_limits() {
  local id=$1 name=$2
  if ! read_confinement "${id}"; then
    FAILURE="could not read the privileges and limits of ${name} from Docker"
    return 1
  fi
  # A rollback on a Docker that cannot enforce a limit gives none of it (check_docker_limits), so it is not checked.
  if [[ "${ENFORCE_CPU}" == "true" && "${CONF_NANO}" != "${WANT_NANO}" ]] \
      || [[ "${ENFORCE_MEMORY}" == "true" && "${CONF_MEMORY}" != "${WANT_MEMORY}" ]] \
      || [[ "${ENFORCE_SWAP}" == "true" && "${CONF_SWAP}" != "${WANT_SWAP}" ]] \
      || [[ "${ENFORCE_PIDS}" == "true" && "${CONF_PIDS}" != "${CONTAINER_PIDS_LIMIT}" ]]; then
    FAILURE="${name} does not have the limits (${LIMITS_SUMMARY}): Docker reports NanoCpus ${CONF_NANO:-?}, Memory ${CONF_MEMORY:-?}, MemorySwap ${CONF_SWAP:-?} and PidsLimit ${CONF_PIDS:-?}"
    return 1
  fi
}

# check_privileges <name>: the privileges this script gives a new controller (hardening_args), from read_confinement.
# Sets FAILURE and returns 1 when they differ.
check_privileges() {
  # Docker writes the capabilities with or without CAP_, as it was given them or by its version.
  if [[ "${CONF_READ_ONLY}" != "true" || "${CONF_DROP//CAP_/}" != '["ALL"]' || "${CONF_ADD//CAP_/}" != '["CHOWN","KILL","SETGID","SETUID"]' \
      || "${CONF_OPTIONS}" != *'"no-new-privileges'* ]]; then
    FAILURE="$1 does not have the container's privileges (read-only root filesystem ${CONF_READ_ONLY:-?}, capabilities dropped ${CONF_DROP:-?} and added ${CONF_ADD:-?}, security options ${CONF_OPTIONS:-?})"
    return 1
  fi
}

# --rollback gives the version it brings back this run's limits (docker update), and puts back the ones it had if the
# rollback does not get as far as starting it: LIMITS_RESTORE holds them as docker update options, for container
# LIMITS_RESTORE_ID, from the change until the start (the EXIT trap runs restore_rollback_limits). Docker cannot take a
# CPU or memory limit off a container (0 leaves one as it is), so one the version had none of stays: LIMITS_KEPT names it.
LIMITS_RESTORE=()
LIMITS_RESTORE_ID=""
LIMITS_KEPT=""

# give_rollback_limits <container> <name>: called only once the roof's verified stop has passed, so that no check that
# refuses the rollback comes after the change. Fails, with the limits put back, when Docker cannot give them.
give_rollback_limits() {
  local id=$1 name=$2 restore=() kept=""
  read_confinement "${id}" \
    || fail "Could not read the limits of ${name} from Docker. The current controller was not stopped."
  if [[ ${#limit_args[@]} -eq 0 ]]; then
    echo "[rollback] Docker here enforces none of the limits: ${name} is given none (see the warning above)"
    return 0
  fi
  if [[ "${ENFORCE_CPU}" == "true" && "${CONF_NANO}" != "${WANT_NANO}" ]]; then
    if (( CONF_NANO > 0 )); then
      restore+=(--cpus "$(( CONF_NANO / 1000000000 )).$(printf '%09d' $(( CONF_NANO % 1000000000 )))")
    else
      kept="a CPU limit"
    fi
  fi
  if [[ "${ENFORCE_MEMORY}" == "true" && ( "${CONF_MEMORY}" != "${WANT_MEMORY}" \
      || ( "${ENFORCE_SWAP}" == "true" && "${CONF_SWAP}" != "${WANT_SWAP}" ) ) ]]; then
    if (( CONF_MEMORY > 0 )); then
      restore+=(--memory "${CONF_MEMORY}")
      (( CONF_SWAP == 0 )) || [[ "${ENFORCE_SWAP}" != "true" ]] || restore+=(--memory-swap "${CONF_SWAP}")
    else
      kept+="${kept:+ and }a memory limit"
    fi
  fi
  if [[ "${ENFORCE_PIDS}" == "true" && "${CONF_PIDS}" != "${CONTAINER_PIDS_LIMIT}" ]]; then
    # Docker takes -1 for no process limit.
    if (( CONF_PIDS > 0 )); then restore+=(--pids-limit "${CONF_PIDS}"); else restore+=(--pids-limit -1); fi
  fi
  if [[ ${#restore[@]} -eq 0 && -z "${kept}" ]]; then
    echo "[rollback] ${name} already has the limits: ${LIMITS_SUMMARY}"
    return 0
  fi

  echo "[rollback] Giving ${name} the limits: ${LIMITS_SUMMARY}"
  LIMITS_RESTORE=(${restore[@]+"${restore[@]}"})
  LIMITS_RESTORE_ID=${id}
  LIMITS_KEPT=${kept}
  if ! dockerc update "${limit_args[@]}" "${id}" >/dev/null; then
    restore_rollback_limits
    fail "Could not give ${name} the limits (see above). The current controller was not stopped."
  fi
  if ! check_limits "${id}" "${name}"; then
    restore_rollback_limits
    fail "${FAILURE}. The current controller was not stopped."
  fi
}

# Puts back the limits give_rollback_limits changed (by container ID, wherever the rollback got to) and disarms it.
restore_rollback_limits() {
  local id=${LIMITS_RESTORE_ID}
  [[ -n "${id}" ]] || return 0
  LIMITS_RESTORE_ID=""
  if [[ ${#LIMITS_RESTORE[@]} -gt 0 ]]; then
    if dockerc update "${LIMITS_RESTORE[@]}" "${id}" >/dev/null 2>&1; then
      log_err "[rollback] Put back the limits container ${id:0:12} had before this rollback."
    else
      log_err "[rollback] WARNING: could not put back the limits container ${id:0:12} had before this rollback (docker update ${LIMITS_RESTORE[*]}); check them with docker inspect."
    fi
  fi
  if [[ -n "${LIMITS_KEPT}" ]]; then
    log_err "[rollback] WARNING: container ${id:0:12} keeps ${LIMITS_KEPT} this rollback gave it, which it had none of before: Docker cannot take a CPU or memory limit off a container. Deploy again to run without it."
  fi
}

# ---------------------------------------------------------------------------------------------------------------------
# Pull mode

# inspect_pulled <format>: fields of the pulled image, for the platform it was pulled for. Docker's containerd image
# store can hold several platforms of an image: asked for one it does not hold, it reports empty fields (or fails, in
# the classic store). A Docker CLI without `image inspect --platform` reports the only platform the classic store holds.
inspect_pulled() {
  dockerc image inspect ${inspect_platform_args[@]+"${inspect_platform_args[@]}"} --format "$1" "${IMAGE_REF}"
}

# check_pulled_image: the pulled image must be the one IMAGE_REF's digest names, for BUILD_PLATFORM. Docker checks the
# digest as it pulls; this also covers an image that was already on the Pi, and a registry mirror in between.
check_pulled_image() {
  local digests platform labels version revision
  inspect_platform_args=()
  if dockerc image inspect --help 2>/dev/null | grep -q -- '--platform'; then
    inspect_platform_args=(--platform "${BUILD_PLATFORM}")
  fi
  # The platform first: the classic store fails every inspect for a platform it does not hold (Docker's error is shown).
  platform=$(inspect_pulled '{{.Os}}/{{.Architecture}}') || platform=""
  [[ "${platform}" != "/" ]] || platform=""
  [[ "${platform}" == "${BUILD_PLATFORM}" ]] \
    || fail "The image ${IMAGE_REF} is not for ${BUILD_PLATFORM} (Docker reports '${platform:-no image for that platform}'). The running controller was not touched."
  digests=$(inspect_pulled '{{range .RepoDigests}}{{println .}}{{end}}') \
    || fail "Could not inspect the pulled image ${IMAGE_REF}. The running controller was not touched."
  grep -q "@${IMAGE_DIGEST}\$" <<<"${digests}" \
    || fail "The pulled image does not carry the digest ${IMAGE_DIGEST} (Docker reports: $(tr '\n' ' ' <<<"${digests}")). The running controller was not touched."
  labels=$(inspect_pulled '{{with .Config}}{{index .Labels "org.opencontainers.image.version"}} {{index .Labels "org.opencontainers.image.revision"}}{{end}}' 2>/dev/null) \
    || labels=""
  read -r version revision <<<"${labels}" || true
  echo "[pull] ${IMAGE_DIGEST} for ${platform}: version ${version:-unknown}${revision:+, commit ${revision:0:12}}"
  # The pre-flight and the controller run this platform's image, whatever else the Pi holds under the digest.
  image_platform_args=(--platform "${BUILD_PLATFORM}")
}

# ---------------------------------------------------------------------------------------------------------------------
# Offline: a release's archive (IMAGE_ARCHIVE), loaded in place of the pull

# archive_json <query> <file> [<platform>]: from a JSON file of an OCI archive, with jq or python3. "image": the digest
# of the one image index.json names; "platform": the digest of the one manifest an index lists for the platform;
# "config": a manifest's config's digest; and "layers": its layers' digests, one a line. Empty when there is none, or
# more than one. Docker's own reading of the files it loads matches key names without regard to case, so these read
# the archive's own files as they are, or not at all: "layout", oci-layout's version; "index", index.json's one image
# ("image <digest>") and its names ("name <key> <value>", by key), with nothing else in it; and "docker",
# manifest.json's one image ("config <path>", "layer <path>" for each layer, "tag <name>" for each tag), with nothing
# else. A value that is not printable ASCII without spaces makes "index" and "docker" empty, so no line can pass for
# another. "unique" is "unique" when the file is one JSON value whose every object gives each name once, compared as
# decoded ("a" and "\u0061" are one name): jq reads its tokens (--stream) before it builds objects, and an object
# rebuilt from them has fewer tokens when a name is given twice; python3 is given each object's names as they are read
# (object_pairs_hook). python3 refuses a name given twice in every query, and jq's queries are run only on files that
# are unique.
archive_json() {
  local query=$1 file=$2 platform=${3:-}
  if command -v jq >/dev/null 2>&1; then
    case "${query}" in
      unique) jq -n -r --stream '[inputs] as $tokens | [fromstream($tokens[])] as $values
        | if ($values | length) == 1 and ([$values[0] | tostream] | length) == ($tokens | length) then "unique" else "" end' \
        "${file}" ;;
      image) jq -r 'if (.manifests | length) == 1 then (.manifests[0].digest // "") else "" end' "${file}" ;;
      platform) jq -r --arg os "${platform%%/*}" --arg arch "${platform#*/}" \
        '[.manifests[] | select(.platform.os == $os and .platform.architecture == $arch)]
         | if length == 1 then (.[0].digest // "") else "" end' "${file}" ;;
      config) jq -r '.config.digest // ""' "${file}" ;;
      layers) jq -r '(.layers // [])[] | (.digest // "")' "${file}" ;;
      layout) jq -r 'if type == "object" and keys == ["imageLayoutVersion"] and (.imageLayoutVersion | type) == "string"
                     then .imageLayoutVersion else "" end' "${file}" ;;
      index) jq -r 'def word: type == "string" and (explode | length > 0 and all(. > 32 and . < 127));
        if type == "object" and (keys - ["schemaVersion", "mediaType", "manifests"]) == []
           and (.manifests | type) == "array" and (.manifests | length) == 1 and (.manifests[0] | type) == "object"
           and ((.manifests[0] | keys) - ["mediaType", "digest", "size", "annotations"]) == []
           and ((.manifests[0].annotations // {}) | type) == "object"
        then .manifests[0] | (.annotations // {}) as $names
          | if (.digest | word) and ([$names | to_entries[] | (.key | word) and (.value | word)] | all)
            then "image \(.digest)", ($names | to_entries | sort_by(.key) | .[] | "name \(.key) \(.value)")
            else "" end
        else "" end' "${file}" ;;
      docker) jq -r 'def word: type == "string" and (explode | length > 0 and all(. > 32 and . < 127));
        if type == "array" and length == 1 and (.[0] | type) == "object"
           and ((.[0] | keys) - ["Config", "RepoTags", "Layers"]) == []
           and ((.[0].Layers // []) | type) == "array" and ((.[0].RepoTags // []) | type) == "array"
           and (.[0] | (.Config | word) and ([(.Layers // [])[], (.RepoTags // [])[] | word] | all))
        then .[0] | "config \(.Config)", ((.Layers // [])[] | "layer \(.)"), ((.RepoTags // [])[] | "tag \(.)")
        else "" end' "${file}" ;;
    esac
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import json,sys
query, path, platform = sys.argv[1:]
def once(pairs):
    # Each name once, as decoded: Go reads an unpaired surrogate escape as U+FFFD, so two of them are one name to it.
    names = set()
    for name, _ in pairs:
        name = "".join("\ufffd" if 0xD800 <= ord(c) <= 0xDFFF else c for c in name)
        if name in names:
            raise ValueError("a name given twice")
        names.add(name)
    return dict(pairs)
def constant(name):
    raise ValueError("not JSON: " + name)
with open(path, "rb") as f:
    d = json.loads(f.read().decode("utf-8"), object_pairs_hook=once, parse_constant=constant)
def present(value, default):
    # As jq'"'"'s // : null and false are absent.
    return default if value is None or value is False else value
def word(value):
    return isinstance(value, str) and len(value) > 0 and all(32 < ord(c) < 127 for c in value)
if query == "unique":
    print("unique")
elif query == "layout":
    ok = isinstance(d, dict) and list(d) == ["imageLayoutVersion"] and isinstance(d["imageLayoutVersion"], str)
    print(d["imageLayoutVersion"] if ok else "")
elif query == "index":
    lines = []
    found = d.get("manifests") if isinstance(d, dict) else None
    if (isinstance(d, dict) and set(d) <= {"schemaVersion", "mediaType", "manifests"}
            and isinstance(found, list) and len(found) == 1 and isinstance(found[0], dict)
            and set(found[0]) <= {"mediaType", "digest", "size", "annotations"}
            and isinstance(present(found[0].get("annotations"), {}), dict)):
        names = present(found[0].get("annotations"), {})
        if word(found[0].get("digest")) and all(word(k) and word(v) for k, v in names.items()):
            lines = ["image " + found[0]["digest"]] + ["name {} {}".format(k, names[k]) for k in sorted(names)]
    print("\n".join(lines))
elif query == "docker":
    lines = []
    if isinstance(d, list) and len(d) == 1 and isinstance(d[0], dict) and set(d[0]) <= {"Config", "RepoTags", "Layers"}:
        layers, tags = present(d[0].get("Layers"), []), present(d[0].get("RepoTags"), [])
        if (isinstance(layers, list) and isinstance(tags, list) and word(d[0].get("Config"))
                and all(word(value) for value in layers + tags)):
            lines = ["config " + d[0]["Config"]] + ["layer " + value for value in layers] + ["tag " + value for value in tags]
    print("\n".join(lines))
elif query == "layers":
    for layer in present(d.get("layers"), []):
        print(layer.get("digest") if isinstance(layer, dict) and isinstance(layer.get("digest"), str) else "")
else:
    found = [d.get("config")] if query == "config" else d.get("manifests") or []
    found = [m for m in found if isinstance(m, dict)]
    if query == "platform":
        found = [m for m in found if isinstance(m.get("platform"), dict)
                 and "{}/{}".format(m["platform"].get("os"), m["platform"].get("architecture")) == platform]
    digest = found[0].get("digest") if len(found) == 1 else ""
    print(digest if isinstance(digest, str) else "")' "${query}" "${file}" "${platform}"
  else
    return 1
  fi
}

# archive_members <archive>: the members of a tar archive as a release writes one (build/release-assets.py), read from
# the archive's own headers, not from what a tar program lists: one a line, in order, "file <name>" or "folder <name>"
# (without its trailing /). It prints "problem <what is wrong>" last and fails unless the archive is that and nothing
# else: each header a POSIX ustar header ("ustar", version "00") that sums to its checksum, for a plain file (type 0)
# or a folder of no size (type 5), with no link target, nothing in its name prefix or after it, and a name of a-z, 0-9,
# ".", "-" and "/" alone; at most 256 of them; then the end of the archive, two empty blocks, and only empty blocks after
# it. So it holds no pax or GNU extension header (which could give a member another name, a link target or a sparse
# map), no hard or symbolic link (a pax hard link can carry data), no device, FIFO or sparse file, and nothing GNU tar,
# bsdtar and Docker's Go reader could read differently: they all find these members, with these names and sizes.
archive_members() {
  local archive=$1 size blocks block=0 count=0 sum checksum stored i code name shown end length kind
  local octal_field='^ *([0-7]+) *$'
  local -a header=() ascii=()
  local letters=abcdefghijklmnopqrstuvwxyz numerals=0123456789
  for (( i = 0; i < 26; i++ )); do ascii[97 + i]=${letters:i:1}; done
  for (( i = 0; i < 10; i++ )); do ascii[48 + i]=${numerals:i:1}; done
  ascii[45]=-
  ascii[46]=.
  ascii[47]=/
  size=$(wc -c < "${archive}" | tr -d ' ') || size=""
  if ! [[ "${size}" =~ ^[0-9]+$ ]] || (( size % 512 != 0 )); then
    echo "problem its size is not a whole number of 512-byte blocks"
    return 1
  fi
  blocks=$(( size / 512 ))
  while true; do
    if (( block >= blocks )); then
      echo "problem it ends without the two empty blocks that end an archive"
      return 1
    fi
    read -r -a header <<<"$(dd if="${archive}" bs=512 skip="${block}" count=1 2>/dev/null | od -An -v -tu1 | tr '\n' ' ')"
    if (( ${#header[@]} != 512 )); then
      echo "problem its block ${block} could not be read"
      return 1
    fi
    sum=0
    checksum=0
    for (( i = 0; i < 512; i++ )); do sum=$(( sum + header[i] )); done
    if (( sum == 0 )); then
      # The end: this block and every one after it empty, two at least.
      if (( blocks - block < 2 )) \
          || [[ "$(tail -c +$(( block * 512 + 1 )) "${archive}" | tr -d '\000' | wc -c | tr -d ' ')" != 0 ]]; then
        echo "problem it does not end with empty blocks alone, two at least, after its last member"
        return 1
      fi
      return 0
    fi
    count=$(( count + 1 ))
    if (( count > 256 )); then
      echo "problem it holds more than 256 members"
      return 1
    fi

    # The checksum: the header's bytes summed, its checksum field counted as spaces.
    stored=""
    for (( i = 148; i < 156; i++ )); do
      code=${header[i]}
      checksum=$(( checksum + code ))
      if (( code >= 48 && code <= 55 )); then
        stored+=${ascii[code]}
      elif (( code == 0 || code == 32 )); then
        stored+=" "
      else
        stored+="?"
      fi
    done
    if ! [[ "${stored}" =~ ${octal_field} ]] || (( 8#${BASH_REMATCH[1]} != sum - checksum + 256 )); then
      echo "problem its block ${block} is not a tar header that sums to its checksum"
      return 1
    fi
    if [[ "${header[*]:257:8}" != "117 115 116 97 114 0 48 48" ]]; then
      echo "problem member ${count}'s header is not a POSIX ustar header"
      return 1
    fi

    # The name, up to its first NUL, with nothing after that: shown in what follows only when it is one.
    name=""
    shown=""
    end=100
    for (( i = 0; i < 100; i++ )); do
      code=${header[i]}
      if (( code == 0 )); then
        end=${i}
        break
      fi
      if [[ -z "${ascii[code]:-}" ]]; then
        name="?"
        break
      fi
      name+=${ascii[code]}
    done
    for (( i = end; i < 100; i++ )); do
      if (( header[i] != 0 )); then
        name="?"
      fi
    done
    [[ -z "${name}" || "${name}" == *"?"* ]] || shown=" (${name})"

    # What it is, by its raw type.
    case "${header[156]}" in
      48) kind="file" ;;
      53) kind="folder" ;;
      49) echo "problem member ${count}${shown} is a hard link"; return 1 ;;
      50) echo "problem member ${count}${shown} is a symbolic link"; return 1 ;;
      51|52) echo "problem member ${count}${shown} is a device"; return 1 ;;
      54) echo "problem member ${count}${shown} is a FIFO"; return 1 ;;
      83) echo "problem member ${count}${shown} is a GNU sparse file"; return 1 ;;
      120|103) echo "problem member ${count}${shown} is a pax extended header"; return 1 ;;
      75|76) echo "problem member ${count}${shown} is a GNU long name or link"; return 1 ;;
      *) echo "problem member ${count}${shown} is of type ${header[156]}, not a file (48, '0') or a folder (53, '5')"; return 1 ;;
    esac
    if [[ -z "${shown}" ]]; then
      echo "problem member ${count} has no name of a-z, 0-9, '.', '-' and '/' alone"
      return 1
    fi

    # Its size: 11 octal digits, then NUL or a space.
    stored=""
    for (( i = 124; i < 135; i++ )); do
      code=${header[i]}
      if (( code >= 48 && code <= 55 )); then
        stored+=${ascii[code]}
      fi
    done
    if (( ${#stored} != 11 )) || (( header[135] != 0 && header[135] != 32 )); then
      echo "problem member ${count}${shown} has a size that is not 11 octal digits"
      return 1
    fi
    length=$(( 8#${stored} ))

    # No link target, and nothing in the name prefix or after it (where GNU and other formats keep more).
    for (( i = 157; i < 512; i++ )); do
      if (( i == 257 )); then
        i=344
      elif (( header[i] != 0 )); then
        if (( i < 257 )); then
          echo "problem member ${count}${shown} has a link target"
        else
          echo "problem member ${count}${shown} has a name prefix, or more in its header than a POSIX ustar header holds"
        fi
        return 1
      fi
    done

    if [[ "${kind}" == folder ]]; then
      name=${name%/}
      if (( length != 0 )); then
        echo "problem member ${count}${shown} is a folder that holds data"
        return 1
      fi
    elif [[ "${name}" == */ ]]; then
      echo "problem member ${count}${shown} is a file whose name ends with /"
      return 1
    fi
    echo "${kind} ${name}"
    block=$(( block + 1 + (length + 511) / 512 ))
  done
}

# check_json_once <file> <what>: IMAGE_ARCHIVE is refused unless its <what>, a member check_image_archive took out of it,
# is one JSON value that gives each name once in each object (archive_json unique). Both readers here keep the last of
# two values for a name, and Docker's Go decoder can keep parts of both (an object decoded into the map the first one
# made), so such a file would be read one way by this check and another by Docker.
check_json_once() {
  [[ "$(archive_json unique "$1" 2>/dev/null)" == "unique" ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its $2 is not one JSON value that gives each name once in each object (a name given twice, even spelt with escapes, is read one way by this check and another by Docker): give the release's archive of that image. Nothing was changed."
}

# check_image_archive: IMAGE_ARCHIVE must be the release's archive of the image IMAGE_REF's digest names, for
# BUILD_PLATFORM, and hold nothing else, before Docker reads any of it. Each of Docker's image stores loads its own part
# of an archive and applies all it finds there: the containerd store reads index.json, and the names it gives, when
# there is an oci-layout (manifest.json when there is none); the classic store reads manifest.json, every image in it
# and their tags (RepoTags). A load cannot be undone by checking the image afterwards (check_loaded_image), and an image
# Docker already holds by an allowed ID would pass that check whatever the load did, so the archive is checked whole:
#  - its members, as its own headers give them (archive_members: plain POSIX ustar headers, no link, special file,
#    sparse file or extension header), are oci-layout, index.json, manifest.json and blobs/sha256/<hex> for the index,
#    the platform's manifest, its config and its layers, each once and each a file, with the folders blobs and
#    blobs/sha256, which are folders;
#  - every JSON file it reads (oci-layout, index.json, manifest.json, the index, the platform's manifest and its config)
#    is one value, with no name given twice in an object, so this check and Docker read it alike;
#  - oci-layout is an OCI image layout 1.0.0, and index.json names that index alone, with no name but IMAGE_REF's own
#    (io.containerd.image.name <repository>:<tag>, with or without org.opencontainers.image.ref.name <tag>);
#  - manifest.json is that one image: the platform's config and its layers, in order, tagged IMAGE_REF's
#    <repository>:<tag> or not at all;
#  - every blob is the one its digest names, layers included.
# What is checked is what Docker loads: the archive is copied once, first, into a folder of the script's own that no one
# but this user and root can change (ARCHIVE_DIR, make_private_folder), and that copy (ARCHIVE_COPY) is checked, has its
# SHA-256 compared with IMAGE_ARCHIVE_SHA256 when it is given, and is the file docker load reads. Whatever then happens
# to IMAGE_ARCHIVE (replaced, or written over in place) changes nothing Docker reads. ARCHIVE_MANIFEST and ARCHIVE_CONFIG
# are then the platform's manifest and config: with the index, the IDs Docker gives the image it loads (the containerd
# image store's is the index's, or the manifest's for the platform; the classic store's is the config's). Run before any
# Docker call, and in a dry run too: it only reads the archive, and writes only into ARCHIVE_DIR, which is removed once
# Docker has loaded the copy, and on exit.
ARCHIVE_MANIFEST=""
ARCHIVE_CONFIG=""
ARCHIVE_DIR=""
ARCHIVE_COPY=""
# Room the copy must leave on its file system, for the members the check takes out of it beside it: 16 MiB, in KiB.
ARCHIVE_HEADROOM_KB=16384

# Private folders. The script's work folder (WORK_DIR) and its copy of IMAGE_ARCHIVE (ARCHIVE_DIR) are each made as a
# new folder, mode 0700, by its physical path, and only in a TMPDIR that is a trusted_folder (make_private_folder); each
# is removed only while it is still the folder that was made (private_folder), and only the exact path that was made.
# No one but root and this user can then change what is in them, rename them, or put another folder or a link in their
# place, so what the script writes there, checks and reads back (the copy Docker loads, the new controller's cidfile, the
# identity check's copies) stays what it wrote, and their removal can only remove them.

# trusted_folder <folder>: prints <folder>'s physical path (every symbolic link in it followed) when that folder, and
# every folder above it up to /, is a real folder (not a link), root's or this user's, that no one else can write to
# unless it is sticky (as /tmp is: there, no one else can rename or remove what root or this user owns). No one else can
# then change what that path names, or a path below it that root or this user made. Fails, printing nothing, otherwise.
trusted_folder() {
  local physical folder user
  user=$(id -u) || return 1
  physical=$(cd -- "$1" 2>/dev/null && pwd -P) || return 1
  folder=${physical}
  while true; do
    [[ -d "${folder}" && ! -L "${folder}" ]] || return 1
    [[ -n "$(find "${folder}" -maxdepth 0 -type d \( -user 0 -o -user "${user}" \) \
        \( -perm -1000 -o \( ! -perm -0020 ! -perm -0002 \) \) 2>/dev/null)" ]] || return 1
    [[ "${folder}" != / ]] || break
    folder=$(dirname "${folder}")
  done
  printf '%s\n' "${physical}"
}

# private_folder <folder>: <folder> is still a folder make_private_folder made: a real folder (not a link), this user's,
# mode 0700.
private_folder() {
  [[ -d "$1" && ! -L "$1" && -n "$(find "$1" -maxdepth 0 -type d -user "$(id -u)" -perm 0700 2>/dev/null)" ]]
}

# make_private_folder <name>: makes a new folder, <name>.<random> with mode 0700, under TMPDIR's physical path (TMPDIR is
# /tmp when it is not set) once TMPDIR is a trusted_folder, and prints the exact path it made. Returns 2, having made
# nothing, when TMPDIR is not a trusted_folder; 1 when the folder cannot be made, or is not then a private_folder (it
# then prints the path, which is never removed).
make_private_folder() {
  local parent folder
  parent=$(trusted_folder "${TMPDIR:-/tmp}") || return 2
  folder=$(umask 077 && mktemp -d "${parent%/}/$1.XXXXXX") || return 1
  printf '%s\n' "${folder}"
  private_folder "${folder}"
}

# The reason make_private_folder's return code <status> gives for <what>, for a message.
private_folder_problem() {
  if (( $1 == 2 )); then
    printf '%s' "TMPDIR (${TMPDIR:-/tmp}) is not a folder only this user and root can change, for $2: it is not there, or it or a folder above it is someone else's, or writable by others and not sticky, so someone else could change what is written there. Nothing was made in it. Set TMPDIR to a folder that is, such as /tmp"
  elif [[ -n "${3:-}" ]]; then
    printf '%s' "Could not make a private folder in ${TMPDIR:-/tmp} for $2 ($3 is not a folder of this user's with mode 0700 once made)"
  else
    printf '%s' "Could not make a private folder in ${TMPDIR:-/tmp} for $2"
  fi
}

# make_work_dir: WORK_DIR, the script's work folder (make_private_folder), unless it is made already. Sets FAILURE and
# fails when it cannot be.
make_work_dir() {
  local folder status=0
  [[ -z "${WORK_DIR}" ]] || return 0
  folder=$(make_private_folder hvo-roof-deploy) || status=$?
  if (( status != 0 )); then
    FAILURE=$(private_folder_problem "${status}" "its work folder" "${folder}")
    return 1
  fi
  WORK_DIR=${folder}
}

# remove_work_dir: removes WORK_DIR, only while it is still the private_folder that was made, and only what the script
# puts there: the new controller's cidfile, a built image, the identity check's store copy and its copy of the secrets
# (from docker cp, so only that one folder is removed whole, and rm never follows a link in it), then the folder with
# rmdir. Anything else there is left, with the folder, and said. WORK_DIR is forgotten either way.
remove_work_dir() {
  local folder=${WORK_DIR}
  [[ -n "${folder}" ]] || return 0
  WORK_DIR=""
  if ! private_folder "${folder}"; then
    log_err "[deploy] WARNING: ${folder} is no longer the work folder this script made (a folder of this user's, mode 0700), so it was left as it is."
    return 1
  fi
  rm -f -- "${folder}/new-controller.cid" "${folder}/image.tar" "${folder}/identity-schema-check.json" 2>/dev/null || true
  rm -rf -- "${folder}/identity-schema-secrets" 2>/dev/null || true
  if ! rmdir -- "${folder}" 2>/dev/null; then
    log_err "[deploy] WARNING: ${folder} holds more than this script put there, so it was left as it is."
    return 1
  fi
}

# remove_archive_members <folder>: removes what check_image_archive took out of the archive into <folder> (inside
# ARCHIVE_DIR), file by known file, then its folders with rmdir; anything else there is left.
remove_archive_members() {
  local members=$1
  [[ -d "${members}" && ! -L "${members}" ]] || return 0
  if [[ -d "${members}/blobs" && ! -L "${members}/blobs" ]]; then
    if [[ -d "${members}/blobs/sha256" && ! -L "${members}/blobs/sha256" ]]; then
      rm -f -- "${members}"/blobs/sha256/* 2>/dev/null || true
      rmdir -- "${members}/blobs/sha256" 2>/dev/null || true
    fi
    rmdir -- "${members}/blobs" 2>/dev/null || true
  fi
  rm -f -- "${members}/oci-layout" "${members}/index.json" "${members}/manifest.json" 2>/dev/null || true
  rmdir -- "${members}" 2>/dev/null || true
}

# remove_archive_dir: removes ARCHIVE_DIR, only while it is still the private_folder that was made, and only what the
# script put there: the copy and the members it took out, file by known file, then the folder with rmdir. Anything
# else there is left, with the folder, and said. ARCHIVE_DIR is forgotten either way.
remove_archive_dir() {
  local folder=${ARCHIVE_DIR}
  [[ -n "${folder}" ]] || return 0
  ARCHIVE_DIR=""
  if ! private_folder "${folder}"; then
    log_err "[load] WARNING: ${folder} is no longer the folder this script made for its copy of IMAGE_ARCHIVE (a folder of this user's, mode 0700), so it was left as it is."
    return 1
  fi
  remove_archive_members "${folder}/members"
  rm -f -- "${folder}/image.oci.tar" 2>/dev/null || true
  if ! rmdir -- "${folder}" 2>/dev/null; then
    log_err "[load] WARNING: ${folder} holds more than this script put there, so it was left as it is."
    return 1
  fi
}

check_image_archive() {
  local folder listing kind members member named names manifest config layer layers blobs expected docker size free needed digest
  local status
  local digest_pattern='^sha256:[0-9a-f]{64}$' blob_pattern='^blobs/sha256/[0-9a-f]{64}$' image_name image_tag=""
  local release_archive="give the release's archive of that image. Nothing was changed."
  local release_archive_sentence="Give the release's archive of that image. Nothing was changed."

  # The one name the archive may give the image: IMAGE_REF's own, <repository>:<tag> (none when it has no tag).
  image_name=${IMAGE_REF%@*}
  if [[ "${image_name##*/}" == *:* ]]; then
    image_tag=${image_name##*:}
  else
    image_name=""
  fi

  # The copy Docker will load goes in a new folder of the script's own (make_private_folder): made by its physical path,
  # only in a TMPDIR that no one but this user and root can change, and removed (remove_archive_dir) only while it is
  # still that folder. Only the exact folder made is ever removed.
  status=0
  folder=$(make_private_folder hvo-roof-archive) || status=$?
  (( status == 0 )) || fail "$(private_folder_problem "${status}" "the copy of IMAGE_ARCHIVE that Docker loads" "${folder}"). Nothing was changed."
  ARCHIVE_DIR=${folder}
  size=$(wc -c < "${IMAGE_ARCHIVE}" | tr -d ' ') || size=""
  free=$(df -Pk "${folder}" 2>/dev/null | awk 'NR == 2 { print $4 }') || free=""
  [[ "${size}" =~ ^[0-9]+$ && "${free}" =~ ^[0-9]+$ ]] \
    || fail "Could not tell the size of IMAGE_ARCHIVE ${IMAGE_ARCHIVE}, or how much room ${folder%/*} has for a copy of it. Nothing was changed."
  needed=$(( (size + 1023) / 1024 + ARCHIVE_HEADROOM_KB ))
  (( free >= needed )) \
    || fail "There is not room in ${folder%/*} for a private copy of IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: it needs $(( (needed + 1023) / 1024 )) MiB (the archive and 16 MiB more), and $(( free / 1024 )) MiB is free. The script loads only a copy of its own, so that Docker reads the bytes it checked. Free some room there, or set TMPDIR to a folder with room. Nothing was changed."
  ARCHIVE_COPY="${folder}/image.oci.tar"
  # One read of the archive, into a file only this user can read or write, hashed as it is written.
  if ! digest=$(umask 077 && tee "${ARCHIVE_COPY}" < "${IMAGE_ARCHIVE}" | sha256_hex) || ! chmod 0600 "${ARCHIVE_COPY}"; then
    fail "Could not copy IMAGE_ARCHIVE ${IMAGE_ARCHIVE} into ${folder} (see above). Nothing was changed."
  fi
  if [[ -n "${IMAGE_ARCHIVE_SHA256}" && "${digest}" != "${IMAGE_ARCHIVE_SHA256}" ]]; then
    fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} is not the release's archive: its SHA-256 is ${digest}, and IMAGE_ARCHIVE_SHA256 (release.json's) is ${IMAGE_ARCHIVE_SHA256}. Nothing was changed."
  fi
  echo "[load] Checking a copy of ${IMAGE_ARCHIVE} that only this user can change (SHA-256 ${digest}${IMAGE_ARCHIVE_SHA256:+, as release.json gives it}): Docker loads that copy"

  # Its members, from the archive's own headers (archive_members): each a plain file, or one of the two folders, by name
  # (folders without their trailing /), each once.
  if ! listing=$(archive_members "${ARCHIVE_COPY}"); then
    fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} is not a tar archive as a release writes one, of plain files and folders in POSIX ustar headers alone: $(tail -n 1 <<<"${listing}" | sed 's/^problem //'). ${release_archive_sentence}"
  fi
  while read -r kind member; do
    case "${kind} ${member}" in
      " "|"file oci-layout"|"file index.json"|"file manifest.json"|"folder blobs"|"folder blobs/sha256") ;;
      "file blobs"|"file blobs/sha256")
        fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} holds ${member} as a file: in an image's offline archive it is a folder. ${release_archive_sentence}" ;;
      "file "*)
        [[ "${member}" =~ ${blob_pattern} ]] \
          || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} holds '${member}', which is not part of an image's offline archive (oci-layout, index.json, manifest.json and blobs/sha256/<hex>): ${release_archive}" ;;
      *)
        fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} holds the folder '${member}', and the only folders in an image's offline archive are blobs and blobs/sha256: ${release_archive}" ;;
    esac
  done <<<"${listing}"
  members=$(cut -d ' ' -f 2- <<<"${listing}")
  member=$(sort <<<"${members}" | uniq -d | head -n 1)
  [[ -z "${member}" ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} holds '${member}' more than once, and Docker would read the last: ${release_archive}"
  for member in oci-layout index.json manifest.json; do
    grep -qxF "${member}" <<<"${members}" \
      || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} has no ${member}: a release's offline archive has oci-layout and index.json, which Docker's containerd image store reads, and manifest.json, which the classic store reads. ${release_archive_sentence}"
  done
  grep -qxF "blobs/sha256/${IMAGE_DIGEST#sha256:}" <<<"${members}" \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} does not hold the image ${IMAGE_DIGEST} that IMAGE_REF names: ${release_archive}"

  # Named members only, into a folder of their own beside the copy: nothing else the archive holds is written anywhere.
  folder="${ARCHIVE_DIR}/members"
  (umask 077 && mkdir "${folder}") || fail "Could not make a folder to check IMAGE_ARCHIVE in. Nothing was changed."
  tar -xf "${ARCHIVE_COPY}" -C "${folder}" oci-layout index.json manifest.json "blobs/sha256/${IMAGE_DIGEST#sha256:}" 2>/dev/null \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} could not be read (tar -x): ${release_archive}"
  for member in oci-layout index.json manifest.json "blobs/sha256/${IMAGE_DIGEST#sha256:}"; do
    [[ -f "${folder}/${member}" && ! -L "${folder}/${member}" ]] \
      || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its ${member} is not a file: ${release_archive}"
  done
  check_json_once "${folder}/oci-layout" oci-layout
  check_json_once "${folder}/index.json" index.json
  check_json_once "${folder}/manifest.json" manifest.json
  check_json_once "${folder}/blobs/sha256/${IMAGE_DIGEST#sha256:}" "index ${IMAGE_DIGEST}"

  # index.json: the index IMAGE_REF names, alone and under no other name, in an OCI image layout.
  named=$(archive_json image "${folder}/index.json" 2>/dev/null) || named=""
  if [[ "${named}" != "${IMAGE_DIGEST}" \
      || "$(sha256_hex < "${folder}/blobs/sha256/${IMAGE_DIGEST#sha256:}")" != "${IMAGE_DIGEST#sha256:}" ]]; then
    fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} names the image '${named:-none, or more than one}', and IMAGE_REF names ${IMAGE_DIGEST}: ${release_archive}"
  fi
  names=$(archive_json index "${folder}/index.json" 2>/dev/null) || names=""
  if [[ "$(head -n 1 <<<"${names}")" == "image ${IMAGE_DIGEST}" ]]; then
    names=$(sed 1d <<<"${names}")
  else
    names="?"
  fi
  case "${names}" in
    "") ;;
    "name io.containerd.image.name ${image_name}"|"name io.containerd.image.name ${image_name}"$'\n'"name org.opencontainers.image.ref.name ${image_tag}")
      [[ -n "${image_name}" ]] || names="?" ;;
    *) names="?" ;;
  esac
  [[ "${names}" != "?" ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its index.json says more than the image, its type, size and digest, or names it other than ${image_name:-not at all} (Docker's containerd image store would load it under that name): ${release_archive}"
  [[ "$(archive_json layout "${folder}/oci-layout" 2>/dev/null)" == "1.0.0" ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its oci-layout is not an OCI image layout 1.0.0: ${release_archive}"

  # The index's manifest for BUILD_PLATFORM, its config and its layers.
  manifest=$(archive_json platform "${folder}/blobs/sha256/${IMAGE_DIGEST#sha256:}" "${BUILD_PLATFORM}" 2>/dev/null) || manifest=""
  if ! [[ "${manifest}" =~ ${digest_pattern} ]] \
      || ! tar -xf "${ARCHIVE_COPY}" -C "${folder}" "blobs/sha256/${manifest#sha256:}" 2>/dev/null \
      || [[ ! -f "${folder}/blobs/sha256/${manifest#sha256:}" || -L "${folder}/blobs/sha256/${manifest#sha256:}" ]] \
      || [[ "$(sha256_hex < "${folder}/blobs/sha256/${manifest#sha256:}")" != "${manifest#sha256:}" ]]; then
    fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} does not hold the image ${IMAGE_DIGEST} has for ${BUILD_PLATFORM}: give the release's archive for ${BUILD_PLATFORM} (<image>-<version>-linux-${BUILD_PLATFORM#linux/}.oci.tar). Nothing was changed."
  fi
  check_json_once "${folder}/blobs/sha256/${manifest#sha256:}" "${BUILD_PLATFORM} manifest ${manifest}"
  config=$(archive_json config "${folder}/blobs/sha256/${manifest#sha256:}" 2>/dev/null) || config=""
  [[ "${config}" =~ ${digest_pattern} ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: the ${BUILD_PLATFORM} manifest ${manifest} names no config. Nothing was changed."
  layers=$(archive_json layers "${folder}/blobs/sha256/${manifest#sha256:}" 2>/dev/null) || layers="?"
  while IFS= read -r layer; do
    [[ -z "${layer}" || "${layer}" =~ ${digest_pattern} ]] \
      || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: the ${BUILD_PLATFORM} manifest ${manifest} does not list its layers by digest. Nothing was changed."
  done <<<"${layers}"

  # The blobs are those, and only those.
  expected=$({ printf '%s\n' "${IMAGE_DIGEST}" "${manifest}" "${config}"; [[ -z "${layers}" ]] || printf '%s\n' "${layers}"; } \
    | sed 's|^sha256:|blobs/sha256/|' | sort -u)
  blobs=$(grep -E "${blob_pattern}" <<<"${members}" | sort) || blobs=""
  [[ "${blobs}" == "${expected}" ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE} holds other blobs than the index, the ${BUILD_PLATFORM} manifest, its config and its layers: ${release_archive}"

  # manifest.json: that one image, its config and its layers in order, under IMAGE_REF's own name or none.
  expected="config blobs/sha256/${config#sha256:}"
  while IFS= read -r layer; do
    [[ -z "${layer}" ]] || expected+=$'\n'"layer blobs/sha256/${layer#sha256:}"
  done <<<"${layers}"
  docker=$(archive_json docker "${folder}/manifest.json" 2>/dev/null) || docker=""
  [[ "${docker}" == "${expected}" || ( -n "${image_name}" && "${docker}" == "${expected}"$'\n'"tag ${image_name}" ) ]] \
    || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its manifest.json is not the one image for ${BUILD_PLATFORM}, with its config and layers, tagged ${image_name:-nothing} or not at all (Docker's classic image store would load and tag what it says): ${release_archive}"

  # Every blob is the one its digest names: the config and the layers too, so nothing in the archive is left for the
  # loaders to check.
  if ! tar -xf "${ARCHIVE_COPY}" -C "${folder}" "blobs/sha256/${config#sha256:}" 2>/dev/null \
      || [[ ! -f "${folder}/blobs/sha256/${config#sha256:}" || -L "${folder}/blobs/sha256/${config#sha256:}" ]] \
      || [[ "$(sha256_hex < "${folder}/blobs/sha256/${config#sha256:}")" != "${config#sha256:}" ]]; then
    fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its config ${config} for ${BUILD_PLATFORM} is not the blob its digest names: ${release_archive}"
  fi
  check_json_once "${folder}/blobs/sha256/${config#sha256:}" "config ${config}"
  while IFS= read -r layer; do
    [[ -z "${layer}" || "$(tar -xOf "${ARCHIVE_COPY}" "blobs/sha256/${layer#sha256:}" 2>/dev/null | sha256_hex)" == "${layer#sha256:}" ]] \
      || fail "IMAGE_ARCHIVE ${IMAGE_ARCHIVE}: its layer ${layer} for ${BUILD_PLATFORM} is not the blob its digest names: ${release_archive}"
  done <<<"${layers}"
  remove_archive_members "${folder}"
  ARCHIVE_MANIFEST=${manifest}
  ARCHIVE_CONFIG=${config}
  echo "[load] ${IMAGE_ARCHIVE} holds ${IMAGE_DIGEST}, with its image for ${BUILD_PLATFORM} (manifest ${ARCHIVE_MANIFEST}, config ${ARCHIVE_CONFIG}), and nothing else"
}

# check_loaded_image: once IMAGE_ARCHIVE is loaded, Docker must hold an image whose ID is the index's, the platform's
# manifest's or its config's (check_image_archive), for BUILD_PLATFORM. The pre-flight and the controller then run that
# image by its ID, never pulling, and the controller carries the release-image label naming IMAGE_REF.
check_loaded_image() {
  local id loaded="" platform labels version revision
  for id in "${IMAGE_DIGEST}" "${ARCHIVE_MANIFEST}" "${ARCHIVE_CONFIG}"; do
    if [[ "$(dockerc image inspect --format '{{.Id}}' "${id}" 2>/dev/null)" == "${id}" ]]; then
      loaded=${id}
      break
    fi
  done
  [[ -n "${loaded}" ]] \
    || fail "Docker loaded ${IMAGE_ARCHIVE}, and holds no image with its IDs: ${IMAGE_DIGEST} (the index), ${ARCHIVE_MANIFEST} (the ${BUILD_PLATFORM} manifest) or ${ARCHIVE_CONFIG} (its config). The running controller was not touched."
  inspect_platform_args=()
  if dockerc image inspect --help 2>/dev/null | grep -q -- '--platform'; then
    inspect_platform_args=(--platform "${BUILD_PLATFORM}")
  fi
  platform=$(dockerc image inspect ${inspect_platform_args[@]+"${inspect_platform_args[@]}"} --format '{{.Os}}/{{.Architecture}}' "${loaded}") \
    || platform=""
  [[ "${platform}" != "/" ]] || platform=""
  [[ "${platform}" == "${BUILD_PLATFORM}" ]] \
    || fail "The image ${loaded} from ${IMAGE_ARCHIVE} is not for ${BUILD_PLATFORM} (Docker reports '${platform:-no image for that platform}'). The running controller was not touched."
  # Each label as JSON on a line of its own; one that is not plain is not shown.
  labels=$(dockerc image inspect ${inspect_platform_args[@]+"${inspect_platform_args[@]}"} \
      --format '{{with .Config}}{{json (index .Labels "org.opencontainers.image.version")}}{{println}}{{json (index .Labels "org.opencontainers.image.revision")}}{{end}}' \
      "${loaded}" 2>/dev/null) || labels=""
  version="" revision=""
  { IFS= read -r version; IFS= read -r revision; } <<<"${labels}" || true
  if [[ "${version}" =~ ${PLAIN_JSON_STRING} ]]; then version=${version:1:${#version}-2}; else version=""; fi
  if [[ "${revision}" =~ ${PLAIN_JSON_STRING} ]]; then revision=${revision:1:${#revision}-2}; else revision=""; fi
  echo "[load] ${IMAGE_DIGEST} for ${platform}, image ${loaded}: version ${version:-unknown}${revision:+, commit ${revision:0:12}}"
  DEPLOY_IMAGE=${loaded}
  image_platform_args=(--platform "${BUILD_PLATFORM}" --pull never --label "${RELEASE_IMAGE_LABEL}=${IMAGE_REF}")
}

# ---------------------------------------------------------------------------------------------------------------------
# Restore. Everything here runs without errexit, and every step checks its own result.

# The ID of the container this deploy created: from `docker run -d`, or from the cidfile docker writes on creation
# (a run that failed after creating the container, or was interrupted, prints no ID).
new_container_id() {
  if [[ -n "${NEW_CONTAINER_ID}" ]]; then
    echo "${NEW_CONTAINER_ID}"
  elif [[ -n "${NEW_CIDFILE}" && -s "${NEW_CIDFILE}" ]]; then
    tr -d '[:space:]' < "${NEW_CIDFILE}"
  fi
}

# rename_back <id> <name>: sets RESTORE_OUTCOME and returns 1 on failure.
rename_back() {
  log_err "[rollback] Renaming container ${1:0:12} to $2"
  if ! dockerc rename "$1" "$2" >/dev/null; then
    RESTORE_OUTCOME="Could not rename container ${1:0:12} to $2. The roof controller is NOT running; check the containers."
    return 1
  fi
}

# Makes sure no container this run started can run next to the original controller: a deploy stops and removes the
# container it created (by ID; a container it did not create is never touched), a rollback stops the version it was
# bringing back. Sets RESTORE_OUTCOME and returns 1 when that cannot be confirmed.
retire_switch_container() {
  local id
  if [[ "${ROLLBACK}" == "true" ]]; then
    id=${ROLLBACK_TARGET_ID}
    if lookup_container "id=${id}" && [[ "${CSTATE}" == "running" ]]; then
      log_err "[rollback] Stopping the version being rolled back to (SIGTERM first, so it stops the roof)"
      dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${id}" >/dev/null
    fi
    if ! lookup_container "id=${id}" || [[ "${CSTATE}" == "running" ]]; then
      RESTORE_OUTCOME="The version being rolled back to (container ${id:0:12}) could not be stopped, so the original controller was NOT restarted: two controllers must never drive the HAT. Stop it by hand, then check the containers."
      return 1
    fi
    return 0
  fi

  id=$(new_container_id)
  [[ -n "${id}" ]] || return 0
  log_err "[rollback] Last log lines of the new controller:"
  dockerc logs --tail 60 "${id}" >&2 2>&1 || true
  log_err "[rollback] Stopping and removing the new controller (SIGTERM first, so it stops the roof)"
  dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${id}" >/dev/null 2>&1
  dockerc rm -f -v "${id}" >/dev/null 2>&1
  if ! lookup_container "id=${id}" || [[ "${CSTATE}" != "missing" ]]; then
    RESTORE_OUTCOME="The new controller (container ${id:0:12}) could not be removed, so the previous controller was NOT restarted: two controllers must never drive the HAT. Remove it (docker rm -f ${id:0:12}), then restore ${PREVIOUS_CONTAINER_NAME} by hand."
    return 1
  fi
}

# Identity schema compatibility is checked against the exact immutable image of the preserved
# container, never its mutable tag, container labels or version string. Store bytes stay private.
parse_identity_schema() {
  # Preserve numeric token types and duplicate properties: jq/object-model readers
  # can otherwise accept a document the controller's integer serializer rejects.
  python3 -c 'import json,sys
class ObjectPairs(list): pass
def reject_constant(_): raise ValueError("non-JSON constant")
d=json.load(sys.stdin, object_pairs_hook=ObjectPairs, parse_constant=reject_constant)
if not isinstance(d,ObjectPairs): sys.exit(1)
values=[value for key,value in d if key == "schemaVersion"]
if len(values)>1: sys.exit(1)
v=values[0] if values else 1
if type(v) is not int or v not in (1,2,3): sys.exit(1)
print(v)'
}

# Sets SCHEMA_FAILURE; a missing expected store, unsupported metadata or failed read is unknown.
identity_schema_compatible() {
  local target=$1 image capability env key secret secrets_dir store_path="" schema schema_file
  SCHEMA_FAILURE="identity schema compatibility could not be established"
  if ! command -v python3 >/dev/null 2>&1; then
    SCHEMA_FAILURE="strict identity schema recovery checks require Python 3"
    return 1
  fi
  image=$(dockerc inspect --format '{{.Image}}' "${target}" 2>/dev/null) || return 1
  [[ "${image}" =~ ^sha256:[0-9a-f]{64}$ ]] || return 1
  # As JSON: exactly 1, 2 or 3 (or none), with nothing before or after (review ROOF-151-R3 F8).
  capability=$(dockerc image inspect --format '{{json (index .Config.Labels "io.hvo.roof.identity-schema")}}' "${image}" 2>/dev/null) || return 1
  case "${capability}" in
    '""'|null) capability=1 ;;
    '"1"'|'"2"'|'"3"') capability=${capability:1:1} ;;
    *) SCHEMA_FAILURE="the target image declares an unsupported identity schema"; return 1 ;;
  esac
  env=$(dockerc inspect --format '{{json .Config.Env}}' "${target}" 2>/dev/null) || return 1
  # JSON preserves embedded newlines and delimiters. Line-formatted inspect output
  # could otherwise truncate a path and inspect a different file from the process.
  if ! store_path=$(printf '%s' "${env}" | python3 -c 'import json,sys
entries=json.load(sys.stdin)
if not isinstance(entries,list): sys.exit(1)
paths=[]
for entry in entries:
    if not isinstance(entry,str): sys.exit(1)
    key,sep,value=entry.partition("=")
    if sep and key.replace("__",":").lower()=="roofcontrollersecurity:identity:storepath":
        paths.append(value)
if not paths or any(value!=paths[0] for value in paths): sys.exit(1)
if "\n" in paths[0] or "\r" in paths[0]: sys.exit(1)
print(paths[0],end="")' 2>/dev/null); then
    SCHEMA_FAILURE="the preserved container has no unambiguous explicit identity store path"
    return 1
  fi
  make_work_dir || return 1
  # AddKeyPerFile has higher precedence than environment. Refuse an override rather
  # than guess which store is live; the normal deployed secrets directory is required.
  secrets_dir="${WORK_DIR}/identity-schema-secrets"
  rm -rf "${secrets_dir}" || return 1
  if ! dockerc cp "${target}:/run/secrets" "${secrets_dir}" >/dev/null 2>&1 \
      || [[ ! -d "${secrets_dir}" || -L "${secrets_dir}" ]]; then
    SCHEMA_FAILURE="the preserved secrets configuration could not be checked"
    return 1
  fi
  for secret in "${secrets_dir}"/* "${secrets_dir}"/.[!.]* "${secrets_dir}"/..?*; do
    [[ -e "${secret}" || -L "${secret}" ]] || continue
    key=${secret##*/}
    key=${key//__/:}
    key=$(printf '%s' "${key}" | LC_ALL=C tr '[:upper:]' '[:lower:]')
    if [[ "${key}" == roofcontrollersecurity:identity:storepath ]]; then
      rm -rf "${secrets_dir}"
      SCHEMA_FAILURE="an identity store path secret override requires explicit forward repair"
      return 1
    fi
  done
  rm -rf "${secrets_dir}" || return 1
  if [[ -z "${store_path}" ]]; then
    # An explicitly empty environment override means the profile is in-memory.
    schema=1
  else
    [[ "${store_path}" == /* && "${store_path}" != *$'\n'* && "${store_path}" != *$'\r'* ]] || return 1
    make_work_dir || return 1
    schema_file="${WORK_DIR}/identity-schema-check.json"
    rm -f "${schema_file}" || return 1
    if ! dockerc cp "${target}:${store_path}" "${schema_file}" >/dev/null 2>&1 \
        || [[ ! -f "${schema_file}" || -L "${schema_file}" ]]; then
      SCHEMA_FAILURE="the preserved identity store could not be read"
      return 1
    fi
    if ! schema=$(parse_identity_schema <"${schema_file}" 2>/dev/null); then
      rm -f "${schema_file}"
      SCHEMA_FAILURE="the preserved identity schema is malformed or unsupported"
      return 1
    fi
    rm -f "${schema_file}"
  fi
  if (( schema > capability )); then
    SCHEMA_FAILURE="identity schema ${schema} is not supported by the preserved image (maximum ${capability})"
    return 1
  fi
}

# Prevent a rejected restore from coming back on a daemon restart. Never claim success
# when Docker cannot confirm both the stopped state and restart policy.
quarantine_controller() {
  local target=$1 policy_ok=true policy
  dockerc update --restart no "${target}" >/dev/null 2>&1 || policy_ok=false
  if ! lookup_container "id=${target}"; then return 1; fi
  if [[ "${CSTATE}" == running ]]; then
    log_err "[rollback] The original controller is still running; stopping it fully before checking identity compatibility."
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${target}" >/dev/null 2>&1 || true
  fi
  lookup_container "id=${target}" && [[ "${CSTATE}" == stopped ]] || return 1
  policy=$(dockerc inspect --format '{{.HostConfig.RestartPolicy.Name}}' "${target}" 2>/dev/null) || return 1
  [[ "${policy_ok}" == true && "${policy}" == no ]]
}

prepare_original_restore() {
  [[ -n "${ORIGINAL_ID}" ]] || return 0
  if ! quarantine_controller "${ORIGINAL_ID}"; then
    RESTORE_OUTCOME="Automatic recovery refused: Docker could not confirm the original controller is stopped with restart=no. It may still be running or stopped. It was NOT started; check its state and repair forward. Identity data and backups were not changed."
    return 1
  fi
  if ! identity_schema_compatible "${ORIGINAL_ID}"; then
    RESTORE_OUTCOME="Automatic recovery refused: ${SCHEMA_FAILURE}. The original controller is kept stopped with restart=no. Identity data and backups were not changed; repair forward with a compatible release."
    return 1
  fi
}

# Puts the rollback target back as <name>-previous (restart policy no) and the original controller back as <name>
# (restart policy unless-stopped), wherever the switch got to. Sets RESTORE_OUTCOME and returns 1 on failure.
put_names_back() {
  local original_name="" target_name=""
  if [[ -n "${ORIGINAL_ID}" ]]; then
    if ! lookup_container "id=${ORIGINAL_ID}"; then
      RESTORE_OUTCOME="Docker could not report the state of the original controller (container ${ORIGINAL_ID:0:12}), so it was NOT restored; it may be running or stopped. Check the containers."
      return 1
    fi
    if [[ "${CSTATE}" == "missing" ]]; then
      RESTORE_OUTCOME="The original controller (container ${ORIGINAL_ID:0:12}) could not be found. The roof controller is NOT running; check the containers."
      return 1
    fi
    original_name=${CNAME}
  fi
  if [[ -n "${ROLLBACK_TARGET_ID}" ]]; then
    if ! lookup_container "id=${ROLLBACK_TARGET_ID}"; then
      RESTORE_OUTCOME="Docker could not report the state of the version being rolled back to (container ${ROLLBACK_TARGET_ID:0:12}); the swap was NOT undone. Check the containers."
      return 1
    fi
    if [[ "${CSTATE}" == "missing" ]]; then
      RESTORE_OUTCOME="The version being rolled back to (container ${ROLLBACK_TARGET_ID:0:12}) could not be found; the swap was NOT undone. Check the containers."
      return 1
    fi
    target_name=${CNAME}
  fi

  if [[ -n "${target_name}" && "${target_name}" != "${PREVIOUS_CONTAINER_NAME}" ]]; then
    if [[ "${original_name}" == "${PREVIOUS_CONTAINER_NAME}" ]]; then
      rename_back "${ORIGINAL_ID}" "${SWAP_CONTAINER_NAME}" || return 1
      original_name=${SWAP_CONTAINER_NAME}
    fi
    rename_back "${ROLLBACK_TARGET_ID}" "${PREVIOUS_CONTAINER_NAME}" || return 1
  fi
  if [[ -n "${ROLLBACK_TARGET_ID}" ]]; then
    dockerc update --restart no "${ROLLBACK_TARGET_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not set restart policy no on ${PREVIOUS_CONTAINER_NAME}"
  fi

  if [[ -n "${original_name}" && "${original_name}" != "${CONTAINER_NAME}" ]]; then
    if ! lookup_container "$(name_filter "${CONTAINER_NAME}")"; then
      RESTORE_OUTCOME="Docker could not report whether ${CONTAINER_NAME} is free; the original controller was NOT restored (it is kept, stopped, as ${original_name})."
      return 1
    fi
    if [[ "${CSTATE}" != "missing" ]]; then
      RESTORE_OUTCOME="${CONTAINER_NAME} is taken by a container this run did not create (${CID:0:12}); it was left alone, and the original controller was NOT restored: it is kept, stopped, as ${original_name}. Check the containers."
      return 1
    fi
    rename_back "${ORIGINAL_ID}" "${CONTAINER_NAME}" || return 1
  fi
  if [[ -n "${original_name}" ]]; then
    dockerc update --restart unless-stopped "${ORIGINAL_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not set restart policy unless-stopped on ${CONTAINER_NAME}"
  fi
}

# Starts the original controller again if it was running before the switch. Sets RESTORE_OUTCOME.
restart_original() {
  local mode=$1
  case "${mode}" in
    none)
      if [[ "${ROLLBACK}" == "true" ]]; then
        RESTORE_OUTCOME="Undone: ${PREVIOUS_CONTAINER_NAME} is back, stopped. There was no ${CONTAINER_NAME} before the rollback, so no controller is running."
      else
        RESTORE_OUTCOME="There was no previous controller to restore: the roof controller is NOT running and the roof cannot be controlled remotely until this is fixed."
      fi
      return
      ;;
    stopped)
      if [[ "${ROLLBACK}" == "true" ]]; then
        RESTORE_OUTCOME="Undone: the original controller is back as ${CONTAINER_NAME}, stopped as it was before; ${PREVIOUS_CONTAINER_NAME} is unchanged."
      else
        RESTORE_OUTCOME="Rolled back: the previous controller is restored as ${CONTAINER_NAME}, stopped as it was before the deploy."
      fi
      return
      ;;
  esac

  # Still running means its stop never began, or the daemon is still finishing one whose client was cut off (a lost
  # connection to the Pi). Starting it would then do nothing and it would exit moments later, so stop it fully first;
  # the roof was verified stopped before the switch began.
  if lookup_container "id=${ORIGINAL_ID}" && [[ "${CSTATE}" == "running" ]]; then
    log_err "[rollback] The original controller is still running; stopping it fully (SIGTERM first) before starting it again"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${ORIGINAL_ID}" >/dev/null \
      || log_err "[rollback] WARNING: could not stop the original controller; starting it anyway"
  fi
  log_err "[rollback] Starting the original controller as ${CONTAINER_NAME}"
  if ! dockerc start "${ORIGINAL_ID}" >/dev/null; then
    RESTORE_OUTCOME="The previous controller could not be restarted. The roof controller is NOT running."
    return
  fi
  if wait_ready "${CONTAINER_NAME}"; then
    if [[ "${ROLLBACK}" == "true" ]]; then
      RESTORE_OUTCOME="Undone: the original controller is running and ready as ${CONTAINER_NAME}; ${PREVIOUS_CONTAINER_NAME} is unchanged."
    else
      RESTORE_OUTCOME="Rolled back: the previous controller is running and ready."
    fi
    return
  fi
  dockerc logs --tail 60 "${CONTAINER_NAME}" >&2 2>&1 || true
  RESTORE_OUTCOME="The previous controller was restarted but did not become ready. The roof cannot be controlled remotely until this is fixed."
}

# Undoes an unfinished switch (RESTORE_MODE) and reports the outcome on the script's own stdout and stderr; the caller
# exits non-zero. Further signals are ignored, docker runs in its own session (dockerc) and failed writes do not
# matter, so a second Ctrl-C or a lost terminal cannot cut the restore short.
restore_original() {
  local reason=$1 mode=${RESTORE_MODE} what=Deployment
  set +e
  trap '' HUP INT TERM PIPE
  exec 1>&8 2>&9
  RESTORING=true
  [[ "${ROLLBACK}" != "true" ]] || what=Rollback
  log_err "[rollback] ${reason}"

  RESTORE_OUTCOME=""
  if retire_switch_container && prepare_original_restore && put_names_back; then
    restart_original "${mode}"
  fi

  show_containers >&2
  log_err "[deploy] ERROR: ${what} failed (${reason}). ${RESTORE_OUTCOME}"
  RESTORE_MODE=""
}

# Restores the original controller and exits non-zero. For failures during the switch.
abort_switch() {
  restore_original "$1"
  exit 1
}

on_exit() {
  local status=$?
  # This runs on every exit, including a signal or a lost terminal: no errexit, no further interruptions, and the
  # script's own output back in place of the interrupted call's redirections.
  set +e
  trap '' HUP INT TERM PIPE
  exec 1>&8 2>&9
  if [[ -n "${RESTORE_MODE}" ]]; then
    if [[ "${RESTORING}" == "true" ]]; then
      log_err "[deploy] ERROR: the restore did not finish; check the containers (docker ps -a --filter name=${CONTAINER_NAME})."
    else
      restore_original "the script stopped unexpectedly (exit ${status}) while switching controllers"
    fi
    [[ "${status}" != "0" ]] || status=1
  fi
  # A rollback that changed the limits of the version it was bringing back and did not start it puts them back.
  if [[ -n "${LIMITS_RESTORE_ID}" ]]; then
    restore_rollback_limits
    [[ "${status}" != "0" ]] || status=1
  fi
  remove_work_dir
  remove_archive_dir
  exit "${status}"
}
# The script's stdout and stderr, kept on fds 8 and 9. A signal's trap runs inside the call it interrupted, with that
# call's redirections still in place: a Ctrl-C during the readiness probe (dockerc exec ... >/dev/null 2>&1) would
# otherwise send the whole restore report to /dev/null.
exec 8>&1 9>&2
trap on_exit EXIT
# Signals end the script through the EXIT trap (which restores during the switch) with the usual 128+N status.
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 141' PIPE
trap 'exit 143' TERM

# ---------------------------------------------------------------------------------------------------------------------

# --verify-remote: the checks of step 3 from this machine, and nothing else. It never calls Docker, so it works for a
# controller that Docker Compose runs, and for one this machine has no Docker context for.
if [[ "${VERIFY_REMOTE}" == "true" ]]; then
  [[ "${SKIP_REMOTE_CHECK}" != "true" ]] \
    || fail "--verify-remote is the check that SKIP_REMOTE_CHECK=true skips; unset SKIP_REMOTE_CHECK."
  resolve_operator_key
  echo "[verify] Checking ${REMOTE_BASE_URL} from this machine (no Docker calls). Expected HAT: ${HAT_SUMMARY}"
  verify_remote true || fail "${FAILURE}."
  echo "[done] Verified at ${REMOTE_BASE_URL} from this machine: authenticated Status (hatMode ${VERIFIED_HAT_MODE}) and a verified Stop."
  exit 0
fi

# The offline archive, before any Docker call: one that does not hold the release's image changes nothing.
[[ -z "${IMAGE_ARCHIVE}" ]] || check_image_archive

# The work folder, before any Docker call too: the new controller's cidfile (by which a restore removes it), the
# identity check's copies and a built image go there.
make_work_dir || fail "${FAILURE}. Nothing was changed."

# Checked as the switch runs docker: in its own session, with no terminal to prompt on. A context that asks for a
# password or a host key confirmation would otherwise pass here and fail at the old controller's stop.
if ! context_errors=$("${OWN_SESSION[@]}" docker --context "${DOCKER_CONTEXT}" info </dev/null 2>&1 >/dev/null); then
  [[ -z "${context_errors}" ]] || log_err "${context_errors}"
  fail "Docker context '${DOCKER_CONTEXT}' is not available without a terminal. Configure it first (e.g. docker context use ${DOCKER_CONTEXT}); it must connect without prompting (for an ssh:// context: an SSH key or ssh-agent, and a known host key). Nothing was changed."
fi

resolve_operator_key
read_container_states
echo "[deploy] Existing container ${CONTAINER_NAME}: ${STATE}; ${PREVIOUS_CONTAINER_NAME}: ${PREVIOUS_STATE}"
# The limits a deploy or a rollback gives, with any not set kept from the running controller; --stop gives none.
[[ "${STOP}" == "true" ]] || resolve_limits
if [[ -n "${HAT_EMULATOR_ENDPOINT}" && "${ROLLBACK}" != "true" && "${STOP}" != "true" ]]; then
  log_err "[deploy] WARNING: HAT emulator mode: the new controller will use the HAT emulator at ${HAT_EMULATOR_ENDPOINT} (ALLOW_EMULATED_HAT=true), not the physical HAT. It does not operate the roof."
fi

if [[ "${DRY_RUN}" == "true" ]]; then
  if [[ "${STATE}" == "running" ]]; then
    response=$(container_api GET Status) || fail "Could not read Status inside ${CONTAINER_NAME}."
    http_status=$(tail -n 1 <<<"${response}")
    echo "[dry-run] GET Status inside ${CONTAINER_NAME} -> HTTP ${http_status}"
    if [[ "${http_status}" == "200" ]]; then
      parsed=$(sed '$d' <<<"${response}" | parse_status) || fail "Cannot parse Status (install jq or python3)."
      IFS=$'\t' read -r state mask motion hat_mode _ <<<"${parsed}"
      echo "[dry-run] relayRegisterState=${state} relayRegisterMask=${mask} commandedMotion=${motion} hatMode=${hat_mode}"
    fi
    if [[ "${SKIP_REMOTE_CHECK}" != "true" ]]; then
      if response=$(remote_api GET Status); then
        echo "[dry-run] GET Status at ${REMOTE_BASE_URL} from this machine -> HTTP $(tail -n 1 <<<"${response}")"
      else
        echo "[dry-run] GET Status at ${REMOTE_BASE_URL} from this machine failed (expected if the current controller uses another URL)."
      fi
    fi
  fi
  if [[ "${ROLLBACK}" == "true" ]]; then
    echo "[dry-run] Would request a verified Stop, swap ${CONTAINER_NAME} with ${PREVIOUS_CONTAINER_NAME} and verify it at ${REMOTE_BASE_URL}."
  else
    if [[ -n "${IMAGE_ARCHIVE}" ]]; then
      image_step="load ${IMAGE_ARCHIVE} (${IMAGE_DIGEST} for ${BUILD_PLATFORM}) into Docker context '${DOCKER_CONTEXT}', pulling nothing, and check the image's ID and platform"
    elif [[ -n "${IMAGE_REF}" ]]; then
      image_step="pull ${IMAGE_REF} for ${BUILD_PLATFORM} into Docker context '${DOCKER_CONTEXT}' and check its digest and platform"
    else
      image_step="build ${IMAGE_TAG} for ${BUILD_PLATFORM}"
    fi
    echo "[dry-run] Would ${image_step}, run the pre-flight check on the Pi, request a verified Stop, stop ${CONTAINER_NAME} (-t ${STOP_TIMEOUT_SECONDS}) and keep it as ${PREVIOUS_CONTAINER_NAME}, start the new container and verify it (ready within ${READY_TIMEOUT_SECONDS}s, then Status and Stop at ${REMOTE_BASE_URL}), rolling back on failure."
    echo "[dry-run] HAT: ${HAT_SUMMARY}"
  fi
  echo "[dry-run] The container: a read-only root filesystem, only the capabilities CHOWN, KILL, SETGID and SETUID, no-new-privileges, and ${LIMITS_SUMMARY}"
  echo "[dry-run] Secrets dir on Pi: ${SECRETS_DIR}; identity dir: ${IDENTITY_DIR:-<none, kept in memory>}; HTTPS cert dir: ${HTTPS_CERT_DIR:-<none, insecure HTTP>}"
  echo "[dry-run] Settings dir on Pi: ${CONFIG_DIR:-<none, kept in memory>}; managed secrets dir: ${MANAGED_SECRETS_DIR:-<none, kept in memory>}"
  # Last, as the deploy would check it first: a Docker that cannot enforce the limits fails the dry run as it would the
  # deploy, and a rollback's dry run warns as the rollback would.
  check_docker_limits dry-run
  exit 0
fi

# The --verify-remote and dry-run blocks above and the --stop and rollback blocks below always exit. A top-level command that bash abandons (an
# expansion error does that without tripping set -e) must never fall through into a swap, a build or a deploy.
[[ "${DRY_RUN}" != "true" ]] || fail "internal error: the --dry-run path did not finish; nothing was changed."
[[ "${VERIFY_REMOTE}" != "true" ]] || fail "internal error: the --verify-remote path did not finish; nothing was changed."

refuse_running_previous

# --stop: the verified stop of step 2, then the controller is stopped gracefully and kept. Nothing else is changed.
if [[ "${STOP}" == "true" ]]; then
  case "${STATE}" in
    missing)
      echo "[done] There is no ${CONTAINER_NAME} to stop. Nothing was changed."
      exit 0
      ;;
    stopped)
      echo "[done] ${CONTAINER_NAME} is already stopped. Nothing was changed."
      exit 0
      ;;
  esac
  stop_roof_before_replacing
  echo "[stop] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
  dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null \
    || fail "Could not stop ${CONTAINER_NAME} (see above). Check it: docker ps -a --filter name=${CONTAINER_NAME}"
  echo "[done] Stopped ${CONTAINER_NAME} after the roof's stop. It is kept: docker start ${CONTAINER_NAME} runs it again."
  exit 0
fi

[[ "${STOP}" != "true" ]] || fail "internal error: the --stop path did not finish; nothing was built or deployed."

# A deploy and a rollback both give the controller the limits: Docker must be able to enforce them, before the build,
# the pull or a Stop. A host that cannot is refused, with no override; a rollback goes ahead with those it can, and warns.
if [[ "${ROLLBACK}" == "true" ]]; then
  check_docker_limits rollback
else
  check_docker_limits deploy
fi

if [[ "${ROLLBACK}" == "true" ]]; then
  lookup_container "$(name_filter "${SWAP_CONTAINER_NAME}")" \
    || fail "Could not read the state of ${SWAP_CONTAINER_NAME} from Docker. Nothing was changed; check the Docker context and retry."
  if [[ "${CSTATE}" != "missing" ]]; then
    fail "${SWAP_CONTAINER_NAME} exists: an earlier --rollback stopped halfway and could not be undone. Nothing was changed. Find out which version it is (docker ps -a --filter name=${CONTAINER_NAME}), rename it to whichever of ${CONTAINER_NAME} and ${PREVIOUS_CONTAINER_NAME} is free (docker rename ${SWAP_CONTAINER_NAME} <name>) or remove it if it is not needed, then retry."
  fi
  [[ "${PREVIOUS_STATE}" == "stopped" ]] \
    || fail "There is no stopped ${PREVIOUS_CONTAINER_NAME} to roll back to."

  # The HAT the previous version was deployed for, known before anything is stopped. The hatMode check once it runs
  # stays the backstop, for a HatEmulator setting from elsewhere (the secrets directory).
  hat_read=0
  read_previous_hat || hat_read=$?
  case "${hat_read}" in
    0) ;;
    2) fail "${PREVIOUS_CONTAINER_NAME} cannot be rolled back to: ${HAT_PROBLEM}. Nothing was changed." ;;
    *) fail "Could not read the environment of ${PREVIOUS_CONTAINER_NAME} from Docker. Nothing was changed; check the Docker context and retry." ;;
  esac
  if [[ -n "${PREVIOUS_EMULATOR}" ]]; then
    [[ "${ALLOW_EMULATED_HAT}" == "true" ]] \
      || fail "${PREVIOUS_CONTAINER_NAME} was deployed for the HAT emulator at ${PREVIOUS_EMULATOR}, not the physical HAT, and does not operate the roof. Nothing was changed. To roll back to it on a test rig, set ALLOW_EMULATED_HAT=true."
    log_err "[rollback] WARNING: ${PREVIOUS_CONTAINER_NAME} was deployed for the HAT emulator at ${PREVIOUS_EMULATOR} (ALLOW_EMULATED_HAT=true): the restored controller does not operate the roof."
  fi

  if ! identity_schema_compatible "${PREVIOUS_ID}"; then
    if ! quarantine_controller "${PREVIOUS_ID}"; then
      fail "Rollback refused: ${SCHEMA_FAILURE}. Docker could not confirm the target is stopped with restart=no; the current controller was not stopped. Identity data and backups were not changed."
    fi
    fail "Rollback refused: ${SCHEMA_FAILURE}. The target is kept stopped with restart=no; the current controller was not stopped. Identity data and backups were not changed; repair forward with a compatible release."
  fi

  # What the version brought back runs with, read before anything changes. Its privileges stay as it was deployed: Docker
  # cannot change them on a container.
  read_confinement "${PREVIOUS_ID}" \
    || fail "Could not read the privileges and limits of ${PREVIOUS_CONTAINER_NAME} from Docker. Nothing was changed; check the Docker context and retry."
  if ! check_privileges "${PREVIOUS_CONTAINER_NAME}"; then
    log_err "[rollback] WARNING: ${FAILURE}. It was deployed before this script narrowed the container's privileges, and keeps the ones it has; deploy a release again to narrow them."
  fi

  # Every check that can refuse the rollback comes before anything changes: the roof's idle check and its verified stop
  # (on a refusal, both containers are as they were, the limits included). Only then does the version brought back get
  # this run's limits, so that a rollback keeps the limits the operator chose; from the change until it has started, a
  # failure or an interrupt puts its own back (give_rollback_limits).
  if [[ "${STATE}" == "running" ]]; then
    stop_roof_before_replacing
  fi
  give_rollback_limits "${PREVIOUS_ID}" "${PREVIOUS_CONTAINER_NAME}"

  # From here until the restored version has started, a failure or an interrupt undoes the swap (restore_original).
  ORIGINAL_ID=${CURRENT_ID}
  ROLLBACK_TARGET_ID=${PREVIOUS_ID}
  if [[ "${STATE}" == "missing" ]]; then
    RESTORE_MODE=none
  else
    RESTORE_MODE=${STATE}
  fi

  if [[ "${STATE}" == "running" ]]; then
    echo "[rollback] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not stop ${CONTAINER_NAME}"
  fi

  echo "[rollback] Swapping ${CONTAINER_NAME} and ${PREVIOUS_CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${CONTAINER_NAME}" "${SWAP_CONTAINER_NAME}" \
      || abort_switch "Could not rename ${CONTAINER_NAME} to ${SWAP_CONTAINER_NAME}"
    dockerc update --restart no "${SWAP_CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not set restart policy no on ${SWAP_CONTAINER_NAME}"
  fi
  dockerc rename "${PREVIOUS_CONTAINER_NAME}" "${CONTAINER_NAME}" \
    || abort_switch "Could not rename ${PREVIOUS_CONTAINER_NAME} to ${CONTAINER_NAME}"
  if [[ "${STATE}" != "missing" ]]; then
    dockerc rename "${SWAP_CONTAINER_NAME}" "${PREVIOUS_CONTAINER_NAME}" \
      || abort_switch "Could not rename ${SWAP_CONTAINER_NAME} to ${PREVIOUS_CONTAINER_NAME}"
  fi
  dockerc update --restart unless-stopped "${CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not set restart policy unless-stopped on ${CONTAINER_NAME}"
  echo "[rollback] Starting ${CONTAINER_NAME}"
  dockerc start "${CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not start the rolled-back controller ${CONTAINER_NAME}"
  # Started: whatever the checks say, it is left running (run --rollback again to swap back), with this run's limits.
  RESTORE_MODE=""
  LIMITS_RESTORE_ID=""
  write_desired_limits rollback "${PREVIOUS_ID}"
  warn_in_container_log "${CONTAINER_NAME}"

  if ! verify_controller "${CONTAINER_NAME}"; then
    show_containers
    [[ -z "${PREVIOUS_EMULATOR}" ]] \
      || FAILURE+=" (it was deployed for the HAT emulator at ${PREVIOUS_EMULATOR}: check that the emulator is running)"
    fail "The rolled-back controller did not pass verification: ${FAILURE}. It is left running. Run --rollback again to swap back."
  fi
  show_containers
  echo "[done] Rolled back. ${CONTAINER_NAME} is verified at ${REMOTE_BASE_URL}. HAT: hatMode ${VERIFIED_HAT_MODE}. The replaced version is kept as ${PREVIOUS_CONTAINER_NAME}."
  if (( ${#UNENFORCED_WARNING[@]} > 0 )); then
    log_err "[done] WARNING: ${CONTAINER_NAME} runs without the limits Docker on ${PI_HOST} cannot enforce (${LIMITS_SUMMARY}): fix the host as the warning above says, then deploy again. An install, an upgrade or a deploy is refused until then."
  fi
  exit 0
fi

[[ "${ROLLBACK}" != "true" ]] || fail "internal error: the --rollback path did not finish; nothing was built or deployed."

make_work_dir || fail "${FAILURE}. The running controller was not touched."

if [[ -n "${IMAGE_ARCHIVE}" ]]; then
  # Offline: the copy of the release's archive checked above, never IMAGE_ARCHIVE itself, is loaded into the Pi's Docker,
  # and the image is used only if Docker gives it one of the IDs the archive's digests name, for the platform this run
  # deploys. Nothing is pulled or built. The copy goes once Docker has read it (on exit, if the load fails).
  echo "[load] Loading the checked copy of ${IMAGE_ARCHIVE} into Docker context '${DOCKER_CONTEXT}' (nothing is pulled)..."
  dockerc load -i "${ARCHIVE_COPY}" \
    || fail "Could not load ${IMAGE_ARCHIVE} into Docker context '${DOCKER_CONTEXT}' (see above). The running controller was not touched."
  remove_archive_dir || true
  check_loaded_image
elif [[ -n "${IMAGE_REF}" ]]; then
  # Pull mode: the Pi's Docker pulls the released image, and it is used only if it is the image the digest names, for
  # the platform this run deploys. Nothing is built here, so the script also runs outside a checkout.
  echo "[pull] Pulling ${IMAGE_REF} for ${BUILD_PLATFORM} into Docker context '${DOCKER_CONTEXT}'..."
  dockerc pull --platform "${BUILD_PLATFORM}" "${IMAGE_REF}" \
    || fail "Could not pull ${IMAGE_REF} for ${BUILD_PLATFORM} into Docker context '${DOCKER_CONTEXT}' (see above). The running controller was not touched."
  check_pulled_image
else
  if ! docker buildx version >/dev/null 2>&1; then
    fail "docker buildx is required but not available. Install Docker Buildx and try again."
  fi

  # The product version and commit the image carries (docs/releasing.md): a deploy from a checkout is a -dev build.
  ROOF_VERSION=$("${REPO_ROOT}/build/version.sh" --dev) || fail "could not read the product version from Directory.Build.props."
  ROOF_REVISION=$(git -C "${REPO_ROOT}" rev-parse HEAD 2>/dev/null || true)

  echo "[build] Building ${IMAGE_TAG} (${ROOF_VERSION}${ROOF_REVISION:+, commit ${ROOF_REVISION:0:12}}) for ${BUILD_PLATFORM}..."
  docker buildx build \
    --platform "${BUILD_PLATFORM}" \
    -f "${DOCKERFILE_PATH}" \
    -t "${IMAGE_TAG}" \
    --build-arg "ROOF_VERSION=${ROOF_VERSION}" \
    --build-arg "ROOF_REVISION=${ROOF_REVISION}" \
    --build-arg "ROOF_CREATED=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --load \
    "${REPO_ROOT}"

  docker save "${IMAGE_TAG}" -o "${WORK_DIR}/image.tar"

  echo "[deploy] Loading image into Docker context '${DOCKER_CONTEXT}'"
  dockerc load < "${WORK_DIR}/image.tar"
fi

# The container's privileges and limits (docs/deployment.md, "The container's privileges and limits"), for the pre-flight
# container and the controller alike. The root filesystem is read-only. What the container writes outside its mounts has
# a place of its own: the supervisor's run directory, /tmp (.NET's diagnostics sockets) and the controller's ASP.NET Core
# key ring, which protects nothing that must outlive the container, are tmpfs; the web UI's sign-in keys are on a volume
# of the container's own, so people stay signed in when the container restarts. Of the Linux capabilities only the
# supervisor's are kept: CHOWN for the web UI's files, SETUID and SETGID to start it as its own user, and KILL to signal
# it. Nothing in the container can gain privileges (a setuid program included). The limits are the ones checked above
# (limit_args: the CPU, memory and process limits); --memory-swap the same as --memory keeps the container from swapping.
# shellcheck disable=SC2054 # commas are part of --tmpfs values
hardening_args=(
  --read-only
  --tmpfs /tmp:size=64m
  --tmpfs /run/hvo-roof:size=4m,mode=0755
  --tmpfs /root/.aspnet:size=4m,mode=0700
  --mount type=volume,dst=/var/lib/hvo-roof-web
  --cap-drop ALL --cap-add CHOWN --cap-add KILL --cap-add SETGID --cap-add SETUID
  --security-opt no-new-privileges
  "${limit_args[@]}"
)

# Environment, devices and mounts of the controller container. The pre-flight check runs with exactly these.
# shellcheck disable=SC2054 # commas are part of --mount values
container_args=(
  --env "HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI}"
  --env "RoofControllerOptionsV4__IgnorePhysicalLimitSwitches=${IGNORE_PHYSICAL_LIMIT_SWITCHES}"
  --env "OTEL_SERVICE_NAME=${OTEL_SERVICE_NAME}"
  --env "OTEL_RESOURCE_ATTRIBUTES=service.instance.id=${OTEL_SERVICE_INSTANCE_ID}"
  --env "OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT}"
  --env "OTEL_EXPORTER_OTLP_PROTOCOL=${OTEL_EXPORTER_OTLP_PROTOCOL}"
  --env "OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL}"
  --mount "type=bind,src=${SECRETS_DIR},dst=/run/secrets,readonly"
)

if [[ -n "${IDENTITY_DIR}" ]]; then
  # Read-write: the controller saves the store here, and the pre-flight check makes sure it can.
  container_args+=(
    --mount "type=bind,src=${IDENTITY_DIR},dst=/var/lib/hvo-roof/identity"
    --env "RoofControllerSecurity__Identity__StorePath=/var/lib/hvo-roof/identity/identity.json"
  )
fi

if [[ -n "${CONFIG_DIR}" ]]; then
  # Read-write: the controller saves the settings changed through the API here, and the pre-flight check makes sure it can.
  container_args+=(
    --mount "type=bind,src=${CONFIG_DIR},dst=/etc/hvo-roof/config"
    --env "RoofControllerSettings__FilePath=/etc/hvo-roof/config/appsettings.Local.json"
  )
fi

if [[ "${LINUX_AUTH_ENABLED}" == true ]]; then
  container_args+=(
    --mount "type=bind,src=${LINUX_AUTH_CONTROLLER_DIR},dst=/run/hvo-roof-linux-auth/controller,readonly"
    --mount "type=bind,src=${LINUX_AUTH_KEY_FILE},dst=/etc/hvo-roof/linux-auth/assertion.key,readonly"
    --env "RoofControllerSecurity__Identity__LinuxAuth__Enabled=true"
    --env "RoofControllerSecurity__Identity__LinuxAuth__SocketPath=/run/hvo-roof-linux-auth/controller/worker.sock"
    --env "RoofControllerSecurity__Identity__LinuxAuth__AssertionKeyPath=/etc/hvo-roof/linux-auth/assertion.key"
    --env "RoofControllerSecurity__Identity__LinuxAuth__Audience=hvo-roof-controller"
  )
elif [[ "${LINUX_AUTH_ENABLED}" != false ]]; then
  fail "LINUX_AUTH_ENABLED must be true or false."
fi

if [[ -n "${MANAGED_SECRETS_DIR}" ]]; then
  container_args+=(
    --mount "type=bind,src=${MANAGED_SECRETS_DIR},dst=/var/lib/hvo-roof/settings-secrets"
    --env "RoofControllerSettings__SecretsFilePath=/var/lib/hvo-roof/settings-secrets/secrets.json"
  )
fi

if [[ -n "${HAT_EMULATOR_ENDPOINT}" ]]; then
  # The HAT emulator answers the HAT's registers. No host device or Pi file is mapped: not the HAT's I2C device, so this
  # controller cannot reach the physical HAT whatever its settings say, and not /dev/gpiomem or the thermal sensor,
  # which a test rig that is not a Pi does not have.
  container_args+=(
    --env "HatEmulator__Enabled=true"
    --env "HatEmulator__Host=${HAT_EMULATOR_HOST}"
    --env "HatEmulator__Port=${HAT_EMULATOR_PORT}"
    --env "HatEmulator__AllowOutsideDevelopment=true"
  )
else
  # Explicitly off, and not allowed outside Development. The verification then checks the hatMode the controller
  # reports, since a setting in the secrets directory would override these.
  # shellcheck disable=SC2054 # commas are part of --mount values
  container_args+=(
    --device /dev/gpiomem:/dev/gpiomem
    --mount type=bind,src=/sys/class/thermal/thermal_zone0/temp,dst=/sys/class/thermal/thermal_zone0/temp,readonly
    --device /dev/i2c-1:/dev/i2c-1
    --env "HatEmulator__Enabled=false"
    --env "HatEmulator__AllowOutsideDevelopment=false"
  )
fi

if [[ -n "${HTTPS_CERT_DIR}" ]]; then
  # Remote clients use HTTPS only; plain HTTP listens on loopback inside the container (health check, Stop calls, the
  # web UI). The web UI serves the controller's certificate (its supervisor gives it a private copy).
  publish_args=(-p "${publish_prefix}${HTTPS_HOST_PORT}:8443" -p "${publish_prefix}${WEB_HOST_PORT}:8088")
  container_args+=(
    --mount "type=bind,src=${HTTPS_CERT_DIR},dst=/https,readonly"
    --env "ASPNETCORE_URLS=http://localhost:8080;https://+:8443"
    --env "Kestrel__Certificates__Default__Path=/https/${HTTPS_CERT_FILE}"
    --env "RoofControllerSecurity__RequireHttps=true"
    --env "RoofWeb__Urls=https://+:8088"
  )
else
  publish_args=(-p "${publish_prefix}${HOST_PORT}:8080" -p "${publish_prefix}${WEB_HOST_PORT}:8088")
  container_args+=(
    --env "ASPNETCORE_URLS=http://+:8080"
    --env "RoofControllerSecurity__RequireHttps=false"
    --env "RoofWeb__Urls=http://+:8088"
  )
fi

if [[ -n "${ALLOWED_HOSTS}" ]]; then
  container_args+=(--env "AllowedHosts=${ALLOWED_HOSTS}")
fi

container_args+=(${extra_args[@]+"${extra_args[@]}"})

# Pre-flight on the Pi with the new image and the final container's configuration, before touching the running
# controller. Docker checks the devices and mount sources; --validate-deployment checks the configuration (it does
# not open the HAT). The key's SHA-256 lets it confirm this script's key is configured without passing the key.
echo "[deploy] Pre-flight: validating the new container's configuration on the Pi"
deploy_key_sha256=$(printf '%s' "${OPERATOR_KEY}" | sha256_hex)
if ! dockerc run --rm ${image_platform_args[@]+"${image_platform_args[@]}"} "${hardening_args[@]}" "${container_args[@]}" \
    --env "DeploymentCheck__DeployKeySha256=${deploy_key_sha256}" "${DEPLOY_IMAGE}" --validate-deployment; then
  fail "Pre-flight failed (see above). The running controller was not touched."
fi

# The build and the pre-flight take minutes: decide on the containers as they are now.
read_container_states
refuse_running_previous

# Confirm the recovery parser exists before stopping a controller. A later parser
# failure is still handled fail-closed by identity_schema_compatible.
if [[ "${STATE}" != missing ]] && ! command -v python3 >/dev/null 2>&1; then
  fail "Controller replacement and strict identity recovery checks require Python 3. The current controller was not stopped."
fi

# From the first change on, a failure or an interrupt restores the original controller (restore_original).
ORIGINAL_ID=${CURRENT_ID}
case "${STATE}" in
  running)
    stop_roof_before_replacing
    RESTORE_MODE=running
    echo "[deploy] Stopping ${CONTAINER_NAME} gracefully (SIGTERM, up to ${STOP_TIMEOUT_SECONDS}s)"
    dockerc stop -t "${STOP_TIMEOUT_SECONDS}" "${CONTAINER_NAME}" >/dev/null \
      || abort_switch "Could not stop ${CONTAINER_NAME}; it was not replaced"
    ;;
  stopped)
    RESTORE_MODE=stopped
    echo "[deploy] ${CONTAINER_NAME} is not running (nothing to stop). The new controller turns all relays off when it initializes."
    ;;
  missing)
    # Any existing <name>-previous is left alone.
    RESTORE_MODE=none
    echo "[deploy] No existing container."
    ;;
esac

if [[ "${STATE}" != "missing" ]]; then
  # Only now, once the old controller has stopped, is an older stopped <name>-previous removed (by ID), with its volume
  # (the web UI's sign-in keys).
  if [[ "${PREVIOUS_STATE}" == "stopped" ]]; then
    echo "[deploy] Removing the older ${PREVIOUS_CONTAINER_NAME}"
    dockerc rm -v "${PREVIOUS_ID}" >/dev/null \
      || abort_switch "Could not remove the older ${PREVIOUS_CONTAINER_NAME}; ${CONTAINER_NAME} was not replaced"
  fi
  echo "[deploy] Keeping the old controller as ${PREVIOUS_CONTAINER_NAME} (not restarted automatically)"
  dockerc rename "${CONTAINER_NAME}" "${PREVIOUS_CONTAINER_NAME}" \
    || abort_switch "Could not rename ${CONTAINER_NAME} to ${PREVIOUS_CONTAINER_NAME}; it was not replaced"
  dockerc update --restart no "${PREVIOUS_CONTAINER_NAME}" >/dev/null \
    || abort_switch "Could not set restart policy no on ${PREVIOUS_CONTAINER_NAME}"
fi

echo "[deploy] Starting the new container on ${PI_HOST}"
# Docker writes the new container's ID to the cidfile as soon as it is created, so the restore can remove it even
# when `docker run` fails after creating it (e.g. a port already in use).
NEW_CIDFILE="${WORK_DIR}/new-controller.cid"
NEW_CONTAINER_ID=$(dockerc run -d --cidfile "${NEW_CIDFILE}" --name "${CONTAINER_NAME}" --restart unless-stopped \
    --stop-timeout "${STOP_TIMEOUT_SECONDS}" --log-driver local --log-opt max-size=10m --log-opt max-file=5 \
    ${image_platform_args[@]+"${image_platform_args[@]}"} "${hardening_args[@]}" "${publish_args[@]}" "${container_args[@]}" \
    "${DEPLOY_IMAGE}") \
  || abort_switch "docker run failed for the new controller"

check_limits "${NEW_CONTAINER_ID}" "the new controller" || abort_switch "${FAILURE}"
check_privileges "the new controller" || abort_switch "${FAILURE}"
echo "[deploy] The new controller has a read-only root filesystem, only the capabilities CHOWN, KILL, SETGID and SETUID, no-new-privileges, and ${LIMITS_SUMMARY}"
verify_controller "${CONTAINER_NAME}" || abort_switch "${FAILURE}"
# The switch is complete and verified, and what is left only reports it: from here neither a signal nor a lost terminal
# ends the script with an error while the new controller runs.
trap '' HUP INT TERM PIPE
RESTORE_MODE=""
write_desired_limits deploy "${NEW_CONTAINER_ID}"

echo "[deploy] Container status" || true
show_containers
# A first deploy keeps nothing: only a replaced controller becomes <name>-previous, and an older one is left alone.
if [[ "${STATE}" != "missing" ]]; then
  KEPT="The previous version is kept as ${PREVIOUS_CONTAINER_NAME}; run with --rollback to return to it."
elif [[ "${PREVIOUS_STATE}" == "stopped" ]]; then
  KEPT="There was no ${CONTAINER_NAME} to replace. The older ${PREVIOUS_CONTAINER_NAME} is left as it was; run with --rollback to return to it."
else
  KEPT="There was no controller before this one, so there is no previous version to roll back to."
fi
echo "[done] Deployment complete and verified at ${REMOTE_BASE_URL}. HAT: ${HAT_SUMMARY}. ${KEPT}" || true
