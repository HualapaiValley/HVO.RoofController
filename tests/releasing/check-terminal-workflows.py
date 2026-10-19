#!/usr/bin/env python3
"""Run fake-machine/driver workflows using the managed code extracted from the exact final terminal distributions.

The final executables themselves are exercised by published-terminal.py. This complementary
check replaces the owned assemblies in a private copy of the test host with exact parsed-bundle
members separately for each program, so all pages, prompts, apply/failure paths execute that
program's distributed managed implementation. The independent test host JITs the exact bundled IL;
self-contained ReadyToRun images are exercised in their original final executables by the PTY gate.
No machine installer, host service, Docker daemon or physical HAT is called by these filters.
"""
import argparse
import hashlib
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
REQUIRED = {
    'hvo-roof': ['EveryPageAndLongPrivatePrompt_KeepStopAndNewestResultVisible',
                 'ActualWorkflowCatalogue_RetainsItsApiAndPromptAssertions', 'Capabilities_AreExplicit',
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dist', type=Path)
    parser.add_argument('--tests', type=Path, required=True)
    parser.add_argument('--rid', choices=RIDS, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
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
            command = ['dotnet', 'vstest', str(staged / 'HVO.RoofControllerV4.RPi.Tests.dll'),
                       '/TestCaseFilter:' + FILTERS[name], '/Logger:trx;LogFileName=final-terminal-workflows.trx',
                       '/ResultsDirectory:' + str(result_dir.resolve())]
            result = subprocess.run(command, env=environment, capture_output=True, timeout=600)
            (result_dir / 'workflow-run.txt').write_bytes(result.stdout + result.stderr)
            if result.returncode:
                raise RuntimeError(f'{name}: final distributed workflow check failed; see workflow-run.txt')
        results = ET.parse(result_dir / 'final-terminal-workflows.trx').getroot()
        names = [item.attrib.get('testName', '') for item in results.iter()
                 if item.tag.endswith('UnitTestResult') and item.attrib.get('outcome') == 'Passed']
        if any(not any(method in test for test in names) for method in REQUIRED[name]):
            raise RuntimeError(f'{name}: required workflow tests did not execute successfully')
        programs[name] = dict(sha256=digest, execution='JIT of exact bundled managed assemblies; native final-file PTY is separate', assemblies={path: hashlib.sha256(data).hexdigest()
                              for path, data in members.items()}, filter=FILTERS[name], passed_tests=len(names))
    (args.output / 'workflow-identity.json').write_text(json.dumps(dict(rid=args.rid, programs=programs), indent=2) + '\n')
    print('PASS: distributed owned assemblies, all roof pages/prompts and fake-machine installer workflows')


if __name__ == '__main__':
    main()
