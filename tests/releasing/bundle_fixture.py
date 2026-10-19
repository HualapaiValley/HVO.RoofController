"""Small format-6 fixtures; no executable code or runtime required."""
import json
import struct
import zlib

SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')


def string(text):
    raw = text.encode(); size = len(raw); prefix = bytearray()
    while size >= 128:
        prefix.append((size & 127) | 128); size >>= 7
    return bytes(prefix) + bytes([size]) + raw


def make_bundle(prefix=b'host', rid='linux-x64', extra=(), edit=None, deps_edit=None, suffix=b'', deps_bytes=None):
    runtime = '.NETCoreApp,Version=v10.0/' + rid
    libs = {n: {'type': 'package'} for n in ['Terminal.Gui/2.5.0', 'Wcwidth/4.0.1', 'Markdig/1.3.2']}
    deps = {'runtimeTarget': {'name': runtime}, 'libraries': libs, 'targets': {runtime: {n: {'runtime': {n.split('/')[0]+'.dll': {}}} for n in libs}}}
    deps['libraries']['HVO.RoofControllerV4.TerminalUi/4.0.0'] = {'type': 'project'}
    deps['targets'][runtime]['HVO.RoofControllerV4.TerminalUi/4.0.0'] = {'runtime': {'HVO.RoofControllerV4.TerminalUi.dll': {}}}
    if deps_edit: deps_edit(deps)
    payloads = [('Terminal.Gui.dll', b'A'*500, 1, True), ('Wcwidth.dll', b'wcwidth', 1, False),
                ('Markdig.dll', b'markdig', 1, False), ('HVO.RoofControllerV4.TerminalUi.dll', b'notices', 1, False),
                ('program.deps.json', json.dumps(deps).encode() if deps_bytes is None else deps_bytes, 3, False),
                ('program.runtimeconfig.json', b'{}', 4, False), *extra]
    data = bytearray(prefix + b'\0'*8 + SIGNATURE)
    entries = []
    for name, content, kind, compress in payloads:
        if compress:
            encoder = zlib.compressobj(wbits=-15); stored = encoder.compress(content) + encoder.flush()
        else: stored = content
        entries.append(dict(path=name, offset=len(data), size=len(content), compressed_size=len(stored) if compress else 0, kind=kind))
        data.extend(stored)
    offset = len(data)
    struct.pack_into('<q', data, len(prefix), offset)
    deps_entry, config = entries[4:6]
    header = struct.pack('<IIi', 6, 0, len(entries)) + string('fixture') + struct.pack('<qqqqQ', deps_entry['offset'], deps_entry['size'], config['offset'], config['size'], 0)
    if edit: edit(entries)
    data.extend(header)
    for e in entries:
        data.extend(struct.pack('<qqqB', e['offset'], e['size'], e['compressed_size'], e['kind']) + string(e['path']))
    return bytes(data) + suffix
