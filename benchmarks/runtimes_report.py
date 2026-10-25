#!/usr/bin/env python3
"""The release body's sections for the runtimes outside .NET, from each benchmark's machine-readable output.

usage: runtimes_report.py typescript <node.json> <tag>   (runtimes/typescript/bench/node.ts with NINO_BENCH_JSON:
           tinybench's results)
       runtimes_report.py go <benchstat CSV> <tag>   (runtimes/go/bench: go test -bench . -benchmem -count N, then
           benchstat -format csv)
       runtimes_report.py rust <criterion directory> <tag> [<bench output>]
           (runtimes/rust/bench: cargo bench; target/criterion, and its output for the encoded sizes)

Each table has a row per case and operation and a column per serializer, Nino first, then Protocol Buffers and
MessagePack; each time is a mean or median with its spread (tinybench's relative margin of error, benchstat's 95%
confidence interval, half of criterion's 95% confidence interval).

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


def spread(pct):
    return f" ±{pct:.0f}%" if pct >= 1 else f" ±{pct:.1f}%"


def pivot(cells, order):
    """The serializers of `cells` ({(case, op): {serializer: text}}) in `order`, the others after them."""
    seen = list(dict.fromkeys(w for row in cells.values() for w in row))
    return [w for w in order if w in seen] + [w for w in seen if w not in order]


def grid(cells, who, first="Case"):
    table = [f"| {first} | Operation | " + " | ".join(who) + " |", "|---|---|" + "--:|" * len(who)]
    for (case, op), row in cells.items():
        table.append(f"| {case} | {op} | " + " | ".join(row.get(w, "") for w in who) + " |")
    return table


def typescript(path, tag):
    data = json.load(open(path, encoding="utf-8"))
    if data["rows"] and "stringify" in data["rows"][0]:
        return typescript_json_only(data, tag)
    cells = {}
    for r in data["rows"]:
        hz = f"{r['hz']:,.0f}/s" if r["hz"] >= 10 else f"{r['hz']:.1f}/s"
        cells.setdefault((r["scenario"], r["op"]), {})[r["serializer"]] = \
            f"{time(r['mean'] * 1e6)}{spread(r['rme'])} ({hz})"
        if r["op"] == "serialize":
            cells.setdefault((r["scenario"], "size"), {})[r["serializer"]] = size(r["bytes"])
    scenarios, ops = list(dict.fromkeys(c for c, _ in cells)), ["serialize", "deserialize", "size"]
    cells = {k: cells[k] for k in sorted(cells, key=lambda k: (scenarios.index(k[0]), ops.index(k[1])))}
    who = pivot(cells, ["Nino", "Protobuf", "MessagePack", "JSON"])
    intro = (f"The code nino-gen generates for `runtimes/typescript/bench` (`Program.cs`'s types), against Protocol "
             f"Buffers (protobuf-es), MessagePack (msgpackr) and `JSON` (to and from UTF-8 bytes), in Node {data.get('node', '')}, on data .NET "
             f"wrote, timed by tinybench {data.get('tinybench', '')}: the mean time of a pass over each scenario's "
             "values, ± its relative margin of error, and passes per second.")
    return section("typescript", tag, f"Generated TypeScript of {tag} against Protocol Buffers, MessagePack and JSON, "
                   "in Node", intro, grid(cells, who, "Scenario"))


def typescript_json_only(data, tag):
    """The report of a release whose benchmark compared with JSON only (before tinybench)."""
    rows = ["| Scenario | Nino serialize | JSON.stringify | Nino deserialize | JSON.parse | Nino size | JSON size |",
            "|---|--:|--:|--:|--:|--:|--:|"]
    for r in data["rows"]:
        ms = lambda x: time(x * 1e6)
        rows.append(f"| {r['name']} | {ms(r['serialize'])} | {ms(r['stringify'])} | {ms(r['deserialize'])} | "
                    f"{ms(r['parse'])} | {size(r['ninoBytes'])} | {size(r['jsonBytes'])} |")
    intro = (f"The code nino-gen generates for `runtimes/typescript/bench` (`Program.cs`'s types), against `JSON`, "
             f"in Node {data.get('node', '')}, on data .NET wrote: time per pass over each scenario's values.")
    return section("typescript", tag, f"Generated TypeScript of {tag} against JSON, in Node", intro, rows)


def benchstat_csv(path):
    """benchstat -format csv: {unit: {benchmark: (value, CI text)}}, and the machine lines before the tables."""
    env, units, unit = {}, {}, None
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.match(r"^(goos|goarch|cpu): (.*)$", line)
        if m:
            env[m.group(1)] = m.group(2)
            continue
        cols = line.split(",")
        if len(cols) == 3 and cols[0] == "" and cols[2] == "CI":
            unit = cols[1]
            units[unit] = {}
        elif unit and len(cols) == 3 and cols[0] and cols[0] != "geomean" and cols[1]:
            units[unit][cols[0]] = (float(cols[1]), cols[2])
        elif not line:
            unit = None
    return env, units


def go(path, tag):
    env, units = benchstat_csv(path)
    if not units.get("sec/op"):
        sys.exit("no benchstat sec/op table in " + path)
    cells = {}

    def key(name):
        # Serialize/<case>/<serializer>-<procs>; releases before the cases: Serialize/<serializer>-<procs>.
        parts = re.sub(r"-\d+$", "", name).split("/")
        op = parts[0].lower()
        return (parts[1], op, parts[2]) if len(parts) == 3 else ("SimpleClass", op, parts[-1])

    for name, (sec, ci) in units["sec/op"].items():
        case, op, who = key(name)
        cells.setdefault((case, op), {})[who] = time(sec * 1e9) + (f" ±{ci}" if ci and ci != "∞" else "")
    for name, (v, _) in units.get("allocs/op", {}).items():
        case, op, who = key(name)
        cells.setdefault((case, f"{op} allocs"), {})[who] = f"{v:.0f}"
    for name, (v, _) in units.get("wire-B", {}).items():
        case, op, who = key(name)
        if op == "serialize":
            cells.setdefault((case, "size"), {})[who] = size(int(v))
    cases = list(dict.fromkeys(c for c, _ in cells))
    ops = ["serialize", "deserialize", "serialize allocs", "deserialize allocs", "size"]
    cells = {k: cells[k] for k in sorted(cells, key=lambda k: (cases.index(k[0]), ops.index(k[1]) if k[1] in ops else 99))}
    who = pivot(cells, ["Nino", "NinoTags", "Protobuf", "MessagePack", "JSON", "Gob"])
    machine = ", ".join(v for v in (env.get("cpu"), env.get("goos") and f"{env['goos']}/{env.get('goarch', '')}") if v)
    intro = ("`runtimes/go/bench` (`testing.B`, summarized by benchstat: medians ± their 95% confidence interval): "
             "codecs in the shape nino-gen generates (`Nino`) and the struct tags (`NinoTags`) against Protocol "
             "Buffers (google.golang.org/protobuf), MessagePack (vmihailenco/msgpack), encoding/json and gob, on "
             f"{machine or 'a GitHub runner'}. Allocations are counted per operation; sizes are the encoded bytes.")
    return section("go", tag, f"The Go runtime of {tag} against Protocol Buffers, MessagePack, encoding/json and gob",
                   intro, grid(cells, who))


RUST_SIZES = re.compile(r"^([\w-]+): (.*\bbytes\b.*)$")


def rust(directory, tag, log=None):
    """One row per group and operation, one column per serializer (Nino first), and each group's encoded sizes
    (which compare.rs prints) from the bench's output when `log` is given."""
    times = {}
    for path in sorted(glob.glob(os.path.join(directory, "**", "new", "benchmark.json"), recursive=True)):
        bench = json.load(open(path, encoding="utf-8"))
        estimates = json.load(open(os.path.join(os.path.dirname(path), "estimates.json"), encoding="utf-8"))
        key = (bench["group_id"], bench["function_id"])
        mean = estimates["mean"]
        ci = mean.get("confidence_interval", {})
        half = (ci.get("upper_bound", 0) - ci.get("lower_bound", 0)) / 2
        times.setdefault(key, {})[bench.get("value_str") or ""] = (mean["point_estimate"],
                                                                   100 * half / mean["point_estimate"])
    if not times:
        sys.exit("no criterion results under " + directory)
    sizes = {}
    for line in open(log, encoding="utf-8", errors="replace") if log else []:
        m = RUST_SIZES.match(line.strip())
        if m:
            sizes[m.group(1)] = {who: int(n) for n, who in re.findall(r"(\d+) (?:bytes )?([\w-]+)", m.group(2))}
    cells = {}
    for group in dict.fromkeys(group for group, _ in times):
        for op in ("serialize", "deserialize"):
            for w, (mean, pct) in times.get((group, op), {}).items():
                cells.setdefault((group, op), {})[w] = time(mean) + spread(pct)
        for w, n in sizes.get(group, {}).items():
            cells.setdefault((group, "size"), {})[w] = size(n)
    who = pivot(cells, ["nino", "prost", "rmp-serde", "bincode", "postcard", "serde_json"])
    intro = ("`runtimes/rust/bench` (criterion): codecs in the shape nino-gen generates against Protocol Buffers "
             "(prost), MessagePack (rmp-serde), bincode, postcard and serde_json. Times are criterion's mean "
             "estimates ± half their 95% confidence interval; sizes are the encoded bytes of each case.")
    return section("rust", tag, f"The Rust runtime of {tag} against Protocol Buffers, MessagePack, bincode, postcard "
                   "and serde_json", intro, grid(cells, who))


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
