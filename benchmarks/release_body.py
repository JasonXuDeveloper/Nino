#!/usr/bin/env python3
"""Writes the benchmark sections of a release body (.github/workflows/report.yml).

usage:
  release_body.py dotnet --body current.md --tag TAG --settings BDN_ARGS --x64 merged.md [--v3 merged.md]
                         [--arm64 merged.md] --out DIR
  release_body.py runtimes --body current.md --sections DIR --out updated.md
  release_body.py complete --body current.md

dotnet: the Performance Report (x64), the v3 comparison and the Arm64 section of a release, each only when the body
lacks it. Writes DIR/body.md (the new body), the sections it added as DIR/perf.md, DIR/v3.md and DIR/arm64.md, and
DIR/assets.tsv, the files to attach to the release (source path, tab, asset name); nothing when every section is there
(or has no report). The sections go before the Performance Report: parsers that
read every table from "Performance Report" to the end of the body then never see their tables.

runtimes: appends the sections of the runtimes outside .NET (DIR/runtime-section-<lang>[-arm64]/section.md, from
runtimes_report.py) that the body lacks, x64 first, then Arm64. Writes the new body to updated.md only when it added
one, and exits 0 either way.

complete: prints true when the body has every .NET section (the benchmark then need not run), false otherwise.

The docs site (nino.docs scripts/sync-benchmarks.ts) finds the sections by their headings: keep them as they are.
"""
import argparse
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))

# GitHub's limit for a release body, in characters.
BODY_LIMIT = 125_000

# The docs site looks for these headings. The Arm64 one never contains "Performance Report", by which parsers find
# the x64 report.
PERF_MARK = "Performance Report"
V3_HEADING = "## 📊 v3 3.3.13 vs v4 (same machine)"
ARM64_HEADING = "## 📊 Arm64 benchmark"

# Release assets: the full BenchmarkDotNet reports (the body has the short tables of compact_report.py), and the
# whole Arm64 section, where the docs site reads it when the body has only a link to it.
FULL_X64_ASSET = "benchmark-x64-full.md"
FULL_ARM64_ASSET = "benchmark-arm64-full.md"
FULL_V3_ASSET = "benchmark-v3-full.md"
ARM64_ASSET = "arm64-benchmark.md"

# The runtime sections, in the order they are appended.
RUNTIMES = ("typescript", "go", "rust", "typescript-arm64", "go-arm64", "rust-arm64")


def run(script, *args):
    """A script of this directory's output; its failure stops this one."""
    return subprocess.run([sys.executable, os.path.join(HERE, script), *args],
                          check=True, stdout=subprocess.PIPE, encoding="utf-8").stdout


def asset(repo, tag, name):
    return f"[{name}](https://github.com/{repo}/releases/download/{tag}/{name})"


def perf_section(report, repo, tag, now):
    return "\n".join((
        "## 📊 Performance Report",
        "",
        "<details>",
        "<summary>Click to expand benchmark results</summary>",
        "",
        run("compact_report.py", report),
        f"Median, Min, Max and the GC counts: {asset(repo, tag, FULL_X64_ASSET)}.",
        "",
        "</details>",
        "",
        "---",
        f"*Benchmark generated automatically on {now}*",
    )) + "\n"


def v3_section(v3, x64, repo, tag, settings):
    compare = subprocess.run([sys.executable, os.path.join(HERE, "compare_v3.py"), v3, x64],
                             stdout=subprocess.PIPE, encoding="utf-8")
    table = compare.stdout
    if compare.returncode:
        table += "compare.py could not read the reports.\n"
    lines = open(v3, encoding="utf-8").read().splitlines()
    first = next((i for i, line in enumerate(lines) if line.startswith("|")), len(lines))
    # The environment block of the first half (the docs site names its machine from it), then the runner of each half.
    environment = lines[:first]
    runners = [line for line in lines[first + 1:] if not line.startswith("|")]
    if runners and runners[0] == "":
        runners = runners[1:]
    return "".join((
        f"{V3_HEADING}\n",
        "\n",
        "<details>\n",
        f"<summary>Nino v3 3.3.13 (NuGet) against {tag}, back to back on the same runners</summary>\n",
        "\n",
        # Never write the words "Performance Report" here: parsers find the v4 report by them.
        "The v3 build of `benchmarks/Nino.Benchmark` (the Nino and MemoryPack cases of the same `SimpleTest`, built "
        "against Nino 3.3.13 from nuget.org) ran directly before the v4\n",
        "SimpleTest run below, its serialize and its deserialize cases each on the runner of the same v4 cases, with\n",
        "the same BenchmarkDotNet settings\n",
        f"(`{settings}`). MemoryPack runs in both, so its two columns show how much the runner moved.\n",
        "\n",
        "### v4 vs v3\n",
        "\n",
        table,
        "\n",
        "### v3 3.3.13 runners\n",
        "\n",
        "".join(line + "\n" for line in environment + runners),
        "\n",
        f"The v3 report: {asset(repo, tag, FULL_V3_ASSET)}.\n",
        "\n",
        "</details>\n",
    ))


def arm64_section(report, repo, tag, settings):
    return "".join((
        f"{ARM64_HEADING}\n",
        "\n",
        "<details>\n",
        f"<summary>The same benchmarks of {tag} on an Arm64 runner</summary>\n",
        "\n",
        # Never write the words "Performance Report" here: parsers find the x64 report by them.
        "The benchmark classes of the x64 report below, with the same BenchmarkDotNet settings\n",
        f"(`{settings}`), on GitHub's `ubuntu-26.04-arm` runners. Compare the serializers within\n",
        "this table: the runners differ from the x64 ones, so times across the two tables say little.\n",
        "\n",
        run("compact_report.py", report),
        "\n",
        f"Median, Min, Max and the GC counts: {asset(repo, tag, FULL_ARM64_ASSET)}.\n",
        "\n",
        "</details>\n",
    ))


def join(*parts):
    return "\n\n".join(part for part in parts if part) + "\n"


def dotnet(args):
    body = open(args.body, encoding="utf-8").read()
    repo = os.environ["GITHUB_REPOSITORY"]
    now = args.now or time.strftime("%Y-%m-%d %H:%M:%S UTC", time.gmtime())
    sections = {}
    if PERF_MARK in body:
        print("⏭️ Performance Report already exists in release notes", file=sys.stderr)
    else:
        sections["perf"] = perf_section(args.x64, repo, args.tag, now)
    if V3_HEADING in body:
        print("⏭️ v3 comparison already exists in release notes", file=sys.stderr)
    elif not args.v3:
        print("⚠️ No v3 report (see the v3 steps): the v3 comparison is left out", file=sys.stderr)
    else:
        sections["v3"] = v3_section(args.v3, args.x64, repo, args.tag, args.settings)
    if ARM64_HEADING in body:
        print("⏭️ Arm64 benchmark already exists in release notes", file=sys.stderr)
    elif not args.arm64:
        print("⚠️ No Arm64 report (see the Arm64 jobs): the Arm64 section is left out", file=sys.stderr)
    else:
        sections["arm64"] = arm64_section(args.arm64, repo, args.tag, args.settings)
    if not sections:
        print("⏭️ Nothing to add", file=sys.stderr)
        return

    body = body.rstrip("\n")
    v3 = sections.get("v3", "").strip("\n")
    arm64 = sections.get("arm64", "").strip("\n")
    perf = sections.get("perf", "").strip("\n")
    match = re.search(r"^.*" + PERF_MARK, body, re.MULTILINE)
    if match:  # the report is already there: put the new sections in front of its heading
        head, tail = body[:match.start()].rstrip("\n"), body[match.start():]
    else:
        head, tail = body, perf
    text = join(head, v3, arm64, tail)
    if len(text) > BODY_LIMIT and arm64:  # no room for the Arm64 section: it is attached instead
        arm64 = "\n\n".join((ARM64_HEADING, f"The Arm64 report is attached to this release: "
                             f"{asset(repo, args.tag, ARM64_ASSET)} (the release notes have no room for it)."))
        text = join(head, v3, arm64, tail)

    os.makedirs(args.out, exist_ok=True)
    open(os.path.join(args.out, "body.md"), "w", encoding="utf-8").write(text)
    for name, section in sections.items():
        open(os.path.join(args.out, name + ".md"), "w", encoding="utf-8").write(section)
    # The full reports the body links to, and the whole Arm64 section (where the docs site reads it when the body has
    # only a link to it).
    assets = []
    if "perf" in sections:
        assets.append((args.x64, FULL_X64_ASSET))
    if "arm64" in sections:
        assets += [(os.path.join(args.out, "arm64.md"), ARM64_ASSET), (args.arm64, FULL_ARM64_ASSET)]
    if "v3" in sections:
        assets.append((args.v3, FULL_V3_ASSET))
    with open(os.path.join(args.out, "assets.tsv"), "w", encoding="utf-8") as out:
        out.writelines(f"{os.path.abspath(path)}\t{name}\n" for path, name in assets)


def complete(args):
    body = open(args.body, encoding="utf-8").read()
    print(str(PERF_MARK in body and V3_HEADING in body and ARM64_HEADING in body).lower())


def runtimes(args):
    body = open(args.body, encoding="utf-8").read()
    text = body
    added = 0
    for lang in RUNTIMES:
        path = os.path.join(args.sections, f"runtime-section-{lang}", "section.md")
        section = open(path, encoding="utf-8").read() if os.path.isfile(path) else ""
        if not section:
            print(f"⚠️ No {lang} section (see its runtimes job): left out", file=sys.stderr)
            continue
        heading = section.split("\n", 1)[0]
        if heading in body:
            print(f"⏭️ {heading} already in the release notes", file=sys.stderr)
            continue
        if len(text) + 2 + len(section) > BODY_LIMIT:
            print(f"::warning::No room for the {lang} section in the release body (GitHub's {BODY_LIMIT:,} "
                  "characters)")
            continue
        text += "\n\n" + section
        added += 1
    if not added:
        print("⏭️ Nothing to add", file=sys.stderr)
        return
    open(args.out, "w", encoding="utf-8").write(text)
    print(f"✅ Added {added} runtime section(s)", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    one = commands.add_parser("dotnet")
    one.add_argument("--body", required=True)
    one.add_argument("--tag", required=True)
    one.add_argument("--settings", required=True, help="the BenchmarkDotNet settings the text names")
    one.add_argument("--x64", required=True, help="the merged x64 report")
    one.add_argument("--v3")
    one.add_argument("--arm64")
    one.add_argument("--out", required=True)
    one.add_argument("--now", help="the time the Performance Report names (default: now)")
    one.set_defaults(run=dotnet)
    two = commands.add_parser("runtimes")
    two.add_argument("--body", required=True)
    two.add_argument("--sections", required=True)
    two.add_argument("--out", required=True)
    two.set_defaults(run=runtimes)
    three = commands.add_parser("complete")
    three.add_argument("--body", required=True)
    three.set_defaults(run=complete)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
