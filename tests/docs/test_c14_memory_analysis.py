"""Checks for the offline investigation report, independent of a .NET runtime."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("c14_memory", ROOT / "build/analyze-c14-memory.py")
ANALYSIS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ANALYSIS)


class MemoryAnalysisTests(unittest.TestCase):
    def test_short_run_is_not_assessed(self):
        self.assertEqual(ANALYSIS.memory_check([(i, 100) for i in range(10)]), {"assessed": False})

    def test_endpoint_and_trend_use_the_original_strict_ten_percent_limit(self):
        points = [(i * 60, 100 if i < 5 else 110) for i in range(10)]
        result = ANALYSIS.memory_check(points)
        self.assertAlmostEqual(result["growthPercent"], 10)
        self.assertFalse(result["passed"])
        self.assertGreater(result["trendPercent"], 10)

    def test_a_flat_endpoint_does_not_hide_a_rising_trend(self):
        values = [100] * 5 + [50] * 15 + [150] * 15 + [100] * 5
        result = ANALYSIS.memory_check([(i * 60, value) for i, value in enumerate(values)])
        self.assertEqual(result["growthPercent"], 0)
        self.assertGreater(result["trendPercent"], 10)
        self.assertFalse(result["passed"])

    def test_only_a_contiguous_aligned_anonymous_heap_matches_the_arena_shape(self):
        fixture = """04000000-04200000 rw-p 00000000 00:00 0 [anonymous]
Rss: 1024 kB
04200000-08000000 ---p 00000000 00:00 0 [anonymous]
Rss: 0 kB
08000000-08200000 rw-p 00000000 00:00 0 [anonymous]
Rss: 2048 kB
08300000-0c000000 ---p 00000000 00:00 0 [anonymous]
Rss: 0 kB
0c001000-0c200000 rw-p 00000000 00:00 0 [anonymous]
Rss: 3072 kB
0c200000-10000000 ---p 00000000 00:00 0 [anonymous]
Rss: 0 kB
10000000-10200000 rw-s 00000000 00:00 0 [runtime-doublemapper]
Rss: 4096 kB
10200000-10400000 rw-p 00000000 00:00 0 [heap]
Rss: 512 kB
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "maps.txt"
            path.write_text(fixture)
            result = ANALYSIS.mapping_residency(path)
        self.assertEqual(result["arenaShapedHeaps"], 1)
        self.assertEqual(result["rssMiB"]["arenaShapedAnonymous"], 1)
        self.assertEqual(result["rssMiB"]["otherAnonymous"], 5)
        self.assertEqual(result["rssMiB"]["runtimeDoublemapper"], 4)
        self.assertEqual(sum(result["rssMiB"].values()), 10.5)


if __name__ == "__main__":
    unittest.main()
