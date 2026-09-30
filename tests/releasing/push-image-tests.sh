#!/usr/bin/env bash
# Tests for build/push-image.sh (docs/releasing.md): it pushes OCI archives made here, of two platforms like the
# release's images, to a registry of its own in Docker (registry:3, on 127.0.0.1 over HTTP), and tags them again.
# Requires bash, Docker, skopeo, curl, tar, gzip, jq and sha256sum.
# Run: tests/releasing/push-image-tests.sh
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PUSH="${TESTS_DIR}/../../build/push-image.sh"
REGISTRY_IMAGE="registry:3@sha256:ddf754342cfc8acc51a56d5d0ab6af06826461864460636d8bd5c546dab2a7b8"

for tool in docker skopeo curl tar gzip jq sha256sum; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "push-image-tests.sh needs ${tool}." >&2; exit 1; }
done

PASSED=0
FAILED=0
FAILURES=()

WORK=$(mktemp -d)
registry=""
cleanup() {
  [[ -z "${registry}" ]] || docker rm -f "${registry}" >/dev/null 2>&1
  rm -rf "${WORK}"
}
trap cleanup EXIT

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

# put <dir> <file> <media type>: stores the file as a blob and prints its descriptor.
put() {
  local dir=$1 file=$2 type=$3 digest
  digest=$(sha256sum "${file}" | cut -d' ' -f1)
  cp "${file}" "${dir}/blobs/sha256/${digest}"
  jq -cn --arg type "${type}" --arg digest "sha256:${digest}" --argjson size "$(stat -c %s "${file}")" \
    '{mediaType: $type, digest: $digest, size: $size}'
}

# archive <name> <text>: an OCI archive as buildx writes it for linux/amd64 and linux/arm64, an index.json listing one
# index over one image per platform, each image a layer holding <text>; prints its path.
archive() {
  local name=$1 text=$2 dir="${WORK}/${1}" platform layer config manifests="[]" descriptor
  mkdir -p "${dir}/blobs/sha256" "${dir}.files"
  for platform in linux/amd64 linux/arm64; do
    printf '%s %s\n' "${text}" "${platform}" > "${dir}.files/greeting"
    tar -cf "${dir}.layer.tar" -C "${dir}.files" --owner=0 --group=0 --mtime=@0 greeting
    gzip -n -c "${dir}.layer.tar" > "${dir}.layer.tar.gz"
    layer=$(put "${dir}" "${dir}.layer.tar.gz" application/vnd.oci.image.layer.v1.tar+gzip)
    jq -cn --arg os "${platform%/*}" --arg arch "${platform#*/}" \
      --arg diff "sha256:$(sha256sum "${dir}.layer.tar" | cut -d' ' -f1)" \
      '{os: $os, architecture: $arch, config: {}, rootfs: {type: "layers", diff_ids: [$diff]}}' > "${dir}.config"
    config=$(put "${dir}" "${dir}.config" application/vnd.oci.image.config.v1+json)
    jq -cn --argjson config "${config}" --argjson layer "${layer}" \
      '{schemaVersion: 2, mediaType: "application/vnd.oci.image.manifest.v1+json", config: $config, layers: [$layer]}' \
      > "${dir}.manifest"
    descriptor=$(put "${dir}" "${dir}.manifest" application/vnd.oci.image.manifest.v1+json)
    manifests=$(jq -c --argjson descriptor "${descriptor}" --arg os "${platform%/*}" --arg arch "${platform#*/}" \
      '. + [$descriptor + {platform: {os: $os, architecture: $arch}}]' <<<"${manifests}")
  done
  jq -cn --argjson manifests "${manifests}" \
    '{schemaVersion: 2, mediaType: "application/vnd.oci.image.index.v1+json", manifests: $manifests}' > "${dir}.index"
  descriptor=$(put "${dir}" "${dir}.index" application/vnd.oci.image.index.v1+json)
  jq -cn --argjson descriptor "${descriptor}" '{schemaVersion: 2, manifests: [$descriptor]}' > "${dir}/index.json"
  printf '{"imageLayoutVersion":"1.0.0"}' > "${dir}/oci-layout"
  tar -cf "${dir}.tar" -C "${dir}" oci-layout index.json blobs
  echo "${dir}.tar"
}

# The digest index.json names, and the digest the registry holds for a reference.
archive_digest() { tar -xOf "$1" index.json | jq -r '.manifests[0].digest'; }
registry_digest() {
  echo "sha256:$(skopeo inspect --tls-verify=false --raw "docker://$1" | sha256sum | cut -d' ' -f1)"
}

registry=$(docker run -d -p 127.0.0.1::5000 "${REGISTRY_IMAGE}") || { echo "cannot start ${REGISTRY_IMAGE}." >&2; exit 1; }
port=$(docker port "${registry}" 5000/tcp | head -n 1 | sed 's/.*://')
REPO="127.0.0.1:${port}/hvo/roof-controller"
for _ in $(seq 1 50); do
  curl -fsS "http://127.0.0.1:${port}/v2/" >/dev/null 2>&1 && break
  sleep 0.2
done

first=$(archive first "Hello from 4.0.0")
second=$(archive second "Hello from 4.0.1")
digest=$(archive_digest "${first}")

# ---- an archive ------------------------------------------------------------------------------------------------------

check "pushed" 0 "${digest}" "${PUSH}" --insecure "${first}" "${REPO}:4.0.0"
check "the tag names the archive's digest" 0 "${digest}" registry_digest "${REPO}:4.0.0"
for platform in amd64 arm64; do
  platform_digest=$(tar -xOf "${first}" "blobs/sha256/${digest#sha256:}" |
    jq -r --arg arch "${platform}" '.manifests[] | select(.platform.architecture == $arch) | .digest')
  check "the ${platform} image is there with its own digest" 0 "${platform_digest}" registry_digest "${REPO}@${platform_digest}"
done
check "pushed again" 0 "${digest}" "${PUSH}" --insecure "${first}" "${REPO}:4.0.0"
check "a registry that needs TLS" 1 "cannot push ${REPO}:4.0.0" "${PUSH}" "${first}" "${REPO}:4.0.0"

printf 'not a tar' > "${WORK}/bad.tar"
check "not an archive" 1 "is not an OCI archive" "${PUSH}" --insecure "${WORK}/bad.tar" "${REPO}:4.0.0"
check "no archive" 1 "is not a file" "${PUSH}" --insecure "${WORK}/none.tar" "${REPO}:4.0.0"
mkdir -p "${WORK}/two"
tar -xf "${first}" -C "${WORK}/two"
jq '.manifests += .manifests' "${WORK}/two/index.json" > "${WORK}/two/index.json.new"
mv "${WORK}/two/index.json.new" "${WORK}/two/index.json"
tar -cf "${WORK}/two.tar" -C "${WORK}/two" oci-layout index.json blobs
check "two images in the archive" 1 "holds 2 images, not one" "${PUSH}" --insecure "${WORK}/two.tar" "${REPO}:4.0.0"
check "no tag" 1 "is not <repository>:<tag>" "${PUSH}" --insecure "${first}" "${REPO}"
check "an upper-case repository" 1 "is not <repository>:<tag>" "${PUSH}" --insecure "${first}" "127.0.0.1:${port}/HVO/roof:4.0.0"
check "no arguments" 2 "build/push-image.sh [--insecure] <oci archive> <repository>:<tag>" "${PUSH}"

# ---- another tag -----------------------------------------------------------------------------------------------------

check "tagged latest" 0 "${digest}" "${PUSH}" --insecure --tag "${REPO}:4.0.0@${digest}" latest
check "latest names the release's digest" 0 "${digest}" registry_digest "${REPO}:latest"
check "the version tag is unchanged" 0 "${digest}" registry_digest "${REPO}:4.0.0"

# The version tag pushed again with another image after the release: latest stays where it was.
"${PUSH}" --insecure "${second}" "${REPO}:4.0.0" >/dev/null 2>&1
check "a version tag pushed again" 1 "${REPO}:4.0.0 is $(archive_digest "${second}") in the registry, not ${digest}" \
  "${PUSH}" --insecure --tag "${REPO}:4.0.0@${digest}" latest
check "latest not moved" 0 "${digest}" registry_digest "${REPO}:latest"

check "a version not pushed" 1 "cannot read ${REPO}:9.9.9" "${PUSH}" --insecure --tag "${REPO}:9.9.9@${digest}" latest
check "no digest" 1 "must name the image's tag and digest" "${PUSH}" --insecure --tag "${REPO}:4.0.0" latest
check "a short digest" 1 "is not <repository>:<tag>@sha256:<hex>" "${PUSH}" --insecure --tag "${REPO}:4.0.0@sha256:abc" latest
check "a new tag that is not one" 1 "'la/test' is not a tag" "${PUSH}" --insecure --tag "${REPO}:4.0.0@${digest}" la/test
check "no new tag" 2 "build/push-image.sh [--insecure] --tag" "${PUSH}" --insecure --tag "${REPO}:4.0.0@${digest}"

# ---------------------------------------------------------------------------------------------------------------------

echo "${PASSED} passed, ${FAILED} failed"
for failure in "${FAILURES[@]+"${FAILURES[@]}"}"; do
  echo "  ${failure}"
done
[[ ${FAILED} -eq 0 ]]
