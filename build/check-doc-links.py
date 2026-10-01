#!/usr/bin/env python3
"""The links in the repository's Markdown files (#75): every relative link and image names a file or folder in the
repository, and every anchor (#...) a heading or an HTML anchor of the file it names.

  build/check-doc-links.py [--root <folder>] [<file or folder>...]

With no files or folders it checks every Markdown file git tracks. The root (by default this repository) is where a
link starting with / points, and no link may point outside it. Links to the web (http, https, mailto) are left alone, as
are links in code: fenced blocks and inline code. An anchor is checked as GitHub makes one from a
heading: the heading's text in lower case, without its punctuation, with each space a hyphen, and -1, -2 and so on
after a heading's text that came before. It prints each broken link as <file>:<line>: <link>: <why>, and exits 1 when
there is one.
"""

import argparse
import functools
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent

FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
HEADING = re.compile(r"^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$")
INLINE_CODE = re.compile(r"(`+)(.+?)\1")
# [text](target "title") and ![alt](target): the target up to a space or the closing parenthesis, with one level of
# parentheses inside it.
LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\[\]]*\])*\]\(\s*<?((?:[^()\s<>]|\([^()\s]*\))*)>?(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)")
# [label]: target, a reference definition.
DEFINITION = re.compile(r"^ {0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+.*)?$")
HTML_TARGET = re.compile(r"<(?:a|img|source)\b[^>]*?\b(?:href|src|srcset)\s*=\s*[\"']([^\"']+)[\"']", re.IGNORECASE)
HTML_ANCHOR = re.compile(r"<[a-z]+\b[^>]*?\b(?:id|name)\s*=\s*[\"']([^\"']+)[\"']", re.IGNORECASE)
EXTERNAL = re.compile(r"^(?:[a-z][a-z0-9+.-]*:|//)", re.IGNORECASE)


def markdown_files(paths, root):
    """The Markdown files to check: those given, or in the folders given, or every one git tracks under the root."""
    if not paths:
        listed = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "--", "*.md"],
                                check=True, capture_output=True, text=True).stdout
        return [root / name for name in listed.split("\0") if name]
    files = []
    for argument in paths:
        path = Path(argument).resolve()
        files.extend(sorted(path.rglob("*.md")) if path.is_dir() else [path])
    return files


def outside_fences(text):
    """Each line number and line outside fenced code blocks."""
    fence = None
    for number, line in enumerate(text.splitlines(), start=1):
        opening = FENCE.match(line)
        if fence:
            # A fence closes with at least as many of the same character, and nothing after them.
            if opening and opening.group(1)[0] == fence[0] and len(opening.group(1)) >= len(fence) \
                    and not line.strip().strip(fence[0]):
                fence = None
            continue
        if opening:
            fence = opening.group(1)
            continue
        yield number, line


def prose_lines(text):
    """Each line number and line outside fenced code blocks, with its inline code blanked out."""
    for number, line in outside_fences(text):
        yield number, INLINE_CODE.sub(lambda match: " " * len(match.group(0)), line)


def slug(heading):
    """GitHub's anchor for a heading: its text as shown, in lower case, without punctuation, spaces as hyphens."""
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)  # a link or image shows its text
    text = re.sub(r"<[^>]+>", "", text)  # HTML tags show nothing
    text = text.replace("`", "")
    text = re.sub(r"(\*\*|__|\*|~~)", "", text)
    text = text.lower()
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


@functools.lru_cache(maxsize=None)
def anchors(path):
    """The anchors a Markdown file has: its headings' slugs, numbered as GitHub numbers repeats, and its HTML ids."""
    seen = {}
    found = set()
    for _, line in outside_fences(path.read_text(encoding="utf-8")):
        heading = HEADING.match(line)
        if heading:
            base = slug(heading.group(2))
            count = seen.get(base, 0)
            seen[base] = count + 1
            found.add(base if count == 0 else f"{base}-{count}")
        found.update(HTML_ANCHOR.findall(line))
    return frozenset(found)


def targets(line):
    """Each link target on a line of prose."""
    yield from LINK.findall(line)
    definition = DEFINITION.match(line)
    if definition:
        yield definition.group(1)
    for target in HTML_TARGET.findall(line):
        # srcset lists "url width" pairs.
        yield from (part.strip().split()[0] for part in target.split(",") if part.strip())


def problem(source, target, root):
    """Why a link from a Markdown file does not resolve, or None when it does."""
    if not target or EXTERNAL.match(target):
        return None
    path_part, _, anchor = target.partition("#")
    path_part = unquote(path_part.split("?", 1)[0])
    anchor = unquote(anchor)
    if path_part.startswith("/"):
        resolved = (root / path_part.lstrip("/")).resolve()
    elif path_part:
        resolved = (source.parent / path_part).resolve()
    else:
        resolved = source
    try:
        resolved.relative_to(root)
    except ValueError:
        return "outside the repository"
    if not resolved.exists():
        return "no such file or folder"
    if anchor and resolved.suffix.lower() == ".md" and resolved.is_file():
        if anchor not in anchors(resolved):
            return f"{resolved.relative_to(root).as_posix()} has no heading or anchor #{anchor}"
    return None


def check(files, root):
    """Each broken link in the files, as (file, line, target, why)."""
    for path in files:
        text = path.read_text(encoding="utf-8")
        for number, line in prose_lines(text):
            for target in targets(line):
                why = problem(path, target, root)
                if why:
                    yield path, number, target, why


def main(arguments):
    parser = argparse.ArgumentParser(description="Check the relative links and anchors in Markdown files.")
    parser.add_argument("--root", type=Path, default=REPOSITORY_ROOT,
                        help="the repository: where / points, and what no link may leave (default: this one)")
    parser.add_argument("paths", nargs="*", help="Markdown files or folders (default: every one git tracks)")
    options = parser.parse_args(arguments)
    root = options.root.resolve()
    files = markdown_files(options.paths, root)
    broken = list(check(files, root))
    for path, number, target, why in broken:
        try:
            shown = path.relative_to(root).as_posix()
        except ValueError:
            shown = path
        print(f"{shown}:{number}: {target}: {why}")
    print(f"{len(files)} Markdown files, {len(broken)} broken links", file=sys.stderr)
    return 1 if broken else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
