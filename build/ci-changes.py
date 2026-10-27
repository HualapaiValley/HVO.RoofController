#!/usr/bin/env python3
"""Which of a pull request's CI jobs its changes can affect (docs/ci-runners.md, "Jobs a pull request can skip").

    build/ci-changes.py --base <commit> [--head <commit>] [--github-output FILE] [--summary FILE]
    build/ci-changes.py --name-status FILE [--root DIR]

Prints `<job>=true` for each job below that the changes can affect, and `<job>=false` for one they cannot. With --base
it diffs <commit> (the pull request's base) against --head (default HEAD, the merge commit the workflow checked out) in
this repository; with --name-status it reads `git diff --name-status --no-renames -z` output from FILE instead (the
tests). Anything it cannot work out (a commit it cannot read, a project reference it cannot follow, no changes at all)
runs every job: it never fails, and it says why on stderr.

A job is skipped only when every changed file is in a class of files the job cannot read:

  docs        docs/** and the Markdown files at the top of the repository
  test code   tests/HVO.RoofControllerV4.RPi.Tests/**
  a project   src/<project>/**, for a project the job does not build, run or copy into an image; worked out from the
              project references and the Dockerfiles' COPY lines at the commit tested, so it follows the graph

Every other file (build/, .github/, deploy/, the root's build files, src/'s own files, the other tests/ folders, ...)
runs every job. deploy-script also runs for any deleted or renamed file, because its link check reads every link.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath

PREFIX = "HVO.RoofControllerV4."
TEST_PROJECT = "tests/HVO.RoofControllerV4.RPi.Tests/"
MAC = PREFIX + "Mac"
INSTALLER = PREFIX + "Installer"
CLI = PREFIX + "Cli"
DOCKERFILES = ("src/HVO.RoofControllerV4.RPi/Dockerfile", "src/HVO.RoofControllerV4.Emulator/Dockerfile")

# The jobs, in the order they are printed. Each says which classes it cannot read (see the module's docstring):
#   docs        none of these read docs/ or a top-level Markdown file: the test classes their filters select read no
#               repository file outside src/ and tests/ (ScenarioCoverageTests, InstallerDocsTests and the other tests
#               that read docs run in build-and-test), and their scripts read none
#   test code   these neither build nor run the test project
#   projects    "mac" is the Mac app's project closure, "images" every project the two Dockerfiles copy and the
#               projects they reference, "rig" the images' with the installer's and hvo-roof's closures
JOBS = {
    # ci.yml
    "deploy_script": {"docs": False, "test_code": True, "projects": None},
    "install_sh_mac": {"docs": True, "test_code": True, "projects": None},
    "installer_kiosk": {"docs": True, "test_code": False, "projects": None},
    "installer_mac": {"docs": True, "test_code": False, "projects": None},
    "mac_app": {"docs": True, "test_code": True, "projects": "mac"},
    "mac_cli": {"docs": True, "test_code": False, "projects": None},
    "terminal_distributions": {"docs": True, "test_code": False, "projects": None},
    # scenarios.yml
    "scenarios": {"docs": True, "test_code": False, "projects": None},
    "browser": {"docs": True, "test_code": False, "projects": None},
    "container_scenarios": {"docs": True, "test_code": True, "projects": "images"},
    "installer_rig": {"docs": True, "test_code": True, "projects": "rig"},
}

SOURCE_DIRECTORY = re.compile(r"^src/([^/]+)/")
IDENTITY_ITEMS = {"PackageReference", "PackageVersion", "NuGetAuditSuppress", "InternalsVisibleTo", "Using",
                  "AssemblyAttribute"}


class Unknown(Exception):
    """Something the decision cannot rely on: every job runs."""


def is_docs(path):
    return path.startswith("docs/") or ("/" not in path and path.lower().endswith(".md"))


def project_of(path):
    """The src/ folder a path is in (src/<folder>/...), or None for anything else, src/'s own files included."""
    match = SOURCE_DIRECTORY.match(path)
    return match.group(1) if match else None


def _inside(root, path):
    """path (absolute) relative to root, as a POSIX string; Unknown when it is outside the repository."""
    relative = os.path.relpath(path, root)
    if relative == ".." or relative.startswith("../") or os.path.isabs(relative):
        raise Unknown(f"{path} is outside the repository")
    return PurePosixPath(relative).as_posix()


def _check_target(relative, what):
    """A file a build or an image reads must not be one a job is told it cannot read."""
    if is_docs(relative) or relative.startswith(TEST_PROJECT) or relative == TEST_PROJECT.rstrip("/"):
        raise Unknown(f"{what} reads {relative}, which the skip rules say no build reads")


def input_attributes(path):
    """Read XML attributes, including either quote form and entity escapes. Imports require MSBuild evaluation:
    relative paths in an imported file need not be relative to that file, so never guess at their dependencies."""
    try:
        document = ET.parse(path)
    except ET.ParseError as error:
        raise Unknown(f"cannot parse {path}: {error}") from error
    for element in document.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if tag == "Import":
            raise Unknown(f"{path} imports build logic, which the skip rules cannot evaluate")
        for attribute in ("Include", "Project", "Update"):
            if attribute in element.attrib:
                yield tag, element.attrib[attribute]


def references(root, folder):
    """The src/ folders a project's literal XML items name outside its own folder. Imports, dynamic names and paths
    into docs/ or the test project are Unknown; they enable every job rather than guessing at MSBuild evaluation."""
    directory = root / "src" / folder
    names = set()
    paths = [p for p in directory.rglob("*") if not {"bin", "obj"} & set(p.relative_to(directory).parts)]
    if directory.is_symlink() or any(p.is_symlink() for p in paths):
        raise Unknown(f"{directory} has symlinked inputs, which the skip rules cannot follow")
    files = [p for p in paths if p.suffix in (".csproj", ".props", ".targets") and p.is_file()]
    for project_file in files:
        for _, value in input_attributes(project_file):
            for item in value.split(";"):
                item = item.strip().replace("\\", "/")
                if "$(" in item or "@(" in item or "%(" in item:
                    raise Unknown(f"{project_file.relative_to(root)} names {item}, which the skip rules cannot follow")
                if not item or (".." not in item and not item.startswith("/")):
                    continue  # inside the projects folder, or a packages name
                relative = _inside(root, os.path.normpath(project_file.parent / item))
                _check_target(relative, project_file.relative_to(root).as_posix())
                if relative in (".", "src"):
                    raise Unknown(f"{project_file} reads a broad source directory")
                name = project_of(relative)
                if name and name != folder:
                    names.add(name)
    return names


def closure(root, folders):
    """The folders and every src/ folder they reach through references()."""
    seen, pending = set(), list(folders)
    while pending:
        folder = pending.pop()
        if folder in seen:
            continue
        if not (root / "src" / folder).is_dir():
            raise Unknown(f"src/{folder} is not a folder")
        seen.add(folder)
        pending.extend(references(root, folder) - seen)
    return seen


def _copy_sources(line):
    """The sources of a Dockerfile COPY or ADD instruction's text after the keyword (JSON or shell form)."""
    words = line.split()
    while words and words[0].startswith("--"):
        if words[0].startswith("--from="):
            return []  # from another stage of the build, not the context
        words = words[1:]
    rest = " ".join(words)
    if rest.startswith("["):
        try:
            items = json.loads(rest)
        except json.JSONDecodeError as error:
            raise Unknown(f"cannot read COPY {rest}: {error}") from error
    else:
        if any(character in rest for character in "\"'\\"):
            raise Unknown(f"cannot follow quoted or escaped shell-form COPY {rest}")
        items = words
    if len(items) < 2:
        raise Unknown(f"cannot read COPY {rest}")
    return items[:-1]


def image_folders(root):
    """Every src/ folder the Dockerfiles copy from the build context, and the folders those projects reference."""
    folders = set()
    for dockerfile in DOCKERFILES:
        text = (root / dockerfile).read_text(encoding="utf-8")
        for directive in re.findall(r"^\s*#\s*escape\s*=\s*(\S+)", text, re.MULTILINE | re.IGNORECASE):
            if directive != "\\":
                raise Unknown(f"{dockerfile} uses an unsupported escape directive")
        # Instructions joined across line continuations; comments dropped.
        logical = re.sub(r"\\\n", " ", "\n".join(l for l in text.splitlines() if not l.lstrip().startswith("#")))
        for line in logical.splitlines():
            parts = line.split(None, 1)
            if not parts:
                continue
            keyword, rest = parts[0], parts[1] if len(parts) > 1 else ""
            keyword = keyword.upper()
            if keyword == "RUN" and "--mount" in rest:
                # Bind is the default mount type. Reject every mount rather than silently miss an implicit bind.
                raise Unknown(f"{dockerfile} uses a RUN mount, which the skip rules cannot follow")
            if keyword not in ("COPY", "ADD"):
                continue
            for source in _copy_sources(rest):
                source = source[2:] if source.startswith("./") else source
                if (source.startswith("/") or ".." in PurePosixPath(source).parts or "$" in source
                        or any(c in source.split("/")[0] for c in "*?[]")):
                    raise Unknown(f"{dockerfile} copies {source}, which the skip rules cannot follow")
                source = PurePosixPath(source).as_posix()
                _check_target(source, dockerfile)
                name = project_of(source)
                if name is None:
                    # A whole project directory (without a trailing slash) is supported; a wider context copy is not.
                    if (root / source).is_dir():
                        name = project_of(source.rstrip("/") + "/")
                        if name is None:
                            raise Unknown(f"{dockerfile} copies broad context directory {source}")
                    elif source.rstrip("/") in ("", ".", "src", "docs", TEST_PROJECT.rstrip("/")):
                        raise Unknown(f"{dockerfile} copies unclassified context directory {source}")
                if name:
                    folders.add(name)
    return closure(root, folders)


def check_root_build_files(root):
    """Shared build files at the root and src/ are inherited by the project folders. Package/type identities are
    supported; any file input or import needs MSBuild's project-relative evaluation, so it enables every job.
    Conditions are deliberately not evaluated: a dependency in any branch must be safe for every job/configuration."""
    for directory in (root, root / "src"):
        for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
            path = directory / name
            if not path.is_file():
                continue
            for tag, value in input_attributes(path):
                if tag not in IDENTITY_ITEMS:
                    raise Unknown(f"{path} names build input {value}, which the skip rules cannot evaluate")


def project_sets(root):
    check_root_build_files(root)
    # Scenario/browser jobs also build the test graph. Validate excluded input classes in every project, including
    # projects outside the Mac/image/rig closures, before allowing any docs/test-code skip.
    for directory in (root / "src").iterdir():
        if directory.is_dir() and directory.name not in ("bin", "obj"):
            references(root, directory.name)
    images = image_folders(root)
    return {
        "mac": closure(root, {MAC}),
        "images": images,
        "rig": images | closure(root, {INSTALLER, CLI}),
    }


def affects(job, status, path, sets):
    """Whether a change (git's status letter and the path) can affect the job."""
    rules = JOBS[job]
    if job == "deploy_script" and status != "M" and status != "A":
        return True  # a deleted file can be a link's target; its link check reads every Markdown file
    if is_docs(path):
        return not rules["docs"]
    if path.startswith(TEST_PROJECT):
        if job == "deploy_script" and path.lower().endswith(".md"):
            return True
        return not rules["test_code"]
    folder = project_of(path)
    if folder is not None and rules["projects"] is not None:
        return folder in sets[rules["projects"]]
    return True


def parse_name_status(data):
    """`git diff --name-status --no-renames -z` output: status, NUL, path, NUL, ..."""
    fields = data.split("\0")
    if fields and fields[-1] == "":
        fields.pop()
    if len(fields) % 2:
        raise Unknown("the diff's name-status output is not status and path pairs")
    changes = []
    for status, path in zip(fields[0::2], fields[1::2]):
        if not status or status[0] not in "ACDMT" or not path:
            raise Unknown(f"cannot read the diff's line {status!r} {path!r}")
        changes.append((status[0], path))
    return changes


def decide(changes, root):
    """{job: True to run, False to skip}, and the reason when every job runs."""
    if not changes:
        raise Unknown("the diff names no file")
    sets = project_sets(root)
    return {job: any(affects(job, status, path, sets) for status, path in changes) for job in JOBS}


def git_diff(root, base, head):
    def git(*arguments):
        return subprocess.run(["git", "-C", str(root), *arguments], check=True, capture_output=True, timeout=120,
                              env=dict(os.environ, GIT_TERMINAL_PROMPT="0")).stdout
    for commit in (base, head):
        if not re.fullmatch(r"[0-9a-fA-F]{40}|HEAD", commit):
            raise Unknown(f"{commit!r} is not a full commit SHA")
    try:
        git("cat-file", "-e", base + "^{commit}")
    except subprocess.CalledProcessError:
        try:
            git("fetch", "--no-tags", "--quiet", "--depth=1", "origin", base)
        except subprocess.CalledProcessError as error:
            raise Unknown(f"cannot fetch the base commit {base}: {error.stderr.decode(errors='replace').strip()}")
    try:
        output = git("diff", "--name-status", "--no-renames", "-z", base, head)
    except subprocess.CalledProcessError as error:
        raise Unknown(f"git diff failed: {error.stderr.decode(errors='replace').strip()}")
    return parse_name_status(output.decode("utf-8", errors="surrogateescape"))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--base", help="the pull request's base commit (full SHA)")
    source.add_argument("--name-status", type=Path, help="read git diff --name-status --no-renames -z output")
    parser.add_argument("--head", default="HEAD", help="the commit tested (default HEAD)")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--github-output", type=Path, help="also append the lines to this file ($GITHUB_OUTPUT)")
    parser.add_argument("--summary", type=Path, help="append a Markdown table to this file ($GITHUB_STEP_SUMMARY)")
    args = parser.parse_args(argv)

    root = args.root.resolve()
    changes = []
    try:
        if args.name_status is not None:
            changes = parse_name_status(args.name_status.read_bytes().decode("utf-8", errors="surrogateescape"))
        else:
            changes = git_diff(root, args.base, args.head)
        run, reason = decide(changes, root), None
    except Exception as error:  # Anything at all: every job runs.
        run, reason = {job: True for job in JOBS}, f"{type(error).__name__}: {error}"
        print(f"::warning title=CI changes::every job runs: {reason}", file=sys.stderr)

    lines = "".join(f"{job}={'true' if run[job] else 'false'}\n" for job in JOBS)
    sys.stdout.write(lines)
    if args.github_output is not None:
        with args.github_output.open("a", encoding="utf-8") as output:
            output.write(lines)  # one write: every job's line, or none
    if args.summary is not None:
        table = ["### Jobs this change can affect", ""]
        if reason:
            table += [f"Every job runs: {reason}", ""]
        else:
            table += [f"{len(changes)} changed file(s); see docs/ci-runners.md for the rules.", ""]
        table += ["| Job | Runs |", "|---|---|"] + [f"| {job} | {'yes' if run[job] else 'skipped'} |" for job in JOBS]
        with args.summary.open("a", encoding="utf-8") as summary:
            summary.write("\n".join(table) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
