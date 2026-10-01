#!/usr/bin/env bash
# install.sh: checks this machine, then downloads the release's hvo-roof-install for it, checks the download and starts
# it (docs/install.md#installsh).
#
#   curl -fsSL https://github.com/HualapaiValley/HVO.RoofController/releases/latest/download/install.sh | bash
#   curl -fsSL https://github.com/HualapaiValley/HVO.RoofController/releases/latest/download/install.sh | bash -s -- [options] [installer arguments]
#
# Each release's copy installs that release: build/release-assets.py writes its version into release_version below, and
# this copy, the repository's, refuses to run. It needs bash 3.2 or later (a Mac's), curl, tar and sha256sum (shasum on a
# Mac).
#
# The whole script is one function, called on its last line, so a download cut short runs nothing: cut short in the
# function, bash finds it unfinished; cut short in the last line, the call is not made, or is made without the marker
# that line ends its arguments with, and refused. Its standard input is the download, so it reads nothing from it: its
# questions, and the installer's, are on the terminal (/dev/tty).

hvo_roof_install_sh() {
  if (($# == 0)) || [ "${!#}" != "end of install.sh" ]; then
    printf 'install.sh: the download was cut short, and nothing was done: download it again.\n' >&2
    exit 1
  fi
  set -- "${@:1:$(($# - 1))}"
  set -euo pipefail
  umask 077

  local release_version=''  # build/release-assets.py writes the release's version here.
  local repository="HualapaiValley/HVO.RoofController"
  # tests/install puts the files this script reads (/etc/os-release, /proc/device-tree/model, /dev/i2c-1 and
  # /etc/hvo-roof/install.json), and /var/lib/docker, whose free space it checks before Docker is installed, in a folder
  # of its own. Nothing else changes with it.
  local root="${HVO_ROOF_INSTALL_TEST_ROOT:-}"

  # The free space each place needs, in KiB: the installer's download, a person's programs, and Docker's images (the
  # controller's and the one it replaces, or a rig's and the HAT emulator's).
  local need_download_kib=$((200 * 1024)) need_home_kib=$((200 * 1024)) need_docker_kib=$((2 * 1024 * 1024))
  # How far this machine's clock may be from github.com's: further, and TLS, the attestation and the certificates the
  # installer makes go wrong.
  local clock_tolerance_seconds=300

  local failures=0
  local -a sudo_command=()

  # ---------------------------------------------------------------------------------------------------------------------
  # Output

  say() { printf '%s\n' "$*"; }
  ok() { printf '  ok    %s\n' "$*"; }
  note() { printf '  note  %s\n' "$*"; }
  failed() {
    printf '  FAIL  %s\n' "$*"
    failures=$((failures + 1))
  }
  die() {
    printf 'install.sh: %s\n' "$*" >&2
    exit 1
  }
  usage_error() {
    printf 'install.sh: %s\n' "$*" >&2
    printf 'install.sh --help lists its options.\n' >&2
    exit 2
  }

  usage() {
    local which="${release_version:-"(the repository's copy, which installs nothing)"}"
    cat <<EOF
install.sh for HVO Roof Controller ${which}

Checks this machine, downloads hvo-roof-install ${release_version:+${release_version} }for it, checks the download
against the release's SHA256SUMS (and its attestation, when gh is signed in) and starts it.

  curl -fsSL https://github.com/${repository}/releases/latest/download/install.sh | bash
  curl -fsSL https://github.com/${repository}/releases/latest/download/install.sh | bash -s -- [options] [--] [installer arguments]

Options, before the installer's arguments:
  --roles LIST      What this machine is for, so the right things are checked: controller, rig, kiosk, cli or mac-app,
                    separated by commas. Without it: an answers file's roles, what is installed (for a command such as
                    upgrade), or a question.
  --from DIR        Take hvo-roof-install and SHA256SUMS from DIR, a folder holding the release's files, not from
                    GitHub. The installer reads the release from DIR too (its --release DIR), except for rollback,
                    whose --release is the release before.
  --check           Check this machine, then stop: download nothing and change nothing.
  --install-docker  When Docker is needed and missing, install it from Docker's apt repository without asking
                    (Debian, Raspberry Pi OS and Ubuntu).
  --enable-i2c      When the controller needs I2C and it is off, turn it on with raspi-config without asking.
  -h, --help        This help. (--help after the installer's arguments is the installer's.)

The first argument that is not one of these, and everything after it (or after --), goes to hvo-roof-install: upgrade,
--answers FILE, --plan, --help and the rest.

Exit codes: the installer's, when it ran; otherwise 0 when --check passed, 1 when a check, the download or its
verification failed, and 2 for a command line, an answer or a role that is not valid, or roles or a sudo password with
no terminal to ask for them on.
EOF
  }

  # ---------------------------------------------------------------------------------------------------------------------
  # Helpers

  have() { command -v "$1" >/dev/null 2>&1; }

  # True when there is a terminal to ask on: not from cron, CI or ssh without one.
  have_terminal() { { : </dev/tty; } 2>/dev/null; }

  # ask_yes <question>: true when the person at the terminal answers yes.
  ask_yes() {
    local reply=""
    have_terminal || return 1
    printf '%s [y/N] ' "$1" >/dev/tty
    IFS= read -r reply </dev/tty || reply=""
    case "${reply}" in
      y | Y | yes | Yes | YES) return 0 ;;
      *) return 1 ;;
    esac
  }

  # as_root <command...>: the command as root, with sudo unless this script is root already or only plans.
  as_root() {
    ${sudo_command[@]+"${sudo_command[@]}"} "$@"
  }

  # contains <list> <item>: true when the comma-separated list holds the item.
  contains() {
    case ",$1," in
      *",$2,"*) return 0 ;;
      *) return 1 ;;
    esac
  }

  # The value of a name in /etc/os-release, without its quotes. The file is read, never run.
  os_release() {
    local file="${root}/etc/os-release" line value=""
    [ -r "${file}" ] || return 0
    while IFS= read -r line || [ -n "${line}" ]; do
      case "${line}" in
        "$1="*)
          value=${line#*=}
          value=${value#[\"\']}
          value=${value%[\"\']}
          ;;
      esac
    done <"${file}"
    printf '%s' "${value}"
  }

  # The roles in a JSON file's "roles" array (an answers file or an install record), separated by commas.
  json_roles() {
    local text pattern='"roles":\[([^]]*)\]'
    text=$(tr -d ' \t\r\n' <"$1") || return 1
    if [[ ${text} =~ ${pattern} ]]; then
      printf '%s' "${BASH_REMATCH[1]}" | tr -d '"'
    fi
  }

  # Human-readable KiB.
  size_of() {
    if (($1 >= 1024 * 1024)); then
      printf '%d.%d GB' $(($1 / 1024 / 1024)) $(($1 * 10 / 1024 / 1024 % 10))
    else
      printf '%d MB' $(($1 / 1024))
    fi
  }

  # check_space <path> <KiB needed> <what it is for>: the free space on the file system that holds the path (or the
  # nearest folder above it that is there).
  check_space() {
    local path=$1 need=$2 what=$3 free
    while [ ! -d "${path}" ] && [ "${path}" != / ]; do
      path=$(dirname "${path}")
    done
    free=$(df -Pk "${path}" 2>/dev/null | awk 'NR == 2 { print $4 }' || true)
    if ! [[ ${free} =~ ^[0-9]+$ ]]; then
      note "df could not tell the free space on ${path}: ${what} need $(size_of "${need}")"
    elif ((free < need)); then
      failed "${path} has $(size_of "${free}") free, and ${what} need $(size_of "${need}"): free some space, then run this again."
    else
      ok "$(size_of "${free}") free on ${path} for ${what}"
    fi
  }

  # Seconds since 1970 for an HTTP date, with a Mac's date or GNU's (which takes "" for today's midnight).
  http_date_seconds() {
    [ -n "$1" ] || return 1
    if [ "${os}" = macos ]; then
      LC_ALL=C date -j -u -f '%a, %d %b %Y %H:%M:%S GMT' "$1" +%s 2>/dev/null
    else
      LC_ALL=C date -u -d "$1" +%s 2>/dev/null
    fi
  }

  # What a curl exit code means.
  curl_problem() {
    case "$1" in
      5) printf "the proxy's name did not resolve" ;;
      6) printf 'its name did not resolve (DNS)' ;;
      7) printf 'the connection was refused, or the network is unreachable' ;;
      22) printf 'it answered with an HTTP error' ;;
      28) printf 'it did not answer in time' ;;
      35 | 51 | 53 | 54 | 58 | 59 | 60 | 66 | 77 | 80 | 82 | 83 | 90 | 91)
        printf 'TLS failed (curl exit %s): a clock that is wrong, or a proxy that intercepts HTTPS, does this' "$1" ;;
      *) printf 'curl exit %s' "$1" ;;
    esac
  }

  # fetch <url> <file>: a download over HTTPS only, retried a few times.
  fetch() {
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --connect-timeout 15 --max-time 600 -o "$2" "$1"
  }

  # probe <url> <curl options...>: one request, HTTPS only, with a short timeout.
  probe() {
    local url=$1
    shift
    curl -sS --proto '=https' --tlsv1.2 --connect-timeout 15 --max-time 30 "$@" "${url}"
  }

  # The file's SHA-256, from its contents on standard input: given a name with a backslash or a newline in it, sha256sum
  # escapes the name, and puts a backslash before the hash.
  sha256_of() {
    if have sha256sum; then
      sha256sum <"$1" | awk '{ print $1 }'
    else
      shasum -a 256 <"$1" | awk '{ print $1 }'
    fi
  }

  # The download: the installer for this machine, checked against the release's SHA256SUMS and, with gh, its
  # attestation; then the hand-over. It never returns.
  fetch_and_start() {
    local base_url="https://github.com/${repository}/releases/download/v${release_version}"
    local release_page="https://github.com/${repository}/releases/tag/v${release_version}"
    local asset="hvo-roof-install-${rid}" work installer expected actual status=0
    work=$(mktemp -d "${TMPDIR:-/tmp}/hvo-roof-install.XXXXXXXX")
    local quoted_work
    printf -v quoted_work '%q' "${work}"
    # shellcheck disable=SC2064 # The folder is named now, so the trap needs no variable when it runs.
    trap "rm -rf ${quoted_work}" EXIT
    installer="${work}/hvo-roof-install"

    if [ -n "${from}" ]; then
      ${quick} || say "Taking hvo-roof-install ${release_version} for ${rid} from ${from}"
      [ -f "${from}/${asset}" ] || die "${from} has no ${asset}: download it from ${release_page} into it."
      [ -f "${from}/SHA256SUMS" ] || die "${from} has no SHA256SUMS: download it from ${release_page} into it."
      cp "${from}/${asset}" "${installer}"
      cp "${from}/SHA256SUMS" "${work}/SHA256SUMS"
    else
      ${quick} || say "Downloading hvo-roof-install ${release_version} for ${rid}"
      fetch "${base_url}/SHA256SUMS" "${work}/SHA256SUMS" || status=$?
      ((status == 0)) || die "SHA256SUMS could not be downloaded from ${base_url}: $(curl_problem "${status}")."
      fetch "${base_url}/${asset}" "${installer}" || status=$?
      ((status == 0)) || die "${asset} could not be downloaded from ${base_url}: $(curl_problem "${status}")."
    fi

    # SHA256SUMS is sha256sum's: a hash, two spaces (or a space and *, for binary) and the file's name.
    expected=$(awk -v name="${asset}" '$2 == name || $2 == "*" name { print $1 }' "${work}/SHA256SUMS")
    [[ ${expected} =~ ^[0-9a-f]{64}$ ]] \
      || die "SHA256SUMS does not list ${asset} once, so the download cannot be checked: nothing was installed."
    actual=$(sha256_of "${installer}")
    [ "${actual}" = "${expected}" ] \
      || die "${asset}'s SHA-256 is ${actual}, and SHA256SUMS says ${expected}: the download is not the release's, and nothing was installed. Run this again; if it happens again, tell the project (https://github.com/${repository}/issues)."
    ${quick} || ok "${asset}'s SHA-256 is the one SHA256SUMS lists"

    # The attestation says the release workflow built it, from this repository and the release's tag. gh fetches it
    # when it is signed in.
    if ${quick}; then
      :
    elif ! have gh; then
      note "its attestation was not checked: gh (GitHub's CLI, https://cli.github.com) is not installed"
    elif ! gh auth status >/dev/null 2>&1 </dev/null; then
      note "its attestation was not checked: gh is not signed in (gh auth login), or did not reach GitHub (gh auth status)"
    else
      local -a verify=(gh attestation verify "${installer}" --repo "${repository}"
        --signer-workflow "${repository}/.github/workflows/release.yml")
      # A dry run is made from a branch, and has no tag.
      case "${release_version}" in
        *-dryrun.*) ;;
        *) verify+=(--source-ref "refs/tags/v${release_version}") ;;
      esac
      local verification
      if verification=$("${verify[@]}" 2>&1 </dev/null); then
        ok "its attestation says ${repository}'s release workflow built it"
      else
        printf '%s\n' "${verification}" | tail -n 5 | sed 's/^/        /'
        die "${asset}'s attestation did not verify, so it may not be the release's: nothing was installed."
      fi
    fi

    chmod 0755 "${installer}"
    local built
    status=0
    built=$("${installer}" --version 2>&1 </dev/null) || status=$?
    ((status != 126)) \
      || die "Programs cannot run from ${TMPDIR:-/tmp} (it is mounted noexec): set TMPDIR to a folder they can run from, then run this again."
    case "${built}" in
      "${release_version}+"*) [[ ${built#"${release_version}+"} =~ ^[0-9a-f]{40}$ ]] ;;
      *) false ;;
    esac || die "${asset} says its version is '${built}', and this is release ${release_version}: nothing was installed."

    # The hand-over. The installer reads the terminal, since this script's standard input is the download: with none
    # (cron, CI) it reads nothing, and refuses a question it would have asked.
    # rollback's --release is the release before, not this one: it is given, or the installer downloads it.
    local -a arguments=(${passed[@]+"${passed[@]}"})
    if [ -n "${from}" ] && ! ${quick} && ! ${has_release} && ${reads_release} && [ "${command}" != rollback ]; then
      arguments+=(--release "${from}")
    fi
    local input=/dev/null
    ! have_terminal || input=/dev/tty
    if ! ${quick}; then
      say ""
      say "Starting hvo-roof-install ${release_version}$( ((${#sudo_command[@]} > 0)) && printf ' as root (sudo)')"
      say ""
    fi
    status=0
    as_root "${installer}" ${arguments[@]+"${arguments[@]}"} <"${input}" || status=$?
    exit "${status}"
  }

  # ---------------------------------------------------------------------------------------------------------------------
  # The command line: this script's options, then the installer's arguments.

  local roles="" roles_from="" from="" check_only=false install_docker=false enable_i2c=false
  while (($# > 0)); do
    case "$1" in
      --roles | --from)
        { (($# >= 2)) && [ -n "$2" ]; } || usage_error "$1 needs a value."
        if [ "$1" = --roles ]; then roles=$2; else from=$2; fi
        shift 2
        ;;
      --roles=?*)
        roles=${1#--roles=}
        shift
        ;;
      --from=?*)
        from=${1#--from=}
        shift
        ;;
      --check)
        check_only=true
        shift
        ;;
      --install-docker)
        install_docker=true
        shift
        ;;
      --enable-i2c)
        enable_i2c=true
        shift
        ;;
      -h | --help)
        usage
        exit 0
        ;;
      --)
        shift
        break
        ;;
      *) break ;;
    esac
  done
  local -a passed=("$@")
  [ -z "${roles}" ] || roles_from="--roles"

  if [ -z "${release_version}" ]; then
    die "This is the repository's copy of install.sh, which names no release: use a release's copy.
  curl -fsSL https://github.com/${repository}/releases/latest/download/install.sh | bash"
  fi

  # What the installer is asked to do: its command (the first argument, when it is not an option), and whether it only
  # prints something (its help or its version), only plans, or reads its answers from a file.
  local command="" subcommand="" answers="" quick=false plan=false has_release=false index=0 argument
  if ((${#passed[@]} > 0)) && [[ ${passed[0]} != -* ]]; then
    command=${passed[0]}
    subcommand=${passed[1]:-}
  fi
  while ((index < ${#passed[@]})); do
    argument=${passed[index]}
    case "${argument}" in
      --answers)
        answers=${passed[index + 1]:-}
        index=$((index + 1))
        ;;
      --answers=*) answers=${argument#--answers=} ;;
      --help | -h | -\?) quick=true ;;
      --version) [ -n "${command}" ] || quick=true ;;
      --plan) plan=true ;;
      --release | --release=*) has_release=true ;;
    esac
    index=$((index + 1))
  done
  # The commands that read the release (--release DIR): an install, and upgrade, rollback, restore and cert, which
  # deploy the controller from it. cert show reads nothing.
  local reads_release=false
  case "${command}:${subcommand}" in
    : | upgrade:* | rollback:* | restore:* | cert:import | cert:-* | cert:) reads_release=true ;;
  esac
  if [ -n "${answers}" ] && ! ${quick} && [ ! -r "${answers}" ]; then
    usage_error "--answers ${answers}: there is no such file, or you cannot read it."
  fi
  if [ -n "${from}" ]; then
    [ -d "${from}" ] || usage_error "--from ${from}: there is no such folder."
    from=$(CDPATH='' cd -- "${from}" && pwd)
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # The platform: Linux on 64-bit ARM or x86-64, or a Mac with Apple silicon.

  local os="" rid="" processor pi_model="" description kernel
  kernel=$(uname -s)
  case "${kernel}" in
    Linux) os=linux ;;
    Darwin) os=macos ;;
    *) die "This machine runs ${kernel}: the roof controller's programs run on Linux (64-bit ARM or x86-64) and on Macs with Apple silicon." ;;
  esac
  processor=$(uname -m)
  if [ "${os}" = macos ]; then
    # Under Rosetta uname says x86_64 on Apple silicon too: the hardware says which Mac it is.
    [ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" = 1 ] \
      || die "This Mac has an Intel processor: hvo-roof, the Mac app and the installer run on Macs with Apple silicon only."
    rid=osx-arm64
    description="macOS $(sw_vers -productVersion 2>/dev/null || printf '?') on Apple silicon"
  else
    case "${processor}" in
      aarch64 | arm64) rid=linux-arm64 ;;
      x86_64 | amd64) rid=linux-x64 ;;
      arm* | aarch32)
        die "This machine runs 32-bit ARM (${processor}), and the roof controller's programs are 64-bit: install the 64-bit Raspberry Pi OS (or another 64-bit Linux), then run this again."
        ;;
      *) die "This machine's processor is ${processor}: the roof controller's programs run on 64-bit ARM (aarch64) and x86-64." ;;
    esac
    # The 32-bit Raspberry Pi OS runs on a 64-bit kernel on a Pi 4 or 5: uname says aarch64, and nothing 64-bit runs.
    local bits
    bits=$(getconf LONG_BIT 2>/dev/null || printf 64)
    [ "${bits}" = 64 ] \
      || die "This machine has a 64-bit kernel and a ${bits}-bit system, and the roof controller's programs are 64-bit: install the 64-bit Raspberry Pi OS (or another 64-bit Linux), then run this again."
    # musl's ldd says so on its standard error, and exits 1.
    if have ldd; then
      case "$(ldd --version 2>&1 || true)" in
        *musl* | *MUSL*)
          die "This Linux has musl (as Alpine does), and the roof controller's programs need glibc (as Debian, Raspberry Pi OS and Ubuntu have)."
          ;;
      esac
    fi
    if [ -r "${root}/proc/device-tree/model" ]; then
      pi_model=$(tr -d '\0' <"${root}/proc/device-tree/model")
      case "${pi_model}" in
        "Raspberry Pi"*) ;;
        *) pi_model="" ;;
      esac
    fi
    description=$(os_release PRETTY_NAME)
    description="Linux on ${processor}${description:+, ${description}}${pi_model:+, ${pi_model}}"
  fi

  # The installer's help or version: nothing to check, and nothing that needs root.
  ! ${quick} || fetch_and_start

  say "HVO Roof Controller ${release_version}: install.sh"
  say ""
  say "Checking this machine"
  ok "${description} (${rid})"

  # ---------------------------------------------------------------------------------------------------------------------
  # The roles: what the checks are for, and whether the installer runs as root.

  local system_record="${root}/etc/hvo-roof/install.json" user_record config_home
  config_home=${XDG_CONFIG_HOME:-}
  [[ ${config_home} == /* ]] || config_home="${HOME}/.config"
  user_record="${config_home}/hvo-roof/install.json"

  local scope=""
  if [ -n "${roles}" ]; then
    :
  elif [ -n "${answers}" ]; then
    roles=$(json_roles "${answers}") || usage_error "--answers ${answers} could not be read."
    [ -n "${roles}" ] || usage_error "--answers ${answers} names no roles."
    roles_from="the answers file"
  elif [ -n "${command}" ] || ${plan}; then
    # A command works on what is installed: the machine's (as root) when it has a record, else the person's.
    if [ -e "${system_record}" ]; then
      scope=system
      roles=$(json_roles "${system_record}" 2>/dev/null || true)
    elif [ -e "${user_record}" ]; then
      roles=$(json_roles "${user_record}" 2>/dev/null || true)
    fi
    [ -z "${roles}" ] || roles_from="what is installed"
    # Restore puts back a controller or a rig, as root; backup and cert work on one, recorded or not.
    case "${command}:${os}" in
      restore:linux | backup:linux | cert:linux) scope=system ;;
    esac
  else
    have_terminal || usage_error "There is no terminal to ask what this machine is for on: give --roles, or the installer's --answers FILE."
    local choice=""
    {
      say ""
      say "What is this machine for? (The installer asks for the rest.)"
      if [ -n "${pi_model}" ]; then
        say "  1) The observatory's roof controller, which drives the real HAT, and its touchscreen kiosk (installed as root)"
        say "  2) A test rig: the controller with the HAT emulator, which moves no roof (installed as root)"
        say "  3) hvo-roof, the command-line client, for you"
      elif [ "${os}" = linux ]; then
        say "  1) A test rig: the controller with the HAT emulator, which moves no roof (installed as root)"
        say "  2) hvo-roof, the command-line client, for you"
      else
        say "  1) The Mac app and hvo-roof, for you"
        say "  2) A test rig: the controller with the HAT emulator, in Docker Desktop, for you"
      fi
      printf 'Choose a number: '
    } >/dev/tty
    IFS= read -r choice </dev/tty || choice=""
    case "${pi_model:+pi-}${os}:${choice}" in
      pi-linux:1) roles=controller,kiosk ;;
      pi-linux:2 | linux:1 | macos:2) roles=rig ;;
      pi-linux:3 | linux:2) roles=cli ;;
      macos:1) roles=mac-app,cli ;;
      *) usage_error "'${choice}' is not one of the choices." ;;
    esac
    roles_from="your choice"
  fi

  # The machine's roles run as root, the person's as the person: a rig is the machine's on Linux and the person's on a Mac.
  local role system_roles="" user_roles="" IFS_before=${IFS}
  IFS=,
  for role in ${roles}; do
    case "${role}:${os}" in
      controller:* | kiosk:* | rig:linux) system_roles="${system_roles:+${system_roles},}${role}" ;;
      cli:* | mac-app:* | rig:macos) user_roles="${user_roles:+${user_roles},}${role}" ;;
      *)
        IFS=${IFS_before}
        usage_error "'${role}', from ${roles_from}, is not a role: the roles are controller, rig, kiosk, cli and mac-app."
        ;;
    esac
  done
  IFS=${IFS_before}
  if [ -n "${system_roles}" ] && [ -n "${user_roles}" ]; then
    usage_error "${system_roles//,/ and } (installed as root) and ${user_roles//,/ and } (installed as you) are installed in separate runs."
  fi
  if [ -z "${scope}" ]; then
    if [ -n "${system_roles}" ]; then scope=system; else scope=user; fi
  fi
  if [ -n "${roles}" ]; then
    ok "for ${roles//,/, } (from ${roles_from}), installed as $([ "${scope}" = system ] && printf root || printf you)"
  fi

  if [ "$(id -u)" = 0 ]; then
    [ "${scope}" = system ] \
      || die "${user_roles:-What is installed for a person} is yours, and never installed as root: run this as yourself, without sudo."
  elif [ "${scope}" = system ] && ! ${plan}; then
    # A plan changes nothing, and the installer makes one without root.
    sudo_command=(sudo --)
  fi

  # What the run needs: Docker for the controller, a rig and the kiosk (which the controller's container serves); I2C for
  # the controller; the ports, for a first install's controller or rig (one whose scope has a record with neither, or no
  # record). Uninstall and backup check what they need themselves, and a plan says what is missing.
  local needs_docker=false needs_i2c=false needs_ports=false record record_roles
  if ${reads_release} && ! ${plan}; then
    if contains "${roles}" controller || contains "${roles}" rig || contains "${roles}" kiosk || [ "${command}:${os}" = restore:linux ]; then
      needs_docker=true
    fi
    if contains "${roles}" controller && [ -n "${pi_model}" ] && [ "${command}" != cert ]; then
      needs_i2c=true
    fi
    if { contains "${roles}" controller || contains "${roles}" rig; } && [ -z "${command}" ] && [ -z "${answers}" ]; then
      if [ "${scope}" = system ]; then record=${system_record}; else record=${user_record}; fi
      if [ ! -e "${record}" ]; then
        needs_ports=true
      elif record_roles=$(json_roles "${record}" 2>/dev/null) \
        && ! contains "${record_roles}" controller && ! contains "${record_roles}" rig; then
        needs_ports=true
      fi
    fi
  fi

  if contains "${roles}" controller && [ -z "${pi_model}" ]; then
    failed "The controller drives the real HAT, on the observatory's Raspberry Pi, and this is not a Pi (/proc/device-tree/model names none): to test, choose a test rig."
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # The commands this script and the installer use

  local tool missing="" summer=sha256sum
  for tool in curl tar; do
    have "${tool}" || missing="${missing} ${tool}"
  done
  if ! have sha256sum; then
    if have shasum; then
      summer=shasum
    else
      missing="${missing} $([ "${os}" = macos ] && printf shasum || printf sha256sum)"
    fi
  fi
  if [ -n "${missing}" ]; then
    failed "These commands are missing:${missing}. Install them$([ "${os}" = linux ] && printf ' (sudo apt-get install%s)' "${missing/sha256sum/coreutils}"), then run this again."
  else
    ok "curl, tar and ${summer}"
  fi
  have curl || die "Without curl nothing more can be checked: install it, then run this again."
  if ((${#sudo_command[@]} > 0)); then
    have sudo \
      || die "The installer runs as root, and sudo is not installed: run this as root, or install sudo (as root, apt-get install sudo), then run this again."
    if ! sudo -n true 2>/dev/null; then
      have_terminal || usage_error "The installer runs as root, and there is no terminal for sudo to ask for your password on: run this as root, or where sudo asks for no password."
      say "  The installer runs as root: sudo asks for your password."
      # shellcheck disable=SC2024 # sudo asks on the terminal: the redirect is meant to be this script's.
      sudo -v </dev/tty || die "sudo did not let you run commands as root, and the installer runs as root: nothing was installed."
    fi
    ok "sudo lets you run commands as root"
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # Free space, the network and the clock

  local download_folder=${TMPDIR:-/tmp}
  check_space "${download_folder}" "${need_download_kib}" "the installer's download (TMPDIR)"
  if [ "${scope}" = user ] && ${reads_release} && ! ${plan}; then
    check_space "${HOME}" "${need_home_kib}" "your programs"
  fi

  local headers status=0 remote_date remote_seconds skew
  headers=$(probe https://github.com -I 2>/dev/null) || status=$?
  if ((status != 0)); then
    if [ -n "${from}" ]; then
      note "github.com did not answer ($(curl_problem "${status}")): the release's files come from ${from}"
    else
      failed "github.com did not answer: $(curl_problem "${status}"). The installer and the release come from there."
    fi
  else
    ok "github.com answers"
    # The last Date is github.com's: through a proxy, its reply to CONNECT comes first. awk reads to the end: with
    # pipefail, a printf cut off by an awk that stopped reading would end the script.
    remote_date=$(printf '%s\n' "${headers}" | tr -d '\r' | awk 'tolower($1) == "date:" { sub(/^[^:]*:[ \t]*/, ""); date = $0 } END { if (date != "") print date }')
    remote_seconds=$(http_date_seconds "${remote_date}" || true)
    if [[ ${remote_seconds} =~ ^[0-9]+$ ]]; then
      skew=$(($(date -u +%s) - remote_seconds))
      ((skew >= 0)) || skew=$((-skew))
      if ((skew > clock_tolerance_seconds)); then
        failed "This machine's clock is $( ((skew < 600)) && printf '%d seconds' "${skew}" || printf '%d minutes' $(((skew + 30) / 60))) out (it says $(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S') GMT, and github.com ${remote_date}): set it, and synchronise it$([ "${os}" = linux ] && printf ' (sudo timedatectl set-ntp true)'), then run this again."
      else
        ok "the clock is within ${skew} s of github.com's"
      fi
    else
      note "github.com sent no date to compare this machine's clock with"
    fi
  fi
  if [ "${os}" = linux ] && have timedatectl; then
    case "$(timedatectl show --property=NTPSynchronized --value 2>/dev/null || true)" in
      yes) ok "the clock is synchronised" ;;
      no) note "the clock is not synchronised: turn synchronisation on (sudo timedatectl set-ntp true), so that it stays right" ;;
    esac
  fi
  if ${needs_docker}; then
    local code
    status=0
    code=$(probe https://ghcr.io/v2/ -o /dev/null -w '%{http_code}' 2>/dev/null) || status=$?
    if ((status != 0)); then
      failed "ghcr.io did not answer: $(curl_problem "${status}"). Docker pulls the controller's images from there."
    else
      ok "ghcr.io answers (HTTP ${code})"
    fi
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # Docker

  # True when Docker's apt repository serves this Linux: Debian (Raspberry Pi OS is Debian) and Ubuntu.
  apt_docker_os() {
    have apt-get && have dpkg && case "$(os_release ID)" in debian | ubuntu) true ;; *) false ;; esac
  }

  # Docker Engine and its plugins from Docker's apt repository, as https://docs.docker.com/engine/install/debian/ does it.
  install_docker_from_apt() {
    local id codename architecture
    id=$(os_release ID)
    codename=$(os_release VERSION_CODENAME)
    architecture=$(dpkg --print-architecture)
    if [ -z "${codename}" ]; then
      say "  /etc/os-release names no VERSION_CODENAME, so Docker's apt repository for it cannot be chosen."
      return 1
    fi
    say "  Installing Docker from https://download.docker.com/linux/${id} ${codename} (${architecture})"
    as_root apt-get update -q </dev/null >/dev/null \
      && as_root apt-get install -y -q ca-certificates curl </dev/null >/dev/null \
      && as_root install -m 0755 -d /etc/apt/keyrings </dev/null \
      && as_root curl -fsSL --proto '=https' --tlsv1.2 -o /etc/apt/keyrings/docker.asc "https://download.docker.com/linux/${id}/gpg" </dev/null \
      && as_root chmod a+r /etc/apt/keyrings/docker.asc </dev/null \
      && printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/%s %s stable\n' \
        "${architecture}" "${id}" "${codename}" | as_root tee /etc/apt/sources.list.d/docker.list >/dev/null \
      && as_root apt-get update -q </dev/null >/dev/null \
      && as_root apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin </dev/null >/dev/null
  }

  # Docker's engine: it answers, its version is 20.10 or later, and whether Compose v2 is there. False when it fails.
  check_docker_engine() {
    local version major minor compose
    if ! version=$(as_root docker version --format '{{.Server.Version}}' 2>&1 </dev/null); then
      failed "Docker is installed, and its engine did not answer ($(printf '%s' "${version}" | tail -n 1)): start it$([ "${os}" = linux ] && printf ' (sudo systemctl enable --now docker)' || printf ' (open Docker Desktop)'), then run this again."
      return 1
    fi
    if ! [[ ${version} =~ ^([0-9]+)\.([0-9]+) ]]; then
      failed "Docker's engine says its version is '${version}', which this script cannot read: the installer needs 20.10 or later."
      return 1
    fi
    major=$((10#${BASH_REMATCH[1]}))
    minor=$((10#${BASH_REMATCH[2]}))
    if ((major < 20 || (major == 20 && minor < 10))); then
      failed "Docker ${version} is too old, and the installer needs Docker Engine 20.10 or later: upgrade it (https://docs.docker.com/engine/install/), then run this again."
      return 1
    fi
    compose=$(as_root docker compose version --short 2>/dev/null </dev/null || true)
    compose=${compose#v}
    if [[ ${compose} =~ ^[2-9]\. ]]; then
      ok "Docker ${version}, with Compose ${compose}"
    else
      ok "Docker ${version}"
      note "Docker Compose v2 is not installed: the installer does not use it, and the release's docker-compose.yaml does (the docker-compose-plugin package)"
    fi
  }

  # Docker is checked here, and when it is missing and can be installed, its folder's free space too: whether to install
  # it is decided once every other check has passed, and it is installed last.
  local docker_missing=false docker_root needs=""
  if ${needs_docker}; then
    case "${roles}" in
      "") needs="a restore needs" ;;
      *,*) needs="${roles//,/ and } need" ;;
      *) needs="${roles} needs" ;;
    esac
    if have docker; then
      if check_docker_engine && [ "${os}" = linux ]; then
        docker_root=$(as_root docker info --format '{{.DockerRootDir}}' 2>/dev/null </dev/null || true)
        check_space "${docker_root:-/var/lib/docker}" "${need_docker_kib}" "Docker's images"
      fi
    elif [ "${os}" = macos ]; then
      failed "Docker is not installed, and a test rig runs in Docker Desktop: install it (https://docs.docker.com/desktop/setup/install/mac-install/), start it, then run this again."
    elif ${check_only} || ! apt_docker_os; then
      failed "Docker is not installed, and ${needs} it: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/$(apt_docker_os && printf ', or run this with --install-docker')), then run this again."
    else
      docker_missing=true
      check_space "${root}/var/lib/docker" "${need_docker_kib}" "Docker's images"
    fi
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # I2C: the controller drives the HAT's relays and reads its inputs over /dev/i2c-1. When it is off and raspi-config can
  # turn it on, whether to is decided with Docker's install, and it is turned on last.

  local i2c_off=false
  if ${needs_i2c}; then
    if [ -e "${root}/dev/i2c-1" ]; then
      ok "I2C is on (/dev/i2c-1)"
    elif ${check_only} || ! have raspi-config; then
      failed "I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it: turn it on (sudo raspi-config nonint do_i2c 0$(have raspi-config && printf ', or run this with --enable-i2c')), then run this again."
    else
      i2c_off=true
    fi
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # The ports a first install's controller listens on. The installer asks which, and checks the ones chosen: here a port
  # in use is only noted.

  # True when something on this machine listens on the TCP port.
  port_in_use() {
    if have ss; then
      ss -Hltn 2>/dev/null | awk -v port="$1" '{ n = split($4, part, ":"); if (part[n] == port) found = 1 } END { exit !found }'
    elif have lsof; then
      lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1
    else
      (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
    fi
  }

  if ${needs_ports}; then
    local port purpose free_ports=""
    for port in 8443 8088 8080; do
      case "${port}" in
        8443) purpose="the API over HTTPS" ;;
        8088) purpose="the web UI" ;;
        *) purpose="the API over plain HTTP, when it is turned on" ;;
      esac
      if port_in_use "${port}"; then
        note "port ${port}, for ${purpose}, is in use: stop what listens on it, or choose another port in the installer"
      else
        free_ports="${free_ports:+${free_ports}, }${port}"
      fi
    done
    case "${free_ports}" in
      "") ;;
      *,*) ok "ports ${free_ports} are free" ;;
      *) ok "port ${free_ports} is free" ;;
    esac
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # What this script changes: Docker and I2C. Both are asked about (or taken from --install-docker and --enable-i2c)
  # before either is made, and only once every other check has passed. What is made is named if a check fails after it.

  local install_docker_now=false enable_i2c_now=false made=""
  if ${docker_missing}; then
    if ((failures > 0)); then
      failed "Docker is not installed, and ${needs} it: it is installed only once every other check passes, so put right what failed, then run this again (or install Docker Engine 20.10 or later yourself: https://docs.docker.com/engine/install/)."
    elif ${install_docker} || ask_yes "  Docker is not installed, and ${needs} it. Install it from Docker's apt repository?"; then
      install_docker_now=true
    else
      failed "Docker is not installed: install Docker Engine 20.10 or later (https://docs.docker.com/engine/install/, or run this with --install-docker), then run this again."
    fi
  fi
  if ${i2c_off}; then
    if ((failures > 0)); then
      failed "I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it: it is turned on only once every other check passes, so put right what failed, then run this again (or turn it on yourself: sudo raspi-config nonint do_i2c 0)."
    elif ${enable_i2c} || ask_yes "  I2C is off (there is no /dev/i2c-1), and the controller drives the HAT over it. Turn it on with raspi-config?"; then
      enable_i2c_now=true
    else
      failed "I2C is off: turn it on (sudo raspi-config nonint do_i2c 0, or run this with --enable-i2c), then run this again."
    fi
  fi

  if ${install_docker_now}; then
    if ((failures > 0)); then
      note "Docker was not installed: it is installed only once every other check passes, and one failed"
    elif install_docker_from_apt; then
      made="Docker was installed"
      check_docker_engine || true
    else
      made="an attempt to install Docker"
      failed "Docker could not be installed: install it yourself (https://docs.docker.com/engine/install/), then run this again."
    fi
  fi
  if ${enable_i2c_now}; then
    if ((failures > 0)); then
      note "I2C was not turned on: it is turned on only once every other check passes, and one failed"
    elif ! as_root raspi-config nonint do_i2c 0 </dev/null; then
      made="${made:+${made} and }an attempt to turn I2C on"
      failed "raspi-config could not turn I2C on: turn it on yourself (sudo raspi-config, Interface Options), then run this again."
    elif [ -e "${root}/dev/i2c-1" ]; then
      made="${made:+${made} and }I2C was turned on"
      ok "I2C is on (/dev/i2c-1): raspi-config turned it on"
    else
      made="${made:+${made} and }I2C was turned on"
      failed "raspi-config turned I2C on from the next start: restart this Pi (sudo reboot), then run this again."
    fi
  fi

  say ""
  if ((failures > 0)); then
    die "$( ((failures == 1)) && printf 'A check' || printf '%d checks' "${failures}") failed${made:+ after ${made}}, and nothing ${made:+else }was downloaded or installed."
  fi
  if ${check_only}; then
    say "This machine is ready for hvo-roof-install ${release_version}."
    exit 0
  fi

  fetch_and_start
}

hvo_roof_install_sh "$@" "end of install.sh" </dev/null
