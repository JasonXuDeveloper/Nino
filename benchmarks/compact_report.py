#!/usr/bin/env python3
"""Shortens a merged BenchmarkDotNet report (merge_reports.py) for a release body.

usage: compact_report.py merged.md > compact.md

GitHub caps a release body at 125,000 characters, and the release notes of a major version list hundreds of commits.
The body keeps what the docs site (nino.docs scripts/sync-benchmarks.ts) and readers use: one table per benchmark
class (its name as a heading instead of a Type column on every row) with Method, Mean, Ratio, Payload and
Allocated, times as bdn.show prints them (151.20 ns, 17.04 μs), cells without BenchmarkDotNet's column padding. The
environment block before the table and the runner list after it are kept as they are. The full report (Median, Min,
Max, GC counts) is attached to the release.
"""
import sys

from bdn import cells, mean_ns, show

COLUMNS = ("Method", "Mean", "Ratio", "Payload", "Allocated")


def compact(text):
    lines = text.splitlines()
    first = next((i for i, line in enumerate(lines) if line.startswith("|")), None)
    if first is None:
        sys.exit("no table")
    end = next((i for i in range(first, len(lines)) if not lines[i].startswith("|")), len(lines))
    header = cells(lines[first])
    if "Type" not in header or "Method" not in header or "Mean" not in header:
        sys.exit("the table needs Type, Method and Mean columns (merge_reports.py's output)")
    keep = [name for name in COLUMNS if name in header]
    right = ["-:" if name != "Method" else "-" for name in keep]
    blank = "|" + " |" * len(keep)

    groups = {}  # Type -> rows, in the order the report lists them
    for line in lines[first + 2:end]:
        row = dict(zip(header, (cell.strip("*") for cell in cells(line))))
        if not row.get("Method"):
            if groups:  # BenchmarkDotNet's blank row between groups, after which Ratio restarts at 1.00
                rows = groups[list(groups)[-1]]
                if rows and rows[-1] != blank:
                    rows.append(blank)
            continue
        ns = mean_ns(row["Mean"])
        if ns is not None:
            row["Mean"] = show(ns)
        groups.setdefault(row["Type"], []).append("| " + " | ".join(row.get(name, "-") or "-" for name in keep) + " |")

    out = lines[:first]
    for name, rows in groups.items():
        while rows and rows[-1] == blank:
            rows.pop()
        out += [f"### {name}", "", "| " + " | ".join(keep) + " |", "|" + "|".join(right) + "|", *rows, ""]
    out += lines[end:]
    return "\n".join(out).rstrip("\n") + "\n"


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    sys.stdout.write(compact(open(sys.argv[1], encoding="utf-8").read()))
