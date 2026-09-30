#!/usr/bin/env python3
"""Makes and checks the Mac app's bundle, HVO Roof.app, on Linux or a Mac, with the standard library and rcodesign
(https://github.com/indygreg/apple-platform-rs) to sign it. No Xcode.

    bundle.py make <publish dir> <out dir> [--build N] [--rcodesign PATH]
    bundle.py check <HVO Roof.app> [--unsigned]

make: the program and native libraries from `dotnet publish -r osx-arm64` go in Contents/MacOS, the icon in
Contents/Resources, and Info.plist (the template beside this script) gets the version, the build number and the oldest
macOS that every Mach-O file in the bundle runs on. With --rcodesign the bundle is then signed ad hoc: with no
certificate, which is enough for your own Macs (docs/mac.md).

check: reads back what make promises: Info.plist's keys, the program and native libraries built for arm64 and (unless
--unsigned) each carrying a code signature, the icon's images, and nothing else in the bundle. It exits 1 with a
message per problem.
"""
import argparse
import os
import plistlib
import shutil
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP_NAME = "HVO Roof.app"
EXECUTABLE = "hvo-roof-mac"
VERSION = "1.0.0"
BUNDLE_ID = "io.github.hualapaivalley.roof"
# The native libraries the program loads from beside it: Avalonia's macOS windowing, Skia and HarfBuzz.
LIBRARIES = ["libAvaloniaNative.dylib", "libHarfBuzzSharp.dylib", "libSkiaSharp.dylib"]
# The icon's elements (make-icns.py) and the pixel size of each.
ICON_ELEMENTS = {b"icp4": 16, b"icp5": 32, b"ic11": 32, b"icp6": 64, b"ic12": 64, b"ic07": 128, b"ic08": 256,
                 b"ic13": 256, b"ic09": 512, b"ic14": 512, b"ic10": 1024}

CPU_ARM64 = 0x0100000C
LC_CODE_SIGNATURE = 0x1D
LC_VERSION_MIN_MACOSX = 0x24
LC_BUILD_VERSION = 0x32
PLATFORM_MACOS = 1


class MachO:
    """What check and make need from a Mach-O file: its architectures, and for each whether it is signed and the
    oldest macOS it runs on."""

    def __init__(self, path):
        with open(path, "rb") as file:
            data = file.read()
        self.slices = {}
        magic = struct.unpack(">I", data[:4])[0] if len(data) >= 4 else 0
        if magic in (0xCAFEBABE, 0xCAFEBABF):  # universal: a big-endian table of slices
            wide = magic == 0xCAFEBABF
            count = struct.unpack(">I", data[4:8])[0]
            entry = 32 if wide else 20
            for index in range(count):
                start = 8 + index * entry
                if wide:
                    cpu, _, offset, size = struct.unpack(">iiQQ", data[start:start + 24])
                else:
                    cpu, _, offset, size = struct.unpack(">iiII", data[start:start + 16])
                self.slices[cpu & 0xFFFFFFFF] = self._thin(data[offset:offset + size], path)
        elif data[:4] == b"\xcf\xfa\xed\xfe":
            cpu = struct.unpack("<I", data[4:8])[0]
            self.slices[cpu] = self._thin(data, path)
        else:
            raise ValueError(f"{path}: not a 64-bit Mach-O file")

    @staticmethod
    def _thin(data, path):
        if data[:4] != b"\xcf\xfa\xed\xfe":
            raise ValueError(f"{path}: a slice is not a 64-bit little-endian Mach-O image")
        count = struct.unpack("<I", data[16:20])[0]
        at, signed, minimum = 32, False, None
        for _ in range(count):
            command, size = struct.unpack("<II", data[at:at + 8])
            if command == LC_CODE_SIGNATURE:
                signed = True
            elif command == LC_BUILD_VERSION:
                platform, version = struct.unpack("<II", data[at + 8:at + 16])
                if platform == PLATFORM_MACOS:
                    minimum = version
            elif command == LC_VERSION_MIN_MACOSX:
                minimum = struct.unpack("<I", data[at + 8:at + 12])[0]
            at += size
        return {"signed": signed, "minimum": minimum}


def version_text(encoded):
    """xxxx.yy.zz, as Mach-O load commands hold a version, as text: 12.0 or 13.3.1."""
    major, minor, patch = encoded >> 16, (encoded >> 8) & 0xFF, encoded & 0xFF
    return f"{major}.{minor}.{patch}" if patch else f"{major}.{minor}"


def make(publish, out, build, rcodesign):
    app = os.path.join(out, APP_NAME)
    shutil.rmtree(app, ignore_errors=True)
    macos = os.path.join(app, "Contents", "MacOS")
    resources = os.path.join(app, "Contents", "Resources")
    os.makedirs(macos)
    os.makedirs(resources)
    # The program and the native libraries it loads; nothing else from the publish folder (no symbols, no settings).
    for name in [EXECUTABLE] + LIBRARIES:
        source = os.path.join(publish, name)
        if not os.path.isfile(source):
            sys.exit(f"{source}: missing; publish with: dotnet publish HVO.RoofControllerV4.Mac -c Release -r osx-arm64")
        shutil.copyfile(source, os.path.join(macos, name))
        os.chmod(os.path.join(macos, name), 0o755)
    shutil.copyfile(os.path.join(HERE, "AppIcon.icns"), os.path.join(resources, "AppIcon.icns"))

    minimum = 0
    for name in [EXECUTABLE] + LIBRARIES:
        arm = MachO(os.path.join(macos, name)).slices.get(CPU_ARM64)
        if arm is None:
            sys.exit(f"{name}: has no arm64 code; publish with -r osx-arm64")
        minimum = max(minimum, arm["minimum"] or 0)
    with open(os.path.join(HERE, "Info.plist"), encoding="utf-8") as file:
        plist = file.read()
    plist = (plist.replace("@VERSION@", VERSION).replace("@BUILD@", str(build))
             .replace("@MINIMUM_SYSTEM@", version_text(minimum) if minimum else "12.0"))
    with open(os.path.join(app, "Contents", "Info.plist"), "w", encoding="utf-8") as file:
        file.write(plist)
    with open(os.path.join(app, "Contents", "PkgInfo"), "w", encoding="ascii") as file:
        file.write("APPL????")

    if rcodesign:
        # Ad hoc: no certificate. rcodesign signs the libraries in place, then the program and the bundle.
        subprocess.run([rcodesign, "sign", app], check=True)
    print(app)


def check(app, unsigned):
    problems = []
    contents = os.path.join(app, "Contents")
    expected = {"Info.plist", "PkgInfo", "MacOS/" + EXECUTABLE, "Resources/AppIcon.icns"}
    expected.update("MacOS/" + name for name in LIBRARIES)
    if not unsigned:
        expected.add("_CodeSignature/CodeResources")
    found = set()
    for root, _, files in os.walk(contents):
        for name in files:
            found.add(os.path.relpath(os.path.join(root, name), contents).replace(os.sep, "/"))
    for missing in sorted(expected - found):
        problems.append(f"Contents/{missing} is missing")
    for extra in sorted(found - expected):
        problems.append(f"Contents/{extra} should not be in the bundle")

    plist_path = os.path.join(contents, "Info.plist")
    if os.path.isfile(plist_path):
        with open(plist_path, "rb") as file:
            plist = plistlib.load(file)
        wanted = {"CFBundleExecutable": EXECUTABLE, "CFBundleIdentifier": BUNDLE_ID, "CFBundlePackageType": "APPL",
                  "CFBundleIconFile": "AppIcon", "CFBundleShortVersionString": VERSION, "NSHighResolutionCapable": True}
        for key, value in wanted.items():
            if plist.get(key) != value:
                problems.append(f"Info.plist {key} is {plist.get(key)!r}, not {value!r}")
        if not str(plist.get("CFBundleVersion", "")).isdigit():
            problems.append(f"Info.plist CFBundleVersion is {plist.get('CFBundleVersion')!r}, not a build number")
        minimum_text = str(plist.get("LSMinimumSystemVersion", ""))
        try:
            parts = [int(part) for part in minimum_text.split(".")] + [0, 0]
            minimum = (parts[0] << 16) | (parts[1] << 8) | parts[2]
        except ValueError:
            problems.append(f"Info.plist LSMinimumSystemVersion is {minimum_text!r}, not a macOS version")
            minimum = None
    else:
        minimum = None

    for name in [EXECUTABLE] + LIBRARIES:
        path = os.path.join(contents, "MacOS", name)
        if not os.path.isfile(path):
            continue
        if not os.access(path, os.X_OK):
            problems.append(f"MacOS/{name} is not executable")
        try:
            arm = MachO(path).slices.get(CPU_ARM64)
        except ValueError as error:
            problems.append(str(error))
            continue
        if arm is None:
            problems.append(f"MacOS/{name} has no arm64 code")
            continue
        if not unsigned and not arm["signed"]:
            problems.append(f"MacOS/{name} has no code signature")
        if minimum is not None and arm["minimum"] and arm["minimum"] > minimum:
            problems.append(f"MacOS/{name} needs macOS {version_text(arm['minimum'])}, newer than LSMinimumSystemVersion")

    icon = os.path.join(contents, "Resources", "AppIcon.icns")
    if os.path.isfile(icon):
        problems.extend(check_icon(icon))

    for problem in problems:
        print(f"{APP_NAME}: {problem}", file=sys.stderr)
    if problems:
        return 1
    print(f"{app}: bundle checked{' (unsigned)' if unsigned else ''}")
    return 0


def check_icon(path):
    with open(path, "rb") as file:
        data = file.read()
    if data[:4] != b"icns" or struct.unpack(">I", data[4:8])[0] != len(data):
        return ["AppIcon.icns is not an icon file"]
    problems, seen, at = [], set(), 8
    while at < len(data):
        kind, size = data[at:at + 4], struct.unpack(">I", data[at + 4:at + 8])[0]
        image = data[at + 8:at + size]
        if kind in ICON_ELEMENTS:
            seen.add(kind)
            width, height = struct.unpack(">II", image[16:24]) if image[:8] == b"\x89PNG\r\n\x1a\n" else (0, 0)
            if (width, height) != (ICON_ELEMENTS[kind],) * 2:
                problems.append(f"AppIcon.icns {kind.decode()} is {width}x{height}, not {ICON_ELEMENTS[kind]} pixels")
        at += size
    for kind in sorted(set(ICON_ELEMENTS) - seen):
        problems.append(f"AppIcon.icns has no {kind.decode()} image")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    make_command = commands.add_parser("make")
    make_command.add_argument("publish")
    make_command.add_argument("out")
    make_command.add_argument("--build", type=int, default=1)
    make_command.add_argument("--rcodesign")
    check_command = commands.add_parser("check")
    check_command.add_argument("app")
    check_command.add_argument("--unsigned", action="store_true")
    args = parser.parse_args()
    if args.command == "make":
        make(args.publish, args.out, args.build, args.rcodesign)
        return 0
    return check(args.app, args.unsigned)


if __name__ == "__main__":
    sys.exit(main())
