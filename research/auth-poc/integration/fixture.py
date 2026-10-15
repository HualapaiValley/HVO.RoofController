"""Disposable integrated auth fixture, NOT the production HVO installer or website.
Real Linux UIDs, sudo policy, PAM pam_unix, Unix sockets and loopback HTTP.
Application authorization reuses the inert policy model; no hardware operations.
"""
import ctypes
import ctypes.util
from dataclasses import replace
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import pwd
import shutil
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

PACKAGE = Path('/opt/hvo-poc-package')
PREFIX = Path('/opt/hvo-auth-fixture')
STATE = Path('/run/hvo-auth-fixture')
SOCKET = STATE / 'broker.sock'
# Public disposable-fixture values; never valid on the host or another installation.
FIXTURE_PASSWORD = 'Inert-fixture-input-2026!'
UIDS = {'fixture-admin': 26001, 'fixture-limited': 26002,
        'fixture-user': 26003, 'fixture-expired': 26004}


def abort(code, detail):
    print(json.dumps({'error': detail}))
    return code


def helper():
    # This fixed, root-owned program is the ONLY command permitted by fixture
    # sudoers. SUDO_UID is accepted only inside that privileged sudo path.
    # Host/container root is part of the TCB; root can impersonate any actor.
    if os.geteuid() != 0:
        return abort(3, 'PrivilegedHelperRequired')
    command = os.environ.get('SUDO_COMMAND')
    raw_uid = os.environ.get('SUDO_UID', '')
    if command != str(PREFIX / 'local-helper') or not raw_uid.isdecimal():
        return abort(3, 'TrustedSudoInvocationRequired')
    uid = int(raw_uid)
    if uid == 0:
        return abort(3, 'OriginalCallerRequired')
    raw = sys.stdin.buffer.read(1025)
    try:
        request = json.loads(raw)
    except (ValueError, UnicodeDecodeError):
        return abort(2, 'InvalidRequest')
    if len(raw) > 1024 or not isinstance(request, dict) or set(request) != {'operation'}:
        return abort(2, 'IdentityClaimsNotAccepted')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(5)
        connection.connect(str(SOCKET))
        request['sudo_uid'] = uid
        connection.sendall(json.dumps(request).encode() + b'\n')
        answer = connection.makefile('rb').readline(4096)
    print(answer.decode().strip())
    return 0 if json.loads(answer).get('ok') else 3


def local_client():
    # Batch semantics only: sudo -n must never prompt or read a password.
    result = subprocess.run(['/usr/bin/sudo', '-n', '--', str(PREFIX/'local-helper')],
                            input=sys.stdin.buffer.read(1025), capture_output=True, timeout=8)
    if result.returncode and not result.stdout:
        return abort(3, 'LinuxAdminPermissionDenied')
    sys.stdout.buffer.write(result.stdout)
    return result.returncode


def pam_authenticate(login, password):
    library = ctypes.CDLL(ctypes.util.find_library('pam'))
    libc = ctypes.CDLL(ctypes.util.find_library('c'))
    class Message(ctypes.Structure):
        _fields_ = [('style', ctypes.c_int), ('msg', ctypes.c_char_p)]
    class Response(ctypes.Structure):
        _fields_ = [('resp', ctypes.c_void_p), ('code', ctypes.c_int)]
    callback_type = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_int,
        ctypes.POINTER(ctypes.POINTER(Message)), ctypes.POINTER(ctypes.POINTER(Response)), ctypes.c_void_p)
    class Conversation(ctypes.Structure):
        _fields_ = [('conv', callback_type), ('data', ctypes.c_void_p)]
    libc.calloc.argtypes = [ctypes.c_size_t, ctypes.c_size_t]
    libc.calloc.restype = ctypes.c_void_p
    libc.strdup.argtypes = [ctypes.c_char_p]
    libc.strdup.restype = ctypes.c_void_p
    def converse(count, messages, responses, data):
        if not 0 < count <= 8:
            return 19
        # Only a password echo-off request is supported. Reject other interactive
        # challenges rather than treating them as authentication success.
        styles = [messages[i].contents.style for i in range(count)]
        if any(style not in (1, 3, 4) for style in styles):
            return 19
        array = ctypes.cast(libc.calloc(count, ctypes.sizeof(Response)), ctypes.POINTER(Response))
        if not array:
            return 5
        for i, style in enumerate(styles):
            if style == 1:
                array[i].resp = libc.strdup(password.encode())
            array[i].code = 0
        responses[0] = array  # PAM owns and frees the responses.
        return 0
    callback = callback_type(converse)
    conversation = Conversation(callback, None)
    library.pam_start.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.POINTER(Conversation), ctypes.POINTER(ctypes.c_void_p)]
    library.pam_start.restype = ctypes.c_int
    for name in ('pam_authenticate', 'pam_acct_mgmt', 'pam_end'):
        getattr(library, name).argtypes = [ctypes.c_void_p, ctypes.c_int]
        getattr(library, name).restype = ctypes.c_int
    handle = ctypes.c_void_p()
    started = library.pam_start(b'hvo-poc', login.encode(), ctypes.byref(conversation), ctypes.byref(handle))
    if started != 0:
        return False
    outcome = 1
    try:
        outcome = library.pam_authenticate(handle, 0)
        if outcome != 0:
            return False
        outcome = library.pam_acct_mgmt(handle, 0)
        return outcome == 0
    finally:
        library.pam_end(handle, outcome)


def install():
    if os.geteuid() != 0 or not Path('/.dockerenv').is_file() or os.environ.get('HVO_DISPOSABLE_AUTH_FIXTURE') != '1':
        raise RuntimeError('Installation is allowed only inside the disposable fixture container')
    if PREFIX.exists() or STATE.exists():
        raise RuntimeError('Refusing to replace an existing installation')
    PREFIX.mkdir(mode=0o755)
    STATE.mkdir(mode=0o700)
    for name in ('fixture.py', 'auth_model.py'):
        shutil.copyfile(PACKAGE/name, PREFIX/name)
        (PREFIX/name).chmod(0o644)
    for executable, mode in [('local-helper', 'helper'), ('local-client', 'client')]:
        (PREFIX/executable).write_text('#!/bin/sh\nexec /usr/bin/python3 -I '+str(PREFIX/'fixture.py')+' '+mode+'\n')
        (PREFIX/executable).chmod(0o755)
    for name, uid in UIDS.items():
        subprocess.run(['useradd', '-U', '-M', '-u', str(uid), '-s', '/usr/sbin/nologin', name], check=True, capture_output=True)
    # The only credentials provisioned are public test fixtures inside this
    # throwaway container. Values travel over stdin, not arguments or logs.
    subprocess.run(['chpasswd'], input=''.join(name+':'+FIXTURE_PASSWORD+'\n' for name in UIDS), text=True, check=True, capture_output=True)
    subprocess.run(['chage', '-E', '0', 'fixture-expired'], check=True, capture_output=True)
    policy = Path('/etc/sudoers.d/hvo-auth-fixture')
    policy.write_text('fixture-admin,fixture-limited ALL=(root) NOPASSWD: '+str(PREFIX/'local-helper')+'\n')
    policy.chmod(0o440)
    subprocess.run(['visudo', '-cf', str(policy)], check=True, capture_output=True)
    Path('/etc/pam.d/hvo-poc').write_text('auth required pam_unix.so nodelay\naccount required pam_unix.so\n')
    manifest = {'version': 1, 'scope': 'disposable-integrated-prototype', 'prefix': str(PREFIX),
                'users': UIDS, 'policy': 'sudo fixed-helper command only',
                'production_installer': False, 'production_website': False}
    (STATE/'install.json').write_text(json.dumps(manifest, indent=2))
    return manifest


def integrated_test():
    manifest = install()
    sys.path.insert(0, str(PREFIX))
    from auth_model import Denied, Model, Peer, Principal, Role, KNOWN_OPERATIONS
    class SudoAuthority:
        def __init__(self):
            self.current = None
        def check(self, peer, operation, now):
            if self.current != (peer, operation):
                raise Denied('LinuxAdminPermissionDenied')
    class LinuxProvider:
        def authenticate_and_check_account(self, login, password):
            if login not in UIDS or not pam_authenticate(login, password):
                raise Denied('SignInFailed', 401)
            return 'uid:'+str(pwd.getpwnam(login).pw_uid)
    class VirtualProvider:
        def __init__(self):
            self.salt = b'public-disposable-fixture-salt'
            self.expected = hashlib.pbkdf2_hmac('sha256', FIXTURE_PASSWORD.encode(), self.salt, 10000)
        def authenticate_and_check_account(self, login, password):
            actual = hashlib.pbkdf2_hmac('sha256', password.encode(), self.salt, 10000)
            if login != 'fixture-admin' or not hmac.compare_digest(actual, self.expected):
                raise Denied('SignInFailed', 401)
            return 'virtual-fixture-admin'
    principals = [Principal('linux:'+name, 'linux', 'uid:'+str(uid),
                  Role.ADMIN if name == 'fixture-admin' else Role.VIEWER) for name, uid in UIDS.items()]
    principals += [Principal('virtual:fixture-admin', 'virtual', 'virtual-fixture-admin', Role.VIEWER)]
    authority = SudoAuthority()
    model = Model(principals, {uid:'uid:'+str(uid) for uid in UIDS.values()}, authority,
                  {'linux': LinuxProvider(), 'virtual': VirtualProvider()})
    lock = threading.Lock()
    evidence = {'native_sudo_peer_uids': [], 'native_web_provider_checks': True}
    class Broker(socketserver.StreamRequestHandler):
        def handle(self):
            pid, uid, gid = struct.unpack('iII', self.request.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
            try:
                if uid != 0:
                    raise Denied('RootHelperPeerRequired')
                request = json.loads(self.rfile.readline(1025))
                if set(request) != {'operation', 'sudo_uid'} or type(request['sudo_uid']) is not int:
                    raise Denied('InvalidRequest', 400)
                actor = request['sudo_uid']
                if actor not in (UIDS['fixture-admin'], UIDS['fixture-limited']):
                    raise Denied('LinuxAdminPermissionDenied')
                operation = request['operation']
                if operation not in KNOWN_OPERATIONS:
                    raise Denied('UnknownOperation', 400)
                peer = Peer(actor, pid, 'kernel-helper-connection:'+str(pid))
                with lock:
                    model.now = int(time.time())
                    authority.current = (peer, operation)
                    try:
                        handle = model.local_open(peer, operation)
                        principal = model.check(handle, 'local', operation, peer)
                        model.revoke(handle)
                    finally:
                        authority.current = None
                evidence['native_sudo_peer_uids'].append(uid)
                answer = {'ok': True, 'principal': principal, 'actor_uid': actor}
            except Denied as error:
                answer = {'ok': False, 'error': error.code}
            except (ValueError, KeyError, TypeError):
                answer = {'ok': False, 'error': 'InvalidRequest'}
            self.wfile.write(json.dumps(answer).encode()+b'\n')
    broker = socketserver.UnixStreamServer(str(SOCKET), Broker)
    SOCKET.chmod(0o600)
    broker_thread = threading.Thread(target=broker.serve_forever, daemon=True)
    broker_thread.start()
    class Web(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass
        def send(self, status, body):
            raw = json.dumps(body).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
        def do_GET(self):
            self.do_POST()
        def do_POST(self):
            try:
                size = int(self.headers.get('Content-Length', '0'))
                if not 0 <= size <= 2048:
                    raise Denied('InvalidRequest', 400)
                body = json.loads(self.rfile.read(size)) if size else {}
                token = self.headers.get('Authorization', '').removeprefix('Bearer ')
                audience = 'api' if self.path.startswith('/api-key/') else 'web'
                with lock:
                    model.now = int(time.time())
                    if self.path == '/login':
                        handle = model.web_login(body['provider'], body['name'], body['password'])
                        if body.get('short_fixture_session') is True:
                            model.credentials[handle] = replace(model.credentials[handle], expires=model.now+1)
                        result = {'handle': handle, 'owner': model.credentials[handle].owner}
                    elif self.path in ('/status','/settings','/api-key/status','/api-key/settings'):
                        operation = 'settings:write' if self.path.endswith('settings') else 'status:read'
                        result = {'principal': model.check(token, audience, operation)}
                    elif self.path == '/keys':
                        result = {'handle': model.issue_mock_key(token, 'web', body['scopes'])}
                    elif self.path == '/revoke-key':
                        owner = model.check(token, 'web', 'status:read')
                        target = model.credentials.get(body['handle'])
                        if target is None or target.owner != owner or target.audience != 'api':
                            raise Denied('PermissionDenied')
                        model.revoke(body['handle'])
                        result = {'revoked': True}
                    elif self.path == '/logout':
                        model.check(token, 'web', 'status:read')
                        model.revoke(token)
                        result = {'signed_out': True}
                    else:
                        raise Denied('UnknownOperation', 404)
                self.send(200, result)
            except Denied as error:
                self.send(error.status, {'error': error.code})
            except (ValueError, KeyError, TypeError):
                self.send(400, {'error': 'InvalidRequest'})
    server = ThreadingHTTPServer(('127.0.0.1', 0), Web)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    origin = 'http://127.0.0.1:'+str(server.server_address[1])
    cases=[]
    def record(name, action):
        try:
            action()
            cases.append({'test': name, 'passed': True})
        except Exception as error:
            cases.append({'test': name, 'passed': False, 'error_type': type(error).__name__})
    def equal(actual, expected):
        if actual != expected:
            raise AssertionError('Unexpected fixture result')
    def local(user, operation='status:read', extras=None, env=None, direct=False):
        uid=UIDS[user]
        argv=['setpriv','--reuid='+str(uid),'--regid='+str(uid),'--init-groups',str(PREFIX/('local-helper' if direct else 'local-client'))]
        request={'operation':operation}
        request.update(extras or {})
        environment=os.environ.copy()
        environment.update(env or {})
        p=subprocess.run(argv,input=json.dumps(request),text=True,capture_output=True,env=environment,timeout=8)
        return p.returncode,json.loads(p.stdout) if p.stdout else {'error':'NoResponse'}
    def http(path, body=None, token=None):
        headers={'Content-Type':'application/json'}
        if token: headers['Authorization']='Bearer '+token
        req=urllib.request.Request(origin+path,data=json.dumps(body or {}).encode(),headers=headers)
        try:
            with urllib.request.urlopen(req,timeout=8) as response:
                return response.status,json.load(response)
        except urllib.error.HTTPError as error:
            return error.code,json.load(error)
    def login(provider='linux',name='fixture-admin',password=FIXTURE_PASSWORD,short=False):
        return http('/login',{'provider':provider,'name':name,'password':password,'short_fixture_session':short})
    try:
        record('installed_package_and_private_policy', lambda: (equal((STATE/'install.json').is_file(),True), equal(SOCKET.stat().st_mode & 0o777,0o600)))
        record('authorized_sudo_linux_admin', lambda: equal(local('fixture-admin')[1]['principal'],'linux:fixture-admin'))
        record('nonadmin_cannot_use_sudo_helper', lambda: equal(local('fixture-user')[1]['error'],'LinuxAdminPermissionDenied'))
        record('direct_unprivileged_helper_rejected', lambda: equal(local('fixture-user',direct=True)[1]['error'],'PrivilegedHelperRequired'))
        record('sudo_authorized_viewer_still_denied_admin_role', lambda: equal(local('fixture-limited','settings:write')[1]['error'],'ControllerRoleDenied'))
        record('forged_sudo_environment_cannot_promote_nonadmin', lambda: equal(local('fixture-user',env={'SUDO_UID':str(UIDS['fixture-admin']),'SUDO_USER':'fixture-admin'})[1]['error'],'LinuxAdminPermissionDenied'))
        record('sudo_overwrites_forged_origin_for_authorized_caller', lambda: equal(local('fixture-limited',env={'SUDO_UID':str(UIDS['fixture-admin']),'SUDO_USER':'fixture-admin'})[1]['actor_uid'],UIDS['fixture-limited']))
        record('request_username_or_uid_claim_rejected', lambda: equal(local('fixture-admin',extras={'uid':0})[1]['error'],'IdentityClaimsNotAccepted'))
        record('wrong_linux_password_denied_by_pam', lambda: equal(login(password='wrong-inert-fixture')[0],401))
        record('expired_linux_account_denied_by_pam', lambda: equal(login(name='fixture-expired')[0],401))
        linux=login()[1]['handle']
        virtual=login('virtual')[1]['handle']
        limited=login(name='fixture-user')[1]['handle']
        record('linux_web_login_maps_real_uid', lambda: equal(http('/status',token=linux),(200,{'principal':'linux:fixture-admin'})))
        record('virtual_web_login_has_separate_identity', lambda: equal(http('/status',token=virtual),(200,{'principal':'virtual:fixture-admin'})))
        record('linux_nonadmin_can_login_web_with_viewer_role', lambda: equal(http('/status',token=limited)[0],200))
        record('linux_web_viewer_role_denied', lambda: equal(http('/settings',token=limited),(403,{'error':'ControllerRoleDenied'})))
        record('virtual_web_viewer_role_denied', lambda: equal(http('/settings',token=virtual),(403,{'error':'ControllerRoleDenied'})))
        record('linux_web_admin_role_allowed', lambda: equal(http('/settings',token=linux)[0],200))
        record('accepted_web_session_reused', lambda: equal(http('/status',token=linux)[0],200))
        record('saved_web_session_cannot_override_local_gate', lambda: equal(local('fixture-user',extras={'session':linux})[1]['error'],'LinuxAdminPermissionDenied'))
        record('local_protocol_rejects_saved_session_even_for_admin', lambda: equal(local('fixture-admin',extras={'session':linux})[1]['error'],'IdentityClaimsNotAccepted'))
        for provider,owner in [('linux',linux),('virtual',virtual)]:
            key=http('/keys',{'scopes':['status:read']},owner)[1]['handle']
            record(provider+'_owned_scoped_key_works', lambda k=key: equal(http('/api-key/status',token=k)[0],200))
            record(provider+'_key_cannot_write_settings', lambda k=key: equal(http('/api-key/settings',token=k)[0],403))
            record(provider+'_owner_revokes_key', lambda k=key,o=owner: equal(http('/revoke-key',{'handle':k},o)[0],200))
            record(provider+'_revoked_key_denied', lambda k=key: equal(http('/api-key/status',token=k)[0],401))
        record('viewer_cannot_mint_broader_key', lambda: equal(http('/keys',{'scopes':['settings:write']},virtual)[0],403))
        short=login(short=True)[1]['handle']
        time.sleep(1.2)
        record('real_elapsed_session_expiry', lambda: equal(http('/status',token=short)[0],401))
        record('logout_invalidates_saved_session', lambda: (equal(http('/logout',token=virtual)[0],200),equal(http('/status',token=virtual)[0],401)))
        record('role_change_invalidates_session', lambda: (model.change_role('linux:fixture-admin',Role.VIEWER),equal(http('/status',token=linux)[0],401)))
    finally:
        server.shutdown();server.server_close()
        broker.shutdown();broker.server_close()
        server_thread.join(timeout=3);broker_thread.join(timeout=3)
    result={'scope':'disposable integrated prototype install, not production full install',
            'install':manifest,'passed':sum(c['passed'] for c in cases),'failed':sum(not c['passed'] for c in cases),
            'tests':cases,'evidence':evidence,
            'real':['container Linux accounts','fixed-command sudo policy and original UID','kernel Unix peer credentials','PAM pam_unix password/account validation','loopback HTTP requests','virtual password hashing','wall-clock session expiry'],
            'prototype':['application roles/scopes/session model','fixture HTTP endpoints, not HVO web UI','predictable inert process-local credential handles'],
            'not_covered':['production installer auth adapter integration','production website Linux-provider wiring','polkit route','host/container deployment boundary','graphical kiosk UI'],
            'secrets':'only public disposable test fixtures; no host credentials used'}
    print(json.dumps(result,indent=2))
    return 0 if result['failed']==0 else 1


if __name__=='__main__':
    mode=sys.argv[1] if len(sys.argv)>1 else 'test'
    if mode=='helper': sys.exit(helper())
    if mode=='client': sys.exit(local_client())
    sys.exit(integrated_test())
