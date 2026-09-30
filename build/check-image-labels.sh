#!/usr/bin/env bash
# Checks the OCI labels of every platform image in an OCI archive (docker buildx build --output type=oci,dest=<file>):
# the version and commit it was built for, the repository it came from, and a title and creation time.
#
#   build/check-image-labels.sh <oci-archive> <version> <revision>
#
# Needs tar and jq. The Dockerfiles set the labels from ROOF_VERSION, ROOF_REVISION and ROOF_CREATED (docs/releasing.md).
set -euo pipefail

SOURCE="https://github.com/HualapaiValley/HVO.RoofController"

fail() {
  echo "check-image-labels.sh: $*" >&2
  exit 1
}

[[ $# -eq 3 ]] || { sed -n '2,7p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
archive=$1 version=$2 revision=$3
[[ -f "${archive}" ]] || fail "${archive} was not found."
[[ -n "${version}" && -n "${revision}" ]] || fail "the version and revision must not be empty."

work=$(mktemp -d)
trap 'rm -rf "${work}"' EXIT
tar -xf "${archive}" -C "${work}" index.json blobs 2>/dev/null || fail "${archive} is not an OCI archive."

blob() {
  local digest=$1
  [[ "${digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "unexpected digest '${digest}'."
  local file="${work}/blobs/sha256/${digest#sha256:}"
  [[ -f "${file}" ]] || fail "blob ${digest} is missing from the archive."
  cat "${file}"
}

# The image manifests reachable from the archive's index, through nested indexes, skipping attestation manifests.
manifests() {
  local index=$1
  jq -r '.manifests[] | select(.annotations["vnd.docker.reference.type"] != "attestation-manifest")
         | "\(.mediaType) \(.digest)"' <<<"${index}" |
    while read -r media digest; do
      case "${media}" in
        application/vnd.oci.image.index.v1+json | application/vnd.docker.distribution.manifest.list.v2+json)
          manifests "$(blob "${digest}")" ;;
        application/vnd.oci.image.manifest.v1+json | application/vnd.docker.distribution.manifest.v2+json)
          echo "${digest}" ;;
      esac
    done
}

checked=0
while read -r digest; do
  config=$(blob "$(blob "${digest}" | jq -r '.config.digest')")
  platform=$(jq -r '"\(.os)/\(.architecture)"' <<<"${config}")
  [[ "${platform}" != "unknown/unknown" ]] || continue
  label() { jq -r --arg name "org.opencontainers.image.$1" '.config.Labels[$name] // ""' <<<"${config}"; }

  [[ "$(label version)" == "${version}" ]] || fail "${platform}: the version label is '$(label version)', not '${version}'."
  [[ "$(label revision)" == "${revision}" ]] || fail "${platform}: the revision label is '$(label revision)', not '${revision}'."
  [[ "$(label source)" == "${SOURCE}" ]] || fail "${platform}: the source label is '$(label source)', not ${SOURCE}."
  [[ -n "$(label title)" ]] || fail "${platform}: the title label is empty."
  [[ "$(label created)" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})$ ]] \
    || fail "${platform}: the created label '$(label created)' is not an RFC 3339 time."
  echo "${platform}: $(label title) ${version}, commit ${revision}, created $(label created)"
  checked=$((checked + 1))
done < <(manifests "$(cat "${work}/index.json")")

[[ ${checked} -gt 0 ]] || fail "${archive} holds no platform image."
