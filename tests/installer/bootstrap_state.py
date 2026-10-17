"""Preserved state of the historical installer's fixture during a rig bootstrap."""

import hashlib
import json
import sys
from pathlib import Path


def snapshot(root=Path("/")):
    def path(name):
        return root / name.lstrip("/")

    def hash_value(value):
        return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()

    identity = json.loads(path("/var/lib/hvo-roof/identity/identity.json").read_text())
    assert identity.get("kioskCredential") is None, "existing installation gained commissioning access"
    assert not path("/var/lib/hvo-roof-commissioning/first-install-claimed").exists(), "existing installation gained a fresh claim"
    settings = json.loads(path("/etc/hvo-roof/config/appsettings.Local.json").read_text())
    assert settings["RoofControllerUi"]["DefaultCamera"] == "bootstrap-preservation-fixture", "non-default settings fixture lost"
    files = {}
    for folder in ["/etc/hvo-roof/secrets", "/etc/hvo-roof/ca", "/etc/hvo-roof/config", "/etc/hvo-roof/https", "/var/lib/hvo-roof/settings-secrets"]:
        assert path(folder).is_dir(), f"missing preservation fixture directory: {folder}"
        for file in path(folder).rglob("*"):
            if file.is_file():
                files["/" + file.relative_to(root).as_posix()] = hashlib.sha256(file.read_bytes()).hexdigest()
    for required in ["/etc/hvo-roof/ca.crt", "/etc/hvo-roof/ca/ca.key", "/etc/hvo-roof/config/appsettings.Local.json", "/etc/hvo-roof/https/roof-controller.pfx"]:
        assert path(required).is_file(), f"missing preservation fixture file: {required}"
        files[required] = hashlib.sha256(path(required).read_bytes()).hexdigest()
    record = json.loads(path("/etc/hvo-roof/install.json").read_text())
    # The pinned installer predates resource records. Its absent field means today's
    # mandatory PID default, with neither CPU nor memory capped. Keep explicit values
    # intact so a changed PID limit or an added CPU/memory cap still fails comparison.
    controller = record.get("controller")
    if isinstance(controller, dict) and "resources" not in controller:
        controller["resources"] = {"pidsLimit": 512}
    return {
        "users": hash_value(identity.get("users")),
        "nativePrincipals": hash_value(identity.get("linuxPrincipals")),
        "files": files,
        "choices": hash_value({key: record.get(key) for key in ["roles", "hat", "controller", "kiosk"]}),
    }


if __name__ == "__main__":
    Path(sys.argv[1]).write_text(json.dumps(snapshot(), sort_keys=True))
