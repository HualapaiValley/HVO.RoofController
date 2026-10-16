#!/usr/bin/env bash
# Checks the base images of the controller's and the HAT emulator's Dockerfiles: every FROM names its image by tag and
# by the digest of its multi-platform index (image:tag@sha256:<digest>), so a rebuild of the same commit uses the same
# bases; the SDK's tag is the SDK src/global.json pins; and the two Dockerfiles use the same SDK and the same ASP.NET
# Core runtime, tag and digest. With --registry, it also reads each pinned digest from its registry, which must be an
# index holding linux/amd64 and linux/arm64. Reports every problem it finds; in GitHub Actions, also as annotations.
#
#   build/check-base-images.sh [--registry] [<repository root>]
#
# Needs jq, and docker buildx for --registry. Dependabot moves the tags and digests (.github/dependabot.yml).
set -euo pipefail

SDK=mcr.microsoft.com/dotnet/sdk
RUNTIME=mcr.microsoft.com/dotnet/aspnet
PLATFORMS=(linux/amd64 linux/arm64)
GLOBAL_JSON=src/global.json
DOCKERFILES=(src/HVO.RoofControllerV4.RPi/Dockerfile src/HVO.RoofControllerV4.Emulator/Dockerfile)

registry=0
if [[ "${1:-}" == --registry ]]; then
  registry=1
  shift
fi
[[ $# -le 1 && "${1:-}" != -* ]] || { sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
root=${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}

errors=0
# problem <file> <line, or empty> <message>
problem() {
  local file=$1 line=$2 message=$3
  if [[ "${GITHUB_ACTIONS:-}" == true ]]; then
    echo "::error file=${file}${line:+,line=${line}}::${message}"
  fi
  echo "check-base-images.sh: ${file}${line:+:${line}}: ${message}" >&2
  errors=$((errors + 1))
}

pin=""
if [[ ! -f "${root}/${GLOBAL_JSON}" ]]; then
  problem "${GLOBAL_JSON}" "" "was not found."
else
  pin=$(jq -r '.sdk.version // ""' "${root}/${GLOBAL_JSON}" 2>/dev/null) || pin=""
  [[ -n "${pin}" ]] || problem "${GLOBAL_JSON}" "" "has no sdk.version."
fi

# A tag cannot hold ':' or '/', so the image is everything before the last ':' ahead of the '@'. The digest is sha256,
# in lower case.
pinned='^([^@[:space:]]+):([A-Za-z0-9_][A-Za-z0-9_.-]{0,127})@sha256:[0-9a-f]{64}$'
# The first build and runtime image seen, and where: the others must be the same.
sdk_ref="" sdk_file="" sdk_line="" runtime_ref="" runtime_file="" runtime_line=""

for dockerfile in "${DOCKERFILES[@]}"; do
  if [[ ! -f "${root}/${dockerfile}" ]]; then
    problem "${dockerfile}" "" "was not found."
    continue
  fi
  stages=" scratch " number=0 sdk_seen=0 runtime_seen=0
  while IFS= read -r text || [[ -n "${text}" ]]; do
    number=$((number + 1))
    [[ "${text}" =~ ^[[:space:]]*[Ff][Rr][Oo][Mm][[:space:]]+(.*)$ ]] || continue
    read -r -a words <<<"${BASH_REMATCH[1]}"
    ref="" stage=""
    for ((i = 0; i < ${#words[@]}; i++)); do
      if [[ -z "${ref}" && "${words[i]}" == --* ]]; then
        continue
      elif [[ -z "${ref}" ]]; then
        ref=${words[i]}
      elif [[ "${words[i]}" =~ ^[Aa][Ss]$ ]] && ((i + 1 < ${#words[@]})); then
        stage=${words[i + 1]}
        break
      fi
    done
    # An earlier stage of this Dockerfile, or scratch, is not a base image to pin.
    if [[ -n "${ref}" && "${stages}" != *" ${ref} "* ]]; then
      image=${ref%%@*}
      [[ "${image##*/}" != *:* ]] || image=${image%:*}
      [[ "${image}" != "${SDK}" ]] || sdk_seen=1
      [[ "${image}" != "${RUNTIME}" ]] || runtime_seen=1
      if [[ ! "${ref}" =~ ${pinned} ]]; then
        problem "${dockerfile}" "${number}" "FROM ${ref} is not pinned by tag and digest (image:tag@sha256:<the digest of the image's multi-platform index>)."
      elif [[ "${image}" == "${SDK}" ]]; then
        tag=${BASH_REMATCH[2]}
        if [[ -n "${pin}" && "${tag}" != "${pin}" ]]; then
          problem "${dockerfile}" "${number}" "the build image ${SDK}:${tag} does not match ${GLOBAL_JSON} (${pin}); update both in the same change."
        fi
        if [[ -z "${sdk_ref}" ]]; then
          sdk_ref=${ref} sdk_file=${dockerfile} sdk_line=${number}
        elif [[ "${ref}" != "${sdk_ref}" ]]; then
          problem "${dockerfile}" "${number}" "the build image ${ref} differs from ${sdk_ref} (${sdk_file}:${sdk_line}); update both in the same change."
        fi
      elif [[ "${image}" == "${RUNTIME}" ]]; then
        if [[ -z "${runtime_ref}" ]]; then
          runtime_ref=${ref} runtime_file=${dockerfile} runtime_line=${number}
        elif [[ "${ref}" != "${runtime_ref}" ]]; then
          problem "${dockerfile}" "${number}" "the runtime image ${ref} differs from ${runtime_ref} (${runtime_file}:${runtime_line}); update both in the same change."
        fi
      fi
    fi
    [[ -z "${stage}" ]] || stages+="${stage} "
  done <"${root}/${dockerfile}"
  [[ ${sdk_seen} -eq 1 ]] || problem "${dockerfile}" "" "has no FROM ${SDK} line."
  [[ ${runtime_seen} -eq 1 ]] || problem "${dockerfile}" "" "has no FROM ${RUNTIME} line."
done

# Each digest must be the tag's index, not one platform's image: a single-platform image would be used for every
# platform built.
if [[ ${errors} -eq 0 && ${registry} -eq 1 ]]; then
  for which in sdk runtime; do
    if [[ "${which}" == sdk ]]; then
      ref=${sdk_ref} file=${sdk_file} line=${sdk_line}
    else
      ref=${runtime_ref} file=${runtime_file} line=${runtime_line}
    fi
    if ! raw=$(docker buildx imagetools inspect --raw "${ref}" 2>&1); then
      problem "${file}" "${line}" "${ref} could not be read from its registry: ${raw}"
      continue
    fi
    held=$(jq -r '(.manifests // [])[] | .platform // empty | "\(.os)/\(.architecture)"' <<<"${raw}" 2>/dev/null) || held=""
    missing=0
    for platform in "${PLATFORMS[@]}"; do
      if ! grep -qxF "${platform}" <<<"${held}"; then
        problem "${file}" "${line}" "${ref} is not an index holding ${platform}: pin the digest that 'docker buildx imagetools inspect ${ref%@*}' prints for the tag, not one platform's."
        missing=1
      fi
    done
    [[ ${missing} -eq 1 ]] || echo "${ref}: an index holding ${PLATFORMS[*]}"
  done
fi

if [[ ${errors} -gt 0 ]]; then
  echo "check-base-images.sh: ${errors} problem(s) with the base images." >&2
  exit 1
fi
echo "Build image ${sdk_ref} (${GLOBAL_JSON}: ${pin}) and runtime image ${runtime_ref}, in ${DOCKERFILES[*]}"
