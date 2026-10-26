#!/usr/bin/env python3
"""Tests for build/release-assets.py (#64): a release's assets gathered from CI's artifacts, with release.json and
SHA256SUMS, and the refusals of a folder or file that is not exactly what a release holds. The programs are stand-ins
with the right file headers, so no .NET build is needed. From the repository root:

    python3 -m unittest discover -s tests/releasing -p 'test_*.py'
"""

import contextlib
import gzip
import hashlib
import importlib.util
import io
import json
import pathlib
import plistlib
import re
import stat
import struct
import tarfile
import tempfile
import unittest
import zipfile
from bundle_fixture import make_bundle

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("release_assets", REPOSITORY / "build" / "release-assets.py")
release_assets = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_assets)

COMMIT = "0123456789abcdef0123456789abcdef01234567"
CREATED = "2026-10-01T12:00:00Z"
CREATED_SECONDS = 1790856000

OCI_INDEX = "application/vnd.oci.image.index.v1+json"
OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json"


def layer(files):
    """A layer: a gzipped tar of the files, the same each time, and its diff ID (the SHA-256 of the tar)."""
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            tar.addfile(info, io.BytesIO(data))
    compressed = io.BytesIO()
    with gzip.GzipFile(fileobj=compressed, mode="wb", mtime=0) as archive:
        archive.write(raw.getvalue())
    return compressed.getvalue(), "sha256:" + hashlib.sha256(raw.getvalue()).hexdigest()


class Image:
    """An image as docker buildx build --output type=oci writes it, for the platforms: an index of one image for each,
    with a config and a layer of its own. config_platforms gives an image a config for another platform; the platform's
    image in empty_platforms has no layers, which BuildKit writes as null (FROM scratch with labels alone)."""

    def __init__(self, name, platforms=("linux/amd64", "linux/arm64"), config_platforms=None, empty_platforms=()):
        self.blobs = {}
        self.manifests, self.configs, self.layers = {}, {}, {}
        entries = []
        for platform in platforms:
            operating_system, architecture = (config_platforms or {}).get(platform, platform).split("/")
            diff_ids = []
            if platform not in empty_platforms:
                data, diff_id = layer({f"app/{name}": f"{name} for {platform}\n".encode()})
                self.layers[platform] = self.add(data, "application/vnd.oci.image.layer.v1.tar+gzip")
                diff_ids.append(diff_id)
            config = {"architecture": architecture, "os": operating_system, "rootfs": {"type": "layers", "diff_ids": diff_ids},
                      "config": {"Labels": {"org.opencontainers.image.title": name}}}
            self.configs[platform] = self.add(json.dumps(config).encode(), "application/vnd.oci.image.config.v1+json")
            manifest = {"schemaVersion": 2, "mediaType": OCI_MANIFEST, "config": self.configs[platform],
                        "layers": [self.layers[platform]] if platform in self.layers else None}
            self.manifests[platform] = self.add(json.dumps(manifest).encode(), OCI_MANIFEST)
            os_, arch = platform.split("/")
            entries.append({**self.manifests[platform], "platform": {"architecture": arch, "os": os_}})
        self.index = self.add(json.dumps({"schemaVersion": 2, "mediaType": OCI_INDEX, "manifests": entries}).encode(), OCI_INDEX)
        self.digest = self.index["digest"]

    def add(self, data, media_type):
        digest = "sha256:" + hashlib.sha256(data).hexdigest()
        self.blobs[digest] = data
        return {"mediaType": media_type, "digest": digest, "size": len(data)}

    def write(self, path, images=None, blobs=None):
        """The OCI archive: index.json naming the images (the index alone by default), and the blobs (every one)."""
        with tarfile.open(path, "w") as tar:
            def add(name, data):
                info = tarfile.TarInfo(name)
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))

            add("oci-layout", b'{"imageLayoutVersion":"1.0.0"}')
            add("index.json", json.dumps({"schemaVersion": 2, "mediaType": OCI_INDEX,
                                          "manifests": [self.index] if images is None else images}).encode())
            for digest, data in (self.blobs if blobs is None else blobs).items():
                add(f"blobs/sha256/{digest[7:]}", data)


CONTROLLER_IMAGE = Image("roof-controller")
EMULATOR_IMAGE = Image("roof-hat-emulator")
CONTROLLER_DIGEST = CONTROLLER_IMAGE.digest
EMULATOR_DIGEST = EMULATOR_IMAGE.digest

# The offline archives a release holds: <image>-<version>-linux-<arch>.oci.tar, for each image and platform.
ARCHIVES = {f"{image}-4.0.0-linux-{arch}.oci.tar": ("image-archive", runtime)
            for image in ("roof-controller", "roof-hat-emulator")
            for arch, runtime in (("amd64", "linux-x64"), ("arm64", "linux-arm64"))}


def elf(machine, body=b"program"):
    """The start of a 64-bit little-endian ELF file for the machine (183 aarch64, 62 x86-64)."""
    return b"\x7fELF\x02\x01\x01" + b"\0" * 11 + struct.pack("<H", machine) + body


def mach_o_arm64(body=b"program"):
    return struct.pack("<II", 0xFEEDFACF, 0x0100000C) + body


AARCH64, X86_64 = elf(183), elf(62)


def mac_zip(path, version="4.0.0", entries=None, program=None):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("HVO Roof.app/Contents/Info.plist", plistlib.dumps({"CFBundleShortVersionString": version}))
        archive.writestr("HVO Roof.app/Contents/MacOS/hvo-roof-mac", mach_o_arm64() if program is None else program)
        for name, data in (entries or {}).items():
            archive.writestr(name, data)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Release:
    """CI's artifacts, as the release workflow downloads them, in a temporary folder."""

    def __init__(self, root):
        self.root = root
        self.cli = root / "cli"
        self.installer = root / "installer"
        self.kiosk = root / "kiosk"
        self.mac = root / "hvo-roof-mac.zip"
        self.controller_archive = root / "roof-controller.tar"
        self.emulator_archive = root / "roof-hat-emulator.tar"
        self.out = root / "out"
        CONTROLLER_IMAGE.write(self.controller_archive)
        EMULATOR_IMAGE.write(self.emulator_archive)
        for runtime, program in (("linux-arm64", AARCH64), ("linux-x64", X86_64), ("osx-arm64", mach_o_arm64(b"cli"))):
            (self.cli / runtime).mkdir(parents=True)
            (self.cli / runtime / "hvo-roof").write_bytes(make_bundle(prefix=program, rid=runtime))
        for runtime, program in (("linux-arm64", elf(183, b"install")), ("linux-x64", elf(62, b"install")),
                                 ("osx-arm64", mach_o_arm64(b"install"))):
            (self.installer / runtime).mkdir(parents=True)
            (self.installer / runtime / "hvo-roof-install").write_bytes(make_bundle(prefix=program, rid=runtime))
        self.kiosk.mkdir()
        (self.kiosk / "hvo-roof-kiosk").write_bytes(elf(183, b"kiosk"))
        for name in ("hvo-roof-kiosk.service", "hvo-roof-terminal.service", "terminal-handoff.sh", "99-hvo-roof-kiosk-backlight.rules", "appsettings.Local.example.json"):
            (self.kiosk / name).write_text(f"{name}\n", encoding="utf-8")
        mac_zip(self.mac)

    def arguments(self, version="4.0.0", out=None, **overrides):
        values = {
            "--version": version, "--commit": COMMIT, "--created": CREATED,
            "--controller-digest": CONTROLLER_DIGEST, "--emulator-digest": EMULATOR_DIGEST,
            "--controller-archive": str(self.controller_archive), "--emulator-archive": str(self.emulator_archive),
            "--cli": str(self.cli), "--installer": str(self.installer), "--kiosk": str(self.kiosk),
            "--mac-zip": str(self.mac),
            "-o": str(out or self.out),
        }
        values.update(overrides)
        return [item for pair in values.items() if pair[1] is not None for item in pair]


def run(arguments):
    """Runs the script's main; returns its exit status, standard output and standard error."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            status = release_assets.main(arguments)
        except SystemExit as exit:
            status = exit.code
    return status, out.getvalue(), err.getvalue()


class LinuxAuthAssets(unittest.TestCase):
    def test_optional_workers_are_hashed_and_mark_schema_compatibility(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            release = Release(root)
            native = root / "native"
            for rid, content in (("linux-arm64", AARCH64), ("linux-x64", X86_64)):
                (native / rid).mkdir(parents=True)
                (native / rid / "HVO.RoofControllerV4.LinuxAuth").write_bytes(content)
            status, _, error = run(release.arguments(**{"--linux-auth": str(native)}))
            self.assertEqual(0, status, error)
            manifest = json.loads((release.out / "release.json").read_text())
            self.assertEqual(3, manifest["identityStoreSchema"])
            workers = [item for item in manifest["assets"] if item["kind"] == "linux-auth"]
            self.assertEqual(["linux-arm64", "linux-x64"], [item["platform"] for item in workers])
            for item in workers:
                self.assertEqual(sha256(release.out / item["name"]), item["sha256"])

    def test_optional_worker_wrong_architecture_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            release = Release(root)
            native = root / "native"
            for rid in ("linux-arm64", "linux-x64"):
                (native / rid).mkdir(parents=True)
                (native / rid / "HVO.RoofControllerV4.LinuxAuth").write_bytes(AARCH64)
            status, _, error = run(release.arguments(**{"--linux-auth": str(native)}))
            self.assertEqual(1, status)
            self.assertIn("must be the self-contained worker for linux-x64", error)


class ReleaseTestCase(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.release = Release(pathlib.Path(directory.name))

    def build(self, version="4.0.0", **overrides):
        status, out, err = run(self.release.arguments(version, **overrides))
        self.assertEqual((status, err), (0, ""))
        return out

    def refused(self, message, version="4.0.0", status=1, **overrides):
        actual, _, err = run(self.release.arguments(version, **overrides))
        self.assertEqual(actual, status, err)
        self.assertIn(message, err)


class TheAssets(ReleaseTestCase):
    EXPECTED = {
        "hvo-roof-linux-arm64": ("cli", "linux-arm64"),
        "hvo-roof-linux-x64": ("cli", "linux-x64"),
        "hvo-roof-osx-arm64": ("cli", "osx-arm64"),
        "hvo-roof-install-linux-arm64": ("installer", "linux-arm64"),
        "hvo-roof-install-linux-x64": ("installer", "linux-x64"),
        "hvo-roof-install-osx-arm64": ("installer", "osx-arm64"),
        "hvo-roof-kiosk-4.0.0-linux-arm64.tar.gz": ("kiosk", "linux-arm64"),
        "HVO-Roof-4.0.0.zip": ("mac-app", "osx-arm64"),
        "docker-compose.yaml": ("compose", None),
        "deploy-roofcontroller-rpi.sh": ("deploy-script", None),
        "install.sh": ("install-script", None),
        **ARCHIVES,
    }

    def setUp(self):
        super().setUp()
        self.printed = self.build().split()
        self.out = self.release.out
        self.manifest = json.loads((self.out / "release.json").read_text(encoding="utf-8"))

    def test_the_folder_holds_the_assets_release_json_and_sha256sums(self):
        expected = sorted([*self.EXPECTED, "release.json", "SHA256SUMS"])
        self.assertEqual(sorted(path.name for path in self.out.iterdir()), expected)
        self.assertEqual(self.printed, expected)

    def test_release_json_names_the_release(self):
        self.assertEqual(
            {key: self.manifest[key] for key in
             ("schemaVersion", "version", "tag", "prerelease", "commit", "created", "repository")},
            {"schemaVersion": 1, "version": "4.0.0", "tag": "v4.0.0", "prerelease": False, "commit": COMMIT,
             "created": CREATED, "repository": "https://github.com/HualapaiValley/HVO.RoofController"})

    def test_release_json_pins_each_image_to_its_digest(self):
        images = {key: {name: value for name, value in image.items() if name != "offlineArchives"}
                  for key, image in self.manifest["images"].items()}
        self.assertEqual(images, {
            "controller": {
                "repository": "ghcr.io/hualapaivalley/roof-controller", "tag": "4.0.0", "digest": CONTROLLER_DIGEST,
                "reference": f"ghcr.io/hualapaivalley/roof-controller:4.0.0@{CONTROLLER_DIGEST}",
                "platforms": ["linux/amd64", "linux/arm64"]},
            "hatEmulator": {
                "repository": "ghcr.io/hualapaivalley/roof-hat-emulator", "tag": "4.0.0", "digest": EMULATOR_DIGEST,
                "reference": f"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{EMULATOR_DIGEST}",
                "platforms": ["linux/amd64", "linux/arm64"]},
        })

    def test_release_json_declares_each_image_s_state_contracts(self):
        self.assertEqual(self.manifest["stateContracts"], release_assets.STATE_CONTRACTS)
        self.assertEqual(self.manifest["identityStoreSchema"],
                         release_assets.STATE_CONTRACTS["controller"]["identity"]["reads"]["max"])

    def test_release_json_lists_every_other_asset_with_its_size_and_sha256(self):
        assets = self.manifest["assets"]
        self.assertEqual([asset["name"] for asset in assets], sorted(self.EXPECTED))
        for asset in assets:
            path = self.out / asset["name"]
            self.assertEqual((asset["kind"], asset["platform"]), self.EXPECTED[asset["name"]], asset["name"])
            self.assertEqual(asset["size"], path.stat().st_size, asset["name"])
            self.assertEqual(asset["sha256"], sha256(path), asset["name"])
            self.assertEqual("files" in asset, asset["kind"] in ("kiosk", "mac-app"), asset["name"])

    def test_release_json_lists_the_sha256_of_each_file_in_the_kiosk_s_tarball(self):
        kiosk = next(asset for asset in self.manifest["assets"] if asset["kind"] == "kiosk")
        folder = self.release.kiosk
        self.assertEqual(kiosk["files"], {
            name: hashlib.sha256((folder / name).read_bytes()).hexdigest()
            for name in ("99-hvo-roof-kiosk-backlight.rules", "appsettings.Local.example.json", "hvo-roof-kiosk",
                         "hvo-roof-kiosk.service", "hvo-roof-terminal.service", "terminal-handoff.sh")})

    def test_sha256sums_covers_every_asset_and_release_json_as_sha256sum_writes_it(self):
        lines = (self.out / "SHA256SUMS").read_text(encoding="utf-8").splitlines()
        names = sorted([*self.EXPECTED, "release.json"])
        self.assertEqual(lines, [f"{sha256(self.out / name)}  {name}" for name in names])

    def test_the_programs_are_ci_s_files_and_executable(self):
        for runtime in ("linux-arm64", "linux-x64", "osx-arm64"):
            for folder, program in ((self.release.cli, "hvo-roof"), (self.release.installer, "hvo-roof-install")):
                asset = self.out / f"{program}-{runtime}"
                self.assertEqual(asset.read_bytes(), (folder / runtime / program).read_bytes())
                self.assertTrue(asset.stat().st_mode & stat.S_IXUSR, asset.name)
        script = self.out / "deploy-roofcontroller-rpi.sh"
        self.assertEqual(script.read_bytes(), release_assets.DEPLOY_SCRIPT.read_bytes())
        self.assertTrue(script.stat().st_mode & stat.S_IXUSR)

    def test_install_sh_is_the_repository_s_with_the_release_s_version_written_in(self):
        script = self.out / "install.sh"
        repository = release_assets.INSTALL_SCRIPT.read_text(encoding="utf-8").splitlines()
        published = script.read_text(encoding="utf-8").splitlines()
        changed = [(before, after) for before, after in zip(repository, published) if before != after]
        self.assertEqual(len(published), len(repository))
        self.assertEqual(len(changed), 1, changed)
        self.assertRegex(changed[0][0], r"^  local release_version=''")
        self.assertEqual(changed[0][1], "  local release_version='4.0.0'")
        self.assertEqual(stat.S_IMODE(script.stat().st_mode), 0o755)

    def test_a_release_without_upgrade_notes_has_none(self):
        self.assertNotIn("upgradeNotes", self.manifest)

    def test_the_mac_app_is_ci_s_zip_renamed(self):
        self.assertEqual((self.out / "HVO-Roof-4.0.0.zip").read_bytes(), self.release.mac.read_bytes())

    def test_the_mac_app_lists_its_program_s_hash(self):
        mac = next(asset for asset in self.manifest["assets"] if asset["kind"] == "mac-app")
        self.assertEqual(mac["files"], {
            "hvo-roof-mac": hashlib.sha256(mach_o_arm64()).hexdigest()})

    def test_the_compose_file_pins_both_images(self):
        images = set(re.findall(r"^\s*image: (\S+)$", (self.out / "docker-compose.yaml").read_text(), re.MULTILINE))
        self.assertEqual(images, {f"ghcr.io/hualapaivalley/roof-controller:4.0.0@{CONTROLLER_DIGEST}",
                                  f"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{EMULATOR_DIGEST}"})

    def test_the_kiosk_tarball_holds_one_folder_owned_by_root_with_fixed_modes_and_the_release_s_time(self):
        with tarfile.open(self.out / "hvo-roof-kiosk-4.0.0-linux-arm64.tar.gz") as tar:
            members = tar.getmembers()
            folder = "hvo-roof-kiosk-4.0.0-linux-arm64"
            self.assertEqual(
                [(member.name, member.isdir(), oct(member.mode)) for member in members],
                [(folder, True, "0o755"),
                 (f"{folder}/99-hvo-roof-kiosk-backlight.rules", False, "0o644"),
                 (f"{folder}/appsettings.Local.example.json", False, "0o644"),
                 (f"{folder}/hvo-roof-kiosk", False, "0o755"),
                 (f"{folder}/hvo-roof-kiosk.service", False, "0o644"),
                 (f"{folder}/hvo-roof-terminal.service", False, "0o644"),
                 (f"{folder}/terminal-handoff.sh", False, "0o644")])
            for member in members:
                self.assertEqual((member.uid, member.gid, member.uname, member.gname, member.mtime),
                                 (0, 0, "root", "root", CREATED_SECONDS), member.name)
            self.assertEqual(tar.extractfile(f"{folder}/hvo-roof-kiosk").read(), elf(183, b"kiosk"))

    def test_the_same_inputs_make_the_same_kiosk_tarball(self):
        again = self.release.root / "again"
        self.build(out=again)
        name = "hvo-roof-kiosk-4.0.0-linux-arm64.tar.gz"
        self.assertEqual((again / name).read_bytes(), (self.out / name).read_bytes())


class TheOfflineArchives(ReleaseTestCase):
    """Each image's offline archives (#127): one per platform, loadable by docker load in either image store."""

    IMAGES = (("controller", "roof-controller", CONTROLLER_IMAGE), ("hatEmulator", "roof-hat-emulator", EMULATOR_IMAGE))

    def setUp(self):
        super().setUp()
        self.build()
        self.out = self.release.out
        self.manifest = json.loads((self.out / "release.json").read_text(encoding="utf-8"))

    def test_release_json_lists_each_image_s_archives_with_the_ids_docker_may_give_it(self):
        for key, name, image in self.IMAGES:
            archives = self.manifest["images"][key]["offlineArchives"]
            self.assertEqual([archive["platform"] for archive in archives], ["linux/amd64", "linux/arm64"], key)
            for archive in archives:
                path = self.out / archive["name"]
                platform = archive["platform"]
                self.assertEqual(archive, {
                    "name": f"{name}-4.0.0-linux-{platform.split('/')[1]}.oci.tar", "platform": platform,
                    "size": path.stat().st_size, "sha256": sha256(path),
                    "manifestDigest": image.manifests[platform]["digest"], "configDigest": image.configs[platform]["digest"]})

    def test_each_archive_names_the_pushed_index_and_holds_its_platform_s_blobs_alone(self):
        for key, name, image in self.IMAGES:
            for platform in ("linux/amd64", "linux/arm64"):
                archive = self.out / f"{name}-4.0.0-linux-{platform.split('/')[1]}.oci.tar"
                with tarfile.open(archive) as tar:
                    files = {member.name: tar.extractfile(member).read() for member in tar.getmembers() if member.isfile()}
                reference = f"ghcr.io/hualapaivalley/{name}:4.0.0"
                self.assertEqual(json.loads(files["oci-layout"]), {"imageLayoutVersion": "1.0.0"})
                self.assertEqual(json.loads(files["index.json"]), {
                    "schemaVersion": 2, "mediaType": OCI_INDEX,
                    "manifests": [{**image.index, "annotations": {"io.containerd.image.name": reference,
                                                                  "org.opencontainers.image.ref.name": "4.0.0"}}]})
                config, layer_ = image.configs[platform]["digest"], image.layers[platform]["digest"]
                self.assertEqual(json.loads(files["manifest.json"]), [
                    {"Config": f"blobs/sha256/{config[7:]}", "RepoTags": [reference], "Layers": [f"blobs/sha256/{layer_[7:]}"]}])
                blobs = {f"sha256:{path[13:]}": data for path, data in files.items() if path.startswith("blobs/sha256/")}
                self.assertEqual(set(blobs), {image.digest, image.manifests[platform]["digest"], config, layer_}, archive.name)
                for digest, data in blobs.items():
                    self.assertEqual(data, image.blobs[digest], digest)

    def test_each_archive_is_root_s_with_fixed_modes_and_the_release_s_time(self):
        for archive in ARCHIVES:
            with tarfile.open(self.out / archive) as tar:
                members = tar.getmembers()
            self.assertEqual([member.name for member in members][:5],
                             ["oci-layout", "index.json", "manifest.json", "blobs", "blobs/sha256"], archive)
            for member in members:
                self.assertEqual((member.uid, member.gid, member.uname, member.gname, member.mtime, oct(member.mode)),
                                 (0, 0, "root", "root", CREATED_SECONDS, "0o755" if member.isdir() else "0o644"),
                                 f"{archive}: {member.name}")

    def test_the_same_inputs_make_the_same_archives(self):
        again = self.release.root / "again"
        self.build(out=again)
        for archive in ARCHIVES:
            self.assertEqual((again / archive).read_bytes(), (self.out / archive).read_bytes(), archive)

    def test_an_image_without_layers_is_split_too(self):
        image = Image("roof-hat-emulator", empty_platforms=("linux/arm64",))
        image.write(self.release.emulator_archive)
        out = self.release.root / "empty"
        self.build(out=out, **{"--emulator-digest": image.digest})
        with tarfile.open(out / "roof-hat-emulator-4.0.0-linux-arm64.oci.tar") as tar:
            docker = json.loads(tar.extractfile("manifest.json").read())
            blobs = {name for name in tar.getnames() if name.startswith("blobs/sha256/")}
        self.assertEqual(docker[0]["Layers"], [])
        self.assertEqual(blobs, {f"blobs/sha256/{digest[7:]}" for digest in
                                 (image.digest, image.manifests["linux/arm64"]["digest"], image.configs["linux/arm64"]["digest"])})

    def test_every_header_of_each_archive_is_a_plain_ustar_header(self):
        # What the deploy script's archive_members accepts, and nothing more: each header "ustar\0" "00", summing to its
        # checksum, a file (0) or a folder (5) with no link target, nothing past the name field's 100 bytes but the
        # fields ustar has, then empty blocks to the end. A pax header (x, g) would be refused.
        for archive in ARCHIVES:
            self.assert_plain_ustar(self.out / archive)

    def test_each_archive_is_plain_ustar_even_for_a_time_pax_would_keep_in_a_header_of_its_own(self):
        # PAX_FORMAT keeps a time with a fraction of a second in a pax header; ustar holds whole seconds.
        out = self.release.root / "fraction"
        out.mkdir()
        release_assets.offline_archives(self.release.controller_archive, "roof-controller", "ghcr.io/hualapaivalley/roof-controller",
                                        "4.0.0", CONTROLLER_DIGEST, out, CREATED_SECONDS + 0.5)
        for platform in ("amd64", "arm64"):
            self.assert_plain_ustar(out / f"roof-controller-4.0.0-linux-{platform}.oci.tar")

    def test_what_ustar_cannot_hold_is_refused_as_the_release_is_built(self):
        # A time past ustar's 11 octal digits: PAX_FORMAT would write it in a pax header, which the deploy script refuses.
        out = self.release.root / "late"
        out.mkdir()
        with self.assertRaisesRegex(release_assets.AssetError,
                                    r"roof-controller-4\.0\.0-linux-amd64\.oci\.tar cannot be written as a plain ustar archive"):
            release_assets.offline_archives(self.release.controller_archive, "roof-controller", "ghcr.io/hualapaivalley/roof-controller",
                                            "4.0.0", CONTROLLER_DIGEST, out, 8 ** 11)
        self.assertEqual(list(out.iterdir()), [], "no archive is left half written")
        for name, accepted in (("blobs/sha256/" + "a" * 64, True), ("a" * 100, True), ("a" * 101, False),
                               ("Blobs/sha256", False), ("blobs/sha256/x y", False), ("ünïcode", False)):
            self.assertEqual(bool(release_assets.ARCHIVE_MEMBER_NAME.match(name)), accepted, name)

    def assert_plain_ustar(self, path):
        data = path.read_bytes()
        self.assertEqual(len(data) % 512, 0, path.name)
        offset, members = 0, []
        while offset < len(data):
            header = data[offset:offset + 512]
            if header == bytes(512):
                self.assertEqual(data[offset:], bytes(len(data) - offset), f"{path.name}: only empty blocks after the end")
                self.assertGreaterEqual(len(data) - offset, 1024, f"{path.name}: two empty blocks end it")
                break
            where = f"{path.name} at {offset}"
            self.assertEqual(header[257:265], b"ustar\x0000", where)
            self.assertIn(header[156:157], (b"0", b"5"), where)
            stored = int(header[148:156].replace(b"\x00", b" ").strip() or b"-1", 8)
            self.assertEqual(stored, sum(header[:148]) + 8 * 32 + sum(header[156:]), where)
            name = header[:100].rstrip(b"\x00")
            self.assertNotIn(b"\x00", name, where)
            self.assertRegex(name.decode("ascii"), release_assets.ARCHIVE_MEMBER_NAME, where)
            self.assertEqual(header[157:257], bytes(100), f"{where}: no link target")
            self.assertEqual(header[345:512], bytes(167), f"{where}: no name prefix, nothing past ustar's fields")
            self.assertRegex(header[124:136], rb"^[0-7]{11}\x00$", where)
            size = int(header[124:135], 8)
            members.append((header[156:157], name.decode("ascii")))
            offset += 512 + (size + 511) // 512 * 512
        self.assertEqual(members[:5], [(b"0", "oci-layout"), (b"0", "index.json"), (b"0", "manifest.json"),
                                       (b"5", "blobs/"), (b"5", "blobs/sha256/")], path.name)

    def test_the_archives_are_attested_with_the_other_assets(self):
        listed = {line.split("  ", 1)[1] for line in (self.out / "SHA256SUMS").read_text(encoding="utf-8").splitlines()}
        self.assertLessEqual(set(ARCHIVES), listed)


class TheOfflineArchiveRefusals(ReleaseTestCase):
    def test_an_archive_of_another_image_than_the_one_pushed(self):
        self.refused(f"holds the image {EMULATOR_DIGEST}, not {CONTROLLER_DIGEST}, the one pushed",
                     **{"--controller-archive": str(self.release.emulator_archive)})

    def test_an_index_without_every_platform(self):
        image = Image("roof-controller", platforms=("linux/amd64",))
        image.write(self.release.controller_archive)
        self.refused("must list one image for each of linux/amd64, linux/arm64, and nothing else",
                     **{"--controller-digest": image.digest})

    def test_an_index_with_another_platform(self):
        image = Image("roof-controller", platforms=("linux/amd64", "linux/arm64", "linux/riscv64"))
        image.write(self.release.controller_archive)
        self.refused("must list one image for each of linux/amd64, linux/arm64, and nothing else",
                     **{"--controller-digest": image.digest})

    def test_an_image_whose_config_is_for_another_platform(self):
        image = Image("roof-controller", config_platforms={"linux/arm64": "linux/amd64"})
        image.write(self.release.controller_archive)
        self.refused("the linux/arm64 image's config is for linux/amd64", **{"--controller-digest": image.digest})

    def test_an_archive_without_a_layer(self):
        blobs = dict(CONTROLLER_IMAGE.blobs)
        del blobs[CONTROLLER_IMAGE.layers["linux/arm64"]["digest"]]
        CONTROLLER_IMAGE.write(self.release.controller_archive, blobs=blobs)
        self.refused(f"does not hold layer 1 of the linux/arm64 image, the blob {CONTROLLER_IMAGE.layers['linux/arm64']['digest']}")

    def test_a_layer_that_is_not_the_blob_its_digest_names(self):
        blobs = dict(CONTROLLER_IMAGE.blobs)
        digest = CONTROLLER_IMAGE.layers["linux/amd64"]["digest"]
        blobs[digest] = bytes(len(blobs[digest]))
        CONTROLLER_IMAGE.write(self.release.controller_archive, blobs=blobs)
        self.refused(f"layer 1 of the linux/amd64 image is not the blob {digest} its manifest names")

    def test_an_archive_of_two_images(self):
        CONTROLLER_IMAGE.write(self.release.controller_archive, images=[CONTROLLER_IMAGE.index, CONTROLLER_IMAGE.index])
        self.refused("holds 2 images, not one")

    def test_an_image_that_is_not_an_index(self):
        manifest = CONTROLLER_IMAGE.manifests["linux/amd64"]
        CONTROLLER_IMAGE.write(self.release.controller_archive, images=[manifest])
        self.refused("is not an index of platforms", **{"--controller-digest": manifest["digest"]})

    def test_a_file_that_is_not_an_archive(self):
        self.release.emulator_archive.write_bytes(b"not a tar")
        self.refused("is not an OCI archive of an image release-assets.py can split")

    def test_no_archive(self):
        self.release.emulator_archive.unlink()
        self.refused("is not a file: give the OCI archive the roof-hat-emulator image was pushed from")


class APrerelease(ReleaseTestCase):
    def test_a_release_candidate_is_a_prerelease_named_for_its_version(self):
        self.build("4.0.0-rc.2")
        manifest = json.loads((self.release.out / "release.json").read_text(encoding="utf-8"))
        self.assertEqual((manifest["version"], manifest["tag"], manifest["prerelease"]),
                         ("4.0.0-rc.2", "v4.0.0-rc.2", True))
        self.assertTrue((self.release.out / "HVO-Roof-4.0.0-rc.2.zip").is_file())
        self.assertTrue((self.release.out / "hvo-roof-kiosk-4.0.0-rc.2-linux-arm64.tar.gz").is_file())
        self.assertEqual(manifest["images"]["controller"]["tag"], "4.0.0-rc.2")

    def test_a_dry_run_is_a_prerelease(self):
        self.build("4.0.0-dryrun.7")
        manifest = json.loads((self.release.out / "release.json").read_text(encoding="utf-8"))
        self.assertTrue(manifest["prerelease"])


class InstallSh(unittest.TestCase):
    SCRIPT = "#!/usr/bin/env bash\nf() {\n  local release_version=''  # written here\n  echo\n}\n"

    def test_a_release_candidate_s_version_is_written_in(self):
        self.assertEqual(release_assets.install_script(self.SCRIPT, "4.0.0-rc.1"),
                         "#!/usr/bin/env bash\nf() {\n  local release_version='4.0.0-rc.1'\n  echo\n}\n")

    def test_a_script_without_the_line_is_refused(self):
        for text in ("#!/usr/bin/env bash\n", "  local release_version='4.0.0'\n", self.SCRIPT + self.SCRIPT):
            with self.subTest(text=text):
                with self.assertRaisesRegex(release_assets.AssetError, "must name its release on one line"):
                    release_assets.install_script(text, "4.0.0")

    def test_a_version_that_is_not_a_release_s_is_refused(self):
        for version in ("v4.0.0", "4.0.0'; rm -rf ~; '", ""):
            with self.subTest(version=version):
                with self.assertRaisesRegex(release_assets.AssetError, "is not a release's version"):
                    release_assets.install_script(self.SCRIPT, version)

    def test_the_repository_s_copy_names_no_release(self):
        text = release_assets.INSTALL_SCRIPT.read_text(encoding="utf-8")
        self.assertEqual(len(release_assets.INSTALL_SCRIPT_VERSION.findall(text)), 1)


class AnotherRegistry(ReleaseTestCase):
    def test_release_json_and_the_compose_file_name_the_images_there(self):
        self.build(**{"--registry": "localhost:5000/hvo"})
        manifest = json.loads((self.release.out / "release.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["images"]["controller"]["reference"],
                         f"localhost:5000/hvo/roof-controller:4.0.0@{CONTROLLER_DIGEST}")
        self.assertEqual(manifest["images"]["hatEmulator"]["repository"], "localhost:5000/hvo/roof-hat-emulator")
        compose = (self.release.out / "docker-compose.yaml").read_text(encoding="utf-8")
        self.assertIn(f"image: localhost:5000/hvo/roof-hat-emulator:4.0.0@{EMULATOR_DIGEST}", compose)
        self.assertNotIn("ghcr.io", compose)


class TheUpgradeNotes(ReleaseTestCase):
    def notes(self, **files):
        """docs/upgrade-notes, with each X_Y_Z keyword's text (or bytes) as X.Y.Z.md."""
        folder = self.release.root / "upgrade-notes"
        folder.mkdir(exist_ok=True)
        for name, text in files.items():
            (folder / (name.replace("_", ".") + ".md")).write_bytes(text if isinstance(text, bytes) else text.encode("utf-8"))
        return str(folder)

    def upgrade_notes(self):
        return json.loads((self.release.out / "release.json").read_text(encoding="utf-8"))["upgradeNotes"]

    def test_release_json_carries_the_notes_text_without_the_blank_lines_around_it(self):
        self.build(**{"--upgrade-notes": self.notes(**{"4_0_0": "\nStop the roof before upgrading.\n\n- Then run it.\n\n"})})
        self.assertEqual(self.upgrade_notes(), [{"version": "4.0.0", "text": "Stop the roof before upgrading.\n\n- Then run it."}])

    def test_release_json_carries_every_releases_notes_up_to_its_own_oldest_first(self):
        mac_zip(self.release.mac, version="4.10.0")
        folder = self.notes(**{"4_10_0": "Ten.\n", "4_9_0": "Nine.\n", "4_0_0": "Zero.\n", "4_11_0": "Later.\n", "5_0_0": "Five.\n"})
        self.build("4.10.0", **{"--upgrade-notes": folder})
        self.assertEqual(self.upgrade_notes(), [{"version": "4.0.0", "text": "Zero."}, {"version": "4.9.0", "text": "Nine."},
                                                {"version": "4.10.0", "text": "Ten."}])

    def test_a_release_candidate_carries_its_final_releases_notes(self):
        mac_zip(self.release.mac, version="4.1.0")
        self.build("4.1.0-rc.1", **{"--upgrade-notes": self.notes(**{"4_0_0": "Zero.\n", "4_1_0": "One.\n", "4_2_0": "Two.\n"})})
        self.assertEqual([note["version"] for note in self.upgrade_notes()], ["4.0.0", "4.1.0"])

    def test_a_release_before_every_note_carries_none(self):
        self.build(**{"--upgrade-notes": self.notes(**{"4_0_1": "Later.\n"})})
        manifest = json.loads((self.release.out / "release.json").read_text(encoding="utf-8"))
        self.assertNotIn("upgradeNotes", manifest)

    def test_the_notes_are_not_an_asset(self):
        printed = self.build(**{"--upgrade-notes": self.notes(**{"4_0_0": "Notes.\n"})}).split()
        self.assertNotIn("4.0.0.md", printed)
        self.assertNotIn("4.0.0.md", (self.release.out / "SHA256SUMS").read_text(encoding="utf-8"))

    def test_empty_notes(self):
        self.refused("is empty: a release's upgrade notes say what someone upgrading must do",
                     **{"--upgrade-notes": self.notes(**{"4_0_0": " \n\n"})})

    def test_notes_that_are_not_utf_8(self):
        self.refused("is not UTF-8 text", **{"--upgrade-notes": self.notes(**{"4_0_0": b"caf\xe9\n"})})

    def test_notes_too_long_to_print(self):
        self.refused("keep them under 16384 and link to the rest",
                     **{"--upgrade-notes": self.notes(**{"4_0_0": "x" * (16 * 1024 + 1)})})

    def test_every_releases_notes_together_too_long_to_print(self):
        mac_zip(self.release.mac, version="4.8.0")
        folder = self.notes(**{f"4_{minor}_0": "x" * (16 * 1024) for minor in range(9)})
        self.refused("hold 147456 characters together; the installer prints them whole, so keep them under 131072",
                     "4.8.0", **{"--upgrade-notes": folder})

    def test_a_file_that_is_not_a_releases_notes(self):
        folder = self.notes(**{"4_0_0": "Zero.\n"})
        (pathlib.Path(folder) / "README.md").write_text("Notes.\n", encoding="utf-8")
        self.refused("README.md is not a release's upgrade notes", **{"--upgrade-notes": folder})

    def test_notes_that_are_a_link(self):
        folder = self.notes(**{"4_0_0": "Zero.\n"})
        (pathlib.Path(folder) / "3.9.0.md").symlink_to(pathlib.Path(folder) / "4.0.0.md")
        self.refused("3.9.0.md is not a release's upgrade notes", **{"--upgrade-notes": folder})

    def test_missing_notes(self):
        self.refused("is not a folder: --upgrade-notes is docs/upgrade-notes",
                     **{"--upgrade-notes": str(self.release.root / "upgrade-notes")})


class TheRefusals(ReleaseTestCase):
    def test_a_missing_runtime(self):
        (self.release.cli / "linux-x64" / "hvo-roof").unlink()
        (self.release.cli / "linux-x64").rmdir()
        self.refused("must hold exactly linux-arm64, linux-x64, osx-arm64; it lacks linux-x64")

    def test_a_runtime_this_script_does_not_release(self):
        (self.release.cli / "osx-x64").mkdir()
        self.refused("it also holds osx-x64, which this script does not release (add it to build/release-assets.py)")

    def test_a_program_for_the_wrong_processor(self):
        (self.release.cli / "linux-arm64" / "hvo-roof").write_bytes(X86_64)
        self.refused("linux-arm64/hvo-roof is built for x86-64, not aarch64")

    def test_a_mac_program_built_for_linux(self):
        (self.release.cli / "osx-arm64" / "hvo-roof").write_bytes(AARCH64)
        self.refused("osx-arm64/hvo-roof is built for aarch64, not macos-arm64")

    def test_an_intel_mac_program(self):
        (self.release.cli / "osx-arm64" / "hvo-roof").write_bytes(struct.pack("<II", 0xFEEDFACF, 0x01000007) + b"program")
        self.refused("osx-arm64/hvo-roof is built for macos-x64, not macos-arm64")

    def test_a_linux_program_built_for_the_mac(self):
        (self.release.cli / "linux-arm64" / "hvo-roof").write_bytes(mach_o_arm64())
        self.refused("linux-arm64/hvo-roof is built for macos-arm64, not aarch64")

    def test_a_program_that_is_not_one(self):
        (self.release.cli / "linux-x64" / "hvo-roof").write_bytes(b"#!/bin/sh\n")
        self.refused("linux-x64/hvo-roof is built for no known processor, not x86-64")

    def test_an_installer_runtime_missing(self):
        (self.release.installer / "osx-arm64" / "hvo-roof-install").unlink()
        (self.release.installer / "osx-arm64").rmdir()
        self.refused(f"The hvo-roof-install folder {self.release.installer} must hold exactly linux-arm64, linux-x64,"
                     " osx-arm64; it lacks osx-arm64")

    def test_an_installer_for_the_wrong_processor(self):
        (self.release.installer / "linux-arm64" / "hvo-roof-install").write_bytes(X86_64)
        self.refused("linux-arm64/hvo-roof-install is built for x86-64, not aarch64")

    def test_a_folder_without_the_installer_in_it(self):
        (self.release.installer / "linux-x64" / "hvo-roof-install").rename(self.release.installer / "linux-x64" / "hvo-roof")
        self.refused("linux-x64/hvo-roof-install was not found")

    def test_a_kiosk_file_missing(self):
        (self.release.kiosk / "hvo-roof-kiosk.service").unlink()
        self.refused("it lacks hvo-roof-kiosk.service")

    def test_a_kiosk_file_this_script_does_not_release(self):
        (self.release.kiosk / "notes.txt").write_text("notes\n", encoding="utf-8")
        self.refused("it also holds notes.txt")

    def test_a_kiosk_for_the_wrong_processor(self):
        (self.release.kiosk / "hvo-roof-kiosk").write_bytes(X86_64)
        self.refused("hvo-roof-kiosk is built for x86-64, not aarch64")

    def test_a_mac_app_of_another_version(self):
        mac_zip(self.release.mac, version="3.9.0")
        self.refused("holds version '3.9.0', not 4.0.0")

    def test_a_mac_app_zip_holding_more_than_the_app(self):
        mac_zip(self.release.mac, entries={"README.txt": "hello"})
        self.refused("must hold HVO Roof.app alone")

    def test_a_mac_app_that_is_not_for_arm64(self):
        mac_zip(self.release.mac, program=X86_64)
        self.refused("is not an arm64 Mac program")

    def test_a_mac_app_that_is_not_a_zip(self):
        self.release.mac.write_bytes(b"not a zip")
        self.refused("is not HVO Roof.app's zip")

    def test_an_output_folder_with_files_in_it(self):
        self.release.out.mkdir()
        (self.release.out / "stale").write_text("stale\n", encoding="utf-8")
        self.refused("must be a new or empty folder")

    def test_arguments_it_cannot_use(self):
        for option, value, message in (
                ("--version", "v4.0.0", "--version must be a product version"),
                ("--commit", "0123abc", "--commit must be a whole commit hash"),
                ("--created", "2026-10-01 12:00", "--created must be a UTC time"),
                ("--created", "2026-13-01T12:00:00Z", "--created must be a UTC time"),
                ("--controller-digest", "sha256:abc", "--controller-digest must be sha256:<64 hex digits>"),
                ("--emulator-digest", "1" * 64, "--emulator-digest must be sha256:<64 hex digits>"),
                ("--registry", "GHCR.io/Owner", "--registry must be a lower-case <registry>/<owner>")):
            with self.subTest(option=option, value=value):
                if option == "--version":
                    self.refused(message, value, status=2)
                else:
                    self.refused(message, status=2, **{option: value})
        self.assertFalse(self.release.out.exists())


if __name__ == "__main__":
    unittest.main()
