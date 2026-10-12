#!/usr/bin/env bash
# Exercise the published web process without a controller, credentials, published ports or hardware.
# Run against the actual Dockerfile output: an ordinary dotnet build has Razor files at restore time
# and cannot catch missing framework assets in the Docker restore-before-COPY sequence.
set -euo pipefail
[[ $# -eq 1 ]] || { echo "Usage: $0 <controller-image>" >&2; exit 2; }
container=$(docker run -d --network none --entrypoint dotnet --workdir /app/web \
  -e ASPNETCORE_ENVIRONMENT=Production -e RoofWeb__Urls=http://127.0.0.1:8088 \
  "$1" HVO.RoofControllerV4.Web.dll)
trap 'docker rm -f "$container" >/dev/null' EXIT

docker exec "$container" curl -fsS --retry 20 --retry-connrefused --retry-delay 1 \
  --max-time 2 -o /dev/null http://127.0.0.1:8088/health/live
mime=$(docker exec "$container" curl -fsS --max-time 5 -o /dev/null -w '%{content_type}' \
  http://127.0.0.1:8088/_framework/blazor.web.js)
case "$mime" in
  text/javascript*|application/javascript*) ;;
  *) echo "Blazor bootstrap is not JavaScript: $mime" >&2; exit 1 ;;
esac
status=$(docker exec "$container" curl -sS --max-time 5 -o /dev/null -w '%{http_code}' \
  -X POST -H 'Origin: http://127.0.0.1:8088' \
  'http://127.0.0.1:8088/_blazor/negotiate?negotiateVersion=1')
[[ "$status" == 401 ]] || { echo "Anonymous live connection returned $status, expected 401" >&2; exit 1; }
echo 'PASS: published Blazor script loads; anonymous live connection remains refused.'
