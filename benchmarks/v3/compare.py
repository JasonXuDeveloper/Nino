#!/usr/bin/env python3
"""Compare two BenchmarkDotNet *-report-github.md files from SimpleTest: Nino v3 3.3.13 vs Nino v4.

usage: python3 compare.py <v3 report-github.md> <v4 report-github.md>

Prints: case | v3 3.3.13 | v4 | v4 vs v3 | MemoryPack (v4 run) | MemoryPack (v3 run)
  - all times are the BDN "Mean" column, as measured, no derived values;
  - "v4 vs v3" = mean(v4) / mean(v3)  (< 1.00x means v4 is faster);
  - the two MemoryPack columns are the same benchmark in each run, so their ratio shows run-to-run drift.
"""
import re
import sys

UNITS_NS = {"ns": 1.0, "μs": 1e3, "µs": 1e3, "us": 1e3, "ms": 1e6, "s": 1e9}
LIBS = ("Nino", "MemoryPack", "MessagePack")


def parse(path):
    """-> {method: mean_ns}. Uses the first table with a Method and a Mean column."""
    rows, header = {}, None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line.startswith("|"):
                continue
            cells = [c.strip() for c in line.strip("|").split("|")]
            if header is None:
                if "Method" in cells and "Mean" in cells:
                    header = cells
                continue
            if set("".join(cells)) <= set("-: "):
                continue  # separator or blank group row
            row = dict(zip(header, cells))
            method = row.get("Method", "").strip("*")
            if not method:
                continue
            if "Job" in row and "ShortRun" not in row["Job"]:
                continue  # if a report holds several jobs, keep the ShortRun one (the configured job)
            m = re.match(r"([\d,.]+)\s*(\S+)", row["Mean"].strip("*"))
            if not m:
                continue
            rows[method] = float(m.group(1).replace(",", "")) * UNITS_NS[m.group(2)]
    if not rows:
        sys.exit(f"no benchmark rows found in {path}")
    return rows


def fmt(ns):
    if ns is None:
        return "-"
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("μs", 1e3)):
        if ns >= scale:
            return f"{ns / scale:,.2f} {unit}"
    return f"{ns:,.2f} ns"


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
        if lib == "Nino" and case not in cases:
            cases.append(case)
    # serialize first, then deserialize; same order as the SimpleTest source
    cases.sort(key=lambda c: (not c.startswith("Serialize"),))

    header = ["case", "v3 3.3.13", "v4", "v4 vs v3", "MemoryPack (v4 run)", "MemoryPack (v3 run)"]
    table = []
    for case in cases:
        a, b = v3.get("Nino" + case), v4.get("Nino" + case)
        ratio = f"{b / a:.2f}x" if a and b else "-"
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
    # a case measured as 0.00 ns (the JIT removed the work) has no ratio
    drift = [v4["MemoryPack" + c] / v3["MemoryPack" + c] for c in cases
             if v4.get("MemoryPack" + c) and v3.get("MemoryPack" + c)]
    if drift:
        print(f"MemoryPack drift (v4 run / v3 run): min {min(drift):.2f}x, max {max(drift):.2f}x "
              "(same code in both runs; the spread is the noise floor of this comparison).")


if __name__ == "__main__":
    main()
