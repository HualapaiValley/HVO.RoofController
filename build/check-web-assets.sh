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

# Read the real anonymous HTML without printing its antiforgery field. Follow fingerprinted styles from that page.
work=$(mktemp -d)
trap 'rm -rf "$work"; docker rm -f "$container" >/dev/null' EXIT
docker exec "$container" curl -fsS --max-time 10 http://127.0.0.1:8088/signin > "$work/signin.html"
python3 - "$work/signin.html" > "$work/styles.txt" <<'PY'
from html.parser import HTMLParser
import sys
class Styles(HTMLParser):
    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == 'link' and values.get('rel') == 'stylesheet':
            print(values['href'])
html = open(sys.argv[1]).read()
if '_content/HVO.WebSite.Themes' in html:
    raise SystemExit('Obsolete theme URL in published HTML')
Styles().feed(html)
PY
while IFS= read -r path; do
  mime=$(docker exec "$container" curl -fsS --max-time 5 -o /dev/null -w '%{content_type}' "http://127.0.0.1:8088/${path#/}")
  [[ "$mime" == text/css* ]] || { echo 'Published stylesheet returned incorrect MIME' >&2; exit 1; }
done < "$work/styles.txt"
# Inspect exactly the Dockerfile publish output, not a source build's asset cache.
docker cp "$container:/app/web" "$work/publish" >/dev/null
python3 "$(dirname "$0")/check-web-publish.py" "$work/publish"
for path in css/hvo-dark.css css/app.css js/stop.js js/ui.js Components/Roof/CameraStream.razor.js lib/bootstrap-icons/bootstrap-icons.min.css lib/bootstrap-icons/fonts/bootstrap-icons.woff2 lib/bootstrap-icons/fonts/bootstrap-icons.woff lib/bootstrap/LICENSE lib/bootstrap-icons/LICENSE; do
  docker exec "$container" curl -fsS --max-time 5 "http://127.0.0.1:8088/$path" > "$work/asset"
  cmp "$work/asset" "$work/publish/wwwroot/$path"
  mime=$(docker exec "$container" curl -fsS --max-time 5 -o /dev/null -w '%{content_type}' "http://127.0.0.1:8088/$path")
  case "$path:$mime" in
    *.css:text/css*|*.js:text/javascript*|*.js:application/javascript*|*.woff:application/font-woff*|*.woff:font/woff*|*.woff2:font/woff2*|*/LICENSE:*) ;;
    *) echo 'Published asset returned incorrect MIME' >&2; exit 1 ;;
  esac
done
legacy=$(docker exec "$container" curl -sS --max-time 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:8088/_content/HVO.WebSite.Themes/css/themes/hvo-dark.css)
[[ "$legacy" == 404 ]] || { echo 'Legacy theme URL still resolves' >&2; exit 1; }
echo 'PASS: published Web CSS, icon fonts/notices and fingerprint URLs are served; legacy theme is absent.'
