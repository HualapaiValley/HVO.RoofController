#!/usr/bin/env python3
"""Exact final terminal files, without credentials, controller, install/apply or host commands.

Runs offline help/version/notices/nonterminal refusal, then bounded PTYs at 80x24 and
120x32 in all colour/border modes, navigating all roof pages and a private prompt plus installer Back/cancel. All HOME/XDG/cache/temp/extraction paths are private. An empty PATH disables survey
subprocesses; a clean non-root host with no Roof installation is required. Broader page and
prompt coverage uses Terminal.Gui/fake-machine tests; CLI/emulator tmux smoke stays separate.
"""
import argparse
import errno
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'build'))
from terminal_payload import check, RIDS, ROOT

ESCAPES = re.compile(r'\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[()][A-Za-z0-9]|\x1b[=>78]')
TIMEOUT = 45


def require(condition, message):
    if not condition: raise RuntimeError(message)


def pty_start(program, args, env, cwd, columns, rows, installer, output, workflow=False):
    # Set the size before the child starts; waitpid is always nonblocking and bounded.
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', rows, columns, 0, 0))
    pid=os.fork()
    if pid==0:
        os.close(master); os.setsid(); fcntl.ioctl(slave,termios.TIOCSCTTY,0)
        for fd in (0,1,2): os.dup2(slave,fd)
        if slave>2: os.close(slave)
        os.chdir(cwd); os.execve(program,[program,*args],env)
    os.close(slave)
    captured=bytearray(); deadline=time.monotonic()+TIMEOUT; sent=False; status=None
    steps = ([(b'\r', ('What to install',)), (b'\x1b', ('This machine',))] if installer else
             [(b'\x1b[11~', ('ROOF STATE',)), (b'\x1b[12~', ('SETTING / VALUE',)),
              (b'\x1b[13~', ('PEOPLE / ACCOUNTS',)), (b'\x1b[14~', ('CONTROLLER / HEALTH',)),
              (b'\x1b[15~', ('CONNECTION / TRUST',)), (b'\t\r', ('Save and check',)),
              (b'\x1b[20~', ('Stop failed:',)), (b'\x1b', ('CONNECTION / TRUST',))]) if workflow else []
    step = -1; since = 0
    try:
        while time.monotonic()<deadline:
            ready,_,_=select.select([master],[],[],0.1)
            if ready:
                try: chunk=os.read(master,65536)
                except OSError as e:
                    if e.errno!=errno.EIO: raise
                    chunk=b''
                captured.extend(chunk)
            require(len(captured)<4*1024*1024,'PTY output exceeded bound')
            screen=ESCAPES.sub('',captured.decode('utf-8','replace'))
            expected=('HVO Roof installer','This machine','F10') if installer else ('HVO roof','Setup','F9')
            recent = ESCAPES.sub('', captured[since:].decode('utf-8', 'replace'))
            ready_for_step = not sent and (all(t in screen for t in expected) if step < 0 else all(t in recent for t in steps[step][1]))
            if not sent and ready_for_step:
                step += 1
                if step < len(steps):
                    since = len(captured)
                    os.write(master, steps[step][0])
                else:
                    # F10 is xterm's ESC[21~. Esc goes Back in Roof's installer.
                    os.write(master,b'\x1b[21~'); sent=True
            done,status_now=os.waitpid(pid,os.WNOHANG)
            if done:
                status=status_now; break
        require(status is not None,f'PTY child timed out at workflow step {step} (including after F10)')
        code=os.waitstatus_to_exitcode(status)
        require(sent and code==(130 if installer else 9 if workflow else 0),f'PTY startup/F10 at {columns}x{rows}: exit {code}; output={screen[-1500:]}')
    finally:
        if status is None:
            os.killpg(pid,signal.SIGKILL); os.waitpid(pid,0)
        os.close(master)
        output.write_bytes(captured)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dist',type=Path); parser.add_argument('--rid',required=True,choices=RIDS)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args(); args.output.mkdir(parents=True,exist_ok=True)
    require(os.geteuid()!=0,'PTY smoke requires a non-root disposable user')
    require(not Path('/etc/hvo-roof').exists() and not Path('/usr/local/bin/hvo-roof').exists(),
            'PTY smoke refuses an installed Roof host')
    notice=(ROOT/'src/HVO.RoofControllerV4.TerminalUi/Notices/THIRD-PARTY-NOTICES.txt').read_bytes()
    records=[]
    for name in ['hvo-roof-install','hvo-roof']:
        program=str((args.dist/f'{name}-{args.rid}').resolve()); installer=name.endswith('install')
        payload=check(program,args.rid)
        with tempfile.TemporaryDirectory(prefix='hvo-terminal-') as tmp:
            home=Path(tmp); home.chmod(0o700)
            for folder in ['config','cache','state','data','tmp','extract','empty-path','work']:(home/folder).mkdir(mode=0o700)
            # Allowlist: no credentials, inherited Docker context, DOTNET_STARTUP_HOOKS or user config.
            env=dict(HOME=tmp,XDG_CONFIG_HOME=str(home/'config'),XDG_CACHE_HOME=str(home/'cache'),
                     XDG_STATE_HOME=str(home/'state'),XDG_DATA_HOME=str(home/'data'),TMPDIR=str(home/'tmp'),
                     DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(home/'extract'),PATH=str(home/'empty-path'),
                     TERM='xterm-256color',COLORTERM='truecolor',LANG='C.UTF-8',LC_ALL='C.UTF-8',
                     DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1')
            def run(label,argv,code,contains=None,exact=None):
                result=subprocess.run([program,*argv],env=env,cwd=home/'work',stdin=subprocess.DEVNULL,
                                      capture_output=True,timeout=TIMEOUT)
                (args.output/f'{name}-{label}.txt').write_bytes(result.stdout+result.stderr)
                require(result.returncode==code,f'{name} {label}: exit {result.returncode}: {result.stderr[:300]}')
                if contains:require(contains.encode() in result.stdout+result.stderr,f'{name} {label}: missing expected text')
                if exact is not None:require(result.stdout==exact and not result.stderr,f'{name} {label}: embedded notices differ')
                return result
            run('help',['--help'],0,'--third-party-notices')
            version = run('version',['--version'],0)
            require(re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+[^\s]*\s*', version.stdout.decode('utf-8')) is not None, 'invalid version output')
            run('notices',['--third-party-notices'],0,exact=notice)
            # The option terminates even before a command that would otherwise mutate or authorize.
            run('notices-before-command',['uninstall' if installer else 'open','--third-party-notices'],0,exact=notice)
            run('no-terminal',[] if installer else ['ui'],2,'terminal')
            modes = ['truecolor,unicode', 'ansi16,unicode', 'nocolor,unicode', 'ansi16,ascii']
            for width,height in [(80,24),(120,32)]:
                for mode in modes:
                    mode_env = dict(env, HVO_TERMINAL=mode)
                    capture = args.output/f'{name}-{width}x{height}-{mode.replace(",", "-")}.ans'
                    pty_start(program,[] if installer else ['ui'],mode_env,home/'work',width,height,installer,capture, workflow=True)
                    drawn = capture.read_bytes()
                    if mode.startswith('ansi16'):
                        require(re.search(rb'\x1b\[[0-9;]*(?:38|48);(?:2|5);', drawn) is None, 'ANSI16 emitted extended colour escapes')
                    if mode.startswith('nocolor'):
                        sgr = re.findall(rb'\x1b\[([0-9;]*)m', drawn)
                        require(not any(int(part) in {*range(30, 38), *range(40, 48), *range(90, 98), *range(100, 108), 38, 48}
                                        for codes in sgr for part in codes.split(b';') if part), 'NO_COLOR emitted explicit colours')
                    if mode.endswith('ascii'):
                        plain = ESCAPES.sub('', drawn.decode('utf-8', 'replace'))
                        require(not any(char in plain for char in '┌─│╭╰⟦⟧☑☐◉○►◄'), 'ASCII mode emitted Unicode controls')
                        require('+--' in plain and '[ ' in plain, 'ASCII mode omitted frame/action glyphs')
            files=[p.relative_to(home).as_posix() for p in home.rglob('*') if p.is_file()]
            require(not any('onigwrap' in f.lower() for f in files),'onigwrap extracted')
            require(not any(f.startswith(('config/','data/','extract/','work/')) for f in files),
                    'smoke wrote configuration, install record, answers or extracted payload: '+str(files))
            # The installer deliberately writes its cancellation log. Nothing else is allowed.
            allowed={'state/hvo-roof/install.log','Library/Logs/hvo-roof-install.log'} if installer else set()
            require(set(files)<=allowed,'unexpected private files: '+str(files))
            records.append(dict(program=name,rid=args.rid,sha256=payload['sha256'],private_files=files,
                                notices_sha256=hashlib.sha256(notice).hexdigest(),pty_sizes=[[80,24],[120,32]], modes=modes, workflow="roof pages/private prompt/F9 or installer roles/Back/cancel"))
    (args.output/'runtime-summary.json').write_text(json.dumps(records,indent=2)+'\n')
    print('PASS final installer and CLI: offline help/version/notices, no-TTY, all-mode PTY workflows at both sizes; no install/answers/extraction')


def terminate(signum, _frame):
    # Let pty_start's finally reap its app when an outer bounded timeout sends TERM.
    raise SystemExit(128 + signum)


if __name__=='__main__':
    signal.signal(signal.SIGTERM, terminate)
    main()
