#!/usr/bin/env python3
"""Where a tree of this repository keeps its .NET projects, its benchmarks and the runtimes' benchmarks.

    python3 .github/scripts/layout.py [DIR]                        # a checkout (default: the current directory)
    python3 .github/scripts/layout.py --ref REF [-C REPO]          # a commit, read with git ls-tree
    python3 .github/scripts/layout.py --list FILE                  # `git ls-tree -r --name-only` output (- for stdin)
    ... [--prefix P | --json | --get KEY]
    python3 .github/scripts/layout.py --pathspec KEY ...           # KEY's path in every layout, for git log -- ...

Workflows that run one tree's code with another tree's scripts (report.yml measures a release tag with its own
scripts, ab-bench.yml and the test branch's runtime-ab.yml and dotnet-ab.yml build two commits) ask this script
where each tree keeps a thing instead of naming the path: the .NET projects moved from the root into runtimes/csharp
(#273), the benchmark project from src/ into benchmarks/ (v4.0.0-rc.8), and a tag or commit from either side of a
move must still build.

Prints KEY=value lines (for $GITHUB_ENV or $GITHUB_OUTPUT, each key after --prefix), one JSON object (--json, for a
job output read with fromJSON), or one value (--get, which fails when the tree lacks it). A value is a path relative
to the tree's root, or empty when the tree has no such thing (a tag older than the Unity benchmark, say). The keys:
KEYS below.
"""

import argparse
import json
import os
import subprocess
import sys

# The .NET root of each layout, newest first: the tree's .NET projects are under the first whose Nino.Core exists.
ROOTS = ("runtimes/csharp", ".")

# Each key's candidate paths, the first that exists wins. "{root}" is the tree's .NET root; a path without it is the
# same in every layout. Older layouts come after the current one.
KEYS = {
    "CSHARP_ROOT": ("{root}",),
    # v4.0.0-rc.7 and older: src/Nino.sln.
    "SOLUTION": ("{root}/Nino.slnx", "{root}/src/Nino.sln"),
    "CORE": ("{root}/src/Nino.Core",),
    "GENERATOR": ("{root}/src/Nino.Generator",),
    "CODEGEN": ("{root}/src/Nino.Codegen",),
    "UNITY_PROJECT": ("{root}/src/Nino.Unity",),
    "UNITY_PACKAGE": ("{root}/src/Nino.Unity/Packages/com.jasonxudeveloper.nino",),
    "UNITY_BENCHMARK": ("{root}/src/Nino.Unity/Assets/Test/Runtime/Burst/BurstBenchmark.cs",),
    # The .NET benchmarks; the root's benchmarks/ in case the scripts stay there when the projects move.
    "BENCHMARKS": ("{root}/benchmarks", "benchmarks"),
    # v4.0.0-rc.7 and older: src/Nino.Benchmark.
    "BENCH": ("{root}/benchmarks/Nino.Benchmark", "{root}/src/Nino.Benchmark"),
    # The v3 3.3.13 project of its own (rc.7 to rc.8's development); since, the benchmark project built with
    # NINO_BENCH_V3=true.
    "BENCH_V3": ("{root}/benchmarks/v3",),
    # Only benchmarks/Nino.GeneratorBenchmark: rc.7's src/Nino.GeneratorBenchmark predates the harness the build
    # section runs (report.yml), so a tree without this one has no build section.
    "GENERATOR_BENCH": ("{root}/benchmarks/Nino.GeneratorBenchmark",),
    "MEMBERS": ("{root}/benchmarks/members", "benchmarks/members"),
    "BENCH_REPORTS": ("{root}/benchmarks/reports", "benchmarks/reports"),
    "BENCH_COMPARE": ("{root}/benchmarks/compare", "benchmarks/compare"),
    "SHARDS": ("{root}/benchmarks/shards.json", "benchmarks/shards.json"),
    # The case manifest BenchGen writes (#335): the cases, their quick subset and the libraries that break on them.
    "BENCH_CASES": ("{root}/benchmarks/bench-cases.json",),
    "GO_RUNTIME": ("runtimes/go",),
    "GO_BENCH": ("runtimes/go/bench",),
    "RUST_RUNTIME": ("runtimes/rust",),
    "RUST_BENCH": ("runtimes/rust/bench",),
    "TYPESCRIPT_RUNTIME": ("runtimes/typescript",),
    "TYPESCRIPT_BENCH": ("runtimes/typescript/bench",),
}


def join(root, template):
    path = template.replace("{root}", root)
    return path[2:] if path.startswith("./") else path


def listing_exists(names):
    """-> exists(path) over the file names of `git ls-tree -r --name-only`, directories included."""
    paths = {"."}
    for name in names:
        name = name.strip()
        while name:
            if name in paths:
                break
            paths.add(name)
            name = os.path.dirname(name)
    return lambda path: path in paths


def ref_exists(ref, repo):
    out = subprocess.run(["git", "-C", repo, "ls-tree", "-r", "--name-only", ref],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"layout.py: cannot list {ref}: {out.stderr.strip()}")
    return listing_exists(out.stdout.splitlines())


def dir_exists(directory):
    if not os.path.isdir(directory):
        sys.exit(f"layout.py: {directory} is not a directory")
    return lambda path: os.path.exists(os.path.join(directory, path))


def resolve(exists):
    """-> {key: path or ""} of the tree exists() describes."""
    root = next((r for r in ROOTS if exists(join(r, "{root}/src/Nino.Core"))), ".")
    return {key: next((join(root, t) for t in templates if exists(join(root, t))), "")
            for key, templates in KEYS.items()}


def pathspec(key):
    """-> KEY's paths in every layout, distinct, newest first."""
    seen = []
    for root in ROOTS:
        for template in KEYS[key]:
            path = join(root, template)
            if path not in seen:
                seen.append(path)
    return seen


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    source = parser.add_mutually_exclusive_group()
    source.add_argument("dir", nargs="?", default=".", help="a checkout of the tree (default: .)")
    source.add_argument("--ref", help="a commit, tag or tree of the repository (git ls-tree)")
    source.add_argument("--list", help="the output of git ls-tree -r --name-only, or - for stdin")
    source.add_argument("--pathspec", nargs="+", metavar="KEY", choices=KEYS, help="KEY's paths in every layout")
    parser.add_argument("-C", dest="repo", default=".", help="the repository --ref reads (default: .)")
    out = parser.add_mutually_exclusive_group()
    out.add_argument("--prefix", default="", help="a prefix for every key printed")
    out.add_argument("--json", action="store_true", help="one JSON object")
    out.add_argument("--get", choices=KEYS, help="one key's value; fails when the tree has none")
    args = parser.parse_args(argv)

    if args.pathspec:
        print(" ".join(p for key in args.pathspec for p in pathspec(key)))
        return 0
    if args.ref:
        exists = ref_exists(args.ref, args.repo)
    elif args.list:
        with (sys.stdin if args.list == "-" else open(args.list, encoding="utf-8")) as f:
            exists = listing_exists(f.read().splitlines())
    else:
        exists = dir_exists(args.dir)
    layout = resolve(exists)
    if args.get:
        if not layout[args.get]:
            print(f"layout.py: the tree has no {args.get} ({' or '.join(pathspec(args.get))})", file=sys.stderr)
            return 1
        print(layout[args.get])
    elif args.json:
        print(json.dumps(layout, separators=(",", ":")))
    else:
        for key, value in layout.items():
            print(f"{args.prefix}{key}={value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
