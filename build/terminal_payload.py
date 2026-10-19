#!/usr/bin/env python3
"""Check the FINAL release assembler's terminal programs (including macOS signature).

Validates bundle manifests, decoded dependency metadata, and the retained notice inventory.
Does not execute a program. Runtime/notice delivery checks are published-terminal.py's job.
"""
import argparse
import json
from pathlib import Path
import re

from dotnet_bundle import Bundle, require

ROOT = Path(__file__).resolve().parents[1]
RIDS = ('linux-x64', 'linux-arm64', 'osx-arm64')
EXCLUDED = re.compile(r'textmate|onigwrap', re.I)
REQUIRED = {'Terminal.Gui.dll', 'Wcwidth.dll', 'Markdig.dll', 'HVO.RoofControllerV4.TerminalUi.dll'}


def check(path, rid, inventory=None):
    inventory = inventory or json.loads((ROOT / 'build/terminal-notices.json').read_text())
    with Bundle(path) as bundle:
        names = {e['path'] for e in bundle.entries}
        require(REQUIRED <= names, 'missing retained terminal assemblies')
        require(not any(EXCLUDED.search(n) for n in names), 'excluded highlighting/native member present')
        # Validate every compressed stream, not just metadata; catches corrupt final packaging.
        for entry in bundle.entries:
            bundle.member(entry)
        deps = bundle.json_member('DepsJson')
        require(isinstance(deps, dict) and isinstance(deps.get('libraries'), dict)
                and isinstance(deps.get('targets'), dict) and isinstance(deps.get('runtimeTarget'), dict),
                'invalid dependency metadata structure')
        runtime = deps.get('runtimeTarget', {}).get('name', '')
        require(runtime == '.NETCoreApp,Version=v10.0/' + rid and runtime in deps['targets'], 'dependency RID mismatch')
        require(not EXCLUDED.search(json.dumps(deps)), 'excluded highlighting dependency metadata present')
        for library in deps['libraries'].values():
            require(isinstance(library, dict) and library.get('type') in ('package', 'runtimepack', 'project'),
                    'invalid dependency library')
        packages = {k: v for k, v in deps['libraries'].items() if v['type'] in ('package', 'runtimepack')}
        require(any(k.startswith('Terminal.Gui/') for k in packages)
                and any(k.startswith('Wcwidth/') for k in packages)
                and any(k.startswith('Markdig/') for k in packages), 'missing retained dependency metadata')
        require(not (set(packages) - set(inventory['packages'])),
                'package notice inventory needs review: ' + ', '.join(sorted(set(packages) - set(inventory['packages']))))
        declared_assemblies = set()
        for target in deps['targets'].values():
            require(isinstance(target, dict), 'invalid dependency target')
            for name, item in target.items():
                require(name in deps['libraries'], 'target library missing from dependency libraries')
                require(isinstance(item, dict), 'invalid dependency target item')
                for kind in ('runtime', 'native', 'runtimeTargets', 'resources'):
                    assets = item.get(kind, {})
                    require(isinstance(assets, dict), 'invalid dependency asset collection')
                    for asset in assets:
                        require(isinstance(assets[asset], dict), 'invalid dependency asset metadata')
                        require(not EXCLUDED.search(asset), 'excluded dependency asset present')
                        if asset.endswith('.dll'):
                            locale = assets[asset].get('locale') if kind == 'resources' else None
                            declared_assemblies.add((locale + '/' if locale else '') + asset.rsplit('/', 1)[-1])
        # The SDK's composite ReadyToRun image contains the entry assembly's generated code; it is
        # additional to the original IL assembly named in deps.json, not another NuGet dependency.
        deps_name = next(e['path'] for e in bundle.entries if e['type'] == 'DepsJson')
        entry_name = deps_name.removesuffix('.deps.json')
        if entry_name + '.dll' in declared_assemblies:
            declared_assemblies.add(entry_name + '.r2r.dll')
        require(all(e['path'] in declared_assemblies for e in bundle.entries if e['type'] == 'Assembly'),
                'assembly missing from dependency inventory')
        summary = bundle.summary()
        summary.update(rid=rid, packages=sorted(packages), native_members=[e['path'] for e in bundle.entries if e['type']=='NativeBinary'])
        return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dist', type=Path, help='final release-assets.py output folder')
    parser.add_argument('--rid', choices=RIDS, action='append', help='limit to a supported host RID')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        results = [check(args.dist / f'{program}-{rid}', rid)
                   for rid in args.rid or RIDS for program in ('hvo-roof', 'hvo-roof-install')]
        if args.output:
            args.output.write_text(json.dumps(results, indent=2)+'\n')
        for result in results:
            print(f"PASS {result['artifact']}: {result['entry_count']} validated entries, {len(result['packages'])} inventoried dependencies, native={result['native_members']}")
    except (OSError, ValueError, KeyError, TypeError, StopIteration) as error:
        parser.exit(1, f'terminal payload: {error}\n')


if __name__ == '__main__':
    main()
