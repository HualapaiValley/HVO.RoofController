#!/usr/bin/env python3
"""Native worker checks. May run ONLY in a disposable Docker container, never on a host."""
import base64
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import uuid

ROOT = Path('/etc/hvo-roof/linux-auth')
RUN = Path('/run/hvo-roof-linux-auth')
EXE = '/opt/hvo-roof-linux-auth/HVO.RoofControllerV4.LinuxAuth'
HELPER = '/usr/local/libexec/hvo-roof-local-auth'
CTRL = str(RUN / 'controller/worker.sock')
LOCAL = str(RUN / 'local.sock')


def call(*args, **kwargs):
    return subprocess.run(args, capture_output=True, text=True, check=True, **kwargs)


class UnixConnection(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__('localhost', timeout=10)
        self.path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX)
        self.sock.settimeout(10)
        self.sock.connect(self.path)


def request(route, body, path=CTRL):
    connection = UnixConnection(path)
    try:
        connection.request('POST', '/v1/' + route, json.dumps(body), {'Content-Type': 'application/json'})
        response = connection.getresponse()
        return response.status, json.loads(response.read())
    finally:
        connection.close()


def helper(user, body, env=None, direct=False):
    command = ['runuser', '-u', user, '--']
    if env:
        command += ['env', *[f'{k}={v}' for k, v in env.items()]]
    if not direct:
        command += ['sudo', '-n', '--']
    return subprocess.run(command + [HELPER], input=json.dumps(body), text=True, capture_output=True, timeout=15)


def main():
    if os.geteuid() != 0 or not Path('/.dockerenv').exists() or os.environ.get('HVO_DISPOSABLE_FIXTURE') != '1':
        raise SystemExit('Disposable container required')
    if ROOT.exists():
        raise SystemExit('Refusing pre-existing fixture configuration')
    ROOT.mkdir(parents=True, mode=0o700)
    (RUN / 'controller').mkdir(parents=True, mode=0o750)
    entries = []
    passwords = {}
    for index, suffix in enumerate(['admin', 'limited', 'plain', 'controller', 'expired', 'locked']):
        name = 'fixture-' + suffix
        uid = 27001 + index
        call('useradd', '-U', '-M', '-u', str(uid), '-s', '/usr/sbin/nologin', name)
        passwords[name] = secrets.token_urlsafe(32)
        call('chpasswd', input=name + ':' + passwords[name] + '\n')
        entries.append(dict(uid=uid, userName=name, subject=str(uuid.uuid4()), enrollmentStamp=str(uuid.uuid4()), enabled=True))
    call('chage', '-E', '0', 'fixture-expired')
    call('passwd', '-l', 'fixture-locked')
    key = secrets.token_bytes(32)
    (ROOT / 'assertion.key').write_bytes(key)
    (ROOT / 'enrollment.json').write_text(json.dumps(entries))
    (ROOT / 'worker.json').write_text(json.dumps(dict(controllerUid=27004, controllerGroupId=27004,
        audience='native-disposable-fixture', controllerSocketPath=CTRL, localSocketPath=LOCAL)))
    for path in ROOT.iterdir():
        path.chmod(0o600)
    os.chown(RUN, 0, 27004)
    RUN.chmod(0o750)
    os.chown(RUN / 'controller', 0, 27004)
    policy = Path('/etc/sudoers.d/hvo-native-fixture')
    policy.write_text(f'fixture-admin,fixture-limited,fixture-locked,fixture-expired ALL=(root) NOPASSWD: {HELPER} ""\n')
    policy.chmod(0o440)
    call('visudo', '-cf', str(policy))
    checks = []

    def check(name, condition):
        checks.append(dict(name=name, passed=bool(condition)))
        if not condition:
            raise AssertionError(name)

    def decode(result):
        token = json.loads(result.stdout)['assertion']
        payload, signature = token.split('.')
        raw = base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4))
        mac = base64.urlsafe_b64decode(signature + '=' * (-len(signature) % 4))
        check('assertion MAC verifies', hmac.compare_digest(mac, hmac.digest(key, raw, 'sha256')))
        return json.loads(raw)

    notify = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
    notify.bind('/run/hvo-native-notify.test')
    notify.settimeout(3)
    worker_env = os.environ | {'NOTIFY_SOCKET': '/run/hvo-native-notify.test'}
    startup_started = time.monotonic()
    process = subprocess.Popen([EXE, 'serve'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=worker_env, cwd='/')
    try:
        for _ in range(100):
            if Path(CTRL).exists() and Path(LOCAL).exists():
                time.sleep(.1)
                break
            if process.poll() is not None:
                raise AssertionError('worker failed to start')
            time.sleep(.05)
        check('private socket modes', Path(LOCAL).stat().st_mode & 0o777 == 0o600 and Path(CTRL).stat().st_mode & 0o777 == 0o660)
        check('service readiness notification follows socket setup', notify.recv(128) == b'READY=1')
        check('worker launched from filesystem root becomes ready within service deadline', time.monotonic() - startup_started < 15)
        watch_count = sum(path.read_text().count('inotify wd:') for path in (Path('/proc') / str(process.pid) / 'fdinfo').iterdir())
        check('worker does not retain unused application configuration file watchers', watch_count == 0)
        result = helper('fixture-admin', {'operation': 'identity'})
        check('native sudo approved caller', result.returncode == 0)
        proof = decode(result)
        check('bound native assertion claims', proof['purpose'] == 'local-admin' and proof['operation'] == 'identity'
              and proof['subject'] == entries[0]['subject'] and proof['expiresAt'] - proof['issuedAt'] == 30)
        check('unprivileged sudo denied', helper('fixture-plain', {'operation': 'read'}).returncode != 0)
        check('direct unprivileged helper denied', helper('fixture-admin', {'operation': 'read'}, direct=True).returncode != 0)
        check('caller supplied UID rejected', helper('fixture-admin', {'operation': 'read', 'uid': 27002}).returncode != 0)
        check('portable session input rejected', helper('fixture-admin', {'operation': 'read', 'session': 'inert'}).returncode != 0)
        check('unknown operation rejected', helper('fixture-admin', {'operation': 'anything'}).returncode != 0)
        result = helper('fixture-limited', {'operation': 'configure'}, env={'SUDO_UID': '27001', 'DOTNET_STARTUP_HOOKS': '/missing'})
        check('sudo protected origin and sanitized runtime', result.returncode == 0 and decode(result)['subject'] == entries[1]['subject'])
        check('controller root cannot call local signer', request('local', {'uid': 27001, 'operation': 'read'})[0] == 401)
        check('wrong password PAM denial', request('password', {'userName': 'fixture-admin', 'password': secrets.token_urlsafe(16)})[0] == 401)
        status, value = request('password', {'userName': 'fixture-admin', 'password': passwords['fixture-admin']})
        check('native PAM password authentication', status == 200 and 'assertion' in value)
        for suffix in ['expired', 'locked']:
            name = 'fixture-' + suffix
            check(suffix + ' password denied', request('password', {'userName': name, 'password': passwords[name]})[0] == 401)
            entry = next(e for e in entries if e['userName'] == name)
            check(suffix + ' revalidation denied', request('revalidate', {k: entry[k] for k in ['subject', 'enrollmentStamp']})[0] == 401)
        check('locked-password SSH-key administrator permitted native sudo', helper('fixture-locked', {'operation': 'identity'}).returncode == 0)
        check('expired administrator denied native sudo', helper('fixture-expired', {'operation': 'identity'}).returncode != 0)
        locked = next(e for e in entries if e['userName'] == 'fixture-locked')
        check('locked-password local proof revalidates independently of web password', request('revalidate',
              dict(subject=locked['subject'], enrollmentStamp=locked['enrollmentStamp'], purpose='local-admin'))[0] == 200)
        proof_request = {k: entries[0][k] for k in ['subject', 'enrollmentStamp']}
        check('current enrollment revalidates', request('revalidate', proof_request)[0] == 200)
        entries[0]['enabled'] = False
        (ROOT / 'enrollment.json').write_text(json.dumps(entries))
        check('native enrollment disable immediately revokes', request('revalidate', proof_request)[0] == 401)
        check('disabled enrollment blocks local helper', helper('fixture-admin', {'operation': 'read'}).returncode != 0)
        entries[0]['enabled'] = True
        entries[0]['enrollmentStamp'] = str(uuid.uuid4())
        (ROOT / 'enrollment.json').write_text(json.dumps(entries))
        check('changed enrollment stamp rejects old identity', request('revalidate', proof_request)[0] == 401)
        entries[0]['uid'] = 99999
        (ROOT / 'enrollment.json').write_text(json.dumps(entries))
        check('native UID mismatch fails closed', request('password', {'userName': 'fixture-admin', 'password': passwords['fixture-admin']})[0] == 401)
        entries[0]['uid'] = 27001
        (ROOT / 'enrollment.json').write_text(json.dumps(entries))
        proof_request = {k: entries[0][k] for k in ['subject', 'enrollmentStamp']}
        original_pam = Path('/etc/pam.d/hvo-roof-linux-auth').read_text()
        delay = Path('/opt/fixture-pam-delay')
        delay.write_text('#!/bin/sh\nsleep 15\n')
        delay.chmod(0o700)
        Path('/etc/pam.d/hvo-roof-linux-auth').write_text('account required pam_exec.so /opt/fixture-pam-delay\n')
        started = time.monotonic()
        check('hung PAM transaction fails closed within bounded child lifetime', request('revalidate', proof_request)[0] == 401
              and time.monotonic() - started < 8)
        Path('/etc/pam.d/hvo-roof-linux-auth').write_text(original_pam)
        check('worker remains usable after PAM timeout', request('revalidate', proof_request)[0] == 200)
        (ROOT / 'enrollment.json').chmod(0o666)
        check('unsafe enrollment permissions fail closed', request('revalidate', proof_request)[0] == 401)
        (ROOT / 'enrollment.json').chmod(0o600)
        # Give an unrelated peer directory access; kernel UID gate must still deny it.
        script = "import socket,http.client,json; s=socket.socket(socket.AF_UNIX); s.connect(" + repr(CTRL) + "); s.sendall(b'POST /v1/revalidate HTTP/1.1\\r\\nHost: localhost\\r\\nContent-Length: 2\\r\\nConnection: close\\r\\n\\r\\n{}'); print(s.recv(1024).split(b'\\r\\n')[0].decode())"
        denied = subprocess.run(['setpriv', '--reuid=27003', '--regid=27004', '--clear-groups', 'python3', '-c', script], capture_output=True, text=True)
        check('kernel peer UID gate denies filesystem-authorized outsider', denied.returncode == 0 and '401' in denied.stdout)
        # The actual configured non-root controller peer can use only its private endpoint.
        body = json.dumps(proof_request).encode()
        wire = b'POST /v1/revalidate HTTP/1.1\r\nHost: localhost\r\nContent-Length: ' + str(len(body)).encode() + b'\r\nConnection: close\r\n\r\n' + body
        allowed_script = 'import socket; s=socket.socket(socket.AF_UNIX); s.connect(' + repr(CTRL) + '); s.sendall(' + repr(wire) + "); print(s.recv(1024).split(b'\\r\\n')[0].decode())"
        allowed = subprocess.run(['setpriv', '--reuid=27004', '--regid=27004', '--clear-groups', 'python3', '-c', allowed_script], capture_output=True, text=True)
        check('configured non-root controller peer succeeds', allowed.returncode == 0 and '200' in allowed.stdout)
        competing = subprocess.run([EXE, 'serve'], capture_output=True, text=True, timeout=5)
        check('active worker exclusive lock prevents socket replacement', competing.returncode != 0
              and request('revalidate', proof_request)[0] == 200)
        directory_inode = (RUN / 'controller').stat().st_ino
        process.kill()
        process.communicate(timeout=5)
        process = subprocess.Popen([EXE, 'serve'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=worker_env, cwd='/')
        ready = False
        for _ in range(100):
            if process.poll() is not None:
                break
            try:
                ready = request('revalidate', proof_request)[0] == 200
                if ready:
                    break
            except (OSError, http.client.HTTPException):
                pass
            time.sleep(.05)
        check('worker crash restart preserves mounted directory inode and recovers authentication', ready
              and (RUN / 'controller').stat().st_ino == directory_inode)
        check('restart announces fresh service readiness', notify.recv(128) == b'READY=1')
        check('no TCP listener from worker', all(line.split()[1].split(':')[-1] == '0000'
              for file in ['/proc/net/tcp', '/proc/net/tcp6'] for line in Path(file).read_text().splitlines()[1:]
              if line.split()[3] == '0A'))
    finally:
        notify.close()
        process.terminate()
        try:
            process.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate(timeout=5)
        print(json.dumps(dict(checks=checks, passed=sum(x['passed'] for x in checks), failed=sum(not x['passed'] for x in checks),
            fixtureOnly=True, nativePam=True, nativeSudo=True, productionApi=False)))


if __name__ == '__main__':
    main()
