# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
# 2.2.3-10.0 ships .NET SDK 10.0.401, which satisfies src/global.json. CI ("Check dev container
# SDK") fails when this image's SDK falls outside the feature band that global.json pins.
FROM mcr.microsoft.com/devcontainers/dotnet:2.2.3-10.0

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
