#!/usr/bin/env python3
"""Tests for build/release-compose.py (#63): the release compose file made from the Pi compose file, with the release's
images and nothing built, and the refusals of a version, digest or source it cannot convert. CI's Compose profiles step
checks the result with Docker Compose itself. From the repository root:

    python3 -m unittest discover -s tests/releasing -p 'test_*.py'
"""

import contextlib
import difflib
import importlib.util
import io
import pathlib
import re
import tempfile
import unittest

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("release_compose", REPOSITORY / "build" / "release-compose.py")
release_compose = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_compose)

SOURCE = release_compose.SOURCE.read_text(encoding="utf-8")
CONTROLLER_DIGEST = "sha256:" + "1" * 64
EMULATOR_DIGEST = "sha256:" + "2" * 64


def images(text):
    return re.findall(r"^\s*image: (\S+)$", text, re.MULTILINE)


def run(*arguments):
    """Runs the script's main; returns its exit status, standard output and standard error."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            status = release_compose.main(list(arguments))
        except SystemExit as exit:
            status = exit.code
    return status, out.getvalue(), err.getvalue()


class TheReleaseFile(unittest.TestCase):
    def setUp(self):
        self.result = release_compose.convert(SOURCE, "4.0.0")

    def test_every_service_names_a_released_image_at_the_version(self):
        self.assertEqual(
            set(images(self.result)),
            {"ghcr.io/hualapaivalley/roof-controller:4.0.0", "ghcr.io/hualapaivalley/roof-hat-emulator:4.0.0"})
        # The controller's anchor, the deployment check's, the HAT emulator and the emulated controller.
        self.assertEqual(len(images(self.result)), 4)

    def test_nothing_is_built_and_every_image_may_be_pulled(self):
        body = [line.split("#", 1)[0] for line in self.result.splitlines()]
        for word in ("build", "pull_policy", "ROOF_VERSION", "context:", "dockerfile:", "platforms:"):
            self.assertFalse([line for line in body if word in line], word)

    def test_the_header_names_the_release_and_pulls_instead_of_building(self):
        header = self.result.split("\nx-", 1)[0]
        self.assertTrue(header.startswith("# Roof controller 4.0.0 on the Raspberry Pi, from the release's images."))
        self.assertIn("/blob/v4.0.0/docs/deployment.md", header)
        self.assertIn("docker compose --profile pi pull && docker compose --profile pi run --rm roof-controller-check",
                      header)
        self.assertIn("docker compose --profile emulator up -d\n", header)
        self.assertNotIn("--build", header)
        self.assertNotIn("tests/emulator", header)

    def test_everything_else_is_the_source_file(self):
        # Line for line, less the build blocks, the image lines and the comments (the header's edits, and the
        # comments on what was left out).
        kept = [line for line in self.result.splitlines() if not line.lstrip().startswith(("#", "image:"))]
        source = [line for line in SOURCE.splitlines() if not line.lstrip().startswith(("#", "image:"))]
        dropped = []
        for tag, first, last, _, _ in difflib.SequenceMatcher(a=source, b=kept, autojunk=False).get_opcodes():
            self.assertIn(tag, ("equal", "delete"), f"{tag}: {source[first:last]}")
            if tag == "delete":
                dropped += source[first:last]
        allowed = re.compile(r"\s*(build:|context: \.\./\.\.|dockerfile: |args: \*build-args|platforms:|- linux/arm64|"
                             r"pull_policy: never|x-build-args: &build-args|ROOF_(VERSION|REVISION|CREATED): |$)")
        self.assertEqual([line for line in dropped if not allowed.match(line)], [])
        self.assertEqual(dropped.count(""), 1, "the blank line after the build arguments")

    def test_digests_pin_each_image(self):
        result = release_compose.convert(SOURCE, "4.1.0-rc.1", controller_digest=CONTROLLER_DIGEST,
                                         emulator_digest=EMULATOR_DIGEST)
        self.assertEqual(set(images(result)), {
            f"ghcr.io/hualapaivalley/roof-controller:4.1.0-rc.1@{CONTROLLER_DIGEST}",
            f"ghcr.io/hualapaivalley/roof-hat-emulator:4.1.0-rc.1@{EMULATOR_DIGEST}"})

    def test_another_registry(self):
        result = release_compose.convert(SOURCE, "4.0.0", registry="127.0.0.1:15000/hvo")
        self.assertEqual(set(images(result)),
                         {"127.0.0.1:15000/hvo/roof-controller:4.0.0", "127.0.0.1:15000/hvo/roof-hat-emulator:4.0.0"})


class TheCommandLine(unittest.TestCase):
    def test_writes_the_file(self):
        with tempfile.TemporaryDirectory() as directory:
            output = pathlib.Path(directory) / "docker-compose.yaml"
            status, out, err = run("--version", "4.0.0", "--controller-digest", CONTROLLER_DIGEST, "-o", str(output))
            self.assertEqual((status, out, err), (0, "", ""))
            self.assertIn(f"roof-controller:4.0.0@{CONTROLLER_DIGEST}", output.read_text(encoding="utf-8"))

    def test_writes_to_standard_output_without_an_output(self):
        status, out, _ = run("--version", "4.0.0")
        self.assertEqual(status, 0)
        self.assertEqual(out, release_compose.convert(SOURCE, "4.0.0"))

    def test_refuses_what_is_not_a_product_version(self):
        for version in ("v4.0.0", "4.0", "4.0.0+build.1", "04.0.0", "4.0.0-", "latest", ""):
            with self.subTest(version=version):
                status, out, err = run("--version", version)
                self.assertEqual(status, 2)
                self.assertEqual(out, "")
                self.assertIn("--version must be a product version", err)

    def test_refuses_a_digest_that_is_not_sha256(self):
        for value in ("1" * 64, "sha256:" + "1" * 63, "sha256:" + "A" * 64, "sha512:" + "1" * 64):
            for option in ("--controller-digest", "--emulator-digest"):
                with self.subTest(option=option, value=value):
                    status, _, err = run("--version", "4.0.0", option, value)
                    self.assertEqual(status, 2)
                    self.assertIn(f"{option} must be sha256:<64 hex digits>", err)

    def test_refuses_a_registry_docker_would_not_take(self):
        for registry in ("GHCR.io/owner", "ghcr.io/owner/", "-ghcr.io", ""):
            with self.subTest(registry=registry):
                status, _, err = run("--version", "4.0.0", f"--registry={registry}")
                self.assertEqual(status, 2)
                self.assertIn("--registry must be", err)


class ASourceItCannotConvert(unittest.TestCase):
    def convert(self, source):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "docker-compose.yaml"
            path.write_text(source, encoding="utf-8")
            return run("--version", "4.0.0", "--source", str(path))

    def assertRefused(self, source, message):
        status, out, err = self.convert(source)
        self.assertEqual(status, 1)
        self.assertEqual(out, "")
        self.assertIn(message, err)

    def test_an_image_with_no_released_image(self):
        self.assertRefused(SOURCE.replace("image: hvo/roof-hat-emulator:dev", "image: hvo/another:dev"),
                           "the image hvo/another:dev has no released image")

    def test_an_image_no_longer_named(self):
        self.assertRefused(SOURCE.replace("image: hvo/roof-controller:emulated", "image: hvov9/roof-controller:v4"),
                           "the source no longer names hvo/roof-controller:emulated")

    def test_a_header_edit_that_no_longer_matches(self):
        self.assertRefused(SOURCE.replace("--profile emulator up -d --build", "--profile emulator up --build -d"),
                           "holds 'docker compose --profile emulator up -d --build' 0 times, not once")

    def test_another_first_line(self):
        self.assertRefused("# Something else.\n" + SOURCE.split("\n", 1)[1], "the source's first line is not")

    def test_a_build_that_is_not_a_mapping(self):
        self.assertRefused(SOURCE.replace("    build:\n      context: ../..\n", "    build: ../..\n    x-context:\n", 1),
                           "still holds 'build'")

    def test_a_pull_policy_other_than_never(self):
        self.assertRefused(SOURCE.replace("pull_policy: never", "pull_policy: always"), "still holds 'pull_policy'")

    def test_an_image_line_the_conversion_does_not_see(self):
        self.assertRefused(SOURCE.replace("  image: hvov9/roof-controller:v4\n  build:", "  { image: hvov9/roof-controller:v4 }\n  build:", 1),
                           "names an image that is not released")

    def test_a_missing_source(self):
        status, _, err = run("--version", "4.0.0", "--source", "/nonexistent/docker-compose.yaml")
        self.assertEqual(status, 1)
        self.assertIn("/nonexistent/docker-compose.yaml", err)


if __name__ == "__main__":
    unittest.main()
