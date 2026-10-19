#!/usr/bin/env python3
"""Check the redesign documents/data and source traceability; never runs a UI."""

import argparse
import csv
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = Path(__file__).resolve().parent
SKY_COMMIT = "bf5ef211fa66e76dd83ac171a3fea944b6d5dc26"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sky-readme", type=Path)
    parser.add_argument("--sky-css", type=Path)
    args = parser.parse_args()
    require(bool(args.sky_readme) == bool(args.sky_css), "Supply both pinned Sky source files")

    with (DOCS / "inventory.csv").open(newline="") as stream:
        rows = list(csv.DictReader(stream))
    require(rows, "Empty inventory")
    ids = [row["id"] for row in rows]
    require(len(ids) == len(set(ids)), "Duplicate inventory ID")
    states = set("shell data form auth motion feed camera stop long loading empty invalid denied expired fault offline recovery".split())
    slices = {"web", "native", "terminal", "installer", "contract"}
    statuses = {"pending", "in-progress", "implemented-unverified", "verified"}
    stops = {"web-shell", "native-shell", "tui-f9", "command-only", "installer-no-motion", "not-connected", "adapter-owned"}
    for row in rows:
        label = row["id"]
        require(all(row.values()), f"{label}: empty data cell")
        source = ROOT / row["source"]
        require(source.is_file() and source.resolve().is_relative_to(ROOT), f"{label}: missing/nonlocal source")
        require(row["source_anchor"] in source.read_text(), f"{label}: source anchor absent")
        require(set(row["states"].split(";")) <= states, f"{label}: unknown state profile")
        require(row["slice"] in slices, f"{label}: unknown slice")
        require(row["redesign_status"] in statuses, f"{label}: unknown status")
        require(row["stop"] in stops, f"{label}: unknown Stop applicability")
        if row["redesign_status"] == "verified":
            require(not row["redesign_evidence"].startswith("none"), f"{label}: verified without evidence")

    covered = {row["source"] for row in rows}
    web = ROOT / "src/HVO.RoofControllerV4.Web/Components"
    routes = 0
    for path in web.rglob("*.razor"):
        for route in re.findall(r'^@page "([^"]+)"', path.read_text(), re.M):
            rel = path.relative_to(ROOT).as_posix()
            require(any(row["source"] == rel and row["source_anchor"] == f'@page "{route}"' for row in rows), f"Uninventoried route {route}")
            routes += 1
    for pattern in ("src/HVO.RoofControllerV4.Screens/Kiosk*Page.cs", "src/HVO.RoofControllerV4.Cli/Ui/RoofUi*Page.cs"):
        for path in ROOT.glob(pattern):
            if path.name == "RoofUiPage.cs":
                continue  # Shared helper is checked through its own inventory row/source anchor.
            require(path.relative_to(ROOT).as_posix() in covered, f"Uninventoried page {path.name}")
    wizard_path = "src/HVO.RoofControllerV4.Installer/Wizard/WizardPages.cs"
    for name in re.findall(r'class (\w+Page)\s*:', (ROOT / wizard_path).read_text()):
        require(any(row["source"] == wizard_path and row["source_anchor"] == f"class {name}" for row in rows), f"Uninventoried installer page {name}")

    with (DOCS / "installer-steps.csv").open(newline="") as stream:
        steps = list(csv.DictReader(stream))
    require(len({step["id"] for step in steps}) == len(steps), "Duplicate plan-output ID")
    concrete = set()
    for path in (ROOT / "src/HVO.RoofControllerV4.Installer/Plan").glob("*.cs"):
        concrete.update(re.findall(r'(?:public|internal) sealed class (\w+)[^{]*? : (?:PlanStep|CameraFileStep)(?=[,(\s])', path.read_text()))
    require({step["step"] for step in steps} == concrete, "Plan-output coverage differs from concrete installer steps")
    for step in steps:
        require(all(step.values()), f"{step['id']}: empty plan-output cell")
        require(step["source_anchor"] in (ROOT / step["source"]).read_text(), f"{step['id']}: missing source anchor")
        require(set(step["surface_ids"].split(";")) <= set(ids), f"{step['id']}: missing parent surface")
        require(step["redesign_status"] in statuses, f"{step['id']}: unknown status")
        if step["redesign_status"] == "verified":
            require(not step["redesign_evidence"].startswith("none"), f"{step['id']}: verified without evidence")

    data = json.loads((DOCS / "tokens.json").read_text())
    require(data["source"]["commit"] == SKY_COMMIT, "Wrong Sky revision")
    tokens = data["roles"]
    names = [token["role"] for token in tokens]
    expected = {"background", "surface", "surface-raised", "border", "text", "muted", "accent", "focus", "success", "warning", "danger", "info"}
    expected |= {f"{tone}-{suffix}" for tone in ("success", "warning", "danger", "info") for suffix in ("background", "text")}
    require(len(names) == len(set(names)) == 20 and set(names) == expected, "Incomplete/duplicate semantic roles")
    ansi = set("Black Blue Green Cyan Red Magenta Yellow Gray DarkGray BrightBlue BrightGreen BrightCyan BrightRed BrightMagenta BrightYellow White".split())
    for token in tokens:
        require(re.fullmatch(r"#[0-9a-f]{6}", token["value"]), f"Invalid RGB: {token['role']}")
        require(token["ansi16"] in ansi, f"Invalid ANSI name: {token['role']}")
    for tone, pair in data["notice_pairs"].items():
        require(pair == {"foreground": f"{tone}-text", "background": f"{tone}-background", "standalone": tone}, f"Incomplete notice pair: {tone}")
    require(set(data["notice_pairs"]) == {"success", "warning", "danger", "info"}, "Missing notice pair")
    for action in data["roof_actions"].values():
        for key in ("foreground", "background", "border", "indicator"):
            if key in action:
                require(action[key] in expected, f"Action references unknown role {action[key]}")

    if args.sky_readme:
        for entry, path in zip(data["source"]["files"], (args.sky_readme, args.sky_css)):
            require(hashlib.sha256(path.read_bytes()).hexdigest() == entry["sha256"], f"Pinned source hash differs: {entry['path']}")
        primary = re.findall(r'\| ([a-z-]+) \| `(--[a-z-]+)` \| `(#\w+)` \| (\w+) \|', args.sky_readme.read_text())
        actual = [(token["role"], token["css_token"], token["value"], token["ansi16"]) for token in tokens]
        require(actual == primary, "Roles/values/fallbacks differ from final merged primary table")
        css = dict(re.findall(r"(--hvo-[\w-]+):\s*([^;]+);", args.sky_css.read_text()))
        bg = tuple(int(data["roles"][0]["value"][i:i + 2], 16) for i in (1, 3, 5))
        for token in tokens:
            value = css[token["css_token"]].strip()
            if value.startswith("rgba"):
                red, green, blue, alpha = map(float, re.findall(r"[\d.]+", value))
                channels = [int(c * alpha + b * (1 - alpha) + 0.5) for c, b in zip((red, green, blue), bg)]
                value = "#" + "".join(f"{c:02x}" for c in channels)
            require(value.lower() == token["value"], f"CSS/composite mismatch: {token['role']}")
    print(f"PASS: {len(rows)} source-traced rows, {routes} routes, {len(steps)} installer steps, 20 roles; "
          + ("pinned primary table/CSS/hash verified" if args.sky_readme else "primary-source check not requested"))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError) as error:
        raise SystemExit(f"FAIL: {error}") from error
