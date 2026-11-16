#!/usr/bin/env python3
"""Tests of generator_report.py and readme.py --build on fixtures/generator/ (the --json files of
benchmarks/Nino.GeneratorBenchmark, with the numbers of the old local run) (run: python3
benchmarks/reports/test_generator_report.py)."""
import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import generator_report  # noqa: E402
import readme  # noqa: E402


def load(name):
    with open(os.path.join(HERE, "fixtures", "generator", name + ".json"), encoding="utf-8") as f:
        return json.load(f)


def section(tag="v4.0.0-test"):
    # Given in no particular order: the tables come out fewest types first.
    return generator_report.section(tag, [load("v3-100"), load("v3-30")], [load("v4-30"), load("v4-100")])


TABLE_100 = """| | v3 (3.3.13) | v4 | v4 vs v3 |
|---|---:|---:|---:|
| Generated source | 62.0 MB (1,204,143 lines) | 4.38 MB (77,535 lines) | **14× smaller** |
| Compiling the generated code (emit) | 462 s | 2.10 s | **220× faster** |
| Running the generator | 4.60 s | 0.42 s | **11× faster** |
| Re-generating after editing a member | 3.50 s | 0.48 s | **7.3× faster** |
| Re-generating after editing a comment | 4.00 s | 0.44 s | **9.1× faster** |
| IL of the generated code | 7.30 MB | 1.50 MB | **4.9× smaller** |"""


class SectionTests(unittest.TestCase):
    def test_layout(self):
        text = section()
        lines = text.splitlines()
        self.assertEqual(lines[0], generator_report.HEADING)
        self.assertIn("Environment: Intel Xeon 2.10 GHz, 4 cores, .NET 10.0.0, Roslyn 4.3.0.0", lines)
        self.assertLess(lines.index("### 330 types (N = 30)"), lines.index("### 1,100 types (N = 100)"))
        self.assertIn(TABLE_100, text)
        self.assertIn("(v3: 3 at 330 types, 2 at 1,100 types; v4: 5 at 330 types, 5 at 1,100 types)", text)
        self.assertEqual(lines[-1], "</details>")
        # Parsers of the .NET report find it by these words (release_body.py).
        for word in ("Performance Report", "Method", "Mean"):
            self.assertNotIn(word, text)

    def test_ratios_never_say_slower(self):
        self.assertEqual(generator_report.ratio(100, 98, "faster"), "on par")
        self.assertEqual(generator_report.ratio(100, 200, "faster"), "—")
        self.assertEqual(generator_report.ratio(2210, 420, "smaller"), "**5.3× smaller**")

    def test_compile_errors_fail(self):
        broken = dict(load("v4-30"), errors=3)
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            generator_report.section("v4.0.0-test", [load("v3-30")], [broken])

    def test_unpaired_workloads_fail(self):
        with self.assertRaises(SystemExit):
            generator_report.section("v4.0.0-test", [load("v3-30")], [load("v4-100")])


class ParseTests(unittest.TestCase):
    def test_round_trip_in_a_body(self):
        body = "## 📊 Performance Report\n\nx\n\n" + section() + "\n## 🎮 Unity benchmark (Burst, IL2CPP)\n\n| a |\n"
        found = generator_report.parse(body)
        self.assertEqual(found["environment"], "Intel Xeon 2.10 GHz, 4 cores, .NET 10.0.0, Roslyn 4.3.0.0")
        self.assertEqual([(t["types"], t["roots"]) for t in found["tables"]], [(330, 30), (1100, 100)])
        self.assertEqual(found["tables"][1]["table"], TABLE_100)
        self.assertEqual(found["tables"][1]["rows"]["emit"], ("462 s", "2.10 s", "**220× faster**"))

    def test_no_section(self):
        self.assertIsNone(generator_report.parse("## 📊 Performance Report\n"))

    def test_missing_row(self):
        text = section().replace("| Running the generator |", "| Something else |")
        with self.assertRaises(ValueError):
            generator_report.parse(text)


class ReadmeTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.path = os.path.join(self.dir, "section.md")
        Path(self.path).write_text(section(), encoding="utf-8")

    def test_values(self):
        values = readme.build(self.path, "v4.0.0-test")
        self.assertEqual(values["v3.build"], (
            "generated code compiles **220× faster** at 1,100 types (462 s → 2.10 s); **14× smaller** source",
            "1,100 个类型时生成代码的编译**快 220 倍**（462 秒 → 2.10 秒）；源码**小 14 倍**"))
        self.assertEqual(values["doc.build.100"], "\n" + TABLE_100 + "\n")
        self.assertIn("doc.build.30", values)
        self.assertTrue(values["doc.build.source"].startswith(
            "the build section of [v4.0.0-test](https://github.com/JasonXuDeveloper/Nino/releases/tag/v4.0.0-test): "))

    def test_every_marker_exists(self):
        # README.md, README.zh.md and docs/v4-benchmarks.md carry a marker for every key, as main() fills them.
        values = readme.build(self.path, "v4.0.0-test")
        for name, zh in (("README.md", False), ("README.zh.md", True), ("docs/v4-benchmarks.md", False)):
            copy = Path(self.dir) / os.path.basename(name)
            shutil.copy(readme.ROOT / name, copy)
            keys = {k for k in values if k.startswith("doc.") == name.startswith("docs/")}
            self.assertEqual(readme.fill(copy, {k: values[k] for k in keys}, zh), keys, name)
            text = copy.read_text(encoding="utf-8")
            if name == "README.zh.md":
                self.assertIn("<!--bench:v3.build-->1,100 个类型时生成代码的编译**快 220 倍**", text)
            elif name == "README.md":
                self.assertIn("<!--bench:v3.build-->generated code compiles **220× faster**", text)
            else:
                self.assertIn("<!--bench:doc.build.100-->\n" + TABLE_100 + "\n<!--/bench-->", text)

    def test_section_missing(self):
        Path(self.path).write_text("## 📊 Performance Report\n", encoding="utf-8")
        with self.assertRaises(SystemExit):
            readme.build(self.path)


if __name__ == "__main__":
    unittest.main()
