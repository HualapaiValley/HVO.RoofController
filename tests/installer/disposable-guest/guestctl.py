"""Control only the disposable guest through outer-container loopback."""
import json
import os
import pathlib
import shlex
import signal
import socket
import subprocess
import sys
import time
import uuid

ROOT=pathlib.Path('/opt/guest')
STATE=pathlib.Path('/run/guest')

def ssh(command, check=True, capture=False):
    return subprocess.run(['ssh','-F','/dev/null','-i',str(STATE/'id_ed25519'),'-p','2222',
        '-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',f'UserKnownHostsFile={STATE}/known_hosts',
        '-o','ConnectTimeout=5','ubuntu@127.0.0.1',command],check=check,capture_output=capture,text=capture)

def ready(timeout=900):
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        if not (STATE/'known_hosts').exists():
            result=subprocess.run(['ssh-keyscan','-p','2222','-t','ed25519','127.0.0.1'],capture_output=True,text=True)
            if result.returncode==0 and result.stdout.strip():
                # This endpoint exists only in this network-none container. No external trust is weakened.
                (STATE/'known_hosts').write_text(result.stdout)
        if (STATE/'known_hosts').exists():
            result=ssh('test -f /var/lib/hvo-guest-prepared && sudo -n systemctl is-active docker',False,True)
            if result.returncode==0:
                return
        time.sleep(3)
    raise SystemExit('Disposable guest did not become ready; inspect /run/guest/console.log')

def serve():
    if not pathlib.Path('/.dockerenv').exists():
        raise SystemExit('Run inside the constrained disposable container only')
    disk=ROOT/'work.qcow2'
    if (ROOT/'resume/manifest.json').is_file():
        from resume_state import restore
        configuration=restore(ROOT,STATE)
        return launch(disk,configuration['vcpus'],configuration['guest_memory_mib'])
    # Starting an old container must never silently truncate its installed disk.
    if disk.exists():
        raise SystemExit('Existing writable guest disk: use the guarded cold-resume procedure')
    os.umask(0o077)
    STATE.mkdir(mode=0o700,parents=True,exist_ok=True)
    subprocess.run(['ssh-keygen','-q','-t','ed25519','-N','','-f',str(STATE/'id_ed25519')],check=True)
    key=(STATE/'id_ed25519.pub').read_text().strip()
    seed=STATE/'seed'
    seed.mkdir()
    (seed/'meta-data').write_text('instance-id: hvo-disposable-runtime\nlocal-hostname: hvo-disposable\n')
    marker=json.dumps({'purpose':'hvo-auth-install-test','run_id':str(uuid.uuid4())})
    (seed/'user-data').write_text('#cloud-config\nssh_pwauth: false\ndisable_root: true\nusers:\n - default\nssh_authorized_keys:\n - '+key+'\nwrite_files:\n - path: /etc/hvo-disposable-guest.json\n   owner: root:root\n   permissions: "0644"\n   content: |\n     '+marker+'\n')
    subprocess.run(['genisoimage','-quiet','-output',str(STATE/'seed.iso'),'-volid','cidata','-joliet','-rock',str(seed)],check=True)
    subprocess.run(['qemu-img','create','-f','qcow2','-F','qcow2','-b',str(ROOT/'base.qcow2'),str(disk)],check=True)
    return launch(disk,2,3072)

def launch(disk,vcpus,memory_mib):
    cmd=['qemu-system-aarch64','-machine','virt','-cpu','cortex-a72','-accel','tcg,thread=multi','-smp',str(vcpus),'-m',str(memory_mib),
         '-bios','/usr/share/qemu-efi-aarch64/QEMU_EFI.fd','-display','none','-serial',f'file:{STATE}/console.log',
         '-qmp',f'unix:{STATE}/qmp.sock,server=on,wait=off','-monitor','none',
         '-drive',f'file={disk},if=virtio,format=qcow2',
         '-drive',f'file={STATE}/seed.iso,if=virtio,format=raw,readonly=on',
         '-netdev','user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=tcp:127.0.0.1:18080-:8080,hostfwd=tcp:127.0.0.1:18081-:8081,hostfwd=tcp:127.0.0.1:18088-:8088,hostfwd=tcp:127.0.0.1:18443-:8443,hostfwd=tcp:127.0.0.1:15200-:5200,hostfwd=tcp:127.0.0.1:15290-:5290',
         '-device','virtio-net-pci,netdev=net0,romfile=',
         '-device','virtio-gpu-pci',
         '-device','virtio-keyboard-pci',
         '-device','virtio-tablet-pci']
    child=subprocess.Popen(cmd)
    signal.signal(signal.SIGTERM,lambda *_: child.terminate())
    signal.signal(signal.SIGINT,lambda *_: child.terminate())
    raise SystemExit(child.wait())

def monitor(command):
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as sock:
        sock.connect(str(STATE/'qmp.sock'))
        stream=sock.makefile('rwb')
        json.loads(stream.readline())
        def send(value):
            stream.write(json.dumps(value).encode()+b'\n')
            stream.flush()
            while True:
                response=json.loads(stream.readline())
                if 'error' in response: raise RuntimeError(response['error'])
                if 'return' in response: return response['return']
        send({'execute':'qmp_capabilities'})
        result=send({'execute':'human-monitor-command','arguments':{'command-line':command}})
        if result.strip(): raise RuntimeError(result)

def main():
    action=sys.argv[1]
    if action=='serve': serve()
    elif action=='ready':
        ready()
        ssh('uname -a; systemctl is-system-running || true; sudo -n docker info --format "{{.ServerVersion}}"')
    elif action=='exec': ssh('sudo -n '+shlex.join(sys.argv[2:]))
    elif action in ('put','get'):
        source,dest=sys.argv[2:4]
        base=['scp','-F','/dev/null','-i',str(STATE/'id_ed25519'),'-P','2222','-o','StrictHostKeyChecking=yes','-o',f'UserKnownHostsFile={STATE}/known_hosts']
        if action=='put': subprocess.run(base+[source,'ubuntu@127.0.0.1:'+dest],check=True)
        else: subprocess.run(base+['ubuntu@127.0.0.1:'+source,dest],check=True)
    elif action=='reboot':
        old=ssh('cat /proc/sys/kernel/random/boot_id',capture=True).stdout.strip()
        ssh('sudo -n systemctl reboot',False)
        time.sleep(10)
        ready()
        new=ssh('cat /proc/sys/kernel/random/boot_id',capture=True).stdout.strip()
        if old==new: raise SystemExit('Guest did not reboot')
        print(json.dumps({'reboot_verified':True,'old_boot_id':old,'new_boot_id':new}))
    elif action in ('snapshot','restore'):
        name=sys.argv[2]
        if not name.replace('-','').replace('_','').isalnum(): raise SystemExit('Invalid snapshot name')
        if action=='snapshot':
            ssh('sudo -n sync')
            monitor('savevm '+name)
        else:
            monitor('loadvm '+name)
            ready()
        print(json.dumps({'action':action,'snapshot':name,'complete':True}))
    else: raise SystemExit('Use serve, ready, exec, put, get, reboot, snapshot, restore')

if __name__=='__main__': main()
