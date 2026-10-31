"""The bootstrap comparison permits the new PID default while preserving old choices."""

import copy
import json
import tempfile
import unittest
from pathlib import Path

from bootstrap_state import snapshot


class BootstrapStateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.record = {"roles": ["rig"], "hat": "emulator", "controller": {"connection": "private-ca", "hostNames": ["roof-rig"]}}
        self.identity = {"users": [{"name": "fixture-person"}], "linuxPrincipals": []}
        for folder in ["/etc/hvo-roof/secrets", "/etc/hvo-roof/ca", "/etc/hvo-roof/config", "/etc/hvo-roof/https", "/var/lib/hvo-roof/settings-secrets", "/var/lib/hvo-roof/identity"]:
            self.path(folder).mkdir(parents=True)
        for file in ["/etc/hvo-roof/ca.crt", "/etc/hvo-roof/ca/ca.key", "/etc/hvo-roof/https/roof-controller.pfx", "/etc/hvo-roof/secrets/fixture"]:
            self.path(file).write_text("fixture bytes")
        self.write_json("/etc/hvo-roof/config/appsettings.Local.json", {"RoofControllerUi": {"DefaultCamera": "bootstrap-preservation-fixture"}})
        self.write_json("/var/lib/hvo-roof/identity/identity.json", self.identity)
        self.write_record()

    def path(self, name):
        return self.root / name.lstrip("/")

    def write_json(self, name, value):
        self.path(name).write_text(json.dumps(value))

    def write_record(self):
        self.write_json("/etc/hvo-roof/install.json", self.record)

    def test_historical_record_and_explicit_pid_default_preserve_the_same_choices(self):
        before = snapshot(self.root)
        self.record["controller"]["resources"] = {"pidsLimit": 512}
        self.write_record()
        self.assertEqual(before, snapshot(self.root))

    def test_added_cpu_memory_or_different_pid_limit_changes_preserved_choices(self):
        before = snapshot(self.root)
        for resources in [{"cpus": 1, "pidsLimit": 512}, {"memory": "2g", "pidsLimit": 512}, {"pidsLimit": 1024}, {}]:
            with self.subTest(resources=resources):
                self.record["controller"]["resources"] = resources
                self.write_record()
                self.assertNotEqual(before["choices"], snapshot(self.root)["choices"])

    def test_changes_to_each_preexisting_choice_are_detected(self):
        before = snapshot(self.root)
        original = copy.deepcopy(self.record)
        for key, value in [("roles", ["controller"]), ("hat", "real"), ("controller", {"connection": "private-ca", "hostNames": ["changed"]}), ("kiosk", {"enabled": True})]:
            with self.subTest(key=key):
                self.record = copy.deepcopy(original)
                self.record[key] = value
                self.write_record()
                self.assertNotEqual(before["choices"], snapshot(self.root)["choices"])

    def test_changed_people_or_native_principals_are_detected(self):
        before = snapshot(self.root)
        for key in ["users", "linuxPrincipals"]:
            with self.subTest(key=key):
                changed = copy.deepcopy(self.identity)
                changed[key] = [{"name": "changed"}]
                self.write_json("/var/lib/hvo-roof/identity/identity.json", changed)
                self.assertNotEqual(before[key if key == "users" else "nativePrincipals"], snapshot(self.root)[key if key == "users" else "nativePrincipals"])

    def test_a_recorded_sign_in_is_not_a_change_but_every_other_field_is(self):
        self.identity = {"users": [{"name": "fixture-person", "displayName": "Fixture Person"}],
                         "linuxPrincipals": [{"id": "fixture-principal", "name": "fixture-linux"}]}
        self.write_json("/var/lib/hvo-roof/identity/identity.json", self.identity)
        before = snapshot(self.root)
        signed_in = copy.deepcopy(self.identity)
        signed_in["users"][0]["lastLoginUtc"] = "2026-10-30T12:00:00+00:00"
        signed_in["linuxPrincipals"][0]["lastLoginUtc"] = "2026-10-30T12:00:00+00:00"
        self.write_json("/var/lib/hvo-roof/identity/identity.json", signed_in)
        self.assertEqual(before, snapshot(self.root))
        for key, field, value in [("users", "displayName", "Someone Else"), ("users", "role", "RoofAdmin"),
                                  ("linuxPrincipals", "name", "changed"), ("linuxPrincipals", "enabled", False)]:
            with self.subTest(key=key, field=field):
                changed = copy.deepcopy(signed_in)
                changed[key][0][field] = value
                self.write_json("/var/lib/hvo-roof/identity/identity.json", changed)
                name = "users" if key == "users" else "nativePrincipals"
                self.assertNotEqual(before[name], snapshot(self.root)[name])

    def test_changed_configuration_or_secret_file_bytes_are_detected(self):
        before = snapshot(self.root)
        self.path("/etc/hvo-roof/secrets/fixture").write_text("changed fixture bytes")
        self.assertNotEqual(before["files"], snapshot(self.root)["files"])
        self.path("/etc/hvo-roof/secrets/fixture").write_text("fixture bytes")
        self.write_json("/etc/hvo-roof/config/appsettings.Local.json", {"RoofControllerUi": {"DefaultCamera": "bootstrap-preservation-fixture", "Extra": True}})
        self.assertNotEqual(before["files"], snapshot(self.root)["files"])

    def test_lost_nondefault_setting_is_refused(self):
        self.write_json("/etc/hvo-roof/config/appsettings.Local.json", {"RoofControllerUi": {"DefaultCamera": "changed"}})
        with self.assertRaisesRegex(AssertionError, "non-default settings fixture lost"):
            snapshot(self.root)

    def test_commissioning_access_or_a_fresh_claim_is_refused(self):
        self.identity["kioskCredential"] = {"fixture": True}
        self.write_json("/var/lib/hvo-roof/identity/identity.json", self.identity)
        with self.assertRaisesRegex(AssertionError, "commissioning access"):
            snapshot(self.root)
        self.identity.pop("kioskCredential")
        self.write_json("/var/lib/hvo-roof/identity/identity.json", self.identity)
        claim = self.path("/var/lib/hvo-roof-commissioning/first-install-claimed")
        claim.parent.mkdir()
        claim.write_text("fixture")
        with self.assertRaisesRegex(AssertionError, "fresh claim"):
            snapshot(self.root)


if __name__ == "__main__":
    unittest.main()
