#!/usr/bin/env python3
"""Checks ConformU's results against what this phase of the Alpaca server expects (#220).

Two kinds of run, as tests/alpaca/conformu-expected.json names them:

- "results": a conformance check's machine-readable results file (--resultsfile). It must report no error and no
  issue (ConformU logs some failures, the shutter state straight after a command among them, as issues, so no errors
  alone is not enough), and exactly the configuration alerts listed.
- "protocol-log": the Alpaca protocol check's log, which ends with its summary (it writes no results file). It must
  report no error and no issue, and every information message must be one listed.

Every listed alert or message must also be reported, so a check that ConformU stops making is noticed. Each carries
why it is expected.

Usage: check-conformu-results.py --expected conformu-expected.json NAME=PATH [NAME=PATH ...]
"""

import argparse
import json
import re
import sys

SUMMARY = re.compile(r"Found (\d+) errors?, (\d+) issues? and (\d+) information messages?\.")
CLEAN = "Congratulations there were no errors, issues or information alerts"
ENTRY = re.compile(r"^\d{2}:\d{2}:\d{2}\.\d+ (?P<member>[A-Z]+ \S+) ==> (?P<text>.*)$")
HEADING = re.compile(r"^\d{2}:\d{2}:\d{2}\.\d+ (Error|Issue|Information Message) Summary\s*$")


def load_json(path):
    with open(path, encoding="utf-8-sig") as stream:
        return json.load(stream)


def entries(results, kind):
    """The (key, message) pairs of one kind (Errors, Issues, ConfigurationAlerts) in a results file."""
    return [(entry.get("Key", ""), entry.get("Value", "")) for entry in results.get(kind) or []]


def check_results(name, results, expected):
    """The problems with a conformance check's results file."""
    problems = []
    for kind, count in (("Errors", "ErrorCount"), ("Issues", "IssueCount")):
        found = entries(results, kind)
        if results.get(count, 0) != 0 or found:
            problems.append(f"{name}: {results.get(count, len(found))} {kind.lower()}, expected none")
            problems.extend(f"  {key}: {message}" for key, message in found)

    allowed = expected.get("ConfigurationAlerts", [])
    seen = set()
    for key, message in entries(results, "ConfigurationAlerts"):
        match = next((index for index, alert in enumerate(allowed) if alert["Key"] == key and alert["Contains"] in message), None)
        if match is None:
            problems.append(f"{name}: an unexpected configuration alert, {key}: {message}")
        else:
            seen.add(match)

    problems.extend(
        f"{name}: the expected configuration alert was not reported, {alert['Key']}: {alert['Contains']}"
        for index, alert in enumerate(allowed) if index not in seen)
    return problems


def protocol_summary(log):
    """(errors, issues, information messages) from a protocol check's log, or None when it did not finish."""
    if CLEAN in log:
        return 0, 0, []

    match = SUMMARY.search(log)
    if match is None:
        return None

    messages, section, current = [], None, None
    for line in log[match.end():].splitlines():
        heading = HEADING.match(line)
        if heading:
            section = heading.group(1)
            continue

        entry = ENTRY.match(line)
        if entry:
            current = None
            if section == "Information Message":
                current = {"member": entry.group("member"), "text": entry.group("text")}
                messages.append(current)
        elif current is not None and line.strip():
            current["text"] += " " + line.strip()

    return int(match.group(1)), int(match.group(2)), messages


def check_protocol(name, log, expected):
    """The problems with a protocol check's log."""
    summary = protocol_summary(log)
    if summary is None:
        return [f"{name}: the protocol check's log has no summary: it did not finish"]

    errors, issues, messages = summary
    problems = []
    if errors or issues:
        problems.append(f"{name}: {errors} errors and {issues} issues, expected none (the log's Error and Issue Summary say which)")

    allowed = expected.get("InformationMessages", [])
    seen = set()
    for message in messages:
        match = next((index for index, item in enumerate(allowed)
                      if item["Member"] == message["member"] and item["Contains"] in message["text"]), None)
        if match is None:
            problems.append(f"{name}: an unexpected information message, {message['member']}: {message['text']}")
        else:
            seen.add(match)

    problems.extend(
        f"{name}: the expected information message was not reported, {item['Member']}: {item['Contains']}"
        for index, item in enumerate(allowed) if index not in seen)
    return problems


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--expected", required=True, help="the expectations file (tests/alpaca/conformu-expected.json)")
    parser.add_argument("runs", nargs="+", metavar="NAME=PATH", help="a run's name in the expectations, and its results file or log")
    arguments = parser.parse_args(argv)

    expectations = load_json(arguments.expected)
    failed = False
    for run in arguments.runs:
        name, _, path = run.partition("=")
        if not path:
            parser.error(f"{run}: give NAME=PATH")

        expected = expectations.get(name)
        if expected is None:
            problems = [f"{name}: no expectations for this run in {arguments.expected}"]
        else:
            try:
                if expected.get("Kind") == "protocol-log":
                    with open(path, encoding="utf-8-sig", errors="replace") as stream:
                        problems = check_protocol(name, stream.read(), expected)
                else:
                    problems = check_results(name, load_json(path), expected)
            except (OSError, ValueError) as error:
                problems = [f"{name}: {path} could not be read ({error.__class__.__name__}); did ConformU run?"]

        if problems:
            failed = True
            for problem in problems:
                print(problem, file=sys.stderr)
        else:
            print(f"{name}: no errors and no issues; only the expected messages and alerts")

    if failed:
        print("ConformU's results do not meet this phase's expectations (tests/alpaca/conformu-expected.json).", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
