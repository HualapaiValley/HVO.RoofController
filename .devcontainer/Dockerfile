# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
# Pin the complete image version, .NET line and Ubuntu variant, plus the multi-platform index.
# Dependabot updates this tag and digest; build/check-base-images.sh validates the pin.
# This newest version still ships SDK 10.0.401; global.json retains that minimum with patch
# roll-forward until an upstream dev-container image ships the product SDK 10.0.403 (#141).
FROM mcr.microsoft.com/devcontainers/dotnet:2.3.2-10.0-noble@sha256:694076b55f447d4b9f5b49bc6616c2e904d63e1a61e43680b2bbea46011b6783

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
