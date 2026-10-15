#!/usr/bin/env python3
"""Embedded hvo-roof-install step. Check is read-only; apply never reboots or starts/stops a service."""
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile

STATE = '/etc/hvo-roof-kiosk/splash/state.json'
THEME = '/usr/share/plymouth/themes/hvo-roof'
PACKAGES = ['plymouth', 'plymouth-themes', 'python3-pil']
TOKENS = ['quiet', 'splash', 'plymouth.ignore-serial-consoles']
GRUB_DROPIN = '/etc/default/grub.d/90-hvo-roof-splash.cfg'

def is_pi(root):
    return b'Raspberry Pi' in (read(root, '/proc/device-tree/model') or b'')

def ubuntu_grub(root):
    return not is_pi(root) and re.search(r'^ID="?ubuntu"?$', (read(root, '/etc/os-release') or b'').decode(), re.M) is not None and read(root, '/etc/default/grub') is not None

def apt_options(root):
    name = 'ubuntu.sources' if ubuntu_grub(root) else 'debian.sources'
    return ['-o', 'Dir::Etc::sourcelist=sources.list.d/'+name, '-o', 'Dir::Etc::sourceparts=-']


def digest(data):
    return hashlib.sha256(data).hexdigest()


def path(root, name):
    return root / name.lstrip('/')


def read(root, name):
    p = path(root, name)
    return p.read_bytes() if p.exists() else None


def run(args):
    try:
        return subprocess.run(args, check=True, capture_output=True, text=True,
                              timeout=900, env={**os.environ, 'DEBIAN_FRONTEND': 'noninteractive'}).stdout
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or '').strip()[-3000:]
        raise RuntimeError(f'{args[0]} failed (exit {error.returncode}): {detail}') from error


def atomic(p, data, mode=0o644):
    p.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=p.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, p)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def png(data):
    if len(data) < 24 or len(data) > 25*1024*1024 or data[:16] != b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR':
        raise ValueError('Supply a PNG no larger than 25 MiB.')
    w, h = struct.unpack('>II', data[16:24])
    if not w or not h or w*h > 16000000:
        raise ValueError('The image must contain between 1 and 16 million pixels.')


def select_theme(text, key='Theme', value='hvo-roof'):
    """Edit only the Theme entry; preserve unrelated daemon settings and comments."""
    lines = text.splitlines(keepends=True)
    start = next((i for i, line in enumerate(lines) if line.strip() == '[Daemon]'), None)
    if start is None:
        return text.rstrip('\n') + f'\n[Daemon]\n{key}={value}\n'
    end = next((i for i in range(start+1, len(lines)) if lines[i].lstrip().startswith('[')), len(lines))
    for i in range(start+1, end):
        if re.match(r'\s*'+re.escape(key)+r'\s*=', lines[i]):
            lines[i] = f'{key}={value}\n'
            return ''.join(lines)
    lines.insert(start+1, f'{key}={value}\n')
    return ''.join(lines)


def official_sources(root):
    ubuntu = ubuntu_grub(root)
    source = read(root, '/etc/apt/sources.list.d/'+('ubuntu.sources' if ubuntu else 'debian.sources'))
    if source is None:
        raise ValueError('Splash requires the platform official deb822 archive file; no sources will be added.')
    # Trixie uses .pgp; older official images use .gpg. Check each deb822 stanza,
    # so one valid archive cannot hide a different signing key in another stanza.
    stanzas = [block for block in re.split(r'\n[ \t]*\n', source.decode())
               if any(line.strip() and not line.lstrip().startswith('#') for line in block.splitlines())]
    if not stanzas:
        raise ValueError('Splash package source must contain only official distribution archive URIs.')
    for stanza in stanzas:
        uris = re.findall(r'^URIs:[ \t]*(.+)$', stanza, re.M)
        allowed = (r'https?://(?:(?:archive|security)\.ubuntu\.com/ubuntu|ports\.ubuntu\.com/ubuntu-ports)/?' if ubuntu else
                   r'https?://(?:deb\.debian\.org/debian(?:-security)?|security\.debian\.org/debian-security)/?')
        if len(uris) != 1 or any(not re.fullmatch(allowed, u)
                                 for u in uris[0].split()):
            raise ValueError('Splash package source must contain only official distribution archive URIs.')
        signers = re.findall(r'^Signed-By:[ \t]*(.+)$', stanza, re.M)
        keys = ('/usr/share/keyrings/ubuntu-archive-keyring.gpg',) if ubuntu else (
            '/usr/share/keyrings/debian-archive-keyring.gpg', '/usr/share/keyrings/debian-archive-keyring.pgp')
        if len(signers) != 1 or signers[0].strip() not in keys:
            raise ValueError('Official distribution archive signing key is required.')


def desired(config, root):
    connected = list((root/'sys/class/drm').glob('card*-*/status'))
    if not any(p.read_text().strip() == 'connected' for p in connected):
        raise ValueError('No connected DRM display; omit splash on headless systems.')
    if config.get('card'):
        selected = path(root, config['card']).resolve(strict=True).name
        if not any(p.parent.name.startswith(selected+'-') and p.read_text().strip() == 'connected' for p in connected):
            raise ValueError('The configured kiosk card is not a connected display; explicitly select automatic discovery or a verified stable path.')
    angle = config['rotation']
    if angle not in (0, 90, 180, 270):
        raise ValueError('Rotation must be 0, 90, 180 or 270.')
    if not is_pi(root) and angle != 0:
        raise ValueError('Display rotation is Pi-only; use rotation 0 on a VM or other non-Pi host.')
    image = read(root, config['image'])
    if image is None:
        raise ValueError('Operator image is missing; keep its absolute path available for upgrades.')
    png(image)
    if is_pi(root):
        boot = next((p for p in ['/boot/firmware/cmdline.txt', '/boot/cmdline.txt'] if read(root, p) is not None), None)
        if boot is None:
            raise ValueError('No supported Raspberry Pi boot command line.')
        original = read(root, boot).decode().split()
        if any(v in original for v in ['nosplash', 'plymouth.enable=0']):
            raise ValueError('Splash is explicitly disabled in the boot command line; resolve that choice before installing it.')
        boot_content = (' '.join(original + [t for t in TOKENS if t not in original])+'\n').encode()
    elif ubuntu_grub(root):
        # A separate GRUB fragment preserves the distribution and operator settings.
        # Never execute an operator's configuration during read-only planning.
        configs = [root/'etc/default/grub', *(root/'etc/default/grub.d').glob('*.cfg')]
        if any(re.search(r'(?<![\w.])(?:nosplash|plymouth.enable=0)(?![\w.])', p.read_text()) for p in configs):
            raise ValueError('Splash is explicitly disabled in GRUB; resolve that choice before installing it.')
        boot = GRUB_DROPIN
        boot_content = ('# Managed HVO splash; retain all existing kernel options.\n'
            'for hvo_splash_option in quiet splash plymouth.ignore-serial-consoles vt.global_cursor_default=0; do\n'
            '  case " $GRUB_CMDLINE_LINUX $GRUB_CMDLINE_LINUX_DEFAULT " in\n'
            '    *" $hvo_splash_option "*) ;;\n'
            '    *) GRUB_CMDLINE_LINUX_DEFAULT="$GRUB_CMDLINE_LINUX_DEFAULT $hvo_splash_option" ;;\n'
            '  esac\ndone\nunset hvo_splash_option\n').encode()
    else:
        raise ValueError('Splash supports Raspberry Pi boot files or Ubuntu with GRUB; this boot layout is unsupported.')
    files = {
        boot: boot_content,
        '/etc/plymouth/plymouthd.conf': select_theme((read(root, '/etc/plymouth/plymouthd.conf') or b'').decode()).encode(),
        THEME+'/hvo-roof.plymouth': ('[Plymouth Theme]\nName=HVO Roof\nDescription=Operator artwork\nModuleName=script\n\n[script]\nImageDir='+THEME+'\nScriptFile='+THEME+'/theme.script\n').encode(),
        THEME+'/theme.script': config['assets']['theme.script'].replace('@ROTATION@', str(angle)).encode(),
        '/usr/local/libexec/hvo-roof-splash-handoff': config['assets']['handoff.sh'].encode(),
        '/usr/local/libexec/hvo-roof-display-gate': config['assets']['display-gate.py'].encode(),
        '/etc/systemd/system/plymouth-quit.service.d/hvo-roof.conf': b'[Unit]\nAfter=network-online.target docker.service\nBefore=hvo-roof-kiosk.service\n[Service]\nExecStart=\nExecStart=-/usr/local/libexec/hvo-roof-splash-handoff\nTimeoutSec=15\n',
        '/etc/systemd/system/plymouth-quit-wait.service.d/hvo-roof.conf': b'[Unit]\nAfter=plymouth-quit.service\n[Service]\nTimeoutSec=5\n',
        '/etc/systemd/system/hvo-roof-kiosk.service.d/splash.conf': b'[Unit]\nWants=plymouth-quit.service\nAfter=plymouth-quit.service\n',
        '/etc/systemd/system/getty@tty1.service.d/hvo-roof.conf': b'[Unit]\nWants=hvo-roof-display-gate.service\nAfter=hvo-roof-display-gate.service\n',
        '/etc/systemd/system/hvo-roof-display-gate.service': b'[Unit]\nDescription=Bounded kiosk display handoff\nAfter=hvo-roof-kiosk.service plymouth-quit.service\nBefore=getty@tty1.service\n[Service]\nType=oneshot\nExecStart=-/usr/local/libexec/hvo-roof-display-gate\nTimeoutStartSec=12\nRemainAfterExit=yes\nStandardInput=null\nStandardOutput=journal\nStandardError=journal\n',
    }
    # Shutdown starts Plymouth while normal services stop. Explicit ordering makes
    # the kiosk release DRM before any of the distribution's shutdown modes starts.
    for mode in ('reboot', 'poweroff', 'halt', 'kexec'):
        files[f'/etc/systemd/system/plymouth-{mode}.service.d/hvo-roof.conf'] = (
            b'[Unit]\nAfter=hvo-roof-kiosk.service\n')
    if ubuntu_grub(root):
        # Ubuntu dracut's populate helper requires both Theme and ThemeDir to
        # select a configured theme instead of the distribution alternative.
        files['/etc/plymouth/plymouthd.conf'] = select_theme(
            files['/etc/plymouth/plymouthd.conf'].decode(), 'ThemeDir', '/usr/share/plymouth/themes').encode()
        # Ubuntu's stock hook copies the alternative-selected theme, even when
        # plymouthd.conf names another one. Include our configured theme without
        # changing the operator's system-wide alternatives selection.
        files['/etc/initramfs-tools/hooks/hvo-roof-splash'] = b"""#!/bin/sh
set -eu
PREREQ=plymouth
case "${1:-}" in prereqs) echo "$PREREQ"; exit 0 ;; esac
. /usr/share/initramfs-tools/hook-functions
plugins="$(plymouth --get-splash-plugin-path)"
copy_exec "$plugins/script.so"
copy_exec "$plugins/label-pango.so"
mkdir -p "$DESTDIR/usr/share/plymouth/themes"
cp -a /usr/share/plymouth/themes/hvo-roof "$DESTDIR/usr/share/plymouth/themes/"
"""
    fingerprint = digest(image + json.dumps(config['assets'], sort_keys=True).encode() + str(angle).encode())
    return files, image, fingerprint


def intact(root, state):
    return all(read(root, name) is not None and digest(read(root, name)) == value for name, value in state['installed'].items())


def check(config, root):
    current = read(root, STATE)
    state = json.loads(current) if current else None
    if state and state.get('disabling'):
        if config['enabled']:
            raise ValueError('Finish the interrupted splash disable before enabling it again.')
        for name, installed in state['installed'].items():
            content = read(root, name)
            actual = digest(content) if content is not None else None
            if actual not in (installed, state['restored'][name]):
                raise ValueError('Managed splash files were edited during disable; review the backup before proceeding.')
        return 10, 'Resume restoring the pre-splash files and rebuilding boot images; no reboot.'
    if state and not intact(root, state):
        raise ValueError('Managed splash files were edited, or an earlier apply was interrupted. Review the recorded backup before proceeding; no overwrite.')
    if not config['enabled']:
        return (10, 'Restore the pre-splash files; retain installed packages; no reboot.') if state else (0, 'No managed splash to disable.')
    files, image, fingerprint = desired(config, root)
    official_sources(root)
    shared = {'/etc/plymouth/plymouthd.conf', '/boot/firmware/cmdline.txt', '/boot/cmdline.txt'}
    if any(read(root, p) is not None for p in files
           if p not in shared and (not state or p not in state['installed'])):
        raise ValueError('An unmanaged splash file already exists; review/adopt or remove it explicitly first.')
    if any(path(root, p).is_symlink() for p in files):
        raise ValueError('A configuration target is a symlink; review it instead of replacing the link.')
    if (state and state['fingerprint'] == fingerprint and not state.get('pending')
            and all(read(root, name) == content for name, content in files.items())):
        try:
            installed = run(['dpkg-query', '-W', '-f', '${db:Status-Status}\n', *PACKAGES]).splitlines()
            if installed == ['installed'] * len(PACKAGES):
                return 0, 'Splash already configured; no changes.'
        except (subprocess.CalledProcessError, RuntimeError):
            pass
    return 10, 'Back up boot files, install official distribution Plymouth/Pillow packages, configure artwork/gate, and rebuild all initramfs images; no reboot.'


def rebuild(root, require_theme=True):
    run(['update-initramfs', '-u', '-k', 'all'])  # synchronous: success is required before returning
    if ubuntu_grub(root):
        # Removed Ubuntu kernels can leave module directories (for example DKMS).
        # Only bootable kernel images need an initramfs; never infer installation from those remnants.
        kernels = [p.name.removeprefix('vmlinuz-') for p in (root/'boot').glob('vmlinuz-*') if p.is_file()]
        if not kernels or any(not (root/'boot'/('initrd.img-'+k)).is_file()
                              or (root/'boot'/('initrd.img-'+k)).stat().st_size == 0 for k in kernels):
            raise RuntimeError('Each installed Ubuntu kernel requires a completed initramfs image.')
        for kernel in kernels:
            contents = run(['lsinitramfs', str(root/'boot'/('initrd.img-'+kernel))]).splitlines()
            if require_theme and (not all('usr/share/plymouth/themes/hvo-roof/'+name in contents
                       for name in ('hvo-roof.plymouth', 'theme.script', 'hero.png')) or not any(
                           name.endswith('/plymouth/script.so') for name in contents)):
                raise RuntimeError('Completed Ubuntu initramfs is missing the HVO theme or script renderer.')
        run(['update-grub'])
        run(['systemctl', 'daemon-reload'])
        return
    supported = False
    for pattern, destination in [('*-rpi-v8', 'initramfs8'), ('*-rpi-2712', 'initramfs_2712')]:
        target = root/'boot/firmware'/destination
        candidates = list((root/'boot').glob('initrd.img-'+pattern))
        if not candidates:
            if target.exists():
                raise RuntimeError('Firmware initramfs has no matching kernel image: '+destination)
            continue
        supported = True
        if not target.is_file() or not any(digest(p.read_bytes()) == digest(target.read_bytes()) for p in candidates):
            raise RuntimeError('Firmware initramfs is missing or does not match a completed kernel image: '+destination)
    if not supported:
        raise RuntimeError('No completed supported Raspberry Pi kernel initramfs images were found.')
    run(['systemctl', 'daemon-reload'])


def apply(config, root):
    code, detail = check(config, root)
    print(detail, flush=True)
    if code == 0:
        return
    state_data = read(root, STATE)
    state = json.loads(state_data) if state_data else None
    if not config['enabled']:
        backup = Path(state['backup'])
        originals = json.loads((backup/'originals.json').read_text())
        if not state.get('disabling'):
            state['restored'] = {name: digest((backup/name.lstrip('/')).read_bytes()) if originals[name] is not None else None
                                 for name in state['installed']}
            state['disabling'] = True
            atomic(path(root, STATE), json.dumps(state).encode(), 0o600)
        for name, meta in originals.items():
            if meta is None:
                path(root, name).unlink(missing_ok=True)
            else:
                atomic(path(root, name), (backup/name.lstrip('/')).read_bytes(), meta)
        # The restored distribution theme need not contain any HVO assets. Still
        # require completed, readable initramfs images before finishing removal.
        rebuild(root, require_theme=False)
        path(root, STATE).unlink()
        print('Disabled; packages and recovery backup retained. Reboot separately when appropriate.')
        return
    files, image, fingerprint = desired(config, root)
    # Package hooks can rebuild boot files, so save recoverable images before apt.
    (root/'var/backups').mkdir(parents=True, exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix='hvo-roof-splash-', dir=root/'var/backups'))
    originals = {}
    for name in [*files, THEME+'/hero.png', *('/'+str(p.relative_to(root)) for p in (root/'boot').glob('initrd.img-*') if p.is_file()),
                 *('/'+str(p.relative_to(root)) for p in (root/'boot/firmware').glob('initramfs*') if p.is_file())]:
        p = path(root, name)
        originals[name] = (p.stat().st_mode & 0o777) if p.exists() else None
        if p.exists():
            saved = backup/name.lstrip('/')
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, saved)
    atomic(backup/'originals.json', json.dumps(originals).encode(), 0o600)
    print('Recovery backup: '+str(backup), flush=True)
    run(['apt-get', *apt_options(root), 'update'])
    run(['apt-get', '-o', 'DPkg::Lock::Timeout=120', *apt_options(root), '-y', '--no-install-recommends', 'install', *PACKAGES])
    from PIL import Image
    with Image.open(io.BytesIO(image)) as source:
        source.load()
        rotated = source.convert('RGBA').rotate(-config['rotation'], expand=True)
        output = io.BytesIO()
        rotated.save(output, format='PNG')
        files[THEME+'/hero.png'] = output.getvalue()
    for name, content in files.items():
        atomic(path(root, name), content, 0o755 if name.startswith(('/usr/local/libexec/', '/etc/initramfs-tools/hooks/')) else 0o644)
    # Extend the first-install recovery manifest when upgrades add managed files.
    # Existing originals always win; never replace them with the installed theme.
    if state:
        first = Path(state['backup'])
        first_originals = json.loads((first/'originals.json').read_text())
        for name, mode in originals.items():
            if name not in first_originals:
                first_originals[name] = mode
                if mode is not None:
                    saved = first/name.lstrip('/')
                    saved.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(backup/name.lstrip('/'), saved)
        atomic(first/'originals.json', json.dumps(first_originals).encode(), 0o600)
    # Keep the first-install originals across upgrades; interrupted rebuilds are not marked complete.
    state = {'fingerprint': fingerprint, 'backup': state['backup'] if state else str(backup),
             'installed': {p: digest(b) for p, b in files.items()}, 'pending': True}
    atomic(path(root, STATE), json.dumps(state).encode(), 0o600)
    run(['systemd-analyze', 'verify', 'hvo-roof-display-gate.service', 'getty@tty1.service', 'hvo-roof-kiosk.service', 'plymouth-quit.service',
         'plymouth-reboot.service', 'plymouth-poweroff.service', 'plymouth-halt.service', 'plymouth-kexec.service'])
    rebuild(root)
    state['pending'] = False
    atomic(path(root, STATE), json.dumps(state).encode(), 0o600)
    print('Splash configured. Boot images completed. No reboot or kiosk restart was requested.')


def main():
    config = json.load(sys.stdin)
    root = Path(config.get('root', '/'))
    if '--check' in sys.argv:
        code, message = check(config, root)
        print(message)
        return code
    if '--apply' not in sys.argv or root != Path('/') or os.geteuid() != 0:
        raise ValueError('Apply is only allowed by the system installer as root on this machine.')
    apply(config, root)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
