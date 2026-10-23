"""Boot-file planning and recovery tests on a fake filesystem; no services/hardware."""
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch

FOLDER = Path(__file__).resolve().parents[2] / 'src/HVO.RoofControllerV4.Kiosk/deploy/splash'


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, FOLDER / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


INITRD_CONTENTS = '\n'.join(['usr/share/plymouth/themes/hvo-roof/'+n for n in ('hvo-roof.plymouth', 'theme.script', 'hero.png')] + ['usr/lib/aarch64-linux-gnu/plymouth/script.so'])
setup = module('splash_setup', 'setup.py')
gate = module('display_gate', 'display-gate.py')


class SplashTests(unittest.TestCase):
    def test_terminal_handoff_and_managed_splash_have_no_systemd_ordering_cycle(self):
        analyze = Path('/usr/bin/systemd-analyze')
        if not analyze.exists():
            self.skipTest('Offline systemd unit parser is unavailable on this platform')
        files, _, _ = setup.desired(self.config, self.root)
        units = self.root / 'unit-fixture'
        units.mkdir()
        for name, body in files.items():
            if name.startswith('/etc/systemd/system/'):
                target = units / name.removeprefix('/etc/systemd/system/')
                target.parent.mkdir(parents=True, exist_ok=True)
                content = body.decode()
                # Only parser/ordering acceptance: these host commands are never run or installed.
                content = content.replace('/usr/local/libexec/hvo-roof-splash-handoff', '/usr/bin/true')
                content = content.replace('/usr/local/libexec/hvo-roof-display-gate', '/usr/bin/true')
                target.write_text(content)
        deploy = FOLDER.parent
        kiosk = (deploy / 'hvo-roof-kiosk.service').read_text().replace(
            '/opt/hvo-roof-kiosk/hvo-roof-kiosk', '/usr/bin/true')
        # Check the union of boot/handoff ordering edges. The real units are exclusive; removing only Conflicts
        # in this offline graph lets one synthetic target load every ordering edge in the same transaction.
        def ordering_only(content):
            return '\n'.join(line for line in content.splitlines() if not line.startswith('Conflicts=')) + '\n'
        kiosk = ordering_only(kiosk)
        (units / 'hvo-roof-kiosk.service').write_text(kiosk)
        (units / 'hvo-roof-terminal.service').write_text(ordering_only((deploy / 'hvo-roof-terminal.service').read_text()))
        required = ('hvo-roof-kiosk.service hvo-roof-terminal.service hvo-roof-display-gate.service '
                    'getty@tty1.service plymouth-quit.service')
        (units / 'hvo-test-terminal.target').write_text('[Unit]\nRequires=' + required + '\n')
        env = {**os.environ, 'SYSTEMD_UNIT_PATH': str(units) + ':'}
        args = [str(analyze), 'verify', '--man=no', '--generators=no', 'hvo-test-terminal.target']
        result = subprocess.run(args, env=env, capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        # Prove this fixture detects the incompatible order rather than merely accepting any unit text.
        (units / 'hvo-roof-kiosk.service').write_text(kiosk.replace(
            'Before=getty@tty1.service', 'After=getty@tty1.service'))
        incompatible = subprocess.run(args, env=env, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(incompatible.returncode, 0)
        self.assertIn('cycle', incompatible.stderr.lower())

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.write('/proc/device-tree/model', 'Raspberry Pi 5 Model B\0')
        self.write('/boot/firmware/cmdline.txt', 'console=tty1 root=TEST rootwait\n')
        self.write('/sys/class/drm/card7-DSI-1/status', 'connected\n')
        self.write('/boot/initrd.img-test-rpi-2712', b'completed-image')
        self.write('/boot/firmware/initramfs_2712', b'completed-image')
        self.write('/etc/apt/sources.list.d/debian.sources',
                   'Types: deb\nURIs: https://deb.debian.org/debian\nSuites: trixie\nSigned-By: /usr/share/keyrings/debian-archive-keyring.gpg\n')
        self.write('/operator/scene.png', b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR'+struct.pack('>II', 1672, 941))
        self.config = dict(enabled=True, image='/operator/scene.png', rotation=90,
                           assets={p.name: p.read_text() for p in FOLDER.iterdir() if p.name in ['theme.script', 'display-gate.py', 'handoff.sh']})

    def write(self, name, value):
        p = setup.path(self.root, name)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(value.encode() if isinstance(value, str) else value)

    def snapshot(self):
        return {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}

    def ubuntu(self):
        setup.path(self.root, '/proc/device-tree/model').unlink()
        self.write('/etc/os-release', 'ID=ubuntu\n')
        self.write('/etc/default/grub', 'GRUB_CMDLINE_LINUX_DEFAULT="operator=keep"\n')
        self.write('/etc/apt/sources.list.d/ubuntu.sources',
                   'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports/\nSuites: resolute\n'
                   'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n')
        return {**self.config, 'rotation': 0}

    def test_ubuntu_plan_preserves_grub_and_refuses_pi_rotation(self):
        config = self.ubuntu()
        before = self.snapshot()
        self.assertEqual(setup.check(config, self.root)[0], 10)
        files, _, _ = setup.desired(config, self.root)
        self.assertIn(setup.GRUB_DROPIN, files)
        self.assertIn(b'ThemeDir=/usr/share/plymouth/themes', files['/etc/plymouth/plymouthd.conf'])
        self.assertNotIn('/boot/firmware/cmdline.txt', files)
        self.assertIn('rotation = 0;', files[setup.THEME+'/theme.script'].decode())
        self.assertEqual(before, self.snapshot())
        with self.assertRaisesRegex(ValueError, 'Pi-only'):
            setup.desired({**config, 'rotation': 90}, self.root)
        self.write('/etc/default/grub', 'GRUB_CMDLINE_LINUX_DEFAULT="nosplash"\n')
        with self.assertRaisesRegex(ValueError, 'disabled'):
            setup.check(config, self.root)

    def test_ubuntu_grub_fragment_preserves_options_without_duplicates(self):
        config = self.ubuntu()
        files, _, _ = setup.desired(config, self.root)
        fragment = files[setup.GRUB_DROPIN].decode()
        script = 'GRUB_CMDLINE_LINUX="root=KEEP"\nGRUB_CMDLINE_LINUX_DEFAULT="quiet operator=keep"\n'
        result = subprocess.check_output(['sh', '-c', script+fragment+fragment+'printf "%s" "$GRUB_CMDLINE_LINUX $GRUB_CMDLINE_LINUX_DEFAULT"'], text=True).split()
        for option in ['root=KEEP', 'operator=keep', 'quiet', 'splash', 'plymouth.ignore-serial-consoles', 'vt.global_cursor_default=0']:
            self.assertEqual(result.count(option), 1)

    def test_ubuntu_apply_upgrade_repeat_and_disable_restore(self):
        self.config = self.ubuntu()
        (self.root/'lib/modules/1-generic').mkdir(parents=True)
        self.write('/boot/vmlinuz-1-generic', b'kernel')
        self.write('/boot/initrd.img-1-generic', b'completed-initrd')
        self.test_apply_repeat_disable_restore_and_failed_rebuild_state()

    def test_ubuntu_sources_require_official_uri_and_signer(self):
        self.ubuntu()
        setup.official_sources(self.root)
        for uri, key in [('http://example.test/ubuntu', 'ubuntu'), ('http://archive.ubuntu.com/ubuntu/', 'debian')]:
            self.write('/etc/apt/sources.list.d/ubuntu.sources',
                       f'Types: deb\nURIs: {uri}\nSigned-By: /usr/share/keyrings/{key}-archive-keyring.gpg\n')
            with self.assertRaises(ValueError):
                setup.official_sources(self.root)

    def test_ubuntu_rebuild_verifies_every_kernel_and_regenerates_grub(self):
        self.ubuntu()
        (self.root/'lib/modules/1-generic').mkdir(parents=True)
        self.write('/boot/vmlinuz-1-generic', b'kernel')
        self.write('/boot/initrd.img-1-generic', b'completed-initrd')
        with patch.object(setup, 'run', return_value=INITRD_CONTENTS) as command:
            setup.rebuild(self.root)
            self.assertIn(unittest.mock.call(['update-grub']), command.call_args_list)
            with patch.object(setup, 'run', return_value='usr/share/plymouth/themes/bgrt/bgrt.plymouth'):
                with self.assertRaisesRegex(RuntimeError, 'missing the HVO theme'):
                    setup.rebuild(self.root)
            (self.root/'lib/modules/removed-generic').mkdir()
            setup.rebuild(self.root)  # left-over modules are not an installed kernel
            self.write('/boot/vmlinuz-2-generic', b'kernel-without-initrd')
            with self.assertRaisesRegex(RuntimeError, 'Each installed Ubuntu kernel'):
                setup.rebuild(self.root)

    def test_ubuntu_disable_rebuild_still_requires_completed_readable_images(self):
        self.ubuntu()
        self.write('/boot/vmlinuz-1-generic', b'kernel')
        with patch.object(setup, 'run', return_value=''):
            with self.assertRaisesRegex(RuntimeError, 'Each installed Ubuntu kernel'):
                setup.rebuild(self.root, require_theme=False)
        self.write('/boot/initrd.img-1-generic', b'distribution-initrd')
        def command(args):
            if args[0] == 'lsinitramfs':
                raise RuntimeError('unreadable archive')
            return ''
        with patch.object(setup, 'run', side_effect=command):
            with self.assertRaisesRegex(RuntimeError, 'unreadable archive'):
                setup.rebuild(self.root, require_theme=False)

    def test_plan_is_read_only_and_does_not_run_commands(self):
        before = self.snapshot()
        with patch.object(setup, 'run', side_effect=AssertionError('plan called a mutating command')):
            self.assertEqual(setup.check(self.config, self.root)[0], 10)
        self.assertEqual(before, self.snapshot())

    def test_disabled_default_is_noop_even_headless(self):
        self.assertEqual(setup.check(dict(enabled=False), self.root)[0], 0)

    def test_headless_and_non_official_sources_are_blocked(self):
        self.write('/sys/class/drm/card7-DSI-1/status', 'disconnected\n')
        with self.assertRaisesRegex(ValueError, 'No connected'):
            setup.check(self.config, self.root)
        self.write('/sys/class/drm/card7-DSI-1/status', 'connected\n')
        self.write('/etc/apt/sources.list.d/debian.sources', 'URIs: https://example.test/debian\n')
        with self.assertRaisesRegex(ValueError, 'official'):
            setup.check(self.config, self.root)

    def test_modes_rotations_and_console_tokens(self):
        for rotation in [0, 90, 180, 270]:
            files, _, _ = setup.desired({**self.config, 'rotation': rotation}, self.root)
            theme = files[setup.THEME+'/theme.script'].decode()
            self.assertIn('rotation = '+str(rotation)+';', theme)
            for mode, label in [('boot', 'Starting Roof Controller…'), ('reboot', 'Restarting…'), ('shutdown', 'Shutting down…')]:
                self.assertIn(f'if (mode == "{mode}") status_text = "{label}";', theme)
            self.assertIn('Please wait…', theme)
            cmdline = files['/boot/firmware/cmdline.txt'].decode()
            self.assertIn('console=tty1 root=TEST rootwait', cmdline)
            self.write('/boot/firmware/cmdline.txt', cmdline)
            self.assertEqual(cmdline.split().count('splash'), 1)
        with self.assertRaisesRegex(ValueError, 'Rotation'):
            setup.desired({**self.config, 'rotation': 45}, self.root)

    def test_official_gpg_and_trixie_pgp_sources_are_accepted_without_changes(self):
        for extension in ['gpg', 'pgp']:
            source = ('Types: deb\nURIs: http://deb.debian.org/debian/\nSuites: trixie trixie-updates\n'
                      f'Signed-By: /usr/share/keyrings/debian-archive-keyring.{extension}\n\n'
                      'Types: deb\nURIs: http://deb.debian.org/debian-security/\nSuites: trixie-security\n'
                      f'Signed-By: /usr/share/keyrings/debian-archive-keyring.{extension}\n')
            self.write('/etc/apt/sources.list.d/debian.sources', source)
            before = self.snapshot()
            with patch.object(setup, 'run', side_effect=AssertionError('plan called a command')):
                self.assertEqual(setup.check(self.config, self.root)[0], 10)
            self.assertEqual(before, self.snapshot())

    def test_each_archive_requires_an_exact_official_signing_key(self):
        first = ('Types: deb\nURIs: https://deb.debian.org/debian\nSuites: trixie\n'
                 'Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp\n\n')
        for signer in ['', '# Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp',
                       'Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp.extra',
                       'Signed-By: /operator/unrelated.pgp',
                       'Signed-By: /usr/share/keyrings/debian-archive-keyring.pgp /operator/unrelated.pgp']:
            with self.subTest(signer=signer):
                self.write('/etc/apt/sources.list.d/debian.sources', first +
                           'Types: deb\nURIs: https://deb.debian.org/debian-security\nSuites: trixie-security\n' + signer + '\n')
                with self.assertRaisesRegex(ValueError, 'signing key'):
                    setup.check(self.config, self.root)

    def test_explicit_disconnected_card_and_symlink_config_are_refused(self):
        self.write('/dev/dri/card1', b'')
        with self.assertRaisesRegex(ValueError, 'not a connected display'):
            setup.check({**self.config, 'card': '/dev/dri/card1'}, self.root)
        boot = self.root/'boot/firmware/cmdline.txt'
        saved = boot.with_name('cmdline.original')
        boot.rename(saved)
        boot.symlink_to(saved.name)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            setup.check(self.config, self.root)

    def test_preserves_other_plymouth_settings(self):
        old = '# site comment\n[Daemon]\nTheme=old\nDeviceScale=2\n[Other]\nKeep=yes\n'
        new = setup.select_theme(old)
        self.assertEqual(new, old.replace('Theme=old', 'Theme=hvo-roof'))
        self.assertEqual(setup.select_theme(new), new)

    def install_state(self):
        files, _, fingerprint = setup.desired(self.config, self.root)
        for name, value in files.items():
            self.write(name, value)
        state = dict(fingerprint=fingerprint, installed={p: setup.digest(b) for p, b in files.items()}, pending=False, backup='/unused')
        self.write(setup.STATE, json.dumps(state))

    def test_second_run_noop_and_operator_edits_refused(self):
        self.install_state()
        with patch.object(setup, 'run', return_value='installed\n'*3):
            self.assertEqual(setup.check(self.config, self.root)[0], 0)
        self.write(setup.THEME+'/theme.script', 'operator customization')
        with self.assertRaisesRegex(ValueError, 'edited'):
            setup.check(self.config, self.root)

    def test_upgrade_refuses_unmanaged_new_shutdown_dropin(self):
        self.install_state()
        name = '/etc/systemd/system/plymouth-reboot.service.d/hvo-roof.conf'
        state = json.loads(setup.read(self.root, setup.STATE))
        del state['installed'][name]
        self.write(setup.STATE, json.dumps(state))
        self.write(name, '[Unit]\nAfter=operator.service\n')
        with self.assertRaisesRegex(ValueError, 'unmanaged'):
            setup.check(self.config, self.root)

    def test_missing_dependencies_are_repaired(self):
        self.install_state()
        with patch.object(setup, 'run', side_effect=subprocess.CalledProcessError(1, ['dpkg-query'])):
            self.assertEqual(setup.check(self.config, self.root)[0], 10)

    def test_command_failure_reports_bounded_diagnostics_and_short_png_is_refused(self):
        with patch.object(setup.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, ['apt-get'], stderr='package unavailable')):
            with self.assertRaisesRegex(RuntimeError, 'apt-get failed.*package unavailable'):
                setup.run(['apt-get', 'update'])
        with self.assertRaisesRegex(ValueError, 'PNG'):
            setup.png(b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR')

    def test_rebuild_failure_is_not_success(self):
        with patch.object(setup, 'run', side_effect=subprocess.CalledProcessError(1, ['update-initramfs'])):
            with self.assertRaises(subprocess.CalledProcessError):
                setup.rebuild(self.root)

    def test_rebuild_requires_matching_completed_boot_images(self):
        with patch.object(setup, 'run', return_value=''):
            setup.rebuild(self.root)
            self.write('/boot/firmware/initramfs_2712', b'stale-image')
            with self.assertRaisesRegex(RuntimeError, 'does not match'):
                setup.rebuild(self.root)
            setup.path(self.root, '/boot/firmware/initramfs_2712').unlink()
            with self.assertRaisesRegex(RuntimeError, 'missing'):
                setup.rebuild(self.root)
            setup.path(self.root, '/boot/initrd.img-test-rpi-2712').unlink()
            with self.assertRaisesRegex(RuntimeError, 'No completed'):
                setup.rebuild(self.root)

    def test_apply_repeat_disable_restore_and_failed_rebuild_state(self):
        class FakeImage:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def load(self): pass
            def convert(self, mode): return self
            def rotate(self, angle, expand):
                self.angle = angle
                return self
            def save(self, stream, format): stream.write(b'rotated-test-image')
        image = FakeImage()
        pil = types.ModuleType('PIL')
        pil.Image = types.SimpleNamespace(open=lambda _: image)
        original = self.snapshot()
        calls = []
        def command(args):
            calls.append(args)
            if args[0] == 'dpkg-query': return 'installed\n'*3
            if args[0] == 'lsinitramfs':
                # Model the rebuilt archive from the installed theme: after disable
                # HVO assets are gone, so never return their old listing.
                return INITRD_CONTENTS if setup.read(self.root, setup.THEME+'/theme.script') else 'usr/share/plymouth/themes/bgrt/bgrt.plymouth'
            return ''
        with patch.dict('sys.modules', {'PIL': pil}), patch.object(setup, 'run', side_effect=command), patch('sys.stdout', new_callable=io.StringIO):
            desired = setup.desired
            def old_desired(config, root):
                files, image, fingerprint = desired(config, root)
                return {name: value for name, value in files.items()
                        if not any('plymouth-'+mode+'.service.d/' in name
                                   for mode in ('reboot', 'poweroff', 'halt', 'kexec'))}, image, fingerprint
            with patch.object(setup, 'desired', side_effect=old_desired):
                setup.apply(self.config, self.root)
            old_state = json.loads(setup.read(self.root, setup.STATE))
            # The assets and fingerprint have not changed: new desired files still need applying.
            self.assertEqual(setup.check(self.config, self.root)[0], 10)
            setup.apply(self.config, self.root)
            upgraded = json.loads(setup.read(self.root, setup.STATE))
            self.assertEqual(upgraded['backup'], old_state['backup'])
            for mode in ('reboot', 'poweroff', 'halt', 'kexec'):
                name = '/etc/systemd/system/plymouth-'+mode+'.service.d/hvo-roof.conf'
                self.assertEqual(setup.read(self.root, name), b'[Unit]\nAfter=hvo-roof-kiosk.service\n')
                self.assertIsNone(json.loads((Path(upgraded['backup'])/'originals.json').read_text())[name])
            self.assertEqual(image.angle, -self.config['rotation'])
            self.assertFalse(json.loads(setup.read(self.root, setup.STATE))['pending'])
            count = len(calls)
            setup.apply(self.config, self.root)
            self.assertEqual(calls[count:], [['dpkg-query', '-W', '-f', '${db:Status-Status}\n', *setup.PACKAGES]])
            with patch.object(setup, 'rebuild', side_effect=RuntimeError('disable rebuild failed')):
                with self.assertRaisesRegex(RuntimeError, 'disable rebuild failed'):
                    setup.apply(dict(enabled=False), self.root)
            self.assertTrue(json.loads(setup.read(self.root, setup.STATE))['disabling'])
            with self.assertRaisesRegex(ValueError, 'Finish the interrupted'):
                setup.check(self.config, self.root)
            self.assertEqual(setup.check(dict(enabled=False), self.root)[0], 10)
            setup.apply(dict(enabled=False), self.root)
        self.assertIsNone(setup.read(self.root, setup.STATE))
        for name, content in original.items():
            self.assertEqual((self.root/name).read_bytes(), content)
        self.assertFalse(setup.path(self.root, setup.THEME+'/theme.script').exists())
        for mode in ('reboot', 'poweroff', 'halt', 'kexec'):
            self.assertFalse(setup.path(self.root, '/etc/systemd/system/plymouth-'+mode+'.service.d/hvo-roof.conf').exists())
        self.assertFalse(any(call[:2] in (['systemctl', 'reboot'], ['systemctl', 'stop'], ['systemctl', 'poweroff']) for call in calls))
        with patch.dict('sys.modules', {'PIL': pil}), patch.object(setup, 'run', side_effect=command), patch.object(setup, 'rebuild', side_effect=RuntimeError('rebuild failed')), patch('sys.stdout', new_callable=io.StringIO):
            with self.assertRaisesRegex(RuntimeError, 'rebuild failed'):
                setup.apply(self.config, self.root)
        self.assertTrue(json.loads(setup.read(self.root, setup.STATE))['pending'])

    def test_handoff_restores_console_before_kiosk_without_retaining_graphics(self):
        # Exercise the real shell branching with a command recorder, never Plymouth.
        for absent, quit_fails, expected in [(False, False, [2, 8]),
                                             (True, False, [2]),
                                             (False, True, [2, 8, 3])]:
            with self.subTest(absent=absent, quit_fails=quit_fails):
                calls = self.root/'calls'
                calls.write_text('')
                timeout = self.root/'timeout'
                timeout.write_text('#!/bin/sh\n' +
                    'printf "%s\\n" "$*" >> '+str(calls)+'\n' +
                    ('case "$*" in *--ping*) exit 1;; esac\n' if absent else '') +
                    ('case "$*" in *quit*) exit 1;; esac\n' if quit_fails else '') +
                    'exit 0\n')
                timeout.chmod(0o700)
                script = (FOLDER/'handoff.sh').read_text().replace('/usr/bin/timeout', str(timeout))
                result = subprocess.run(['sh', '-c', script], capture_output=True, text=True, timeout=5)
                self.assertEqual(result.returncode, 0)
                commands = calls.read_text().splitlines()
                self.assertEqual([int(c.split()[0]) for c in commands], expected)
                self.assertTrue(all('--retain-splash' not in c for c in commands))
                if not absent:
                    self.assertTrue(all(c.endswith('/usr/bin/plymouth quit') for c in commands[1:]))

    def test_gate_fails_open_at_ten_seconds_without_sleeping_or_hardware(self):
        ticks = [0]
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            gate.wait_for_display(lambda: False, lambda: ticks[0], lambda n: ticks.__setitem__(0, ticks[0]+n))
            self.assertIn('fallback timeout', output.getvalue())
        self.assertGreaterEqual(ticks[0], 10)
        self.assertLess(ticks[0], 10.2)
        with patch('sys.stdout', new_callable=io.StringIO) as output:
            gate.wait_for_display(lambda: True, lambda: 0, lambda _: self.fail('slept after readiness'))
            self.assertIn('owns active display', output.getvalue())


if __name__ == '__main__':
    unittest.main()
