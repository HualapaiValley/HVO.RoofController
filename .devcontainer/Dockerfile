# See here for image contents: https://github.com/devcontainers/images/tree/main/src/dotnet
FROM mcr.microsoft.com/devcontainers/dotnet:2.1.4-10.0

# The base image may include a Yarn APT source without the signing key,
# causing apt failures. Remove it since we don't need it.
RUN set -e; \
    rm -f /etc/apt/sources.list.d/yarn.list || true; \
    rm -f /etc/apt/sources.list.d/yarn.sources || true; \
    apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
