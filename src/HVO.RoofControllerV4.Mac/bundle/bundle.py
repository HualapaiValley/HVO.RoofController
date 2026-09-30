#!/usr/bin/env python3
"""Makes and checks the Mac app's bundle, HVO Roof.app, on Linux or a Mac, with the standard library and rcodesign
(https://github.com/indygreg/apple-platform-rs) to sign it. No Xcode.

    bundle.py make <publish dir> <out dir> [--version X.Y.Z[-suffix]] [--build N] [--rcodesign PATH]
    bundle.py check <HVO Roof.app> [--version X.Y.Z[-suffix]] [--unsigned]

make: the program and native libraries from `dotnet publish -r osx-arm64` go in Contents/MacOS, the icon in
Contents/Resources, and Info.plist (the template beside this script) gets the version, the build number and the oldest
macOS that every Mach-O file in the bundle runs on. The version is the product version the app was published with
(docs/releasing.md), by default the VersionPrefix in Directory.Build.props; macOS shows only its X.Y.Z
(CFBundleShortVersionString), so a prerelease such as 4.0.0-ci.12 is told apart by the build number (CFBundleVersion).
With --rcodesign the bundle is then signed ad hoc: with no certificate, which is enough for your own Macs (docs/mac.md).

check: reads back what make promises: Info.plist's keys (with --version, the X.Y.Z of that version), the program and
native libraries built for arm64 and (unless --unsigned) each carrying a code signature, the icon's images, and nothing
else in the bundle. It exits 1 with a message per problem.
"""
import argparse
import os
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
APP_NAME = "HVO Roof.app"
EXECUTABLE = "hvo-roof-mac"
# A product version: X.Y.Z, then an optional prerelease (-ci.12) and commit (+0123abcd), as the assemblies carry it.
PRODUCT_VERSION = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")
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


def read(layout, data, at, what):
    """struct.unpack_from, or ValueError saying what is cut short: a damaged file is a problem to report, not a crash."""
    if at < 0 or at + struct.calcsize(layout) > len(data):
        raise ValueError(f"{what} is cut short")
    return struct.unpack_from(layout, data, at)


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
            count = read(">I", data, 4, f"{path}: the universal header")[0]
            entry = 32 if wide else 20
            for index in range(count):
                cpu, _, offset, size = read(">iiQQ" if wide else ">iiII", data, 8 + index * entry,
                                            f"{path}: the universal header")
                if offset + size > len(data):
                    raise ValueError(f"{path}: a slice is cut short")
                self.slices[cpu & 0xFFFFFFFF] = self._thin(data[offset:offset + size], path)
        elif data[:4] == b"\xcf\xfa\xed\xfe":
            cpu = read("<I", data, 4, f"{path}: the Mach-O header")[0]
            self.slices[cpu] = self._thin(data, path)
        else:
            raise ValueError(f"{path}: not a 64-bit Mach-O file")

    @staticmethod
    def _thin(data, path):
        if data[:4] != b"\xcf\xfa\xed\xfe":
            raise ValueError(f"{path}: a slice is not a 64-bit little-endian Mach-O image")
        count = read("<I", data, 16, f"{path}: the Mach-O header")[0]
        at, signed, minimum = 32, False, None
        for _ in range(count):
            command, size = read("<II", data, at, f"{path}: a load command")
            if size < 8 or at + size > len(data):
                raise ValueError(f"{path}: the load command at byte {at} has a bad size, {size}")
            body = data[at:at + size]
            if command == LC_CODE_SIGNATURE:
                signed = True
            elif command == LC_BUILD_VERSION:
                platform, version = read("<II", body, 8, f"{path}: a build version")
                if platform == PLATFORM_MACOS:
                    minimum = version
            elif command == LC_VERSION_MIN_MACOSX:
                minimum = read("<I", body, 8, f"{path}: a minimum version")[0]
            at += size
        return {"signed": signed, "minimum": minimum}


def version_text(encoded):
    """xxxx.yy.zz, as Mach-O load commands hold a version, as text: 12.0 or 13.3.1."""
    major, minor, patch = encoded >> 16, (encoded >> 8) & 0xFF, encoded & 0xFF
    return f"{major}.{minor}.{patch}" if patch else f"{major}.{minor}"


def product_version():
    """The VersionPrefix in the repository's Directory.Build.props (docs/releasing.md)."""
    directory = HERE
    while not os.path.isfile(os.path.join(directory, "Directory.Build.props")):
        parent = os.path.dirname(directory)
        if parent == directory:
            sys.exit(f"{HERE}: no Directory.Build.props above it; give the version with --version")
        directory = parent
    with open(os.path.join(directory, "Directory.Build.props"), encoding="utf-8") as file:
        prefixes = re.findall(r"<VersionPrefix>([^<]*)</VersionPrefix>", file.read())
    if len(prefixes) != 1:
        sys.exit(f"{directory}/Directory.Build.props: holds {len(prefixes)} VersionPrefix, not 1; "
                 "give the version with --version")
    return prefixes[0].strip()


def short_version(version):
    """The X.Y.Z of a product version, which CFBundleShortVersionString holds: 4.0.0 for 4.0.0-ci.12+0123abcd."""
    match = PRODUCT_VERSION.match(version)
    if not match:
        raise ValueError(f"{version!r} is not a product version such as 4.0.0 or 4.0.0-ci.12")
    return ".".join(match.groups()[:3])


def make(publish, out, build, rcodesign, version=None):
    try:
        short = short_version(product_version() if version is None else version)
    except ValueError as error:
        sys.exit(f"--version: {error}")

    # The program and the native libraries it loads; nothing else from the publish folder (no symbols, no settings).
    # Each is checked first, so a file make cannot use is named where it was published.
    publish_again = "publish again with: dotnet publish HVO.RoofControllerV4.Mac -c Release -r osx-arm64"
    minimum = 0
    for name in [EXECUTABLE] + LIBRARIES:
        source = os.path.join(publish, name)
        if not os.path.isfile(source):
            sys.exit(f"{source}: missing; {publish_again}")
        try:
            arm = MachO(source).slices.get(CPU_ARM64)
        except ValueError as error:
            sys.exit(f"{error}; {publish_again}")
        if arm is None:
            sys.exit(f"{source}: has no arm64 code; {publish_again}")
        minimum = max(minimum, arm["minimum"] or 0)

    # The new bundle is built and signed in a folder beside the last one, which it replaces only when whole: a make
    # that stops at any step leaves the last bundle as it was (even when the publish folder is inside it).
    app = os.path.join(out, APP_NAME)
    os.makedirs(out, exist_ok=True)
    work = tempfile.mkdtemp(prefix=".bundle-", dir=out)
    new, last = os.path.join(work, APP_NAME), os.path.join(work, "last")
    try:
        macos = os.path.join(new, "Contents", "MacOS")
        resources = os.path.join(new, "Contents", "Resources")
        os.makedirs(macos)
        os.makedirs(resources)
        for name in [EXECUTABLE] + LIBRARIES:
            shutil.copyfile(os.path.join(publish, name), os.path.join(macos, name))
            os.chmod(os.path.join(macos, name), 0o755)
        shutil.copyfile(os.path.join(HERE, "AppIcon.icns"), os.path.join(resources, "AppIcon.icns"))
        with open(os.path.join(HERE, "Info.plist"), encoding="utf-8") as file:
            plist = file.read()
        plist = (plist.replace("@VERSION@", short).replace("@BUILD@", str(build))
                 .replace("@MINIMUM_SYSTEM@", version_text(minimum) if minimum else "12.0"))
        with open(os.path.join(new, "Contents", "Info.plist"), "w", encoding="utf-8") as file:
            file.write(plist)
        with open(os.path.join(new, "Contents", "PkgInfo"), "w", encoding="ascii") as file:
            file.write("APPL????")

        if rcodesign:
            # Ad hoc: no certificate. rcodesign signs the libraries in place, then the program and the bundle.
            try:
                subprocess.run([rcodesign, "sign", new], check=True)
            except OSError as error:
                sys.exit(f"{rcodesign}: {error.strerror}; the last bundle is left as it was")
            except subprocess.CalledProcessError as error:
                sys.exit(f"{rcodesign} sign exited {error.returncode}; the last bundle is left as it was")

        # Two renames, so the last bundle is never partly deleted: it moves into the work folder, then the new one takes
        # its place. It is deleted with the work folder only once the new one is there (a kill between the two renames
        # would leave it in the work folder).
        try:
            if os.path.lexists(app):
                os.replace(app, last)
            os.replace(new, app)
        except OSError as error:
            sys.exit(f"{app}: {error.strerror}; the last bundle is left as it was")
    finally:
        # Whatever stopped make, the last bundle goes back if the new one is not in its place.
        if os.path.lexists(last) and not os.path.lexists(app):
            os.replace(last, app)
        shutil.rmtree(work, ignore_errors=True)
        if os.path.lexists(work):
            print(f"{work}: could not all be deleted (a folder in the last bundle that cannot be written?); "
                  "delete it yourself", file=sys.stderr)
    print(app)


def check(app, unsigned, version=None):
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
    minimum = None
    if os.path.isfile(plist_path):
        plist_problems, minimum = check_plist(plist_path, version)
        problems.extend(plist_problems)

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


def check_plist(path, version=None):
    """Info.plist's keys, as problems, and the oldest macOS it names (as a Mach-O version), or None. With a version,
    CFBundleShortVersionString must be its X.Y.Z; without one, any X.Y.Z."""
    try:
        with open(path, "rb") as file:
            plist = plistlib.load(file)
    except Exception as error:  # plistlib raises several kinds, expat's among them
        return [f"Info.plist cannot be read: {error}"], None
    if not isinstance(plist, dict):
        return ["Info.plist is not a dictionary"], None
    problems = []
    wanted = {"CFBundleExecutable": EXECUTABLE, "CFBundleIdentifier": BUNDLE_ID, "CFBundlePackageType": "APPL",
              "CFBundleIconFile": "AppIcon", "NSHighResolutionCapable": True}
    if version is not None:
        wanted["CFBundleShortVersionString"] = short_version(version)
    for key, value in wanted.items():
        if plist.get(key) != value:
            problems.append(f"Info.plist {key} is {plist.get(key)!r}, not {value!r}")
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", str(plist.get("CFBundleShortVersionString", ""))):
        short = plist.get("CFBundleShortVersionString")
        problems.append(f"Info.plist CFBundleShortVersionString is {short!r}, not X.Y.Z")
    if not str(plist.get("CFBundleVersion", "")).isdigit():
        problems.append(f"Info.plist CFBundleVersion is {plist.get('CFBundleVersion')!r}, not a build number")
    minimum_text = str(plist.get("LSMinimumSystemVersion", ""))
    try:
        parts = [int(part) for part in minimum_text.split(".")] + [0, 0]
        return problems, (parts[0] << 16) | (parts[1] << 8) | parts[2]
    except ValueError:
        problems.append(f"Info.plist LSMinimumSystemVersion is {minimum_text!r}, not a macOS version")
        return problems, None


def check_icon(path):
    with open(path, "rb") as file:
        data = file.read()
    if len(data) < 8 or data[:4] != b"icns" or struct.unpack(">I", data[4:8])[0] != len(data):
        return ["AppIcon.icns is not an icon file"]
    problems, seen, at = [], set(), 8
    while at < len(data):
        if at + 8 > len(data):
            problems.append(f"AppIcon.icns is cut short at byte {at}")
            break
        kind, size = data[at:at + 4], struct.unpack(">I", data[at + 4:at + 8])[0]
        if size < 8 or at + size > len(data):
            problems.append(f"AppIcon.icns element {kind.decode('latin-1')!r} at byte {at} has a bad size, {size}")
            break
        image = data[at + 8:at + size]
        if kind in ICON_ELEMENTS:
            seen.add(kind)
            png = image[:8] == b"\x89PNG\r\n\x1a\n" and len(image) >= 24
            width, height = struct.unpack(">II", image[16:24]) if png else (0, 0)
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
    make_command.add_argument("--version")
    make_command.add_argument("--build", type=int, default=1)
    make_command.add_argument("--rcodesign")
    check_command = commands.add_parser("check")
    check_command.add_argument("app")
    check_command.add_argument("--version")
    check_command.add_argument("--unsigned", action="store_true")
    args = parser.parse_args()
    if args.command == "make":
        make(args.publish, args.out, args.build, args.rcodesign, args.version)
        return 0
    if args.version is not None:
        try:
            short_version(args.version)
        except ValueError as error:
            parser.error(f"--version: {error}")
    return check(args.app, args.unsigned, args.version)


if __name__ == "__main__":
    sys.exit(main())
