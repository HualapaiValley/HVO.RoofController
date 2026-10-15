#!/usr/bin/env python3
"""Build and verify native authentication only in a disposable, constrained Linux container."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

REPO = Path(__file__).resolve().parents[2]
NAME = 'hvo-native-auth-worker-test'
IMAGE = 'hvo-linux-auth-native-test:local'
LABEL = 'hvo.native-auth-fixture'
DOCKER = ['sudo', '-n', 'docker']


def invoke(args, **kwargs):
    return subprocess.run(args, text=True, capture_output=True, **kwargs)


def checked(args, **kwargs):
    result = invoke(args, **kwargs)
    if result.returncode:
        # Build output contains no fixture credentials, which exist only at container runtime.
        sys.stderr.write(result.stdout + result.stderr)
        raise RuntimeError('Build/setup command failed')
    return result


def inspect(kind, name):
    result = invoke(DOCKER + [kind, 'inspect', name])
    return json.loads(result.stdout)[0] if result.returncode == 0 else None


def host_state():
    return {
        path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
        for path in ['/etc/passwd', '/etc/group']
    } | {path: Path(path).exists() for path in ['/etc/pam.d/hvo-roof-linux-auth', '/etc/sudoers.d/hvo-native-fixture']}


def main():
    if sys.platform != 'linux' or Path('/.dockerenv').exists():
        raise SystemExit('Run this orchestrator in the selected Linux development VM')
    checked(DOCKER + ['version', '--format', '{{.Server.Version}}'])
    if inspect('container', NAME):
        raise SystemExit('Refusing a pre-existing named container')
    previous = inspect('image', IMAGE)
    if previous and previous['Config'].get('Labels', {}).get(LABEL) != 'true':
        raise SystemExit('Refusing a pre-existing unlabelled image')
    before = host_state()
    report = {'nativeComponentOnly': True, 'productionApplication': False}
    try:
        project = 'HVO.RoofControllerV4.LinuxAuth/HVO.RoofControllerV4.LinuxAuth.csproj'
        checked(['dotnet', 'restore', project, '--source', str(Path.home() / '.nuget/packages'),
                 '-p:NuGetAudit=false', '-m:1'], cwd=REPO / 'src')
        checked(['dotnet', 'build', project, '-c', 'Release', '--no-restore', '-p:PublishReadyToRun=false', '-m:1'], cwd=REPO / 'src')
        with tempfile.TemporaryDirectory(prefix='hvo-native-worker-build-') as temporary:
            context = Path(temporary)
            shutil.copytree(REPO / 'src/HVO.RoofControllerV4.LinuxAuth/bin/Release/net10.0', context / 'worker')
            for name in ['Dockerfile', 'native_worker_fixture.py']:
                shutil.copy2(REPO / 'tests/linux-auth' / name, context / name)
            for name in ['hvo-roof-local-auth', 'hvo-roof-linux-auth.pam']:
                shutil.copy2(REPO / 'deploy/linux-auth' / name, context / name)
            checked(DOCKER + ['build', '-t', IMAGE, str(context)], timeout=300)
        command = DOCKER + ['create', '--name', NAME, '--label', LABEL + '=true', '--network', 'none',
                           '--pids-limit', '128', '--memory', '768m', '--cpus', '2', '--cap-drop', 'ALL']
        for capability in ['CHOWN', 'DAC_OVERRIDE', 'SETUID', 'SETGID', 'FOWNER', 'AUDIT_WRITE']:
            command += ['--cap-add', capability]
        command += ['--tmpfs', '/run:rw,nosuid,nodev,size=32m', '--tmpfs', '/tmp:rw,nosuid,nodev,size=32m', IMAGE]
        checked(command)
        container = inspect('container', NAME)
        host = container['HostConfig']
        if host['NetworkMode'] != 'none' or host['Privileged'] or host['Binds'] or host['PortBindings'] or host['Devices']:
            raise RuntimeError('Fixture isolation verification failed')
        result = invoke(DOCKER + ['start', '--attach', NAME], timeout=180)
        completed = inspect('container', NAME)
        report['exitCode'] = completed['State']['ExitCode']
        report['fixture'] = json.loads(result.stdout)
        if result.stderr:
            # Fixture failures contain assertion names, never input values or assertion tokens.
            report['error'] = result.stderr
    finally:
        container = inspect('container', NAME)
        if container:
            if container['Config'].get('Labels', {}).get(LABEL) != 'true':
                raise RuntimeError('Refusing to remove an unrelated container')
            checked(DOCKER + ['rm', '--force', '--volumes', NAME])
        built = inspect('image', IMAGE)
        if built and built['Config'].get('Labels', {}).get(LABEL) == 'true':
            checked(DOCKER + ['image', 'rm', IMAGE])
        report['containerRemoved'] = inspect('container', NAME) is None
        report['imageRemoved'] = inspect('image', IMAGE) is None
        report['hostAccountsAndFixturePoliciesUnchanged'] = before == host_state()
        print(json.dumps(report, indent=2))
    return 0 if report.get('exitCode') == 0 and report['hostAccountsAndFixturePoliciesUnchanged'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
