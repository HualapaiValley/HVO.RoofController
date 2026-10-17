#!/usr/bin/env bash
# Tests for build/check-base-images.sh (the Dockerfiles' base images pinned by tag and digest). It runs against
# repositories made here, holding src/global.json and the three Dockerfiles, and its --registry check against a stand-in
# for docker, so no Docker or network is needed; and, without --registry, against this repository's own files. Requires
# bash, python3, jq and sha256sum or shasum.
# Run: tests/versioning/base-image-tests.sh
set -uo pipefail

TESTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
SCRIPT=${BASE_IMAGE_CHECKER:-"${TESTS_DIR}/../../build/check-base-images.sh"}
SDK=mcr.microsoft.com/dotnet/sdk
RUNTIME=mcr.microsoft.com/dotnet/aspnet
RPI=src/HVO.RoofControllerV4.RPi/Dockerfile
EMULATOR=src/HVO.RoofControllerV4.Emulator/Dockerfile
DEV=.devcontainer/Dockerfile
DEV_IMAGE=mcr.microsoft.com/devcontainers/dotnet

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

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum | cut -d' ' -f1; else shasum -a 256 | cut -d' ' -f1; fi
}

# digest <text>: a sha256 digest, as a registry gives one.
digest() {
  printf 'sha256:%s' "$(printf '%s' "$1" | sha256)"
}

SDK_REF="${SDK}:10.0.402@$(digest sdk)"
RUNTIME_REF="${RUNTIME}:10.0.13@$(digest aspnet)"
DEV_REF="${DEV_IMAGE}:2.3.2-10.0-noble@$(digest devcontainer)"

# dockerfile <build image> <runtime image>: a Dockerfile shaped like the repository's, with a stage built on another.
dockerfile() {
  printf '# syntax=docker/dockerfile:1\nFROM --platform=%s %s AS build\nRUN true\nFROM %s AS runtime\n' \
    "\$BUILDPLATFORM" "$1" "$2"
  printf 'COPY --from=build /app .\nFROM runtime AS final\n'
}

# repo <name> [<Pi build> <Pi runtime> <emulator build> <emulator runtime> [<global.json SDK>]]: a repository holding
# src/global.json and the three Dockerfiles, by default with matching pins, and prints its path.
repo() {
  local dir="${WORK}/$1"
  mkdir -p "${dir}/src/HVO.RoofControllerV4.RPi" "${dir}/src/HVO.RoofControllerV4.Emulator" "${dir}/.devcontainer"
  printf '{\n  "sdk": {\n    "version": "%s",\n    "rollForward": "patch"\n  }\n}\n' "${6-10.0.402}" > "${dir}/src/global.json"
  dockerfile "${2-${SDK_REF}}" "${3-${RUNTIME_REF}}" > "${dir}/${RPI}"
  dockerfile "${4-${SDK_REF}}" "${5-${RUNTIME_REF}}" > "${dir}/${EMULATOR}"
  printf 'FROM %s\n' "${DEV_REF}" > "${dir}/${DEV}"
  echo "${dir}"
}

# ---- The files -------------------------------------------------------------------------------------------------------

check "the repository's own Dockerfiles" 0 "and runtime image ${RUNTIME}:" "${SCRIPT}" "${TESTS_DIR}/../.."

r=$(repo good)
check "pinned and matching" 0 \
  "Build image ${SDK_REF} (src/global.json: 10.0.402) and runtime image ${RUNTIME_REF}, in ${RPI} ${EMULATOR} ${DEV}" \
  "${SCRIPT}" "${r}"

r=$(repo dev-tag-only)
printf 'FROM %s:2.3.2-10.0-noble\n' "${DEV_IMAGE}" > "${r}/${DEV}"
check "dev container requires a digest" 1 "${DEV}:1: FROM ${DEV_IMAGE}:2.3.2-10.0-noble is not pinned" "${SCRIPT}" "${r}"
r=$(repo dev-digest-only)
printf 'FROM %s@%s\n' "${DEV_IMAGE}" "$(digest devcontainer)" > "${r}/${DEV}"
check "dev container requires a tag" 1 "${DEV}:1: FROM ${DEV_IMAGE}@" "${SCRIPT}" "${r}"
r=$(repo dev-missing)
rm "${r}/${DEV}"
check "dev Dockerfile is required" 1 "${DEV}: was not found" "${SCRIPT}" "${r}"
r=$(repo dev-wrong-base)
printf 'FROM scratch\n' > "${r}/${DEV}"
check "dev container base is required" 1 "${DEV}: has no FROM ${DEV_IMAGE} line" "${SCRIPT}" "${r}"
r=$(repo dev-stage)
printf 'FROM %s AS dev\nFROM DEV AS final\n' "${DEV_REF}" > "${r}/${DEV}"
check "dev container needs no product runtime stage" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"

r=$(repo sdk-tag-only "${SDK}:10.0.402")
check "build image by tag only" 1 "${RPI}:2: FROM ${SDK}:10.0.402 is not pinned by tag and digest" "${SCRIPT}" "${r}"
r=$(repo runtime-tag-only "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "${RUNTIME}:10.0.13")
check "runtime image by tag only" 1 "${EMULATOR}:4: FROM ${RUNTIME}:10.0.13 is not pinned by tag and digest" \
  "${SCRIPT}" "${r}"
r=$(repo digest-only "${SDK_REF}" "${RUNTIME}@$(digest aspnet)")
check "runtime image by digest only" 1 "${RPI}:4: FROM ${RUNTIME}@$(digest aspnet) is not pinned by tag and digest" \
  "${SCRIPT}" "${r}"
upper=$(printf 'aspnet' | sha256 | tr 'a-f' 'A-F')
r=$(repo upper-case-digest "${SDK_REF}" "${RUNTIME}:10.0.13@sha256:${upper}")
check "digest in upper case" 1 "${RPI}:4: FROM ${RUNTIME}:10.0.13@sha256" "${SCRIPT}" "${r}"
short=$(digest aspnet)
r=$(repo short-digest "${SDK_REF}" "${RUNTIME}:10.0.13@${short%?}")
check "digest too short" 1 "is not pinned by tag and digest" "${SCRIPT}" "${r}"
r=$(repo floating "${SDK}:10.0@$(digest sdk)" "${RUNTIME_REF}" "${SDK}:10.0@$(digest sdk)")
check "build image not global.json's" 1 \
  "${RPI}:2: the build image ${SDK}:10.0 does not satisfy src/global.json (10.0.402, rollForward: patch)" \
  "${SCRIPT}" "${r}"
r=$(repo other-sdk-pin "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "${RUNTIME_REF}" 10.0.403)
check "global.json moved alone" 1 "${EMULATOR}:2: the build image ${SDK}:10.0.402 does not satisfy src/global.json (10.0.403, rollForward: patch)" \
  "${SCRIPT}" "${r}"

# global.json can keep the dev container's minimum while product builds use a later patched SDK.
PATCH_REF="${SDK}:10.0.403@$(digest sdk-next-patch)"
r=$(repo sdk-patch "${PATCH_REF}" "${RUNTIME_REF}" "${PATCH_REF}" "${RUNTIME_REF}")
check "later patch in the same feature band" 0 "Build image ${PATCH_REF}" "${SCRIPT}" "${r}"
sed 's/"patch"/"disable"/' "${r}/src/global.json" > "${r}/src/global.json.tmp"
mv "${r}/src/global.json.tmp" "${r}/src/global.json"
check "disabled roll-forward requires an exact SDK" 1 "rollForward: disable" "${SCRIPT}" "${r}"
for version in 10.0.401 10.0.500 11.0.402 10.0.403-preview.1; do
  ref="${SDK}:${version}@$(digest invalid-sdk)"
  r=$(repo "sdk-policy-${version}" "${ref}" "${RUNTIME_REF}" "${ref}" "${RUNTIME_REF}")
  check "reject incompatible SDK ${version}" 1 "does not satisfy src/global.json" "${SCRIPT}" "${r}"
done

# The two Dockerfiles: the same build image and the same runtime image, tag and digest.
r=$(repo runtime-tags "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "${RUNTIME}:10.0.12@$(digest aspnet-12)")
check "runtime tags differ" 1 \
  "${EMULATOR}:4: the runtime image ${RUNTIME}:10.0.12@$(digest aspnet-12) differs from ${RUNTIME_REF} (${RPI}:4)" \
  "${SCRIPT}" "${r}"
r=$(repo runtime-digests "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "${RUNTIME}:10.0.13@$(digest aspnet-rebuilt)")
check "runtime digests differ" 1 "the runtime image ${RUNTIME}:10.0.13@$(digest aspnet-rebuilt) differs from ${RUNTIME_REF}" \
  "${SCRIPT}" "${r}"
r=$(repo sdk-digests "${SDK_REF}" "${RUNTIME_REF}" "${SDK}:10.0.402@$(digest sdk-rebuilt)")
check "build image digests differ" 1 "${EMULATOR}:2: the build image ${SDK}:10.0.402@$(digest sdk-rebuilt) differs from ${SDK_REF}" \
  "${SCRIPT}" "${r}"

# Other images: pinned the same way, and they do not stand in for the runtime.
r=$(repo other-image "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "debian:12@$(digest debian)")
check "no runtime image" 1 "${EMULATOR}: has no FROM ${RUNTIME} line" "${SCRIPT}" "${r}"
r=$(repo other-image-tag-only "${SDK_REF}" "${RUNTIME_REF}" "${SDK_REF}" "debian:12")
check "another image by tag only" 1 "${EMULATOR}:4: FROM debian:12 is not pinned by tag and digest" "${SCRIPT}" "${r}"
r=$(repo registry-port "${SDK_REF}" "registry.example:5000/aspnet@$(digest aspnet)")
check "a registry port is not a tag" 1 "FROM registry.example:5000/aspnet@$(digest aspnet) is not pinned by tag and digest" \
  "${SCRIPT}" "${r}"

# Instructions in any case, a file without a final newline, and every problem reported.
r=$(repo lower-case)
printf 'from --platform=%s %s as build\nfrom %s as runtime\n' "\$BUILDPLATFORM" "${SDK}:10.0.402" "${RUNTIME_REF}" \
  > "${r}/${RPI}"
check "lower-case from" 1 "${RPI}:1: FROM ${SDK}:10.0.402 is not pinned" "${SCRIPT}" "${r}"
printf 'FROM %s AS build\nFROM %s' "${SDK_REF}" "${RUNTIME}:10.0.13" > "${r}/${RPI}"
check "no final newline" 1 "${RPI}:2: FROM ${RUNTIME}:10.0.13 is not pinned" "${SCRIPT}" "${r}"
r=$(repo two-problems "${SDK}:10.0.402" "${RUNTIME}:10.0.13")
check "every problem reported" 1 "2 problem(s) with the base images" "${SCRIPT}" "${r}"
check "annotations in GitHub Actions" 1 \
  "::error file=${RPI},line=4::FROM ${RUNTIME}:10.0.13 is not pinned by tag and digest" \
  env GITHUB_ACTIONS=true "${SCRIPT}" "${r}"

r=$(repo no-global-json) && rm "${r}/src/global.json"
check "no global.json" 1 "src/global.json: was not found" "${SCRIPT}" "${r}"
r=$(repo no-sdk-version) && printf '{ "msbuild-sdks": {} }\n' > "${r}/src/global.json"
check "no SDK in global.json" 1 "src/global.json: has no sdk.version" "${SCRIPT}" "${r}"
r=$(repo no-emulator) && rm "${r}/${EMULATOR}"
check "no emulator Dockerfile" 1 "${EMULATOR}: was not found" "${SCRIPT}" "${r}"
check "too many arguments" 2 "build/check-base-images.sh [--registry] [<repository root>]" "${SCRIPT}" "${r}" extra
check "unknown option" 2 "build/check-base-images.sh [--registry] [<repository root>]" "${SCRIPT}" --other

# Logical instructions, preserving the first physical line for diagnostics. Each case catches a physical-line
# parser accepting an unpinned base, rejecting heredoc/shell content, or silently accepting unsupported syntax.
r=$(repo split-keyword)
printf 'FR\\\nOM alpine:latest AS tools\n' >> "${r}/${RPI}"
check "split FROM keyword" 1 "${RPI}:7: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"
r=$(repo continued-image)
printf 'FROM \\\n  alpine:latest AS tools\n' >> "${r}/${RPI}"
check "continued FROM image" 1 "${RPI}:7: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"
r=$(repo continued-comments)
printf 'FR\\\n  # ignored comment, including its escape \\\n\nOM alpine:latest AS tools\n' >> "${r}/${RPI}"
check "comments and empty continuation lines" 1 "${RPI}:7: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"
r=$(repo backtick)
{ printf '# escape=`\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; printf 'FR`\nOM alpine:latest AS tools\n'; } > "${r}/${RPI}"
check "backtick escape directive" 1 "${RPI}:8: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"
r=$(repo lowercase-split)
printf 'fr\\\nom alpine:latest as tools\n' >> "${r}/${RPI}"
check "lowercase logical from" 1 "${RPI}:7: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"
r=$(repo heredoc-run)
printf 'RUN <<EOF\nFROM alpine:latest AS text\nEOF\n' >> "${r}/${RPI}"
check "RUN heredoc is not instructions" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"
r=$(repo heredoc-copy)
printf "COPY <<'EOF' /example\nFROM alpine:latest AS text\nEOF\n" >> "${r}/${RPI}"
check "quoted COPY heredoc is not instructions" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"
r=$(repo heredoc-tabs)
printf 'ADD <<-"EOF" /example\n\tFROM alpine:latest AS text\n\tEOF\n' >> "${r}/${RPI}"
check "tab-stripping ADD heredoc is not instructions" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"
r=$(repo heredoc-multiple)
printf "RUN cat <<ONE <<'TWO'\nFROM alpine:latest AS text\nONE\nFROM debian:latest AS more\nTWO\n" >> "${r}/${RPI}"
check "multiple heredocs are not instructions" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"
r=$(repo continued-run)
printf 'RUN echo \\\nFROM alpine:latest AS text\n' >> "${r}/${RPI}"
check "continued RUN is not FROM" 0 "Build image ${SDK_REF}" "${SCRIPT}" "${r}"
r=$(repo heredoc-unterminated)
printf 'RUN <<EOF\necho example\n' >> "${r}/${RPI}"
check "unterminated heredoc fails closed" 1 "${RPI}:7: unterminated heredoc EOF" "${SCRIPT}" "${r}"
r=$(repo heredoc-unsupported)
# shellcheck disable=SC2016 # The fixture deliberately contains an unexpanded delimiter.
printf 'RUN <<$EOF\necho example\n' >> "${r}/${RPI}"
check "dynamic heredoc delimiter fails closed" 1 "${RPI}:7: unsupported heredoc header" "${SCRIPT}" "${r}"
r=$(repo quoted-flags)
printf 'FROM --platform="linux/amd64" %s AS tools\n' "${SDK_REF}" >> "${r}/${RPI}"
check "quoted FROM flags fail closed" 1 "${RPI}:7: unsupported FROM flag syntax" "${SCRIPT}" "${r}"
r=$(repo unknown-instruction)
printf 'FROB example\n' >> "${r}/${RPI}"
check "unknown instruction fails closed" 1 "${RPI}:7: unsupported instruction FROB" "${SCRIPT}" "${r}"
r=$(repo invalid-escape)
{ printf '# escape=~\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
check "invalid escape directive fails closed" 1 "${RPI}:1: unsupported escape directive" "${SCRIPT}" "${r}"
r=$(repo duplicate-escape)
{ printf '# escape=\\\n# escape=`\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
check "duplicate parser directive fails closed" 1 "${RPI}:2: duplicate escape directive" "${SCRIPT}" "${r}"
r=$(repo custom-frontend)
{ printf '# syntax=example/frontend:1\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
check "custom frontend fails closed" 1 "${RPI}:1: unsupported syntax frontend" "${SCRIPT}" "${r}"
r=$(repo malformed-from)
printf 'FROM %s AS tools ignored\n' "${SDK_REF}" >> "${r}/${RPI}"
check "malformed FROM fails closed" 1 "${RPI}:7: unsupported FROM arguments" "${SCRIPT}" "${r}"
r=$(repo dangling-continuation)
printf 'RUN true \\\n' >> "${r}/${RPI}"
check "dangling continuation fails closed" 1 "${RPI}:7: unterminated line continuation" "${SCRIPT}" "${r}"
r=$(repo late-escape)
printf '# escape=`\nFR\\\nOM alpine:latest AS tools\n' >> "${r}/${RPI}"
check "late escape comment does not change parsing" 1 "${RPI}:8: FROM alpine:latest is not pinned" "${SCRIPT}" "${r}"

r=$(repo variable-base)
# shellcheck disable=SC2016 # FROM image variables are intentionally unsupported.
printf 'FROM ${BASE}:latest@%s AS tools\n' "$(digest tools)" >> "${r}/${RPI}"
check "variable image fails closed" 1 "${RPI}:7: unsupported FROM image syntax" "${SCRIPT}" "${r}"
r=$(repo shebang-frontend)
{ printf '#!/example/frontend\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
check "shebang frontend fails closed" 1 "${RPI}:1: unsupported Dockerfile shebang/frontend syntax" "${SCRIPT}" "${r}"

# ---- --registry ------------------------------------------------------------------------------------------------------

# A stand-in for docker buildx imagetools inspect --raw <reference>: prints FAKE_SDK_INDEX for the SDK and
# FAKE_RUNTIME_INDEX for anything else, or fails with FAKE_DOCKER_FAIL; it touches FAKE_DOCKER_CALLED when run.
mkdir -p "${WORK}/bin"
cat > "${WORK}/bin/docker" <<'FAKE'
#!/usr/bin/env bash
[[ $# -eq 5 && "$1 $2 $3 $4" == "buildx imagetools inspect --raw" ]] || { echo "unexpected docker $*" >&2; exit 64; }
[[ -z "${FAKE_DOCKER_CALLED:-}" ]] || touch "${FAKE_DOCKER_CALLED}"
[[ -z "${FAKE_DOCKER_LOG:-}" ]] || printf '%s\n' "$5" >> "${FAKE_DOCKER_LOG}"
[[ -z "${FAKE_DOCKER_FAIL:-}" ]] || { echo "${FAKE_DOCKER_FAIL}" >&2; exit 1; }
case "$5" in
  */sdk:*) cat "${FAKE_SDK_INDEX}" ;;
  */aspnet:*) cat "${FAKE_RUNTIME_INDEX}" ;;
  */devcontainers/dotnet:*) cat "${FAKE_DEV_INDEX:-${FAKE_RUNTIME_INDEX}}" ;;
  *) cat "${FAKE_OTHER_INDEX:-${FAKE_RUNTIME_INDEX}}" ;;
esac
FAKE
chmod +x "${WORK}/bin/docker"

# index <name> <platform...>: a manifest list, as the registry serves it, holding an image for each platform.
index() {
  local file="${WORK}/$1.json" platform manifests="[]"
  shift
  for platform in "$@"; do
    manifests=$(jq -c --arg digest "$(digest "${platform}")" --arg os "${platform%%/*}" --arg rest "${platform#*/}" \
      '. + [{mediaType: "application/vnd.docker.distribution.manifest.v2+json", digest: $digest,
             platform: ({os: $os, architecture: ($rest | split("/")[0])}
                        + (if ($rest | contains("/")) then {variant: ($rest | split("/")[1])} else {} end))}]' \
      <<<"${manifests}")
  done
  jq -n --argjson manifests "${manifests}" \
    '{schemaVersion: 2, mediaType: "application/vnd.docker.distribution.manifest.list.v2+json", manifests: $manifests}' \
    > "${file}"
  echo "${file}"
}
all=$(index all linux/amd64 linux/arm/v7 linux/arm64/v8)
amd64=$(index amd64 linux/amd64)
single="${WORK}/single.json"
jq -n '{schemaVersion: 2, mediaType: "application/vnd.docker.distribution.manifest.v2+json",
        config: {digest: "sha256:0"}, layers: []}' > "${single}"

registry() {
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="$1" FAKE_RUNTIME_INDEX="$2" FAKE_DOCKER_FAIL="${3-}" \
    "${SCRIPT}" --registry "${WORK}/good"
}
check "indexes for both platforms" 0 "${RUNTIME_REF}: an index holding linux/amd64 linux/arm64" registry "${all}" "${all}"
check "the build image's index checked" 0 "${SDK_REF}: an index holding linux/amd64 linux/arm64" registry "${all}" "${all}"
check "a single-platform runtime image" 1 \
  "${RPI}:4: ${RUNTIME_REF} is not an index holding linux/amd64: pin the digest that 'docker buildx imagetools inspect ${RUNTIME}:10.0.13' prints" \
  registry "${all}" "${single}"
check "a build image index without arm64" 1 "${RPI}:2: ${SDK_REF} is not an index holding linux/arm64" \
  registry "${amd64}" "${all}"
check "a registry that cannot be read" 1 "${SDK_REF} could not be read from its registry: unauthorized" \
  registry "${all}" "${all}" unauthorized
# Platform entries alone do not prove the response is an index. Require one schema-2 index document.
wrong_media="${WORK}/wrong-media.json"
jq '.mediaType = "application/vnd.oci.image.manifest.v1+json"' "${all}" > "${wrong_media}"
check "image manifest cannot pose as index" 1 "is not a schemaVersion 2 Docker manifest list or OCI image index" \
  registry "${all}" "${wrong_media}"
wrong_schema="${WORK}/wrong-schema.json"
jq '.schemaVersion = 1' "${all}" > "${wrong_schema}"
check "index schema version must be two" 1 "is not a schemaVersion 2 Docker manifest list or OCI image index" \
  registry "${all}" "${wrong_schema}"
missing_media="${WORK}/missing-media.json"
jq 'del(.mediaType)' "${all}" > "${missing_media}"
check "index media type is required" 1 "is not a schemaVersion 2 Docker manifest list or OCI image index" \
  registry "${all}" "${missing_media}"
multiple_documents="${WORK}/multiple-documents.json"
cat "${all}" "${all}" > "${multiple_documents}"
check "registry response must be one document" 1 "is not a schemaVersion 2 Docker manifest list or OCI image index" \
  registry "${all}" "${multiple_documents}"
oci="${WORK}/oci.json"
jq '.mediaType = "application/vnd.oci.image.index.v1+json"' "${all}" > "${oci}"
check "OCI index accepted" 0 "${RUNTIME_REF}: an index holding linux/amd64 linux/arm64" registry "${oci}" "${oci}"

# All external bases, in both files, are inspected once; only earlier stage aliases and scratch are exempt.
TOOLS_REF="example/tools:1@$(digest tools)"
r=$(repo third-base)
printf 'FROM %s AS tools\n' "${TOOLS_REF}" >> "${r}/${RPI}"
check "third external single-platform base" 1 "${RPI}:7: ${TOOLS_REF} is not an index holding linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_OTHER_INDEX="${single}" \
  "${SCRIPT}" --registry "${r}"
r=$(repo emulator-third-base)
printf 'FROM %s AS tools\n' "${TOOLS_REF}" >> "${r}/${EMULATOR}"
check "additional emulator base is inspected" 1 "${EMULATOR}:7: ${TOOLS_REF} is not an index holding linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_OTHER_INDEX="${single}" \
  "${SCRIPT}" --registry "${r}"
r=$(repo deduplicated-bases)
printf 'FROM %s AS tools\nFROM %s AS moretools\n' "${TOOLS_REF}" "${TOOLS_REF}" >> "${r}/${RPI}"
printf 'FROM %s AS tools\n' "${TOOLS_REF}" >> "${r}/${EMULATOR}"
check "third external index accepted" 0 "${TOOLS_REF}: an index holding linux/amd64 linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_OTHER_INDEX="${all}" \
  FAKE_DOCKER_LOG="${WORK}/registry-calls" "${SCRIPT}" --registry "${r}"
check "each unique external base inspected once" 0 "" test "$(wc -l < "${WORK}/registry-calls")" -eq 4
r=$(repo case-insensitive-stages)
printf 'FROM RUNTIME AS Another\nFROM aNoThEr AS final-again\nFROM scratch AS Empty\nFROM EMPTY AS empty-again\n' >> "${r}/${RPI}"
check "earlier stage names match case-insensitively" 0 "${RUNTIME_REF}: an index holding linux/amd64 linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_OTHER_INDEX="${single}" \
  "${SCRIPT}" --registry "${r}"

r=$(repo dev-registry)
check "dev container index is inspected" 0 "${DEV_REF}: an index holding linux/amd64 linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_DEV_INDEX="${oci}" \
  "${SCRIPT}" --registry "${r}"
check "dev container single-platform base is rejected" 1 "${DEV}:1: ${DEV_REF} is not a schemaVersion 2" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_DEV_INDEX="${single}" \
  "${SCRIPT}" --registry "${r}"
check "dev container index must include arm64" 1 "${DEV}:1: ${DEV_REF} is not an index holding linux/arm64" \
  env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" FAKE_DEV_INDEX="${amd64}" \
  "${SCRIPT}" --registry "${r}"

# The registry is read only when the files are right.
check "files checked first" 1 "${RPI}:4: FROM ${RUNTIME}:10.0.13 is not pinned" \
  env PATH="${WORK}/bin:${PATH}" FAKE_DOCKER_CALLED="${WORK}/called" "${SCRIPT}" --registry "${WORK}/two-problems"
check "registry not read" 1 "" test -e "${WORK}/called"

# F3: the input alphabet is checked before comments, directives, continuations or heredoc bodies can hide it.
# Character diagnostics must contain a code point and must not echo the fixture's raw source text.
f3_character() {
  python3 - "$1" "$2" "$3" <<'PY'
import sys
from pathlib import Path
path, point, kind = Path(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
text = path.read_text()
char = chr(point)
if kind == "escape":
    text = "# escape" + char + "= `\n" + text + "RUN true # `\nFROM alpine:latest AS tools\n"
elif kind == "bom":
    text = char + text
elif kind == "heredoc":
    text += "RUN <<EOF\nraw-source-sentinel " + char + "\nEOF\n"
else:
    text += "# raw-source-sentinel " + char + "\n"
path.write_bytes(text.encode("utf-8"))
PY
}
redacted_rejection() {
  local output status
  output=$("$@" 2>&1)
  status=$?
  printf '%s\n' "${output}"
  [[ "${output}" != *raw-source-sentinel* ]] || return 99
  return "${status}"
}
for point in 11 160; do
  r=$(repo "f3-escape-${point}")
  f3_character "${r}/${RPI}" "${point}" escape
  codepoint=$(printf 'U+%04X' "${point}")
  check "escape whitespace ${codepoint}" 1 "${RPI}:1: unsupported character ${codepoint}" \
    redacted_rejection "${SCRIPT}" "${r}"
  check "escape whitespace ${codepoint} with registry" 1 "${RPI}:1: unsupported character ${codepoint}" \
    redacted_rejection env PATH="${WORK}/bin:${PATH}" FAKE_SDK_INDEX="${all}" FAKE_RUNTIME_INDEX="${all}" \
    "${SCRIPT}" --registry "${r}"
done
r=$(repo f3-form-feed)
f3_character "${r}/${RPI}" 12 comment
check "form feed in comment fails closed" 1 "${RPI}:7: unsupported character U+000C" \
  redacted_rejection "${SCRIPT}" "${r}"
r=$(repo f3-bom)
f3_character "${r}/${RPI}" 65279 bom
check "initial BOM fails closed" 1 "${RPI}:1: unsupported character U+FEFF" \
  redacted_rejection "${SCRIPT}" "${r}"
r=$(repo f3-unicode-body)
f3_character "${r}/${EMULATOR}" 233 heredoc
check "non-ASCII heredoc content fails closed" 1 "${EMULATOR}:8: unsupported character U+00E9" \
  redacted_rejection "${SCRIPT}" "${r}"

# Cover the rest of the forbidden ASCII control range, plus representative Unicode spaces and a non-BMP code point.
for point in 0 1 2 3 4 5 6 7 8 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31 127 133 5760 8195 8232 8239 12288 128512; do
  r=$(repo "f3-alphabet-${point}")
  f3_character "${r}/${RPI}" "${point}" comment
  codepoint=$(printf 'U+%04X' "${point}")
  check "forbidden alphabet ${codepoint}" 1 "${RPI}:7: unsupported character ${codepoint}" \
    redacted_rejection "${SCRIPT}" "${r}"
done
r=$(repo f3-invalid-utf8)
printf '# raw-source-sentinel \377\n' >> "${r}/${RPI}"
check "invalid UTF-8 byte fails closed without raw text" 1 "${RPI}:7: unsupported character U+DCFF" \
  redacted_rejection "${SCRIPT}" "${r}"

# Near misses remain errors even after an ordinary header comment has ended BuildKit's directive prologue.
for header in '#escape =' '# syntax docker/dockerfile:1 trailing junk' '# escape?=`' '# note: SYNTAX typo'; do
  r=$(repo "f3-near-miss-${PASSED}-${FAILED}")
  { printf '%s\n' "${header}"; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
  check "near-miss directive ${header}" 1 "${RPI}:1: ambiguous escape/syntax header" "${SCRIPT}" "${r}"
done
r=$(repo f3-near-miss-after-comment)
{ printf '# ordinary header\n#escape =\n'; dockerfile "${SDK_REF}" "${RUNTIME_REF}"; } > "${r}/${RPI}"
check "near miss after header comment fails closed" 1 "${RPI}:2: ambiguous escape/syntax header" "${SCRIPT}" "${r}"

# ---------------------------------------------------------------------------------------------------------------------

echo "${PASSED} passed, ${FAILED} failed"
for failure in "${FAILURES[@]+"${FAILURES[@]}"}"; do
  echo "  ${failure}"
done
[[ ${FAILED} -eq 0 ]]
