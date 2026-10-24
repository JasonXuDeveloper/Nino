"""Reads BenchmarkDotNet's GitHub Markdown reports (*-report-github.md) for the scripts that compare them: readme.py,
ab.py, merge_reports.py, compact_report.py, compare_v3.py and members/table.py. One parser, one unit table and one
floor, so the scripts cannot drift apart in how they read a report or which times they call too fast to measure."""
import re

UNITS_NS = {"ns": 1.0, "μs": 1e3, "µs": 1e3, "us": 1e3, "ms": 1e6, "s": 1e9}
# About three clock cycles. Below it, a case cannot be told apart from another: a struct read of 40 bytes measured
# anywhere from 0.05 to 0.8 ns in Nino and MemoryPack alike, as BenchmarkDotNet subtracts its empty-method overhead.
# The docs site (nino.docs src/lib/benchmarks.ts) uses the same floor.
FLOOR_NS = 1


def cells(line):
    """The cells of a Markdown table line, stripped."""
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def class_name(path):
    """The benchmark class a report of one class is for, from its file name as BenchmarkDotNet writes it
    (Nino.Benchmark.SimpleTest-report-github.md -> SimpleTest); None for a joined summary of several classes
    (BenchmarkRun-joined-...) or a renamed copy. BenchmarkDotNet leaves the Type column out of a report of one class."""
    name = re.sub(r"^.*[\\/]", "", path)
    if not name.endswith("-report-github.md") or name.startswith("BenchmarkRun-"):
        return None
    return name[:-len("-report-github.md")].rsplit(".", 1)[-1] or None


def span(lines):
    """-> (first, end): the lines of the first table (lines that start with "|"), or None without one."""
    first = next((i for i, line in enumerate(lines) if line.startswith("|")), None)
    if first is None:
        return None
    return first, next((i for i in range(first, len(lines)) if not lines[i].startswith("|")), len(lines))


def rows(path):
    """-> (the lines before the table, [{column: cell}]) for the first table with a Method and a Mean column, or
    ([], []) without one. Cells lose BenchmarkDotNet's bold marks; separator rows, blank group rows and rows of
    another width are left out."""
    lines = open(path, encoding="utf-8").read().splitlines()
    first = next((i for i, line in enumerate(lines)
                  if line.startswith("|") and {"Method", "Mean"} <= set(cells(line))), None)
    if first is None:
        return [], []
    header, out = cells(lines[first]), []
    for line in lines[first + 1:]:
        if not line.startswith("|"):
            break
        row = [cell.strip("*") for cell in cells(line)]
        if len(row) == len(header) and not set("".join(row)) <= set("-: "):
            out.append(dict(zip(header, row)))
    return lines[:first], out


def mean_ns(cell):
    """A time as BenchmarkDotNet prints it ("1,234.5 ns", "17.0 μs"), in ns; None for anything else ("NA")."""
    match = re.match(r"([\d,.]+)\s*(\S+)", cell)
    if not match or match.group(2) not in UNITS_NS:
        return None
    return float(match.group(1).replace(",", "")) * UNITS_NS[match.group(2)]


def show(ns):
    """A time for a comparison table: 1.23 ms, 17.04 μs, 151.20 ns."""
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("μs", 1e3)):
        if ns >= scale:
            return f"{ns / scale:,.2f} {unit}"
    return f"{ns:,.2f} ns"
