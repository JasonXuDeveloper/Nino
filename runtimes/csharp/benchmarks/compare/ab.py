#!/usr/bin/env python3
"""Compares two BenchmarkDotNet github reports of the same benchmarks, such as two commits run on one machine.

usage: ab.py [base.md] head.md > table.md

One row per Nino benchmark in head: its mean in base and head, head against base (above 1.00x is faster), and head
against MemoryPack's case of the same name in head. Times may be in any unit BenchmarkDotNet prints. Without base.md
the table has no base columns: never pass head as its own base, which reads as a comparison that was not made.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "reports"))
from bdn import FLOOR_NS, mean_ns, rows, show  # noqa: E402 (benchmarks/reports/bdn.py)


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
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    base = means(sys.argv[1]) if len(sys.argv) == 3 else None
    head = means(sys.argv[-1])
    if base is None:
        print("| Type | Method | head | head vs MemoryPack |")
        print("|---|---|---:|---:|")
    else:
        print("| Type | Method | base | head | head vs base | head vs MemoryPack |")
        print("|---|---|---:|---:|---:|---:|")
    for (kind, method), now in head.items():
        if not method.startswith("Nino"):
            continue
        rival = head.get((kind, "MemoryPack" + method[len("Nino"):]))
        against_rival = ratio(rival, now) if rival else "-"
        if base is None:
            print(f"| {kind} | {method} | {show(now)} | {against_rival} |")
            continue
        before = base.get((kind, method))
        print(f"| {kind} | {method} | {show(before) if before else '-'} | {show(now)} | "
              f"{ratio(before, now) if before else '-'} | {against_rival} |")


if __name__ == "__main__":
    main()
