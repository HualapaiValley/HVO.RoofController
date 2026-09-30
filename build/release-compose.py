#!/usr/bin/env python3
"""The release compose file (docs/releasing.md): src/HVO.RoofControllerV4.RPi/docker-compose.yaml with the release's
images on GHCR in place of the images it builds.

  build/release-compose.py --version <version> [--controller-digest sha256:<hex>] [--emulator-digest sha256:<hex>]
                           [--registry <registry>/<owner>] [--source <compose file>] [-o <output>]

Every build: mapping, the build arguments and the pull_policy: never of the deployment checks are left out, so Compose
pulls each image, and the header's commands pull instead of building. The profiles, services, settings and mounts are
the source file's. The controller's services name <registry>/roof-controller:<version> and the HAT emulator
<registry>/roof-hat-emulator:<version>, each pinned to its digest when one is given (the release workflow gives both).
The output goes to standard output without -o. Any source line this script does not recognise fails it, so a change to
the source file cannot slip into a release unconverted.
"""

import argparse
import re
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
SOURCE = REPOSITORY_ROOT / "src" / "HVO.RoofControllerV4.RPi" / "docker-compose.yaml"
REGISTRY = "ghcr.io/hualapaivalley"

# The images the source file builds, and the released image that replaces each.
IMAGES = {
    "hvov9/roof-controller:v4": "roof-controller",
    "hvo/roof-controller:emulated": "roof-controller",
    "hvo/roof-hat-emulator:dev": "roof-hat-emulator",
}

# A product version (build/version.sh): a release, or a prerelease of one. A Docker tag cannot hold SemVer's '+'.
VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
REGISTRY_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._:-]*(/[a-z0-9][a-z0-9._-]*)*$")

FIRST_LINE = "# Roof controller on the Raspberry Pi. See docs/deployment.md.\n"

# The header's text about building, each replaced once: (source text, release text).
HEADER_EDITS = [
    ("Both images build for this machine (amd64 or arm64).", "Both images are pulled for this machine (amd64 or arm64)."),
    ("docker compose --profile emulator up -d --build\n", "docker compose --profile emulator up -d\n"),
    ("#                tests/emulator/compose-smoke-test.sh opens and closes the emulated roof through this profile.\n", ""),
    ("docker compose --profile pi build && docker compose --profile pi run --rm roof-controller-check\n",
     "docker compose --profile pi pull && docker compose --profile pi run --rm roof-controller-check\n"),
]


class ConversionError(Exception):
    pass


def release_header(version):
    return (
        f"# Roof controller {version} on the Raspberry Pi, from the release's images. See docs/deployment.md, \"Deploying\n"
        f"# with compose\" (https://github.com/HualapaiValley/HVO.RoofController/blob/v{version}/docs/deployment.md).\n"
        "# Made by build/release-compose.py from src/HVO.RoofControllerV4.RPi/docker-compose.yaml, which builds these images\n"
        "# from a checkout instead.\n"
    )


def indent_of(line):
    return len(line) - len(line.lstrip(" "))


def drop_preceding_comments(output, indent):
    """Removes the comment lines just emitted at this indentation: they describe the block being left out."""
    while output and output[-1].strip().startswith("#") and indent_of(output[-1]) == indent:
        output.pop()


def convert(source, version, registry=REGISTRY, controller_digest=None, emulator_digest=None):
    references = {
        "roof-controller": f"{registry}/roof-controller:{version}" + (f"@{controller_digest}" if controller_digest else ""),
        "roof-hat-emulator": f"{registry}/roof-hat-emulator:{version}" + (f"@{emulator_digest}" if emulator_digest else ""),
    }

    if not source.startswith(FIRST_LINE):
        raise ConversionError(f"the source's first line is not {FIRST_LINE.strip()!r}")
    text = release_header(version) + source[len(FIRST_LINE):]
    for old, new in HEADER_EDITS:
        count = text.count(old)
        if count != 1:
            raise ConversionError(f"the source's header holds {old.strip()!r} {count} times, not once")
        text = text.replace(old, new)

    lines = text.splitlines(keepends=True)
    output = []
    replaced = set()
    index = 0
    while index < len(lines):
        line = lines[index]
        stripped = line.strip()
        indent = indent_of(line)

        # A block left out whole: a build: mapping, or the build arguments' anchor. Its lines are those indented
        # deeper; the comments just above it describe it.
        if re.fullmatch(r"build:", stripped) or re.fullmatch(r"x-build-args: &build-args", stripped):
            drop_preceding_comments(output, indent)
            index += 1
            while index < len(lines) and lines[index].strip() and indent_of(lines[index]) > indent:
                index += 1
            # A top-level block took its blank line with it.
            if indent == 0 and index < len(lines) and not lines[index].strip():
                index += 1
            continue

        if stripped == "pull_policy: never":
            drop_preceding_comments(output, indent)
            index += 1
            continue

        match = re.fullmatch(r"(\s*)image: (\S+)\n?", line)
        if match:
            image = match.group(2)
            if image not in IMAGES:
                raise ConversionError(f"line {index + 1}: the image {image} has no released image")
            output.append(f"{match.group(1)}image: {references[IMAGES[image]]}\n")
            replaced.add(image)
            index += 1
            continue

        output.append(line)
        index += 1

    missing = sorted(set(IMAGES) - replaced)
    if missing:
        raise ConversionError(f"the source no longer names {', '.join(missing)}: update IMAGES")

    result = "".join(output)
    for number, line in enumerate(result.splitlines(), 1):
        body = line.split("#", 1)[0]
        for leftover in ("build", "pull_policy"):
            if leftover in body:
                raise ConversionError(f"line {number} of the result still holds '{leftover}': {line.strip()}")
        match = re.search(r"\bimage:\s*(\S+)", body)
        if match and match.group(1) not in references.values():
            raise ConversionError(f"line {number} of the result names an image that is not released: {line.strip()}")
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Writes the release compose file: the Pi compose file with the release's images, built by nothing.")
    parser.add_argument("--version", required=True, help="the release's product version, such as 4.0.0 or 4.0.0-rc.1")
    parser.add_argument("--controller-digest", help="the controller image's digest (sha256:<64 hex digits>)")
    parser.add_argument("--emulator-digest", help="the HAT emulator image's digest (sha256:<64 hex digits>)")
    parser.add_argument("--registry", default=REGISTRY, help=f"where the images are, default {REGISTRY}")
    parser.add_argument("--source", type=Path, default=SOURCE, help="the compose file to convert")
    parser.add_argument("-o", "--output", type=Path, help="the file to write, default standard output")
    arguments = parser.parse_args(argv)

    if not VERSION.match(arguments.version):
        parser.error(f"--version must be a product version such as 4.0.0 or 4.0.0-rc.1, not {arguments.version!r}")
    for name in ("controller_digest", "emulator_digest"):
        value = getattr(arguments, name)
        if value is not None and not DIGEST.match(value):
            parser.error(f"--{name.replace('_', '-')} must be sha256:<64 hex digits>, not {value!r}")
    if not REGISTRY_PATTERN.match(arguments.registry):
        parser.error(f"--registry must be a lower-case <registry>/<owner>, not {arguments.registry!r}")

    try:
        source = arguments.source.read_text(encoding="utf-8")
        result = convert(source, arguments.version, arguments.registry,
                         arguments.controller_digest, arguments.emulator_digest)
    except (OSError, ConversionError) as error:
        print(f"release-compose.py: {arguments.source}: {error}", file=sys.stderr)
        return 1

    if arguments.output is None:
        sys.stdout.write(result)
    else:
        arguments.output.write_text(result, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
