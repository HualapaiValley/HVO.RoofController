#!/usr/bin/env python3
"""Assemble a private single-architecture release from real published files for the disposable guest.

This does not build, publish, push, pull, install or start anything. Image digests must already exist
in the guest's isolated registry. Real ELF inputs are validated; nothing substitutes fake binaries.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release_assets", ROOT / "build/release-assets.py")
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--registry", required=True, help="the guest-private registry and owner prefix")
    parser.add_argument("--controller-digest", required=True)
    parser.add_argument("--emulator-digest", required=True)
    parser.add_argument("--installer", required=True, type=Path)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--native-worker", type=Path,
                        help="required for schema2/schema3; omit for a genuine schema1 legacy bundle")
    parser.add_argument("--kiosk", type=Path, help="optional complete published kiosk asset directory")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--identity-schema", type=int, choices=(1, 2, 3), default=2,
                        help="1 only for a genuine legacy image used to prove downgrade refusal")
    args = parser.parse_args()
    if args.identity_schema != 1 and args.native_worker is None:
        parser.error("--native-worker is required unless --identity-schema 1 is selected")
    if not assets.release_compose.VERSION.fullmatch(args.version):
        parser.error("invalid release version")
    if not assets.COMMIT.fullmatch(args.commit):
        parser.error("commit must be a full lowercase source commit hash")
    if not assets.release_compose.REGISTRY_PATTERN.fullmatch(args.registry):
        parser.error("registry must be a valid registry/owner prefix")
    for value in (args.controller_digest, args.emulator_digest):
        if not assets.release_compose.DIGEST.fullmatch(value):
            parser.error("image digests must be sha256 plus 64 lowercase hex digits")
    if args.output.exists() and any(args.output.iterdir()):
        parser.error("output must be new or empty")
    programs = {
        "hvo-roof-install-linux-arm64": (args.installer, "installer"),
        "hvo-roof-linux-arm64": (args.cli, "cli"),
    }
    if args.native_worker is not None:
        programs["hvo-roof-linux-auth-linux-arm64"] = (args.native_worker, "linux-auth")
    for path, _ in programs.values():
        if not path.is_file() or path.is_symlink() or assets.processor(path) != "aarch64":
            parser.error(f"{path} must be the real published ARM64 single-file executable")
    args.output.mkdir(parents=True, exist_ok=True)
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    listed = {}
    contents = {}
    for name, (path, kind) in programs.items():
        shutil.copyfile(path, args.output / name)
        (args.output / name).chmod(0o755)
        listed[name] = (kind, "linux-arm64")
    if args.kiosk:
        name, hashes = assets.kiosk_asset(args.kiosk, args.output, args.version, assets.created_time(now))
        listed[name] = ("kiosk", "linux-arm64")
        contents[name] = hashes
    manifest = assets.manifest(args.output, listed, args.version, args.commit, now, args.registry,
                              {"controller": args.controller_digest, "hatEmulator": args.emulator_digest}, contents)
    manifest["identityStoreSchema"] = args.identity_schema
    manifest["testBundle"] = True
    manifest["images"]["controller"]["platforms"] = ["linux/arm64"]
    manifest["images"]["hatEmulator"]["platforms"] = ["linux/arm64"]
    (args.output / "release.json").write_text(json.dumps(manifest, indent=2) + "\n")
    names = sorted([*listed, "release.json"])
    (args.output / "SHA256SUMS").write_text("".join(f"{assets.sha256(args.output / name)}  {name}\n" for name in names))
    print(json.dumps({"version": args.version, "assets": names, "identityStoreSchema": args.identity_schema}))


if __name__ == "__main__":
    main()
