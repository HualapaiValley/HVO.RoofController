#!/usr/bin/env python3
"""Tests for the state contracts (#130): each image's io.hvo.roof.state-contracts label is release.json's stateContracts
for it (build/release-assets.py), each a contract the installer reads, and identityStoreSchema and the controller's
io.hvo.roof.identity-schema label agree with it. From the repository root:

    python3 -m unittest discover -s tests/releasing -p 'test_*.py'
"""

import importlib.util
import json
import pathlib
import re
import unittest

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("release_assets", REPOSITORY / "build" / "release-assets.py")
release_assets = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_assets)

DOCKERFILES = {
    "controller": REPOSITORY / "src" / "HVO.RoofControllerV4.RPi" / "Dockerfile",
    "hatEmulator": REPOSITORY / "src" / "HVO.RoofControllerV4.Emulator" / "Dockerfile",
}


def label(dockerfile, name):
    """The value of a LABEL in a Dockerfile, its double quotes and backslashes unescaped; None when it has none."""
    match = re.search(rf'{re.escape(name)}="((?:[^"\\]|\\.)*)"', dockerfile.read_text(encoding="utf-8"))
    return None if match is None else re.sub(r"\\(.)", r"\1", match.group(1))


class TheStateContracts(unittest.TestCase):
    def test_each_image_declares_in_its_label_what_release_json_says(self):
        self.assertEqual(set(DOCKERFILES), set(release_assets.STATE_CONTRACTS))
        for image, dockerfile in DOCKERFILES.items():
            with self.subTest(image=image):
                declared = label(dockerfile, "io.hvo.roof.state-contracts")
                self.assertIsNotNone(declared, f"{dockerfile} has no io.hvo.roof.state-contracts label")
                self.assertEqual(json.loads(declared), release_assets.STATE_CONTRACTS[image])

    def test_each_store_writes_a_schema_it_reads(self):
        for image, stores in release_assets.STATE_CONTRACTS.items():
            for store, contract in stores.items():
                with self.subTest(image=image, store=store):
                    self.assertEqual({"writes", "reads"}, set(contract))
                    self.assertEqual({"min", "max"}, set(contract["reads"]))
                    numbers = [contract["reads"]["min"], contract["writes"], contract["reads"]["max"]]
                    self.assertTrue(all(type(number) is int and number >= 1 for number in numbers), numbers)
                    self.assertEqual(sorted(numbers), numbers, "min <= writes <= max")

    def test_the_controller_keeps_an_identity_store_and_settings_and_the_emulator_nothing(self):
        self.assertEqual({"identity", "settings"}, set(release_assets.STATE_CONTRACTS["controller"]))
        self.assertEqual({}, release_assets.STATE_CONTRACTS["hatEmulator"])

    def test_the_identity_schema_label_is_the_newest_identity_schema_the_controller_reads(self):
        newest = release_assets.STATE_CONTRACTS["controller"]["identity"]["reads"]["max"]
        self.assertEqual(str(newest), label(DOCKERFILES["controller"], "io.hvo.roof.identity-schema"))


if __name__ == "__main__":
    unittest.main()
