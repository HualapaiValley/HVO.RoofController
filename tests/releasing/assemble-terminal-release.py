#!/usr/bin/env python3
"""Exercise the complete release assembler with real terminal inputs and inert other-product fixtures.

No program is executed. Only final terminal files are retained; never a release for publication.
Production release-assets.py also enforces the same payload policy on its final copied files.
"""
import argparse
from pathlib import Path
import shutil
import tempfile

from test_release_assets import Release, run


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cli',required=True,type=Path)
    parser.add_argument('--installer',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=False)
    with tempfile.TemporaryDirectory(prefix='hvo-terminal-assembler-') as tmp:
        fixture=Release(Path(tmp))
        for source,dest,name in [(args.cli,fixture.cli,'hvo-roof'),(args.installer,fixture.installer,'hvo-roof-install')]:
            for rid in ['linux-x64','linux-arm64','osx-arm64']:
                shutil.copyfile(source/rid/name,dest/rid/name)
        code,out,err=run(fixture.arguments())
        if code: raise RuntimeError(err)
        for name in ['hvo-roof','hvo-roof-install']:
            for rid in ['linux-x64','linux-arm64','osx-arm64']:
                shutil.copy2(fixture.out/f'{name}-{rid}',args.output/f'{name}-{rid}')
    print('PASS complete release assembler; retained six real final terminal programs only')


if __name__=='__main__': main()
