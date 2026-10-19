#!/usr/bin/env python3
"""Convert actual terminal/installer captures and select the documentation images (#176).

Run after the focused driver catalogue and controlled emulator tmux smoke, with their output
directories. Driver .cells.json preserves full graphemes; tmux .ans preserves emitted SGR.
NO_COLOR samples are also drawn with light terminal defaults in the evidence directory.
"""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('terminal_svg', Path(__file__).with_name('ansi-to-svg.py'))
svg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(svg)
ROOT = Path(__file__).resolve().parents[2]


def convert(source, target, title, light=False):
    cells = source.with_suffix('.cells.json')
    rows = svg.parse_cells(json.loads(cells.read_text())) if cells.exists() else svg.parse(source.read_text())
    svg.mask_local_host(rows)
    svg.DEFAULT_FG, svg.DEFAULT_BG = ('#202020', '#ffffff') if light else ('#d4d4d4', '#1e1e1e')
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(svg.render(rows, title))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--roof', type=Path, required=True)
    parser.add_argument('--installer', type=Path, required=True)
    parser.add_argument('--tmux', type=Path, help='controlled emulator captures; required with --docs')
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--docs', action='store_true', help='replace the complete documented set; requires all sources')
    args = parser.parse_args()
    families = [('roof', args.roof), ('installer', args.installer)]
    if args.tmux is not None: families.append(('tmux', args.tmux))
    if args.docs and args.tmux is None: parser.error('--docs requires --tmux')
    for family, folder in families:
        for source in sorted(folder.glob('*.ans')):
            target = args.evidence / family / source.with_suffix('.svg').name
            convert(source, target, 'hvo-roof-install' if family == 'installer' else 'hvo-roof ui')
            if 'nocolor' in source.name:
                convert(source, target.with_name(target.stem + '-light.svg'), 'terminal defaults: light', light=True)
    if args.docs:
        selections = []
        for number, name in enumerate(['roof', 'settings', 'people', 'system', 'setup'], 1):
            selections.append((args.roof / f'176-truecolor-unicode-120x32-page{number}.ans',
                               ROOT / f'docs/images/terminal/{number:02}-{name}.svg', 'hvo-roof ui'))
        for name in ['06-opening', '07-stopped']:
            selections.append((args.tmux / f'{name}.ans', ROOT / f'docs/images/terminal/{name}.svg', 'hvo-roof ui'))
        for name, source in [('08-provider-signin', '08-provider-signin'), ('09-linux-users', '09-linux-users'),
                             ('10-system-offline', '10-system-offline-120x36')]:
            selections.append((args.roof / f'{source}.ans', ROOT / f'docs/images/terminal/{name}.svg', 'hvo-roof ui'))
        for target in sorted((ROOT / 'docs/images/install').glob('*.svg')):
            selections.append((args.installer / f'{target.stem}.ans', target, 'hvo-roof-install'))
        missing = [source.name for source, _, _ in selections if not source.is_file()]
        if missing:
            raise RuntimeError('Missing actual documentation captures: ' + ', '.join(missing))
        for source, target, title in selections:
            convert(source, target, title)
        print(f'PASS: {len(selections)} actual documentation images refreshed; full mode/size matrix retained separately')


if __name__ == '__main__':
    main()
