#!/bin/bash
set -e
set -o pipefail

command_exists() {
	command -v "$1" >/dev/null 2>&1
}

echo "Running post-create setup..."

# Docker may create this SDK-state directory as root when mounting the certificate volume.
# Fix only the parent directory; never recurse into existing certificates or change their access.
sudo chown vscode:vscode /home/vscode/.dotnet

# Setup must not create or replace credentials, including the SDK first-use certificate.
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false

# Display .NET version
echo "Checking .NET installation..."
dotnet --info
echo "Installed SDKs:"
dotnet --list-sdks || true

# Install development CLI utilities and IoT/hardware dependencies
echo "Installing development CLI utilities and hardware libraries..."
sudo apt-get update -y
sudo apt-get install -y --no-install-recommends \
	jq ripgrep \
	libgpiod-dev i2c-tools \
	fontconfig fonts-dejavu-core fonts-open-sans \
	|| echo "Warning: Some package installations failed, continuing..."
sudo fc-cache -f || true

# Show the Docker CLI version; check daemon readiness separately with docker info.
if command_exists docker; then
	docker --version
else
	echo "Warning: docker CLI not found on PATH"
fi

# SSH, remote Docker contexts and HTTPS certificates are opt-in; see README.md.

# Restore the solution from src/ so src/global.json selects the SDK.
echo "Restoring NuGet packages..."
(cd src && dotnet restore HVO.RoofController.sln --configfile NuGet.config)

echo "Post-create setup completed successfully!"
