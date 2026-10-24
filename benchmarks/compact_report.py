#!/usr/bin/env python3
"""Shortens a merged BenchmarkDotNet report (merge_reports.py) for a release body.

usage: compact_report.py merged.md > compact.md

GitHub caps a release body at 125,000 characters, and the release notes of a major version list hundreds of commits.
The body keeps what the docs site (nino.docs scripts/sync-benchmarks.ts) and readers use: one table per benchmark
class (its name as a heading instead of a Type column on every row) with Method, Mean, Ratio, Payload and
Allocated, times as bdn.show prints them (151.20 ns, 17.04 μs), cells without BenchmarkDotNet's column padding. The
environment block before the table and the runner list after it are kept as they are. The full report (Median, Min,
Max, GC counts) is attached to the release. A report of one class, which BenchmarkDotNet writes without a Type
column, is named after its file (Nino.Benchmark.SimpleTest-report-github.md); a Type cell of "-" (merge_reports.py's
row of such a report, renamed) stops the script rather than print a table titled "-".
"""
import sys

from bdn import cells, class_name, mean_ns, show, span

COLUMNS = ("Method", "Mean", "Ratio", "Payload", "Allocated")


def compact(text, type_name=None):
    """type_name: the class of a report without a Type column (a report of one class)."""
    lines = text.splitlines()
    table = span(lines)
    if table is None:
        sys.exit("no table")
    first, end = table
    header = cells(lines[first])
    if "Method" not in header or "Mean" not in header:
        sys.exit("the table needs Method and Mean columns")
    if "Type" not in header and not type_name:
        sys.exit("the table has no Type column, and the file name does not name the class "
                 "(<Namespace>.<Class>-report-github.md, as BenchmarkDotNet writes a report of one class)")
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
        name = row.get("Type") or type_name
        if not name or name == "-":
            sys.exit(f"{row['Method']}: no class in the Type column (a report of one class merged under a name "
                     "that does not give it: keep BenchmarkDotNet's file name)")
        groups.setdefault(name, []).append("| " + " | ".join(row.get(name, "-") or "-" for name in keep) + " |")

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
    sys.stdout.write(compact(open(sys.argv[1], encoding="utf-8").read(), class_name(sys.argv[1])))
