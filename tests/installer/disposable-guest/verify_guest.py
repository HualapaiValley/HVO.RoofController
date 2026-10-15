"""Record native harness feasibility; this is not an HVO application acceptance test."""
import json
import pathlib
import subprocess
import sys

NAME='hvo-disposable-guest-auth-integration'
CTL=['sudo','-n','docker','exec',NAME,'python3','/opt/guest/guestctl.py']

def call(*args):
    return subprocess.run([*CTL,*args],capture_output=True,text=True,check=True).stdout.strip()

def main():
    evidence=pathlib.Path(sys.argv[1])
    report={'scope':'native guest harness only; no HVO installation or authentication claim'}
    report['isolation']=json.loads(subprocess.run([sys.executable,str(pathlib.Path(__file__).with_name('run_guest.py')),'inspect'],capture_output=True,text=True,check=True).stdout)
    call('ready')
    report['guest']=json.loads(call('exec','python3','-c','''import json,pathlib,subprocess
marker=json.loads(pathlib.Path('/etc/hvo-disposable-guest.json').read_text())
virt=subprocess.check_output(['systemd-detect-virt'],text=True).strip()
assert marker['purpose']=='hvo-auth-install-test' and virt=='qemu'
assert pathlib.Path('/etc/hvo-disposable-guest.json').stat().st_uid==0
print(json.dumps({'virtualization':virt,'kernel':subprocess.check_output(['uname','-r'],text=True).strip(),
 'systemd':subprocess.run(['systemctl','is-system-running'],capture_output=True,text=True).stdout.strip(),
 'docker_version':subprocess.check_output(['docker','version','--format','{{.Server.Version}}'],text=True).strip(),
 'drm_devices':[p.name for p in pathlib.Path('/dev/dri').glob('*')],
 'connected_displays':sum(p.read_text().strip()=='connected' for p in pathlib.Path('/sys/class/drm').glob('card*-*/status')),
 'input_devices':[p.read_text().strip() for p in pathlib.Path('/sys/class/input').glob('event*/device/name')],
 'marker_purpose':marker['purpose']}))
'''))
    report['nested_container']=json.loads(call('exec','python3','-c','''import json,pathlib,shutil,subprocess,tarfile,tempfile
with tempfile.TemporaryDirectory(prefix='hvo-harness-') as temporary:
 root=pathlib.Path(temporary)/'root'
 for original,target in [('/usr/bin/true','bin/true'),('/lib/aarch64-linux-gnu/libc.so.6','lib/aarch64-linux-gnu/libc.so.6'),('/lib/ld-linux-aarch64.so.1','lib/ld-linux-aarch64.so.1')]:
  dest=root/target; dest.parent.mkdir(parents=True,exist_ok=True); shutil.copyfile(original,dest); dest.chmod(0o755)
 archive=pathlib.Path(temporary)/'root.tar'
 with tarfile.open(archive,'w') as tar: tar.add(root,arcname='.')
 tag='hvo-harness-probe:local'
 subprocess.run(['docker','import',str(archive),tag],check=True,capture_output=True)
 try:
  subprocess.run(['docker','run','--rm','--network','none','--name','hvo-harness-probe',tag,'/bin/true'],check=True,capture_output=True)
 finally: subprocess.run(['docker','image','rm',tag],check=True,capture_output=True)
 print(json.dumps({'actual_guest_docker_container_started':True,'probe_image_removed':True}))
'''))
    call('snapshot','harness-proof')
    call('exec','touch','/var/lib/hvo-harness-rollback-marker')
    report['reboot']=json.loads(call('reboot'))
    call('exec','test','-f','/var/lib/hvo-harness-rollback-marker')
    report['restore']=json.loads(call('restore','harness-proof'))
    call('exec','test','!','-e','/var/lib/hvo-harness-rollback-marker')
    report['restore']['post_snapshot_file_absent']=True
    subprocess.run(['sudo','-n','docker','exec',NAME,'python3','-c',"import sys; sys.path.insert(0,'/opt/guest'); import guestctl; guestctl.monitor('delvm harness-proof')"],check=True)
    report['browser']=json.loads(subprocess.run(['sudo','-n','docker','exec',NAME,'python3','-c',
        "import json; from playwright.sync_api import sync_playwright\nwith sync_playwright() as p:\n b=p.chromium.launch(headless=True,args=['--no-sandbox']); page=b.new_page(); page.set_content('<title>Disposable harness</title>'); assert page.title()=='Disposable harness'; print(json.dumps({'actual_chromium_launch':True,'version':b.version})); b.close()"],check=True,capture_output=True,text=True).stdout)
    report['cloud_image_sha256']=subprocess.run(['sudo','-n','docker','exec',NAME,'cat','/opt/guest/verified-image-sha256.txt'],check=True,capture_output=True,text=True).stdout.strip()
    report['status']='ready for HVO installation acceptance'
    evidence.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report,indent=2))

if __name__=='__main__': main()
