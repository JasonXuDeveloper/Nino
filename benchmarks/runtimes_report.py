#!/usr/bin/env python3
"""The release body's sections for the runtimes outside .NET, from each benchmark's machine-readable output.

usage: runtimes_report.py typescript <node.json> <tag>   (runtimes/typescript/bench/node.ts with NINO_BENCH_JSON)
       runtimes_report.py go <go test -bench output> <tag>   (runtimes/go/bench: go test -bench . -benchmem)
       runtimes_report.py rust <criterion directory> <tag> [<bench output>]
           (runtimes/rust/bench: cargo bench; target/criterion, and its output for the encoded sizes)

Each prints one section: a "## " heading of its own (HEADINGS: never the words "Performance Report", by which the
docs site finds the .NET report), then a table. No table has a Method or Mean column, so nothing that reads the
.NET tables (nino.docs scripts/sync-benchmarks.ts) takes these rows for its own. The numbers are one GitHub runner's,
from one run: compare the serializers within a table, not across tables or releases.
"""
import glob
import json
import os
import re
import sys

HEADINGS = {
    "typescript": "## 🌐 TypeScript runtime benchmark",
    "go": "## 🐹 Go runtime benchmark",
    "rust": "## 🦀 Rust runtime benchmark",
}


def time(ns):
    """A time in the unit that keeps it readable: 151.2 ns, 17.04 μs, 3.21 ms."""
    for unit, size in (("s", 1e9), ("ms", 1e6), ("μs", 1e3)):
        if ns >= size:
            return f"{ns / size:.2f} {unit}"
    return f"{ns:.1f} ns"


def size(n):
    return f"{n / 1024:.1f} KiB" if n >= 1024 else f"{n} B"


def section(lang, tag, summary, intro, table):
    return "\n".join([
        HEADINGS[lang], "", "<details>", f"<summary>{summary}</summary>", "", intro, "", *table, "", "</details>", ""])


def typescript(path, tag):
    data = json.load(open(path, encoding="utf-8"))
    rows = ["| Scenario | Nino serialize | JSON.stringify | Nino deserialize | JSON.parse | Nino size | JSON size |",
            "|---|--:|--:|--:|--:|--:|--:|"]
    for r in data["rows"]:
        ms = lambda x: time(x * 1e6)
        rows.append(f"| {r['name']} | {ms(r['serialize'])} | {ms(r['stringify'])} | {ms(r['deserialize'])} | "
                    f"{ms(r['parse'])} | {size(r['ninoBytes'])} | {size(r['jsonBytes'])} |")
    intro = (f"The code nino-gen generates for `runtimes/typescript/bench` (`Program.cs`'s types), against `JSON`, "
             f"in Node {data.get('node', '')}, on data .NET wrote: time per pass over each scenario's values.")
    return section("typescript", tag, f"Generated TypeScript of {tag} against JSON, in Node", intro, rows)


GO_LINE = re.compile(r"^Benchmark(\w+)/(\S+?)(?:-\d+)?\s+(\d+)\s+([\d.]+) ns/op(.*)$")


def go(path, tag):
    env, rows = {}, []
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.match(r"^(goos|goarch|cpu): (.*)$", line)
        if m:
            env[m.group(1)] = m.group(2)
            continue
        m = GO_LINE.match(line)
        if not m:
            continue
        op, case, _, ns, rest = m.groups()
        mbs = re.search(r"([\d.]+) MB/s", rest)
        bop = re.search(r"(\d+) B/op", rest)
        allocs = re.search(r"(\d+) allocs/op", rest)
        rows.append(f"| {op} | {case} | {time(float(ns))} | {mbs.group(1) + ' MB/s' if mbs else ''} | "
                    f"{size(int(bop.group(1))) if bop else ''} | {allocs.group(1) if allocs else ''} |")
    if not rows:
        sys.exit("no benchmark lines in " + path)
    table = ["| Operation | Case | Time | Throughput | Allocated | Allocations |", "|---|---|--:|--:|--:|--:|", *rows]
    machine = ", ".join(v for v in (env.get("cpu"), env.get("goos") and f"{env['goos']}/{env.get('goarch', '')}") if v)
    intro = ("`runtimes/go/bench`: codecs in the shape nino-gen generates (`Nino`) and the struct tags "
             "(`NinoTags`) "
             f"against encoding/json, MessagePack and gob, on {machine or 'a GitHub runner'}.")
    return section("go", tag, f"The Go runtime of {tag} against encoding/json, MessagePack and gob", intro, table)


RUST_SIZES = re.compile(r"^([\w-]+): (.*\bbytes\b.*)$")


def rust(directory, tag, log=None):
    """One row per group and operation, one column per serializer (Nino first), and each group's encoded sizes
    (which compare.rs prints) from the bench's output when `log` is given."""
    times = {}
    for path in sorted(glob.glob(os.path.join(directory, "**", "new", "benchmark.json"), recursive=True)):
        bench = json.load(open(path, encoding="utf-8"))
        estimates = json.load(open(os.path.join(os.path.dirname(path), "estimates.json"), encoding="utf-8"))
        key = (bench["group_id"], bench["function_id"])
        times.setdefault(key, {})[bench.get("value_str") or ""] = estimates["mean"]["point_estimate"]
    if not times:
        sys.exit("no criterion results under " + directory)
    sizes = {}
    for line in open(log, encoding="utf-8", errors="replace") if log else []:
        m = RUST_SIZES.match(line.strip())
        if m:
            sizes[m.group(1)] = {who: int(n) for n, who in re.findall(r"(\d+) (?:bytes )?([\w-]+)", m.group(2))}
    who = sorted({w for row in times.values() for w in row}, key=lambda w: (w != "nino", w))
    table = ["| Case | Operation | " + " | ".join(who) + " |", "|---|---|" + "--:|" * len(who)]
    groups = list(dict.fromkeys(group for group, _ in times))
    for group in groups:
        for op in ("serialize", "deserialize"):
            row = times.get((group, op))
            if row:
                table.append(f"| {group} | {op} | " + " | ".join(time(row[w]) if w in row else "" for w in who) + " |")
        if group in sizes:
            table.append(f"| {group} | size | " + " | ".join(size(sizes[group][w]) if w in sizes[group] else ""
                                                             for w in who) + " |")
    intro = ("`runtimes/rust/bench` (criterion): codecs in the shape nino-gen generates against "
             "serde_json, bincode, rmp-serde and postcard. Times are criterion's mean estimates; sizes are the "
             "encoded bytes of each case.")
    return section("rust", tag, f"The Rust runtime of {tag} against serde_json, bincode, rmp-serde and postcard",
                   intro, table)


def main():
    if len(sys.argv) not in (4, 5) or sys.argv[1] not in HEADINGS or (len(sys.argv) == 5 and sys.argv[1] != "rust"):
        sys.exit(__doc__)
    lang, source, tag = sys.argv[1:4]
    if lang == "rust":
        print(rust(source, tag, *sys.argv[4:]), end="")
        return
    print({"typescript": typescript, "go": go}[lang](source, tag), end="")


if __name__ == "__main__":
    main()
