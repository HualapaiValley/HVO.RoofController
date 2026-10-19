#!/usr/bin/env python3
"""Run fake-machine/driver workflows using the managed code extracted from the exact final terminal distributions.

The final executables themselves are exercised by published-terminal.py. This complementary
check replaces the owned assemblies in a private copy of the test host with exact parsed-bundle
members, so all pages, prompts, apply/failure paths execute the distributed implementation.
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
FILTER = ('(FullyQualifiedName~.Cli.|FullyQualifiedName~.Installer.)&FullyQualifiedName!~InstallerKioskSystemTests&'
          'FullyQualifiedName!~InstallerMacSystemTests&TestCategory!=Scenario&'
          'TestCategory!=Browser&TestCategory!=Soak')


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
    combined, programs = {}, {}
    for name in ['hvo-roof', 'hvo-roof-install']:
        digest, members = extract_owned(args.dist / f'{name}-{args.rid}', args.rid)
        programs[name] = digest
        for path, data in members.items():
            if path in combined and combined[path] != data:
                raise RuntimeError(f'Final programs disagree on shared assembly {path}')
            combined[path] = data
    if set(combined) != OWNED:
        raise RuntimeError('Final bundle misses owned assembly: ' + str(OWNED - set(combined)))
    # Repository-relative fixture lookups still resolve, while the staged host remains private and disposable.
    staging_parent = Path(__file__).resolve().parents[2] / 'tests/HVO.RoofControllerV4.RPi.Tests/TestResults'
    staging_parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.final-terminal-workflows-', dir=staging_parent) as temporary:
        staged = Path(temporary) / 'tests'
        shutil.copytree(args.tests, staged)
        for path, data in combined.items():
            (staged / path).write_bytes(data)
        environment = dict(os.environ, HVO_CLI_RENDERS_DIR=str(args.output.resolve() / 'roof'),
                           HVO_INSTALLER_RENDERS_DIR=str(args.output.resolve() / 'installer'),
                           HVO_TERMINAL_DIST=str(args.dist.resolve()))
        command = ['dotnet', 'vstest', str(staged / 'HVO.RoofControllerV4.RPi.Tests.dll'),
                   '/TestCaseFilter:' + FILTER, '/Logger:trx;LogFileName=final-terminal-workflows.trx',
                   '/ResultsDirectory:' + str(args.output.resolve())]
        result = subprocess.run(command, env=environment, capture_output=True, timeout=600)
        (args.output / 'workflow-run.txt').write_bytes(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError('Final distributed workflow check failed; see workflow-run.txt')
    results = ET.parse(args.output / 'final-terminal-workflows.trx').getroot()
    names = [item.attrib.get('testName', '') for item in results.iter() if item.tag.endswith('UnitTestResult') and item.attrib.get('outcome') == 'Passed']
    required = ['EveryPageAndLongPrivatePrompt_KeepStopAndNewestResultVisible', 'ActualWorkflowCatalogue_RetainsItsApiAndPromptAssertions', 'Capabilities_AreExplicit', 'AllConditionalForms_AreBoundedAndKeepCancellationDistinct',
                'F9_SendsStop_FromEveryPage', 'Esc_GoesBackAPage_AndNeverCloses_WhileF10Quits']
    if any(not any(method in name for name in names) for method in required):
        raise RuntimeError('Required workflow tests did not execute successfully')
    summary = dict(rid=args.rid, final_programs=programs,
                   assemblies={name: hashlib.sha256(data).hexdigest() for name, data in combined.items()},
                   filter=FILTER, passed_tests=len(names))
    (args.output / 'workflow-identity.json').write_text(json.dumps(summary, indent=2) + '\n')
    print('PASS: distributed owned assemblies, all roof pages/prompts and fake-machine installer workflows')


if __name__ == '__main__':
    main()
