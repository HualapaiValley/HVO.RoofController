#!/usr/bin/env python3
"""Tests for build/check-doc-links.py (#75): the links and anchors of made-up Markdown files that resolve and those that
do not, GitHub's anchors for headings, the links in code left alone, and in a git checkout the links to files git does
not track; then this repository's own Markdown, which CI checks with the script too. From the repository root:

    python3 -m unittest discover -s tests/docs -p 'test_*.py'
"""

import contextlib
import importlib.util
import io
import pathlib
import subprocess
import tempfile
import textwrap
import unittest

REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("check_doc_links", REPOSITORY / "build" / "check-doc-links.py")
check_doc_links = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(check_doc_links)


def run(*arguments):
    """Runs the script's main; returns its exit status, standard output and standard error."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            status = check_doc_links.main(list(arguments))
        except SystemExit as exit:
            status = exit.code
    return status, out.getvalue(), err.getvalue()


class TheSlug(unittest.TestCase):
    def test_is_githubs_anchor_for_a_heading(self):
        cases = {
            "Getting it": "getting-it",
            "install.sh's tests": "installshs-tests",
            "`hvo-roof` and the Mac app": "hvo-roof-and-the-mac-app",
            "The rig end to end": "the-rig-end-to-end",
            "Trusting the CA (in browsers)": "trusting-the-ca-in-browsers",
            "**Bold** and *emphasis*": "bold-and-emphasis",
            "A [link](other.md) in it": "a-link-in-it",
            "RIG_RELEASE_DIR": "rig_release_dir",
            "RoofWeb__StopKeyFile": "roofweb__stopkeyfile",
            "__Strong__ and ~~gone~~": "strong-and-gone",
            "`__init__` and `*`": "__init__-and-",
            "Upgrading: 4.0.0 → 4.1.0": "upgrading-400--410",
            "📖 Related Documentation": "-related-documentation",
        }
        for heading, expected in cases.items():
            with self.subTest(heading=heading):
                self.assertEqual(check_doc_links.slug(heading), expected)


class AFolderOfMarkdown(unittest.TestCase):
    def setUp(self):
        self._folder = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self._folder.name).resolve()
        check_doc_links.anchors.cache_clear()
        check_doc_links.tracked.cache_clear()
        self.write("docs/guide.md", """\
            # The guide

            ## Before you start

            ## Steps

            ### Steps

            <a id="by-hand"></a>
            By hand.

            ```bash
            # Not a heading
            ```
            """)
        self.write("docs/images/screen.svg", "<svg/>")
        self.write("src/tool/README.md", "# Tool\n")

    def tearDown(self):
        self._folder.cleanup()

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(textwrap.dedent(text), encoding="utf-8")
        return path

    def broken(self, text):
        """The broken links of a page in docs/ with the text, as (line, target, why)."""
        page = self.write("docs/page.md", text)
        return [(line, target, why) for _, line, target, why in check_doc_links.check([page], self.root)]

    def test_links_that_resolve_are_not_reported(self):
        self.assertEqual(self.broken("""\
            # Page

            See [the guide](guide.md), [its start](guide.md#before-you-start), [the steps](guide.md#steps) and
            [the steps again](guide.md#steps-1), [by hand](guide.md#by-hand), [the tool](../src/tool/README.md),
            [the folder](../src/tool), [from the root](/src/tool/README.md#tool) and [here](#page).
            ![A screen](images/screen.svg)
            <img src="images/screen.svg" alt="A screen" width="96">
            [ref]: guide.md#steps

            Not checked: [the web](https://example.com/missing), [mail](mailto:someone@example.com),
            `[code](missing.md)`.

            ```markdown
            [in a block](missing.md)
            ```
            """), [])

    def test_a_missing_file_is_reported(self):
        self.assertEqual(self.broken("# Page\n\nSee [the guide](guid.md).\n"),
                         [(3, "guid.md", "no such file or folder")])

    def test_a_missing_anchor_is_reported(self):
        self.assertEqual(self.broken("# Page\n\n[A](guide.md#before-you-begin)\n[B](#nowhere)\n"), [
            (3, "guide.md#before-you-begin", "docs/guide.md has no heading or anchor #before-you-begin"),
            (4, "#nowhere", "docs/page.md has no heading or anchor #nowhere"),
        ])

    def test_a_heading_in_a_code_block_is_no_anchor(self):
        self.assertEqual(self.broken("[A](guide.md#not-a-heading)\n"),
                         [(1, "guide.md#not-a-heading", "docs/guide.md has no heading or anchor #not-a-heading")])

    def test_a_third_heading_with_the_same_text_is_numbered_2(self):
        self.write("docs/repeats.md", "# R\n\n## Notes\n\n## Notes\n\n## Notes\n")
        self.assertEqual(self.broken("[A](repeats.md#notes-2)\n[B](repeats.md#notes-3)\n"),
                         [(2, "repeats.md#notes-3", "docs/repeats.md has no heading or anchor #notes-3")])

    def test_images_and_html_are_checked_too(self):
        self.assertEqual(self.broken("![Gone](images/gone.png)\n<img src=\"images/gone.svg\">\n"), [
            (1, "images/gone.png", "no such file or folder"),
            (2, "images/gone.svg", "no such file or folder"),
        ])

    def test_a_reference_definition_is_checked_and_a_footnote_is_not_a_link(self):
        self.assertEqual(self.broken("""\
            # Page

            See [the guide][g], [gone][x] and a note.[^1]

            [g]: <guide.md#steps> "The steps"
            [x]: gone.md
            [^1]: The guide says more.
            """), [(6, "gone.md", "no such file or folder")])

    def test_a_target_with_a_space_in_angle_brackets_or_escaped(self):
        self.write("docs/a b.md", "# A B\n")
        self.assertEqual(self.broken("""\
            [A](<a b.md>), [A again](a%20b.md#a-b), [C](<c d.md>)
            """), [(1, "c d.md", "no such file or folder")])

    def test_an_underlined_heading_is_an_anchor(self):
        self.write("docs/setext.md", """\
            The title
            =========

            A section with
            two lines
            ---

            - A list item
            ---

            Text.

            ---
            """)
        self.assertEqual(self.broken("""\
            [A](setext.md#the-title), [B](setext.md#a-section-with-two-lines), [C](setext.md#a-list-item),
            [D](setext.md#text)
            """), [
            (1, "setext.md#a-list-item", "docs/setext.md has no heading or anchor #a-list-item"),
            (2, "setext.md#text", "docs/setext.md has no heading or anchor #text"),
        ])

    def test_a_heading_with_underscores_in_a_word_keeps_them(self):
        self.write("docs/settings.md", "# Settings\n\n## RoofWeb__StopKeyFile\n")
        self.assertEqual(self.broken("[A](settings.md#roofweb__stopkeyfile)\n[B](settings.md#roofwebstopkeyfile)\n"), [
            (2, "settings.md#roofwebstopkeyfile", "docs/settings.md has no heading or anchor #roofwebstopkeyfile"),
        ])

    def test_an_image_inside_a_link_is_checked_too(self):
        self.assertEqual(self.broken("[![A screen](images/gone.svg)](guide.md) [![B](images/screen.svg)](gone.md)\n"), [
            (1, "images/gone.svg", "no such file or folder"),
            (1, "gone.md", "no such file or folder"),
        ])

    def test_in_a_git_checkout_a_file_git_does_not_track_is_reported(self):
        self.write("docs/page.md", "# Page\n")
        self.write("docs/notes/kept.md", "# Kept\n")
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        subprocess.run(["git", "-C", str(self.root), "add", "docs/guide.md", "docs/page.md", "docs/notes/kept.md"],
                       check=True)
        self.write("docs/local.md", "# Local\n")
        self.assertEqual(self.broken("""\
            # Page

            [A](guide.md#steps), [B](notes), [C](notes/kept.md), [D](#page), [E](local.md), [F](images/screen.svg)
            """), [
            (3, "local.md", "not in git: a fresh clone does not have it"),
            (3, "images/screen.svg", "not in git: a fresh clone does not have it"),
        ])

    def test_a_link_outside_the_repository_is_reported(self):
        self.assertEqual(self.broken("[Up](../../outside.md)\n"), [(1, "../../outside.md", "outside the repository")])

    def test_main_lists_each_broken_link_and_fails(self):
        self.write("docs/page.md", "# Page\n\n[Gone](gone.md)\n")
        status, out, err = run("--root", str(self.root), str(self.root / "docs"))
        self.assertEqual(status, 1)
        self.assertEqual(out, "docs/page.md:3: gone.md: no such file or folder\n")
        self.assertIn("2 Markdown files, 1 broken links", err)

    def test_main_passes_when_every_link_resolves(self):
        self.write("docs/page.md", "# Page\n\n[The guide](guide.md#steps)\n")
        status, out, _ = run("--root", str(self.root), str(self.root / "docs"))
        self.assertEqual((status, out), (0, ""))


class ThisRepository(unittest.TestCase):
    def test_every_link_in_its_markdown_resolves(self):
        check_doc_links.anchors.cache_clear()
        status, out, _ = run()
        self.assertEqual((status, out), (0, ""), "build/check-doc-links.py lists the broken links")


if __name__ == "__main__":
    unittest.main()
