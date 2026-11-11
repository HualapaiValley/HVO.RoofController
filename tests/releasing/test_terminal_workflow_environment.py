"""Driver subprocesses use the reviewed inert PATH; non-render contracts retain their environment."""
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('terminal_workflows', Path(__file__).with_name('check-terminal-workflows.py'))
workflows = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflows)


def selected_phases(program, class_name, category='Unit', namespace='Installer'):
    """Evaluate the VSTest predicates against a representative method, including class boundaries."""
    fields = dict(FullyQualifiedName=f'HVO.RoofControllerV4.RPi.Tests.{namespace}.{class_name}.Method',
                  TestCategory=category)

    def predicate(match):
        field, operator, value = match.groups()
        actual = fields[field]
        result = value in actual if operator.endswith('~') else actual == value
        return str(not result if operator.startswith('!') else result)

    phases = []
    for phase, test_filter in workflows.phase_filters(program):
        expression = re.sub(r'(FullyQualifiedName|TestCategory)(!~|!=|~|=)([^&|()]+)', predicate, test_filter)
        expression = expression.replace('&', ' and ').replace('|', ' or ')
        if eval(expression, {'__builtins__': {}}, {}):
            phases.append(phase)
    return phases


class WorkflowEnvironment(unittest.TestCase):
    def test_workflow_budget_reserves_setup_and_upload_and_fits_the_stress_case(self):
        source = (Path(__file__).parents[2] / '.github/workflows/ci.yml').read_text()
        job = source.split('  terminal-distributions:\n', 1)[1].split('\n  installer-kiosk:', 1)[0]
        step = job.split('      - name: Exercise final distributed terminal workflows\n', 1)[1].split('      - name:', 1)[0]
        job_minutes = int(re.search(r'^    timeout-minutes: (\d+)$', job, re.M).group(1))
        step_minutes = int(re.search(r'^        timeout-minutes: (\d+)$', step, re.M).group(1))
        self.assertLessEqual(step_minutes, job_minutes - 4 - 2,
                             'Leave four minutes for preparation and two for evidence upload/cleanup')
        # Budget stress only: both passing baselines remain evidence, not a diagnosed slowdown.
        planning_seconds = 316 + 376.11 * 2.4 + 67 + 15 + 30
        self.assertGreaterEqual(step_minutes * 60, planning_seconds,
                                'The aggregate limit must admit the documented installer stress case')
        upload = job.split('      - name: Upload exact-file runtime evidence\n', 1)[1]
        self.assertIn('        if: always()\n', upload)

    def test_render_subprocess_starts_absolute_host_with_only_inert_bash(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            environment = workflows.phase_environment(dict(os.environ), home, render=True)
            host = home / 'absolute test host'
            host.write_text('#!' + sys.executable + '\n' + textwrap.dedent('''\
                import json, os
                from pathlib import Path
                import shutil, subprocess, sys
                output = Path(next(a.split(':', 1)[1] for a in sys.argv if a.startswith('/ResultsDirectory:')))
                result = subprocess.run(['bash', '-c', 'printf unsafe > unexpected-file'],
                                        cwd=os.environ['HOME'], capture_output=True, timeout=5)
                print(json.dumps(dict(code=result.returncode, silent=not result.stdout and not result.stderr,
                                      wrote=Path(os.environ['HOME'], 'unexpected-file').exists(),
                                      entries=sorted(p.name for p in Path(os.environ['PATH']).iterdir()),
                                      env_available=shutil.which('env') is not None,
                                      clipboard_available=shutil.which('pbcopy') is not None)))
                (output / 'final-terminal-workflows.trx').write_text(
                    '<TestRun><Results><UnitTestResult testName="ControlledDriver" outcome="Passed"/></Results></TestRun>')
                '''))
            host.chmod(0o700)
            output = home / 'proof'
            passed = workflows.run_phase(host, home, environment, 'ControlledDriver', output)
            self.assertEqual(['ControlledDriver'], passed)
            self.assertEqual(dict(code=127, silent=True, wrote=False, entries=['bash'],
                                  env_available=False, clipboard_available=False),
                             json.loads((output / 'workflow-run.txt').read_text()))
            self.assertEqual(str(home), environment['HOME'])
            self.assertEqual(str(home / 'tmp'), environment['TMPDIR'])

    def test_non_render_contracts_keep_the_established_environment(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = dict(PATH=os.environ['PATH'], HOME='contract-fixture', DOTNET_ReadyToRun='0')
            environment = workflows.phase_environment(base, Path(tmp), render=False)
            self.assertEqual(base, environment)
            self.assertEqual([], list(Path(tmp).iterdir()))

    def test_relative_dotnet_host_is_rejected_before_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError, 'absolute'):
                workflows.run_phase('dotnet', Path(tmp), {}, 'ControlledDriver', Path(tmp) / 'proof')

    def test_installer_catalogue_is_partitioned_once_without_admitting_host_installs(self):
        folder = Path(__file__).parents[1] / 'HVO.RoofControllerV4.RPi.Tests/Installer'
        checked = set()
        for source in folder.glob('*.cs'):
            text = source.read_text()
            if not re.search(r'\[TestClass\b', text):
                continue
            for name in re.findall(r'public (?:sealed |partial )*class (\w+)', text):
                checked.add(name)
                with self.subTest(test_class=name):
                    phases = selected_phases('hvo-roof-install', name)
                    expected = 0 if name in ['InstallerKioskSystemTests', 'InstallerMacSystemTests'] else 1
                    self.assertEqual(expected, len(phases), phases)
                    if name in workflows.RENDER_CLASSES['hvo-roof-install']:
                        self.assertEqual(['render'], phases)
                    for category in ['Scenario', 'Browser', 'Soak']:
                        self.assertEqual([], selected_phases('hvo-roof-install', name, category))
        self.assertTrue({'InstallerBootstrapTests', 'InstallerKioskCommissioningTests'} <= checked,
                        'Combined TestClass/platform attributes must be included in the guard')

    def test_large_contracts_have_separate_budgets_and_future_contracts_keep_the_remainder(self):
        for name, expected in [('InstallerBackupTests', 'contracts-backup'),
                               ('InstallerLifecycleTests', 'contracts-lifecycle'),
                               ('InstallerStateContractTests', 'contracts-state'),
                               ('FutureInstallerTests', 'contracts'),
                               ('InstallerBackupTestsExtra', 'contracts')]:
            with self.subTest(test_class=name):
                self.assertEqual([expected], selected_phases('hvo-roof-install', name))
        self.assertEqual([], selected_phases('hvo-roof-install', 'InstallerBackupTests', namespace='Other'))
        self.assertEqual(['contracts'], selected_phases('hvo-roof', 'RoofCliSafetyTests', namespace='Cli'))
        self.assertEqual(['render'], selected_phases('hvo-roof', 'RoofTerminalUiTests', namespace='Cli'))

    def test_timeout_keeps_test_progress_and_terminates_the_testhost_too(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            host = home / 'host'
            host.write_text('#!' + sys.executable + '\n' + textwrap.dedent('''\
                import json, os, subprocess, sys, time
                from pathlib import Path
                output = Path(next(a.split(':', 1)[1] for a in sys.argv if a.startswith('/ResultsDirectory:')))
                time.sleep(2.5)  # Deliberately longer than the reported two-second command budget.
                child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
                (output / 'processes.json').write_text(json.dumps([os.getpid(), child.pid]))
                print('Passed earlier contract [123 ms]', flush=True)
                print('diagnostic on stderr', file=sys.stderr, flush=True)
                (output / 'ready').touch()
                time.sleep(60)
                '''))
            host.chmod(0o700)
            output = home / 'proof'
            real_wait = subprocess.Popen.wait

            def expire_after_ready(process, timeout=None):
                if timeout == 2:
                    # Fixture startup has its own generous bound. Force the real wait timeout
                    # only after both processes and their flushed diagnostic output exist.
                    deadline = time.monotonic() + 30
                    ready = output / 'ready'
                    while not ready.exists() and process.poll() is None and time.monotonic() < deadline:
                        time.sleep(.01)
                    self.assertTrue(ready.exists(), 'The controlled host did not become ready in 30 seconds')
                    return real_wait(process, timeout=0)
                return real_wait(process, timeout=timeout)

            try:
                with patch.object(workflows.subprocess.Popen, 'wait', expire_after_ready):
                    with self.assertRaisesRegex(RuntimeError, 'exceeded 2 seconds; see workflow-run.txt'):
                        workflows.run_phase(host, home, dict(os.environ), 'ControlledDriver', output, timeout=2)
                log = (output / 'workflow-run.txt').read_text()
                self.assertIn('Passed earlier contract [123 ms]', log)
                self.assertIn('diagnostic on stderr', log)
                parent, child = json.loads((output / 'processes.json').read_text())
                # An orphan can briefly remain a zombie until init reaps it; it must not execute.
                for _ in range(100):
                    status = subprocess.run(['ps', '-o', 'stat=', '-p', str(child)],
                                            capture_output=True, text=True, check=False).stdout.strip()
                    if not status or status.startswith('Z'):
                        break
                    time.sleep(.01)
                self.assertTrue(not status or status.startswith('Z'), status)
                with self.assertRaises(ProcessLookupError):
                    os.kill(parent, 0)
            finally:
                processes = output / 'processes.json'
                if processes.exists():
                    try:
                        os.killpg(json.loads(processes.read_text())[0], signal.SIGKILL)
                    except ProcessLookupError:
                        pass

    def test_failed_and_empty_runs_keep_logs_and_never_report_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            host = home / 'host'
            for code, result in [(1, '<UnitTestResult testName="Control" outcome="Passed"/>'),
                                 (0, '')]:
                with self.subTest(exit_code=code):
                    host.write_text('#!' + sys.executable + '\n' + textwrap.dedent(f'''\
                        from pathlib import Path
                        import sys
                        output = Path(next(a.split(':', 1)[1] for a in sys.argv if a.startswith('/ResultsDirectory:')))
                        (output / 'final-terminal-workflows.trx').write_text('<TestRun><Results>{result}</Results></TestRun>')
                        print('retained failure evidence', flush=True)
                        sys.exit({code})
                        '''))
                    host.chmod(0o700)
                    output = home / str(code)
                    with self.assertRaisesRegex(RuntimeError, 'failed' if code else 'no workflow tests passed'):
                        workflows.run_phase(host, home, dict(os.environ), 'ControlledDriver', output)
                    self.assertIn('retained failure evidence', (output / 'workflow-run.txt').read_text())
            # A later independent phase still succeeds after failure and empty-result paths.
            host.write_text(host.read_text().replace('<Results></Results>',
                           '<Results><UnitTestResult testName="Control" outcome="Passed"/></Results>'))
            self.assertEqual(['Control'], workflows.run_phase(host, home, dict(os.environ),
                                                             'ControlledDriver', home / 'recovery'))

    def test_cli_signals_keep_progress_and_terminate_both_test_processes(self):
        from bundle_fixture import make_bundle

        for stop_signal in [signal.SIGINT, signal.SIGTERM]:
            with self.subTest(signal=stop_signal), tempfile.TemporaryDirectory() as tmp:
                home = Path(tmp)
                dist = home / 'dist'
                dist.mkdir()
                for program in ['hvo-roof', 'hvo-roof-install']:
                    owned = [program + '.dll', 'HVO.RoofControllerV4.Client.dll',
                             'HVO.RoofControllerV4.Common.dll']

                    def dependencies(deps):
                        target = deps['targets'][deps['runtimeTarget']['name']]
                        for name in owned:
                            library = name.removesuffix('.dll') + '/4.0.0'
                            deps['libraries'][library] = {'type': 'project'}
                            target[library] = {'runtime': {name: {}}}

                    (dist / (program + '-linux-x64')).write_bytes(make_bundle(
                        deps_edit=dependencies, extra=[(name, b'fixture IL', 1, False) for name in owned]))
                tests = home / 'tests'
                tests.mkdir()
                commands = home / 'commands'
                commands.mkdir()
                host = commands / 'dotnet'
                host.write_text('#!' + sys.executable + '\n' + textwrap.dedent('''\
                    import json, os, subprocess, sys, time
                    from pathlib import Path
                    output = Path(next(a.split(':', 1)[1] for a in sys.argv if a.startswith('/ResultsDirectory:')))
                    child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
                    (output / 'processes.json').write_text(json.dumps([os.getpid(), child.pid]))
                    print('Passed earlier contract [123 ms]', flush=True)
                    print('diagnostic on stderr', file=sys.stderr, flush=True)
                    (output / 'ready').touch()
                    time.sleep(60)
                    '''))
                host.chmod(0o700)
                output = home / 'proof'
                phase = output / 'hvo-roof/contracts'
                with (home / 'runner.log').open('wb') as log:
                    runner = subprocess.Popen(
                        [sys.executable, workflows.__file__, str(dist), '--rid', 'linux-x64',
                         '--tests', str(tests), '--output', str(output)],
                        env=dict(os.environ, PATH=str(commands)), stdout=log, stderr=subprocess.STDOUT)
                    try:
                        deadline = time.monotonic() + 5
                        while not (phase / 'ready').exists() and runner.poll() is None and time.monotonic() < deadline:
                            time.sleep(.01)
                        self.assertTrue((phase / 'ready').exists(), (home / 'runner.log').read_text())
                        os.kill(runner.pid, stop_signal)
                        code = runner.wait(timeout=5)
                        self.assertIn(code, [-int(stop_signal), 128 + int(stop_signal)])
                        progress = (phase / 'workflow-run.txt').read_text()
                        self.assertIn('Passed earlier contract [123 ms]', progress)
                        self.assertIn('diagnostic on stderr', progress)
                        for pid in json.loads((phase / 'processes.json').read_text()):
                            for _ in range(100):
                                status = subprocess.run(['ps', '-o', 'stat=', '-p', str(pid)],
                                                        capture_output=True, text=True, check=False).stdout.strip()
                                if not status or status.startswith('Z'):
                                    break
                                time.sleep(.01)
                            self.assertTrue(not status or status.startswith('Z'), status)
                    finally:
                        processes = phase / 'processes.json'
                        if processes.exists():
                            try:
                                os.killpg(json.loads(processes.read_text())[0], signal.SIGKILL)
                            except ProcessLookupError:
                                pass
                        if runner.poll() is None:
                            runner.kill()
                        runner.wait(timeout=5)


if __name__ == '__main__':
    unittest.main()
