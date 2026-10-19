"""Bounded read-only .NET 6.0 single-file bundle reader; never extracts or executes.

Primary format: dotnet/runtime v10.0.0, src/installer/managed/Microsoft.NET.HostModel/
Bundle/{Manifest,FileEntry,FileType,Bundler}.cs and AppHost/HostWriter.cs.
Trailing data is permitted: macOS signing appends a signature after the manifest.
"""
from __future__ import annotations

import hashlib
import json
import mmap
from pathlib import Path
import struct
import zlib

SIGNATURE = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
MAX_FILE = 256 * 1024 * 1024
MAX_MEMBER = 128 * 1024 * 1024
TYPES = ('Unknown', 'Assembly', 'NativeBinary', 'DepsJson', 'RuntimeConfigJson', 'Symbols')


class BundleError(ValueError):
    """Invalid or unsupported bundle (including a bound exceeded)."""


def require(condition, message):
    if not condition:
        raise BundleError(message)


class Reader:
    def __init__(self, data, pos):
        self.data, self.pos = data, pos

    def take(self, count):
        require(0 <= self.pos <= len(self.data) and 0 <= count <= len(self.data) - self.pos,
                'out-of-bounds field')
        result = self.data[self.pos:self.pos + count]
        self.pos += count
        return result

    def number(self, fmt):
        return struct.unpack(fmt, self.take(struct.calcsize(fmt)))[0]

    def string(self, limit=4096):
        length = 0
        for shift in range(0, 35, 7):
            part = self.number('<B')
            require(shift != 28 or part <= 7, 'invalid string length')
            length |= (part & 127) << shift
            if not part & 128:
                break
        else:
            raise BundleError('unterminated string length')
        require(length <= limit, 'oversized string')
        try:
            return self.take(length).decode('utf-8', 'strict')
        except UnicodeError as error:
            raise BundleError('invalid UTF-8') from error


def safe_path(name):
    require(bool(name) and '\\' not in name and ':' not in name
            and all(part not in ('', '.', '..') for part in name.split('/'))
            and all(ord(c) >= 32 and ord(c) != 127 for c in name), 'unsafe member path')


class Bundle:
    """Use as a context manager; validates the entire manifest before exposing any member."""
    def __init__(self, path):
        self.path = Path(path)

    def __enter__(self):
        self.stream = self.path.open('rb')
        self.data = None
        try:
            length = self.path.stat().st_size
            require(0 < length <= MAX_FILE, 'bundle file size out of bounds')
            self.data = mmap.mmap(self.stream.fileno(), 0, access=mmap.ACCESS_READ)
            self._parse()
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *args):
        if self.data is not None:
            self.data.close()
        self.stream.close()

    def _parse(self):
        data = self.data
        marker = data.find(SIGNATURE)
        require(marker >= 8 and data.find(SIGNATURE, marker + 1) == -1, 'missing or non-unique bundle signature')
        offset = struct.unpack_from('<q', data, marker - 8)[0]
        require(marker + len(SIGNATURE) <= offset < len(data), 'invalid manifest offset')
        reader = Reader(data, offset)
        major, minor, count = reader.number('<I'), reader.number('<I'), reader.number('<i')
        require((major, minor) == (6, 0), 'unsupported bundle version')
        require(0 < count <= 10000, 'entry count out of bounds')
        self.bundle_id = reader.string(256)
        require(bool(self.bundle_id), 'empty bundle id')
        deps_offset, deps_size, config_offset, config_size = [reader.number('<q') for _ in range(4)]
        require(reader.number('<Q') in (0, 1), 'unsupported bundle flags')
        self.entries = []
        names = set()
        for _ in range(count):
            start, size, compressed = [reader.number('<q') for _ in range(3)]
            kind, name = reader.number('<B'), reader.string()
            safe_path(name)
            require(name.casefold() not in names, 'duplicate member path')
            require(kind < len(TYPES), 'unknown member type')
            require(0 <= start < offset and 0 <= size <= MAX_MEMBER and 0 <= compressed <= size
                    and start + (compressed or size) <= offset, 'member range out of bounds')
            names.add(name.casefold())
            self.entries.append(dict(path=name, offset=start, size=size, compressed_size=compressed, type=TYPES[kind]))
        spans = sorted((e['offset'], e['offset'] + (e['compressed_size'] or e['size'])) for e in self.entries)
        require(all(a[1] <= b[0] for a, b in zip(spans, spans[1:])), 'overlapping payloads')
        require(sum(e['size'] for e in self.entries) <= 1024 * 1024 * 1024, 'total member size out of bounds')
        for kind, start, size in [('DepsJson', deps_offset, deps_size), ('RuntimeConfigJson', config_offset, config_size)]:
            entries = [e for e in self.entries if e['type'] == kind]
            require(len(entries) == 1 and (entries[0]['offset'], entries[0]['size']) == (start, size),
                    f'{kind} header disagrees with manifest')
            require(entries[0]['compressed_size'] == 0, f'compressed {kind} is unsupported')

    def member(self, entry):
        require(entry in self.entries, 'member is not in this manifest')
        size, stored = entry['size'], entry['compressed_size'] or entry['size']
        content = self.data[entry['offset']:entry['offset'] + stored]
        if entry['compressed_size']:
            decoder = zlib.decompressobj(-15)
            try:
                content = decoder.decompress(content, size + 1)
            except zlib.error as error:
                raise BundleError('invalid compressed member') from error
            require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail, 'invalid compressed stream bounds')
        require(len(content) == size, 'decoded member size mismatch')
        return content

    def json_member(self, kind):
        entry = next(e for e in self.entries if e['type'] == kind)
        require(entry['size'] <= 8 * 1024 * 1024, 'dependency/config JSON size out of bounds')
        def unique_object(pairs):
            result = {}
            for key, value in pairs:
                require(key not in result, 'duplicate JSON property')
                result[key] = value
            return result
        try:
            document = json.loads(self.member(entry), object_pairs_hook=unique_object)
            require(isinstance(document, dict), 'metadata JSON must be an object')
            pending = [(document, 0)]
            while pending:
                value, depth = pending.pop()
                require(depth <= 64, 'metadata JSON nesting out of bounds')
                children = value.values() if isinstance(value, dict) else value if isinstance(value, list) else ()
                pending.extend((child, depth + 1) for child in children)
            return document
        except (ValueError, UnicodeError, RecursionError) as error:
            raise BundleError(f'invalid {kind}') from error

    def summary(self):
        return dict(artifact=self.path.name, bytes=len(self.data), sha256=hashlib.sha256(self.data).hexdigest(),
                    bundle_version=[6, 0], entry_count=len(self.entries), entries=self.entries)
