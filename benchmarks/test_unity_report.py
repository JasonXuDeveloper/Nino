#!/usr/bin/env python3
"""Tests of unity_report.py on the lines in fixtures/ (run: python3 benchmarks/test_unity_report.py)."""
import contextlib
import io
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import unity_report  # noqa: E402


def fixture(name):
    return os.path.join(HERE, "fixtures", name)


def report(*names):
    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        text = unity_report.report("v4.0.0-test", [fixture(n) for n in names])
    return text, err.getvalue()


class UnityReportTests(unittest.TestCase):
    def test_section_layout(self):
        text, _ = report("unity-bench-6000.3.27f1.txt", "unity-bench-2022.3.62f2.txt")
        lines = text.splitlines()
        self.assertEqual(lines[0], unity_report.HEADING)
        self.assertEqual(lines[1], "")
        # One environment line per Unity version, in the order given, then the legend.
        self.assertTrue(lines[2].startswith("Unity 6000.3.27f1, IL2CPP player (C++ Release, "))
        self.assertTrue(lines[3].startswith("Unity 2022.3.62f2, IL2CPP player ("))
        self.assertEqual(lines[4], unity_report.LEGEND)
        self.assertIn("### Unity 6000.3.27f1", lines)
        self.assertIn("### Unity 2022.3.62f2", lines)
        self.assertLess(lines.index("### Unity 6000.3.27f1"), lines.index("### Unity 2022.3.62f2"))
        self.assertTrue(text.endswith("|\n"))
        # Never the words the .NET report is found by, nor its column names.
        for word in ("Performance Report", "| Method", "| Mean"):
            self.assertNotIn(word, text)

    def test_environment_line(self):
        text, _ = report("unity-bench-6000.3.27f1.txt")
        self.assertIn(
            "Unity 6000.3.27f1, IL2CPP player (C++ Release, code generation OptimizeSpeed, stripping Minimal, "
            "not a Development build, Nino.Core Release), Linux x64, AMD EPYC 7763 64-Core Processor (4 cores), "
            "Burst 1.8.29, kernels SSSE3.", text.splitlines())

    def test_rows(self):
        text, _ = report("unity-bench-6000.3.27f1.txt", "unity-bench-2022.3.62f2.txt")
        lines = text.splitlines()
        self.assertIn("| Case | Operation | Without Burst | With Burst | Speedup |", lines)
        self.assertIn("|---|---|--:|--:|--:|", lines)
        self.assertIn("| (int, long)[32] | Serialize | 3.28 μs | 835.4 ns | 3.92× |", lines)
        self.assertIn("| (int, long)[1000] | Deserialize | 64.86 μs | 1.16 μs | 55.9× |", lines)
        self.assertIn("| (int, long)[100000] | Serialize | 8.16 ms | 612.00 μs | 13.3× |", lines)
        self.assertIn("| List of 1000 ASCII strings (8–40 chars) | Deserialize | 0.9 ns | 0.8 ns | 1.12× |", lines)

    def test_bad_lines_and_empty_files_are_left_out(self):
        text, err = report("unity-bench-2022.3.62f2.txt", "unity-bench-empty.txt")
        self.assertNotIn("broken", text)
        self.assertIn("unreadable line", err)
        self.assertIn("unity-bench-empty.txt: no NINO_UNITY_BENCH lines", err)
        self.assertEqual(text.count("### Unity"), 1)

    def test_no_rows_fails(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            unity_report.report("v4.0.0-test", [fixture("unity-bench-empty.txt")])

    def test_cells_are_escaped(self):
        self.assertEqual(unity_report.cell("List<string> | x"), "List&lt;string&gt; \\| x")

    def test_missing_build_line_says_less(self):
        run = unity_report.parse(fixture("unity-bench-6000.3.27f1.txt"))
        run["build"] = {}
        line = unity_report.environment(run)
        self.assertTrue(line.startswith("Unity 6000.3.27f1, IL2CPP player (not a Development build, Nino.Core Release)"))
        self.assertNotIn("Burst 1", line)


if __name__ == "__main__":
    unittest.main()
