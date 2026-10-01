#!/usr/bin/env python3
"""Tests for build/secret-scan.py: a made-up credential in a made-up repository, found in the files git tracks or
would add, in the files named, in a commit's message and in the lines a commit added, as itself, its base64 and its
password, and never printed; the tokens and keys it looks for, and what it does not mistake for one; what stops it;
then this repository, as CI checks it. From the repository root:

    python3 -m unittest discover -s tests/security -p 'test_*.py'
"""

import base64
import contextlib
import importlib.util
import io
import pathlib
import subprocess
import tempfile
import unittest

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("secret_scan", REPOSITORY / "build" / "secret-scan.py")
secret_scan = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(secret_scan)

CREDENTIAL = "watcher:Corvus-Hydra-42"
PASSWORD = "Corvus-Hydra-42"
BASE64 = base64.b64encode(CREDENTIAL.encode()).decode()
# Made up, in the shapes the scan looks for, and put together so that this file holds none of them.
GITHUB_TOKEN = "ghp_" + "A1b2C3d4E5" * 4
PRIVATE_KEY = "-----BEGIN PRIVATE KEY-----\n" + "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC" * 2 + "\n-----END PRIVATE KEY-----\n"
AWS_KEY = "AKIA" + "ABCDEFGHIJKLMNOP"
SLACK_TOKEN = "xoxb" + "-1234567890-abcdefghij"


def run(*arguments):
    """Runs the script's main; returns its exit status, standard output and standard error."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        status = secret_scan.main(list(arguments))
    return status, out.getvalue(), err.getvalue()


class ARepository(unittest.TestCase):
    """A repository whose first commit holds the credential as the camera proxy did, and whose second removes it."""

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.repo = pathlib.Path(folder.name)
        self.git("init", "--quiet", "--initial-branch=main")
        self.write("Camera.cs", f'var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("{CREDENTIAL}"));\n')
        self.write(".gitignore", "ignored.txt\n")
        self.commit("Camera proxy")
        self.leak = self.git("rev-parse", "--short", "HEAD").strip() + ":Camera.cs"
        self.write("Camera.cs", "var auth = options.Authorization;\n")
        self.commit("Read the camera's credential from settings")

    def git(self, *arguments):
        return subprocess.run(["git", "-C", str(self.repo), "-c", "user.name=Test", "-c", "user.email=test@example.com",
                               *arguments], capture_output=True, text=True, check=True).stdout

    def write(self, path, text):
        (self.repo / path).write_text(text)

    def commit(self, message):
        self.git("add", "--all")
        self.git("commit", "--quiet", "--allow-empty", "-m", message)

    def scan(self, *arguments):
        status, out, err = run("--repo", str(self.repo), "--leak", self.leak, *arguments)
        for secret in (CREDENTIAL, PASSWORD, BASE64, GITHUB_TOKEN, AWS_KEY, SLACK_TOKEN, PRIVATE_KEY.split("\n")[1]):
            self.assertNotIn(secret, out + err, "the scan printed what it found")
        return status, out, err


class TheCredential(ARepository):
    def test_passes_when_no_copy_is_left(self):
        self.write("README.md", "The camera's credential is a setting.\n")
        self.commit("Docs")
        self.assertEqual(self.scan(), (0, "PASS\n", ""))

    def test_is_found_in_a_tracked_file_by_its_line(self):
        self.write("notes.md", f"one\ntwo {CREDENTIAL}\n")
        self.commit("Notes")
        status, out, _ = self.scan()
        self.assertEqual(status, 1)
        self.assertIn("notes.md:2: the Blue Iris credential (#22)", out)
        self.assertTrue(out.endswith("FAIL (2 found)\n"), out)  # the file, and the lines its commit added

    def test_is_found_as_its_password_and_its_base64(self):
        self.write("a.txt", f"password = {PASSWORD}\n")
        self.write("b.txt", f"Authorization: Basic {BASE64}\n")
        status, out, _ = self.scan()
        self.assertEqual(status, 1)
        self.assertIn("a.txt:1: the Blue Iris credential (#22)", out)
        self.assertIn("b.txt:1: the Blue Iris credential (#22)", out)

    def test_is_found_in_a_file_git_would_add_but_not_one_it_ignores(self):
        self.write("new.txt", PASSWORD)
        self.write("ignored.txt", PASSWORD)
        status, out, _ = self.scan()
        self.assertEqual(status, 1)
        self.assertIn("new.txt:1:", out)
        self.assertNotIn("ignored.txt", out)

    def test_is_found_in_a_file_named(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        body = pathlib.Path(folder.name) / "body.md"
        body.write_text(f"## Summary\n\nThe header was Basic {BASE64}.\n")
        status, out, _ = self.scan(str(body))
        self.assertEqual(status, 1)
        self.assertIn(f"{body}:3: the Blue Iris credential (#22)", out)

    def test_is_found_in_a_commit_message(self):
        self.git("commit", "--quiet", "--allow-empty", "-m", f"Try {PASSWORD}")
        commit = self.git("rev-parse", "--short", "HEAD").strip()
        status, out, _ = self.scan()
        self.assertEqual(status, 1)
        self.assertEqual(out, f"commit {commit}, its message: the Blue Iris credential (#22)\nFAIL (1 found)\n")

    def test_is_found_in_a_commit_whose_lines_a_later_commit_removed(self):
        self.write("debug.txt", f"{CREDENTIAL}\n")
        self.commit("Debugging")
        commit = self.git("rev-parse", "--short", "HEAD").strip()
        self.write("debug.txt", "\n")
        self.commit("Done debugging")
        status, out, _ = self.scan()
        self.assertEqual(status, 1)
        self.assertEqual(out, f"commit {commit}, its added lines: the Blue Iris credential (#22)\nFAIL (1 found)\n")

    def test_in_the_commits_before_the_range_is_not_looked_for(self):
        self.write("debug.txt", f"{CREDENTIAL}\n")
        self.commit("Debugging")
        self.write("debug.txt", "\n")
        self.commit("Done debugging")
        self.assertEqual(self.scan("--range", "HEAD~1..HEAD"), (0, "PASS\n", ""))


class TokensAndKeys(ARepository):
    def test_a_github_token_and_a_private_key_are_found(self):
        self.write("token.txt", f"GH_TOKEN={GITHUB_TOKEN}\n")
        self.write("key.pem", "\n" + PRIVATE_KEY)
        status, out, _ = self.scan("--range", "HEAD..HEAD")
        self.assertEqual(status, 1)
        self.assertIn("token.txt:1: a GitHub token", out)
        self.assertIn("key.pem:2: a private key", out)

    def test_an_aws_key_and_a_slack_token_are_found(self):
        self.write("cloud.txt", f"{AWS_KEY}\n{SLACK_TOKEN}\n")
        status, out, _ = self.scan("--range", "HEAD..HEAD")
        self.assertEqual(status, 1)
        self.assertIn("cloud.txt:1: an AWS access key", out)
        self.assertIn("cloud.txt:2: a Slack token", out)

    def test_a_key_blocks_start_or_a_tokens_prefix_alone_is_not_one(self):
        self.write("Tests.cs", 'key.Should().StartWith("-----BEGIN PRIVATE KEY-----");\nvar prefix = "ghp_";\n')
        self.assertEqual(self.scan("--range", "HEAD..HEAD"), (0, "PASS\n", ""))


class WhatStopsIt(ARepository):
    def test_a_commit_the_clone_lacks(self):
        status, out, err = self.scan("--leak", "0000000:Camera.cs")
        self.assertEqual((status, out), (2, ""))
        self.assertIn("git fetch --unshallow", err)

    def test_a_file_with_no_literal(self):
        commit = self.leak.split(":")[0]
        status, out, err = self.scan("--leak", f"{commit}:.gitignore")
        self.assertEqual((status, out), (2, ""))
        self.assertIn('holds no GetBytes("...") literal', err)

    def test_a_file_named_that_is_not_there(self):
        status, out, err = self.scan(str(self.repo / "missing.md"))
        self.assertEqual((status, out), (2, ""))
        self.assertIn("missing.md: No such file or directory", err)


class ThisRepository(unittest.TestCase):
    def test_holds_no_secret(self):
        commit = secret_scan.LEAK.split(":")[0]
        if subprocess.run(["git", "-C", str(REPOSITORY), "cat-file", "-e", commit + "^{commit}"],
                          capture_output=True).returncode != 0:
            self.skipTest(f"{commit} is not in this clone (a shallow one); CI's secret-scan job fetches it")
        status, out, err = run()
        self.assertEqual((status, err), (0, ""), out)


if __name__ == "__main__":
    unittest.main()
