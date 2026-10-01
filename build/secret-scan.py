#!/usr/bin/env python3
"""Looks for secrets in what is about to be made public: the repository is public, so run it before a commit, a push,
and posting an issue, a pull request or a comment (AGENTS.md).

  build/secret-scan.py [--range <from>..<to>] [--leak <commit>:<path>] [--repo <folder>] [<file>...]

It looks in every file git tracks or would add (as it is in the working tree, so edits not yet committed count), in the
message and the added lines of each commit in the range, and in each file named, such as the body of an issue, a pull
request or a comment saved before posting it. It looks for:

  - the Blue Iris credential the camera proxy once held (#22), read from the file and commit that held it (--leak),
    with its base64 and its password: the history still holds it, so a copy is found wherever it reappears;
  - GitHub, AWS and Slack tokens, and private keys in PEM form.

The range is by default every commit since the credential's, so a branch's commits are all looked at, even one whose
lines a later commit removed. A clone without that commit (a shallow one) cannot be checked: fetch the history first
(git fetch --unshallow). It prints where each one is, as <where>: <what>, and never what it found, then PASS or
FAIL (<n> found). It exits 0 when it found none, 1 when it found one and 2 when it could not look.
"""

import argparse
import base64
import re
import subprocess
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent

# The commit and file that held the camera proxy's Blue Iris credential, as GetBytes("<user>:<password>").
LEAK = "99e51a3:src/HVO.RoofControllerV4.RPi/Controllers/CameraController.cs"
LEAK_LITERAL = re.compile(r'GetBytes\("([^"]+)"\)')
CREDENTIAL = "the Blue Iris credential (#22)"

# Each matches a secret whole, so a test that names the start of a PEM block, or a token's prefix, is not one.
PATTERNS = (
    ("a GitHub token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
    ("a GitHub token", re.compile(r"\bgithub_pat_[A-Za-z0-9_]{60,}\b")),
    ("an AWS access key", re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
    ("a Slack token", re.compile(r"\bxox[abposr]-[A-Za-z0-9-]{10,}\b")),
    ("a private key", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----\s*[A-Za-z0-9+/=]{40,}")),
)


class CannotLook(Exception):
    """What stops the scan before it has looked: the commit or literal is missing, or git failed."""


def git(repo, *arguments):
    result = subprocess.run(["git", "-C", str(repo), *arguments], capture_output=True, text=True)
    if result.returncode != 0:
        raise CannotLook(f"git {' '.join(arguments)}: {result.stderr.strip() or 'failed'}")
    return result.stdout


def needles(repo, leak):
    """The credential as the leaked file held it, its base64 (as in an Authorization header) and its password."""
    try:
        source = git(repo, "show", leak)
    except CannotLook as error:
        raise CannotLook(f"{error}; fetch the history first (git fetch --unshallow)") from None
    match = LEAK_LITERAL.search(source)
    if not match:
        raise CannotLook(f"{leak} holds no GetBytes(\"...\") literal")
    credential = match.group(1)
    found = {credential, base64.b64encode(credential.encode()).decode()}
    password = credential.split(":", 1)[1] if ":" in credential else ""
    if len(password) >= 4:
        found.add(password)
    return found


def findings(text, secrets):
    """(line, what) for each secret in the text, by its line, without the secret."""
    found = []
    for number, line in enumerate(text.splitlines(), start=1):
        if any(secret in line for secret in secrets):
            found.append((number, CREDENTIAL))
    for what, pattern in PATTERNS:
        for match in pattern.finditer(text):
            found.append((text.count("\n", 0, match.start()) + 1, what))
    return sorted(set(found))


def commits(repo, revisions):
    """(commit, message, added lines) for each commit in the range, merges left out: their parents' commits count."""
    log = git(repo, "log", "--no-merges", "--patch", "--no-color", "--no-ext-diff", "--format=%x00%h%x00%B%x00",
              revisions)
    parts = log.split("\x00")
    for index in range(1, len(parts) - 2, 3):
        commit, message, patch = parts[index], parts[index + 1], parts[index + 2]
        # A file's header (diff --git ... +++ b/<path>) runs to its first hunk (@@): an added line that starts with ++
        # looks like its +++ line, so the lines added are told by where they are, not how they start.
        added, in_hunk = [], False
        for line in patch.splitlines():
            if line.startswith("diff --git "):
                in_hunk = False
            elif line.startswith("@@"):
                in_hunk = True
            elif in_hunk and line.startswith("+"):
                added.append(line[1:])
        yield commit, message, "\n".join(added)


def scan(repo, revisions, leak, files):
    """Each place a secret is, as <where>: <what>."""
    secrets = needles(repo, leak)
    report = []
    paths = git(repo, "ls-files", "--cached", "--others", "--exclude-standard", "-z").split("\x00")
    for path in sorted(set(filter(None, paths))):
        file = repo / path
        if not file.is_file() or file.is_symlink():
            continue
        text = file.read_bytes().decode("utf-8", "ignore")
        report += [f"{path}:{line}: {what}" for line, what in findings(text, secrets)]
    for commit, message, added in commits(repo, revisions):
        report += [f"commit {commit}, its message: {what}" for _, what in findings(message, secrets)]
        report += [f"commit {commit}, its added lines: {what}" for _, what in findings(added, secrets)]
    for path in files:
        try:
            text = Path(path).read_bytes().decode("utf-8", "ignore")
        except OSError as error:
            raise CannotLook(f"{path}: {error.strerror}") from None
        report += [f"{path}:{line}: {what}" for line, what in findings(text, secrets)]
    return report


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--repo", type=Path, default=REPOSITORY_ROOT, help="the checkout to look in (this one)")
    parser.add_argument("--leak", default=LEAK, help=f"the commit and file that held the credential ({LEAK})")
    parser.add_argument("--range", dest="revisions",
                        help="the commits to look at (from the credential's commit to HEAD)")
    parser.add_argument("files", nargs="*", help="other files to look in, such as an issue's or a comment's body")
    options = parser.parse_args(arguments)
    revisions = options.revisions or f"{options.leak.split(':', 1)[0]}..HEAD"
    try:
        report = scan(options.repo.resolve(), revisions, options.leak, options.files)
    except CannotLook as error:
        print(f"secret-scan: {error}", file=sys.stderr)
        return 2
    for line in report:
        print(line)
    print("PASS" if not report else f"FAIL ({len(report)} found)")
    return 1 if report else 0


if __name__ == "__main__":
    sys.exit(main())
