#!/usr/bin/env bash
# Tests for src/HVO.RoofControllerV4.Installer/install.sh, as a release publishes it (build/release-assets.py writes its
# version in). It runs as `curl ... | bash` runs it, piped into bash, with a PATH of fake commands (tests/install/fakes:
# uname, sudo, curl, docker, gh, raspi-config and the rest) and a few real ones, so no network, Docker, Pi, Mac or root
# is needed; the files it reads (/etc/os-release, /proc/device-tree/model, /dev/i2c-1, /etc/hvo-roof/install.json) are
# in a folder of the test's own. INSTALL_SH_BASH names the bash it runs in (a Mac's /bin/bash is 3.2). Requires bash 3.2
# or later and python3.
# Run: tests/install/install-sh-tests.sh [test-name ...]
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPOSITORY=$(cd "${TESTS_DIR}/../.." && pwd)
REPOSITORY_SCRIPT="${REPOSITORY}/src/HVO.RoofControllerV4.Installer/install.sh"
INSTALL_SH_BASH=${INSTALL_SH_BASH:-$(command -v bash)}
VERSION="4.0.0-rc.1"
DRY_RUN_VERSION="4.0.0-dryrun.17"
COMMIT="0123456789abcdef0123456789abcdef01234567"
RELEASE_URL="https://github.com/HualapaiValley/HVO.RoofController/releases/download/v${VERSION}"
PI_DESCRIPTION="Linux on aarch64, Debian GNU/Linux 12 (bookworm), Raspberry Pi 5 Model B Rev 1.0 (linux-arm64)"
# The real commands install.sh and the fakes use; everything else on the PATH is a fake, or missing.
REAL_TOOLS="cat tr awk sed grep tail head dirname mktemp rm cp chmod mkdir ln wc"
# The fakes (tests/install/fakes/fake-command, linked under each name).
FAKES="uname sysctl sw_vers getconf ldd id sudo curl docker dpkg apt-get raspi-config timedatectl ss lsof df tar sha256sum shasum gh date"
# A Mac with Apple silicon (also: make_mac).
MAC=(FAKE_KERNEL=Darwin FAKE_MACHINE=arm64)

PASSED=0
FAILED=0
FAILURES=()
CURRENT_FAILED=false

# ---------------------------------------------------------------------------------------------------------------------
# Harness

# The release's install.sh, and a dry run's, written once for all the tests.
SUITE=$(mktemp -d)
trap 'rm -rf "${SUITE}"' EXIT
SCRIPT="${SUITE}/install.sh"
DRY_RUN_SCRIPT="${SUITE}/install-dry-run.sh"

# bake <version> <file>: install.sh as build/release-assets.py publishes it for that release.
bake() {
  python3 - "${REPOSITORY}" "$1" "$2" <<'PY'
import importlib.util, pathlib, sys
repository = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("release_assets", repository / "build" / "release-assets.py")
release_assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_assets)
pathlib.Path(sys.argv[3]).write_text(release_assets.install_script(release_assets.INSTALL_SCRIPT.read_text(), sys.argv[2]))
PY
}

# A Raspberry Pi 5 with the 64-bit Raspberry Pi OS, I2C on, Docker, sudo without a password, and the release's files.
setup() {
  WORK=$(mktemp -d)
  export FAKE_STATE_DIR="${WORK}/state"
  SCRIPT_UNDER_TEST=${SCRIPT}
  EXPECT=()
  mkdir -p "${FAKE_STATE_DIR}" "${WORK}/fakebin" "${WORK}/realbin" "${WORK}/tmp" "${WORK}/home" "${WORK}/docker" \
    "${WORK}/release" "${WORK}/root/etc" "${WORK}/root/proc/device-tree" "${WORK}/root/dev"
  : >"${FAKE_STATE_DIR}/calls.log"
  local name
  for name in ${FAKES}; do
    ln -s "${TESTS_DIR}/fakes/fake-command" "${WORK}/fakebin/${name}"
  done
  for name in ${REAL_TOOLS}; do
    ln -s "$(command -v "${name}")" "${WORK}/realbin/${name}"
  done
  ln -s "${INSTALL_SH_BASH}" "${WORK}/realbin/bash"
  printf 'PRETTY_NAME="Debian GNU/Linux 12 (bookworm)"\nNAME="Debian GNU/Linux"\nVERSION_CODENAME=bookworm\nID=debian\n' \
    >"${WORK}/root/etc/os-release"
  printf 'Raspberry Pi 5 Model B Rev 1.0\0' >"${WORK}/root/proc/device-tree/model"
  : >"${WORK}/root/dev/i2c-1"
  for name in linux-arm64 linux-x64 osx-arm64; do
    cp "${TESTS_DIR}/fakes/hvo-roof-install" "${WORK}/release/hvo-roof-install-${name}"
  done
  write_sums
}

teardown() {
  rm -rf "${WORK}"
}

# write_sums: the release's SHA256SUMS, for the installers in it.
write_sums() {
  python3 - "${WORK}/release" <<'PY'
import hashlib, pathlib, sys
folder = pathlib.Path(sys.argv[1])
lines = [f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n" for p in sorted(folder.glob("hvo-roof-install-*"))]
(folder / "SHA256SUMS").write_text("".join(lines))
PY
}

# without <command>...: those commands are not installed.
without() {
  local name
  for name in "$@"; do
    rm "${WORK}/fakebin/${name}"
  done
}

# not_a_pi: a Linux machine that is not a Raspberry Pi, with I2C off.
not_a_pi() {
  rm -f "${WORK}/root/proc/device-tree/model" "${WORK}/root/dev/i2c-1"
}

# make_mac: what a Mac has (shasum, not sha256sum; no apt, raspi-config, timedatectl or ss); run with "${MAC[@]}".
make_mac() {
  not_a_pi
  rm -f "${WORK}/root/etc/os-release"
  without sha256sum dpkg apt-get raspi-config timedatectl ss getconf ldd
}

# record <file> <roles>: an install record (or an answers file) naming the roles, separated by commas.
record() {
  local roles="" role IFS=,
  for role in $2; do
    roles="${roles:+${roles}, }\"${role}\""
  done
  mkdir -p "$(dirname "$1")"
  printf '{\n  "version": "4.0.0-rc.0",\n  "roles": [%s]\n}\n' "${roles}" >"$1"
}

# build_command [NAME=value...] [-- install.sh args...]: sets RUN_CMD to pipe SCRIPT_UNDER_TEST into bash, as
# `curl ... | bash -s -- args` does, with the test's PATH and files plus the given variables.
build_command() {
  local env_pairs=()
  while (($# > 0)) && [ "$1" != "--" ]; do
    env_pairs+=("$1")
    shift
  done
  [ "${1:-}" != "--" ] || shift

  # shellcheck disable=SC2016 # expanded by the inner shell
  RUN_CMD=(env -i
    PATH="${WORK}/fakebin:${WORK}/realbin" HOME="${WORK}/home" TMPDIR="${WORK}/tmp"
    HVO_ROOF_INSTALL_TEST_ROOT="${WORK}/root" FAKE_STATE_DIR="${FAKE_STATE_DIR}"
    FAKE_COMMAND="${TESTS_DIR}/fakes/fake-command" FAKE_BIN="${WORK}/fakebin" FAKE_PYTHON="$(command -v python3)"
    FAKE_REAL_DATE="$(command -v date)" FAKE_RELEASE_DIR="${WORK}/release" FAKE_DOCKER_ROOT="${WORK}/docker"
    FAKE_REAL_SHA256SUM="$(command -v sha256sum || true)" FAKE_REAL_SHASUM="$(command -v shasum || true)"
    TZ=MST7
    FAKE_RELEASE_VERSION="${VERSION}" FAKE_INSTALLER_VERSION="${VERSION}+${COMMIT}"
    ${env_pairs[@]+"${env_pairs[@]}"}
    "${WORK}/realbin/bash" -c 'cat "$1" | "$2" -s -- "${@:3}"' install-sh "${SCRIPT_UNDER_TEST}" "${WORK}/realbin/bash"
    "$@")
}

# Runs install.sh in a session of its own with no controlling terminal, as from cron, CI or `ssh host 'curl | bash'`:
# /dev/tty cannot be opened even when these tests run in a terminal. Sets OUTPUT (stdout and stderr) and STATUS.
install_sh() {
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

# As install_sh, in a terminal (tests/install/on-terminal), with someone at the keyboard who answers each prompt in
# EXPECT (--expect <prompt> <answer>, in order). OUTPUT is what reached the terminal, typing included.
install_sh_on_terminal() {
  build_command "$@"
  STATUS=0
  ON_TERMINAL_TIMEOUT=20 "${TESTS_DIR}/on-terminal" "${WORK}/terminal.log" ${EXPECT[@]+"${EXPECT[@]}"} \
    -- "${RUN_CMD[@]}" </dev/null || STATUS=$?
  OUTPUT=$(tr -d '\r' <"${WORK}/terminal.log")
}

fail_test() {
  CURRENT_FAILED=true
  echo "    FAIL: $*"
}

assert_status() {
  [[ "${STATUS}" == "$1" ]] || fail_test "expected exit status $1, got ${STATUS}"
}

assert_output_contains() {
  [[ "${OUTPUT}" == *"$1"* ]] || fail_test "output does not contain: $1"
}

assert_output_not_contains() {
  [[ "${OUTPUT}" != *"$1"* ]] || fail_test "output contains: $1"
}

# assert_called <text>: a fake command's call (its line in calls.log: "[root ]name args") contains the text.
assert_called() {
  grep -qF -- "$1" "${FAKE_STATE_DIR}/calls.log" || fail_test "no call contains: $1"
}

assert_not_called() {
  local call
  call=$(grep -F -- "$1" "${FAKE_STATE_DIR}/calls.log" | head -n 1)
  [ -z "${call}" ] || fail_test "a call contains '$1': ${call}"
}

# assert_installer <line>: the installer ran, and its log has the line (root=, terminal=, args=, answer= or stdin=).
assert_installer() {
  if [ ! -f "${FAKE_STATE_DIR}/installer.log" ]; then
    fail_test "the installer did not run"
  elif ! grep -qxF -- "$1" "${FAKE_STATE_DIR}/installer.log"; then
    fail_test "the installer's log has no line '$1': $(tr '\n' ' ' <"${FAKE_STATE_DIR}/installer.log")"
  fi
}

assert_installer_did_not_run() {
  [ ! -e "${FAKE_STATE_DIR}/installer.log" ] || fail_test "the installer ran: $(tr '\n' ' ' <"${FAKE_STATE_DIR}/installer.log")"
}

# assert_nothing_downloaded: no request for the release's files.
assert_nothing_downloaded() {
  assert_not_called "/releases/download/"
}

# The checks failed, and install.sh stopped before downloading anything.
assert_stopped_by_checks() {
  assert_status 1
  assert_output_contains "nothing was downloaded or installed."
  assert_nothing_downloaded
  assert_installer_did_not_run
}

# A check failed after install.sh installed Docker or turned I2C on (made, as the verdict names it), and it stopped
# before downloading anything.
assert_stopped_after() {
  assert_status 1
  assert_output_contains "failed after $1, and nothing else was downloaded or installed."
  assert_nothing_downloaded
  assert_installer_did_not_run
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

# --- The script and its command line ----------------------------------------------------------------------------------

test_the_repository_s_copy_installs_nothing() {
  SCRIPT_UNDER_TEST=${REPOSITORY_SCRIPT}
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "This is the repository's copy of install.sh, which names no release: use a release's copy."
  assert_output_contains "releases/latest/download/install.sh | bash"
  [ ! -s "${FAKE_STATE_DIR}/calls.log" ] || fail_test "it ran commands: $(head -n 1 "${FAKE_STATE_DIR}/calls.log")"
}

test_the_repository_s_copy_has_help() {
  SCRIPT_UNDER_TEST=${REPOSITORY_SCRIPT}
  install_sh -- --help

  assert_status 0
  assert_output_contains "install.sh for HVO Roof Controller (the repository's copy, which installs nothing)"
}

test_help_names_the_release_and_runs_nothing() {
  install_sh -- --help

  assert_status 0
  assert_output_contains "install.sh for HVO Roof Controller ${VERSION}"
  assert_output_contains "downloads hvo-roof-install ${VERSION} for it"
  assert_output_contains "--install-docker"
  [ ! -s "${FAKE_STATE_DIR}/calls.log" ] || fail_test "it ran commands: $(head -n 1 "${FAKE_STATE_DIR}/calls.log")"
}

test_an_option_without_its_value_is_a_usage_error() {
  install_sh -- --roles

  assert_status 2
  assert_output_contains "install.sh: --roles needs a value."
  assert_output_contains "install.sh --help lists its options."
}

test_from_a_folder_that_is_not_there_is_a_usage_error() {
  install_sh -- --from "${WORK}/no-such-folder" --roles rig

  assert_status 2
  assert_output_contains "--from ${WORK}/no-such-folder: there is no such folder."
}

test_an_answers_file_that_is_not_there_is_a_usage_error() {
  install_sh -- --answers "${WORK}/no-such-answers.json"

  assert_status 2
  assert_output_contains "--answers ${WORK}/no-such-answers.json: there is no such file, or you cannot read it."
}

# Cut short anywhere before the end of its last line's call, the download runs no command at all: inside the function
# bash finds it unfinished, and in the last line the call is not made, or is refused without its end marker. Cuts at
# sampled offsets through the script, and at every byte of its end. (Without its last argument, the call would still run
# the checks.)
test_a_download_cut_short_anywhere_runs_nothing() {
  SCRIPT_UNDER_TEST="${WORK}/cut.sh"
  build_command -- --roles rig upgrade
  OUTPUT=$(python3 - "${SCRIPT}" "${WORK}/cut.sh" "${FAKE_STATE_DIR}" "${RUN_CMD[@]}" <<'PY'
import os, subprocess, sys
source, cut, state = sys.argv[1:4]
command = sys.argv[4:]
data = open(source, "rb").read()
call = data.rindex(b"\nhvo_roof_install_sh ") + 1
complete = data.index(b'"end of install.sh"', call) + len(b'"end of install.sh"')
calls, installer = os.path.join(state, "calls.log"), os.path.join(state, "installer.log")
ran, refused = [], 0
offsets = sorted(set(range(0, call, 53)) | set(range(call - 80, len(data) + 1)))
for n in offsets:
    with open(cut, "wb") as f:
        f.write(data[:n])
    open(calls, "w").close()
    if os.path.exists(installer):
        os.remove(installer)
    result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            start_new_session=True)
    did_something = os.path.getsize(calls) > 0 or os.path.exists(installer)
    if b"the download was cut short, and nothing was done" in result.stdout:
        refused += 1
    if n < complete and did_something:
        ran.append(f"cut at byte {n} of {len(data)} (...{data[max(0, n - 30):n]!r}) ran: {open(calls).readline().strip()}")
    if n == len(data) and not os.path.exists(installer):
        ran.append(f"the whole script did not run the installer: {result.stdout.decode()[-300:]}")
print(f"{len(offsets)} cuts; {refused} refused for their missing end marker")
for line in ran:
    print(line)
PY
)
  STATUS=$?

  assert_status 0
  [[ "${OUTPUT}" != *" ran"* && "${OUTPUT}" != *"did not run"* ]] || fail_test "a download cut short ran something"
  [[ "${OUTPUT}" =~ \;\ [1-9][0-9]*\ refused ]] || fail_test "no cut was refused for its missing end marker"
}

# Its standard input is the download, so neither it nor the installer reads what follows it there.
test_nothing_reads_the_download_after_the_script() {
  {
    cat "${SCRIPT}"
    # shellcheck disable=SC2016 # expanded by the bash running the download
    printf 'echo "the line after the script ran" >>"${FAKE_STATE_DIR}/calls.log"\nyes\n'
  } >"${WORK}/longer.sh"
  SCRIPT_UNDER_TEST="${WORK}/longer.sh"
  install_sh FAKE_INSTALLER_ASKS="Go on? " -- --roles rig

  assert_status 0
  assert_installer "terminal=no"
  assert_installer "answer=(none)"
  assert_not_called "the line after the script ran"
}

# --- The platform -----------------------------------------------------------------------------------------------------

test_freebsd_is_refused() {
  install_sh FAKE_KERNEL=FreeBSD FAKE_MACHINE=amd64 -- --roles rig

  assert_status 1
  assert_output_contains "This machine runs FreeBSD: the roof controller's programs run on Linux (64-bit ARM or x86-64) and on Macs with Apple silicon."
  assert_installer_did_not_run
}

test_32_bit_arm_is_refused() {
  install_sh FAKE_MACHINE=armv7l -- --roles controller,kiosk

  assert_status 1
  assert_output_contains "This machine runs 32-bit ARM (armv7l), and the roof controller's programs are 64-bit: install the 64-bit Raspberry Pi OS"
  assert_installer_did_not_run
}

test_a_32_bit_system_on_a_64_bit_kernel_is_refused() {
  install_sh FAKE_LONG_BIT=32 -- --roles controller,kiosk

  assert_status 1
  assert_output_contains "This machine has a 64-bit kernel and a 32-bit system"
  assert_installer_did_not_run
}

test_another_processor_is_refused() {
  install_sh FAKE_MACHINE=riscv64 -- --roles rig

  assert_status 1
  assert_output_contains "This machine's processor is riscv64: the roof controller's programs run on 64-bit ARM (aarch64) and x86-64."
}

test_musl_is_refused() {
  install_sh FAKE_LIBC=musl -- --roles rig

  assert_status 1
  assert_output_contains "This Linux has musl (as Alpine does), and the roof controller's programs need glibc"
}

test_an_intel_mac_is_refused() {
  make_mac
  install_sh FAKE_KERNEL=Darwin FAKE_MACHINE=x86_64 FAKE_ARM64=0 -- --roles mac-app,cli

  assert_status 1
  assert_output_contains "This Mac has an Intel processor: hvo-roof, the Mac app and the installer run on Macs with Apple silicon only."
  assert_installer_did_not_run
}

test_an_old_mac_that_cannot_say_is_refused() {
  make_mac
  install_sh FAKE_KERNEL=Darwin FAKE_MACHINE=x86_64 FAKE_ARM64=missing -- --roles mac-app,cli

  assert_status 1
  assert_output_contains "This Mac has an Intel processor"
}

# Under Rosetta uname says x86_64; the hardware says Apple silicon.
test_a_mac_under_rosetta_gets_the_apple_silicon_installer() {
  make_mac
  install_sh FAKE_KERNEL=Darwin FAKE_MACHINE=x86_64 FAKE_ARM64=1 -- --roles cli

  assert_status 0
  assert_output_contains "ok    macOS 15.0 on Apple silicon (osx-arm64)"
  assert_called "${RELEASE_URL}/hvo-roof-install-osx-arm64"
  assert_installer "root=no"
}

test_x86_64_linux_gets_the_x64_installer() {
  not_a_pi
  install_sh FAKE_MACHINE=x86_64 -- --roles rig

  assert_status 0
  assert_output_contains "ok    Linux on x86_64, Debian GNU/Linux 12 (bookworm) (linux-x64)"
  assert_output_contains "Downloading hvo-roof-install ${VERSION} for linux-x64"
  assert_called "${RELEASE_URL}/hvo-roof-install-linux-x64"
  assert_installer "root=yes"
}

# --- What the machine is for ------------------------------------------------------------------------------------------

test_the_controller_on_a_pi_is_downloaded_checked_and_started_as_root() {
  install_sh -- --roles controller,kiosk

  assert_status 0
  assert_output_contains "HVO Roof Controller ${VERSION}: install.sh"
  assert_output_contains "ok    ${PI_DESCRIPTION}"
  assert_output_contains "ok    for controller, kiosk (from --roles), installed as root"
  assert_output_contains "ok    curl, tar and sha256sum"
  assert_output_contains "ok    sudo lets you run commands as root"
  assert_output_contains "ok    github.com answers"
  assert_output_contains "ok    the clock is within "
  assert_output_contains "ok    the clock is synchronised"
  assert_output_contains "ok    ghcr.io answers (HTTP 401)"
  assert_output_contains "ok    Docker 27.3.1, with Compose 2.29.7"
  assert_output_contains "ok    I2C is on (/dev/i2c-1)"
  assert_output_contains "ok    ports 8443, 8088, 8080 are free"
  assert_output_contains "Downloading hvo-roof-install ${VERSION} for linux-arm64"
  assert_output_contains "ok    hvo-roof-install-linux-arm64's SHA-256 is the one SHA256SUMS lists"
  assert_output_contains "ok    its attestation says HualapaiValley/HVO.RoofController's release workflow built it"
  assert_output_contains "Starting hvo-roof-install ${VERSION} as root (sudo)"
  assert_output_contains "hvo-roof-install ran"
  assert_output_not_contains "FAIL"
  assert_called "root docker version --format {{.Server.Version}}"
  assert_called "df -Pk ${WORK}/docker"
  assert_called "gh attestation verify ${WORK}/tmp/hvo-roof-install."
  assert_called "--repo HualapaiValley/HVO.RoofController --signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml --source-ref refs/tags/v${VERSION}"
  assert_installer "root=yes"
  assert_installer "terminal=no"
  assert_installer "args="
  assert_installer "stdin=0"
  [ -z "$(ls -A "${WORK}/tmp")" ] || fail_test "the download was left in TMPDIR: $(ls -A "${WORK}/tmp")"
}

test_the_download_is_removed_from_a_tmpdir_with_a_quote_in_its_name() {
  local tmpdir="${WORK}/it's here"
  mkdir "${tmpdir}"
  install_sh TMPDIR="${tmpdir}" -- --roles rig

  assert_status 0
  assert_output_contains "hvo-roof-install ran"
  [ -z "$(ls -A "${tmpdir}")" ] || fail_test "the download was left in TMPDIR: $(ls -A "${tmpdir}")"
}

test_the_controller_on_a_machine_that_is_not_a_pi_fails_its_check() {
  not_a_pi
  install_sh -- --roles controller

  assert_stopped_by_checks
  assert_output_contains "FAIL  The controller drives the real HAT, on the observatory's Raspberry Pi, and this is not a Pi"
  assert_output_contains "A check failed, and nothing was downloaded or installed."
}

test_roles_installed_as_root_and_as_the_person_are_installed_in_separate_runs() {
  install_sh -- --roles controller,cli

  assert_status 2
  assert_output_contains "controller (installed as root) and cli (installed as you) are installed in separate runs."
  assert_installer_did_not_run
}

test_an_unknown_role_is_a_usage_error() {
  install_sh -- --roles rig,server

  assert_status 2
  assert_output_contains "'server', from --roles, is not a role: the roles are controller, rig, kiosk, cli and mac-app."
}

test_the_person_s_roles_run_as_the_person() {
  install_sh -- --roles cli

  assert_status 0
  assert_output_contains "ok    for cli (from --roles), installed as you"
  assert_output_contains "for your programs"
  assert_output_contains "Starting hvo-roof-install ${VERSION}"
  assert_output_not_contains "as root (sudo)"
  assert_not_called "sudo"
  assert_not_called "docker"
  assert_not_called "ghcr.io"
  assert_called "df -Pk ${WORK}/home"
  assert_installer "root=no"
}

test_the_person_s_roles_are_refused_as_root() {
  install_sh FAKE_UID=0 -- --roles cli

  assert_status 1
  assert_output_contains "cli is yours, and never installed as root: run this as yourself, without sudo."
  assert_installer_did_not_run
}

test_the_machine_s_roles_as_root_need_no_sudo() {
  install_sh FAKE_UID=0 -- --roles controller,kiosk

  assert_status 0
  assert_output_contains "Starting hvo-roof-install ${VERSION}"
  assert_output_not_contains "as root (sudo)"
  assert_output_not_contains "sudo lets you"
  assert_not_called "sudo"
  assert_called "root docker version"
  assert_installer "root=yes"
}

test_the_roles_come_from_the_answers_file() {
  record "${WORK}/answers.json" rig
  install_sh -- --answers "${WORK}/answers.json"

  assert_status 0
  assert_output_contains "ok    for rig (from the answers file), installed as root"
  assert_installer "args=[--answers][${WORK}/answers.json]"
  assert_installer "root=yes"
}

test_an_answers_file_that_names_no_roles_is_a_usage_error() {
  printf '{"controller": {}}\n' >"${WORK}/answers.json"
  install_sh -- --answers="${WORK}/answers.json"

  assert_status 2
  assert_output_contains "--answers ${WORK}/answers.json names no roles."
}

test_a_command_takes_the_roles_from_the_machine_s_record() {
  record "${WORK}/root/etc/hvo-roof/install.json" controller,kiosk
  install_sh -- upgrade

  assert_status 0
  assert_output_contains "ok    for controller, kiosk (from what is installed), installed as root"
  assert_output_contains "ok    Docker 27.3.1"
  assert_output_not_contains "ports"
  assert_not_called "ss -Hltn"
  assert_installer "root=yes"
  assert_installer "args=[upgrade]"
}

test_a_command_takes_the_roles_from_the_person_s_record() {
  record "${WORK}/home/.config/hvo-roof/install.json" cli
  install_sh -- upgrade

  assert_status 0
  assert_output_contains "ok    for cli (from what is installed), installed as you"
  assert_not_called "sudo"
  assert_installer "root=no"
}

test_without_roles_and_without_a_terminal_it_asks_for_them() {
  install_sh

  assert_status 2
  assert_output_contains "There is no terminal to ask what this machine is for on: give --roles, or the installer's --answers FILE."
  assert_installer_did_not_run
}

test_on_a_pi_it_asks_what_the_machine_is_for() {
  EXPECT=(--expect "Choose a number: " 2)
  install_sh_on_terminal

  assert_status 0
  assert_output_contains "1) The observatory's roof controller, which drives the real HAT, and its touchscreen kiosk (installed as root)"
  assert_output_contains "3) hvo-roof, the command-line client, for you"
  assert_output_contains "ok    for rig (from your choice), installed as root"
  assert_installer "terminal=yes"
  assert_installer "root=yes"
}

test_on_a_mac_it_asks_what_the_machine_is_for() {
  make_mac
  EXPECT=(--expect "Choose a number: " 1)
  install_sh_on_terminal "${MAC[@]}"

  assert_status 0
  assert_output_contains "1) The Mac app and hvo-roof, for you"
  assert_output_contains "ok    for mac-app, cli (from your choice), installed as you"
  assert_installer "root=no"
}

test_an_answer_that_is_not_a_choice_is_a_usage_error() {
  not_a_pi
  EXPECT=(--expect "Choose a number: " 3)
  install_sh_on_terminal

  assert_status 2
  assert_output_contains "2) hvo-roof, the command-line client, for you"
  assert_output_contains "'3' is not one of the choices."
  assert_installer_did_not_run
}

# A plan changes nothing, and the installer makes one without root: no sudo, and nothing it needs is checked.
test_a_plan_runs_without_sudo() {
  install_sh FAKE_SUDO=denied -- --roles controller,kiosk --plan

  assert_status 0
  assert_not_called "sudo"
  assert_not_called "docker"
  assert_installer "root=no"
  assert_installer "args=[--plan]"
}

test_a_restore_runs_as_root_and_needs_docker() {
  : >"${WORK}/backup.tar.gz"
  install_sh -- restore "${WORK}/backup.tar.gz"

  assert_status 0
  assert_output_contains "ok    Docker 27.3.1"
  assert_installer "root=yes"
  assert_installer "args=[restore][${WORK}/backup.tar.gz]"
}

# --- sudo -------------------------------------------------------------------------------------------------------------

test_sudo_asks_for_the_password_on_the_terminal() {
  EXPECT=(--expect "password for pi: " "the right password")
  install_sh_on_terminal FAKE_SUDO=password -- --roles controller,kiosk

  assert_status 0
  assert_output_contains "The installer runs as root: sudo asks for your password."
  assert_output_contains "ok    sudo lets you run commands as root"
  assert_installer "root=yes"
}

test_a_wrong_sudo_password_installs_nothing() {
  EXPECT=(--expect "password for pi: " "not the password")
  install_sh_on_terminal FAKE_SUDO=password -- --roles controller,kiosk

  assert_status 1
  assert_output_contains "sudo did not let you run commands as root, and the installer runs as root: nothing was installed."
  assert_installer_did_not_run
}

test_sudo_with_a_password_and_no_terminal_is_a_usage_error() {
  install_sh FAKE_SUDO=password -- --roles controller,kiosk

  assert_status 2
  assert_output_contains "The installer runs as root, and there is no terminal for sudo to ask for your password on"
  assert_installer_did_not_run
}

test_without_sudo_the_machine_s_roles_are_refused() {
  without sudo
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "The installer runs as root, and sudo is not installed: run this as root, or install sudo"
  assert_installer_did_not_run
}

# --- The commands it needs --------------------------------------------------------------------------------------------

test_missing_commands_are_named() {
  without tar sha256sum shasum
  install_sh -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  These commands are missing: tar sha256sum. Install them (sudo apt-get install tar coreutils), then run this again."
}

test_shasum_stands_in_for_sha256sum() {
  without sha256sum
  install_sh -- --roles rig

  assert_status 0
  assert_output_contains "ok    curl, tar and shasum"
  assert_called "shasum -a 256"
  assert_installer "root=yes"
}

test_without_curl_nothing_more_is_checked() {
  without curl
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "FAIL  These commands are missing: curl."
  assert_output_contains "Without curl nothing more can be checked: install it, then run this again."
  assert_not_called "docker"
}

# --- Free space -------------------------------------------------------------------------------------------------------

test_too_little_space_for_the_download_fails() {
  install_sh FAKE_DF_LOW_PATH="${WORK}/tmp" -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  ${WORK}/tmp has 1 MB free, and the installer's download (TMPDIR) need 200 MB: free some space, then run this again."
}

test_too_little_space_for_docker_s_images_fails() {
  install_sh FAKE_DF_LOW_PATH="${WORK}/docker" FAKE_DF_LOW_FREE=1500000 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  ${WORK}/docker has 1.4 GB free, and Docker's images need 2.0 GB"
}

test_too_little_space_for_the_person_s_programs_fails() {
  install_sh FAKE_DF_LOW_PATH="${WORK}/home" -- --roles cli

  assert_stopped_by_checks
  assert_output_contains "FAIL  ${WORK}/home has 1 MB free, and your programs need 200 MB"
}

test_space_is_checked_on_the_nearest_folder_that_is_there() {
  install_sh FAKE_DOCKER_ROOT="${WORK}/docker/not/made/yet" FAKE_DF_LOW_PATH="${WORK}/docker" -- --roles rig

  assert_stopped_by_checks
  assert_called "df -Pk ${WORK}/docker"
  assert_output_contains "FAIL  ${WORK}/docker has 1 MB free, and Docker's images need 2.0 GB"
}

test_df_without_an_answer_is_only_noted() {
  install_sh FAKE_DF=broken -- --roles rig

  assert_status 0
  assert_output_contains "note  df could not tell the free space on ${WORK}/tmp: the installer's download (TMPDIR) need 200 MB"
  assert_installer "root=yes"
}

# --- The network and the clock ----------------------------------------------------------------------------------------

test_github_not_answering_fails() {
  install_sh FAKE_GITHUB_EXIT=6 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  github.com did not answer: its name did not resolve (DNS). The installer and the release come from there."
}

test_a_tls_failure_says_what_does_that() {
  install_sh FAKE_GITHUB_EXIT=60 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "github.com did not answer: TLS failed (curl exit 60): a clock that is wrong, or a proxy that intercepts HTTPS, does this."
}

test_the_release_from_a_folder_needs_no_github() {
  install_sh FAKE_GITHUB_EXIT=7 -- --from "${WORK}/release" --roles rig

  assert_status 0
  assert_output_contains "note  github.com did not answer (the connection was refused, or the network is unreachable): the release's files come from ${WORK}/release"
  assert_output_contains "Taking hvo-roof-install ${VERSION} for linux-arm64 from ${WORK}/release"
  assert_nothing_downloaded
  assert_installer "args=[--release][${WORK}/release]"
}

# The machine's time is given in UTC (the tests run in MST7): the hour it says is the UTC hour before or after the run.
test_a_clock_an_hour_out_fails() {
  local before after
  before=$(LC_ALL=C date -u '+%a, %d %b %Y %H:')
  install_sh FAKE_CLOCK_SKEW=3600 -- --roles rig
  after=$(LC_ALL=C date -u '+%a, %d %b %Y %H:')

  assert_stopped_by_checks
  assert_output_contains "FAIL  This machine's clock is 60 minutes out"
  [[ ${OUTPUT} == *"(it says ${before}"* || ${OUTPUT} == *"(it says ${after}"* ]] \
    || fail_test "the machine's time is not UTC's (${before} or ${after}): ${OUTPUT##*it says }"
  assert_output_contains "set it, and synchronise it (sudo timedatectl set-ntp true), then run this again."
}

# 3585 s is 59.75 minutes: rounded, 60.
test_a_clock_s_minutes_are_rounded() {
  install_sh FAKE_CLOCK_SKEW=-3585 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  This machine's clock is 60 minutes out"
}

# Just past the limit, the difference is in seconds: "5 minutes out" would read as within it.
test_a_clock_just_past_the_limit_says_seconds() {
  install_sh FAKE_CLOCK_SKEW=310 -- --roles rig

  assert_stopped_by_checks
  [[ ${OUTPUT} =~ "FAIL  This machine's clock is "(309|310)" seconds out (it says" ]] \
    || fail_test "the clock's difference is not in seconds: ${OUTPUT##*clock is }"
}

test_a_clock_two_minutes_out_passes() {
  install_sh FAKE_CLOCK_SKEW=-120 -- --roles rig

  assert_status 0
  assert_output_contains "ok    the clock is within 1"
  assert_output_contains " s of github.com's"
}

test_a_mac_reads_github_s_date_with_its_own_date() {
  make_mac
  install_sh "${MAC[@]}" FAKE_CLOCK_SKEW=-3600 -- --roles cli

  assert_stopped_by_checks
  assert_called "date -j -u -f %a, %d %b %Y %H:%M:%S GMT"
  assert_output_contains "FAIL  This machine's clock is 60 minutes out"
  assert_output_contains "set it, and synchronise it, then run this again."
}

test_a_proxy_s_date_is_not_github_s() {
  install_sh FAKE_PROXY_DATE="Mon, 01 Jan 2024 00:00:00 GMT" -- --roles rig

  assert_status 0
  assert_output_contains "ok    the clock is within "
}

test_github_s_date_is_found_in_headers_of_any_length() {
  install_sh FAKE_GITHUB_HEADER_KIB=512 -- --roles rig

  assert_status 0
  assert_output_contains "ok    the clock is within "
}

test_no_date_from_github_is_only_noted() {
  install_sh FAKE_GITHUB_DATE=no -- --roles rig

  assert_status 0
  assert_output_contains "note  github.com sent no date to compare this machine's clock with"
}

test_a_clock_that_is_not_synchronised_is_only_noted() {
  install_sh FAKE_NTP=no -- --roles rig

  assert_status 0
  assert_output_contains "note  the clock is not synchronised: turn synchronisation on (sudo timedatectl set-ntp true)"
}

test_ghcr_not_answering_fails_when_docker_pulls_from_it() {
  install_sh FAKE_GHCR_EXIT=7 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  ghcr.io did not answer: the connection was refused, or the network is unreachable. Docker pulls the controller's images from there."
}

# --- Docker -----------------------------------------------------------------------------------------------------------

test_docker_is_installed_from_its_apt_repository_with_install_docker() {
  without docker
  install_sh -- --roles controller,kiosk --install-docker

  assert_status 0
  assert_output_contains "Installing Docker from https://download.docker.com/linux/debian bookworm (arm64)"
  assert_called "root apt-get update -q"
  assert_called "sudo -- install -m 0755 -d /etc/apt/keyrings"
  assert_called "root curl -fsSL --proto =https --tlsv1.2 -o /etc/apt/keyrings/docker.asc https://download.docker.com/linux/debian/gpg"
  assert_called "sudo -- chmod a+r /etc/apt/keyrings/docker.asc"
  assert_called "sudo -- tee /etc/apt/sources.list.d/docker.list"
  assert_called "root apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin"
  local sources
  sources=$(cat "${FAKE_STATE_DIR}/tee-docker.list" 2>/dev/null)
  [ "${sources}" = "deb [arch=arm64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian bookworm stable" ] \
    || fail_test "docker.list is: ${sources}"
  assert_output_contains "ok    Docker 27.3.1, with Compose 2.29.7"
  assert_installer "root=yes"
}

test_docker_is_not_installed_when_another_check_failed() {
  without docker
  install_sh FAKE_GITHUB_EXIT=6 -- --roles rig --install-docker

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker is not installed, and rig needs it: it is installed only once every other check passes, so put right what failed, then run this again (or install Docker Engine 20.10 or later yourself: https://docs.docker.com/engine/install/)."
  assert_not_called "apt-get"
  assert_not_called "tee"
}

# Every check runs before Docker is installed: I2C's, here, which comes after Docker's.
test_docker_is_not_installed_when_i2c_would_fail() {
  without docker raspi-config
  rm "${WORK}/root/dev/i2c-1"
  install_sh -- --roles controller,kiosk --install-docker

  assert_stopped_by_checks
  assert_output_contains "FAIL  I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it: turn it on (sudo raspi-config nonint do_i2c 0), then run this again."
  assert_output_contains "FAIL  Docker is not installed, and controller and kiosk need it: it is installed only once every other check passes"
  assert_not_called "apt-get"
}

# Both questions come before either change: no to I2C, and Docker is not installed either.
test_docker_is_not_installed_when_i2c_is_refused() {
  without docker
  rm "${WORK}/root/dev/i2c-1"
  EXPECT=(--expect "Install it from Docker's apt repository? [y/N] " y --expect "Turn it on with raspi-config? [y/N] " n)
  install_sh_on_terminal -- --roles controller,kiosk

  assert_stopped_by_checks
  assert_output_contains "FAIL  I2C is off: turn it on (sudo raspi-config nonint do_i2c 0, or run this with --enable-i2c), then run this again."
  assert_output_contains "note  Docker was not installed: it is installed only once every other check passes, and one failed"
  assert_not_called "apt-get"
  assert_not_called "raspi-config"
}

test_docker_and_i2c_are_both_asked_about_before_either_is_made() {
  without docker
  rm "${WORK}/root/dev/i2c-1"
  EXPECT=(--expect "Install it from Docker's apt repository? [y/N] " y --expect "Turn it on with raspi-config? [y/N] " y)
  install_sh_on_terminal -- --roles controller,kiosk

  assert_status 0
  [[ ${OUTPUT%%Installing Docker from*} == *"Turn it on with raspi-config? [y/N] "* ]] \
    || fail_test "Docker was installed before I2C was asked about"
  assert_output_contains "ok    Docker 27.3.1, with Compose 2.29.7"
  assert_output_contains "ok    I2C is on (/dev/i2c-1): raspi-config turned it on"
  assert_installer "root=yes"
}

test_too_little_space_for_docker_s_images_is_found_before_it_is_installed() {
  without docker
  install_sh FAKE_DF_LOW_PATH="${WORK}/root" FAKE_DF_LOW_FREE=1500000 -- --roles rig --install-docker

  assert_stopped_by_checks
  assert_called "df -Pk ${WORK}/root"
  assert_output_contains "FAIL  ${WORK}/root has 1.4 GB free, and Docker's images need 2.0 GB: free some space, then run this again."
  assert_output_contains "FAIL  Docker is not installed, and rig needs it: it is installed only once every other check passes"
  assert_not_called "apt-get"
}

# A check that can only fail after the change, the engine's, names the change.
test_a_check_that_fails_after_docker_is_installed_names_it() {
  without docker
  install_sh FAKE_DOCKER_ENGINE=down -- --roles rig --install-docker

  assert_stopped_after "Docker was installed"
  assert_called "root apt-get install -y -q docker-ce"
  assert_output_contains "FAIL  Docker is installed, and its engine did not answer"
  assert_output_contains "install.sh: A check failed after Docker was installed, and nothing else was downloaded or installed."
}

test_docker_is_installed_when_the_person_says_yes() {
  without docker
  EXPECT=(--expect "Install it from Docker's apt repository? [y/N] " y)
  install_sh_on_terminal -- --roles rig

  assert_status 0
  assert_output_contains "Docker is not installed, and rig needs it."
  assert_called "root apt-get install -y -q docker-ce"
  assert_installer "root=yes"
}

test_docker_is_not_installed_when_the_person_says_no() {
  without docker
  EXPECT=(--expect "Install it from Docker's apt repository? [y/N] " n)
  install_sh_on_terminal -- --roles controller,kiosk

  assert_stopped_by_checks
  assert_output_contains "Docker is not installed, and controller and kiosk need it."
  assert_output_contains "FAIL  Docker is not installed: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/, or run this with --install-docker), then run this again."
  assert_not_called "apt-get"
}

test_docker_is_not_installed_without_a_terminal_to_ask_on() {
  without docker
  install_sh -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker is not installed: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/, or run this with --install-docker)"
  assert_not_called "apt-get"
}

test_check_installs_no_docker() {
  without docker
  install_sh -- --check --install-docker -- restore "${WORK}/backup.tar.gz"

  assert_status 1
  assert_output_contains "FAIL  Docker is not installed, and a restore needs it: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/, or run this with --install-docker), then run this again."
  assert_not_called "apt-get"
}

test_docker_is_not_installed_on_a_linux_without_its_apt_repository() {
  without docker
  printf 'PRETTY_NAME="Fedora Linux 40"\nID=fedora\nVERSION_ID=40\n' >"${WORK}/root/etc/os-release"
  install_sh -- --roles rig --install-docker

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker is not installed, and rig needs it: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/), then run this again."
  assert_not_called "apt-get"
}

test_docker_that_cannot_be_installed_fails() {
  without docker
  install_sh FAKE_APT=broken -- --roles rig --install-docker

  assert_stopped_after "an attempt to install Docker"
  assert_output_contains "FAIL  Docker could not be installed: install it yourself (https://docs.docker.com/engine/install/), then run this again."
}

test_docker_older_than_20_10_fails() {
  install_sh FAKE_DOCKER_VERSION=19.03.15 -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker 19.03.15 is too old, and the installer needs Docker Engine 20.10 or later"
}

test_docker_20_10_is_new_enough() {
  install_sh FAKE_DOCKER_VERSION=20.10.24 -- --roles rig

  assert_status 0
  assert_output_contains "ok    Docker 20.10.24, with Compose 2.29.7"
}

test_docker_whose_engine_is_down_fails() {
  install_sh FAKE_DOCKER_ENGINE=down -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker is installed, and its engine did not answer (Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?): start it (sudo systemctl enable --now docker), then run this again."
}

test_a_docker_version_it_cannot_read_fails() {
  install_sh FAKE_DOCKER_VERSION=dev -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker's engine says its version is 'dev', which this script cannot read"
}

test_docker_without_compose_is_only_noted() {
  install_sh FAKE_COMPOSE=missing -- --roles rig

  assert_status 0
  assert_output_contains "ok    Docker 27.3.1"
  assert_output_contains "note  Docker Compose v2 is not installed"
}

test_a_rig_on_a_mac_uses_docker_desktop_as_the_person() {
  make_mac
  install_sh "${MAC[@]}" -- --roles rig

  assert_status 0
  assert_output_contains "ok    for rig (from --roles), installed as you"
  assert_output_contains "ok    Docker 27.3.1, with Compose 2.29.7"
  assert_not_called "sudo"
  assert_not_called "docker info"
  assert_installer "root=no"
}

test_a_rig_on_a_mac_without_docker_points_at_docker_desktop() {
  make_mac
  without docker
  install_sh "${MAC[@]}" -- --roles rig

  assert_stopped_by_checks
  assert_output_contains "FAIL  Docker is not installed, and a test rig runs in Docker Desktop: install it (https://docs.docker.com/desktop/setup/install/mac-install/)"
}

# --- I2C --------------------------------------------------------------------------------------------------------------

test_i2c_is_turned_on_with_enable_i2c() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh -- --roles controller,kiosk --enable-i2c

  assert_status 0
  assert_called "root raspi-config nonint do_i2c 0"
  assert_output_contains "ok    I2C is on (/dev/i2c-1): raspi-config turned it on"
  assert_installer "root=yes"
}

test_i2c_is_not_turned_on_when_another_check_failed() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh FAKE_GHCR_EXIT=7 -- --roles controller,kiosk --enable-i2c

  assert_stopped_by_checks
  assert_output_contains "FAIL  I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it: it is turned on only once every other check passes, so put right what failed, then run this again (or turn it on yourself: sudo raspi-config nonint do_i2c 0)."
  assert_not_called "raspi-config"
}

test_i2c_is_turned_on_when_the_person_says_yes() {
  rm "${WORK}/root/dev/i2c-1"
  EXPECT=(--expect "Turn it on with raspi-config? [y/N] " yes)
  install_sh_on_terminal -- --roles controller,kiosk

  assert_status 0
  assert_output_contains "ok    I2C is on (/dev/i2c-1): raspi-config turned it on"
}

test_i2c_that_needs_a_restart_fails() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh FAKE_I2C=reboot -- --roles controller,kiosk --enable-i2c

  assert_stopped_after "I2C was turned on"
  assert_output_contains "FAIL  raspi-config turned I2C on from the next start: restart this Pi (sudo reboot), then run this again."
}

test_a_restart_after_docker_and_i2c_names_both() {
  without docker
  rm "${WORK}/root/dev/i2c-1"
  install_sh FAKE_I2C=reboot -- --roles controller,kiosk --install-docker --enable-i2c

  assert_stopped_after "Docker was installed and I2C was turned on"
}

# Agreed to, I2C is still not turned on once Docker's install has failed.
test_i2c_is_not_turned_on_when_docker_could_not_be_installed() {
  without docker
  rm "${WORK}/root/dev/i2c-1"
  install_sh FAKE_APT=broken -- --roles controller,kiosk --install-docker --enable-i2c

  assert_stopped_after "an attempt to install Docker"
  assert_output_contains "note  I2C was not turned on: it is turned on only once every other check passes, and one failed"
  assert_output_contains "install.sh: A check failed after an attempt to install Docker"
  assert_not_called "raspi-config"
}

test_i2c_that_raspi_config_cannot_turn_on_fails() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh FAKE_I2C=fails -- --roles controller,kiosk --enable-i2c

  assert_stopped_after "an attempt to turn I2C on"
  assert_output_contains "FAIL  raspi-config could not turn I2C on"
}

test_check_turns_no_i2c_on() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh -- --roles controller,kiosk --check --enable-i2c

  assert_status 1
  assert_output_contains "FAIL  I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it: turn it on (sudo raspi-config nonint do_i2c 0, or run this with --enable-i2c), then run this again."
  assert_not_called "raspi-config"
}

test_i2c_off_without_raspi_config_fails() {
  rm "${WORK}/root/dev/i2c-1"
  without raspi-config
  install_sh -- --roles controller,kiosk --enable-i2c

  assert_stopped_by_checks
  assert_output_contains "turn it on (sudo raspi-config nonint do_i2c 0), then run this again."
}

test_a_rig_needs_no_i2c() {
  rm "${WORK}/root/dev/i2c-1"
  install_sh -- --roles rig

  assert_status 0
  assert_output_not_contains "I2C"
}

# --- The ports --------------------------------------------------------------------------------------------------------

test_a_port_in_use_is_only_noted() {
  install_sh FAKE_LISTENING=8443 -- --roles rig

  assert_status 0
  assert_output_contains "note  port 8443, for the API over HTTPS, is in use: stop what listens on it, or choose another port in the installer"
  assert_output_contains "ok    ports 8088, 8080 are free"
}

# hvo-roof installed for the person is not a rig: a first rig, root's, still has its ports checked.
test_a_person_s_record_does_not_skip_a_first_rig_s_ports() {
  record "${WORK}/home/.config/hvo-roof/install.json" cli
  install_sh FAKE_LISTENING=8443 -- --roles rig

  assert_status 0
  assert_output_contains "note  port 8443, for the API over HTTPS, is in use"
}

test_an_installed_rig_s_ports_are_not_checked() {
  record "${WORK}/root/etc/hvo-roof/install.json" rig
  install_sh FAKE_LISTENING=8443 -- --roles rig

  assert_status 0
  assert_output_not_contains "port 8443"
}

test_ports_are_found_with_lsof_without_ss() {
  without ss
  install_sh FAKE_LISTENING="8088 8080" -- --roles rig

  assert_status 0
  assert_called "lsof -nP -iTCP:8443 -sTCP:LISTEN"
  assert_output_contains "note  port 8088, for the web UI, is in use"
  assert_output_contains "note  port 8080, for the API over plain HTTP, when it is turned on, is in use"
  assert_output_contains "ok    port 8443 is free"
}

# --- The download -----------------------------------------------------------------------------------------------------

test_check_downloads_and_changes_nothing() {
  install_sh -- --roles controller,kiosk --check

  assert_status 0
  assert_output_contains "This machine is ready for hvo-roof-install ${VERSION}."
  assert_nothing_downloaded
  assert_installer_did_not_run
}

test_failures_are_counted() {
  not_a_pi
  install_sh FAKE_GHCR_EXIT=28 -- --roles controller

  assert_stopped_by_checks
  assert_output_contains "ghcr.io did not answer: it did not answer in time."
  assert_output_contains "install.sh: 2 checks failed, and nothing was downloaded or installed."
}

test_a_download_that_is_not_the_release_s_is_not_started() {
  printf 'extra\n' >>"${WORK}/release/hvo-roof-install-linux-arm64"
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "hvo-roof-install-linux-arm64's SHA-256 is "
  assert_output_contains "the download is not the release's, and nothing was installed."
  assert_installer_did_not_run
  [ -z "$(ls -A "${WORK}/tmp")" ] || fail_test "the download was left in TMPDIR: $(ls -A "${WORK}/tmp")"
}

test_a_tmpdir_with_a_backslash_in_its_name_is_hashed() {
  mkdir -p "${WORK}/back\\slash"
  install_sh TMPDIR="${WORK}/back\\slash" -- --roles rig

  assert_status 0
  assert_output_contains "ok    hvo-roof-install-linux-arm64's SHA-256 is the one SHA256SUMS lists"
  assert_installer "root=yes"
}

test_sha256sums_without_the_installer_is_refused() {
  grep -v linux-arm64 "${WORK}/release/SHA256SUMS" >"${WORK}/sums" && mv "${WORK}/sums" "${WORK}/release/SHA256SUMS"
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "SHA256SUMS does not list hvo-roof-install-linux-arm64 once, so the download cannot be checked: nothing was installed."
  assert_installer_did_not_run
}

test_sha256sums_listing_the_installer_twice_is_refused() {
  grep linux-arm64 "${WORK}/release/SHA256SUMS" >"${WORK}/twice"
  cat "${WORK}/twice" >>"${WORK}/release/SHA256SUMS"
  install_sh -- --roles rig

  assert_status 1
  assert_output_contains "SHA256SUMS does not list hvo-roof-install-linux-arm64 once"
}

test_sha256sums_in_binary_mode_is_read() {
  sed 's/  hvo-roof/ *hvo-roof/' "${WORK}/release/SHA256SUMS" >"${WORK}/sums" && mv "${WORK}/sums" "${WORK}/release/SHA256SUMS"
  install_sh -- --roles rig

  assert_status 0
  assert_installer "root=yes"
}

test_a_failed_download_says_why() {
  install_sh FAKE_DOWNLOAD_FAILS=hvo-roof-install-linux-arm64 -- --roles rig

  assert_status 1
  assert_output_contains "install.sh: hvo-roof-install-linux-arm64 could not be downloaded from ${RELEASE_URL}: it answered with an HTTP error."
  assert_installer_did_not_run
}

test_sha256sums_that_cannot_be_downloaded_says_why() {
  install_sh FAKE_DOWNLOAD_FAILS=SHA256SUMS FAKE_DOWNLOAD_EXIT=28 -- --roles rig

  assert_status 1
  assert_output_contains "install.sh: SHA256SUMS could not be downloaded from ${RELEASE_URL}: it did not answer in time."
  assert_not_called "hvo-roof-install-linux-arm64"
}

test_a_folder_without_the_installer_is_refused() {
  mkdir "${WORK}/empty"
  install_sh -- --from "${WORK}/empty" --roles rig

  assert_status 1
  assert_output_contains "${WORK}/empty has no hvo-roof-install-linux-arm64: download it from https://github.com/HualapaiValley/HVO.RoofController/releases/tag/v${VERSION} into it."
}

test_an_attestation_that_does_not_verify_is_not_started() {
  install_sh FAKE_ATTESTATION=fails -- --roles rig

  assert_status 1
  assert_output_contains "        X Verification failed"
  assert_output_contains "hvo-roof-install-linux-arm64's attestation did not verify, so it may not be the release's: nothing was installed."
  assert_installer_did_not_run
}

test_without_gh_the_attestation_is_only_noted() {
  without gh
  install_sh -- --roles rig

  assert_status 0
  assert_output_contains "note  its attestation was not checked: gh (GitHub's CLI, https://cli.github.com) is not installed"
  assert_installer "root=yes"
}

test_gh_not_signed_in_only_notes_the_attestation() {
  install_sh FAKE_GH_AUTH=no -- --roles rig

  assert_status 0
  assert_output_contains "note  its attestation was not checked: gh is not signed in (gh auth login), or did not reach GitHub (gh auth status)"
  assert_not_called "gh attestation"
}

# A dry run is made from a branch, so its attestation names no tag.
test_a_dry_run_s_attestation_is_checked_without_a_tag() {
  SCRIPT_UNDER_TEST=${DRY_RUN_SCRIPT}
  install_sh FAKE_RELEASE_VERSION="${DRY_RUN_VERSION}" FAKE_INSTALLER_VERSION="${DRY_RUN_VERSION}+${COMMIT}" -- --roles rig

  assert_status 0
  assert_called "--signer-workflow HualapaiValley/HVO.RoofController/.github/workflows/release.yml"
  assert_not_called "--source-ref"
  assert_output_contains "Starting hvo-roof-install ${DRY_RUN_VERSION} as root (sudo)"
}

test_an_installer_of_another_release_is_not_started() {
  install_sh FAKE_INSTALLER_VERSION="4.0.0-rc.2+${COMMIT}" -- --roles rig

  assert_status 1
  assert_output_contains "hvo-roof-install-linux-arm64 says its version is '4.0.0-rc.2+${COMMIT}', and this is release ${VERSION}: nothing was installed."
  assert_installer_did_not_run
}

test_an_installer_that_names_no_commit_is_not_started() {
  install_sh FAKE_INSTALLER_VERSION="${VERSION}+local" -- --roles rig

  assert_status 1
  assert_output_contains "says its version is '${VERSION}+local'"
  assert_installer_did_not_run
}

test_a_noexec_tmpdir_is_explained() {
  install_sh FAKE_INSTALLER_NOEXEC=1 -- --roles rig

  assert_status 1
  assert_output_contains "Programs cannot run from ${WORK}/tmp (it is mounted noexec): set TMPDIR to a folder they can run from"
}

# --- The hand-over ----------------------------------------------------------------------------------------------------

test_the_installer_s_arguments_pass_through() {
  install_sh -- --roles rig -- upgrade --yes "two words"

  assert_status 0
  assert_installer "args=[upgrade][--yes][two words]"
}

test_the_first_argument_that_is_not_an_option_starts_the_installer_s() {
  record "${WORK}/root/etc/hvo-roof/install.json" rig
  install_sh -- --roles rig status --check

  assert_status 0
  assert_output_not_contains "is ready for"
  assert_installer "args=[status][--check]"
}

test_the_installer_s_exit_status_is_install_sh_s() {
  install_sh FAKE_INSTALLER_EXIT=3 -- --roles rig

  assert_status 3
  assert_output_contains "hvo-roof-install ran"
}

test_the_installer_asks_on_the_terminal() {
  EXPECT=(--expect "Which port for the web UI? " 8099)
  install_sh_on_terminal FAKE_INSTALLER_ASKS="Which port for the web UI? " -- --roles rig

  assert_status 0
  assert_installer "terminal=yes"
  assert_installer "answer=8099"
}

test_a_release_from_a_folder_is_the_installer_s_too() {
  install_sh -- --from "${WORK}/release" --roles rig

  assert_status 0
  assert_installer "args=[--release][${WORK}/release]"
}

test_a_folder_given_from_where_it_is_ignores_cdpath() {
  # install.sh starts under env -i, with no PWD to keep a logical path, so the folder is the physical one: on macOS,
  # TMPDIR's /var is a link to /private/var.
  local here
  here=$(cd -P -- "${WORK}" && pwd)
  mkdir -p "${WORK}/elsewhere/release"
  pushd "${WORK}" >/dev/null || return
  install_sh CDPATH=".:${WORK}/elsewhere" -- --from release --roles rig
  popd >/dev/null || return

  assert_status 0
  assert_output_contains "Taking hvo-roof-install ${VERSION} for linux-arm64 from ${here}/release"
  assert_installer "args=[--release][${here}/release]"
}

test_a_rollback_from_a_folder_is_not_given_it_as_the_release_before() {
  record "${WORK}/root/etc/hvo-roof/install.json" controller,kiosk
  install_sh -- --from "${WORK}/release" rollback

  assert_status 0
  assert_installer "args=[rollback]"
}

test_a_release_the_installer_is_given_is_kept() {
  install_sh -- --from "${WORK}/release" --roles rig -- --release "${WORK}/other"

  assert_status 0
  assert_installer "args=[--release][${WORK}/other]"
}

test_commands_that_read_no_release_are_given_none() {
  record "${WORK}/root/etc/hvo-roof/install.json" controller,kiosk
  install_sh -- --from "${WORK}/release" uninstall

  assert_status 0
  assert_installer "args=[uninstall]"
  assert_not_called "docker"

  rm "${FAKE_STATE_DIR}/installer.log"
  install_sh -- --from "${WORK}/release" cert show

  assert_status 0
  assert_installer "args=[cert][show]"

  rm "${FAKE_STATE_DIR}/installer.log"
  install_sh -- --from "${WORK}/release" cert

  assert_status 0
  assert_installer "args=[cert][--release][${WORK}/release]"
}

test_the_installer_s_help_skips_the_checks() {
  install_sh FAKE_SUDO=denied FAKE_GITHUB_EXIT=6 -- -- --help

  assert_status 0
  assert_output_not_contains "Checking this machine"
  assert_output_not_contains "Starting"
  assert_not_called "sudo"
  assert_installer "root=no"
  assert_installer "args=[--help]"

  rm "${FAKE_STATE_DIR}/installer.log"
  install_sh FAKE_SUDO=denied -- upgrade --help

  assert_status 0
  assert_installer "args=[upgrade][--help]"
}

test_the_installer_s_version_skips_the_checks() {
  install_sh FAKE_SUDO=denied -- --version

  assert_status 0
  [ "${OUTPUT}" = "${VERSION}+${COMMIT}" ] || fail_test "the output is not the installer's version alone"
  assert_not_called "sudo"
}

# --- A Mac ------------------------------------------------------------------------------------------------------------

test_a_mac_installs_the_mac_app_for_the_person() {
  make_mac
  install_sh "${MAC[@]}" -- --roles mac-app,cli

  assert_status 0
  assert_output_contains "ok    macOS 15.0 on Apple silicon (osx-arm64)"
  assert_output_contains "ok    for mac-app, cli (from --roles), installed as you"
  assert_output_contains "ok    curl, tar and shasum"
  assert_output_contains "Downloading hvo-roof-install ${VERSION} for osx-arm64"
  assert_output_not_contains "synchronised"
  assert_not_called "sudo"
  assert_not_called "docker"
  assert_installer "root=no"
}

test_a_mac_without_shasum_names_it() {
  make_mac
  without shasum
  install_sh "${MAC[@]}" -- --roles cli

  assert_stopped_by_checks
  assert_output_contains "FAIL  These commands are missing: shasum. Install them, then run this again."
}

# --- Harness ----------------------------------------------------------------------------------------------------------

test_unknown_test_name_counts_as_failure() {
  OUTPUT=$("${INSTALL_SH_BASH}" "${TESTS_DIR}/install-sh-tests.sh" test_no_such_test 2>&1)
  STATUS=$?

  assert_status 1
  assert_output_contains "FAIL: no such test"
  assert_output_contains "0 passed, 1 failed"
}

# ---------------------------------------------------------------------------------------------------------------------

command -v python3 >/dev/null 2>&1 || { echo "install-sh-tests: python3 is required" >&2; exit 2; }
bake "${VERSION}" "${SCRIPT}" || { echo "install-sh-tests: build/release-assets.py could not write install.sh" >&2; exit 2; }
bake "${DRY_RUN_VERSION}" "${DRY_RUN_SCRIPT}" || exit 2

tests=()
if (($# > 0)); then
  tests=("$@")
else
  while IFS= read -r test; do
    tests+=("${test}")
  done < <(declare -F | awk '{print $3}' | grep '^test_')
fi

# shellcheck disable=SC2016 # expanded by the bash the tests run install.sh in
echo "install.sh tests ($("${INSTALL_SH_BASH}" -c 'echo "bash ${BASH_VERSION}"'))"
for test in ${tests[@]+"${tests[@]}"}; do
  run_test "${test}"
done

echo
echo "${PASSED} passed, ${FAILED} failed"
if ((FAILED > 0)); then
  printf '  %s\n' "${FAILURES[@]}"
  exit 1
fi
