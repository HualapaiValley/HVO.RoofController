#!/usr/bin/env bash
# Checks the base images of the controller's and the HAT emulator's Dockerfiles: every external FROM names its image by tag and
# by the digest of its multi-platform index (image:tag@sha256:<digest>), so a rebuild of the same commit uses the same
# bases; the SDK's tag is the SDK src/global.json pins; and the two Dockerfiles use the same SDK and the same ASP.NET
# Core runtime, tag and digest. With --registry, it also reads each pinned digest from its registry, which must be an
# index holding linux/amd64 and linux/arm64. Reports pin problems and fails closed on unsupported syntax, with file:line
# diagnostics and, in GitHub Actions, annotations.
#
#   build/check-base-images.sh [--registry] [<repository root>]
#
# Needs python3 and jq, and docker buildx for --registry. Dependabot moves the tags and digests (.github/dependabot.yml).
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

# Extract FROM records only after parsing the whole file. A partial/ambiguous parse must never pass the pin gate.
# This deliberately supports a bounded Dockerfile grammar, not arbitrary frontends or shell lexing. See BuildKit's
# frontend/dockerfile/parser/{parser.go,directives.go,split_command.go} for continuation and heredoc boundaries.
logical_froms() {
  if ! command -v python3 >/dev/null 2>&1; then
    printf '1\tpython3 is required to parse logical Dockerfile instructions\n'
    return 1
  fi
  python3 - "$1" <<'PY'
import json
import re
import sys
from pathlib import Path

SPACE = " \t\r"
# Mirror BuildKit v0.29.0 frontend/dockerfile/parser/directives.go:
# https://github.com/moby/buildkit/blob/v0.29.0/frontend/dockerfile/parser/directives.go#L14-L24
# https://github.com/moby/buildkit/blob/v0.29.0/frontend/dockerfile/parser/directives.go#L40-L91
# The anchored regex uses Go's ASCII \s = [\t\n\f\r ], NOT Python's Unicode \s (nor re.ASCII's \s, which adds VT).
# ParseLine cuts '#', trims left whitespace, lowercases keys only, knows syntax/escape/check, stops on the first
# non-directive (including an ordinary comment or blank line), and rejects a repeated key while still active.
DIRECTIVE = re.compile(r"^([a-zA-Z][a-zA-Z0-9]*)[\t\n\f\r ]*=[\t\n\f\r ]*(.+?)[\t\n\f\r ]*$")
DIRECTIVES = {"syntax", "escape", "check"}
COMMANDS = set("add arg cmd copy entrypoint env expose from healthcheck label maintainer onbuild run shell "
               "stopsignal user volume workdir".split())
DELIMITER = r"[A-Za-z_][A-Za-z0-9_.-]*"
HEREDOC = re.compile(r"(?:[0-9]*)<<(-?)(?:({0})|'({0})'|\"({0})\")".format(DELIMITER))


class Unsupported(Exception):
    def __init__(self, line, message):
        self.line, self.message = line, message


def reject(line, message):
    raise Unsupported(line, message)


def flags_and_args(args, line, instruction):
    # BuildKit consumes leading --words before parsing instruction arguments. Quotes and escapes in flags need
    # its full lexer; refuse them rather than guessing which word is the base image or a heredoc declaration.
    while args.startswith("--"):
        parts = re.split(r"[ \t\v\f\r]+", args, maxsplit=1)
        flag, args = parts[0], parts[1].lstrip(SPACE) if len(parts) == 2 else ""
        if flag == "--":
            break
        if not re.fullmatch(r"--[A-Za-z][A-Za-z0-9-]*(?:=[^\s'\"\\`]+)?", flag):
            reject(line, "unsupported " + instruction + " flag syntax (use unquoted --name=value)")
        if instruction == "FROM" and "=" not in flag:
            reject(line, "unsupported FROM flag syntax (use --name=value)")
    return args


def parse(path):
    try:
        data = Path(path).read_bytes().decode("utf-8", errors="surrogateescape")
    except OSError:
        reject(1, "cannot read Dockerfile")
    # Validate the whole input before trimming, skipping comments or consuming heredocs. Never strip a BOM or echo
    # rejected source text. Invalid UTF-8 bytes become U+DC80..U+DCFF via surrogateescape and are rejected here too.
    number = 1
    for char in data:
        if not (0x20 <= ord(char) <= 0x7E or char in "\t\n\r"):
            reject(number, f"unsupported character U+{ord(char):04X} (expected printable ASCII, TAB, LF or CR)")
        if char == "\n":
            number += 1
    # Split on LF only: splitlines() would turn other whitespace into instruction boundaries.
    lines = data.split("\n")
    if lines[-1] == "":
        lines.pop()
    lines = [line.rstrip("\r") for line in lines]
    escape, prologue, header, directives, records = "\\", True, True, set(), []
    i = 0
    while i < len(lines):
        start = i + 1
        text = lines[i].lstrip(SPACE)
        i += 1
        if text.startswith("#!"):
            reject(start, "unsupported Dockerfile shebang/frontend syntax")
        directive = DIRECTIVE.fullmatch(text[1:].lstrip(SPACE)) if text.startswith("#") else None
        key = directive[1].lower() if directive else ""
        recognized = key in DIRECTIVES
        # Stricter than BuildKit by policy: near-miss escape/syntax comments in the header must not silently alter
        # our interpretation. Continue checking the header even after a normal comment has ended the prologue.
        if header and text.startswith("#") and ("escape" in text.lower() or "syntax" in text.lower()):
            if not recognized:
                reject(start, "ambiguous escape/syntax header (expected an exact recognized directive)")
        if prologue:
            if recognized:
                if key in directives:
                    reject(start, "duplicate " + key + " directive")
                directives.add(key)
            else:
                prologue = False
        if recognized and header:
            value = directive[2]  # Values stay case-sensitive, as in BuildKit.
            if key == "escape" and value not in {"\\", "`"}:
                reject(start, "unsupported escape directive (expected backslash or backtick)")
            if key == "syntax" and value != "docker/dockerfile:1":
                reject(start, "unsupported syntax frontend (expected docker/dockerfile:1)")
            if prologue and key == "escape":
                escape = value
        if not text or text.startswith("#"):
            continue
        header = False
        logical = ""
        while True:
            # This matches BuildKit's rule: the final escape continues only if it is not itself preceded by one.
            tail = text.rstrip(" \t")
            continued = tail.endswith(escape) and (len(tail) == 1 or tail[-2] != escape)
            logical += tail[:-1] if continued else text
            if not continued:
                break
            while i < len(lines) and (not lines[i].strip(SPACE) or lines[i].lstrip(SPACE).startswith("#")):
                i += 1
            if i == len(lines):
                reject(start, "unterminated line continuation")
            text = lines[i]  # Continuation indentation is significant; never insert or remove whitespace here.
            i += 1
        words = re.split(r"[ \t\v\f\r]+", logical.strip(SPACE), maxsplit=1)
        command, args = words[0].lower(), words[1] if len(words) == 2 else ""
        if command not in COMMANDS:
            reject(start, "unsupported instruction " + words[0])
        if command == "from":
            args = flags_and_args(args, start, "FROM")
            fields = re.split(r"[ \t\v\f\r]+", args.strip(SPACE))
            if (len(fields) not in {1, 3} or not fields[0]
                    or (len(fields) == 3 and (fields[1].lower() != "as"
                        or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", fields[2])))):
                reject(start, "unsupported FROM arguments (expected image [AS stage])")
            if any(c in fields[0] for c in "'$\"\\` "):
                reject(start, "unsupported FROM image syntax (use literal image references)")
            records.append((start, fields[0], fields[2] if len(fields) == 3 else ""))
        if command not in {"run", "copy", "add", "onbuild"} or "<<" not in args:
            continue
        if command == "onbuild":
            reject(start, "unsupported heredoc header in ONBUILD")
        args = flags_and_args(args, start, command.upper())
        if args.startswith("["):
            try:
                array = json.loads(args)
                if not isinstance(array, list) or any(not isinstance(item, str) for item in array):
                    raise ValueError()
            except ValueError:
                reject(start, "unsupported JSON/heredoc syntax")
            continue  # BuildKit never consumes heredoc bodies for JSON-form instructions.
        docs = []
        for word in re.split(r"[ \t\v\f\r]+", args):
            if "<<" in word:
                match = HEREDOC.fullmatch(word)
                if not match:
                    reject(start, "unsupported heredoc header (use simple literal delimiters)")
                docs.append((match[1] == "-", next(name for name in match.groups()[1:] if name is not None)))
            elif any(c in word for c in "'\"\\`$"):
                reject(start, "unsupported heredoc header (quotes/escapes/expansion outside a delimiter)")
        if not docs:
            reject(start, "unsupported heredoc header")
        for chomp, delimiter in docs:
            while i < len(lines):
                body = lines[i].lstrip("\t") if chomp else lines[i]
                i += 1
                if body == delimiter:
                    break
            else:
                reject(start, "unterminated heredoc " + delimiter)
    return records


try:
    for record in parse(sys.argv[1]):
        print("\t".join(map(str, record)))
except Unsupported as error:
    print(str(error.line) + "\t" + error.message)
    sys.exit(1)
PY
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
# Retain the first source location of every distinct external base, not just the two required .NET images.
external_refs=() external_files=() external_lines=()
external_seen=" "

for dockerfile in "${DOCKERFILES[@]}"; do
  if [[ ! -f "${root}/${dockerfile}" ]]; then
    problem "${dockerfile}" "" "was not found."
    continue
  fi
  if ! records=$(logical_froms "${root}/${dockerfile}"); then
    problem "${dockerfile}" "${records%%$'\t'*}" "${records#*$'\t'}"
    continue
  fi
  stages=" scratch " sdk_seen=0 runtime_seen=0
  while IFS=$'\t' read -r number ref stage; do
    [[ -n "${number}" ]] || continue
    # An earlier stage of this Dockerfile, or scratch, is not a base image to pin.
    folded_ref=$(printf '%s' "${ref}" | tr '[:upper:]' '[:lower:]')
    if [[ -n "${ref}" && "${stages}" != *" ${folded_ref} "* ]]; then
      if [[ "${external_seen}" != *" ${ref} "* ]]; then
        external_refs+=("${ref}") external_files+=("${dockerfile}") external_lines+=("${number}")
        external_seen+="${ref} "
      fi
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
    [[ -z "${stage}" ]] || stages+="$(printf '%s' "${stage}" | tr '[:upper:]' '[:lower:]') "
  done <<<"${records}"
  [[ ${sdk_seen} -eq 1 ]] || problem "${dockerfile}" "" "has no FROM ${SDK} line."
  [[ ${runtime_seen} -eq 1 ]] || problem "${dockerfile}" "" "has no FROM ${RUNTIME} line."
done

# Each pinned digest must be an index, not one platform's image: a single-platform image would be used for every
# platform built.
if [[ ${errors} -eq 0 && ${registry} -eq 1 ]]; then
  for ((i = 0; i < ${#external_refs[@]}; i++)); do
    ref=${external_refs[i]} file=${external_files[i]} line=${external_lines[i]}
    if ! raw=$(docker buildx imagetools inspect --raw "${ref}" 2>&1); then
      problem "${file}" "${line}" "${ref} could not be read from its registry: ${raw}"
      continue
    fi
    missing=0
    if ! jq -se 'length == 1 and (.[0] | type == "object" and .schemaVersion == 2 and
        (.mediaType == "application/vnd.docker.distribution.manifest.list.v2+json" or
         .mediaType == "application/vnd.oci.image.index.v1+json"))' <<<"${raw}" >/dev/null 2>&1; then
      problem "${file}" "${line}" "${ref} is not a schemaVersion 2 Docker manifest list or OCI image index."
      missing=1
    fi
    held=$(jq -r '(.manifests // [])[] | .platform // empty | "\(.os)/\(.architecture)"' <<<"${raw}" 2>/dev/null) || held=""
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
