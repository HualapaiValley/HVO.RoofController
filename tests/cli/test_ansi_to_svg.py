#!/usr/bin/env python3
"""Tests for ansi-to-svg.py, which draws the terminal screens in the CI artifacts and docs/cli.md (#45).

    python3 -m unittest discover -s tests/cli -p 'test_*.py'
"""

import importlib.util
import pathlib
import re
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ElementTree

_spec = importlib.util.spec_from_file_location("ansi_to_svg", pathlib.Path(__file__).with_name("ansi-to-svg.py"))
ansi_to_svg = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ansi_to_svg)

SVG = "{http://www.w3.org/2000/svg}"


def draw(screen):
    """The screen drawn as SVG, parsed: a screen the converter cannot draw as valid XML fails here."""
    return ElementTree.fromstring(ansi_to_svg.render(ansi_to_svg.parse(screen), "test"))


def texts(svg):
    return [element.text for element in svg.iter(f"{SVG}text")][1:]  # the first is the window title


def fills(svg, tag):
    return {element.get("fill") for element in svg.iter(f"{SVG}{tag}")}


class AnsiToSvgTests(unittest.TestCase):
    def test_actual_driver_cells_keep_joined_graphemes_on_the_measured_grid(self):
        def cell(text, columns):
            return dict(text=text, columns=columns, foreground=None, background=None,
                        bold=False, dim=False, underline=False, reverse=False)
        rows = ansi_to_svg.parse_cells([[cell("👩🏽‍🔬", 2), None, cell("x", 1)]])
        self.assertEqual(3, len(rows[0]))
        self.assertTrue(ansi_to_svg.is_wide(rows[0][0][0]))
        svg = ElementTree.fromstring(ansi_to_svg.render(rows, "driver"))
        self.assertEqual(["👩🏽‍🔬", "x"], texts(svg))
        elements = list(svg.iter(f"{SVG}text"))
        self.assertEqual("16.80", elements[1].get("textLength"))
        self.assertEqual("28.80", elements[2].get("x"))

    def test_local_host_mask_uses_cells_after_a_joined_grapheme(self):
        style = ansi_to_svg.Style()
        rows = [[(ansi_to_svg.CellGlyph("👩🏽‍🔬", 2), style), None] + [(c, style) for c in " host-test done"]]
        with patch.object(ansi_to_svg.socket, "gethostname", return_value="host-test"):
            ansi_to_svg.mask_local_host(rows)
        text = ''.join(cell[0] for cell in rows[0] if cell)
        self.assertEqual("👩🏽‍🔬 [host]    done", text)
        self.assertEqual(17, len(rows[0]))

    def test_a_combining_mark_stays_with_its_letter(self):
        svg = draw("Name: José  Role: admin\n")
        self.assertIn("Name: José", texts(svg))

    def test_a_combining_mark_after_a_wide_character(self):
        svg = draw("東́ x\n")
        self.assertIn("東́", texts(svg))

    def test_escapes_that_draw_nothing_are_left_out(self):
        screen = "\x1b=\x1b>a\x1b7b\x1b8\x1b(Bc\x1b[?25l\x1b[2Jd\x07\x01\x7fe\x1b[12\n"
        svg = draw(screen)
        self.assertEqual(["abcde"], texts(svg))

    def test_a_hyperlink_keeps_its_text(self):
        svg = draw("┌┤\x1b]8;;http://127.0.0.1:15196/\x1b\\HVO roof\x1b]8;;\x1b\\├┐\n")
        self.assertEqual(["HVO roof"], texts(svg))

    def test_colours_out_of_range_are_clamped(self):
        svg = draw("\x1b[38;5;300ma\x1b[38;2;300;0;999mb\n")
        colours = fills(svg, "text")
        self.assertIn("#eeeeee", colours)
        self.assertIn("#ff00ff", colours)
        for colour in colours | fills(svg, "rect"):
            self.assertRegex(colour, r"^#[0-9a-f]{6}$")

    def test_colon_separated_colours(self):
        svg = draw("\x1b[38:2::1:2:3ma\x1b[38:2:4:5:6mb\x1b[38:5:196mc\n")
        self.assertTrue({"#010203", "#040506", "#ff0000"} <= fills(svg, "text"))

    def test_the_colour_carries_on_to_the_next_line(self):
        # tmux writes a colour where it changes, so the second line has the first one's background.
        rows = ansi_to_svg.parse("\x1b[38;2;248;250;252m\x1b[48;2;5;7;13mfirst\nsecond\n\x1b[0mthird\n")
        self.assertEqual(("#f8fafc", "#05070d"), rows[1][0][1].colours())
        self.assertEqual((ansi_to_svg.DEFAULT_FG, ansi_to_svg.DEFAULT_BG), rows[2][0][1].colours())

    def test_the_margin_has_the_screens_background(self):
        svg = draw("\x1b[48;2;5;7;13mabc\ndef\n\x1b[48;2;255;193;7mg\n")
        screen = next(svg.iter(f"{SVG}rect"))
        self.assertEqual("#05070d", screen.get("fill"))
        # Only the cell with another background has a rectangle of its own.
        self.assertIn("#ffc107", fills(svg, "rect"))

    def test_a_plain_screen_keeps_the_terminal_background(self):
        svg = draw("plain text\n")
        self.assertEqual(ansi_to_svg.DEFAULT_BG, next(svg.iter(f"{SVG}rect")).get("fill"))

    def test_wide_characters_take_two_cells(self):
        rows = ansi_to_svg.parse("東京!\n")
        self.assertEqual(5, len(rows[0]))
        self.assertIsNone(rows[0][1])

    def test_markup_in_text_is_escaped(self):
        svg = draw("<script> & \"x\"\n")
        self.assertTrue(any(re.search(r"<script> & \"x\"", text) for text in texts(svg)))


if __name__ == "__main__":
    unittest.main()
