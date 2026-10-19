#!/usr/bin/env python3
"""Validate an actual published Web directory, including fingerprint routes and font/icon notices."""
import argparse
import hashlib
import json
import re
from pathlib import Path
from html.parser import HTMLParser
from urllib.request import urlopen
from urllib.error import HTTPError
from urllib.parse import unquote, urlsplit, urljoin

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / 'src/HVO.RoofControllerV4.Web'
REQUIRED = (
    'css/hvo-dark.css', 'css/app.css', 'js/stop.js', 'js/ui.js',
    'lib/bootstrap/css/bootstrap.min.css', 'lib/bootstrap/LICENSE',
    'lib/bootstrap-icons/bootstrap-icons.min.css', 'lib/bootstrap-icons/LICENSE',
    'lib/bootstrap-icons/fonts/bootstrap-icons.woff', 'lib/bootstrap-icons/fonts/bootstrap-icons.woff2',
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check(publish, source=WEB, contract=ROOT / 'docs/ui-redesign/tokens.json'):
    """Check shipped bytes, dependencies, every local CSS reference and the SDK's serving manifest."""
    static = publish / 'wwwroot'
    for relative in REQUIRED:
        actual, expected = static / relative, source / 'wwwroot' / relative
        require(actual.is_file() and actual.stat().st_size > 0, f'Missing published asset: {relative}')
        require(hashlib.sha256(actual.read_bytes()).digest() == hashlib.sha256(expected.read_bytes()).digest(),
                f'Published asset differs from source: {relative}')
    roles = json.loads(contract.read_text())['roles']
    css = (static / 'css/hvo-dark.css').read_text()
    for token in roles:
        definitions = re.findall(r'--roof-' + re.escape(token['role']) + r'\s*:\s*([^;]+);', css)
        require(definitions == [token['value']], f'Canonical CSS role differs: {token["role"]}')
    for path in static.rglob('*.css'):
        text = path.read_text()
        require('HVO.WebSite.Themes' not in text, 'Obsolete theme dependency in published CSS')
        references = re.findall(r'url\(\s*[\'"]?([^\)\'"\s;]+)', text)
        references += re.findall(r'@import\s+[\'"]([^\'"]+)[\'"]', text)
        for match in references:
            if match.startswith('data:'):
                continue
            parsed = urlsplit(match)
            require(not parsed.scheme and not parsed.netloc, f'Nonlocal CSS dependency in {path.name}')
            target = static / unquote(parsed.path).lstrip('/') if parsed.path.startswith('/') else path.parent / unquote(parsed.path)
            require(target.resolve().is_relative_to(static.resolve()) and target.is_file(),
                    f'Missing published CSS dependency from {path.name}')
    manifest = publish / 'HVO.RoofControllerV4.Web.staticwebassets.endpoints.json'
    require(manifest.is_file(), 'Missing published endpoint manifest')
    endpoints = json.loads(manifest.read_text())['Endpoints']
    require(not any('HVO.WebSite.Themes' in json.dumps(e) for e in endpoints), 'Obsolete theme endpoint')
    for relative in REQUIRED:
        # Notices are ordinary static files; CSS, JS and fonts also require their fingerprinted Assets routes.
        matches = [e for e in endpoints if e['AssetFile'] == relative]
        require(any(e['Route'] == relative for e in matches), f'Missing published route: {relative}')
        if relative.endswith(('.css', '.js', '.woff', '.woff2')):
            require(any(e['Route'] != relative for e in matches), f'Missing fingerprint route: {relative}')
    scoped = 'HVO.RoofControllerV4.Web.styles.css'
    require((static / scoped).is_file() and (static / scoped).stat().st_size > 0, 'Missing scoped component styles')
    deps = publish / 'HVO.RoofControllerV4.Web.deps.json'
    require(deps.is_file(), 'Missing published dependency manifest')
    require(not any(name.startswith('HVO.WebSite.Themes/') for name in json.loads(deps.read_text())['libraries']),
            'Obsolete theme runtime assembly')
    require(not list(publish.rglob('HVO.WebSite.Themes*')), 'Obsolete theme file in publish output')
    return len(endpoints)


def check_http(publish, base_url):
    """Exercise only a controlled loopback Web process; never a live controller or authenticated API."""
    parsed = urlsplit(base_url)
    require(parsed.scheme == 'http' and parsed.hostname in ('127.0.0.1', 'localhost') and not parsed.username,
            'HTTP asset validation requires a controlled loopback Web process')
    with urlopen(urljoin(base_url, '/signin'), timeout=10) as response:
        html = response.read().decode()
    require('_content/HVO.WebSite.Themes' not in html, 'Obsolete theme URL in served HTML')
    class Assets(HTMLParser):
        def __init__(self):
            super().__init__()
            self.paths = []
        def handle_starttag(self, tag, attrs):
            values = dict(attrs)
            if tag == 'link' and values.get('rel') == 'stylesheet':
                self.paths.append(values['href'])
    assets = Assets()
    assets.feed(html)
    require(any(path.startswith('css/hvo-dark.') for path in assets.paths), 'Missing fingerprinted theme in HTML')
    endpoints = json.loads((publish / 'HVO.RoofControllerV4.Web.staticwebassets.endpoints.json').read_text())['Endpoints']
    for route in [*assets.paths, *REQUIRED]:
        mapping = next((e['AssetFile'] for e in endpoints if e['Route'] == route and not e.get('Selectors')), None)
        require(mapping is not None, 'Served asset missing from endpoint manifest')
        with urlopen(urljoin(base_url, '/' + route), timeout=5) as response:
            require(response.read() == (publish / 'wwwroot' / mapping).read_bytes(), 'Served bytes differ from publish output')
            if route.endswith('.css'):
                require(response.headers.get_content_type() == 'text/css', 'Incorrect served stylesheet MIME')
    try:
        urlopen(urljoin(base_url, '/_content/HVO.WebSite.Themes/css/themes/hvo-dark.css'), timeout=5)
    except HTTPError as error:
        require(error.code == 404, 'Legacy theme URL must return 404')
    else:
        raise ValueError('Legacy theme URL still resolves')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('publish', type=Path)
    parser.add_argument('--base-url', help='Optional controlled loopback published Web process')
    args = parser.parse_args()
    try:
        count = check(args.publish)
        if args.base_url:
            check_http(args.publish, args.base_url)
        print(f'PASS: published Web assets, 20 roles, notices, CSS/font references and {count} serving routes')
    except (ValueError, KeyError, OSError) as error:
        raise SystemExit(f'FAIL: {error}') from error


if __name__ == '__main__':
    main()
