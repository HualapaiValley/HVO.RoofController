#!/usr/bin/env bash
# Pushes a release's images to the registry with the digests they were checked with (docs/releasing.md).
#
#   build/push-image.sh [--insecure] <oci archive> <repository>:<tag>
#   build/push-image.sh [--insecure] --tag <repository>:<tag>@sha256:<hex> <new tag>
#
# The first form pushes the image in an OCI archive, as `docker buildx build --output type=oci,dest=<file>` writes it,
# to <repository>:<tag>: every platform's image and the index over them, each with the digest it has in the archive.
# The second gives an image already pushed another tag (latest, when a release is published), once it has checked that
# <tag> still names that digest. Either way it reads the new tag back, fails unless it names the image's digest, and
# prints the digest.
#
# --insecure is for a test's local registry over HTTP. Needs skopeo, jq, tar and sha256sum; skopeo uses the login that
# docker login (or docker/login-action) stores.
set -euo pipefail

usage() {
  sed -n '2,5p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

fail() {
  echo "push-image.sh: $*" >&2
  exit 1
}

verify=true
if [[ "${1:-}" == "--insecure" ]]; then
  verify=false
  shift
fi

REFERENCE='^[a-z0-9][a-z0-9._:-]*(/[a-z0-9][a-z0-9._-]*)+$'
TAG='^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$'
DIGEST='^sha256:[0-9a-f]{64}$'

# The digest <reference> (a tag, or @sha256:<hex>) names in the registry: the SHA-256 of its manifest as stored.
registry_digest() {
  local manifest
  manifest=$(mktemp)
  if ! skopeo inspect --tls-verify="${verify}" --retry-times 3 --raw "docker://$1" > "${manifest}"; then
    rm -f "${manifest}"
    fail "cannot read $1 from the registry."
  fi
  echo "sha256:$(sha256sum "${manifest}" | cut -d' ' -f1)"
  rm -f "${manifest}"
}

# Copies <source> to docker://<destination>, every platform, keeping every digest, then checks the destination.
copy() {
  local source=$1 destination=$2 digest=$3 pushed
  skopeo copy --src-tls-verify="${verify}" --dest-tls-verify="${verify}" --retry-times 3 --all --preserve-digests \
    "${source}" "docker://${destination}" >&2 || fail "cannot push ${destination}."
  pushed=$(registry_digest "${destination}")
  [[ "${pushed}" == "${digest}" ]] || fail "${destination} is ${pushed} in the registry, not ${digest}."
  echo "${digest}"
}

if [[ "${1:-}" == "--tag" ]]; then
  [[ $# -eq 3 ]] || usage
  image=$2 new=$3
  [[ "${image}" == *@* ]] || fail "'${image}' must name the image's tag and digest: <repository>:<tag>@sha256:<hex>."
  digest=${image##*@} named=${image%@*}
  repository=${named%:*} tag=${named##*:}
  [[ "${named}" == *:* && "${repository}" =~ ${REFERENCE} && "${tag}" =~ ${TAG} && "${digest}" =~ ${DIGEST} ]] ||
    fail "'${image}' is not <repository>:<tag>@sha256:<hex>."
  [[ "${new}" =~ ${TAG} ]] || fail "'${new}' is not a tag."
  current=$(registry_digest "${named}")
  [[ "${current}" == "${digest}" ]] ||
    fail "${named} is ${current} in the registry, not ${digest}: it was pushed again after the release. Nothing is tagged."
  copy "docker://${repository}@${digest}" "${repository}:${new}" "${digest}"
  exit 0
fi

[[ $# -eq 2 ]] || usage
archive=$1 destination=$2
repository=${destination%:*} tag=${destination##*:}
[[ "${destination}" == *:* && "${repository}" =~ ${REFERENCE} && "${tag}" =~ ${TAG} ]] ||
  fail "'${destination}' is not <repository>:<tag>."
[[ -f "${archive}" ]] || fail "${archive} is not a file."

index=$(tar -xOf "${archive}" index.json 2>/dev/null) || fail "${archive} is not an OCI archive."
count=$(jq '.manifests | length' <<<"${index}" 2>/dev/null) || fail "${archive}'s index.json cannot be read."
[[ "${count}" == 1 ]] || fail "${archive} holds ${count} images, not one."
digest=$(jq -r '.manifests[0].digest' <<<"${index}")
[[ "${digest}" =~ ${DIGEST} ]] || fail "${archive}'s image has no sha256 digest ('${digest}')."

copy "oci-archive:${archive}" "${destination}" "${digest}"
