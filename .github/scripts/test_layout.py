"""Tests of layout.py: the paths of release tags, of this commit and of the layout after the move (python3 -m unittest).

The tags' tests read `git ls-tree` of each tag and are skipped where the clone lacks it (agents.yml fetches them).
"""

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import layout  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# What every layout keeps outside the .NET root.
RUNTIMES = {
    "GO_RUNTIME": "runtimes/go", "GO_BENCH": "runtimes/go/bench",
    "RUST_RUNTIME": "runtimes/rust", "RUST_BENCH": "runtimes/rust/bench",
    "TYPESCRIPT_RUNTIME": "runtimes/typescript", "TYPESCRIPT_BENCH": "runtimes/typescript/bench",
}
NO_RUNTIMES = dict.fromkeys(RUNTIMES, "")


def old_layout(**changes):
    """The layout of main before the move (rc.10 on), with CHANGES."""
    paths = {
        "CSHARP_ROOT": ".", "SOLUTION": "Nino.slnx", "CORE": "src/Nino.Core", "GENERATOR": "src/Nino.Generator",
        "CODEGEN": "src/Nino.Codegen", "UNITY_PROJECT": "src/Nino.Unity",
        "UNITY_PACKAGE": "src/Nino.Unity/Packages/com.jasonxudeveloper.nino",
        "UNITY_BENCHMARK": "src/Nino.Unity/Assets/Test/Runtime/Burst/BurstBenchmark.cs",
        "BENCHMARKS": "benchmarks", "BENCH": "benchmarks/Nino.Benchmark", "BENCH_V3": "",
        "GENERATOR_BENCH": "benchmarks/Nino.GeneratorBenchmark", "MEMBERS": "benchmarks/members",
        "BENCH_REPORTS": "benchmarks/reports", "BENCH_COMPARE": "benchmarks/compare",
        "SHARDS": "benchmarks/shards.json", **RUNTIMES,
    }
    paths.update(changes)
    return paths


def moved(names):
    """NAMES (a tree's files) as #273 moves them: the .NET projects, their tools and props into runtimes/csharp."""
    stays_out = ("tests/conformance/", "tools/agents.sh")
    goes = ("src/", "tests/", "benchmarks/", "tools/", "Nino.slnx", "Directory.", "global.json", "nuget.config")
    return [f"runtimes/csharp/{n}" if n.startswith(goes) and not n.startswith(stays_out) else n for n in names]


def ls_tree(ref):
    """-> the file names of REF, or None when this clone does not have it."""
    out = subprocess.run(["git", "-C", REPO, "ls-tree", "-r", "--name-only", ref], capture_output=True, text=True)
    return out.stdout.splitlines() if out.returncode == 0 and out.stdout else None


class Tags(unittest.TestCase):
    """Releases from either side of each move report.yml must still measure."""

    def check(self, ref, expected):
        names = ls_tree(ref)
        if names is None:
            self.skipTest(f"{ref} is not in this clone")
        self.assertEqual(layout.resolve(layout.listing_exists(names)), expected)

    def test_rc7(self):
        # The benchmark project in src/, v3 in a project of its own, no runtimes outside .NET, no Unity benchmark.
        self.check("refs/tags/v4.0.0-rc.7", old_layout(
            SOLUTION="src/Nino.sln", CODEGEN="", UNITY_BENCHMARK="", BENCH="src/Nino.Benchmark",
            BENCH_V3="benchmarks/v3", GENERATOR_BENCH="", BENCH_REPORTS="", BENCH_COMPARE="", SHARDS="",
            **NO_RUNTIMES))

    def test_rc8(self):
        # The report scripts lay flat in benchmarks/ then.
        self.check("refs/tags/v4.0.0-rc.8", old_layout(BENCH_REPORTS="", BENCH_COMPARE=""))

    def test_rc10(self):
        self.check("refs/tags/v4.0.0-rc.10", old_layout())

    def test_this_commit(self):
        # main, on either side of the move.
        names = ls_tree("HEAD") or []
        self.check("HEAD", Moved.NEW if "runtimes/csharp/src/Nino.Core/Nino.Core.csproj" in names else old_layout())


class Moved(unittest.TestCase):
    """The layout after #273: everything .NET under runtimes/csharp, the runtimes and the corpus where they were."""

    NEW = {
        "CSHARP_ROOT": "runtimes/csharp", "SOLUTION": "runtimes/csharp/Nino.slnx",
        "CORE": "runtimes/csharp/src/Nino.Core", "GENERATOR": "runtimes/csharp/src/Nino.Generator",
        "CODEGEN": "runtimes/csharp/src/Nino.Codegen", "UNITY_PROJECT": "runtimes/csharp/src/Nino.Unity",
        "UNITY_PACKAGE": "runtimes/csharp/src/Nino.Unity/Packages/com.jasonxudeveloper.nino",
        "UNITY_BENCHMARK": "runtimes/csharp/src/Nino.Unity/Assets/Test/Runtime/Burst/BurstBenchmark.cs",
        "BENCHMARKS": "runtimes/csharp/benchmarks", "BENCH": "runtimes/csharp/benchmarks/Nino.Benchmark",
        "BENCH_V3": "", "GENERATOR_BENCH": "runtimes/csharp/benchmarks/Nino.GeneratorBenchmark",
        "MEMBERS": "runtimes/csharp/benchmarks/members", "BENCH_REPORTS": "runtimes/csharp/benchmarks/reports",
        "BENCH_COMPARE": "runtimes/csharp/benchmarks/compare", "SHARDS": "runtimes/csharp/benchmarks/shards.json",
        **RUNTIMES,
    }

    SYNTHETIC = [
        "AGENTS.md", "tools/agents.sh", "tests/conformance/corpus/manifest.json",
        "runtimes/csharp/Nino.slnx", "runtimes/csharp/Directory.Build.props", "runtimes/csharp/global.json",
        "runtimes/csharp/src/Nino.Core/Nino.Core.csproj", "runtimes/csharp/src/Nino.Generator/Nino.Generator.csproj",
        "runtimes/csharp/src/Nino.Codegen/Nino.Codegen.csproj",
        "runtimes/csharp/src/Nino.Unity/Packages/com.jasonxudeveloper.nino/package.json",
        "runtimes/csharp/src/Nino.Unity/Assets/Test/Runtime/Burst/BurstBenchmark.cs",
        "runtimes/csharp/benchmarks/Nino.Benchmark/Nino.Benchmark.csproj",
        "runtimes/csharp/benchmarks/Nino.GeneratorBenchmark/Nino.GeneratorBenchmark.csproj",
        "runtimes/csharp/benchmarks/members/run.sh", "runtimes/csharp/benchmarks/reports/bdn.py",
        "runtimes/csharp/benchmarks/compare/ab.py", "runtimes/csharp/benchmarks/shards.json",
        "runtimes/go/bench/go.mod", "runtimes/rust/bench/Cargo.toml", "runtimes/typescript/bench/Bench.csproj",
    ]

    def test_synthetic(self):
        self.assertEqual(layout.resolve(layout.listing_exists(self.SYNTHETIC)), self.NEW)

    def test_this_commit(self):
        # HEAD in the layout since the move (#273), or a commit before it moved as #273 moves it.
        names = ls_tree("HEAD")
        if names is None:
            self.skipTest("not a git checkout")
        if "src/Nino.Core/Nino.Core.csproj" in names:
            names = moved(names)
        self.assertEqual(layout.resolve(layout.listing_exists(names)), self.NEW)

    def test_reports_kept_at_the_root(self):
        # Should the report scripts stay in the root's benchmarks/ (the plan's other option), they are found there.
        names = [n for n in self.SYNTHETIC if "/benchmarks/reports/" not in n] + ["benchmarks/reports/bdn.py"]
        self.assertEqual(layout.resolve(layout.listing_exists(names))["BENCH_REPORTS"], "benchmarks/reports")

    def test_pathspec(self):
        self.assertEqual(layout.pathspec("CORE"), ["runtimes/csharp/src/Nino.Core", "src/Nino.Core"])
        self.assertEqual(layout.pathspec("GO_BENCH"), ["runtimes/go/bench"])


class Cli(unittest.TestCase):
    def run_main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = layout.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_checkout_directory(self):
        with tempfile.TemporaryDirectory() as tree:
            for name in Moved.SYNTHETIC:
                os.makedirs(os.path.dirname(os.path.join(tree, name)), exist_ok=True)
                open(os.path.join(tree, name), "w").close()
            code, out, _ = self.run_main(tree, "--json")
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(out), Moved.NEW)
            self.assertEqual(self.run_main(tree, "--get", "BENCH")[:2], (0, "runtimes/csharp/benchmarks/Nino.Benchmark\n"))
            # A thing the tree lacks fails --get, so `x=$(layout.py --get ...)` stops a step under set -e.
            code, out, err = self.run_main(tree, "--get", "BENCH_V3")
            self.assertEqual((code, out), (1, ""))
            self.assertIn("BENCH_V3", err)

    def test_listing_with_prefix(self):
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as f:
            f.write("\n".join(Moved.SYNTHETIC))
        try:
            code, out, _ = self.run_main("--list", f.name, "--prefix", "TAG_")
        finally:
            os.unlink(f.name)
        self.assertEqual(code, 0)
        self.assertIn("TAG_CSHARP_ROOT=runtimes/csharp\n", out)
        self.assertIn("TAG_BENCH_V3=\n", out)
        self.assertEqual(len(out.splitlines()), len(layout.KEYS))

    def test_pathspec_cli(self):
        self.assertEqual(self.run_main("--pathspec", "CORE", "GENERATOR")[1],
                         "runtimes/csharp/src/Nino.Core src/Nino.Core runtimes/csharp/src/Nino.Generator src/Nino.Generator\n")


if __name__ == "__main__":
    unittest.main()
