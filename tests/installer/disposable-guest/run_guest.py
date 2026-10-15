"""Create/inspect/remove only the labeled disposable guest container."""
import argparse
import hashlib
import json
import pathlib
import subprocess

NAME='hvo-disposable-guest-auth-integration'
LABEL='hvo.disposable-guest=auth-integration'
IMAGE='hvo-disposable-guest:auth-integration'

def docker(*args, check=True):
    return subprocess.run(['sudo','-n','docker',*args],capture_output=True,text=True,check=check)

def host_state():
    # Read-only privileged hashing returns no account or policy contents.
    script='''import hashlib,json,pathlib,stat
paths=[pathlib.Path('/etc/passwd'),pathlib.Path('/etc/group'),pathlib.Path('/etc/sudoers')]
for directory in ('/etc/pam.d','/etc/sudoers.d'):
    paths.extend(p for p in pathlib.Path(directory).rglob('*') if p.is_file())
result={}
for p in sorted(paths):
    s=p.stat()
    result[str(p)]={'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'uid':s.st_uid,'gid':s.st_gid,'mode':stat.S_IMODE(s.st_mode)}
print(json.dumps(result))
'''
    return json.loads(subprocess.run(['sudo','-n','python3','-c',script],capture_output=True,text=True,check=True).stdout)

def inspect():
    data=json.loads(docker('inspect',NAME).stdout)[0]
    h=data['HostConfig']
    assert data['Config']['Labels'].get('hvo.disposable-guest')=='auth-integration'
    assert h['NetworkMode']=='none' and not h['Privileged'] and not h['Binds']
    assert not h['Devices'] and not h['PortBindings'] and h['CapDrop']==['ALL']
    assert 'no-new-privileges' in h['SecurityOpt']
    # Only the original profile and the explicitly approved cold-resume profile.
    assert (h['NanoCpus'],h['Memory']) in {(2*10**9,4*1024**3),(4*10**9,6*1024**3)}
    assert h['PidsLimit']==128
    return {'id':data['Id'],'network':'none','privileged':False,'host_binds':False,'devices':False,
            'published_ports':False,'cap_drop':h['CapDrop'],'no_new_privileges':True,
            'memory_bytes':h['Memory'],'cpus':h['NanoCpus']//10**9,'pids_limit':128,'state':data['State']['Status']}

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('action',choices=['start','inspect','cleanup'])
    parser.add_argument('--evidence',type=pathlib.Path,default=pathlib.Path('/tmp/hvo-disposable-guest-evidence.json'))
    args=parser.parse_args()
    if args.action=='start':
        if docker('inspect',NAME,check=False).returncode==0: raise SystemExit('Container already exists; inspect/reuse it')
        report={'host_before':host_state(),'image_id':docker('image','inspect',IMAGE,'--format','{{.Id}}').stdout.strip()}
        docker('create','--name',NAME,'--label',LABEL,'--network','none','--memory','4g','--cpus','2',
               '--pids-limit','128','--cap-drop','ALL','--security-opt','no-new-privileges',
               '--tmpfs','/run:rw,nosuid,nodev,size=64m','--tmpfs','/tmp:rw,nosuid,nodev,size=256m',IMAGE)
        report['isolation']=inspect()
        args.evidence.write_text(json.dumps(report,indent=2)+'\n')
        docker('start',NAME)
        print(json.dumps(report,indent=2))
    elif args.action=='inspect': print(json.dumps(inspect(),indent=2))
    elif args.action=='cleanup':
        report=json.loads(args.evidence.read_text())
        current=inspect() # ownership, exact identity and isolation must match before removal
        assert current['id']==report['isolation']['id']
        docker('rm','--force','--volumes',NAME)
        report['container_removed']=docker('inspect',NAME,check=False).returncode!=0
        report['host_after']=host_state()
        report['host_accounts_and_policy_unchanged']=report['host_before']==report['host_after']
        args.evidence.write_text(json.dumps(report,indent=2)+'\n')
        print(json.dumps(report,indent=2))

if __name__=='__main__': main()
