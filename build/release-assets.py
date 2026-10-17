#!/usr/bin/env python3
"""A release's assets (docs/releasing.md), gathered into one folder from what CI built and tested, with release.json
and SHA256SUMS.

  build/release-assets.py --version <version> --commit <sha> --created <time>
                          --controller-digest sha256:<hex> --emulator-digest sha256:<hex>
                          --controller-archive <file> --emulator-archive <file>
                          --cli <folder> --installer <folder> --kiosk <folder> --mac-zip <file>
                          [--upgrade-notes <folder>] [--registry <registry>/<owner>] -o <folder>

--cli holds <runtime>/hvo-roof for each runtime in CLI_RUNTIMES (CI's hvo-roof artifact), --installer
<runtime>/hvo-roof-install for the same runtimes (CI's hvo-roof-install artifact), --kiosk the kiosk's files (CI's
hvo-roof-kiosk artifact) and --mac-zip is CI's HVO Roof.app zip. Each must be exactly what this script expects, for the
right processor, and the output folder must be new or empty, so a release holds nothing stale and nothing missing.
--controller-archive and --emulator-archive are the OCI archives the release workflow built each image into and pushed
(docker buildx build --output type=oci): each must hold the image the digest names, an index of one image for each of
IMAGE_PLATFORMS, which this script splits into one offline archive per platform.
--upgrade-notes is docs/upgrade-notes, a folder of X.Y.Z.md files: release.json carries the text of every release's notes up to
this one's (its version without a pre-release's suffix), oldest first, and the installer shows each one newer than what it
upgrades from, so an upgrade across several releases shows them all. The images are <registry>/roof-controller and <registry>/roof-hat-emulator, on GHCR
unless --registry names another (a test's local registry). The assets:

  hvo-roof-<runtime>                          hvo-roof, one file per runtime (linux-arm64, linux-x64, osx-arm64)
  hvo-roof-install-<runtime>                  the installer, one file per runtime (the same three)
  hvo-roof-kiosk-<version>-linux-arm64.tar.gz the kiosk's program, unit, udev rule and example settings, in one folder
  HVO-Roof-<version>.zip                      HVO Roof.app, signed ad hoc
  docker-compose.yaml                         the release compose file, each image pinned to its digest
  deploy-roofcontroller-rpi.sh                the deploy script
  install.sh                                  the script that checks a machine and starts its installer
                                              (docs/install.md#installsh), with the release's version written in
  <image>-<version>-linux-<arch>.oci.tar      an offline image archive: roof-controller and roof-hat-emulator, for
                                              amd64 and arm64, each loaded by docker load in place of a pull
                                              (docs/releasing.md#offline-image-archives)
  release.json                                the version, the commit, each image's digest and offline archives, each
                                              asset's SHA-256, the SHA-256 of each file in the kiosk's tarball (the
                                              installer checks an installed kiosk against them without downloading it),
                                              each release's upgrade notes, and each image's state contracts
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
INSTALL_SCRIPT = REPOSITORY_ROOT / "src" / "HVO.RoofControllerV4.Installer" / "install.sh"
REPOSITORY_URL = "https://github.com/HualapaiValley/HVO.RoofController"
IMAGE_PLATFORMS = ["linux/amd64", "linux/arm64"]

# release.json's key for each image, and its name: <registry>/<name>, and <name>-<version>-linux-<arch>.oci.tar.
IMAGES = (("controller", "roof-controller"), ("hatEmulator", "roof-hat-emulator"))

# The .NET runtime of a platform, for an offline archive's entry in assets.
PLATFORM_RUNTIMES = {"linux/amd64": "linux-x64", "linux/arm64": "linux-arm64"}

# A member's name in an offline archive, as the deploy script's check of the archive's own headers accepts it: a-z, 0-9,
# ".", "-" and "/", in a ustar header's 100-byte name field alone (a folder's with its trailing /). The archives are
# written as plain ustar (tarfile.USTAR_FORMAT), never with a pax header, which that check refuses.
ARCHIVE_MEMBER_NAME = re.compile(r"^[a-z0-9./-]{1,100}$")

# The media types of an image index and of an image manifest, OCI's and Docker's.
INDEX_TYPES = {"application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"}
MANIFEST_TYPES = {"application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"}

_spec = importlib.util.spec_from_file_location("release_compose", REPOSITORY_ROOT / "build" / "release-compose.py")
release_compose = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_compose)

# The runtimes hvo-roof and hvo-roof-install are released for, and the processor each file must be built for.
CLI_RUNTIMES = {"linux-arm64": "aarch64", "linux-x64": "x86-64", "osx-arm64": "macos-arm64"}

# The longest upgrade notes release.json carries, for one release and for every release's together: the installer prints
# them whole before it upgrades.
UPGRADE_NOTES_LIMIT = 16 * 1024
UPGRADE_NOTES_TOTAL_LIMIT = 128 * 1024

# A release's upgrade notes, and the version they are for: X.Y.Z.md.
UPGRADE_NOTES_NAME = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.md$")

# The kiosk's files and their modes: the program, and docs/kiosk.md's deploy files beside it.
KIOSK_FILES = {
    "hvo-roof-kiosk": 0o755,
    "hvo-roof-kiosk.service": 0o644,
    "99-hvo-roof-kiosk-backlight.rules": 0o644,
    "appsettings.Local.example.json": 0o644,
}

# What each image writes and reads of the stores it keeps (docs/releasing.md, "State contracts"): release.json's
# stateContracts, the same as each Dockerfile's io.hvo.roof.state-contracts label (tests/releasing checks they agree).
# identityStoreSchema, which installers from before stateContracts read, is the newest identity schema the controller
# reads. The HAT emulator keeps no state.
STATE_CONTRACTS = {
    "controller": {
        "identity": {"writes": 3, "reads": {"min": 1, "max": 3}},
        "settings": {"writes": 1, "reads": {"min": 1, "max": 1}},
    },
    "hatEmulator": {},
}

MAC_APP = "HVO Roof.app"
MAC_PROGRAM = f"{MAC_APP}/Contents/MacOS/hvo-roof-mac"

COMMIT = re.compile(r"^[0-9a-f]{40}$")
CREATED = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

# ELF e_machine values, and the Mach-O 64-bit magic with the arm64 CPU type.
ELF_MACHINES = {183: "aarch64", 62: "x86-64"}
MACHO_ARM64 = (0xFEEDFACF, 0x0100000C)
MACHO_X64 = (0xFEEDFACF, 0x01000007)


class AssetError(Exception):
    pass


def processor(path):
    """The processor a program file is built for: aarch64 or x86-64 for ELF, macos-arm64 (or macos-x64, an Intel Mac, which
    is not released) for Mach-O, else None."""
    with open(path, "rb") as file:
        header = file.read(20)
    if header[:4] == b"\x7fELF" and len(header) >= 20:
        order = "<" if header[5] == 1 else ">"
        return ELF_MACHINES.get(struct.unpack(order + "H", header[18:20])[0])
    if len(header) >= 8 and struct.unpack("<II", header[:8]) == MACHO_ARM64:
        return "macos-arm64"
    if len(header) >= 8 and struct.unpack("<II", header[:8]) == MACHO_X64:
        return "macos-x64"
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


def program_assets(folder, program_name, out):
    """<runtime>/<program_name> for each runtime in CLI_RUNTIMES, each for its processor, copied to
    <program_name>-<runtime>; returns {name: runtime}."""
    exact_entries(folder, CLI_RUNTIMES, f"The {program_name} folder")
    names = {}
    for runtime, expected in CLI_RUNTIMES.items():
        program = folder / runtime / program_name
        if not program.is_file():
            raise AssetError(f"{program} was not found.")
        actual = processor(program)
        if actual != expected:
            raise AssetError(f"{program} is built for {actual or 'no known processor'}, not {expected}.")
        name = f"{program_name}-{runtime}"
        shutil.copyfile(program, out / name)
        (out / name).chmod(0o755)
        names[name] = runtime
    return names


def upgrade_notes(folder, version):
    """Every release's upgrade notes in folder up to version's (without a pre-release's suffix, so a release candidate
    carries its final release's), oldest first, as {version, text}: an upgrade across several releases shows each one's.
    The folder holds X.Y.Z.md files alone."""
    core = re.match(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-|$)", version)
    if not core:
        raise AssetError(f"{version} is not a release's version (major.minor.patch).")
    if not folder.is_dir():
        raise AssetError(f"{folder} is not a folder: --upgrade-notes is docs/upgrade-notes.")
    found = []
    for path in folder.iterdir():
        match = UPGRADE_NOTES_NAME.match(path.name)
        if not match or not path.is_file() or path.is_symlink():
            raise AssetError(f"{path} is not a release's upgrade notes: {folder} holds X.Y.Z.md files alone.")
        number = tuple(int(part) for part in match.groups())
        if number <= tuple(int(part) for part in core.groups()):
            found.append((number, path))
    notes = [{"version": ".".join(str(part) for part in number), "text": upgrade_notes_text(path)}
             for number, path in sorted(found)]
    total = sum(len(note["text"]) for note in notes)
    if total > UPGRADE_NOTES_TOTAL_LIMIT:
        raise AssetError(f"{folder}'s notes up to {version} hold {total} characters together; the installer prints"
                         f" them whole, so keep them under {UPGRADE_NOTES_TOTAL_LIMIT}: shorten the oldest.")
    return notes


def upgrade_notes_text(path):
    """The upgrade notes' text, without the blank lines around it: UTF-8, not empty, and short enough to print."""
    try:
        text = path.read_bytes().decode("utf-8").strip()
    except UnicodeDecodeError as error:
        raise AssetError(f"{path} is not UTF-8 text: {error}") from error
    if not text:
        raise AssetError(f"{path} is empty: a release's upgrade notes say what someone upgrading must do.")
    if len(text) > UPGRADE_NOTES_LIMIT:
        raise AssetError(f"{path} holds {len(text)} characters; the installer prints them whole, so keep them under"
                         f" {UPGRADE_NOTES_LIMIT} and link to the rest.")
    return text


def kiosk_asset(kiosk, out, version, mtime):
    """The kiosk's files in one folder, in a tarball that is the same for the same files: sorted, owned by root, with
    fixed modes and the release's time. Returns its name and each file's SHA-256."""
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
        files = {}
        for file_name in sorted(KIOSK_FILES):
            data = (kiosk / file_name).read_bytes()
            info = tarfile.TarInfo(f"{folder}/{file_name}")
            info.size, info.mode = len(data), KIOSK_FILES[file_name]
            add(info, io.BytesIO(data))
            files[file_name] = hashlib.sha256(data).hexdigest()
    with open(out / name, "wb") as file, gzip.GzipFile(filename="", mode="wb", fileobj=file, mtime=mtime) as archive:
        archive.write(buffer.getvalue())
    return name, files


def mac_asset(mac_zip, out, version):
    """HVO Roof.app's zip as CI made, signed and checked it, renamed for the release; its Info.plist must carry this
    version (CFBundleShortVersionString holds its X.Y.Z). The program's SHA-256, under its file name (hvo-roof-mac),
    lets the installer check what it unpacked."""
    try:
        with zipfile.ZipFile(mac_zip) as archive:
            names = archive.namelist()
            if any(not entry.startswith(MAC_APP + "/") for entry in names) or MAC_PROGRAM not in names:
                raise AssetError(f"{mac_zip} must hold {MAC_APP} alone, with {MAC_PROGRAM}.")
            info = plistlib.loads(archive.read(f"{MAC_APP}/Contents/Info.plist"))
            program = archive.read(MAC_PROGRAM)
    except (OSError, KeyError, zipfile.BadZipFile, plistlib.InvalidFileException) as error:
        raise AssetError(f"{mac_zip} is not HVO Roof.app's zip: {error}") from error
    short = version.split("-", 1)[0]
    if info.get("CFBundleShortVersionString") != short:
        raise AssetError(f"{mac_zip} holds version {info.get('CFBundleShortVersionString')!r}, not {short}.")
    if len(program) < 8 or struct.unpack("<II", program[:8]) != MACHO_ARM64:
        raise AssetError(f"{mac_zip}'s {MAC_PROGRAM} is not an arm64 Mac program.")
    name = f"HVO-Roof-{version}.zip"
    shutil.copyfile(mac_zip, out / name)
    return name, {MAC_PROGRAM.rsplit("/", 1)[1]: hashlib.sha256(program).hexdigest()}


# install.sh's line that names the release, empty in the repository's copy, which refuses to run.
INSTALL_SCRIPT_VERSION = re.compile(r"^  local release_version=''(  #.*)?$", re.MULTILINE)


def install_script(text, version):
    """install.sh as a release publishes it: the repository's copy with the release's version written in."""
    if not release_compose.VERSION.match(version):
        raise AssetError(f"{version} is not a release's version.")
    lines = INSTALL_SCRIPT_VERSION.findall(text)
    if len(lines) != 1:
        raise AssetError(f"{INSTALL_SCRIPT} must name its release on one line, \"  local release_version=''\", "
                         f"and has {len(lines)}.")
    return INSTALL_SCRIPT_VERSION.sub(f"  local release_version='{version}'", text)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as file:
        for block in iter(lambda: file.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


class _Checked:
    """A blob read as it is copied, and its SHA-256 and size checked against its descriptor once it has been read."""

    def __init__(self, file, descriptor, what):
        self.file, self.descriptor, self.what = file, descriptor, what
        self.hash, self.size = hashlib.sha256(), 0

    def read(self, size=-1):
        data = self.file.read(size)
        self.hash.update(data)
        self.size += len(data)
        return data

    def check(self):
        if self.size != self.descriptor["size"] or f"sha256:{self.hash.hexdigest()}" != self.descriptor["digest"]:
            raise AssetError(f"{self.what} is not the blob {self.descriptor['digest']} its manifest names.")


def offline_archives(source, image, repository, version, digest, out, mtime):
    """The image in the OCI archive source (docker buildx build --output type=oci), split into one offline archive per
    platform of IMAGE_PLATFORMS, <image>-<version>-linux-<arch>.oci.tar, which docker load reads with either of Docker's
    image stores (docs/releasing.md#offline-image-archives). The archive's index must be the image digest names (the
    one pushed) and list one image for each platform, and each blob must be the one its digest names: they are checked
    as they are copied. Each archive holds the index, with that platform's manifest, config and layers alone:

      oci-layout, index.json   the OCI image layout, naming the index, as <repository>:<version>, for the containerd store
      manifest.json            the platform's config and layers, named <repository>:<version>, for the classic store
      blobs/sha256/<hex>       the index, the platform's manifest, its config and its layers, as they were pushed

    Returns each archive's entry in release.json: its name, platform, size and SHA-256, and the digests of the
    platform's manifest and config, the image IDs Docker may give it besides the index's digest."""
    name = f"{repository}:{version}"
    archives = []
    try:
        with tarfile.open(source) as tar:
            members = {member.name: member for member in tar.getmembers() if member.isfile()}

            def member(descriptor, what):
                digest_ = descriptor.get("digest") if isinstance(descriptor, dict) else None
                size = descriptor.get("size") if isinstance(descriptor, dict) else None
                if not isinstance(digest_, str) or not release_compose.DIGEST.match(digest_) \
                        or not isinstance(size, int) or isinstance(size, bool) or size < 0:
                    raise AssetError(f"{source}: {what} has no sha256 digest and size.")
                found = members.get(f"blobs/sha256/{digest_[7:]}")
                if found is None or found.size != size:
                    raise AssetError(f"{source} does not hold {what}, the blob {digest_}.")
                return found

            def document(descriptor, what):
                checked = _Checked(tar.extractfile(member(descriptor, what)), descriptor, f"{source}: {what}")
                data = checked.read()
                checked.check()
                return data, json.loads(data)

            if "index.json" not in members:
                raise AssetError(f"{source} is not an OCI archive: it has no index.json.")
            top = json.loads(tar.extractfile(members["index.json"]).read())
            entries = top.get("manifests") if isinstance(top, dict) else None
            if not isinstance(entries, list) or len(entries) != 1 or not isinstance(entries[0], dict):
                raise AssetError(f"{source} holds {len(entries) if isinstance(entries, list) else 'no'} images, not one.")
            if entries[0].get("digest") != digest:
                raise AssetError(f"{source} holds the image {entries[0].get('digest')}, not {digest}, the one pushed.")
            if entries[0].get("mediaType") not in INDEX_TYPES:
                raise AssetError(f"{source}'s image {digest} is not an index of platforms ({entries[0].get('mediaType')}).")
            index_bytes, index = document(entries[0], f"the index {digest}")

            platforms = {}
            for entry in index.get("manifests", []) if isinstance(index, dict) else []:
                platform = entry.get("platform") if isinstance(entry, dict) else None
                key = f"{platform.get('os')}/{platform.get('architecture')}" if isinstance(platform, dict) else "?"
                if entry.get("mediaType") not in MANIFEST_TYPES or key not in IMAGE_PLATFORMS or key in platforms:
                    platforms = None
                    break
                platforms[key] = entry
            if platforms is None or sorted(platforms) != IMAGE_PLATFORMS:
                raise AssetError(f"{source}'s index {digest} must list one image for each of {', '.join(IMAGE_PLATFORMS)},"
                                 " and nothing else.")

            for platform in IMAGE_PLATFORMS:
                operating_system, architecture = platform.split("/")
                entry = platforms[platform]
                manifest_bytes, manifest_ = document(entry, f"the {platform} manifest")
                config = manifest_.get("config") if isinstance(manifest_, dict) else None
                layers = manifest_.get("layers") if isinstance(manifest_, dict) else None
                if layers is None and isinstance(manifest_, dict):
                    # An image with no layers (FROM scratch, with labels alone) has them null.
                    layers = []
                if not isinstance(layers, list):
                    raise AssetError(f"{source}: the {platform} manifest {entry['digest']} lists no layers.")
                config_bytes, config_ = document(config, f"the {platform} config")
                if not isinstance(config_, dict) or (config_.get("os"), config_.get("architecture")) != (operating_system, architecture):
                    raise AssetError(f"{source}: the {platform} image's config is for "
                                     f"{config_.get('os') if isinstance(config_, dict) else '?'}/"
                                     f"{config_.get('architecture') if isinstance(config_, dict) else '?'}.")
                archive = f"{image}-{version}-linux-{architecture}.oci.tar"
                index_json = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json",
                              "manifests": [{"mediaType": entries[0]["mediaType"], "digest": digest, "size": entries[0]["size"],
                                             "annotations": {"io.containerd.image.name": name,
                                                             "org.opencontainers.image.ref.name": version}}]}
                docker_json = [{"Config": f"blobs/sha256/{config['digest'][7:]}", "RepoTags": [name],
                                "Layers": [f"blobs/sha256/{layer.get('digest', '')[7:]}" for layer in layers]}]
                try:
                    # Plain ustar, so tarfile refuses (ValueError) what only a pax header could hold, here as the release is
                    # built: the deploy script refuses an archive with one, as it is loaded.
                    with tarfile.open(out / archive, "w", format=tarfile.USTAR_FORMAT) as output:
                        def add(path, size, data=None, directory=False):
                            if not ARCHIVE_MEMBER_NAME.match(path + ("/" if directory else "")):
                                raise AssetError(f"{archive}: {path} is not a member name the deploy script accepts in an offline "
                                                 "archive (a-z, 0-9, '.', '-' and '/', at most 100 bytes).")
                            info = tarfile.TarInfo(path)
                            info.type, info.mode = (tarfile.DIRTYPE, 0o755) if directory else (tarfile.REGTYPE, 0o644)
                            info.size, info.mtime = size, mtime
                            info.uid = info.gid = 0
                            info.uname = info.gname = "root"
                            output.addfile(info, data)

                        for path, data in (("oci-layout", b'{"imageLayoutVersion":"1.0.0"}'),
                                           ("index.json", json.dumps(index_json, separators=(",", ":")).encode()),
                                           ("manifest.json", json.dumps(docker_json, separators=(",", ":")).encode())):
                            add(path, len(data), io.BytesIO(data))
                        add("blobs", 0, directory=True)
                        add("blobs/sha256", 0, directory=True)
                        for descriptor, data in ((entries[0], index_bytes), (entry, manifest_bytes), (config, config_bytes)):
                            add(f"blobs/sha256/{descriptor['digest'][7:]}", len(data), io.BytesIO(data))
                        copied = set()
                        for number, layer in enumerate(layers, 1):
                            what = f"layer {number} of the {platform} image"
                            found = member(layer, what)
                            if layer["digest"] in copied:
                                continue
                            copied.add(layer["digest"])
                            checked = _Checked(tar.extractfile(found), layer, f"{source}: {what}")
                            add(found.name, found.size, checked)
                            checked.check()
                except (ValueError, AssetError) as error:
                    (out / archive).unlink(missing_ok=True)
                    if isinstance(error, AssetError):
                        raise
                    raise AssetError(f"{archive} cannot be written as a plain ustar archive, the only kind the deploy script "
                                     f"loads: {error}.") from error
                archives.append({"name": archive, "platform": platform, "size": (out / archive).stat().st_size,
                                 "sha256": sha256(out / archive), "manifestDigest": entry["digest"],
                                 "configDigest": config["digest"]})
    except (tarfile.TarError, json.JSONDecodeError, UnicodeDecodeError, AttributeError, TypeError) as error:
        raise AssetError(f"{source} is not an OCI archive of an image release-assets.py can split: {error}") from error
    return archives


def manifest(out, assets, version, commit, created, registry, digests, contents, notes=None, offline=None):
    """release.json: what the installer and install.sh read to know the release, its images and its files; an archive
    in contents lists its files' SHA-256 too, notes are every release's upgrade notes up to this one's, and offline each
    image's offline archives, by its key."""
    images = {}
    for key, image in IMAGES:
        repository = f"{registry}/{image}"
        images[key] = {
            "repository": repository,
            "tag": version,
            "digest": digests[key],
            "reference": f"{repository}:{version}@{digests[key]}",
            "platforms": IMAGE_PLATFORMS,
            **({"offlineArchives": offline[key]} if offline and key in offline else {}),
        }
    return {
        "schemaVersion": 1,
        "identityStoreSchema": STATE_CONTRACTS["controller"]["identity"]["reads"]["max"],
        "product": "HVO Roof Controller",
        "version": version,
        "tag": f"v{version}",
        "prerelease": "-" in version,
        "commit": commit,
        "created": created,
        "repository": REPOSITORY_URL,
        "images": images,
        "stateContracts": STATE_CONTRACTS,
        **({"upgradeNotes": notes} if notes else {}),
        "assets": [
            {"name": name, "kind": kind, "platform": platform, "size": (out / name).stat().st_size,
             "sha256": sha256(out / name), **({"files": contents[name]} if name in contents else {})}
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

    notes = upgrade_notes(arguments.upgrade_notes, version) if arguments.upgrade_notes else None
    assets = {}
    for name, runtime in program_assets(arguments.cli, "hvo-roof", out).items():
        assets[name] = ("cli", runtime)
    for name, runtime in program_assets(arguments.installer, "hvo-roof-install", out).items():
        assets[name] = ("installer", runtime)
    if getattr(arguments, "linux_auth", None):
        folder = arguments.linux_auth
        expected_runtimes = {"linux-arm64": "aarch64", "linux-x64": "x86-64"}
        exact_entries(folder, expected_runtimes, "The Linux authentication folder")
        for runtime, expected in expected_runtimes.items():
            worker = folder / runtime / "HVO.RoofControllerV4.LinuxAuth"
            if not worker.is_file() or worker.is_symlink() or processor(worker) != expected:
                raise AssetError(f"{worker} must be the self-contained worker for {runtime}.")
            name = f"hvo-roof-linux-auth-{runtime}"
            shutil.copyfile(worker, out / name)
            (out / name).chmod(0o755)
            assets[name] = ("linux-auth", runtime)
    kiosk, kiosk_files = kiosk_asset(arguments.kiosk, out, version, mtime)
    assets[kiosk] = ("kiosk", "linux-arm64")
    mac, mac_files = mac_asset(arguments.mac_zip, out, version)
    assets[mac] = ("mac-app", "osx-arm64")
    contents = {kiosk: kiosk_files, mac: mac_files}

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
    (out / INSTALL_SCRIPT.name).write_text(install_script(INSTALL_SCRIPT.read_text(encoding="utf-8"), version),
                                           encoding="utf-8")
    (out / INSTALL_SCRIPT.name).chmod(0o755)
    assets[INSTALL_SCRIPT.name] = ("install-script", None)

    digests = {"controller": arguments.controller_digest, "hatEmulator": arguments.emulator_digest}
    offline = {}
    for (key, image), source in zip(IMAGES, (arguments.controller_archive, arguments.emulator_archive)):
        if not source.is_file():
            raise AssetError(f"{source} is not a file: give the OCI archive the {image} image was pushed from.")
        offline[key] = offline_archives(source, image, f"{arguments.registry}/{image}", version, digests[key], out, mtime)
        for archive in offline[key]:
            assets[archive["name"]] = ("image-archive", PLATFORM_RUNTIMES[archive["platform"]])
    release = manifest(out, assets, version, arguments.commit, arguments.created, arguments.registry, digests, contents,
                       notes, offline)
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
    parser.add_argument("--controller-archive", required=True, type=Path,
                        help="the OCI archive the controller image was pushed from, split into its offline archives")
    parser.add_argument("--emulator-archive", required=True, type=Path,
                        help="the OCI archive the HAT emulator image was pushed from, split into its offline archives")
    parser.add_argument("--cli", required=True, type=Path, help="CI's hvo-roof artifact: <runtime>/hvo-roof")
    parser.add_argument("--installer", required=True, type=Path,
                        help="CI's hvo-roof-install artifact: <runtime>/hvo-roof-install")
    parser.add_argument("--linux-auth", type=Path,
                        help="optional native worker: <linux-arm64|linux-x64>/HVO.RoofControllerV4.LinuxAuth, self-contained single files")
    parser.add_argument("--kiosk", required=True, type=Path, help="CI's hvo-roof-kiosk artifact")
    parser.add_argument("--mac-zip", required=True, type=Path, help="CI's HVO Roof.app zip")
    parser.add_argument("--upgrade-notes", type=Path,
                        help="docs/upgrade-notes: every release's X.Y.Z.md up to this one's goes in release.json")
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
