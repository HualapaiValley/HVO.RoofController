#!/usr/bin/env python3
"""Tests for build/release-assets.py (#64): a release's assets gathered from CI's artifacts, with release.json and
SHA256SUMS, and the refusals of a folder or file that is not exactly what a release holds. The programs are stand-ins
with the right file headers, so no .NET build is needed. From the repository root:

    python3 -m unittest discover -s tests/releasing -p 'test_*.py'
"""

import contextlib
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

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("release_assets", REPOSITORY / "build" / "release-assets.py")
release_assets = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_assets)

COMMIT = "0123456789abcdef0123456789abcdef01234567"
CREATED = "2026-10-01T12:00:00Z"
CREATED_SECONDS = 1790856000
CONTROLLER_DIGEST = "sha256:" + "1" * 64
EMULATOR_DIGEST = "sha256:" + "2" * 64


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
        self.kiosk = root / "kiosk"
        self.mac = root / "hvo-roof-mac.zip"
        self.out = root / "out"
        for runtime, program in (("linux-arm64", AARCH64), ("linux-x64", X86_64), ("osx-arm64", mach_o_arm64(b"cli"))):
            (self.cli / runtime).mkdir(parents=True)
            (self.cli / runtime / "hvo-roof").write_bytes(program)
        self.kiosk.mkdir()
        (self.kiosk / "hvo-roof-kiosk").write_bytes(elf(183, b"kiosk"))
        for name in ("hvo-roof-kiosk.service", "99-hvo-roof-kiosk-backlight.rules", "appsettings.Local.example.json"):
            (self.kiosk / name).write_text(f"{name}\n", encoding="utf-8")
        mac_zip(self.mac)

    def arguments(self, version="4.0.0", out=None, **overrides):
        values = {
            "--version": version, "--commit": COMMIT, "--created": CREATED,
            "--controller-digest": CONTROLLER_DIGEST, "--emulator-digest": EMULATOR_DIGEST,
            "--cli": str(self.cli), "--kiosk": str(self.kiosk), "--mac-zip": str(self.mac),
            "-o": str(out or self.out),
        }
        values.update(overrides)
        return [item for pair in values.items() for item in pair]


def run(arguments):
    """Runs the script's main; returns its exit status, standard output and standard error."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            status = release_assets.main(arguments)
        except SystemExit as exit:
            status = exit.code
    return status, out.getvalue(), err.getvalue()


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
        "hvo-roof-kiosk-4.0.0-linux-arm64.tar.gz": ("kiosk", "linux-arm64"),
        "HVO-Roof-4.0.0.zip": ("mac-app", "osx-arm64"),
        "docker-compose.yaml": ("compose", None),
        "deploy-roofcontroller-rpi.sh": ("deploy-script", None),
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
        self.assertEqual(self.manifest["images"], {
            "controller": {
                "repository": "ghcr.io/hualapaivalley/roof-controller", "tag": "4.0.0", "digest": CONTROLLER_DIGEST,
                "reference": f"ghcr.io/hualapaivalley/roof-controller:4.0.0@{CONTROLLER_DIGEST}",
                "platforms": ["linux/amd64", "linux/arm64"]},
            "hatEmulator": {
                "repository": "ghcr.io/hualapaivalley/roof-hat-emulator", "tag": "4.0.0", "digest": EMULATOR_DIGEST,
                "reference": f"ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0@{EMULATOR_DIGEST}",
                "platforms": ["linux/amd64", "linux/arm64"]},
        })

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
                         "hvo-roof-kiosk.service")})

    def test_sha256sums_covers_every_asset_and_release_json_as_sha256sum_writes_it(self):
        lines = (self.out / "SHA256SUMS").read_text(encoding="utf-8").splitlines()
        names = sorted([*self.EXPECTED, "release.json"])
        self.assertEqual(lines, [f"{sha256(self.out / name)}  {name}" for name in names])

    def test_the_programs_are_ci_s_files_and_executable(self):
        for runtime in ("linux-arm64", "linux-x64", "osx-arm64"):
            asset = self.out / f"hvo-roof-{runtime}"
            self.assertEqual(asset.read_bytes(), (self.release.cli / runtime / "hvo-roof").read_bytes())
            self.assertTrue(asset.stat().st_mode & stat.S_IXUSR, runtime)
        script = self.out / "deploy-roofcontroller-rpi.sh"
        self.assertEqual(script.read_bytes(), release_assets.DEPLOY_SCRIPT.read_bytes())
        self.assertTrue(script.stat().st_mode & stat.S_IXUSR)

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
                 (f"{folder}/hvo-roof-kiosk.service", False, "0o644")])
            for member in members:
                self.assertEqual((member.uid, member.gid, member.uname, member.gname, member.mtime),
                                 (0, 0, "root", "root", CREATED_SECONDS), member.name)
            self.assertEqual(tar.extractfile(f"{folder}/hvo-roof-kiosk").read(), elf(183, b"kiosk"))

    def test_the_same_inputs_make_the_same_kiosk_tarball(self):
        again = self.release.root / "again"
        self.build(out=again)
        name = "hvo-roof-kiosk-4.0.0-linux-arm64.tar.gz"
        self.assertEqual((again / name).read_bytes(), (self.out / name).read_bytes())


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
