# syntax=docker/dockerfile:1

# The HAT emulator (HVO.RoofControllerV4.Emulator): the emulated roof, drive, limit switches and HAT, for a roof
# controller running with HatEmulator:Enabled. It controls no hardware and maps no devices.
#
# Build context: the repository root (see /.dockerignore). Builds for linux/amd64 (a development machine or CI) and
# linux/arm64 (a Raspberry Pi test rig):
#   docker build -f src/HVO.RoofControllerV4.Emulator/Dockerfile -t hvo/roof-hat-emulator:dev .
#
# Ports:
#   5290  the HTTP control API (/api/emulator) and the emulated MJPEG camera (/mjpg/camNN/video.mjpg, the path the
#         controller's BlueIris:BaseUrl proxy reads). Neither has authentication: publish it on loopback only
#         (-p 127.0.0.1:5290:5290) or keep it on a private test network.
#   5291  the register port the controller's HatEmulator:Host and HatEmulator:Port point at. Reach it over a Docker
#         network (docker-compose.yaml, profile emulator); it does not need to be published.
#
# Settings (environment variables): Emulator__TimeScale, Emulator__TravelMeters, Emulator__InitialPosition,
# Emulator__RegisterPort, Emulator__CameraFramesPerSecond (see EmulatorHostOptions).
#
# Image pins: the same as src/HVO.RoofControllerV4.RPi/Dockerfile. The SDK tag must match src/global.json and the
# runtime tag should stay on the latest ASP.NET Core patch; the "Emulator image" workflow checks the SDK tag.

# -------------------------
# Build the emulator
# -------------------------
# The build stage runs on the builder's native platform and cross-compiles for the target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG TARGETARCH
# The product version and commit (docs/releasing.md). A release passes ROOF_VERSION=4.0.0, CI 4.0.0-ci.<run>, and the
# deploy script 4.0.0-dev; left empty, the build is Directory.Build.props's 4.0.0-dev. The image has no .git, so the
# commit comes from ROOF_REVISION.
ARG ROOF_VERSION=""
ARG ROOF_REVISION=""
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# Mirror the repository layout so Directory.*.props and src/global.json resolve exactly as in a local checkout.
WORKDIR /repo/src
COPY ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "/repo/"]
COPY ["src/global.json", "src/NuGet.config", "./"]
COPY ["src/HVO.RoofControllerV4.Common/HVO.RoofControllerV4.Common.csproj", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Simulation/HVO.RoofControllerV4.Simulation.csproj", "HVO.RoofControllerV4.Simulation/"]
COPY ["src/HVO.RoofControllerV4.Emulator/HVO.RoofControllerV4.Emulator.csproj", "HVO.RoofControllerV4.Emulator/"]

RUN case "$TARGETARCH" in \
      arm64) echo linux-arm64 ;; \
      amd64) echo linux-x64 ;; \
      *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac > /tmp/rid && \
    echo "SDK $(dotnet --version); target RID $(cat /tmp/rid)"

RUN dotnet restore "HVO.RoofControllerV4.Emulator/HVO.RoofControllerV4.Emulator.csproj" \
    --configfile NuGet.config \
    --runtime "$(cat /tmp/rid)" \
    -p:Configuration=Release

COPY ["src/HVO.RoofControllerV4.Common/", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Simulation/", "HVO.RoofControllerV4.Simulation/"]
COPY ["src/HVO.RoofControllerV4.Emulator/", "HVO.RoofControllerV4.Emulator/"]

RUN dotnet publish "HVO.RoofControllerV4.Emulator/HVO.RoofControllerV4.Emulator.csproj" \
    --no-restore \
    --configuration Release \
    --runtime "$(cat /tmp/rid)" \
    --self-contained false \
    ${ROOF_VERSION:+-p:Version=$ROOF_VERSION} \
    ${ROOF_REVISION:+-p:SourceRevisionId=$ROOF_REVISION} \
    --output /app/publish

# -------------------------
# Runtime image
# -------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
# The image's version, commit and source (OCI labels; build/check-image-labels.sh checks them). The source links a GHCR
# package to its repository.
ARG ROOF_VERSION=""
ARG ROOF_REVISION=""
ARG ROOF_CREATED=""
LABEL org.opencontainers.image.title="HVO Roof Controller HAT emulator" \
      org.opencontainers.image.description="The emulated roof, drive, limit switches and HAT, for testing the roof controller without hardware." \
      org.opencontainers.image.source="https://github.com/HualapaiValley/HVO.RoofController" \
      org.opencontainers.image.vendor="Hualapai Valley Observatory" \
      org.opencontainers.image.version="${ROOF_VERSION}" \
      org.opencontainers.image.revision="${ROOF_REVISION}" \
      org.opencontainers.image.created="${ROOF_CREATED}"

WORKDIR /app

# curl for the health check only.
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Listen on all interfaces inside the container; the emulator's own default is loopback only.
ENV ASPNETCORE_URLS=http://+:5290 \
    ASPNETCORE_ENVIRONMENT=Production \
    Emulator__RegisterAddress=0.0.0.0 \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning \
    TZ=UTC

EXPOSE 5290 5291

# The emulator needs no privileges: run as the image's unprivileged app user.
USER $APP_UID

HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:5290/api/emulator/status >/dev/null || exit 1

ENTRYPOINT ["dotnet", "HVO.RoofControllerV4.Emulator.dll"]
