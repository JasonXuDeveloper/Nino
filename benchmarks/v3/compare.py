#!/usr/bin/env python3
"""Compare two BenchmarkDotNet *-report-github.md files from SimpleTest: Nino v3 3.3.13 vs Nino v4.

usage: python3 compare.py <v3 report-github.md> <v4 report-github.md>

Prints: case | v3 3.3.13 | v4 | v4 vs v3 | MemoryPack (v4 run) | MemoryPack (v3 run)
  - all times are the BDN "Mean" column, as measured, no derived values;
  - "v4 vs v3" = mean(v4) / mean(v3)  (< 1.00x means v4 is faster);
  - the two MemoryPack columns are the same benchmark in each run, so their ratio shows run-to-run drift;
  - a time under FLOOR_NS cannot be told apart from an empty method: v4 under it against a measurable v3 is
    "faster (v4 too fast to measure)", with no ratio; both under it is "on par".
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bdn import FLOOR_NS, mean_ns, rows, show  # noqa: E402 (benchmarks/bdn.py)

LIBS = ("Nino", "MemoryPack", "MessagePack")


def parse(path):
    """-> {method: mean_ns}. Uses the first table with a Method and a Mean column."""
    out = {}
    for row in rows(path)[1]:
        if "Job" in row and "ShortRun" not in row["Job"]:
            continue  # if a report holds several jobs, keep the ShortRun one (the configured job)
        mean = mean_ns(row["Mean"])
        if row["Method"] and mean is not None:
            out[row["Method"]] = mean
    if not out:
        sys.exit(f"no benchmark rows found in {path}")
    return out


def fmt(ns):
    return "-" if ns is None else show(ns)


def split(method):
    for lib in LIBS:
        if method.startswith(lib):
            return lib, method[len(lib):]
    return None, method


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    v3, v4 = parse(sys.argv[1]), parse(sys.argv[2])

    cases = []
    for method in list(v4) + list(v3):
        lib, case = split(method)
        # Only cases v3 has: the v4 report may be the whole suite (report.yml passes the merged report).
        if lib == "Nino" and case not in cases and "Nino" + case in v3:
            cases.append(case)
    # serialize first, then deserialize; same order as the SimpleTest source
    cases.sort(key=lambda c: (not c.startswith("Serialize"),))

    header = ["case", "v3 3.3.13", "v4", "v4 vs v3", "MemoryPack (v4 run)", "MemoryPack (v3 run)"]
    table = []
    for case in cases:
        a, b = v3.get("Nino" + case), v4.get("Nino" + case)
        if a is None or b is None:
            ratio = "-"
        elif b < FLOOR_NS:
            ratio = "on par (both too fast to measure)" if a < FLOOR_NS else "faster (v4 too fast to measure)"
        else:
            ratio = f"{b / a:.2f}x" if a >= FLOOR_NS else "-"
        table.append([case, fmt(a), fmt(b), ratio,
                      fmt(v4.get("MemoryPack" + case)), fmt(v3.get("MemoryPack" + case))])

    widths = [max(len(r[i]) for r in [header] + table) for i in range(len(header))]
    print("| " + " | ".join(h.ljust(w) for h, w in zip(header, widths)) + " |")
    print("|" + "|".join("-" * (w + 1) + ("-" if i == 0 else ":") for i, w in enumerate(widths)) + "|")
    for r in table:
        print("| " + " | ".join(c.ljust(w) if i == 0 else c.rjust(w)
                                for i, (c, w) in enumerate(zip(r, widths))) + " |")
    print()
    print("v4 vs v3 = mean(v4) / mean(v3); below 1.00x means v4 is faster.")
    drift = [v4["MemoryPack" + c] / v3["MemoryPack" + c] for c in cases
             if v4.get("MemoryPack" + c) and v3.get("MemoryPack" + c)]
    if drift:
        print(f"MemoryPack drift (v4 run / v3 run): min {min(drift):.2f}x, max {max(drift):.2f}x "
              "(same code in both runs; the spread is the noise floor of this comparison).")


if __name__ == "__main__":
    main()
