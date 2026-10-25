#!/usr/bin/env python3
"""Checks that the shards of each unit test configuration (ci.yml, unit-tests) together ran the whole suite.

    python3 .github/scripts/test_shards.py <dir> [--shards a,b,c]

<dir> holds one directory per shard, as actions/download-artifact writes them: test-results-unit-<config>-<shard>,
each with the shard's unit.trx, and on the first shard suite.txt, the output of `dotnet test --list-tests` without a
filter. For each configuration, the names of the tests the shards ran (a multiset: names repeat across classes) must
be exactly the names the suite lists: a test no shard selected, or one that two shards both ran, fails the check.

A missing shard (its upload failed on a storage timeout, which the upload step tolerates) is a warning, not a
failure: each shard has already checked that it ran every test its filter lists, and the filters are complements of
each other.
"""

import argparse
import collections
import pathlib
import sys
import xml.etree.ElementTree as ET

TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
PREFIX = "test-results-unit-"


def ran(directory: pathlib.Path) -> collections.Counter:
    names = collections.Counter()
    for trx in directory.rglob("*.trx"):
        for result in ET.parse(trx).getroot().iter(TRX + "UnitTestResult"):
            names[result.get("testName")] += 1
    return names


def listed(path: pathlib.Path) -> collections.Counter:
    # `dotnet test --list-tests` prints a header, then each test indented by four spaces.
    lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    return collections.Counter(line[4:].rstrip() for line in lines if line.startswith("    "))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("dir", type=pathlib.Path)
    parser.add_argument("--shards", default="a,b,c")
    args = parser.parse_args()
    shards = args.shards.split(",")

    configs: dict[str, dict[str, pathlib.Path]] = collections.defaultdict(dict)
    if args.dir.is_dir():
        for d in sorted(args.dir.iterdir()):
            if d.is_dir() and d.name.startswith(PREFIX):
                config, _, shard = d.name[len(PREFIX):].rpartition("-")
                configs[config][shard] = d
    if not configs:
        print(f"::warning::no unit test results in {args.dir}: the shards' own checks stand")
        return 0

    failed = False
    for config, found in sorted(configs.items()):
        missing = [s for s in shards if s not in found]
        if missing:
            print(f"::warning::{config}: no results from shard(s) {', '.join(missing)}: the shards' own checks stand")
            continue
        suite_files = list(found[shards[0]].rglob("suite.txt"))
        if not suite_files:
            print(f"::error::{config}: shard {shards[0]} has no suite.txt (the list of the whole suite)")
            failed = True
            continue
        expected = listed(suite_files[0])
        actual = collections.Counter()
        for s in shards:
            actual += ran(found[s])
        if not expected:
            print(f"::error::{config}: the suite lists no tests")
            failed = True
        elif actual != expected:
            failed = True
            for name, n in sorted((expected - actual).items()):
                print(f"::error::{config}: not run: {name}" + (f" (x{n})" if n > 1 else ""))
            for name, n in sorted((actual - expected).items()):
                print(f"::error::{config}: run more often than listed: {name}" + (f" (x{n})" if n > 1 else ""))
        else:
            print(f"{config}: {sum(actual.values())} tests over shards {', '.join(shards)}, the whole suite")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
