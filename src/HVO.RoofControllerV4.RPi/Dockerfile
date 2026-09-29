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
COPY ["Directory.Build.props", "Directory.Packages.props", "/repo/"]
COPY ["src/global.json", "src/NuGet.config", "./"]
COPY ["src/HVO.RoofControllerV4.Common/HVO.RoofControllerV4.Common.csproj", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Client/HVO.RoofControllerV4.Client.csproj", "HVO.RoofControllerV4.Client/"]
COPY ["src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj", "HVO.RoofControllerV4.RPi/"]
COPY ["src/HVO.WebSite.Themes/HVO.WebSite.Themes.csproj", "HVO.WebSite.Themes/"]

# Map the target platform to a .NET runtime identifier (linux-arm64 for the Raspberry Pi 5).
RUN case "$TARGETARCH" in \
      arm64) echo linux-arm64 ;; \
      amd64) echo linux-x64 ;; \
      *) echo "Unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac > /tmp/rid && \
    echo "SDK $(dotnet --version); target RID $(cat /tmp/rid)"

# Restore with the Release configuration so the ReadyToRun (crossgen) packs are restored too.
RUN dotnet restore "HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj" \
    --configfile NuGet.config \
    --runtime "$(cat /tmp/rid)" \
    -p:Configuration=Release

# Copy the remainder of the needed sources and publish
COPY ["src/HVO.RoofControllerV4.Common/", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.Client/", "HVO.RoofControllerV4.Client/"]
COPY ["src/HVO.WebSite.Themes/", "HVO.WebSite.Themes/"]
COPY ["src/HVO.RoofControllerV4.RPi/", "HVO.RoofControllerV4.RPi/"]

RUN dotnet publish "HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj" \
    --no-restore \
    --configuration Release \
    --runtime "$(cat /tmp/rid)" \
    --self-contained false \
    --output /app/publish

# -------------------------
# Runtime image
# -------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
ARG HVO_FORCE_RASPBERRY_PI=false
ARG HVO_CONTAINER_RPI_HINT=""
WORKDIR /app

# Install the minimal native dependencies needed for GPIO/I2C access and health checks
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
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

# Copy the published binaries from the build stage
COPY --from=build /app/publish .

# Configure ASP.NET Core. The log levels come from appsettings.json, not the environment, so that the settings API
# can change them (an environment variable is a layer above the settings file, which would make them read-only).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    TZ=UTC \
    HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI} \
    HVO_CONTAINER_RPI_HINT=${HVO_CONTAINER_RPI_HINT}

# Expose the HTTP port used by the site
EXPOSE 8080

# Hardware-aware readiness check (anonymous endpoint; loopback is exempt from RequireHttps)
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

# Run the web site
ENTRYPOINT ["dotnet", "HVO.RoofControllerV4.RPi.dll"]
