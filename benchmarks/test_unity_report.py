#!/usr/bin/env python3
"""Tests of unity_report.py on the results in fixtures/unity-bench-* (run: python3 benchmarks/test_unity_report.py)."""
import contextlib
import io
import os
import subprocess
import sys
import tempfile
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
        text, _ = report("unity-bench-6000.3.27f1", "unity-bench-2022.3.62f2")
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
        text, _ = report("unity-bench-6000.3.27f1")
        self.assertIn(
            "Unity 6000.3.27f1, IL2CPP player (C++ Release, code generation OptimizeSpeed, stripping Minimal, "
            "not a Development build, Nino.Core Release), Linux x64, AMD EPYC 7763 64-Core Processor (4 cores), "
            "Burst 1.8.31 (x64 targets SSE2, SSE4, AVX2), kernels AVX2.", text.splitlines())
        # The 2022.3 fixture's player was built with Burst's default targets.
        self.assertIn("Burst 1.8.29 (its default x64 targets), kernels SSSE3.", report("unity-bench-2022.3.62f2")[0])

    def test_rows(self):
        text, _ = report("unity-bench-6000.3.27f1", "unity-bench-2022.3.62f2")
        lines = text.splitlines()
        self.assertIn("| Case | Operation | Without Burst | With Burst | Speedup |", lines)
        self.assertIn("|---|---|--:|--:|--:|", lines)
        self.assertIn("| (int, long)[32] | Serialize | 3.28 μs | 835.40 ns | 3.92× |", lines)
        self.assertNotIn("900.1", text)  # the "SSSE3 only" sample group stays out of the section
        self.assertIn("| (int, long)[1000] | Deserialize | 64.86 μs | 1.16 μs | 55.9× |", lines)
        self.assertIn("| (int, long)[100000] | Serialize | 8.16 ms | 612.00 μs | 13.3× |", lines)
        self.assertIn(
            "| List&lt;string&gt; of 1000 ASCII strings (8–40 chars) | Deserialize | 0.90 ns | 0.80 ns | 1.12× |", lines)
        # Each case once, Serialize first, in the order the cases were run (NUnit runs Deserialize's tests first).
        body = [line for line in lines if line.startswith("| (int, long)")]
        self.assertEqual([line.split(" | ")[1] for line in body], ["Serialize", "Deserialize"] * 3)
        self.assertEqual(body[0].split(" | ")[0], "| (int, long)[32]")
        # Another class's results are not rows.
        self.assertNotIn("Other.Test", text)
        self.assertNotIn("| Speed |", text)

    def test_units_are_converted(self):
        self.assertEqual(unity_report.median_ns({"Unit": 2, "Median": 1.5}), 1.5e6)
        self.assertEqual(unity_report.median_ns({"Unit": 1, "Median": 2.0}), 2e3)
        self.assertIsNone(unity_report.median_ns({"Unit": 4, "Median": 2.0}))  # bytes, not a time

    def test_bad_lines_and_empty_files_are_left_out(self):
        text, err = report("unity-bench-6000.3.27f1", "unity-bench-empty", "unity-bench-missing")
        self.assertNotIn("broken", text)
        self.assertIn("unreadable line", err)
        self.assertIn("unity-bench-empty: no performance test results", err)
        self.assertIn("unity-bench-missing: no performance test results", err)
        self.assertEqual(text.count("### Unity"), 1)

    def test_no_rows_fails(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            unity_report.report("v4.0.0-test", [fixture("unity-bench-empty")])

    def test_cells_are_escaped(self):
        self.assertEqual(unity_report.cell("List<string> | x"), "List&lt;string&gt; \\| x")

    def test_missing_build_line_says_less(self):
        run = unity_report.parse(fixture("unity-bench-6000.3.27f1"))
        run["build"], run["ci"] = {}, {}
        run["run"]["Dependencies"] = []
        line = unity_report.environment(run)
        self.assertTrue(line.startswith("Unity 6000.3.27f1, IL2CPP player (not a Development build), Linux x64"))
        self.assertNotIn("Burst 1", line)


    def test_release_body_appends_the_section_once(self):
        # publish-unity appends the section with release_body.py append: after the body, then never again.
        section = report("unity-bench-6000.3.27f1")[0]
        script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "release_body.py")
        with tempfile.TemporaryDirectory() as folder:
            body, new, again = (os.path.join(folder, name) for name in ("body.md", "new.md", "again.md"))
            with open(os.path.join(folder, "section.md"), "w", encoding="utf-8") as f:
                f.write(section)
            with open(body, "w", encoding="utf-8") as f:
                f.write("## 📊 Performance Report\n\nrows\n")

            def append(source, out):
                subprocess.run([sys.executable, script, "append", "--body", source, "--out", out,
                                os.path.join(folder, "section.md")], check=True, capture_output=True)

            append(body, new)
            with open(new, encoding="utf-8") as f:
                text = f.read()
            self.assertTrue(text.startswith("## 📊 Performance Report"))
            self.assertEqual(text.count(unity_report.HEADING), 1)
            self.assertTrue(text.endswith(section))
            append(new, again)
            self.assertFalse(os.path.exists(again))


if __name__ == "__main__":
    unittest.main()
