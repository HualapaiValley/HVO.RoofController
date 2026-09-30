#!/usr/bin/env bash
# Decides whether publishing a release moves the images' latest tag (docs/releasing.md#publishing).
#
#   build/release-latest.sh <owner>/<repository> <tag> <dir>
#
# Prints move=true when the release for <tag> is published, final, and the newest final release, and downloads its
# release.json into <dir>, checked to be that release's; prints move=false for a prerelease or an older version's fix.
# The newest is the highest vX.Y.Z of the published final releases, not the release GitHub marks latest: GitHub marks a
# newly published release latest unless told otherwise, even a fix to an older version. It says why on stderr, and in
# the step summary when there is one. Refuses a final release whose tag is a prerelease's, and a release.json that is
# not the release's.
#
# Needs gh, logged in or with GH_TOKEN, and jq.
set -euo pipefail

usage() {
  sed -n '4p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

fail() {
  echo "::error::$*" >&2
  exit 1
}

# A line for the log and for the step summary.
note() {
  echo "$*" >&2
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    echo "$*" >> "${GITHUB_STEP_SUMMARY}"
  fi
}

[[ $# -eq 3 ]] || usage
repository=$1
tag=$2
dir=$3

[[ "${repository}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "'${repository}' is not <owner>/<repository>."
[[ "${tag}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || fail "'${tag}' is not a release tag."
[[ -d "${dir}" ]] || fail "${dir} is not a folder."

# Only a published release: the API gives no draft by its tag.
release=$(gh api "repos/${repository}/releases/tags/${tag}") || fail "No published release for ${tag}."

if [[ "$(jq -r .prerelease <<<"${release}")" == true ]]; then
  note "${tag} is a prerelease: latest stays where it is."
  echo "move=false"
  exit 0
fi
if [[ "${tag}" == *-* ]]; then
  fail "${tag} is a prerelease's tag, but its release is not marked a prerelease: latest stays where it is. Mark it one (gh release edit ${tag} --prerelease)."
fi

# Every published final release's version, one per line: --jq runs on each page.
finals=$(gh api --paginate "repos/${repository}/releases" \
  --jq '.[] | select((.draft or .prerelease) | not) | .tag_name | select(test("^v[0-9]+\\.[0-9]+\\.[0-9]+$"))') ||
  fail "Cannot list ${repository}'s releases."
newest=$(sort -V <<<"${finals}" | tail -n 1)
if [[ "${newest}" != "${tag}" ]]; then
  note "${tag} is not the newest final release (the newest is ${newest:-not listed}): latest stays where it is."
  echo "move=false"
  exit 0
fi

gh release download "${tag}" --repo "${repository}" --pattern release.json --dir "${dir}" --clobber >&2 ||
  fail "Cannot download ${tag}'s release.json."
manifest="${dir}/release.json"
listed=$(jq -r .tag "${manifest}") || fail "${tag}'s release.json is not JSON."
[[ "${listed}" == "${tag}" ]] || fail "${tag}'s release.json is for ${listed}."
[[ "$(jq -r .prerelease "${manifest}")" == false ]] ||
  fail "${tag}'s release.json says it is a prerelease, but its release is not marked one."

note "${tag} is the newest final release: latest moves to its images."
echo "move=true"
