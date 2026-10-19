#!/usr/bin/env python3
"""Compares two BenchmarkDotNet github reports of the same benchmarks, such as two commits run on one machine.

usage: ab.py base.md head.md > table.md

One row per Nino benchmark in head: its mean in base and head, head against base (above 1.00x is faster), and head
against MemoryPack's case of the same name in head. Times may be in any unit BenchmarkDotNet prints.
"""
import re
import sys

UNITS = {"ns": 1, "μs": 1e3, "µs": 1e3, "us": 1e3, "ms": 1e6, "s": 1e9}


def means(path):
    """-> {(type, method): mean in ns} from the first table with a Method and a Mean column."""
    lines = open(path, encoding="utf-8").read().splitlines()
    out, header = {}, None
    for line in lines:
        if not line.startswith("|"):
            if out:
                break
            header = None
            continue
        cells = [cell.strip().strip("*") for cell in line.strip().strip("|").split("|")]
        if header is None:
            header = cells if "Method" in cells and "Mean" in cells else []
            continue
        if not header or set(line) <= set("|-: ") or len(cells) != len(header):
            continue
        row = dict(zip(header, cells))
        match = re.match(r"([\d,.]+)\s*(\S+)", row["Mean"])
        if row["Method"] and match and match.group(2) in UNITS:
            out[(row.get("Type", ""), row["Method"])] = float(match.group(1).replace(",", "")) * UNITS[match.group(2)]
    return out


def show(ns):
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("μs", 1e3)):
        if ns >= scale:
            return f"{ns / scale:.2f} {unit}"
    return f"{ns:.2f} ns"


def ratio(slow, fast):
    if fast < 1.0 or slow < 1.0:  # too fast to tell apart (readme.py's FLOOR_NS)
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
