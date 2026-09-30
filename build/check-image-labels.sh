#!/usr/bin/env bash
# Checks the OCI labels of every platform image in an OCI archive (docker buildx build --output type=oci,dest=<file>):
# the version and commit it was built for, the repository it came from, and a title and creation time. Given platforms
# (linux/amd64,linux/arm64), the archive must hold an image for exactly those.
#
#   build/check-image-labels.sh <oci-archive> <version> <revision> [<platforms>]
#
# Needs tar and jq. The Dockerfiles set the labels from ROOF_VERSION, ROOF_REVISION and ROOF_CREATED (docs/releasing.md).
set -euo pipefail

SOURCE="https://github.com/HualapaiValley/HVO.RoofController"

fail() {
  echo "check-image-labels.sh: $*" >&2
  exit 1
}

[[ $# -eq 3 || $# -eq 4 ]] || { sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
archive=$1 version=$2 revision=$3 platforms=${4-}
[[ -f "${archive}" ]] || fail "${archive} was not found."
[[ -n "${version}" && -n "${revision}" ]] || fail "the version and revision must not be empty."
[[ $# -eq 3 || "${platforms}" =~ ^[a-z0-9]+/[a-z0-9]+(,[a-z0-9]+/[a-z0-9]+)*$ ]] \
  || fail "the platforms must be a list such as linux/amd64,linux/arm64, not '${platforms}'."

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

# Adds the image manifests reachable from an index to image_manifests, through nested indexes, skipping attestation
# manifests. No pipeline or command substitution hides a failure: a blob it cannot read, or a manifest of a type it
# does not know, stops the check.
image_manifests=()
collect() {
  local index=$1 entries media digest nested
  entries=$(jq -r '.manifests[] | select(.annotations["vnd.docker.reference.type"] != "attestation-manifest")
                   | "\(.mediaType) \(.digest)"' <<<"${index}") || fail "an index in ${archive} cannot be read."
  while read -r media digest; do
    [[ -n "${media}" ]] || continue
    case "${media}" in
      application/vnd.oci.image.index.v1+json | application/vnd.docker.distribution.manifest.list.v2+json)
        nested=$(blob "${digest}") || exit 1
        collect "${nested}" ;;
      application/vnd.oci.image.manifest.v1+json | application/vnd.docker.distribution.manifest.v2+json)
        image_manifests+=("${digest}") ;;
      *)
        fail "${digest} has the media type '${media}', which is neither an image nor an index." ;;
    esac
  done <<<"${entries}"
}

collect "$(cat "${work}/index.json")"

checked=()
for digest in ${image_manifests[@]+"${image_manifests[@]}"}; do
  manifest=$(blob "${digest}") || exit 1
  config_digest=$(jq -r '.config.digest' <<<"${manifest}") || fail "the manifest ${digest} cannot be read."
  config=$(blob "${config_digest}") || exit 1
  platform=$(jq -r '"\(.os)/\(.architecture)"' <<<"${config}") || fail "the image config ${config_digest} cannot be read."
  [[ "${platform}" != "unknown/unknown" ]] || continue
  label() { jq -r --arg name "org.opencontainers.image.$1" '.config.Labels[$name] // ""' <<<"${config}"; }

  [[ "$(label version)" == "${version}" ]] || fail "${platform}: the version label is '$(label version)', not '${version}'."
  [[ "$(label revision)" == "${revision}" ]] || fail "${platform}: the revision label is '$(label revision)', not '${revision}'."
  [[ "$(label source)" == "${SOURCE}" ]] || fail "${platform}: the source label is '$(label source)', not ${SOURCE}."
  [[ -n "$(label title)" ]] || fail "${platform}: the title label is empty."
  [[ "$(label created)" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})$ ]] \
    || fail "${platform}: the created label '$(label created)' is not an RFC 3339 time."
  echo "${platform}: $(label title) ${version}, commit ${revision}, created $(label created)"
  checked+=("${platform}")
done

[[ ${#checked[@]} -gt 0 ]] || fail "${archive} holds no platform image."
if [[ -n "${platforms}" ]]; then
  held=$(printf '%s\n' "${checked[@]}" | sort | paste -sd, -)
  wanted=$(tr ',' '\n' <<<"${platforms}" | sort -u | paste -sd, -)
  [[ "${held}" == "${wanted}" ]] || fail "${archive} holds images for ${held}, not ${wanted}."
fi
