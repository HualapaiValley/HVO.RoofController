"""Optional Compose overlay: no server/discovery or published port until explicitly selected."""
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
COMPOSE = ROOT / 'src/HVO.RoofControllerV4.RPi/docker-compose.yaml'
OVERLAY = COMPOSE.with_name('docker-compose.alpaca.yaml')


class AlpacaComposeTests(unittest.TestCase):
    def config(self, overlay=True, address='192.168.1.20', profile='pi'):
        env = dict(os.environ, HVO_ALPACA_LAN_ADDRESS=address, HVO_ALPACA_PORT='11112')
        args = ['docker', 'compose', '-f', str(COMPOSE)]
        if overlay:
            args += ['-f', str(OVERLAY)]
        return subprocess.run(args + ['--profile', profile, 'config', '--format', 'json'],
                              env=env, text=True, capture_output=True)

    def test_default_profiles_have_no_alpaca_port_or_responder(self):
        for profile in ('pi', 'pi-lan-http'):
            result = self.config(overlay=False, profile=profile)
            self.assertEqual(0, result.returncode, result.stderr)
            services = json.loads(result.stdout)['services']
            self.assertNotIn('alpaca-discovery', services)
            self.assertFalse(any(p['target'] == 11111 for s in services.values() for p in s.get('ports', [])))

    def test_overlay_requires_explicit_publish_address(self):
        result = self.config(address='')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('HVO_ALPACA_LAN_ADDRESS', result.stderr)

    def test_enabled_server_and_discovery_share_the_advertised_port(self):
        for profile, name in (('pi', 'roof-controller'), ('pi-lan-http', 'roof-controller-lan-http')):
            result = self.config(profile=profile)
            self.assertEqual(0, result.returncode, result.stderr)
            services = json.loads(result.stdout)['services']
            server = services[name]
            alpaca_port = next(p for p in server['ports'] if p['target'] == 11111)
            self.assertEqual(('192.168.1.20', '11112'), (alpaca_port['host_ip'], alpaca_port['published']))
            self.assertEqual('true', server['environment']['Alpaca__Enabled'])
            self.assertEqual('false', server['environment']['Alpaca__Discovery__Enabled'])
            self.assertTrue(any(v['target'] == '/var/lib/hvo-roof-alpaca' for v in server['volumes']))
            key_target = server['environment']['Alpaca__ControllerApiKeyFile']
            self.assertTrue(any(v['target'] == key_target and v.get('read_only') for v in server['volumes']))
            for mount in server['volumes']:
                if mount.get('read_only') and mount['target'] != key_target:
                    self.assertFalse(key_target.startswith(mount['target'].rstrip('/') + '/'),
                                     'the key mountpoint cannot be created inside a read-only parent mount')
            responder = services['alpaca-discovery']
            self.assertEqual('host', responder['network_mode'])
            self.assertIn('--discovery-only', responder['command'])
            self.assertEqual('11112', responder['environment']['Alpaca__Discovery__AdvertisedPort'])
            self.assertEqual('/tmp/alpaca-discovery-ready', responder['environment']['Alpaca__Discovery__ReadyFilePath'])
            self.assertEqual(['CMD', 'test', '-f', '/tmp/alpaca-discovery-ready'], responder['healthcheck']['test'])
            self.assertEqual('1655:1655', responder['user'])
            self.assertTrue(responder['read_only'])
            self.assertEqual(['ALL'], responder['cap_drop'])
            self.assertNotIn('volumes', responder)
            self.assertFalse(any('Key' in k for k in responder['environment']))


if __name__ == '__main__':
    unittest.main()
