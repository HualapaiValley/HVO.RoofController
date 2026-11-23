# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
# Pin the .NET line and Ubuntu variant with the multi-platform index digest.
# Dependabot updates this tag and digest; build/check-base-images.sh validates the pin.
# SDK 10.0.404 (runtime 10.0.15) satisfies the patch roll-forward in src/global.json.
FROM mcr.microsoft.com/devcontainers/dotnet:10.0-noble@sha256:86b40dd4bb6d4b58e9cee1f997203ca084e8447aa5a29df50c70c7bf9f1ffcc9

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
