#!/usr/bin/env bash
# Tests for build/release-latest.sh (docs/releasing.md#publishing): whether publishing a release moves latest, against a
# stand-in for gh that serves made-up releases, two to a page, and their release.json files.
# Requires bash, jq and sort -V.
# Run: tests/releasing/release-latest-tests.sh
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
LATEST="${TESTS_DIR}/../../build/release-latest.sh"

for tool in jq sort; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "release-latest-tests.sh needs ${tool}." >&2; exit 1; }
done

PASSED=0
FAILED=0
FAILURES=()

WORK=$(mktemp -d)
trap 'rm -rf "${WORK}"' EXIT

# Only the summary test writes one: CI's own step summary is not the script's.
unset GITHUB_STEP_SUMMARY

export FAKE_REPO=HualapaiValley/HVO.RoofController
export FAKE_RELEASES="${WORK}/releases.json"
export FAKE_ASSETS="${WORK}/assets"
export FAKE_CALLS="${WORK}/calls"
export FAKE_FAIL=""
OUT="${WORK}/out"
mkdir -p "${WORK}/bin" "${FAKE_ASSETS}" "${OUT}"

# gh as the script uses it: api (a release by its tag, or the releases, --paginate and --jq as gh runs them, page by
# page) and release download. Each call is logged; anything else fails.
cat > "${WORK}/bin/gh" <<'FAKE'
#!/usr/bin/env bash
set -uo pipefail
echo "$*" >> "${FAKE_CALLS}"
not_found() { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
unexpected() { echo "fake gh: unexpected call: gh $*" >&2; exit 2; }
case "${1:-}" in
  api)
    shift
    paginate=false filter="" path=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --paginate) paginate=true ;;
        --jq) filter=$2; shift ;;
        -*) unexpected api "$@" ;;
        *) path=$1 ;;
      esac
      shift
    done
    case "${path}" in
      "repos/${FAKE_REPO}/releases/tags/"*)
        pages=$(jq -c --arg tag "${path##*/}" 'map(select(.tag_name == $tag and (.draft | not))) | .[0] // empty' "${FAKE_RELEASES}")
        ;;
      "repos/${FAKE_REPO}/releases")
        [[ "${FAKE_FAIL}" != list ]] || { echo "gh: Server Error (HTTP 500)" >&2; exit 1; }
        pages=$(jq -c '. as $all | [range(0; length; 2) | $all[.:. + 2]] | if length == 0 then [[]] else . end | .[]' "${FAKE_RELEASES}")
        [[ "${paginate}" == true ]] || pages=$(head -n 1 <<<"${pages}")
        ;;
      *) not_found ;;
    esac
    [[ -n "${pages}" ]] || not_found
    while IFS= read -r page; do
      if [[ -n "${filter}" ]]; then jq -r "${filter}" <<<"${page}" || exit 1; else echo "${page}"; fi
    done <<<"${pages}"
    ;;
  release)
    [[ "${2:-}" == download && "${4:-}" == --repo && "${6:-}" == --pattern && "${8:-}" == --dir ]] || unexpected "$@"
    [[ "$5" == "${FAKE_REPO}" ]] || not_found
    [[ -f "${FAKE_ASSETS}/$3/$7" ]] || { echo "no assets match the file pattern" >&2; exit 1; }
    cp "${FAKE_ASSETS}/$3/$7" "$9"
    ;;
  *) unexpected "$@" ;;
esac
FAKE
chmod +x "${WORK}/bin/gh"
export PATH="${WORK}/bin:${PATH}"

check() {
  local name=$1 expected_status=$2 expected_output=$3
  shift 3
  local output status
  output=$("$@" 2>&1)
  status=$?
  if [[ ${status} -eq ${expected_status} && "${output}" == *"${expected_output}"* ]]; then
    PASSED=$((PASSED + 1))
  else
    FAILED=$((FAILED + 1))
    FAILURES+=("${name}: expected exit ${expected_status} and '${expected_output}', got exit ${status}: ${output}")
  fi
}

# releases <tag>:<final|pre|draft> ...: the repository's releases, newest first as the API lists them. A fresh start:
# no release.json downloaded, no calls logged.
releases() {
  local spec json="[]"
  for spec in "$@"; do
    json=$(jq -c --arg tag "${spec%%:*}" --arg kind "${spec#*:}" \
      '. + [{tag_name: $tag, draft: ($kind == "draft"), prerelease: ($kind == "pre")}]' <<<"${json}")
  done
  echo "${json}" > "${FAKE_RELEASES}"
  rm -rf "${FAKE_ASSETS:?}"/* "${OUT:?}"/*
  : > "${FAKE_CALLS}"
  FAKE_FAIL=""
}

# manifest <tag> [<tag in it> [<prerelease in it>]]: the release's release.json.
manifest() {
  mkdir -p "${FAKE_ASSETS}/$1"
  jq -n --arg tag "${2:-$1}" --argjson prerelease "${3:-false}" '{schemaVersion: 1, tag: $tag, prerelease: $prerelease}' \
    > "${FAKE_ASSETS}/$1/release.json"
}

latest() { "${LATEST}" "${FAKE_REPO}" "$1" "${OUT}"; }
stdout_of() { "${LATEST}" "${FAKE_REPO}" "$1" "${OUT}" 2>/dev/null; }
downloaded() { jq -r .tag "${OUT}/release.json"; }
downloads() { grep -c '^release download' "${FAKE_CALLS}" || true; }

# ---- latest moves ----------------------------------------------------------------------------------------------------

releases v4.0.0:final v4.0.0-rc.1:pre
manifest v4.0.0
check "the first final release" 0 "v4.0.0 is the newest final release: latest moves to its images." latest v4.0.0
check "its release.json downloaded" 0 "v4.0.0" downloaded
check "only move=true on stdout" 0 "" test "$(stdout_of v4.0.0)" = move=true

releases v4.0.10:final v4.0.9:final
manifest v4.0.10
check "v4.0.10 is newer than v4.0.9" 0 "move=true" latest v4.0.10
releases v4.9.0:final v4.10.0:final
manifest v4.10.0
check "v4.10.0 is newer than v4.9.0, whatever the order" 0 "move=true" latest v4.10.0

releases v5.0.0:draft v4.2.0-rc.1:pre v4.2.0:pre v9:final v4.1.0:final
manifest v4.1.0
check "drafts, prereleases and other tags are not newer" 0 "move=true" latest v4.1.0

# ---- latest stays ----------------------------------------------------------------------------------------------------

releases v4.0.0-rc.1:pre
manifest v4.0.0-rc.1 v4.0.0-rc.1 true
check "a prerelease before any final release" 0 "v4.0.0-rc.1 is a prerelease: latest stays where it is." latest v4.0.0-rc.1
check "a prerelease's stdout" 0 "" test "$(stdout_of v4.0.0-rc.1)" = move=false
check "a prerelease's release.json not downloaded" 0 "0" downloads

releases v4.1.0-rc.1:pre v4.0.0:final
check "a prerelease after a final release" 0 "move=false" latest v4.1.0-rc.1

releases v4.0.1:final v4.1.0:final v4.0.0:final
manifest v4.0.1
check "a fix to an older version" 0 "v4.0.1 is not the newest final release (the newest is v4.1.0): latest stays where it is." \
  latest v4.0.1
check "its release.json not downloaded" 0 "0" downloads

# The newest is on the second page: only --paginate finds it.
releases v4.0.1:final v4.0.0-rc.1:pre v4.1.0:final
check "the newest on another page" 0 "(the newest is v4.1.0)" latest v4.0.1

# ---- the step summary ------------------------------------------------------------------------------------------------

releases v4.0.0:final
manifest v4.0.0
summary="${WORK}/summary.md"
: > "${summary}"
GITHUB_STEP_SUMMARY="${summary}" "${LATEST}" "${FAKE_REPO}" v4.0.0 "${OUT}" >/dev/null 2>&1
check "the step summary says why" 0 "v4.0.0 is the newest final release" cat "${summary}"

# ---- refused ---------------------------------------------------------------------------------------------------------

releases v4.0.0:draft
check "a draft" 1 "::error::No published release for v4.0.0." latest v4.0.0
releases v4.0.0:final
check "no release" 1 "No published release for v4.0.1." latest v4.0.1

releases v4.0.0-rc.2:final v4.0.0-rc.1:pre
manifest v4.0.0-rc.2 v4.0.0-rc.2 true
check "a candidate not marked a prerelease" 1 \
  "v4.0.0-rc.2 is a prerelease's tag, but its release is not marked a prerelease: latest stays where it is." \
  latest v4.0.0-rc.2
check "its release.json not downloaded" 0 "0" downloads

releases v4.0.0:final
manifest v4.0.0 v4.0.1
check "another release's release.json" 1 "v4.0.0's release.json is for v4.0.1." latest v4.0.0
manifest v4.0.0 v4.0.0 true
check "a prerelease's release.json" 1 "v4.0.0's release.json says it is a prerelease, but its release is not marked one." \
  latest v4.0.0
mkdir -p "${FAKE_ASSETS}/v4.0.0"
printf 'not json' > "${FAKE_ASSETS}/v4.0.0/release.json"
check "a release.json that is not JSON" 1 "v4.0.0's release.json is not JSON." latest v4.0.0
rm -f "${FAKE_ASSETS}/v4.0.0/release.json"
check "no release.json" 1 "Cannot download v4.0.0's release.json." latest v4.0.0
check "nothing on stdout when refused" 0 "" test -z "$(stdout_of v4.0.0)"

releases v4.0.0:final
FAKE_FAIL=list
check "the releases cannot be listed" 1 "Cannot list ${FAKE_REPO}'s releases." latest v4.0.0

releases v4.0.0:final
check "a version, not a tag" 1 "'4.0.0' is not a release tag." latest 4.0.0
check "two parts" 1 "'v4.0' is not a release tag." latest v4.0
check "not a repository" 1 "'HVO.RoofController' is not <owner>/<repository>." "${LATEST}" HVO.RoofController v4.0.0 "${OUT}"
check "another repository" 1 "No published release for v4.0.0." "${LATEST}" someone/else v4.0.0 "${OUT}"
check "no folder" 1 "${WORK}/none is not a folder." "${LATEST}" "${FAKE_REPO}" v4.0.0 "${WORK}/none"
check "no arguments" 2 "build/release-latest.sh <owner>/<repository> <tag> <dir>" "${LATEST}"

# ---------------------------------------------------------------------------------------------------------------------

echo "${PASSED} passed, ${FAILED} failed"
for failure in "${FAILURES[@]+"${FAILURES[@]}"}"; do
  echo "  ${failure}"
done
[[ ${FAILED} -eq 0 ]]
