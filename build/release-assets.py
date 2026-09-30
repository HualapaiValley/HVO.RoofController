#!/usr/bin/env python3
"""A release's assets (docs/releasing.md), gathered into one folder from what CI built and tested, with release.json
and SHA256SUMS.

  build/release-assets.py --version <version> --commit <sha> --created <time>
                          --controller-digest sha256:<hex> --emulator-digest sha256:<hex>
                          --cli <folder> --kiosk <folder> --mac-zip <file> [--registry <registry>/<owner>] -o <folder>

--cli holds <runtime>/hvo-roof for each runtime in CLI_RUNTIMES (CI's hvo-roof artifact), --kiosk the kiosk's files
(CI's hvo-roof-kiosk artifact) and --mac-zip is CI's HVO Roof.app zip. Each must be exactly what this script expects,
for the right processor, and the output folder must be new or empty, so a release holds nothing stale and nothing
missing. The images are <registry>/roof-controller and <registry>/roof-hat-emulator, on GHCR unless --registry names
another (a test's local registry). The assets:

  hvo-roof-<runtime>                          hvo-roof, one file per runtime (linux-arm64, linux-x64, osx-arm64)
  hvo-roof-kiosk-<version>-linux-arm64.tar.gz the kiosk's program, unit, udev rule and example settings, in one folder
  HVO-Roof-<version>.zip                      HVO Roof.app, signed ad hoc
  docker-compose.yaml                         the release compose file, each image pinned to its digest
  deploy-roofcontroller-rpi.sh                the deploy script
  release.json                                the version, the commit, each image's digest and each asset's SHA-256
  SHA256SUMS                                  every asset's SHA-256, release.json's included, as sha256sum writes it
"""

import argparse
import datetime
import gzip
import hashlib
import importlib.util
import io
import json
import plistlib
import re
import shutil
import struct
import sys
import tarfile
import zipfile
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEPLOY_SCRIPT = REPOSITORY_ROOT / "src" / "HVO.RoofControllerV4.RPi" / "deploy-roofcontroller-rpi.sh"
REPOSITORY_URL = "https://github.com/HualapaiValley/HVO.RoofController"
IMAGE_PLATFORMS = ["linux/amd64", "linux/arm64"]

_spec = importlib.util.spec_from_file_location("release_compose", REPOSITORY_ROOT / "build" / "release-compose.py")
release_compose = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_compose)

# The runtimes hvo-roof is released for, and the processor each file must be built for.
CLI_RUNTIMES = {"linux-arm64": "aarch64", "linux-x64": "x86-64", "osx-arm64": "macos-arm64"}

# The kiosk's files and their modes: the program, and docs/kiosk.md's deploy files beside it.
KIOSK_FILES = {
    "hvo-roof-kiosk": 0o755,
    "hvo-roof-kiosk.service": 0o644,
    "99-hvo-roof-kiosk-backlight.rules": 0o644,
    "appsettings.Local.example.json": 0o644,
}

MAC_APP = "HVO Roof.app"
MAC_PROGRAM = f"{MAC_APP}/Contents/MacOS/hvo-roof-mac"

COMMIT = re.compile(r"^[0-9a-f]{40}$")
CREATED = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

# ELF e_machine values, and the Mach-O 64-bit magic with the arm64 CPU type.
ELF_MACHINES = {183: "aarch64", 62: "x86-64"}
MACHO_ARM64 = (0xFEEDFACF, 0x0100000C)


class AssetError(Exception):
    pass


def processor(path):
    """The processor a program file is built for: aarch64 or x86-64 for ELF, macos-arm64 for Mach-O, else None."""
    with open(path, "rb") as file:
        header = file.read(20)
    if header[:4] == b"\x7fELF" and len(header) >= 20:
        order = "<" if header[5] == 1 else ">"
        return ELF_MACHINES.get(struct.unpack(order + "H", header[18:20])[0])
    if len(header) >= 8 and struct.unpack("<II", header[:8]) == MACHO_ARM64:
        return "macos-arm64"
    return None


def exact_entries(folder, expected, what):
    """Fails unless the folder holds exactly the expected names."""
    if not folder.is_dir():
        raise AssetError(f"{what} {folder} is not a folder.")
    found = {entry.name for entry in folder.iterdir()}
    missing, extra = sorted(set(expected) - found), sorted(found - set(expected))
    if missing or extra:
        raise AssetError(
            f"{what} {folder} must hold exactly {', '.join(sorted(expected))}"
            + (f"; it lacks {', '.join(missing)}" if missing else "")
            + (f"; it also holds {', '.join(extra)}, which this script does not release (add it to"
               " build/release-assets.py)" if extra else "")
            + ".")


def cli_assets(cli, out):
    exact_entries(cli, CLI_RUNTIMES, "The hvo-roof folder")
    names = []
    for runtime, expected in CLI_RUNTIMES.items():
        program = cli / runtime / "hvo-roof"
        if not program.is_file():
            raise AssetError(f"{program} was not found.")
        actual = processor(program)
        if actual != expected:
            raise AssetError(f"{program} is built for {actual or 'no known processor'}, not {expected}.")
        name = f"hvo-roof-{runtime}"
        shutil.copyfile(program, out / name)
        (out / name).chmod(0o755)
        names.append(name)
    return names


def kiosk_asset(kiosk, out, version, mtime):
    """The kiosk's files in one folder, in a tarball that is the same for the same files: sorted, owned by root, with
    fixed modes and the release's time."""
    exact_entries(kiosk, KIOSK_FILES, "The kiosk folder")
    program = kiosk / "hvo-roof-kiosk"
    if processor(program) != "aarch64":
        raise AssetError(f"{program} is built for {processor(program) or 'no known processor'}, not aarch64.")
    folder = f"hvo-roof-kiosk-{version}-linux-arm64"
    name = f"{folder}.tar.gz"
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w", format=tarfile.PAX_FORMAT) as tar:
        def add(info, data=None):
            info.uid = info.gid = 0
            info.uname = info.gname = "root"
            info.mtime = mtime
            tar.addfile(info, data)

        directory = tarfile.TarInfo(folder)
        directory.type, directory.mode = tarfile.DIRTYPE, 0o755
        add(directory)
        for file_name in sorted(KIOSK_FILES):
            data = (kiosk / file_name).read_bytes()
            info = tarfile.TarInfo(f"{folder}/{file_name}")
            info.size, info.mode = len(data), KIOSK_FILES[file_name]
            add(info, io.BytesIO(data))
    with open(out / name, "wb") as file, gzip.GzipFile(filename="", mode="wb", fileobj=file, mtime=mtime) as archive:
        archive.write(buffer.getvalue())
    return name


def mac_asset(mac_zip, out, version):
    """HVO Roof.app's zip as CI made, signed and checked it, renamed for the release; its Info.plist must carry this
    version (CFBundleShortVersionString holds its X.Y.Z)."""
    try:
        with zipfile.ZipFile(mac_zip) as archive:
            names = archive.namelist()
            if any(not entry.startswith(MAC_APP + "/") for entry in names) or MAC_PROGRAM not in names:
                raise AssetError(f"{mac_zip} must hold {MAC_APP} alone, with {MAC_PROGRAM}.")
            info = plistlib.loads(archive.read(f"{MAC_APP}/Contents/Info.plist"))
            program = archive.read(MAC_PROGRAM)[:8]
    except (OSError, KeyError, zipfile.BadZipFile, plistlib.InvalidFileException) as error:
        raise AssetError(f"{mac_zip} is not HVO Roof.app's zip: {error}") from error
    short = version.split("-", 1)[0]
    if info.get("CFBundleShortVersionString") != short:
        raise AssetError(f"{mac_zip} holds version {info.get('CFBundleShortVersionString')!r}, not {short}.")
    if len(program) < 8 or struct.unpack("<II", program) != MACHO_ARM64:
        raise AssetError(f"{mac_zip}'s {MAC_PROGRAM} is not an arm64 Mac program.")
    name = f"HVO-Roof-{version}.zip"
    shutil.copyfile(mac_zip, out / name)
    return name


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as file:
        for block in iter(lambda: file.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def manifest(out, assets, version, commit, created, registry, digests):
    """release.json: what the installer and install.sh read to know the release, its images and its files."""
    images = {}
    for key, image in (("controller", "roof-controller"), ("hatEmulator", "roof-hat-emulator")):
        repository = f"{registry}/{image}"
        images[key] = {
            "repository": repository,
            "tag": version,
            "digest": digests[key],
            "reference": f"{repository}:{version}@{digests[key]}",
            "platforms": IMAGE_PLATFORMS,
        }
    return {
        "schemaVersion": 1,
        "product": "HVO Roof Controller",
        "version": version,
        "tag": f"v{version}",
        "prerelease": "-" in version,
        "commit": commit,
        "created": created,
        "repository": REPOSITORY_URL,
        "images": images,
        "assets": [
            {"name": name, "kind": kind, "platform": platform, "size": (out / name).stat().st_size,
             "sha256": sha256(out / name)}
            for name, (kind, platform) in sorted(assets.items())
        ],
    }


def created_time(created):
    """The release's time as seconds since 1970, from 2026-10-01T12:00:00Z."""
    return int(datetime.datetime.strptime(created, "%Y-%m-%dT%H:%M:%SZ")
               .replace(tzinfo=datetime.timezone.utc).timestamp())


def build(arguments):
    out = arguments.output
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        raise AssetError(f"{out} must be a new or empty folder.")
    out.mkdir(parents=True, exist_ok=True)
    version = arguments.version
    mtime = created_time(arguments.created)

    assets = {}
    for name in cli_assets(arguments.cli, out):
        assets[name] = ("cli", name.removeprefix("hvo-roof-"))
    assets[kiosk_asset(arguments.kiosk, out, version, mtime)] = ("kiosk", "linux-arm64")
    assets[mac_asset(arguments.mac_zip, out, version)] = ("mac-app", "osx-arm64")

    try:
        compose = release_compose.convert(release_compose.SOURCE.read_text(encoding="utf-8"), version,
                                          registry=arguments.registry,
                                          controller_digest=arguments.controller_digest,
                                          emulator_digest=arguments.emulator_digest)
    except release_compose.ConversionError as error:
        raise AssetError(f"{release_compose.SOURCE}: {error}") from error
    (out / "docker-compose.yaml").write_text(compose, encoding="utf-8")
    assets["docker-compose.yaml"] = ("compose", None)
    shutil.copyfile(DEPLOY_SCRIPT, out / DEPLOY_SCRIPT.name)
    (out / DEPLOY_SCRIPT.name).chmod(0o755)
    assets[DEPLOY_SCRIPT.name] = ("deploy-script", None)

    digests = {"controller": arguments.controller_digest, "hatEmulator": arguments.emulator_digest}
    release = manifest(out, assets, version, arguments.commit, arguments.created, arguments.registry, digests)
    (out / "release.json").write_text(json.dumps(release, indent=2) + "\n", encoding="utf-8")

    sums = "".join(f"{sha256(out / name)}  {name}\n" for name in sorted([*assets, "release.json"]))
    (out / "SHA256SUMS").write_text(sums, encoding="utf-8")
    return sorted([*assets, "release.json", "SHA256SUMS"])


def main(argv=None):
    parser = argparse.ArgumentParser(description="Gathers a release's assets, with release.json and SHA256SUMS.")
    parser.add_argument("--version", required=True, help="the release's product version, such as 4.0.0 or 4.0.0-rc.1")
    parser.add_argument("--commit", required=True, help="the commit the release is built from (40 hex digits)")
    parser.add_argument("--created", required=True, help="when the release was built, such as 2026-10-01T12:00:00Z")
    parser.add_argument("--controller-digest", required=True, help="the controller image's digest (sha256:<hex>)")
    parser.add_argument("--emulator-digest", required=True, help="the HAT emulator image's digest (sha256:<hex>)")
    parser.add_argument("--cli", required=True, type=Path, help="CI's hvo-roof artifact: <runtime>/hvo-roof")
    parser.add_argument("--kiosk", required=True, type=Path, help="CI's hvo-roof-kiosk artifact")
    parser.add_argument("--mac-zip", required=True, type=Path, help="CI's HVO Roof.app zip")
    parser.add_argument("--registry", default=release_compose.REGISTRY,
                        help=f"where the images are, default {release_compose.REGISTRY}")
    parser.add_argument("-o", "--output", required=True, type=Path, help="the folder to write, new or empty")
    arguments = parser.parse_args(argv)

    if not release_compose.VERSION.match(arguments.version):
        parser.error(f"--version must be a product version such as 4.0.0 or 4.0.0-rc.1, not {arguments.version!r}")
    if not COMMIT.match(arguments.commit):
        parser.error(f"--commit must be a whole commit hash (40 lower-case hex digits), not {arguments.commit!r}")
    try:
        if not CREATED.match(arguments.created):
            raise ValueError
        created_time(arguments.created)
    except ValueError:
        parser.error(f"--created must be a UTC time such as 2026-10-01T12:00:00Z, not {arguments.created!r}")
    if not release_compose.REGISTRY_PATTERN.match(arguments.registry):
        parser.error(f"--registry must be a lower-case <registry>/<owner>, not {arguments.registry!r}")
    for name in ("controller_digest", "emulator_digest"):
        if not release_compose.DIGEST.match(getattr(arguments, name)):
            parser.error(f"--{name.replace('_', '-')} must be sha256:<64 hex digits>, not {getattr(arguments, name)!r}")

    try:
        names = build(arguments)
    except (OSError, ValueError, AssetError) as error:
        print(f"release-assets.py: {error}", file=sys.stderr)
        return 1
    for name in names:
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
