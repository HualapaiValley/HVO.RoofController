#!/usr/bin/env python3
"""Draws a terminal screen, as 'tmux capture-pane -e -N -p' writes it, as an SVG image (#45).

The terminal smoke (tests/cli/terminal-smoke.sh) saves each screen of 'hvo-roof ui' this way, for the CI artifacts and
for the screenshots in docs/cli.md. Only the standard library is used.

Text is placed cell by cell on a fixed grid, and frame lines and block shadows are drawn as shapes rather than as font
glyphs, so the picture lines up whatever monospace font the viewer has.

    tests/cli/ansi-to-svg.py screen.ans screen.svg [--title TITLE]
"""

import argparse
import html
import re
import sys
import unicodedata

FONT_SIZE = 14
CELL_WIDTH = 8.4
LINE_HEIGHT = 18
PADDING = 12
TITLE_HEIGHT = 30

DEFAULT_FG = "#d4d4d4"
DEFAULT_BG = "#1e1e1e"

# The 16 standard colours, as a dark terminal theme draws them.
PALETTE = [
    "#000000", "#cd3131", "#0dbc79", "#e5e510", "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
    "#666666", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#ffffff",
]

# Frame characters: the arms each one has (up, right, down, left), 1 for a light line, 2 for a heavy one, 3 for double.
BOX = {
    "─": (0, 1, 0, 1), "│": (1, 0, 1, 0), "┌": (0, 1, 1, 0), "┐": (0, 0, 1, 1), "└": (1, 1, 0, 0), "┘": (1, 0, 0, 1),
    "├": (1, 1, 1, 0), "┤": (1, 0, 1, 1), "┬": (0, 1, 1, 1), "┴": (1, 1, 0, 1), "┼": (1, 1, 1, 1),
    "━": (0, 2, 0, 2), "┃": (2, 0, 2, 0), "┏": (0, 2, 2, 0), "┓": (0, 0, 2, 2), "┗": (2, 2, 0, 0), "┛": (2, 0, 0, 2),
    "┣": (2, 2, 2, 0), "┫": (2, 0, 2, 2), "┳": (0, 2, 2, 2), "┻": (2, 2, 0, 2), "╋": (2, 2, 2, 2),
    "═": (0, 3, 0, 3), "║": (3, 0, 3, 0), "╔": (0, 3, 3, 0), "╗": (0, 0, 3, 3), "╚": (3, 3, 0, 0), "╝": (3, 0, 0, 3),
    "╠": (3, 3, 3, 0), "╣": (3, 0, 3, 3), "╦": (0, 3, 3, 3), "╩": (3, 3, 0, 3), "╬": (3, 3, 3, 3),
}

# Rounded corners: the two arms each joins.
ROUNDED = {"╭": ("right", "down"), "╮": ("left", "down"), "╰": ("up", "right"), "╯": ("up", "left")}

# Block elements, as rectangles in eighths of the cell: (x, y, width, height).
BLOCKS = {
    "█": (0, 0, 8, 8), "▀": (0, 0, 8, 4), "▄": (0, 4, 8, 4), "▌": (0, 0, 4, 8), "▐": (4, 0, 4, 8),
    "▖": (0, 4, 4, 4), "▗": (4, 4, 4, 4), "▘": (0, 0, 4, 4), "▝": (4, 0, 4, 4),
}

SGR = re.compile(r"\x1b\[([0-9;:]*)m")
OTHER_ESCAPE = re.compile(r"\x1b(?:\[[0-9;:?]*[A-Za-ln-z]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[()][0-9A-Za-z])")


def colour_256(n):
    if n < 16:
        return PALETTE[n]
    if n < 232:
        n -= 16
        steps = [0, 95, 135, 175, 215, 255]
        return "#%02x%02x%02x" % (steps[n // 36], steps[(n // 6) % 6], steps[n % 6])
    level = 8 + (n - 232) * 10
    return "#%02x%02x%02x" % (level, level, level)


class Style:
    __slots__ = ("fg", "bg", "bold", "dim", "italic", "underline", "reverse")

    def __init__(self):
        self.reset()

    def reset(self):
        self.fg = None
        self.bg = None
        self.bold = self.dim = self.italic = self.underline = self.reverse = False

    def copy(self):
        other = Style()
        for name in self.__slots__:
            setattr(other, name, getattr(self, name))
        return other

    def apply(self, codes):
        params = [int(p) if p else 0 for p in re.split(r"[;:]", codes)] if codes else [0]
        i = 0
        while i < len(params):
            p = params[i]
            if p == 0:
                self.reset()
            elif p == 1:
                self.bold = True
            elif p == 2:
                self.dim = True
            elif p == 3:
                self.italic = True
            elif p == 4:
                self.underline = True
            elif p == 7:
                self.reverse = True
            elif p == 22:
                self.bold = self.dim = False
            elif p == 23:
                self.italic = False
            elif p == 24:
                self.underline = False
            elif p == 27:
                self.reverse = False
            elif 30 <= p <= 37:
                self.fg = PALETTE[p - 30]
            elif 90 <= p <= 97:
                self.fg = PALETTE[p - 90 + 8]
            elif 40 <= p <= 47:
                self.bg = PALETTE[p - 40]
            elif 100 <= p <= 107:
                self.bg = PALETTE[p - 100 + 8]
            elif p == 39:
                self.fg = None
            elif p == 49:
                self.bg = None
            elif p in (38, 48) and i + 1 < len(params):
                if params[i + 1] == 5 and i + 2 < len(params):
                    colour = colour_256(params[i + 2])
                    i += 2
                elif params[i + 1] == 2 and i + 4 < len(params):
                    colour = "#%02x%02x%02x" % tuple(params[i + 2:i + 5])
                    i += 4
                else:
                    colour = None
                if p == 38:
                    self.fg = colour
                else:
                    self.bg = colour
            i += 1

    def colours(self):
        fg, bg = self.fg or DEFAULT_FG, self.bg or DEFAULT_BG
        return (bg, fg) if self.reverse else (fg, bg)

    def text_key(self):
        return (self.colours()[0], self.bold, self.dim, self.italic, self.underline)


def parse(text):
    """The screen as rows of (character, style) cells; a wide character's second cell is None."""
    rows = []
    for line in text.rstrip("\n").split("\n"):
        line = OTHER_ESCAPE.sub("", line)
        style = Style()
        cells = []
        position = 0
        for match in SGR.finditer(line):
            add_text(cells, line[position:match.start()], style)
            style.apply(match.group(1))
            position = match.end()
        add_text(cells, line[position:], style)
        rows.append(cells)
    return rows


def add_text(cells, text, style):
    for ch in text:
        if ch in "\r\x07":
            continue
        if unicodedata.combining(ch) and cells:
            previous, previous_style = cells[-1] if cells[-1] is not None else cells[-2]
            cells[-1 if cells[-1] is not None else -2] = (previous + ch, previous_style)
            continue
        cells.append((ch, style.copy()))
        if unicodedata.east_asian_width(ch) in ("W", "F"):
            cells.append(None)


def arm_width(weight):
    return {1: 1.2, 2: 2.4, 3: 1.0}[weight]


def box_shapes(ch, x, y, colour, segments, paths):
    """Adds the lines of a frame character: from the cell's centre to each edge it joins."""
    cx, cy = x + CELL_WIDTH / 2, y + LINE_HEIGHT / 2
    if ch in ROUNDED:
        ends = {"up": (cx, y), "down": (cx, y + LINE_HEIGHT), "left": (x, cy), "right": (x + CELL_WIDTH, cy)}
        (x1, y1), (x2, y2) = ends[ROUNDED[ch][0]], ends[ROUNDED[ch][1]]
        paths.append(
            f'<path d="M{x1:.2f},{y1:.2f} Q{cx:.2f},{cy:.2f} {x2:.2f},{y2:.2f}" fill="none" stroke="{colour}" '
            f'stroke-width="{arm_width(1)}"/>')
        return
    up, right, down, left = BOX[ch]
    for weight, vertical, start, stop in (
        (up, True, y, cy), (down, True, cy, y + LINE_HEIGHT), (left, False, x, cx), (right, False, cx, x + CELL_WIDTH),
    ):
        if not weight:
            continue
        for offset in ((-1.5, 1.5) if weight == 3 else (0,)):
            # Each arm reaches past the centre by half a line, so that corners and joins close.
            reach = arm_width(weight) / 2
            if vertical:
                segments.setdefault(("v", round(cx + offset, 2), arm_width(weight), colour), []).append((start - reach, stop + reach))
            else:
                segments.setdefault(("h", round(cy + offset, 2), arm_width(weight), colour), []).append((start - reach, stop + reach))


def merged_lines(segments):
    """Joins the arms of neighbouring cells into long lines, which draw without seams."""
    lines = []
    for (direction, at, stroke, colour), spans in segments.items():
        spans.sort()
        merged = [list(spans[0])]
        for begin, end in spans[1:]:
            if begin <= merged[-1][1] + 0.01:
                merged[-1][1] = max(merged[-1][1], end)
            else:
                merged.append([begin, end])
        for begin, end in merged:
            x1, y1, x2, y2 = (at, begin, at, end) if direction == "v" else (begin, at, end, at)
            lines.append(
                f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}" stroke="{colour}" stroke-width="{stroke}"/>')
    return lines


def render(rows, title):
    columns = max((len(row) for row in rows), default=0)
    top = TITLE_HEIGHT if title else 0
    width = columns * CELL_WIDTH + 2 * PADDING
    height = len(rows) * LINE_HEIGHT + 2 * PADDING + top
    background, shapes, texts = [], [], []
    segments, paths = {}, []

    for r, row in enumerate(rows):
        y = PADDING + top + r * LINE_HEIGHT
        c = 0
        while c < len(row):
            cell = row[c]
            if cell is None:
                c += 1
                continue
            bg = cell[1].colours()[1]
            start = c
            while c < len(row) and (row[c] is None or row[c][1].colours()[1] == bg):
                c += 1
            if bg != DEFAULT_BG:
                background.append(
                    f'<rect x="{PADDING + start * CELL_WIDTH:.2f}" y="{y}" width="{(c - start) * CELL_WIDTH + 0.4:.2f}" '
                    f'height="{LINE_HEIGHT + 0.4}" fill="{bg}"/>')

        c = 0
        while c < len(row):
            cell = row[c]
            if cell is None:
                c += 1
                continue
            ch, style = cell
            x = PADDING + c * CELL_WIDTH
            fg = style.colours()[0]
            if ch in BOX or ch in ROUNDED:
                box_shapes(ch, x, y, fg, segments, paths)
                c += 1
                continue
            if ch in BLOCKS:
                bx, by, bw, bh = BLOCKS[ch]
                shapes.append(
                    f'<rect x="{x + bx * CELL_WIDTH / 8:.2f}" y="{y + by * LINE_HEIGHT / 8:.2f}" '
                    f'width="{bw * CELL_WIDTH / 8 + 0.3:.2f}" height="{bh * LINE_HEIGHT / 8 + 0.3:.2f}" fill="{fg}"/>')
                c += 1
                continue
            if ch == " ":
                c += 1
                continue
            # A run of characters in one style, up to the next frame, block or wide character.
            key = style.text_key()
            start = c
            run = []
            while c < len(row) and row[c] is not None:
                ch2, style2 = row[c]
                if ch2 in BOX or ch2 in ROUNDED or ch2 in BLOCKS or style2.text_key() != key:
                    break
                if c > start and unicodedata.east_asian_width(ch2) in ("W", "F"):
                    break
                # A gap of two spaces ends the run, so that columns line up whatever the font.
                if ch2 == " " and c + 1 < len(row) and row[c + 1] is not None and row[c + 1][0] == " ":
                    break
                run.append(ch2)
                c += 1
                if unicodedata.east_asian_width(ch2) in ("W", "F"):
                    c += 1
                    break
            text = "".join(run).rstrip(" ")
            if not text:
                continue
            cells = sum(2 if unicodedata.east_asian_width(t) in ("W", "F") else 1 for t in text)
            attributes = [f'x="{x:.2f}"', f'y="{y + LINE_HEIGHT * 0.75:.2f}"', f'fill="{fg}"']
            if cells > 1:
                attributes.append(f'textLength="{cells * CELL_WIDTH:.2f}" lengthAdjust="spacingAndGlyphs"')
            if style.bold:
                attributes.append('font-weight="bold"')
            if style.italic:
                attributes.append('font-style="italic"')
            if style.dim:
                attributes.append('opacity="0.6"')
            if style.underline:
                attributes.append('text-decoration="underline"')
            texts.append(f'<text {" ".join(attributes)}>{html.escape(text, quote=False)}</text>')

    heading = []
    if title:
        heading = [
            f'<rect x="0" y="0" width="{width:.2f}" height="{TITLE_HEIGHT}" rx="8" fill="#323233"/>',
            f'<rect x="0" y="{TITLE_HEIGHT - 8}" width="{width:.2f}" height="8" fill="#323233"/>',
            *(f'<circle cx="{16 + i * 20}" cy="{TITLE_HEIGHT / 2}" r="6" fill="{colour}"/>'
              for i, colour in enumerate(("#ff5f57", "#febc2e", "#28c840"))),
            f'<text x="{width / 2:.2f}" y="{TITLE_HEIGHT / 2 + 5}" fill="#cccccc" text-anchor="middle" '
            f'font-size="13">{html.escape(title, quote=False)}</text>',
        ]

    return "\n".join([
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width:.0f}" height="{height:.0f}" '
        f'viewBox="0 0 {width:.2f} {height:.2f}" role="img" aria-label="{html.escape(title or "terminal screen")}">',
        '<g font-family="ui-monospace, SFMono-Regular, \'Cascadia Mono\', \'DejaVu Sans Mono\', Menlo, Consolas, '
        f'\'Liberation Mono\', monospace" font-size="{FONT_SIZE}" xml:space="preserve" style="white-space: pre">',
        f'<rect x="0" y="0" width="{width:.2f}" height="{height:.2f}" rx="8" fill="{DEFAULT_BG}"/>',
        *heading,
        *background,
        *shapes,
        *merged_lines(segments),
        *paths,
        *texts,
        "</g>",
        "</svg>",
        "",
    ])


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", help="the screen, with escape sequences (tmux capture-pane -e -N -p)")
    parser.add_argument("target", help="the SVG file to write")
    parser.add_argument("--title", help="a window title to draw above the screen")
    arguments = parser.parse_args()
    with open(arguments.source, encoding="utf-8", errors="replace") as source:
        rows = parse(source.read())
    with open(arguments.target, "w", encoding="utf-8") as target:
        target.write(render(rows, arguments.title))
    return 0


if __name__ == "__main__":
    sys.exit(main())
