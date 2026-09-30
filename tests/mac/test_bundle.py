#!/usr/bin/env python3
"""Tests for bundle.py's reading of the Mac app's bundle (#48): Mach-O files, Info.plist and AppIcon.icns, whole and
damaged. A damaged file is a problem in check's report (exit 1), never a hang or a traceback.

    python3 -m unittest discover -s tests/mac -p 'test_*.py'
"""

import contextlib
import importlib.util
import io
import os
import pathlib
import plistlib
import signal
import struct
import tempfile
import unittest

BUNDLE_DIR = pathlib.Path(__file__).resolve().parents[2] / "src" / "HVO.RoofControllerV4.Mac" / "bundle"
_spec = importlib.util.spec_from_file_location("bundle", BUNDLE_DIR / "bundle.py")
bundle = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bundle)

PNG = b"\x89PNG\r\n\x1a\n"
MACOS_12 = 12 << 16
MACOS_13 = 13 << 16


def thin(commands, cpu=bundle.CPU_ARM64):
    """A 64-bit little-endian Mach-O image with the given load commands, each (command, body)."""
    encoded = b"".join(struct.pack("<II", command, 8 + len(body)) + body for command, body in commands)
    return struct.pack("<IIIIIIII", 0xFEEDFACF, cpu, 0, 2, len(commands), len(encoded), 0, 0) + encoded


def build_version(minimum):
    return bundle.LC_BUILD_VERSION, struct.pack("<IIII", bundle.PLATFORM_MACOS, minimum, minimum, 0)


def signature():
    return bundle.LC_CODE_SIGNATURE, struct.pack("<II", 0, 0)


def universal(*slices):
    """A universal (fat) file holding the given (cpu, image) slices."""
    header = struct.pack(">II", 0xCAFEBABE, len(slices))
    offset = 8 + 20 * len(slices)
    table, images = b"", b""
    for cpu, image in slices:
        table += struct.pack(">iiIII", cpu, 0, offset + len(images), len(image), 0)
        images += image
    return header + table + images


def png(size):
    """The start of a PNG file: enough for its width and height."""
    return PNG + struct.pack(">I", 13) + b"IHDR" + struct.pack(">II", size, size) + b"\x08\x06\x00\x00\x00"


def icns(elements):
    """An .icns file holding the given (kind, data) elements."""
    body = b"".join(kind + struct.pack(">I", 8 + len(data)) + data for kind, data in elements)
    return b"icns" + struct.pack(">I", 8 + len(body)) + body


def whole_icon():
    return icns([(kind, png(size)) for kind, size in bundle.ICON_ELEMENTS.items()])


class Hang(Exception):
    pass


class BundleTestCase(unittest.TestCase):
    """Each test has 10 s: a parser that stops moving fails here instead of hanging CI."""

    def setUp(self):
        def hung(*_):
            raise Hang("bundle.py did not finish within 10 s")

        previous = signal.signal(signal.SIGALRM, hung)
        signal.alarm(10)
        self.addCleanup(signal.signal, signal.SIGALRM, previous)
        self.addCleanup(signal.alarm, 0)
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = pathlib.Path(directory.name)

    def write(self, name, data):
        path = self.directory / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return str(path)


class MachOTests(BundleTestCase):
    def test_a_signed_image_says_so_and_names_the_oldest_macos(self):
        image = bundle.MachO(self.write("program", thin([build_version(MACOS_12), signature()])))
        self.assertEqual(image.slices, {bundle.CPU_ARM64: {"signed": True, "minimum": MACOS_12}})

    def test_an_unsigned_image_says_so(self):
        image = bundle.MachO(self.write("program", thin([build_version(MACOS_12)])))
        self.assertFalse(image.slices[bundle.CPU_ARM64]["signed"])

    def test_a_universal_file_gives_each_slice(self):
        x64 = 0x01000007
        image = bundle.MachO(self.write("program", universal(
            (x64, thin([build_version(MACOS_13)], cpu=x64)),
            (bundle.CPU_ARM64, thin([build_version(MACOS_12), signature()])))))
        self.assertEqual(image.slices[bundle.CPU_ARM64], {"signed": True, "minimum": MACOS_12})
        self.assertEqual(image.slices[x64], {"signed": False, "minimum": MACOS_13})

    def test_damaged_files_are_refused_with_a_reason(self):
        whole = thin([build_version(MACOS_12), signature()])
        zero_size = bytearray(whole)
        struct.pack_into("<I", zero_size, 32 + 4, 0)
        too_long = bytearray(whole)
        struct.pack_into("<I", too_long, 32 + 4, 4096)
        short_build_version = thin([(bundle.LC_BUILD_VERSION, b"\x01\x00\x00\x00")])
        slice_past_the_end = bytearray(universal((bundle.CPU_ARM64, whole)))
        struct.pack_into(">I", slice_past_the_end, 8 + 12, len(whole) + 1)
        cases = {
            "empty": (b"", "not a 64-bit Mach-O file"),
            "not Mach-O": (b"#!/bin/sh\necho hello\n", "not a 64-bit Mach-O file"),
            "header cut short": (whole[:12], "the Mach-O header is cut short"),
            "load commands cut short": (whole[:36], "a load command is cut short"),
            "a load command of size 0": (bytes(zero_size), "has a bad size, 0"),
            "a load command past the end": (bytes(too_long), "has a bad size, 4096"),
            "a build version cut short": (short_build_version, "a build version is cut short"),
            "universal header cut short": (b"\xca\xfe\xba\xbe\x00\x00\x00\x02", "the universal header is cut short"),
            "a slice past the end": (bytes(slice_past_the_end), "a slice is cut short"),
        }
        for name, (data, reason) in cases.items():
            with self.subTest(name):
                with self.assertRaises(ValueError) as refused:
                    bundle.MachO(self.write("program", data))
                self.assertIn(reason, str(refused.exception))


class IconTests(BundleTestCase):
    def test_the_bundles_icon_holds_every_size(self):
        self.assertEqual(bundle.check_icon(str(BUNDLE_DIR / "AppIcon.icns")), [])

    def test_a_whole_icon_has_no_problems(self):
        self.assertEqual(bundle.check_icon(self.write("AppIcon.icns", whole_icon())), [])

    def test_a_missing_or_wrong_size_image_is_named(self):
        elements = [(kind, png(size)) for kind, size in bundle.ICON_ELEMENTS.items() if kind != b"ic10"]
        elements[0] = (b"icp4", png(17))
        problems = bundle.check_icon(self.write("AppIcon.icns", icns(elements)))
        self.assertEqual(problems, ["AppIcon.icns icp4 is 17x17, not 16 pixels", "AppIcon.icns has no ic10 image"])

    def test_damaged_icons_are_problems_not_hangs_or_tracebacks(self):
        whole = whole_icon()
        zero_size = bytearray(whole)
        struct.pack_into(">I", zero_size, 8 + 4, 0)
        past_the_end = bytearray(whole)
        struct.pack_into(">I", past_the_end, 8 + 4, len(whole))
        cut_short = whole + b"ic10"
        cut_short = b"icns" + struct.pack(">I", len(cut_short)) + cut_short[8:]
        short_png = icns([(b"icp4", PNG + b"\x00\x00")])
        cases = {
            "empty": (b"", "AppIcon.icns is not an icon file"),
            "a header cut short": (b"icns", "AppIcon.icns is not an icon file"),
            "the wrong length": (whole[:-1], "AppIcon.icns is not an icon file"),
            "an element of size 0": (bytes(zero_size), "at byte 8 has a bad size, 0"),
            "an element past the end": (bytes(past_the_end), f"at byte 8 has a bad size, {len(whole)}"),
            "an element header cut short": (cut_short, f"AppIcon.icns is cut short at byte {len(whole)}"),
            "a PNG cut short": (short_png, "AppIcon.icns icp4 is 0x0, not 16 pixels"),
        }
        for name, (data, reason) in cases.items():
            with self.subTest(name):
                problems = bundle.check_icon(self.write("AppIcon.icns", data))
                self.assertTrue(any(reason in problem for problem in problems), problems)


class CheckTests(BundleTestCase):
    """bundle.py check over a whole bundle, unsigned, as it is made before rcodesign."""

    def make_bundle(self):
        app = self.directory / bundle.APP_NAME
        program = thin([build_version(MACOS_12)])
        for name in [bundle.EXECUTABLE] + bundle.LIBRARIES:
            path = self.write(f"{bundle.APP_NAME}/Contents/MacOS/{name}", program)
            os.chmod(path, 0o755)
        self.write(f"{bundle.APP_NAME}/Contents/Resources/AppIcon.icns", whole_icon())
        self.write(f"{bundle.APP_NAME}/Contents/PkgInfo", b"APPL????")
        self.write(f"{bundle.APP_NAME}/Contents/Info.plist", plistlib.dumps({
            "CFBundleExecutable": bundle.EXECUTABLE, "CFBundleIdentifier": bundle.BUNDLE_ID,
            "CFBundlePackageType": "APPL", "CFBundleIconFile": "AppIcon", "CFBundleShortVersionString": bundle.VERSION,
            "CFBundleVersion": "7", "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0"}))
        return app

    def check(self, app):
        output, errors = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            code = bundle.check(str(app), unsigned=True)
        return code, errors.getvalue()

    def test_a_whole_bundle_passes(self):
        self.assertEqual(self.check(self.make_bundle()), (0, ""))

    def test_a_damaged_program_icon_or_plist_is_reported_with_exit_1(self):
        cases = {
            "a program cut short": (f"Contents/MacOS/{bundle.EXECUTABLE}", thin([build_version(MACOS_12)])[:36],
                                    "a load command is cut short"),
            "an icon element of size 0": ("Contents/Resources/AppIcon.icns",
                                          b"icns" + struct.pack(">I", 16) + b"icp4" + struct.pack(">I", 0),
                                          "has a bad size, 0"),
            "a plist that is not XML": ("Contents/Info.plist", b"<plist><dict>", "Info.plist cannot be read"),
            "a plist that is not a dictionary": ("Contents/Info.plist", plistlib.dumps(["APPL"]),
                                                 "Info.plist is not a dictionary"),
        }
        for name, (path, data, reason) in cases.items():
            with self.subTest(name):
                app = self.make_bundle()
                (app / path).write_bytes(data)
                code, errors = self.check(app)
                self.assertEqual(code, 1)
                self.assertIn(reason, errors)

    def test_a_program_newer_than_the_plist_says_is_reported(self):
        app = self.make_bundle()
        (app / "Contents/MacOS/libSkiaSharp.dylib").write_bytes(thin([build_version(MACOS_13)]))
        code, errors = self.check(app)
        self.assertEqual(code, 1)
        self.assertIn("MacOS/libSkiaSharp.dylib needs macOS 13.0, newer than LSMinimumSystemVersion", errors)


if __name__ == "__main__":
    unittest.main()
