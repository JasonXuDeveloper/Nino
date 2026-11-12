#!/usr/bin/env python3
"""The release body's build-time section: Nino v3 3.3.13's source generator against the release's, from the --json
files of benchmarks/Nino.GeneratorBenchmark (report.yml's generator job), and the parser of that section (readme.py).

usage:
  generator_report.py section TAG --v3 <json>... --v4 <json>...   the section, to stdout
  generator_report.py heading                                      the section's heading (report.yml checks the body)

Each --v3 file is paired with the --v4 file of the same workload (roots, depth); one table per workload, fewest types
first. A run whose generated code did not compile (errors) fails the script: its times would measure nothing.

The section (the docs site, nino.docs scripts/sync-benchmarks.ts, reads it by its heading; change the format only
together with that parser and parse() below):

  ## 🏗️ Build: v3 3.3.13 vs v4 (source generator)
  <details>, <summary>, a paragraph on the workload and the runs
  Environment: <CPU>, <n> cores, <.NET runtime>, Roslyn <version>
  ### <types> types (N = <roots>)        one per workload, types with a thousands comma (1,100)
  | | v3 (3.3.13) | v4 | v4 vs v3 |      then one row per ROWS label, in that order
  </details>

Cells: sizes "18.7 MB (363,233 lines)" and "2.21 MB" (millions of chars and of bytes), times "0.84 s", "42.9 s",
"462 s", the ratio "**14× smaller**", "**221× faster**", "on par" (within 5%) or "—" (v4 not ahead: never "slower").
"""
import argparse
import json
import re
import sys

import bdn

HEADING = "## 🏗️ Build: v3 3.3.13 vs v4 (source generator)"
HEAD = "| | v3 (3.3.13) | v4 | v4 vs v3 |"
# (key, row label, JSON field, kind): the rows of each table, in order.
ROWS = [("source", "Generated source", "chars", "size"),
        ("emit", "Compiling the generated code (emit)", "emit_ms", "time"),
        ("generate", "Running the generator", "generate_ms", "time"),
        ("member", "Re-generating after editing a member", "member_edit_ms", "time"),
        ("comment", "Re-generating after editing a comment", "comment_edit_ms", "time"),
        ("il", "IL of the generated code", "il_bytes", "size")]


def megabytes(n):
    v = n / 1e6
    return f"{v:.2f} MB" if v < 10 else f"{v:.1f} MB" if v < 100 else f"{v:,.0f} MB"


def seconds(ms):
    s = ms / 1000
    return f"{s:.2f} s" if s < 10 else f"{s:.1f} s" if s < 100 else f"{s:,.0f} s"


def one(r):
    """As readme.py writes a ratio: one decimal below 10 (7.2), whole numbers from 10 (23)."""
    return f"{r:.1f}" if r < 10 else f"{r:.0f}"


def ratio(v3, v4, word):
    r = v3 / v4 if v4 else float("inf")
    if round(abs(r - 1) * 100) <= 5:
        return "on par"
    return f"**{one(r)}× {word}**" if r > 1 else "—"


def row(label, field, kind, v3, v4):
    if kind == "time":
        cells = [seconds(v3[field]), seconds(v4[field])]
        return [label, *cells, ratio(v3[field], v4[field], "faster")]
    if field == "chars":
        cells = [f"{megabytes(r['chars'])} ({r['lines']:,} lines)" for r in (v3, v4)]
    else:
        cells = [megabytes(r[field]) for r in (v3, v4)]
    return [label, *cells, ratio(v3[field], v4[field], "smaller")]


def environment(run):
    return f"{bdn.cpu(run['cpu'])}, {run['cores']} cores, {run['runtime']}, Roslyn {run['roslyn']}"


def section(tag, v3_runs, v4_runs):
    for run in v3_runs + v4_runs:
        if run["errors"]:
            sys.exit(f"the generated code of {run['types']} types (generator {run['generator']}) has "
                     f"{run['errors']} compile errors: not publishing its times")
    v4_by = {(r["roots"], r["depth"]): r for r in v4_runs}
    pairs = sorted(((v3, v4_by[(v3["roots"], v3["depth"])]) for v3 in v3_runs if (v3["roots"], v3["depth"]) in v4_by),
                   key=lambda p: p[0]["types"])
    if not pairs:
        sys.exit("no workload measured with both v3 and v4")

    def measured(runs):
        return ", ".join(f"{r['measured']} at {r['types']:,} types" for r in sorted(runs, key=lambda r: r["types"]))

    depth = pairs[0][0]["depth"]
    out = [HEADING, "", "<details>",
           f"<summary>Nino v3 3.3.13's source generator (NuGet) against {tag}'s, on one runner</summary>", "",
           # Never write "Performance Report", "Method" or "Mean" here: parsers find the .NET report by them.
           "`benchmarks/Nino.GeneratorBenchmark` on a synthetic assembly of N abstract root classes, each with a chain "
           f"of {depth} derived classes, a sealed leaf and a struct (N × {depth + 3} types), compiled in process: it "
           "runs the generator, emits the whole assembly, and runs the generator again after adding a member and "
           "after adding a comment. Each time is the median of the runs after the first (v3: "
           f"{measured([v3 for v3, _ in pairs])}; v4: {measured([v4 for _, v4 in pairs])}); v3 and v4 ran one "
           "after the other on this runner. Sizes are in millions of chars (source) and bytes (IL).", "",
           f"Environment: {environment(pairs[0][1])}", ""]
    for v3, v4 in pairs:
        out += [f"### {v4['types']:,} types (N = {v4['roots']})", "", HEAD, "|---|---:|---:|---:|"]
        out += ["| " + " | ".join(row(label, field, kind, v3, v4)) + " |" for _, label, field, kind in ROWS]
        out.append("")
    out.append("</details>")
    return "\n".join(out) + "\n"


def parse(text):
    """The section in a release body (or alone) -> {"environment": str, "tables": [{"types": int, "roots": int,
    "table": the Markdown table, "rows": {key: (v3, v4, ratio)}}]}; None when the text has no such section."""
    start = text.find(HEADING)
    if start < 0:
        return None
    body = text[start + len(HEADING):]
    end = re.search(r"^## ", body, re.M)
    body = body[:end.start()] if end else body
    env = re.search(r"^Environment: (.+)$", body, re.M)
    labels = {label: key for key, label, _, _ in ROWS}
    tables = []
    for match in re.finditer(r"^### ([\d,]+) types \(N = (\d+)\)\n\n((?:\|.*\n?)+)", body, re.M):
        lines = match.group(3).strip().splitlines()
        rows = {}
        for line in lines[2:]:
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) == 4 and cells[0] in labels:
                rows[labels[cells[0]]] = tuple(cells[1:])
        if set(rows) != set(labels.values()):
            raise ValueError(f"the table of {match.group(1)} types lacks rows: {sorted(set(labels.values()) - set(rows))}")
        tables.append({"types": int(match.group(1).replace(",", "")), "roots": int(match.group(2)),
                       "table": "\n".join(lines), "rows": rows})
    if not env or not tables:
        raise ValueError("the build section has no Environment line or no table")
    return {"environment": env.group(1).strip(), "tables": tables}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    one_ = commands.add_parser("section")
    one_.add_argument("tag")
    one_.add_argument("--v3", nargs="+", required=True)
    one_.add_argument("--v4", nargs="+", required=True)
    commands.add_parser("heading")
    args = parser.parse_args()
    if args.command == "heading":
        print(HEADING)
        return

    def load(paths):
        return [json.load(open(p, encoding="utf-8")) for p in paths]

    sys.stdout.write(section(args.tag, load(args.v3), load(args.v4)))


if __name__ == "__main__":
    main()
