#!/bin/bash
# SessionStart hook for Claude Code on the web: sets up the cloud container the way
# .devcontainer/ sets up the dev container, minus the parts that only make sense on a
# developer machine (docker contexts, SSH keys, dev certs, VS Code extensions).
set -euo pipefail

# Local sessions use the dev container; only the cloud sandbox needs this.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
	exit 0
fi

REPO_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
GLOBAL_JSON="${REPO_DIR}/src/global.json"
DOTNET_DIR="${HOME}/.dotnet"
SDK_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${GLOBAL_JSON}" | head -n 1)"

log() { echo "[session-start] $*" >&2; }

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
	SUDO="sudo"
fi

# --- System packages (mirrors post-create.sh) --------------------------------------------
# libgpiod/i2c-tools back the RPi HAT libraries; the fonts are used by rendering code.
APT_PACKAGES=(jq ripgrep libgpiod-dev i2c-tools fontconfig fonts-dejavu-core fonts-open-sans)
missing=()
for pkg in "${APT_PACKAGES[@]}"; do
	dpkg -s "${pkg}" >/dev/null 2>&1 || missing+=("${pkg}")
done
if [ "${#missing[@]}" -gt 0 ]; then
	log "Installing apt packages: ${missing[*]}"
	export DEBIAN_FRONTEND=noninteractive
	# Third-party PPAs in the base image can be unreachable; that must not stop the install.
	${SUDO} apt-get update -y >/dev/null 2>&1 || true
	${SUDO} apt-get install -y --no-install-recommends "${missing[@]}" >/dev/null 2>&1 \
		|| log "Warning: some apt packages failed to install, continuing"
	${SUDO} fc-cache -f >/dev/null 2>&1 || true
fi

# --- .NET SDK pinned by src/global.json ---------------------------------------------------
# rollForward "patch": any SDK in the pinned feature band at or above the pinned patch.
sdk_satisfies_pin() {
	local dotnet="$1" band="${SDK_VERSION%??}" min="${SDK_VERSION##*.}" v
	[ -x "${dotnet}" ] || return 1
	while read -r v _; do
		case "${v}" in
			"${band}"[0-9][0-9]) [ "${v##*.}" -ge "${min}" ] && return 0 ;;
		esac
	done < <("${dotnet}" --list-sdks 2>/dev/null)
	return 1
}

# Preferred: the official installer. Needs builds.dotnet.microsoft.com (and dot.net or
# raw.githubusercontent.com for the script) in the environment's allowed network domains.
install_with_dotnet_install() {
	local script
	script="$(mktemp)"
	if ! curl -fsSL --retry 3 https://dot.net/v1/dotnet-install.sh -o "${script}" 2>/dev/null \
		&& ! curl -fsSL --retry 3 https://raw.githubusercontent.com/dotnet/install-scripts/main/src/dotnet-install.sh -o "${script}" 2>/dev/null; then
		rm -f "${script}"
		return 1
	fi
	bash "${script}" --jsonfile "${GLOBAL_JSON}" --install-dir "${DOTNET_DIR}" --no-path >/dev/null 2>&1
	local rc=$?
	rm -f "${script}"
	return ${rc}
}

# Fallback: copy /usr/share/dotnet out of the official mcr.microsoft.com/dotnet/sdk image,
# which the default network policy allows. Same bits as the installer, no docker needed.
install_from_mcr() {
	local repo="dotnet/sdk" tag="${SDK_VERSION}-noble" registry="https://mcr.microsoft.com/v2"
	local accept_index='application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json'
	local accept_manifest='application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'
	local digest layers layer
	digest="$(curl -fsSL -H "Accept: ${accept_index}" "${registry}/${repo}/manifests/${tag}" \
		| jq -r '.manifests[] | select(.platform.os=="linux" and .platform.architecture=="amd64") | .digest' | head -n 1)" || return 1
	[ -n "${digest}" ] || return 1
	layers="$(curl -fsSL -H "Accept: ${accept_manifest}" "${registry}/${repo}/manifests/${digest}" | jq -r '.layers[].digest')" || return 1
	mkdir -p "${DOTNET_DIR}"
	for layer in ${layers}; do
		curl -fsSL --retry 3 "${registry}/${repo}/blobs/${layer}" \
			| tar -xz -C "${DOTNET_DIR}" --strip-components=3 --wildcards 'usr/share/dotnet/*' 2>/dev/null || true
	done
	[ -x "${DOTNET_DIR}/dotnet" ]
}

if sdk_satisfies_pin "${DOTNET_DIR}/dotnet"; then
	log ".NET SDK for ${SDK_VERSION} already present in ${DOTNET_DIR}"
else
	log "Installing .NET SDK ${SDK_VERSION} into ${DOTNET_DIR}"
	if ! install_with_dotnet_install || ! sdk_satisfies_pin "${DOTNET_DIR}/dotnet"; then
		log "dotnet-install.sh unavailable (builds.dotnet.microsoft.com blocked?); using mcr.microsoft.com/dotnet/sdk:${SDK_VERSION}-noble"
		install_from_mcr || true
	fi
	if ! sdk_satisfies_pin "${DOTNET_DIR}/dotnet"; then
		log "ERROR: could not install .NET SDK ${SDK_VERSION}. Allow builds.dotnet.microsoft.com or mcr.microsoft.com in the environment's network settings."
		exit 1
	fi
fi

export DOTNET_ROOT="${DOTNET_DIR}"
export PATH="${DOTNET_DIR}:${DOTNET_DIR}/tools:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Persist for the session's shell (matches the dev container's containerEnv and CI's env).
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
	cat >>"${CLAUDE_ENV_FILE}" <<EOF
export DOTNET_ROOT="${DOTNET_DIR}"
export PATH="${DOTNET_DIR}:${DOTNET_DIR}/tools:\${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ENVIRONMENT=Development
EOF
fi

# --- NuGet restore ------------------------------------------------------------------------
# Run from src/ so src/global.json selects the SDK (same as CI and post-create.sh).
log "Restoring NuGet packages"
cd "${REPO_DIR}/src"
dotnet restore HVO.RoofController.sln --configfile NuGet.config -v quiet >&2

log "Done: $(dotnet --version)"
