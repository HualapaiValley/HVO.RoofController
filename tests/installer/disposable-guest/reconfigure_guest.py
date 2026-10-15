"""Guarded, two-phase cold relaunch of the exact owned disposable guest.

prepare stages container-private access recovery and the safe launcher only.
relaunch gracefully powers off the guest, changes only CPU/RAM limits, then boots
the original writable disk and verifies its marker, boot ID and CPU count.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

from run_guest import NAME, docker, inspect

HERE = Path(__file__).resolve().parent
GUEST_IDENTITY = r'''
import json,os,pathlib,stat,subprocess
p=pathlib.Path('/etc/hvo-disposable-guest.json');s=p.stat();m=json.loads(p.read_text())
if s.st_uid!=0 or stat.S_IMODE(s.st_mode)&0o022 or m.get('purpose')!='hvo-auth-install-test':
 raise SystemExit('Invalid disposable marker')
if subprocess.check_output(['systemd-detect-virt'],text=True).strip()!='qemu':
 raise SystemExit('Not the disposable QEMU guest')
root_uuid=subprocess.check_output(['findmnt','-n','-o','UUID','/'],text=True).strip()
if not root_uuid: raise SystemExit('Root filesystem UUID unavailable')
print(json.dumps({'run_id':m['run_id'],'boot_id':pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'cpus':os.cpu_count(),'root_filesystem_uuid':root_uuid}))
'''


def require(value, message):
    if not value:
        raise RuntimeError(message)


def identity(expected_container_id, expected_run_id, require_running=True):
    isolation = inspect()
    require(isolation['id'] == expected_container_id, 'Container identity changed')
    require(not require_running or isolation['state'] == 'running', 'Expected the running guest')
    if not require_running:
        return isolation, None
    result = docker('exec', expected_container_id, 'python3', '/opt/guest/guestctl.py',
                    'exec', 'python3', '-c', GUEST_IDENTITY)
    guest = json.loads(result.stdout)
    require(guest['run_id'] == expected_run_id, 'Guest run identity changed')
    return isolation, guest


def host_config(container_id):
    return json.loads(docker('inspect', container_id).stdout)[0]['HostConfig']


def immutable_container(container_id):
    data=json.loads(docker('inspect', container_id).stdout)[0]
    def digest(value):
        return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    # Hash Config rather than emitting any environment values into evidence.
    return {'config_sha256':digest(data['Config']), 'mounts_sha256':digest(data['Mounts']),
            'image_id':data['Image']}


def limits_only(before, after):
    require(after['NanoCpus'] == 4*10**9 and after['Memory'] == 6*1024**3,
            'Requested CPU/memory limits were not applied')
    ignored = {'NanoCpus', 'Memory'}
    require({k:v for k,v in before.items() if k not in ignored} ==
            {k:v for k,v in after.items() if k not in ignored},
            'An unrelated container setting changed')


def verify_guest_continuity(before, after):
    require(after['run_id'] == before['run_id'], 'Guest marker changed')
    require(after['root_filesystem_uuid'] == before['root_filesystem_uuid'], 'Root filesystem changed')
    require(after['boot_id'] != before['boot_id'] and after['cpus'] == 4, 'Cold boot/CPU verification failed')


def save_receipt(path, report, exclusive=False):
    with path.open('x' if exclusive else 'w') as f:
        json.dump(report, f, indent=2)
        f.write('\n')
        f.flush()
        os.fsync(f.fileno())


def prepare(args):
    isolation, guest = identity(args.expected_container_id, args.expected_run_id)
    require(isolation['cpus'] == 2 and isolation['memory_bytes'] == 4*1024**3,
            'Preparation requires the original resource profile')
    require(guest['cpus'] == 2, 'Unexpected guest CPU count')
    report = {'phase':'preparing', 'container_id':args.expected_container_id,
              'run_id':args.expected_run_id, 'guest_before':guest,
              'isolation_before':isolation, 'host_config_before':host_config(args.expected_container_id),
              'immutable_container':immutable_container(args.expected_container_id)}
    save_receipt(args.evidence, report, exclusive=True)
    # The current entrypoint remains running its already-loaded code. This stages
    # the next start without changing QEMU, the disk, service state or limits.
    require(docker('exec', args.expected_container_id, 'test', '!', '-e', '/opt/guest/resume', check=False).returncode == 0,
            'A resume checkpoint already exists; inspect it before continuing')
    docker('cp', str(HERE/'resume_state.py'), args.expected_container_id+':/opt/guest/resume_state.py')
    stage = ('import sys;sys.path.insert(0,"/opt/guest");from pathlib import Path;'
             'from resume_state import stage;import json;'
             'print(json.dumps(stage(Path("/opt/guest"),Path("/run/guest"),'
             +repr(args.expected_container_id)+','+repr(args.expected_run_id)+','+repr(guest['boot_id'])+')))')
    report['staged'] = json.loads(docker('exec', args.expected_container_id, 'python3', '-c', stage).stdout)
    source_hash = hashlib.sha256((HERE/'guestctl.py').read_bytes()).hexdigest()
    docker('cp', str(HERE/'guestctl.py'), args.expected_container_id+':/opt/guest/guestctl.next.py')
    activate = ('import hashlib,os,pathlib;'
                'p=pathlib.Path("/opt/guest/guestctl.next.py");b=p.read_bytes();'
                'assert hashlib.sha256(b).hexdigest()=='+repr(source_hash)+';'
                'compile(b,str(p),"exec");os.replace(p,"/opt/guest/guestctl.py")')
    docker('exec', args.expected_container_id, 'python3', '-c', activate)
    _, after = identity(args.expected_container_id, args.expected_run_id)
    require(after['boot_id'] == guest['boot_id'], 'Guest rebooted during preparation')
    require(host_config(args.expected_container_id) == report['host_config_before'], 'Limits/settings changed during preparation')
    require(immutable_container(args.expected_container_id) == report['immutable_container'], 'Container configuration/mounts/image changed')
    report.update(phase='prepared', launcher_sha256=source_hash,
                  note='Access checkpoint remains container-private. Live disk, running QEMU and resource limits unchanged.')
    save_receipt(args.evidence, report)
    return report


def relaunch(args):
    report = json.loads(args.evidence.read_text())
    require(report['phase'] == 'prepared', 'Expected a successfully prepared checkpoint')
    require(report['container_id'] == args.expected_container_id and report['run_id'] == args.expected_run_id,
            'Receipt identity mismatch')
    _, guest = identity(args.expected_container_id, args.expected_run_id)
    require(guest['boot_id'] == report['guest_before']['boot_id'], 'Guest boot changed since preparation')
    require(host_config(args.expected_container_id) == report['host_config_before'], 'Container settings changed since preparation')
    require(immutable_container(args.expected_container_id) == report['immutable_container'], 'Container configuration/mounts/image changed')
    installed_hash = docker('exec', args.expected_container_id, 'sha256sum', '/opt/guest/guestctl.py').stdout.split()[0]
    require(installed_hash == report['launcher_sha256'], 'Prepared launcher changed')
    report['phase'] = 'powering_off'
    save_receipt(args.evidence, report)
    # Never force-kill QEMU or truncate/recreate the disk. If orderly poweroff does
    # not finish, stop here with the old limits and report for inspection.
    docker('exec', args.expected_container_id, 'python3', '/opt/guest/guestctl.py',
           'exec', 'systemctl', 'poweroff', check=False)
    deadline = time.monotonic()+240
    while time.monotonic() < deadline:
        state = json.loads(docker('inspect', args.expected_container_id).stdout)[0]['State']
        if state['Status'] == 'exited':
            require(state['ExitCode'] == 0 and not state['OOMKilled'], 'Guest did not shut down cleanly')
            break
        time.sleep(2)
    else:
        raise RuntimeError('Graceful shutdown did not complete; no limits were changed')
    identity(args.expected_container_id, args.expected_run_id, require_running=False)
    docker('update', '--cpus', '4', '--memory', '6g', args.expected_container_id)
    limits_only(report['host_config_before'], host_config(args.expected_container_id))
    require(immutable_container(args.expected_container_id) == report['immutable_container'], 'Container configuration/mounts/image changed')
    report['phase'] = 'starting_original_disk'
    save_receipt(args.evidence, report)
    docker('start', args.expected_container_id)
    # The new entrypoint first makes and verifies the offline disk checkpoint.
    subprocess.run(['sudo','-n','docker','exec',args.expected_container_id,'python3',
                    '/opt/guest/guestctl.py','ready'],check=True,capture_output=True,text=True,timeout=960)
    isolation, after = identity(args.expected_container_id, args.expected_run_id)
    verify_guest_continuity(report['guest_before'], after)
    limits_only(report['host_config_before'], host_config(args.expected_container_id))
    require(immutable_container(args.expected_container_id) == report['immutable_container'], 'Container configuration/mounts/image changed')
    checkpoint = ('import json,pathlib;m=json.loads(pathlib.Path("/opt/guest/resume/manifest.json").read_text());'
                  'print(json.dumps({k:m[k] for k in ("checkpoint_complete","disk_checkpoint_sha256","run_id")}))')
    report['disk_checkpoint'] = json.loads(docker('exec',args.expected_container_id,'python3','-c',checkpoint).stdout)
    require(report['disk_checkpoint']['checkpoint_complete'], 'Offline disk checkpoint not confirmed')
    report.update(phase='complete', guest_after=after, isolation_after=isolation,
                  private_access_restored_from_verified_container_checkpoint=True,
                  note='Same container and original disk; same guest marker; new boot ID; four guest CPUs. Guest memory remains 3072 MiB; outer memory is 6 GiB. Application readiness requires separate acceptance.')
    save_receipt(args.evidence, report)
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['prepare','relaunch'])
    parser.add_argument('--expected-container-id', required=True)
    parser.add_argument('--expected-run-id', required=True, type=lambda s:str(uuid.UUID(s)))
    parser.add_argument('--evidence', required=True, type=Path)
    args=parser.parse_args()
    require(not sys.flags.optimize, 'Python optimization disables required assertions')
    require(len(args.expected_container_id)==64 and all(c in '0123456789abcdef' for c in args.expected_container_id),
            'Use the full expected container ID')
    os.umask(0o077)
    report=prepare(args) if args.action=='prepare' else relaunch(args)
    print(json.dumps({k:report[k] for k in ('phase','container_id','run_id')},indent=2))


if __name__=='__main__': main()
