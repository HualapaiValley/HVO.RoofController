#!/usr/bin/env python3
"""Packs PNG images into a macOS icon file (.icns), with nothing but the standard library.

    make-icns.py <out.icns> <png>...

Each PNG must be square, 16, 32, 64, 128, 256, 512 or 1024 pixels wide; each size goes in the element macOS reads for
it (a 32-pixel image serves both 32 at 1x and 16 at 2x, and so on). An .icns file is the four bytes 'icns', its length,
then elements: a four-byte type, the element's length (header included) and the PNG as it is.
"""
import struct
import sys

# The elements that hold PNG data, by pixel size. A size may serve two elements: its 1x one and the 2x one of half its size.
ELEMENTS = {
    16: [b"icp4"],
    32: [b"icp5", b"ic11"],
    64: [b"icp6", b"ic12"],
    128: [b"ic07"],
    256: [b"ic08", b"ic13"],
    512: [b"ic09", b"ic14"],
    1024: [b"ic10"],
}

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def png_size(data: bytes, name: str) -> int:
    if data[:8] != PNG_SIGNATURE or data[12:16] != b"IHDR":
        sys.exit(f"{name}: not a PNG image")
    width, height = struct.unpack(">II", data[16:24])
    if width != height or width not in ELEMENTS:
        sys.exit(f"{name}: {width}x{height}; an icon image is square, one of {sorted(ELEMENTS)} pixels")
    return width


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        sys.exit(__doc__)
    elements = []
    seen = set()
    for name in argv[2:]:
        with open(name, "rb") as file:
            data = file.read()
        size = png_size(data, name)
        if size in seen:
            sys.exit(f"{name}: a second {size}-pixel image")
        seen.add(size)
        for kind in ELEMENTS[size]:
            elements.append(kind + struct.pack(">I", 8 + len(data)) + data)
    body = b"".join(elements)
    with open(argv[1], "wb") as out:
        out.write(b"icns" + struct.pack(">I", 8 + len(body)) + body)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
