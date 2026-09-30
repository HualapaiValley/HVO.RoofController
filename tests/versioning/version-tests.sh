#!/usr/bin/env bash
# Tests for build/version.sh (the product version) and build/check-image-labels.sh (an image's version labels). Each
# runs against a temporary copy: version.sh with its own Directory.Build.props, check-image-labels.sh with OCI archives
# made here, so no .NET, Docker or network is needed. Requires bash, tar, jq and sha256sum or shasum.
# Run: tests/versioning/version-tests.sh
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
BUILD_DIR="${TESTS_DIR}/../../build"
REVISION=0123456789abcdef0123456789abcdef01234567
SOURCE="https://github.com/HualapaiValley/HVO.RoofController"

PASSED=0
FAILED=0
FAILURES=()

WORK=$(mktemp -d)
trap 'rm -rf "${WORK}"' EXIT

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

# ---- version.sh ------------------------------------------------------------------------------------------------------

# A copy of version.sh beside a Directory.Build.props holding <props>.
version_sh() {
  local props=$1 dir
  dir=$(mktemp -d "${WORK}/repo.XXXXXX")
  mkdir -p "${dir}/build"
  cp "${BUILD_DIR}/version.sh" "${dir}/build/"
  printf '<Project>\n  <PropertyGroup>\n%s\n  </PropertyGroup>\n</Project>\n' "${props}" > "${dir}/Directory.Build.props"
  echo "${dir}/build/version.sh"
}

v=$(version_sh '    <VersionPrefix>4.2.1</VersionPrefix>')
check "prefix" 0 "4.2.1" "${v}"
check "dev" 0 "4.2.1-dev" "${v}" --dev
check "ci" 0 "4.2.1-ci.57" "${v}" --ci 57
check "release tag" 0 "4.2.1" "${v}" --tag v4.2.1
check "release candidate tag" 0 "4.2.1-rc.3" "${v}" --tag v4.2.1-rc.3
check "tag for another version" 1 "the tag 'v4.3.0' is for 4.3.0, but Directory.Build.props holds 4.2.1" "${v}" --tag v4.3.0
check "tag without v" 1 "is not a release tag" "${v}" --tag 4.2.1
check "other prerelease tag" 1 "is not a release tag" "${v}" --tag v4.2.1-beta.1
check "rc zero" 1 "is not a release tag" "${v}" --tag v4.2.1-rc.0
check "leading zero" 1 "is not a release tag" "${v}" --tag v4.02.1
check "tag with build metadata" 1 "is not a release tag" "${v}" --tag v4.2.1+abc
check "ci zero" 1 "is not a positive whole number" "${v}" --ci 0
check "ci not a number" 1 "is not a positive whole number" "${v}" --ci 5a
check "ci without a number" 2 "build/version.sh --ci <run>" "${v}" --ci
check "unknown option" 2 "build/version.sh --tag <tag>" "${v}" --release
check "extra argument" 2 "build/version.sh --dev" "${v}" --dev extra

v=$(version_sh '    <VersionPrefix>4.2</VersionPrefix>')
check "prefix not x.y.z" 1 "the VersionPrefix '4.2' is not a release version" "${v}"
v=$(version_sh '    <VersionPrefix>4.2.1-rc.1</VersionPrefix>')
check "prefix with suffix" 1 "is not a release version" "${v}"
v=$(version_sh '    <VersionSuffix>dev</VersionSuffix>')
check "no prefix" 1 "must hold exactly one VersionPrefix" "${v}"
v=$(version_sh $'    <VersionPrefix>4.2.1</VersionPrefix>\n    <VersionPrefix>4.2.2</VersionPrefix>')
check "two prefixes" 1 "must hold exactly one VersionPrefix" "${v}"

# The repository's own props: a release version, the first of them 4.0.0 or later.
check "repository prefix" 0 "4." "${BUILD_DIR}/version.sh"

# ---- check-image-labels.sh -------------------------------------------------------------------------------------------

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum | cut -d' ' -f1; else shasum -a 256 | cut -d' ' -f1; fi
}

# put <dir> <json>: stores a blob and prints its digest.
put() {
  local dir=$1 json=$2 digest
  digest=$(printf '%s' "${json}" | sha256)
  printf '%s' "${json}" > "${dir}/blobs/sha256/${digest}"
  echo "sha256:${digest}"
}

# archive <name> <labels-json> [platform...]: an OCI archive like buildx writes, an index of a multi-platform index
# holding one image per platform (default linux/amd64) and an attestation manifest for each, and prints its path.
archive() {
  local name=$1 labels=$2 dir="${WORK}/${1}" platform config manifest manifests="[]" index
  shift 2
  [[ $# -gt 0 ]] || set -- linux/amd64
  mkdir -p "${dir}/blobs/sha256"
  for platform in "$@"; do
    config=$(put "${dir}" "$(jq -cn --arg os "${platform%/*}" --arg arch "${platform#*/}" --argjson labels "${labels}" \
      '{os: $os, architecture: $arch, config: {Labels: $labels}}')")
    manifest=$(put "${dir}" "$(jq -cn --arg config "${config}" \
      '{mediaType: "application/vnd.oci.image.manifest.v1+json", config: {digest: $config}, layers: []}')")
    manifests=$(jq -c --arg digest "${manifest}" --arg platform "${platform}" \
      '. + [{mediaType: "application/vnd.oci.image.manifest.v1+json", digest: $digest}]' <<<"${manifests}")
    config=$(put "${dir}" '{"os":"unknown","architecture":"unknown","config":{}}')
    manifest=$(put "${dir}" "$(jq -cn --arg config "${config}" '{config: {digest: $config}, layers: []}')")
    manifests=$(jq -c --arg digest "${manifest}" '. + [{mediaType: "application/vnd.oci.image.manifest.v1+json",
      digest: $digest, annotations: {"vnd.docker.reference.type": "attestation-manifest"}}]' <<<"${manifests}")
  done
  index=$(put "${dir}" "$(jq -cn --argjson manifests "${manifests}" '{manifests: $manifests}')")
  jq -cn --arg index "${index}" '{manifests: [{mediaType: "application/vnd.oci.image.index.v1+json", digest: $index}]}' \
    > "${dir}/index.json"
  tar -cf "${dir}.tar" -C "${dir}" index.json blobs
  echo "${dir}.tar"
}

labels() {
  jq -cn --arg version "$1" --arg revision "$2" --arg source "$3" --arg created "$4" --arg title "${5-HVO Roof Controller}" \
    '{"org.opencontainers.image.version": $version, "org.opencontainers.image.revision": $revision,
      "org.opencontainers.image.source": $source, "org.opencontainers.image.created": $created,
      "org.opencontainers.image.title": $title}'
}

good=$(labels 4.0.0 "${REVISION}" "${SOURCE}" 2026-09-30T12:00:00Z)
labels_sh="${BUILD_DIR}/check-image-labels.sh"
a=$(archive two-platforms "${good}" linux/amd64 linux/arm64)
check "both platforms checked" 0 "linux/arm64: HVO Roof Controller 4.0.0, commit ${REVISION}" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
check "amd64 checked" 0 "linux/amd64: HVO Roof Controller 4.0.0" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
check "wrong version" 1 "linux/amd64: the version label is '4.0.0', not '4.0.1'" "${labels_sh}" "${a}" 4.0.1 "${REVISION}"
check "wrong revision" 1 "the revision label is '${REVISION}', not 'abc'" "${labels_sh}" "${a}" 4.0.0 abc
a=$(archive fractional "$(labels 4.0.0-ci.3 "${REVISION}" "${SOURCE}" 2026-09-30T12:00:00.123+02:00)")
check "fractional created time" 0 "4.0.0-ci.3" "${labels_sh}" "${a}" 4.0.0-ci.3 "${REVISION}"
a=$(archive no-version "$(labels "" "${REVISION}" "${SOURCE}" 2026-09-30T12:00:00Z)")
check "empty version label" 1 "the version label is '', not '4.0.0'" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
a=$(archive other-source "$(labels 4.0.0 "${REVISION}" https://github.com/someone/fork 2026-09-30T12:00:00Z)")
check "other source" 1 "the source label is 'https://github.com/someone/fork'" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
a=$(archive no-created "$(labels 4.0.0 "${REVISION}" "${SOURCE}" "")")
check "no created time" 1 "the created label '' is not an RFC 3339 time" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
a=$(archive no-title "$(labels 4.0.0 "${REVISION}" "${SOURCE}" 2026-09-30T12:00:00Z "")")
check "no title" 1 "the title label is empty" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
check "missing archive" 1 "was not found" "${labels_sh}" "${WORK}/none.tar" 4.0.0 "${REVISION}"
printf 'not a tar' > "${WORK}/bad.tar"
check "not an archive" 1 "is not an OCI archive" "${labels_sh}" "${WORK}/bad.tar" 4.0.0 "${REVISION}"
check "empty version argument" 1 "must not be empty" "${labels_sh}" "${a}" "" "${REVISION}"
check "missing arguments" 2 "check-image-labels.sh <oci-archive> <version> <revision> [<platforms>]" "${labels_sh}" "${a}"

# The platforms the archive must hold, in any order.
a=$(archive platforms "${good}" linux/amd64 linux/arm64)
check "expected platforms" 0 "linux/arm64: HVO Roof Controller 4.0.0" "${labels_sh}" "${a}" 4.0.0 "${REVISION}" linux/amd64,linux/arm64
check "expected platforms in another order" 0 "linux/amd64: HVO Roof Controller 4.0.0" "${labels_sh}" "${a}" 4.0.0 "${REVISION}" linux/arm64,linux/amd64
check "a platform not expected" 1 "holds images for linux/amd64,linux/arm64, not linux/arm64" "${labels_sh}" "${a}" 4.0.0 "${REVISION}" linux/arm64
a=$(archive one-platform "${good}" linux/amd64)
check "an expected platform missing" 1 "holds images for linux/amd64, not linux/amd64,linux/arm64" "${labels_sh}" "${a}" 4.0.0 "${REVISION}" linux/amd64,linux/arm64
check "platforms not a list" 1 "the platforms must be a list such as linux/amd64,linux/arm64, not 'arm64'" "${labels_sh}" "${a}" 4.0.0 "${REVISION}" arm64

# with_entry <archive> <name> <entry-json>: a copy of the archive whose top-level index also lists the entry.
with_entry() {
  local dir="${WORK}/$2"
  rm -rf "${dir}" && mkdir -p "${dir}" && tar -xf "$1" -C "${dir}"
  jq -c --argjson entry "$3" '.manifests += [$entry]' "${dir}/index.json" > "${dir}/index.json.new"
  mv "${dir}/index.json.new" "${dir}/index.json"
  tar -cf "${dir}.tar" -C "${dir}" index.json blobs
  echo "${dir}.tar"
}
missing=sha256:$(printf 'missing' | sha256)
a=$(with_entry "${WORK}/platforms.tar" missing-index \
  "{\"mediaType\": \"application/vnd.oci.image.index.v1+json\", \"digest\": \"${missing}\"}")
check "a nested index missing" 1 "blob ${missing} is missing from the archive" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
a=$(with_entry "${WORK}/platforms.tar" missing-manifest \
  "{\"mediaType\": \"application/vnd.oci.image.manifest.v1+json\", \"digest\": \"${missing}\"}")
check "an image manifest missing" 1 "blob ${missing} is missing from the archive" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"
a=$(with_entry "${WORK}/platforms.tar" other-type \
  "{\"mediaType\": \"application/vnd.example.thing+json\", \"digest\": \"${missing}\"}")
check "an unknown media type" 1 "has the media type 'application/vnd.example.thing+json'" "${labels_sh}" "${a}" 4.0.0 "${REVISION}"

# ---------------------------------------------------------------------------------------------------------------------

echo "${PASSED} passed, ${FAILED} failed"
for failure in "${FAILURES[@]+"${FAILURES[@]}"}"; do
  echo "  ${failure}"
done
[[ ${FAILED} -eq 0 ]]
