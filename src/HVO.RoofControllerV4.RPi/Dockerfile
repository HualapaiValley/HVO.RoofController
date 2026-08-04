# syntax=docker/dockerfile:1

# -------------------------
# Build the application
# -------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution metadata and project files to warm the restore cache
COPY ["src/global.json", "./"]
COPY ["src/NuGet.config", "./"]
COPY ["Directory.Build.props", "../"]
COPY ["Directory.Packages.props", "../"]
COPY ["src/HVO.RoofControllerV4.Common/HVO.RoofControllerV4.Common.csproj", "HVO.RoofControllerV4.Common/"]
COPY ["src/HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj", "HVO.RoofControllerV4.RPi/"]
COPY ["src/HVO.WebSite.Themes/HVO.WebSite.Themes.csproj", "HVO.WebSite.Themes/"]

# Restore dependencies for the target runtime (linux-arm64 for Raspberry Pi 5)
RUN dotnet restore "HVO.RoofControllerV4.RPi/HVO.RoofControllerV4.RPi.csproj" \
    --runtime linux-arm64 \
    --disable-parallel

# Copy the remainder of the source tree and publish the app
COPY src/ ./
WORKDIR /src/HVO.RoofControllerV4.RPi

RUN dotnet publish "HVO.RoofControllerV4.RPi.csproj" \
    --configuration Release \
    --runtime linux-arm64 \
    --self-contained false \
    --output /app/publish

# -------------------------
# Runtime image
# -------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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

# Configure ASP.NET Core and logging defaults
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning \
    TZ=UTC \
    HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI} \
    HVO_CONTAINER_RPI_HINT=${HVO_CONTAINER_RPI_HINT}

# Expose the HTTP port used by the site
EXPOSE 8080

# Lightweight health check endpoint
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

# Run the web site
ENTRYPOINT ["dotnet", "HVO.RoofControllerV4.RPi.dll"]
