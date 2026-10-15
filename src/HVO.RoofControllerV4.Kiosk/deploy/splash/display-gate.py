#!/usr/bin/python3
"""Read-only, fail-open console gate; never changes display or kiosk state."""
import json
import pathlib
import re
import sys
import time

def kiosk_owns_scanout():
    try:
        settings = json.loads(pathlib.Path('/opt/hvo-roof-kiosk/appsettings.Local.json').read_text())['Kiosk']
        selected = settings.get('Card')
        if selected:
            card = pathlib.Path(selected).resolve(strict=True).name
        else:
            connected = [p.parent.name.split('-')[0] for p in pathlib.Path('/sys/class/drm').glob('card*-*/status')
                         if p.read_text().strip() == 'connected']
            card = min(connected, key=lambda name: int(name[4:]))
        minor = pathlib.Path('/sys/class/drm', card, 'dev').read_text().strip().split(':')[1]
        state = pathlib.Path('/sys/kernel/debug/dri', minor, 'state').read_text()
        blocks = re.split(r'(?m)^(?=(?:plane|crtc|connector)\[)', state)
        for plane in blocks:
            if not plane.startswith('plane[') or 'allocated by = hvo-roof-kiosk' not in plane:
                continue
            crtc = re.search(r'(?m)^\s*crtc=(\S+)', plane)
            if crtc and any(b.startswith('crtc[') and b.splitlines()[0].endswith(': ' + crtc[1])
                            and re.search(r'(?m)^\s*active=1$', b) for b in blocks):
                return True
    except (OSError, IndexError, ValueError, KeyError):
        pass
    return False

def wait_for_display(probe=kiosk_owns_scanout, clock=time.monotonic, sleep=time.sleep):
    start = clock()
    print('HVO console gate: waiting at most 10 seconds for kiosk display scanout.', flush=True)
    while clock() - start < 10:
        if probe():
            print(f'HVO console gate: kiosk owns active display framebuffer after {clock()-start:.2f}s; allowing console.', flush=True)
            return
        sleep(0.1)
    print(f'HVO console gate: fallback timeout after {clock()-start:.2f}s; allowing console recovery.', flush=True)


if __name__ == '__main__':
    wait_for_display((lambda: False) if '--test-timeout' in sys.argv else kiosk_owns_scanout)
