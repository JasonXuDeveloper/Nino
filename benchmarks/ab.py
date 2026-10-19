#!/usr/bin/env python3
"""Compares two BenchmarkDotNet github reports of the same benchmarks, such as two commits run on one machine.

usage: ab.py base.md head.md > table.md

One row per Nino benchmark in head: its mean in base and head, head against base (above 1.00x is faster), and head
against MemoryPack's case of the same name in head. Times may be in any unit BenchmarkDotNet prints.
"""
import sys

from bdn import FLOOR_NS, mean_ns, rows, show


def means(path):
    """-> {(type, method): mean in ns} from the first table with a Method and a Mean column."""
    out = {}
    for row in rows(path)[1]:
        mean = mean_ns(row["Mean"])
        if row["Method"] and mean is not None:
            out[(row.get("Type", ""), row["Method"])] = mean
    return out


def ratio(slow, fast):
    if fast < FLOOR_NS or slow < FLOOR_NS:  # too fast to tell apart
        return "-"
    return f"{slow / fast:.2f}x"


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    base, head = means(sys.argv[1]), means(sys.argv[2])
    print("| Type | Method | base | head | head vs base | head vs MemoryPack |")
    print("|---|---|---:|---:|---:|---:|")
    for (kind, method), now in head.items():
        if not method.startswith("Nino"):
            continue
        before = base.get((kind, method))
        rival = head.get((kind, "MemoryPack" + method[len("Nino"):]))
        print(f"| {kind} | {method} | {show(before) if before else '-'} | {show(now)} | "
              f"{ratio(before, now) if before else '-'} | {ratio(rival, now) if rival else '-'} |")


if __name__ == "__main__":
    main()
