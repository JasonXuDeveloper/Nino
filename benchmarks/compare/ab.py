#!/usr/bin/env python3
"""Compares two BenchmarkDotNet github reports of the same benchmarks, such as two commits run on one machine.

usage: ab.py [base.md] head.md > table.md

One row per Nino benchmark in head: its mean in base and head, head against base (above 1.00x is faster), and head
against MemoryPack's case of the same name in head, and against each other library of RIVALS that head ran (a column
only when it did). Times may be in any unit BenchmarkDotNet prints. Without base.md the table has no base columns:
never pass head as its own base, which reads as a comparison that was not made.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "reports"))
from bdn import FLOOR_NS, mean_ns, rows, show  # noqa: E402 (benchmarks/reports/bdn.py)

# The libraries compared with Nino, by their method prefix: MemoryPack always, the others when head ran them.
RIVALS = ("MemoryPack", "LuminPack")


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
    rivals = [r for r in RIVALS if r == "MemoryPack" or any(m.startswith(r) for _, m in head)]
    columns = ["Type", "Method"] + (["head"] if base is None else ["base", "head", "head vs base"]) + \
        [f"head vs {r}" for r in rivals]
    print("| " + " | ".join(columns) + " |")
    print("|---|---|" + "---:|" * (len(columns) - 2))
    for (kind, method), now in head.items():
        if not method.startswith("Nino"):
            continue
        against = []
        for r in rivals:
            rival = head.get((kind, r + method[len("Nino"):]))
            against.append(ratio(rival, now) if rival else "-")
        cells = [kind, method]
        if base is None:
            cells.append(show(now))
        else:
            before = base.get((kind, method))
            cells += [show(before) if before else "-", show(now), ratio(before, now) if before else "-"]
        print("| " + " | ".join(cells + against) + " |")


if __name__ == "__main__":
    main()
