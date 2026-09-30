#!/usr/bin/env bash
# The product version (docs/releasing.md), read from the VersionPrefix in Directory.Build.props.
#
#   build/version.sh                     4.0.0, the version the next release carries
#   build/version.sh --dev               4.0.0-dev, a workstation or deploy-script build
#   build/version.sh --ci <run>          4.0.0-ci.<run>, a CI build
#   build/version.sh --tag <tag>         the version a release tag names, checked: v4.0.0 gives 4.0.0 and v4.0.0-rc.1
#                                        gives 4.0.0-rc.1; any other tag, or one for another version, fails
#
# A release is built with -p:Version=<the --tag output>, every other build with -p:VersionSuffix=dev or ci.<run>, and
# Directory.Build.targets refuses a version that is neither the prefix nor a prerelease of it.
set -euo pipefail

usage() {
  sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

fail() {
  echo "version.sh: $*" >&2
  exit 1
}

props="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/Directory.Build.props"
[[ -f "${props}" ]] || fail "${props} was not found."

prefix=$(sed -n 's:^[[:space:]]*<VersionPrefix>\([^<]*\)</VersionPrefix>[[:space:]]*$:\1:p' "${props}")
[[ "$(printf '%s\n' "${prefix}" | grep -c .)" == 1 ]] || fail "Directory.Build.props must hold exactly one VersionPrefix."
[[ "${prefix}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] \
  || fail "the VersionPrefix '${prefix}' is not a release version such as 4.0.0."

case "${1:-}" in
  "")
    [[ $# -eq 0 ]] || usage
    echo "${prefix}"
    ;;
  --dev)
    [[ $# -eq 1 ]] || usage
    echo "${prefix}-dev"
    ;;
  --ci)
    [[ $# -eq 2 ]] || usage
    [[ "$2" =~ ^[1-9][0-9]*$ ]] || fail "the CI run number '$2' is not a positive whole number."
    echo "${prefix}-ci.$2"
    ;;
  --tag)
    [[ $# -eq 2 ]] || usage
    tag="$2"
    [[ "${tag}" =~ ^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(-rc\.([1-9][0-9]*))?$ ]] \
      || fail "the tag '${tag}' is not a release tag: use v${prefix} for a release or v${prefix}-rc.1 for a release candidate."
    core="${BASH_REMATCH[1]}"
    [[ "${core}" == "${prefix}" ]] \
      || fail "the tag '${tag}' is for ${core}, but Directory.Build.props holds ${prefix}. Change the VersionPrefix first, in its own pull request."
    echo "${tag#v}"
    ;;
  *)
    usage
    ;;
esac
