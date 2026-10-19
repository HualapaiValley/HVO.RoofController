"""Manifest/metadata acceptance and hostile bounds, independent of a .NET publish."""
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'build'))
from dotnet_bundle import Bundle, BundleError, Reader, SIGNATURE, MAX_MEMBER
import terminal_payload
from bundle_fixture import make_bundle


class BundleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'program'

    def parse(self, data):
        self.path.write_bytes(data)
        with Bundle(self.path) as bundle:
            for entry in bundle.entries: bundle.member(entry)
            return bundle.summary()

    def test_valid_compression_manifest_and_signed_trailer_for_each_rid(self):
        for rid in terminal_payload.RIDS:
            self.path.write_bytes(make_bundle(rid=rid, suffix=b'signature trailer'))
            result = terminal_payload.check(self.path, rid)
            self.assertEqual(result['entry_count'], 6)
            with Bundle(self.path) as bundle:
                self.assertEqual(bundle.member(bundle.entries[0]), b'A'*500)

    def test_malformed_manifest_and_paths(self):
        cases = [lambda e: e[0].update(path='../evil'), lambda e: e[0].update(path='/evil'),
                 lambda e: e[0].update(path='a\\b'), lambda e: e[0].update(path='a//b'),
                 lambda e: e[0].update(path='Wcwidth.dll'), lambda e: e[0].update(path='wcwidth.DLL'),
                 lambda e: e[0].update(size=-1), lambda e: e[0].update(size=MAX_MEMBER+1),
                 lambda e: e[0].update(offset=-1), lambda e: e[0].update(offset=2**62),
                 lambda e: e[0].update(kind=99), lambda e: e[0].update(compressed_size=501),
                 lambda e: e[1].update(offset=e[0]['offset']), lambda e: e[4].update(size=1)]
        for change in cases:
            with self.subTest(change=change), self.assertRaises(BundleError): self.parse(make_bundle(edit=change))

    def test_truncation_bad_signature_version_counts_and_header(self):
        valid = make_bundle(); marker = valid.index(SIGNATURE); offset = struct.unpack_from('<q',valid,marker-8)[0]
        cases = [b'', valid[:offset], valid[:-1], valid+SIGNATURE]
        for pos, fmt, value in [(marker-8,'<q',-1),(offset,'<I',7),(offset+8,'<i',-1),(offset+8,'<i',10001)]:
            data=bytearray(valid); struct.pack_into(fmt,data,pos,value); cases.append(data)
        for data in cases:
            with self.subTest(size=len(data)), self.assertRaises(BundleError): self.parse(data)

    def test_invalid_lengths_and_utf8(self):
        for data in [b'\xff'*5,b'\x80',b'\x01\xff',b'\x80\x80\x80\x80\x08']:
            with self.assertRaises(BundleError): Reader(data,0).string()
        with self.assertRaises(BundleError): Reader(b'\x05abcde',0).string(4)

    def test_compression_corruption_expansion_and_trailing_stream(self):
        for change in [lambda e:e[0].update(size=1), lambda e:e[0].update(compressed_size=1)]:
            with self.assertRaises(BundleError): self.parse(make_bundle(edit=change))
        data=bytearray(make_bundle()); data[len(b'host')+8+len(SIGNATURE)]^=255
        with self.assertRaises(BundleError): self.parse(data)

    def test_excluded_managed_and_both_native_suffixes(self):
        for name,kind in [('TextMateSharp.dll',1),('TextMateSharp.Grammars.dll',1),('Onigwrap.dll',1),('libonigwrap.so',2),('libonigwrap.dylib',2)]:
            self.path.write_bytes(make_bundle(extra=[(name,b'x',kind,False)]))
            with self.assertRaisesRegex(BundleError,'excluded'): terminal_payload.check(self.path,'linux-x64')

    def test_dependency_only_exclusion_unknown_notice_and_wrong_rid(self):
        for name in ['Onigwrap/1.0.11','New.Dependency/1.0.0','Markdig/99.0.0']:
            self.path.write_bytes(make_bundle(deps_edit=lambda d:d['libraries'].update({name:{'type':'package'}})))
            with self.assertRaises(BundleError): terminal_payload.check(self.path,'linux-x64')
        self.path.write_bytes(make_bundle())
        with self.assertRaisesRegex(BundleError,'RID'): terminal_payload.check(self.path,'osx-arm64')

    def test_malformed_or_duplicate_dependency_json_and_undeclared_assembly(self):
        for raw in [b'{', b'{"x": 1, "x": 2}', b'['*1100+b'0'+b']'*1100]:
            self.path.write_bytes(make_bundle(deps_bytes=raw))
            with Bundle(self.path) as bundle, self.assertRaises(BundleError): bundle.json_member('DepsJson')
        for change in [lambda d:d.update(runtimeTarget=[]), lambda d:d['libraries'].update({'invalid':[]})]:
            self.path.write_bytes(make_bundle(deps_edit=change))
            with self.assertRaises(BundleError): terminal_payload.check(self.path,'linux-x64')
        self.path.write_bytes(make_bundle(extra=[('Not.Inventoried.dll',b'new payload',1,False)]))
        with self.assertRaisesRegex(BundleError,'assembly missing'): terminal_payload.check(self.path,'linux-x64')

    def test_sdk_composite_image_is_tied_to_the_declared_entry_assembly(self):
        def entry(d):
            d['libraries']['program/4.0.0']={'type':'project'}
            d['targets'][d['runtimeTarget']['name']]['program/4.0.0']={'runtime':{'program.dll':{}}}
        self.path.write_bytes(make_bundle(deps_edit=entry,extra=[('program.r2r.dll',b'compiled code',1,False)]))
        terminal_payload.check(self.path,'linux-x64')
        self.path.write_bytes(make_bundle(extra=[('program.r2r.dll',b'undeclared code',1,False)]))
        with self.assertRaisesRegex(BundleError,'assembly missing'): terminal_payload.check(self.path,'linux-x64')

    def test_notice_document_is_the_inventoried_document_with_every_source(self):
        root=terminal_payload.ROOT
        inventory=json.loads((root/'build/terminal-notices.json').read_text())
        data=(root/'src/HVO.RoofControllerV4.TerminalUi/Notices/THIRD-PARTY-NOTICES.txt').read_bytes()
        self.assertEqual(hashlib.sha256(data).hexdigest(),inventory['noticeSha256'])
        for source in inventory['sources']:
            self.assertIn(source['sha256'].encode(),data)
            self.assertIn(source['source'].encode(),data)
        for package, details in inventory['packages'].items():
            self.assertIn(package.encode(),data)
            self.assertIn(details['noticeSection'].encode(),data)


class FinalAssemblerTests(unittest.TestCase):
    def test_assembler_rejects_final_copied_member_even_when_input_was_clean(self):
        from unittest.mock import patch
        from test_release_assets import Release, run, release_assets
        with tempfile.TemporaryDirectory() as tmp:
            fixture=Release(Path(tmp))
            copy=release_assets.shutil.copyfile
            def changed_copy(source,target,*args,**kwargs):
                result=copy(source,target,*args,**kwargs)
                if Path(target).name=='hvo-roof-linux-x64':
                    Path(target).write_bytes(make_bundle(prefix=b'host',extra=[('libonigwrap.dylib',b'bad',2,False)]))
                return result
            with patch.object(release_assets.shutil,'copyfile',side_effect=changed_copy):
                code,out,error=run(fixture.arguments())
            self.assertEqual(code,1)
            self.assertIn('terminal payload check failed',error)
            self.assertIn('excluded',error)
