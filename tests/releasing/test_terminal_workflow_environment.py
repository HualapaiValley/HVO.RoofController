"""Driver subprocesses use the reviewed inert PATH; non-render contracts retain their environment."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import textwrap
import unittest

spec = importlib.util.spec_from_file_location('terminal_workflows', Path(__file__).with_name('check-terminal-workflows.py'))
workflows = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflows)


class WorkflowEnvironment(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
