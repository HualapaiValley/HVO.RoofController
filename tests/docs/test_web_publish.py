"""Regression guards for the actual published-Web checker (#174)."""
import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('web_publish', ROOT / 'build/check-web-publish.py')
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)


class WebPublishTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.publish = Path(self.temp.name)
        self.static = self.publish / 'wwwroot'
        shutil.copytree(CHECKER.WEB / 'wwwroot', self.static)
        module = Path('Components/Roof/CameraStream.razor.js')
        (self.static / module).parent.mkdir(parents=True)
        shutil.copyfile(CHECKER.WEB / module, self.static / module)
        (self.static / 'HVO.RoofControllerV4.Web.styles.css').write_text('/* scoped components */')
        self.manifest = self.publish / 'HVO.RoofControllerV4.Web.staticwebassets.endpoints.json'
        endpoints = []
        for relative in CHECKER.REQUIRED:
            endpoints.append({'AssetFile': relative, 'Route': relative})
            endpoints.append({'AssetFile': relative, 'Route': relative + '.fingerprint'})
        self.manifest.write_text(json.dumps({'Endpoints': endpoints}))
        (self.publish / 'HVO.RoofControllerV4.Web.deps.json').write_text('{"libraries":{}}')

    def test_all_canonical_roles_assets_notices_and_routes_are_required(self):
        self.assertEqual(CHECKER.check(self.publish), len(CHECKER.REQUIRED) * 2)

    def test_missing_shipped_font_fails_even_when_css_is_present(self):
        (self.static / 'lib/bootstrap-icons/fonts/bootstrap-icons.woff2').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing published asset'):
            CHECKER.check(self.publish)

    def test_missing_notice_fails(self):
        (self.static / 'lib/bootstrap-icons/LICENSE').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing published asset'):
            CHECKER.check(self.publish)

    def test_stale_shipped_css_fails(self):
        with (self.static / 'css/hvo-dark.css').open('a') as stream:
            stream.write('/* stale */')
        with self.assertRaisesRegex(ValueError, 'differs from source'):
            CHECKER.check(self.publish)

    def test_unmapped_fingerprint_fails_even_when_files_exist(self):
        manifest = json.loads(self.manifest.read_text())
        manifest['Endpoints'] = [e for e in manifest['Endpoints'] if e['Route'] != 'css/hvo-dark.css.fingerprint']
        self.manifest.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'Missing fingerprint route'):
            CHECKER.check(self.publish)

    def test_obsolete_runtime_dependency_fails(self):
        (self.publish / 'HVO.RoofControllerV4.Web.deps.json').write_text('{"libraries":{"HVO.WebSite.Themes/4.0.0":{}}}')
        with self.assertRaisesRegex(ValueError, 'Obsolete theme runtime'):
            CHECKER.check(self.publish)

    def test_new_broken_css_reference_fails(self):
        (self.static / 'bad.css').write_text('@font-face {src:url("lost.woff2")}')
        with self.assertRaisesRegex(ValueError, 'Missing published CSS dependency'):
            CHECKER.check(self.publish)

    def test_canonical_role_drift_fails_even_if_published_bytes_match_source(self):
        source = self.publish / 'source'
        shutil.copytree(CHECKER.WEB / 'wwwroot', source / 'wwwroot')
        module = Path('Components/Roof/CameraStream.razor.js')
        (source / module).parent.mkdir(parents=True)
        shutil.copyfile(CHECKER.WEB / module, source / module)
        for path in (self.static / 'css/hvo-dark.css', source / 'wwwroot/css/hvo-dark.css'):
            path.write_text(path.read_text().replace('--roof-background: #05070d;', '--roof-background: #ffffff;'))
        with self.assertRaisesRegex(ValueError, 'Canonical CSS role differs: background'):
            CHECKER.check(self.publish, source=source)

    def test_css_import_url_resolves_its_relative_file(self):
        (self.static / 'extra.css').write_text('@import url("nested.css");')
        (self.static / 'nested.css').write_text('/* imported */')
        self.assertGreater(CHECKER.check(self.publish), 0)

    def test_missing_camera_module_fails_even_when_bootstrap_exists(self):
        (self.static / 'Components/Roof/CameraStream.razor.js').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing published asset'):
            CHECKER.check(self.publish)
