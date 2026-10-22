#!/usr/bin/env python3
"""Summarize completed C14 diagnostic artifacts, without subtracting GC commitment from RSS.

Usage: python3 build/analyze-c14-memory.py label=/path/to/soak-results [...]
Writes JSON to stdout. Exact JSONL timestamps reproduce C14's resource calculations;
15-minute medians and mapping categories help interpret them. This is not a test gate.
"""

import hashlib
import json
from pathlib import Path
import re
import statistics
import sys


MIB = 1024 * 1024
ARENA_SPAN = 64 * MIB
HEADER = re.compile(r"^([0-9a-f]+)-([0-9a-f]+) (\S{4}) \S+ \S+ \S+ (\[.*\])$")


def memory_check(points):
    """The original first/last-five medians and least-squares trend, using bytes."""
    if len(points) < 10 or points[-1][0] - points[0][0] < 300:
        return {"assessed": False}
    start = statistics.median(value for _, value in points[:5])
    end = statistics.median(value for _, value in points[-5:])
    mean_x = statistics.fmean(at for at, _ in points)
    mean_y = statistics.fmean(value for _, value in points)
    variance = sum((at - mean_x) ** 2 for at, _ in points)
    slope = sum((at - mean_x) * (value - mean_y) for at, value in points) / variance
    growth = (end - start) / start
    trend = slope * (points[-1][0] - points[0][0]) / start
    return {
        "assessed": True, "samples": len(points), "spanSeconds": points[-1][0] - points[0][0],
        "startMiB": start / MIB, "endMiB": end / MIB,
        "growthPercent": growth * 100, "trendPercent": trend * 100,
        "passed": growth < 0.10 and trend < 0.10,
    }


def mapping_residency(path):
    """Recognize a glibc-like heap shape, not ownership of live malloc allocations.

    On these Linux x64 runs a non-main glibc heap has a 64 MiB aligned writable
    mapping, followed by an inaccessible remainder to the next 64 MiB boundary.
    Other runtimes, huge-page heaps and coalesced mappings may not match this shape.
    No anonymous range is guessed to be the GC's reservation.
    """
    maps = []
    for line in path.read_text().splitlines():
        if match := HEADER.match(line):
            maps.append({"start": int(match[1], 16), "end": int(match[2], 16),
                         "permissions": match[3], "category": match[4], "rss": 0})
        elif maps and line.startswith("Rss:"):
            maps[-1]["rss"] = int(line.split()[1]) * 1024
    totals = {"arenaShapedAnonymous": 0, "mainHeap": 0, "runtimeDoublemapper": 0,
              "otherAnonymous": 0, "otherMappings": 0}
    heaps = 0
    for index, mapping in enumerate(maps):
        category = mapping["category"]
        following = maps[index + 1] if index + 1 < len(maps) else None
        if (category == "[anonymous]" and mapping["permissions"] == "rw-p"
                and mapping["start"] % ARENA_SPAN == 0
                and (mapping["end"] - mapping["start"] == ARENA_SPAN or (
                    following and following["category"] == "[anonymous]"
                    and following["permissions"] == "---p" and following["start"] == mapping["end"]
                    and following["end"] == mapping["start"] + ARENA_SPAN))):
            key = "arenaShapedAnonymous"
            heaps += 1
        elif category == "[heap]":
            key = "mainHeap"
        elif category == "[runtime-doublemapper]":
            key = "runtimeDoublemapper"
        elif category in ("[anonymous]", "[named-anonymous]", "[stack]"):
            key = "otherAnonymous"
        else:
            key = "otherMappings"
        totals[key] += mapping["rss"]
    return {"arenaShapedHeaps": heaps, "rssMiB": {key: value / MIB for key, value in totals.items()}}


def summarize(directory):
    summary = json.loads((directory / "soak-summary.json").read_text())
    environment = json.loads((directory / "soak-environment.json").read_text())
    rows = [json.loads(line) for line in (directory / "soak-memory.jsonl").read_text().splitlines() if line.strip()]
    samples = [row for row in rows if row["kind"] == "sample"]
    settled = [row for row in samples if row["seconds"] >= summary["warmUpSeconds"]]
    buckets = {}
    for row in samples:
        buckets.setdefault(int(row["seconds"] // 900), []).append(row)

    def median(rows, selector):
        values = [selector(row) for row in rows]
        return statistics.median(values) if all(value is not None for value in values) else None

    series = []
    for bucket, rows in sorted(buckets.items()):
        series.append({
            "fromMinute": bucket * 15, "samples": len(rows),
            "workingSetMiB": median(rows, lambda r: r["originalSample"]["workingSetBytes"] / MIB),
            "managedMiB": median(rows, lambda r: r["originalSample"]["managedBytes"] / MIB),
            "gcCommittedMiB": median(rows, lambda r: r["gc"]["totalCommittedBytes"] / MIB),
            "anonymousMiB": median(rows, lambda r: (value / MIB if (value := r["linuxRollup"]["bytes"]["Anonymous"]) is not None else None)),
            "jitMethods": median(rows, lambda r: r["jit"]["compiledMethodCount"]),
        })
    snapshots = {}
    for phase in ("settled", "final"):
        path = directory / f"soak-smaps-{phase}.txt"
        if path.exists():
            snapshots[phase] = mapping_residency(path)
    files = ["soak-summary.json", "soak-environment.json", "soak-memory.jsonl"]
    files += [f"soak-smaps-{phase}.txt" for phase in snapshots]
    return {
        "runtime": environment["runtime"], "build": environment["build"],
        "requestedRuntimeSettings": environment.get("runtimeSettings"),
        "durationSeconds": summary["durationSeconds"], "elapsedSeconds": summary["elapsedSeconds"],
        "warmUpSeconds": summary["warmUpSeconds"], "cycles": summary["cycles"],
        "checks": summary["checks"], "passed": summary["passed"],
        "workingSet": memory_check([(r["seconds"], r["originalSample"]["workingSetBytes"]) for r in settled]),
        "managedHeap": memory_check([(r["seconds"], r["originalSample"]["managedBytes"]) for r in settled]),
        "settledJitMethodIncrease": settled[-1]["jit"]["compiledMethodCount"] - settled[0]["jit"]["compiledMethodCount"],
        "series": series, "mappingSnapshots": snapshots,
        "sha256": {name: hashlib.sha256((directory / name).read_bytes()).hexdigest() for name in files},
    }


def main(arguments):
    if not arguments:
        raise SystemExit(__doc__)
    runs = {}
    for argument in arguments:
        label, path = argument.split("=", 1)
        if label in runs:
            raise ValueError("duplicate run label")
        runs[label] = summarize(Path(path))
    json.dump({"schemaVersion": 1, "runs": runs}, sys.stdout, indent=2, allow_nan=False)
    print()


if __name__ == "__main__":
    main(sys.argv[1:])
