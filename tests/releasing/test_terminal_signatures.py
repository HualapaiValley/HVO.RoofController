"""Exercise the actual CI shell step using a codesign test double, without executing any program."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
STEP = 'Verify final macOS terminal signatures and identities'
PROGRAMS = ('hvo-roof', 'hvo-roof-install')
FAKE_CODESIGN = '''import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with Path(os.environ['FAKE_CODESIGN_CALLS']).open('a') as log:
    log.write(json.dumps(args) + '\\n')
name = Path(args[-1]).name.removesuffix('-osx-arm64')
settings = json.loads(Path(os.environ['FAKE_CODESIGN_SETTINGS']).read_text()).get(name, {})
if args[:2] == ['--display', '--verbose=4']:
    print('Identifier=' + settings.get('identifier', 'io.github.hualapaivalley.' + name), file=sys.stderr)
    print('Signature=' + settings.get('signature', 'adhoc'), file=sys.stderr)
elif args[:3] == ['--verify', '--strict', '--verbose=2']:
    print('codesign test double: strict verification', file=sys.stderr)
    sys.exit(23 if settings.get('invalid') else 0)
else:
    sys.exit(24)
'''


def workflow_step():
    source = (ROOT / '.github/workflows/ci.yml').read_text()
    start = source.index('      - name: ' + STEP + '\n')
    end = source.index('      - name: Run exact final programs offline on their supported host\n', start)
    block = source[start:end]
    if "        if: matrix.rid == 'osx-arm64'\n" not in block:
        raise AssertionError('The signature gate must be macOS-only and precede final runtime execution.')
    return textwrap.dedent(block.split('        run: |\n', 1)[1])


class FinalMacSignatures(unittest.TestCase):
    def run_step(self, folder, settings):
        (folder / 'terminal-dist').mkdir()
        for program in PROGRAMS:
            # Empty stand-ins are never executed; only the fake codesign inspects their exact paths.
            (folder / 'terminal-dist' / f'{program}-osx-arm64').write_bytes(b'not executable')
        commands = folder / 'commands'
        commands.mkdir()
        codesign = commands / 'codesign'
        codesign.write_text(f'#!{sys.executable}\n' + FAKE_CODESIGN)
        codesign.chmod(0o700)
        config = folder / 'codesign-settings.json'
        config.write_text(json.dumps(settings))
        calls = folder / 'codesign-calls.jsonl'
        environment = dict(os.environ, RUNNER_TEMP=str(folder), PATH=str(commands) + os.pathsep + os.environ['PATH'],
                           FAKE_CODESIGN_CALLS=str(calls), FAKE_CODESIGN_SETTINGS=str(config))
        result = subprocess.run(['bash', '--noprofile', '--norc', '-e', '-o', 'pipefail', '-c', workflow_step()],
                                env=environment, capture_output=True, text=True, timeout=10)
        return result, [json.loads(line) for line in calls.read_text().splitlines()]

    def test_both_exact_final_files_are_strictly_verified_with_expected_identities_and_evidence(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            result, calls = self.run_step(folder, {})
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = []
            for program in PROGRAMS:
                binary = str(folder / 'terminal-dist' / f'{program}-osx-arm64')
                expected.extend([['--display', '--verbose=4', binary], ['--verify', '--strict', '--verbose=2', binary]])
                details = (folder / 'terminal-evidence' / f'{program}-codesign.txt').read_text()
                self.assertIn(f'Identifier=io.github.hualapaivalley.{program}\n', details)
                self.assertIn('Signature=adhoc\n', details)
                self.assertTrue((folder / 'terminal-evidence' / f'{program}-codesign-verify.txt').is_file())
            self.assertEqual(calls, expected)

    def test_invalid_signature_wrong_identifier_or_non_adhoc_fails_for_either_final_program(self):
        for program in PROGRAMS:
            for fault in [{'invalid': True}, {'identifier': 'io.github.hualapaivalley.wrong'}, {'signature': 'Developer ID'}]:
                with self.subTest(program=program, fault=fault), tempfile.TemporaryDirectory() as tmp:
                    folder = Path(tmp)
                    result, calls = self.run_step(folder, {program: fault})
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(['--verify', '--strict', '--verbose=2',
                                   str(folder / 'terminal-dist' / f'{program}-osx-arm64')], calls)
                    self.assertTrue((folder / 'terminal-evidence' / f'{program}-codesign.txt').is_file())
                    self.assertTrue((folder / 'terminal-evidence' / f'{program}-codesign-verify.txt').is_file())
