"""Observe the installed kiosk and virtual display; never send input or roof commands.

Run on the Linux development host only after installation is complete. The raw
screen is private evidence and must be visually reviewed before being shared.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid
import zlib

from run_guest import NAME, inspect

GUEST_QUERY = r'''
import hashlib,json,os,pathlib,pwd,stat,subprocess,sys
marker=pathlib.Path('/etc/hvo-disposable-guest.json')
ms=marker.stat()
guard=json.loads(marker.read_text())
if (ms.st_uid!=0 or stat.S_IMODE(ms.st_mode)&0o022 or
    guard.get('purpose')!='hvo-auth-install-test' or guard.get('run_id')!=sys.argv[1]):
    raise SystemExit('Disposable guest marker did not match')
virt=subprocess.check_output(['systemd-detect-virt'],text=True).strip()
if virt!='qemu': raise SystemExit('Expected the disposable QEMU guest')
props=['LoadState','ActiveState','SubState','MainPID','ExecMainCode','ExecMainStatus',
       'NRestarts','FragmentPath','User','Group','UnitFileState',
       'ActiveEnterTimestampMonotonic','ExecMainStartTimestampMonotonic']
unit='hvo-roof-kiosk.service'
raw=subprocess.check_output(['systemctl','show',unit,'--no-pager',
                             '--property='+','.join(props)],text=True)
service=dict(line.split('=',1) for line in raw.splitlines() if '=' in line)
if set(service)-set(props): raise SystemExit('Unexpected service metadata')
def metadata(path):
    p=pathlib.Path(path)
    if not p.is_file(): return {'present':False}
    s=p.stat()
    return {'present':True,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),
            'uid':s.st_uid,'gid':s.st_gid,'mode':oct(stat.S_IMODE(s.st_mode))}
installed_unit='/etc/systemd/system/'+unit
binary='/opt/hvo-roof-kiosk/hvo-roof-kiosk'
process={'present':False}
pid=int(service.get('MainPID') or 0)
if pid:
    proc=pathlib.Path('/proc')/str(pid)
    try:
        exe=os.readlink(proc/'exe')
        actual_uid=proc.stat().st_uid
        expected_uid=pwd.getpwnam('hvo-kiosk').pw_uid
        process={'present':True,'pid':pid,'executable':exe,
                 'uid':actual_uid,'expected_uid':expected_uid,
                 'matches_expected_executable':exe==binary,
                 'matches_expected_user':actual_uid==expected_uid}
    except (FileNotFoundError,KeyError):
        process={'present':False,'pid':pid}
connectors=[]
for p in sorted(pathlib.Path('/sys/class/drm').glob('card*-*/status')):
    value=p.read_text().strip()
    connectors.append({'connector':p.parent.name,'status':value})
print(json.dumps({'run_id':guard['run_id'],'virtualization':virt,
    'boot_id':pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
    'service':service,'installed_unit':metadata(installed_unit),
    'installed_binary':metadata(binary),'process':process,
    'drm_connectors':connectors}))
'''

# QMP screendump is observational. The temporary screenshot exists only in the
# outer container, is removed after reading, and never enters the guest disk.
OUTER_SCREEN = r'''
import os,socket,sys,tempfile,json
fd,path=tempfile.mkstemp(prefix='kiosk-evidence-',suffix='.ppm',dir='/run/guest')
os.close(fd)
try:
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as s:
        s.settimeout(20)
        s.connect('/run/guest/qmp.sock')
        with s.makefile('rwb') as f:
            if 'QMP' not in json.loads(f.readline()): raise RuntimeError('Missing QMP greeting')
            def call(command,arguments=None):
                request={'execute':command,'id':command}
                if arguments: request['arguments']=arguments
                f.write((json.dumps(request)+'\n').encode()); f.flush()
                while True:
                    response=json.loads(f.readline())
                    if response.get('id')!=command: continue
                    if 'error' in response: raise RuntimeError('QMP observational command failed')
                    return response.get('return')
            call('qmp_capabilities')
            call('screendump',{'filename':path})
    if os.path.getsize(path)>64*1024*1024: raise RuntimeError('Unexpectedly large framebuffer')
    with open(path,'rb') as f: sys.stdout.buffer.write(f.read())
finally:
    os.unlink(path)
'''


def run(*args, binary=False):
    result = subprocess.run(args, capture_output=True, text=not binary, timeout=90)
    if result.returncode:
        # No raw SSH/systemd/QMP stderr in published evidence.
        raise RuntimeError('An observational command failed (exit %d)' % result.returncode)
    return result.stdout


def encode_png(ppm):
    """Losslessly encode QEMU's RGB P6 output with the standard library only."""
    offset = 0
    tokens = []
    whitespace = b' \t\r\n\v\f'
    while len(tokens) < 4:
        while offset < len(ppm) and ppm[offset] in whitespace:
            offset += 1
        if offset < len(ppm) and ppm[offset] == ord('#'):
            offset = ppm.index(b'\n', offset) + 1
            continue
        end = offset
        while end < len(ppm) and ppm[end] not in whitespace:
            end += 1
        if end == offset:
            raise ValueError('Incomplete PPM header')
        tokens.append(ppm[offset:end])
        offset = end
    if tokens[0] != b'P6' or tokens[3] != b'255':
        raise ValueError('Expected QEMU RGB8 PPM')
    width, height = int(tokens[1]), int(tokens[2])
    if not (1 <= width <= 8192 and 1 <= height <= 8192):
        raise ValueError('Unexpected framebuffer dimensions')
    if offset >= len(ppm) or ppm[offset] not in whitespace:
        raise ValueError('Missing PPM data delimiter')
    offset += 1  # P6 has exactly one delimiter before the binary pixel data.
    pixels = ppm[offset:]
    if len(pixels) != width * height * 3:
        raise ValueError('Unexpected framebuffer byte count')
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    scanlines = b''.join(b'\0' + pixels[y*width*3:(y+1)*width*3] for y in range(height))
    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(scanlines)) + chunk(b'IEND', b'')
    return png, {'width': width, 'height': height, 'all_pixels_black': not any(pixels),
                 'pixels_sha256': hashlib.sha256(pixels).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--expected-run-id', required=True, type=uuid.UUID)
    parser.add_argument('--output-dir', required=True, type=Path,
                        help='New private directory; an existing directory is refused')
    args = parser.parse_args()
    if sys.flags.optimize:
        raise SystemExit('Run without Python optimization; isolation assertions are required')
    os.umask(0o077)
    isolation = inspect()
    if isolation['state'] != 'running':
        raise SystemExit('The owned isolated container must already be running')
    observed = json.loads(run('sudo', '-n', 'docker', 'exec', NAME,
                             'python3', '/opt/guest/guestctl.py', 'exec',
                             'python3', '-c', GUEST_QUERY, str(args.expected_run_id)))
    # Source-relative expectations come from the installed unit and kiosk docs.
    repo = Path(__file__).resolve().parents[3]
    source_unit = repo / 'src/HVO.RoofControllerV4.Kiosk/deploy/hvo-roof-kiosk.service'
    source_hash = hashlib.sha256(source_unit.read_bytes()).hexdigest()
    service, process = observed['service'], observed['process']
    checks = {
        'unit_loaded': service.get('LoadState') == 'loaded',
        'unit_enabled': service.get('UnitFileState') == 'enabled',
        'service_active_running': service.get('ActiveState') == 'active' and service.get('SubState') == 'running',
        'unit_path_expected': service.get('FragmentPath') == '/etc/systemd/system/hvo-roof-kiosk.service',
        'unit_matches_current_source': observed['installed_unit'].get('sha256') == source_hash,
        'service_user_expected': service.get('User') == service.get('Group') == 'hvo-kiosk',
        'process_executable_expected': process.get('matches_expected_executable', False),
        'process_user_expected': process.get('matches_expected_user', False),
        'virtual_display_connected': any(c['status'] == 'connected' for c in observed['drm_connectors']),
    }
    # Refuse replacing evidence. Screens can show sensitive UI, so do not publish
    # automatically and do not read environment/configuration/PIN/token files.
    args.output_dir.mkdir(mode=0o700, parents=False, exist_ok=False)
    ppm = run('sudo', '-n', 'docker', 'exec', NAME, 'python3', '-c', OUTER_SCREEN, binary=True)
    png, frame = encode_png(ppm)
    screenshot = args.output_dir / 'kiosk-framebuffer.png'
    screenshot.write_bytes(png)
    frame.update({'file': screenshot.name, 'sha256': hashlib.sha256(png).hexdigest(),
                  'visually_reviewed': False, 'safe_to_publish': False,
                  'note': 'Private raw display capture. Review for secrets and actual kiosk content before sharing; a running service does not prove the displayed UI.'})
    report = {
        'captured_at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'scope': 'Installed kiosk unit/process and actual QEMU framebuffer only; no input, settings, API, or roof command sent.',
        'isolation': isolation, 'guest': observed, 'checks': checks,
        'unit_and_process_checks_passed': all(checks.values()),
        'source_unit_sha256': source_hash,
        'source_references': ['src/HVO.RoofControllerV4.Kiosk/deploy/hvo-roof-kiosk.service',
                              'src/HVO.RoofControllerV4.Installer/Plan/KioskSteps.cs', 'docs/kiosk.md'],
        'framebuffer': frame,
    }
    report_path = args.output_dir / 'kiosk-evidence.json'
    report_path.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'report': str(report_path), 'private_framebuffer': str(screenshot),
                      'unit_and_process_checks_passed': all(checks.values()),
                      'all_pixels_black': frame['all_pixels_black'],
                      'visual_review_required': True}))
    return 0 if all(checks.values()) else 2


if __name__ == '__main__':
    raise SystemExit(main())
