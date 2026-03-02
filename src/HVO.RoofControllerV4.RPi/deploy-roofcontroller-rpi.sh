#!/usr/bin/env bash
set -euo pipefail

# Project-local deploy script for Roof Controller V4 (RPi)

if [[ -z "${PI_HOST:-}" ]]; then
  echo "PI_HOST environment variable is required" >&2
  exit 1
fi

DOCKER_CONTEXT=${DOCKER_CONTEXT:-rpi-remote}
IMAGE_TAG=${IMAGE_TAG:-hvov9/roof-controller:v4}
CONTAINER_NAME=${CONTAINER_NAME:-roof-controller}
HOST_PORT=${HOST_PORT:-8080}
EXTRA_DOCKER_ARGS=${EXTRA_DOCKER_ARGS:-}

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "${SCRIPT_DIR}/../../.." && pwd)
DOCKERFILE_PATH="${SCRIPT_DIR}/Dockerfile"

if ! docker --context "${DOCKER_CONTEXT}" info >/dev/null 2>&1; then
  echo "Docker context '${DOCKER_CONTEXT}' is not available. Configure it first (e.g. docker context use ${DOCKER_CONTEXT})." >&2
  exit 1
fi

if ! docker buildx version >/dev/null 2>&1; then
  echo "docker buildx is required but not available. Install Docker Buildx and try again." >&2
  exit 1
fi

echo "[build] Building ${IMAGE_TAG} for linux/arm64..."
docker buildx build \
  --platform linux/arm64 \
  -f "${DOCKERFILE_PATH}" \
  -t "${IMAGE_TAG}" \
  --load \
  "${REPO_ROOT}"

TMP_TAR=$(mktemp)
trap 'rm -f "${TMP_TAR}"' EXIT

docker save "${IMAGE_TAG}" -o "${TMP_TAR}"

echo "[deploy] Loading image into Docker context '${DOCKER_CONTEXT}'"
docker --context "${DOCKER_CONTEXT}" load < "${TMP_TAR}"

echo "[deploy] Removing any existing container named ${CONTAINER_NAME}"
docker --context "${DOCKER_CONTEXT}" rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true

echo "[deploy] Starting container on ${PI_HOST}"

run_cmd=(
  docker --context "${DOCKER_CONTEXT}" run -d
  --name "${CONTAINER_NAME}"
  --restart unless-stopped
  -p "${HOST_PORT}:8080"
  --device /dev/gpiomem:/dev/gpiomem
  --device /dev/i2c-1:/dev/i2c-1
  --mount type=bind,src=/sys/class/thermal/thermal_zone0/temp,dst=/sys/class/thermal/thermal_zone0/temp,readonly
)

if [[ -n "${EXTRA_DOCKER_ARGS}" ]]; then
  # shellcheck disable=SC2206
  extra_args=(${EXTRA_DOCKER_ARGS})
  run_cmd+=("${extra_args[@]}")
fi

run_cmd+=("${IMAGE_TAG}")

"${run_cmd[@]}"

echo "[deploy] Container status"
docker --context "${DOCKER_CONTEXT}" ps --filter "name=${CONTAINER_NAME}" --format "table {{.Names}}\t{{.Status}}\t{{.Image}}"

echo "[done] Deployment complete. Roof controller is reachable on http://${PI_HOST}:${HOST_PORT}"
