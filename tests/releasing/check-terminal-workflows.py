#!/usr/bin/env python3
"""Run fake-machine/driver workflows using the managed code extracted from the exact final terminal distributions.

The final executables themselves are exercised by published-terminal.py. This complementary
check replaces the owned assemblies in a private copy of the test host with exact parsed-bundle
members separately for each program, so all pages, prompts, apply/failure paths execute that
program's distributed managed implementation. The independent test host JITs the exact bundled IL;
self-contained ReadyToRun images are exercised in their original final executables by the PTY gate.
Driver groups use the reviewed inert bash PATH and an absolute dotnet host. The complementary
non-render groups retain the established environment for command and absolute-openssl contracts.
No machine installer, host service, Docker daemon or physical HAT is called by these filters.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'build'))
from dotnet_bundle import Bundle
from terminal_payload import RIDS, check

spec = importlib.util.spec_from_file_location('published_terminal', Path(__file__).with_name('published-terminal.py'))
published = importlib.util.module_from_spec(spec)
spec.loader.exec_module(published)

OWNED = {'hvo-roof.dll', 'hvo-roof-install.dll',
         'HVO.RoofControllerV4.TerminalUi.dll', 'HVO.RoofControllerV4.Client.dll',
         'HVO.RoofControllerV4.Common.dll', 'Terminal.Gui.dll'}
# Every installer test in this filter uses FakeMachine/redirected files. The two opt-in real-host
# installation suites are explicitly excluded, irrespective of the runner's environment.
FILTERS = {
    'hvo-roof': 'FullyQualifiedName~.Cli.&TestCategory!=Scenario&TestCategory!=Browser&TestCategory!=Soak',
    'hvo-roof-install': ('FullyQualifiedName~.Installer.&FullyQualifiedName!~InstallerKioskSystemTests&'
                         'FullyQualifiedName!~InstallerMacSystemTests&TestCategory!=Scenario&'
                         'TestCategory!=Browser&TestCategory!=Soak')
}
# These groups initialize Terminal.Gui. The upgrade-prerequisite cases include a wizard entry
# alongside fake command/session entries; all of that group uses FakeMachine commands.
RENDER_CLASSES = {
    'hvo-roof': ['RoofTerminalUiTests', 'TerminalDesignTests'],
    'hvo-roof-install': ['InstallerWizardTests', 'InstallerWizardClientTests',
                         'InstallerWizardControllerTests', 'InstallerDesignTests',
                         'InstallerUpgradePrerequisiteTests']
}
CONTRACT_REQUIRED = {
    'hvo-roof-install': ['ACommandThatDoesNotInherit_GetsOnlyTheKeptVariables_AndItsOwn',
                         'Openssl_VerifiesTheChain_ForAServer_ByNameAndAddress']
}
REQUIRED = {
    'hvo-roof': ['EveryPageAndLongPrivatePrompt_KeepStopAndNewestResultVisible',
                 'ActualWorkflowCatalogue_RetainsItsApiAndPromptAssertions', 'Capabilities_AreExplicit',
                 'AdminSettingsCatalogue_TraversesEveryGroupAndType_ThroughActualControls',
                 'RemainingIdentityAndHandEditPrompts_KeepActionsAndStopVisible',
                 'ExceptionallyLongStopDetails_AreBoundedReadableAndResetForTheNewestResult',
                 'LongCallerName_PreservesRoleAndFeedInTheHeader',
                 'StopAcknowledgement_PreservesTheActualVerificationMessage_InTheRgbScheme',
                 'LongActualNameValidation_IsKeyboardReadable_AboveActionsAndStop', 'F9_SendsStop_FromEveryPage', 'AsciiControls_KeepMasksAndScrollbarsAscii_AfterApplicationConfiguration'],
    'hvo-roof-install': ['AllConditionalForms_AreBoundedAndKeepCancellationDistinct',
                         'ClientCaAction_IsFullyVisibleFocusableAndHitTestable',
                         'LongFirstAdminValidation_LeavesActionsVisible_AndCompleteCorrectionReachable',
                         'Esc_GoesBackAPage_AndNeverCloses_WhileF10Quits',
                         'FinalPrograms_KeepOfflineNotices_AfterInstallationAndRollbackCopyPaths']
}


def extract_owned(program, rid):
    inventory = check(program, rid)
    with Bundle(program) as bundle:
        members = {entry['path']: bundle.member(entry) for entry in bundle.entries if entry['path'] in OWNED}
    return inventory['sha256'], members


def phase_filters(name):
    """Disjoint render and remaining contract filters together retain the original catalogue."""
    render = '|'.join('FullyQualifiedName~.' + item + '.' for item in RENDER_CLASSES[name])
    contracts = '&'.join('FullyQualifiedName!~.' + item + '.' for item in RENDER_CLASSES[name])
    return [('contracts', f'({FILTERS[name]})&({contracts})'),
            ('render', f'({FILTERS[name]})&({render})')]


def phase_environment(base, home, render):
    """Isolate actual drivers without hiding utilities from non-render command/crypto contracts."""
    environment = dict(base)
    if render:
        for folder in ['config', 'cache', 'state', 'data', 'tmp', 'extract']:
            (home / folder).mkdir(mode=0o700)
        commands = published.isolated_command_path(home)
        environment.update(HOME=str(home), XDG_CONFIG_HOME=str(home / 'config'),
                           XDG_CACHE_HOME=str(home / 'cache'), XDG_STATE_HOME=str(home / 'state'),
                           XDG_DATA_HOME=str(home / 'data'), TMPDIR=str(home / 'tmp'),
                           DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(home / 'extract'), PATH=str(commands))
    return environment


def run_phase(dotnet, staged, environment, test_filter, output):
    if not Path(dotnet).is_absolute():
        raise ValueError('The managed workflow host must be absolute before restricting PATH')
    output.mkdir(parents=True, exist_ok=True)
    command = [str(dotnet), 'vstest', str(staged / 'HVO.RoofControllerV4.RPi.Tests.dll'),
               '/TestCaseFilter:' + test_filter, '/Logger:trx;LogFileName=final-terminal-workflows.trx',
               '/ResultsDirectory:' + str(output.resolve())]
    result = subprocess.run(command, env=environment, capture_output=True, timeout=600)
    (output / 'workflow-run.txt').write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f'{output.parent.name}/{output.name}: distributed workflow check failed; see workflow-run.txt')
    results = ET.parse(output / 'final-terminal-workflows.trx').getroot()
    names = [item.attrib.get('testName', '') for item in results.iter()
             if item.tag.endswith('UnitTestResult') and item.attrib.get('outcome') == 'Passed']
    if not names:
        raise RuntimeError(f'{output.parent.name}/{output.name}: no workflow tests passed')
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dist', type=Path)
    parser.add_argument('--tests', type=Path, required=True)
    parser.add_argument('--rid', choices=RIDS, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    # Resolve before any render PATH is restricted; vstest and testhost inherit that restricted PATH.
    dotnet = shutil.which('dotnet')
    if dotnet is None:
        raise RuntimeError('The installed pinned dotnet host is required')
    dotnet = Path(dotnet).resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    programs = {}
    # Repository-relative fixture lookups still resolve, while each staged host remains private and disposable.
    staging_parent = Path(__file__).resolve().parents[2] / 'tests/HVO.RoofControllerV4.RPi.Tests/TestResults'
    staging_parent.mkdir(parents=True, exist_ok=True)
    for name in ['hvo-roof', 'hvo-roof-install']:
        digest, members = extract_owned(args.dist / f'{name}-{args.rid}', args.rid)
        expected = (OWNED - {'hvo-roof.dll', 'hvo-roof-install.dll'}) | {name + '.dll'}
        if set(members) != expected:
            raise RuntimeError(f'{name}: final bundle misses owned assembly: {expected - set(members)}')
        result_dir = args.output / name
        result_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='.final-terminal-workflows-', dir=staging_parent) as temporary:
            staged = Path(temporary) / 'tests'
            shutil.copytree(args.tests, staged)
            for path, data in members.items():
                target = staged / path
                target.write_bytes(data)
                if target.read_bytes() != data:
                    raise RuntimeError(f'{name}: staged assembly differs: {path}')
            environment = dict(os.environ, HVO_CLI_RENDERS_DIR=str(args.output.resolve() / 'roof'),
                               HVO_INSTALLER_RENDERS_DIR=str(args.output.resolve() / 'installer'),
                               HVO_TERMINAL_DIST=str(args.dist.resolve()),
                               DOTNET_ReadyToRun='0', COMPlus_ReadyToRun='0')
            names, phases = [], {}
            for phase, test_filter in phase_filters(name):
                with tempfile.TemporaryDirectory(prefix='hvo-driver-home-') as home:
                    phase_env = phase_environment(environment, Path(home), render=phase == 'render')
                    passed = run_phase(dotnet, staged, phase_env, test_filter, result_dir / phase)
                    if phase == 'contracts' and any(not any(method in test for test in passed)
                                                    for method in CONTRACT_REQUIRED.get(name, [])):
                        raise RuntimeError(f'{name}: environment/openssl contract tests did not pass')
                    record = dict(filter=test_filter, passed_tests=len(passed), absolute_dotnet=dotnet.is_absolute(),
                                  path_policy='inert bash only' if phase == 'render' else 'established runner environment')
                    if phase == 'render':
                        commands = Path(phase_env['PATH'])
                        entries = sorted(path.name for path in commands.iterdir())
                        if entries != ['bash']:
                            raise RuntimeError(f'{name}: render PATH changed')
                        record.update(path_entries=entries, bash_sha256=hashlib.sha256((commands / 'bash').read_bytes()).hexdigest())
                    phases[phase] = record
                    names.extend(passed)
        if any(not any(method in test for test in names) for method in REQUIRED[name]):
            raise RuntimeError(f'{name}: required workflow tests did not execute successfully')
        programs[name] = dict(sha256=digest, execution='JIT of exact bundled managed assemblies; native final-file PTY is separate', assemblies={path: hashlib.sha256(data).hexdigest()
                              for path, data in members.items()}, filter=FILTERS[name], passed_tests=len(names), phases=phases)
    (args.output / 'workflow-identity.json').write_text(json.dumps(dict(rid=args.rid, programs=programs), indent=2) + '\n')
    print('PASS: distributed owned assemblies, all roof pages/prompts and fake-machine installer workflows')


if __name__ == '__main__':
    main()
