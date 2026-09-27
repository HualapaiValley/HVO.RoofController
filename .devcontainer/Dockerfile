# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
# 2.1.4-10.0 ships .NET SDK 10.0.302, which satisfies src/global.json. When changing this tag
# or global.json, check that the image's SDK is still in the pinned feature band.
FROM mcr.microsoft.com/devcontainers/dotnet:2.1.4-10.0

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
