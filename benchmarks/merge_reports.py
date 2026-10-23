#!/usr/bin/env python3
"""Merges BenchmarkDotNet github reports of jobs that ran side by side into one report.

usage: merge_reports.py report.md [report.md ...] > merged.md

report.yml runs the benchmark classes in parallel jobs (each class with its Nino and MemoryPack cases on one
runner) and publishes one table, as one run would write it: the first report's environment block, then one table
with the rows of every report in the order given. The columns are the union of the reports' (BenchmarkDotNet leaves
out a column, such as Gen1, that is empty in its run); a cell a report has no column for is "-". Program.cs prints
every time in ns and every size in B, so cells merge as they are. After the table, each report's runner is named,
by the classes of its Type column (a joined summary of two or more classes) or, for a report of one class (the v3
SimpleTest, run in halves), by its first and last case.
"""
import re
import sys

from bdn import cells


def read(path):
    """-> (text before the table, header cells, alignment cells, rows of cells, CPU line)."""
    lines = open(path, encoding="utf-8").read().splitlines()
    first = next((i for i, line in enumerate(lines) if line.startswith("|")), None)
    if first is None:
        sys.exit(f"{path}: no table")
    end = next((i for i in range(first, len(lines)) if not lines[i].startswith("|")), len(lines))

    header, align = cells(lines[first]), cells(lines[first + 1])
    cpu = next((line.strip() for line in lines[:first] if re.search(r"\bCPU\b|cores", line)), "unknown CPU")
    return "\n".join(lines[:first]), header, align, [cells(line) for line in lines[first + 2:end]], cpu


def merge(paths):
    reports = [read(path) for path in paths]
    columns, align = [], {}
    for _, header, alignment, _, _ in reports:
        for i, name in enumerate(header):
            if name not in columns:
                # After the column that comes before it in this report, so Gen0, Gen1, Gen2 stay together.
                at = columns.index(header[i - 1]) + 1 if i > 0 and header[i - 1] in columns else len(columns)
                columns.insert(at, name)
            align.setdefault(name, alignment[i])

    rows = []
    for _, header, _, report_rows, _ in reports:
        if rows:
            rows.append([""] * len(columns))  # the blank row BenchmarkDotNet puts between groups
        for row in report_rows:
            by_name = dict(zip(header, row))
            blank = all(cell == "" for cell in row)
            rows.append([by_name.get(name, "" if blank else "-") for name in columns])

    widths = [max(len(name), *(len(row[i]) for row in rows)) for i, name in enumerate(columns)]

    def line(cells, right):
        return "| " + " | ".join(
            cell.rjust(widths[i]) if right[i] else cell.ljust(widths[i]) for i, cell in enumerate(cells)) + " |"

    right = [align[name].endswith(":") for name in columns]
    out = [reports[0][0], line(columns, [False] * len(columns))]
    out.append("|" + "|".join(("-" * (widths[i] + 1) + ":") if right[i] else ("-" * (widths[i] + 2))
                              for i in range(len(columns))) + "|")
    out += [line(row, right) for row in rows]
    out.append("")
    out.append("The cases ran in parallel on several runners (each case's Nino and MemoryPack runs on one):")
    out.append("")
    for _, header, _, report_rows, cpu in reports:
        if "Type" in header:
            at = header.index("Type")
            names = ", ".join(sorted({row[at] for row in report_rows if len(row) > at and row[at]}))
        else:
            at = header.index("Method")
            methods = [row[at] for row in report_rows if len(row) > at and row[at]]
            names = f"{methods[0]} to {methods[-1]}" if methods else "no cases"
        out.append(f"- {names}: {cpu}")
    return "\n".join(out) + "\n"


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    sys.stdout.write(merge(sys.argv[1:]))
