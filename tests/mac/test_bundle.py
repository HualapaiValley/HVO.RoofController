#!/usr/bin/env python3
"""Tests for bundle.py's reading of the Mac app's bundle (#48): Mach-O files, Info.plist and AppIcon.icns, whole and
damaged, and the product version it carries (#62). A damaged file is a problem in check's report (exit 1), or a line naming it when make stops; never a hang or a
traceback. From the repository root:

    python3 -m unittest discover -s tests/mac -p 'test_*.py'
"""

import contextlib
import errno
import importlib.util
import io
import os
import pathlib
import plistlib
import re
import signal
import struct
import tempfile
import unittest
from unittest import mock

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
BUNDLE_DIR = REPOSITORY / "src" / "HVO.RoofControllerV4.Mac" / "bundle"
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


def universal(*slices, wide=False):
    """A universal (fat) file holding the given (cpu, image) slices; wide, in the 64-bit form (0xCAFEBABF), whose
    entries are 32 bytes instead of 20."""
    header = struct.pack(">II", 0xCAFEBABF if wide else 0xCAFEBABE, len(slices))
    offset = 8 + (32 if wide else 20) * len(slices)
    table, images = b"", b""
    for cpu, image in slices:
        at = offset + len(images)
        table += (struct.pack(">iiQQII", cpu, 0, at, len(image), 0, 0) if wide
                  else struct.pack(">iiIII", cpu, 0, at, len(image), 0))
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

    def check(self, app, version=None):
        """bundle.py check of an unsigned bundle: its exit code and what it wrote to stderr."""
        output, errors = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            code = bundle.check(str(app), unsigned=True, version=version)
        return code, errors.getvalue()


class MachOTests(BundleTestCase):
    def test_a_signed_image_says_so_and_names_the_oldest_macos(self):
        image = bundle.MachO(self.write("program", thin([build_version(MACOS_12), signature()])))
        self.assertEqual(image.slices, {bundle.CPU_ARM64: {"signed": True, "minimum": MACOS_12}})

    def test_an_unsigned_image_says_so(self):
        image = bundle.MachO(self.write("program", thin([build_version(MACOS_12)])))
        self.assertFalse(image.slices[bundle.CPU_ARM64]["signed"])

    def test_a_universal_file_gives_each_slice(self):
        x64 = 0x01000007
        for wide in (False, True):
            with self.subTest("64-bit" if wide else "32-bit"):
                image = bundle.MachO(self.write("program", universal(
                    (x64, thin([build_version(MACOS_13)], cpu=x64)),
                    (bundle.CPU_ARM64, thin([build_version(MACOS_12), signature()])), wide=wide)))
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
        wide = universal((bundle.CPU_ARM64, whole), wide=True)
        wide_slice_past_the_end = bytearray(wide)
        struct.pack_into(">Q", wide_slice_past_the_end, 8 + 16, len(whole) + 1)
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
            # 23 of a 64-bit entry's 32 bytes: enough for a 32-bit entry, not for the offset and size it needs.
            "64-bit universal header cut short": (wide[:8 + 23], "the universal header is cut short"),
            "a 64-bit slice past the end": (bytes(wide_slice_past_the_end), "a slice is cut short"),
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
            "CFBundlePackageType": "APPL", "CFBundleIconFile": "AppIcon", "CFBundleShortVersionString": "4.0.0",
            "CFBundleVersion": "7", "NSHighResolutionCapable": True, "LSMinimumSystemVersion": "12.0"}))
        return app

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

    def test_the_version_is_checked_against_the_one_given_or_as_x_y_z(self):
        app = self.make_bundle()
        self.assertEqual(self.check(app, version="4.0.0-ci.12+0123abcd"), (0, ""))
        code, errors = self.check(app, version="4.1.0")
        self.assertEqual(code, 1)
        self.assertIn("Info.plist CFBundleShortVersionString is '4.0.0', not '4.1.0'", errors)
        for short in ["1.0", "4.0.0-ci.12", "v4.0.0", ""]:
            with self.subTest(short):
                plist = plistlib.loads((app / "Contents/Info.plist").read_bytes())
                plist["CFBundleShortVersionString"] = short
                (app / "Contents/Info.plist").write_bytes(plistlib.dumps(plist))
                code, errors = self.check(app)
                self.assertEqual(code, 1)
                self.assertIn(f"Info.plist CFBundleShortVersionString is {short!r}, not X.Y.Z", errors)

    def test_a_program_newer_than_the_plist_says_is_reported(self):
        app = self.make_bundle()
        (app / "Contents/MacOS/libSkiaSharp.dylib").write_bytes(thin([build_version(MACOS_13)]))
        code, errors = self.check(app)
        self.assertEqual(code, 1)
        self.assertIn("MacOS/libSkiaSharp.dylib needs macOS 13.0, newer than LSMinimumSystemVersion", errors)


class MakeTests(BundleTestCase):
    """bundle.py make from a publish folder, without signing."""

    def publish(self, library=None):
        """A publish folder whose program and libraries need macOS 12, with the given (name, data) file instead, or
        without it when its data is None."""
        for name in [bundle.EXECUTABLE] + bundle.LIBRARIES:
            self.write(f"publish/{name}", thin([build_version(MACOS_12)]))
        if library and library[1] is None:
            os.remove(self.directory / "publish" / library[0])
        elif library:
            self.write(f"publish/{library[0]}", library[1])
        return str(self.directory / "publish")

    def make(self, publish, build=7, rcodesign=None, version="4.0.0-ci.7"):
        with contextlib.redirect_stdout(io.StringIO()):
            bundle.make(publish, str(self.directory / "out"), build, rcodesign, version)
        return self.directory / "out" / bundle.APP_NAME

    def rcodesign(self, script):
        """A stand-in for rcodesign: a shell script, given its arguments."""
        path = self.write("rcodesign", b"#!/bin/sh\n" + script.encode() + b"\n")
        os.chmod(path, 0o755)
        return path

    def out(self):
        """What is in the out folder: only the bundle once make is done, never its work folder."""
        return sorted(os.listdir(self.directory / "out"))

    def test_a_whole_publish_makes_a_bundle_that_check_passes(self):
        app = self.make(self.publish(("libSkiaSharp.dylib", thin([build_version(MACOS_13)]))))
        with (app / "Contents/Info.plist").open("rb") as file:
            plist = plistlib.load(file)
        self.assertEqual((plist["CFBundleShortVersionString"], plist["CFBundleVersion"], plist["LSMinimumSystemVersion"]),
                         ("4.0.0", "7", "13.0"))
        self.assertEqual(self.check(app, version="4.0.0-ci.7"), (0, ""))
        self.assertEqual(self.out(), [bundle.APP_NAME])

    def test_the_version_defaults_to_the_product_version_in_directory_build_props(self):
        prefix = re.search(r"<VersionPrefix>([^<]*)</VersionPrefix>", (REPOSITORY / "Directory.Build.props").read_text())
        app = self.make(self.publish(), version=None)
        with (app / "Contents/Info.plist").open("rb") as file:
            self.assertEqual(plistlib.load(file)["CFBundleShortVersionString"], prefix.group(1))

    def test_a_version_that_is_not_a_product_version_stops_make_before_anything_is_made(self):
        for version in ["4.0", "v4.0.0", "4.0.0-", "04.0.0", "4.0.0 beta", ""]:
            with self.subTest(version):
                with self.assertRaises(SystemExit) as stopped:
                    self.make(self.publish(), version=version)
                self.assertEqual(stopped.exception.code,
                                 f"--version: {version!r} is not a product version such as 4.0.0 or 4.0.0-ci.12")
                self.assertFalse((self.directory / "out").exists())

    def test_rcodesign_signs_the_new_bundle_before_it_takes_the_last_ones_place(self):
        signed = self.directory / "signed"
        # What rcodesign was asked to sign, the start of the folder it was in, and whether it was already whole.
        rcodesign = self.rcodesign(
            f'echo "$1 $(basename "$(dirname "$2")" | cut -c1-8) $(basename "$2")'
            f' $(test -f "$2/Contents/Info.plist" && echo whole)" > "{signed}"')
        self.make(self.publish())
        self.make(self.publish(), build=8, rcodesign=rcodesign)
        self.assertEqual(signed.read_text(), f"sign .bundle- {bundle.APP_NAME} whole\n")
        self.assertEqual(self.out(), [bundle.APP_NAME])
        with (self.directory / "out" / bundle.APP_NAME / "Contents/Info.plist").open("rb") as file:
            self.assertEqual(plistlib.load(file)["CFBundleVersion"], "8")

    def test_a_publish_file_make_cannot_use_stops_it_naming_that_file_and_making_no_bundle(self):
        x64 = 0x01000007
        cases = {
            "a library cut short": ("libSkiaSharp.dylib", thin([build_version(MACOS_12)])[:36],
                                    "a load command is cut short"),
            "a program that is not Mach-O": (bundle.EXECUTABLE, b"#!/bin/sh\n", "not a 64-bit Mach-O file"),
            "a library with no arm64 code": ("libHarfBuzzSharp.dylib", thin([build_version(MACOS_12)], cpu=x64),
                                             "has no arm64 code"),
            "a library missing": ("libAvaloniaNative.dylib", None, "missing"),
        }
        for name, (file, data, reason) in cases.items():
            with self.subTest(name):
                publish = self.publish((file, data))
                with self.assertRaises(SystemExit) as stopped:
                    self.make(publish)
                self.assertIsInstance(stopped.exception.code, str)
                self.assertIn(f"{os.path.join(publish, file)}: {reason}", stopped.exception.code)
                self.assertIn("publish again", stopped.exception.code)
                self.assertFalse((self.directory / "out").exists())

    def test_a_failed_make_leaves_the_last_bundle_as_it_was(self):
        left = "the last bundle is left as it was"
        cases = {
            "a damaged library": (("libSkiaSharp.dylib", b""), None, "not a 64-bit Mach-O file"),
            "rcodesign failing": (None, "exit 3", f"rcodesign sign exited 3; {left}"),
            "rcodesign not there": (None, "/nonexistent/rcodesign",
                                    f"/nonexistent/rcodesign: No such file or directory; {left}"),
        }
        for name, (library, rcodesign, reason) in cases.items():
            with self.subTest(name):
                app = self.make(self.publish())
                if rcodesign and not rcodesign.startswith("/"):
                    rcodesign = self.rcodesign(rcodesign)
                with self.assertRaises(SystemExit) as stopped:
                    self.make(self.publish(library), build=8, rcodesign=rcodesign)
                self.assertIn(reason, stopped.exception.code)
                self.assertEqual(self.check(app), (0, ""))
                with (app / "Contents/Info.plist").open("rb") as file:
                    self.assertEqual(plistlib.load(file)["CFBundleVersion"], "7")
                self.assertEqual(self.out(), [bundle.APP_NAME])

    def test_a_swap_that_stops_puts_the_last_bundle_back(self):
        real = os.replace
        left = "the last bundle is left as it was"
        denied = OSError(errno.EACCES, "Permission denied")
        cases = {
            # Which rename stops (the last bundle moving aside, or the new one taking its place), and how.
            "the last bundle cannot move": ("last", denied),
            "the new bundle cannot take its place": ("new", denied),
            "interrupted between the renames": ("new", KeyboardInterrupt()),
        }
        for name, (step, failure) in cases.items():
            with self.subTest(name):
                app = self.make(self.publish())

                def replace(source, target):
                    moving = ("last" if source == str(app)
                              else "new" if os.path.basename(source) == bundle.APP_NAME else None)
                    if moving == step:
                        raise failure
                    real(source, target)

                stops = SystemExit if isinstance(failure, OSError) else KeyboardInterrupt
                with mock.patch.object(bundle.os, "replace", replace), self.assertRaises(stops) as stopped:
                    self.make(self.publish(), build=8)
                if stops is SystemExit:
                    self.assertEqual(stopped.exception.code, f"{app}: Permission denied; {left}")
                self.assertEqual(self.check(app), (0, ""))
                with (app / "Contents/Info.plist").open("rb") as file:
                    self.assertEqual(plistlib.load(file)["CFBundleVersion"], "7")
                self.assertEqual(self.out(), [bundle.APP_NAME])

    @unittest.skipIf(os.geteuid() == 0, "root can delete from a folder that cannot be written")
    def test_a_folder_in_the_last_bundle_that_cannot_be_written_does_not_stop_make(self):
        app = self.make(self.publish())
        os.chmod(app / "Contents/MacOS", 0o555)
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            self.make(self.publish(), build=8)
        self.assertEqual(self.check(app), (0, ""))
        with (app / "Contents/Info.plist").open("rb") as file:
            self.assertEqual(plistlib.load(file)["CFBundleVersion"], "8")
        # What is left of the last bundle is named, to be deleted by hand.
        leftover = [name for name in self.out() if name != bundle.APP_NAME]
        self.assertEqual(len(leftover), 1)
        self.assertIn(f"{self.directory / 'out' / leftover[0]}: could not all be deleted", errors.getvalue())
        os.chmod(self.directory / "out" / leftover[0] / "last/Contents/MacOS", 0o755)

    def test_the_bundle_can_be_made_again_from_its_own_program_folder(self):
        app = self.make(self.publish())
        self.make(str(app / "Contents/MacOS"), build=8)
        self.assertEqual(self.check(app), (0, ""))
        with (app / "Contents/Info.plist").open("rb") as file:
            self.assertEqual(plistlib.load(file)["CFBundleVersion"], "8")


if __name__ == "__main__":
    unittest.main()
