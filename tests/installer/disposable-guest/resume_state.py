"""Container-private access checkpoint and disk-preserving cold resume.

This module never creates a guest disk, changes an account, or exports credentials.
The caller must verify container ownership/isolation and the live guest marker first.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess

ACCESS_FILES = ('id_ed25519', 'id_ed25519.pub', 'known_hosts', 'seed.iso')


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def regular(path):
    s = path.lstat()
    require(stat.S_ISREG(s.st_mode) and s.st_uid == os.geteuid(), 'Expected an owned regular file')
    return s


def write_json(path, value):
    temporary = path.with_suffix('.new')
    with temporary.open('x') as f:
        json.dump(value, f, indent=2)
        f.write('\n')
        f.flush()
        os.fsync(f.fileno())
    os.replace(temporary, path)


def stage(root, state, container_id, run_id, boot_id):
    """Copy access material within the container, without touching the live disk."""
    os.umask(0o077)
    disk = root / 'work.qcow2'
    disk_stat = regular(disk)
    require(disk_stat.st_size > 0, 'The existing writable disk is empty')
    for name in ACCESS_FILES:
        regular(state / name)
    require(run_id in (state / 'seed/user-data').read_text(), 'Seed does not match the verified guest run')
    resume = root / 'resume'
    resume.mkdir(mode=0o700)  # Refuse replacing any existing checkpoint.
    try:
        hashes = {}
        for name in ACCESS_FILES:
            shutil.copyfile(state / name, resume / name)
            (resume / name).chmod(0o600)
            hashes[name] = digest(resume / name)
            require(hashes[name] == digest(state / name), 'Access copy verification failed')
        shutil.copyfile(root / 'guestctl.py', resume / 'original-guestctl.py')
        if (state / 'console.log').is_file():
            shutil.copyfile(state / 'console.log', resume / 'console-before-resume.log')
        manifest = {'version': 1, 'container_id': container_id, 'run_id': run_id,
                    'boot_id_before': boot_id, 'vcpus': 4, 'guest_memory_mib': 3072,
                    'outer_cpus': 4, 'outer_memory_bytes': 6 * 1024**3,
                    'disk_inode': disk_stat.st_ino, 'disk_device': disk_stat.st_dev,
                    'access_sha256': hashes, 'checkpoint_complete': False}
        write_json(resume / 'manifest.json', manifest)
        return {k: manifest[k] for k in ('container_id', 'run_id', 'boot_id_before', 'vcpus', 'guest_memory_mib')}
    except BaseException:
        # Only this freshly-created directory is owned by this failed operation.
        shutil.rmtree(resume)
        raise


def check_qcow(disk, base):
    info = json.loads(subprocess.check_output(['qemu-img', 'info', '--output=json', str(disk)], text=True))
    require(info.get('format') == 'qcow2', 'Existing disk must be qcow2')
    require(info.get('full-backing-filename') == str(base), 'Unexpected disk backing image')
    require(info.get('virtual-size') == 16 * 1024**3, 'Unexpected virtual disk size')
    subprocess.run(['qemu-img', 'check', '--output=json', str(disk)], check=True, capture_output=True)


def restore(root, state, checker=check_qcow):
    """Called only by the next container entrypoint, before launching QEMU."""
    os.umask(0o077)
    resume = root / 'resume'
    ms = regular(resume / 'manifest.json')
    require(not stat.S_IMODE(ms.st_mode) & 0o077, 'Resume manifest must remain private')
    manifest = json.loads((resume / 'manifest.json').read_text())
    require(manifest.get('version') == 1 and manifest.get('vcpus') == 4 and
            manifest.get('guest_memory_mib') == 3072, 'Unapproved resume configuration')
    require(not state.exists(), 'Runtime state already exists; refuse a second QEMU instance')
    disk = root / 'work.qcow2'
    ds = regular(disk)
    # Overlayfs mount device numbers may change across container starts. The
    # original inode must remain, and qcow2 structure/backing are checked below.
    require(ds.st_ino == manifest['disk_inode'],
            'The original writable disk was replaced')
    for name in ACCESS_FILES:
        regular(resume / name)
        require(digest(resume / name) == manifest['access_sha256'][name], 'Saved access material changed')
    # QEMU is not running at entrypoint startup. No repair flags, snapshot restore,
    # qemu-img create, disk conversion or guest writes occur here.
    checker(disk, root / 'base.qcow2')
    backup = resume / 'disk-before-first-resume.qcow2'
    if not manifest['checkpoint_complete']:
        require(not backup.exists(), 'Incomplete previous checkpoint requires inspection')
        require(shutil.disk_usage(root).free > ds.st_size + 512*1024**2,
                'Insufficient space for an offline private disk checkpoint')
        with backup.open('xb') as output, disk.open('rb') as source:
            shutil.copyfileobj(source, output, 1024 * 1024)
            output.flush()
            os.fsync(output.fileno())
        checkpoint_hash = digest(backup)
        require(checkpoint_hash == digest(disk), 'Offline disk checkpoint verification failed')
        manifest['disk_checkpoint_sha256'] = checkpoint_hash
        manifest['checkpoint_complete'] = True
        write_json(resume / 'manifest.json', manifest)
    else:
        regular(backup)
        require(digest(backup) == manifest['disk_checkpoint_sha256'], 'Offline checkpoint changed')
    state.mkdir(mode=0o700, parents=True)
    for name in ACCESS_FILES:
        shutil.copyfile(resume / name, state / name)
        (state / name).chmod(0o600)
    return manifest
