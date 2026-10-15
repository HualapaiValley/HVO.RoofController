"""Build an OS-only guest baseline; all package/account writes are in its disk."""
import pathlib
import subprocess
import time

ROOT = pathlib.Path('/opt/guest')
subprocess.run(['qemu-img', 'resize', str(ROOT/'base.qcow2'), '16G'], check=True)
seed = ROOT/'setup-seed'
seed.mkdir()
(seed/'meta-data').write_text('instance-id: hvo-disposable-setup\nlocal-hostname: hvo-disposable\n')
(seed/'user-data').write_text('''#cloud-config
ssh_pwauth: false
disable_root: true
package_update: true
packages:
 - docker.io
 - docker-compose-v2
 - python3
 - curl
 - jq
 - libdrm2
 - libgbm1
 - libegl1
 - libgles2
 - libinput10
 - libfontconfig1
runcmd:
 - [systemctl, enable, --now, docker]
 - [sh, -c, 'docker info >/var/log/hvo-guest-docker-info.txt && touch /var/lib/hvo-guest-prepared && echo HVO_GUEST_SETUP_COMPLETE >/dev/ttyAMA0']
 - [sh, -c, 'rm -f /etc/ssh/ssh_host_*; cloud-init clean --logs --machine-id; sync; poweroff']
''')
subprocess.run(['genisoimage','-quiet','-output',str(ROOT/'setup.iso'),'-volid','cidata','-joliet','-rock',str(seed)],check=True)
cmd=['qemu-system-aarch64','-machine','virt','-cpu','cortex-a72','-accel','tcg,thread=multi','-smp','2','-m','3072',
     '-bios','/usr/share/qemu-efi-aarch64/QEMU_EFI.fd','-display','none','-serial','stdio','-monitor','none',
     '-drive',f'file={ROOT}/base.qcow2,if=virtio,format=qcow2',
     '-drive',f'file={ROOT}/setup.iso,if=virtio,format=raw,readonly=on',
     '-netdev','user,id=net0','-device','virtio-net-pci,netdev=net0,romfile=','-no-reboot']
start=time.monotonic()
with (ROOT/'setup-console.log').open('wb') as log:
    child=subprocess.Popen(cmd,stdout=log,stderr=subprocess.STDOUT)
    while child.poll() is None:
        elapsed=time.monotonic()-start
        if elapsed>1800:
            child.terminate()
            child.wait(timeout=30)
            raise SystemExit('Guest setup timed out')
        print(f'Guest OS preparation running ({elapsed:.0f}s)',flush=True)
        time.sleep(30)
console=(ROOT/'setup-console.log').read_text(errors='replace')
if child.returncode or 'HVO_GUEST_SETUP_COMPLETE' not in console:
    print(console[-18000:])
    raise SystemExit('Guest setup did not complete')
print(f'OS-only guest prepared in {time.monotonic()-start:.1f}s')
subprocess.run(['qemu-img','check',str(ROOT/'base.qcow2')],check=True)
for file in ('setup.iso',):
    (ROOT/file).unlink()
