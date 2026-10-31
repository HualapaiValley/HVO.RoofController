# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
# Pin the complete image version, .NET line and Ubuntu variant, plus the multi-platform index.
# Dependabot updates this tag and digest; build/check-base-images.sh validates the pin.
# This version ships SDK 10.0.403 (runtime 10.0.14), the product SDK that src/global.json names (#141).
FROM mcr.microsoft.com/devcontainers/dotnet:2.3.4-10.0-noble@sha256:734d73c596e73d558145c6d2c2fc646021f48cc7fb94b7446cbc1eeeddb90db9

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
