# syntax=docker/dockerfile:1

# Build context: the repository root (see /.dockerignore).
#
# Image pins:
# - The SDK tag must satisfy src/global.json (10.0.401, rollForward: patch). A floating
#   sdk:10.0 tag can move to a newer feature band, which global.json rejects. When the SDK pin
#   changes, update src/global.json and this tag in the same change; the "Pi image" workflow
#   checks that they agree and builds linux/arm64.
# - The runtime tag pins an ASP.NET Core patch release. The app is framework-dependent, so
#   it runs on the newest 10.0 patch installed in the image; keep this on the latest patch
#   for security fixes (Dependabot proposes updates).
# - Version tags are mutable. If immutable image provenance is required, append a reviewed
#   digest (sdk:10.0.401@sha256:...) and update it deliberately.

# -------------------------
# Build the application
# -------------------------
# The build stage runs on the builder's native platform and cross-compiles for the target,
# so an arm64 image built on an x64 machine does not run the SDK under emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG TARGETARCH
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Mirror the repository layout so Directory.*.props and src/global.json resolve exactly as in
# a local checkout. Every dotnet command runs from /repo/src, where global.json applies.
WORKDIR /repo/src
COPY ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "/repo/"]
COPY ["src/global.json", "src/NuGet.config", "./"]
COPY ["src/HVO.RoofControllerV4.Common/HVO.RoofControllerV4.Common.csproj", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Client/HVO.RoofControllerV4.Client.csproj", "HVO.RoofControllerV4.Client/"]
COPY ["src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj", "HVO.RoofControllerV4.RPi/"]
COPY ["src/HVO.RoofControllerV4.Web/HVO.RoofControllerV4.Web.csproj", "HVO.RoofControllerV4.Web/"]
COPY ["src/HVO.WebSite.Themes/HVO.WebSite.Themes.csproj", "HVO.WebSite.Themes/"]

# Map the target platform to a .NET runtime identifier (linux-arm64 for the Raspberry Pi 5).
RUN case "$TARGETARCH" in \
      arm64) echo linux-arm64 ;; \
      amd64) echo linux-x64 ;; \
      *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac > /tmp/rid && \
    echo "SDK $(dotnet --version); target RID $(cat /tmp/rid)"

# Restore with the Release configuration so the ReadyToRun (crossgen) packs are restored too.
RUN for project in HVO.RoofControllerV4.RPi HVO.RoofControllerV4.Web; do \
      dotnet restore "$project/$project.csproj" \
        --configfile NuGet.config \
        --runtime "$(cat /tmp/rid)" \
        -p:Configuration=Release || exit 1; \
    done

# Copy the remainder of the needed sources and publish
COPY ["src/HVO.RoofControllerV4.Common/", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Client/", "HVO.RoofControllerV4.Client/"]
COPY ["src/HVO.WebSite.Themes/", "HVO.WebSite.Themes/"]
COPY ["src/HVO.RoofControllerV4.RPi/", "HVO.RoofControllerV4.RPi/"]
COPY ["src/HVO.RoofControllerV4.Web/", "HVO.RoofControllerV4.Web/"]

# The product version and commit (docs/releasing.md). A release passes ROOF_VERSION=4.0.0, CI 4.0.0-ci.<run>, and the
# deploy script 4.0.0-dev; left empty, the build is Directory.Build.props's 4.0.0-dev. The image has no .git, so the
# commit comes from ROOF_REVISION. Declared here, after the restore: every RUN after an ARG sees it, so a new version
# would otherwise restore again.
ARG ROOF_VERSION=""
ARG ROOF_REVISION=""

# The controller in /app and its web UI in /app/web: two processes, each with its own dependencies.
RUN dotnet publish "HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj" \
    --no-restore \
    --configuration Release \
    --runtime "$(cat /tmp/rid)" \
    --self-contained false \
    ${ROOF_VERSION:+-p:Version=$ROOF_VERSION} \
    ${ROOF_REVISION:+-p:SourceRevisionId=$ROOF_REVISION} \
    --output /app/publish && \
    dotnet publish "HVO.RoofControllerV4.Web/HVO.RoofControllerV4.Web.csproj" \
    --no-restore \
    --configuration Release \
    --runtime "$(cat /tmp/rid)" \
    --self-contained false \
    ${ROOF_VERSION:+-p:Version=$ROOF_VERSION} \
    ${ROOF_REVISION:+-p:SourceRevisionId=$ROOF_REVISION} \
    --output /app/publish/web

# -------------------------
# Runtime image
# -------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
ARG HVO_FORCE_RASPBERRY_PI=false
ARG HVO_CONTAINER_RPI_HINT=""
WORKDIR /app

# Install the minimal native dependencies needed for GPIO/I2C access, health checks and the supervisor (tini)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    tini \
    fontconfig \
    fonts-dejavu-core \
    fonts-liberation2 \
    fonts-noto-core \
    fonts-noto-mono \
    fonts-noto-color-emoji \
    fonts-roboto \
    fonts-open-sans \
    libgpiod2 \
    i2c-tools && \
    rm -rf /var/lib/apt/lists/* && \
    fc-cache -f

# Copy the published binaries from the build stage, and the supervisor and health check
# (docs/deployment.md, "The container's two processes")
COPY --from=build /app/publish .
COPY --chmod=0755 ["src/HVO.RoofControllerV4.RPi/container/roof-supervisor.sh", "/usr/local/bin/roof-supervisor"]
COPY --chmod=0755 ["src/HVO.RoofControllerV4.RPi/container/healthcheck.sh", "/usr/local/bin/roof-healthcheck"]

# Configure ASP.NET Core. The log levels come from appsettings.json, not the environment, so that the settings API
# can change them (an environment variable is a layer above the settings file, which would make them read-only).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    TZ=UTC \
    HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI} \
    HVO_CONTAINER_RPI_HINT=${HVO_CONTAINER_RPI_HINT} \
    RoofWeb__Urls=http://+:8088

# The controller's API, hub, health and OpenAPI (8080, and 8443 for HTTPS), and the web UI (8088)
EXPOSE 8080 8088

# The image's version, commit and source (OCI labels; build/check-image-labels.sh checks them). The source links a GHCR
# package to its repository. After the last RUN, so that a new build time or commit invalidates no layer.
ARG ROOF_VERSION=""
ARG ROOF_REVISION=""
ARG ROOF_CREATED=""
LABEL org.opencontainers.image.title="HVO Roof Controller" \
      org.opencontainers.image.description="The roll-off roof controller and its web UI: on a Raspberry Pi 5 with its HAT, or anywhere with the HAT emulator." \
      org.opencontainers.image.source="https://github.com/HualapaiValley/HVO.RoofController" \
      org.opencontainers.image.vendor="Hualapai Valley Observatory" \
      org.opencontainers.image.version="${ROOF_VERSION}" \
      org.opencontainers.image.revision="${ROOF_REVISION}" \
      org.opencontainers.image.created="${ROOF_CREATED}"

# Healthy exactly when the controller reports ready (anonymous endpoint; loopback is exempt from RequireHttps). The web
# UI's liveness and the supervisor's view are printed alongside and never change the result.
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD ["/usr/local/bin/roof-healthcheck"]

# tini is PID 1 and passes docker stop's SIGTERM to the supervisor, which stops the controller first (its shutdown stops
# the roof), then the web UI. Arguments (--validate-deployment) run the controller alone with them.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/roof-supervisor"]
