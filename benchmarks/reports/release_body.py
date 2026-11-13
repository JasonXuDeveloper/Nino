#!/usr/bin/env python3
"""Publishes a release's benchmark results (.github/workflows/report.yml): attaches them as assets, draws a chart of
each section and writes the benchmark part of the release body from the manifest.

usage:
  release_body.py publish --tag TAG --dir WORK [--release-dir FAKE]
                          NAME=PATH...
  release_body.py body --tag TAG --body current.md --manifest benchmarks-manifest.json --out new.md

publish: every job of report.yml that has results runs it with its own files (the asset names: release_assets.py).
It reads the release's manifest into WORK (none yet: a new one), copies each PATH into WORK as NAME and lists it,
draws the chart of each section whose files are in WORK (charts.py: an SVG in the light and one in the dark theme) and
puts them on the release-notes branch (charts/<tag>/), uploads the files and the manifest (--clobber: a re-run replaces
them), then rewrites the benchmark part of the body. --release-dir runs it against a directory instead of GitHub
(release_assets.Local: body.md, assets/, release-notes/), for a dry run.

body: the body with its benchmark part rewritten from a manifest (what publish does last).

The body: the release notes (release_notes.py) stay as they are; after them, between the START and END markers, a
"## 📊 Benchmarks" heading and one collapsed <details> per section in SECTIONS' order: its title and a one-line
summary, the chart (a <picture> of its light and dark SVG, by the reader's colour scheme), the environment and links to the assets with
the full tables. A body from before the manifest (v4.0.0-rc.9 and older) has the old sections (tables) instead: each
is dropped once the manifest has its asset, and kept as it is (after the charts) until then. The part is a few
thousand characters, far under GitHub's 125,000.

The docs site reads the assets, not the body (nino.docs scripts/sync-benchmarks.ts); it parses the old sections of
releases without a manifest, which is why legacy_sections knows their headings.
"""
import argparse
import os
import re
import statistics
import sys

import charts
import generator_report
import readme
import release_assets
import runtimes_report
import unity_report
from bdn import FLOOR_NS, mean_ns

BODY_LIMIT = 125_000  # GitHub's limit for a release body, in characters
START = ("<!-- nino-benchmarks: written by benchmarks/reports/release_body.py from benchmarks-manifest.json; "
         "edits between these markers are replaced -->")
END = "<!-- /nino-benchmarks -->"
HEADING = "## 📊 Benchmarks"

# The sections in the body's order: (the asset the section is drawn from, its title).
SECTIONS = [("dotnet-x64.md", ".NET x64"), ("dotnet-arm64.md", ".NET Arm64"),
            ("dotnet-v3-x64.md", "v3 3.3.13 vs v4 (.NET x64, same runners)")]
for _lang, _name in (("go", "Go"), ("rust", "Rust"), ("typescript", "TypeScript")):
    SECTIONS += [(f"runtime-{_lang}-x64.md", f"{_name} runtime x64"), (f"runtime-{_lang}-arm64.md", f"{_name} runtime Arm64")]
SECTIONS += [("unity-x64.md", "Unity, Burst kernels in an IL2CPP player"),
             ("build-x64.md", "Build: v3 3.3.13 vs v4 source generator")]
TITLES = dict(SECTIONS)

# The headings of the sections bodies had before the manifest, and the asset that replaces each.
V3_HEADING = "## 📊 v3 3.3.13 vs v4 (same machine)"
ARM64_HEADING = "## 📊 Arm64 benchmark"
LEGACY = {V3_HEADING: "dotnet-v3-x64.md", ARM64_HEADING: "dotnet-arm64.md",
          unity_report.HEADING: "unity-x64.md", generator_report.HEADING: "build-x64.md",
          **{h: f"runtime-{lang}-x64.md" for lang, h in runtimes_report.HEADINGS.items()},
          **{h: f"runtime-{lang}-arm64.md" for lang, h in runtimes_report.ARM64_HEADINGS.items()}}

# Method prefixes of the .NET libraries (GoogleProtobuf before Protobuf, which it starts with).
LIBRARIES = ["Nino"] + sorted((o for o, _, _ in readme.OTHERS), key=len, reverse=True)


# ---------------------------------------------------------------- the body


def legacy_key(line):
    if line.startswith("## ") and "Performance Report" in line:
        return "dotnet-x64.md"
    return LEGACY.get(line.strip())


def legacy_sections(text):
    """{asset name: the old section's text} of a body's benchmark part (each from its heading to the next "## ")."""
    out, key, lines = {}, None, []
    for line in text.splitlines():
        if line.startswith("## "):
            if key:
                out[key] = "\n".join(lines).strip("\n")
            key, lines = legacy_key(line), []
        if key:
            lines.append(line)
    if key:
        out[key] = "\n".join(lines).strip("\n")
    return out


def split(body):
    """-> (the release notes, the benchmark part, what follows END)."""
    if START in body:
        notes, rest = body.split(START, 1)
        part, _, tail = rest.partition(END)
        return notes, part, tail
    lines = body.splitlines(keepends=True)
    at = next((i for i, line in enumerate(lines) if legacy_key(line.rstrip("\n"))), len(lines))
    return "".join(lines[:at]), "".join(lines[at:]), ""


def link(tag, name):
    return f"[{name}]({release_assets.download_url(tag, name)})"


def section(manifest, entry, tag):
    title = entry.get("title") or TITLES.get(entry["name"], entry["name"])
    summary = f"<b>{title}</b>" + (f": {entry['summary']}" if entry.get("summary") else "")
    out = ["<details>", f"<summary>{summary}</summary>", ""]
    if entry.get("chart"):
        img = (f'<img src="{release_assets.chart_url(entry["chart"], entry.get("chart_sha", ""))}" '
               f'alt="{title}" width="{entry.get("chart_width", 940)}">')
        if entry.get("chart_dark"):
            dark = release_assets.chart_url(entry["chart_dark"], entry.get("chart_dark_sha", ""))
            img = f'<picture><source media="(prefers-color-scheme: dark)" srcset="{dark}">{img}</picture>'
        out += [img, ""]
    data = [e["name"] for e in manifest["assets"] if e.get("of") == entry["name"]]
    files = link(tag, entry["name"]) + (" (made from " + ", ".join(link(tag, n) for n in data) + ")" if data else "")
    environment = f"{entry['environment'].rstrip('.')}. " if entry.get("environment") else ""
    out += [f"{environment}The full table: {files}.", "", "</details>"]
    return "\n".join(out)


def render(body, manifest, tag):
    """The body with its benchmark part written from the manifest."""
    notes, part, tail = split(body)
    listed = {e["name"]: e for e in manifest["assets"]}
    kept = [text for key, text in legacy_sections(part).items() if key not in listed]
    sections = [section(manifest, listed[name], tag) for name, _ in SECTIONS if name in listed]
    out = [notes.rstrip("\n"), "", START, HEADING, "",
           "Charts of one run on GitHub's runners; each section's full table is attached to this release, and "
           f"{link(tag, release_assets.MANIFEST)} lists the files.", ""]
    out += [s + "\n" for s in sections] + [t + "\n" for t in kept]
    out += [END]
    text = "\n".join(out).lstrip("\n") + "\n" + tail.lstrip("\n")
    return text.rstrip("\n") + "\n"


# ---------------------------------------------------------------- the charts


def ratio_ok(nino, other):
    """Nino ahead or on par: under the floor, or the other time no shorter than Nino's beyond 5%."""
    if nino < FLOOR_NS:
        return True
    return other >= nino or charts.on_par(other / nino)


def split_method(method):
    lib = next((p for p in LIBRARIES if method.startswith(p)), None)
    return (lib, method[len(lib):]) if lib else (None, method)


def dotnet_chart(path, arch):
    _, v4 = readme.parse(path)
    workloads = all(f"NinoSerialize{k}" in v4 for k in readme.WORKLOAD_KEYS)
    cases = readme.WORKLOADS if workloads else readme.SIMPLE_CASES
    chart = readme.chart_groups(v4, None, cases=cases)
    by_case = {}
    for method, row in v4.items():
        lib, case = split_method(method)
        if lib:
            by_case.setdefault(case, {})[lib] = row["mean"]
    compared = {c: libs for c, libs in by_case.items() if "Nino" in libs and len(libs) > 1}
    ahead = sum(all(ratio_ok(libs["Nino"], t) for lib, t in libs.items() if lib != "Nino") for libs in compared.values())
    summary = (f"Nino v4 fastest or on par in {ahead} of {len(compared)} cases against every library measured "
               f"({len(v4)} rows)")
    what = "the 12 workloads" if workloads else "SimpleTest and PolymorphicTest"
    note = (f"BenchmarkDotNet, .NET 10, {arch} GitHub runners; {what}, every library measured; "
            "N× = N times as long as Nino v4.")
    return chart, summary, note


def v3_chart(v3_path, x64_path):
    _, v3 = readme.parse(v3_path)
    _, v4 = readme.parse(x64_path)
    keys = [m[len("NinoSerialize"):] for m in v3 if m.startswith("NinoSerialize") and m in v4]
    chart, faster, par, ratios = [], 0, 0, []
    for key in keys:
        pair = []
        for verb in ("Serialize", "Deserialize"):
            method = f"Nino{verb}{key}"
            if method not in v3 or method not in v4:
                pair.append(None)
                continue
            new, old = v4[method]["mean"], v3[method]["mean"]
            pair.append((f"{verb} · {key}", [("Nino v4", new), ("Nino v3", old)]))
            if new < FLOOR_NS and old < FLOOR_NS:
                par += 1
            elif new < FLOOR_NS or (old > new and not charts.on_par(old / new)):
                faster += 1
                if new >= FLOOR_NS:
                    ratios.append(old / new)
            elif charts.on_par(old / new):
                par += 1
        chart.append(tuple(pair))
    total = sum(1 for pair in chart for g in pair if g)
    summary = f"v4 faster in {faster} of {total} cases" + (f", on par in {par}" if par else "")
    if ratios:
        summary += f" (median {charts.one(statistics.median(ratios))}× faster)"
    note = ("Nino v3 3.3.13 (NuGet) directly before v4 on the same runners, SimpleTest and PolymorphicTest; "
            "N× = N times as long as Nino v4.")
    return chart, summary, note


def grid(text):
    """The first table of a runtime section -> (serializers, {(case, operation): {serializer: ns}}) of its times."""
    lines = [line for line in text.splitlines() if line.startswith("|")]
    if len(lines) < 3:
        raise ValueError("no table")
    head = [c.strip() for c in lines[0].strip().strip("|").split("|")]
    if head[:2] not in (["Case", "Operation"], ["Scenario", "Operation"]):
        raise ValueError("no Case and Operation columns")
    who, out = head[2:], {}
    for line in lines[2:]:
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip().strip("|"))]
        if len(cells) != len(head) or cells[1] not in ("serialize", "deserialize"):
            continue
        times = {w: mean_ns(c) for w, c in zip(who, cells[2:])}
        out[(cells[0], cells[1])] = {w: t for w, t in times.items() if t is not None}
    return who, out


def runtime_chart(path, entry):
    who, times = grid(open(path, encoding="utf-8").read())
    nino = who[0]
    chart, ahead, total = [], 0, 0
    for case in dict.fromkeys(c for c, _ in times):
        pair = []
        for op in ("serialize", "deserialize"):
            row = times.get((case, op))
            if not row or nino not in row:
                pair.append(None)
                continue
            pair.append((f"{op.capitalize()} · {case}", [(w, row[w]) for w in who if w in row]))
            total += 1
            ahead += all(ratio_ok(row[nino], t) for w, t in row.items() if w != nino)
        chart.append(tuple(pair))
    summary = f"{nino} fastest or on par in {ahead} of {total} operations"
    note = f"{entry.get('environment') or 'a GitHub runner'}; N× = N times as long as {nino}."
    return chart, summary, note


def unity_tables(text):
    """[(Unity version, [(case, operation, without ns, with ns)])] of a Unity section."""
    out = []
    for match in re.finditer(r"^### Unity (\S+)\n\n((?:\|.*\n?)+)", text, re.M):
        rows = []
        for line in match.group(2).strip().splitlines()[2:]:
            cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip().strip("|"))]
            if len(cells) >= 4 and mean_ns(cells[2]) is not None and mean_ns(cells[3]) is not None:
                rows.append((cells[0].replace("\\|", "|").replace("&lt;", "<").replace("&gt;", ">")
                             .replace("&amp;", "&"), cells[1], mean_ns(cells[2]), mean_ns(cells[3])))
        out.append((match.group(1), rows))
    return out


def unity_chart(path, entry):
    tables = unity_tables(open(path, encoding="utf-8").read())
    if not tables:
        raise ValueError("no Unity table")
    version, rows = tables[-1]  # the newest Unity version; the asset has every one
    groups = {}
    for case, op, without, burst in rows:
        groups.setdefault(case, {})[op] = (f"{op} · {case}", [("With Burst", burst), ("Without Burst", without)])
    chart = [(ops.get("Serialize"), ops.get("Deserialize")) for ops in groups.values()]
    speedups = [without / burst for _, _, without, burst in rows if burst > 0]
    faster = sum(1 for s in speedups if s > 1 and not charts.on_par(s))
    summary = f"Unity {version}: the Burst kernels faster in {faster} of {len(rows)} operations"
    if speedups:
        summary += f", up to {charts.one(max(speedups))}×"
    note = f"Unity {version}, IL2CPP player, median per call; N× = N times as long as with the Burst kernels."
    return chart, summary, note


def build_value(cell):
    match = re.match(r"([\d,.]+)\s*(MB|s)\b", cell)
    return float(match.group(1).replace(",", "")) if match else None


def build_chart(path):
    found = generator_report.parse(open(path, encoding="utf-8").read())
    if found is None:
        raise ValueError("no build section")
    tables = found["tables"]
    small, large = tables[0], tables[-1]
    chart = []
    for key, label, _, _ in generator_report.ROWS:
        pair = []
        for table in (small, large) if small is not large else (small, None):
            if table is None:
                pair.append(None)
                continue
            v3, v4, _ = table["rows"][key]
            pair.append((f"{label} · {table['types']:,} types",
                         [("Nino v4", build_value(v4), v4), ("Nino v3", build_value(v3), v3)]))
        chart.append(tuple(pair))
    emit = large["rows"]["emit"][2].replace("**", "")
    source = large["rows"]["source"][2].replace("**", "")
    summary = f"at {large['types']:,} types, compiling the generated code {emit}, generated source {source}"
    note = "Sizes in millions of chars (source) and bytes (IL), times the median of the runs; N× = N times v4's."
    return chart, summary, note


def draw(directory, manifest, tag):
    """Draws the chart of every section whose files are in the directory; sets its entry's title, summary and chart.
    -> the SVGs written."""
    written = []
    for name, title in SECTIONS:
        entry = release_assets.entry(manifest, name)
        path = os.path.join(directory, name)
        if entry is None or not os.path.isfile(path):
            continue
        try:
            if entry["kind"] == "dotnet":
                chart, summary, note = dotnet_chart(path, "Arm64" if entry["arch"] == "arm64" else "x64")
            elif entry["kind"] == "dotnet-v3":
                x64 = os.path.join(directory, "dotnet-x64.md")
                if not os.path.isfile(x64):
                    continue
                chart, summary, note = v3_chart(path, x64)
            elif entry["kind"] == "runtime":
                chart, summary, note = runtime_chart(path, entry)
            elif entry["kind"] == "unity":
                chart, summary, note = unity_chart(path, entry)
            else:
                chart, summary, note = build_chart(path)
        except (ValueError, KeyError, SystemExit) as error:
            print(f"::warning::no chart for {name}: {error}", file=sys.stderr)
            continue
        heading = f"{title} (shorter is better)" if entry["kind"] == "build" else \
            f"{title}: {charts.README_TITLE[0].lower()}{charts.README_TITLE[1:]}"
        stem = os.path.join(directory, name.rsplit(".", 1)[0])
        entry.update(title=title, summary=summary)
        # SVG, light and dark: a few KB each, sharp at any zoom, and raw.githubusercontent.com serves it as
        # image/svg+xml, which GitHub's image proxy shows (PNGs at a readable resolution were about 5 MB a release).
        for theme, suffix, key in (("light", "", "chart"), ("dark", "-dark", "chart_dark")):
            image, _ = charts.svg(chart, theme, heading, note, 0 if entry["kind"] == "build" else FLOOR_NS)
            path = f"{stem}{suffix}.svg"
            with open(path, "w", encoding="utf-8") as f:
                f.write(image)
            entry.update({key: f"charts/{tag}/{os.path.basename(path)}", f"{key}_sha": release_assets.sha(path),
                          "chart_width": int(re.search(r'width="(\d+)"', image).group(1))})
            written.append(path)
        print(f"📈 {name}: {summary}", file=sys.stderr)
    return written


# ---------------------------------------------------------------- commands


def publish(args):
    rel = release_assets.release(args.tag, args.release_dir)
    os.makedirs(args.dir, exist_ok=True)
    manifest = release_assets.fetch(rel, args.dir, []) or release_assets.new_manifest(args.tag)
    manifest["tag"] = args.tag
    added = []
    for pair in args.files:
        name, _, source = pair.partition("=")
        if not source or not os.path.isfile(source):
            print(f"⚠️ {name}: no file {source!r}, left out", file=sys.stderr)
            continue
        release_assets.add(args.dir, manifest, name, source)
        added.append(name)
    if not added:
        print("⏭️ Nothing to publish", file=sys.stderr)
        return
    # v3's chart needs the x64 report: a job that adds only one of them draws it once both are there.
    images = draw(args.dir, manifest, args.tag)
    rel.push_charts(images)
    release_assets.save(args.dir, manifest)
    rel.upload([os.path.join(args.dir, n) for n in added] + [os.path.join(args.dir, release_assets.MANIFEST)])
    body = rel.body()
    text = render(body, manifest, args.tag)
    if len(text) > BODY_LIMIT:
        sys.exit(f"the body would have {len(text):,} characters, over GitHub's {BODY_LIMIT:,}")
    if text != body:
        rel.edit_body(text)
    print(f"✅ {', '.join(added)} published; the body has {len(text):,} characters", file=sys.stderr)


def body(args):
    manifest = release_assets.load(os.path.dirname(os.path.abspath(args.manifest)))
    text = render(open(args.body, encoding="utf-8").read(), manifest, args.tag)
    open(args.out, "w", encoding="utf-8").write(text)
    print(f"{len(text):,} characters", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    one = commands.add_parser("publish")
    one.add_argument("--tag", required=True)
    one.add_argument("--dir", required=True, help="the working directory: the manifest, the assets, the charts")
    one.add_argument("--release-dir", help="a directory standing in for the release (dry run)")
    one.add_argument("files", nargs="+", metavar="NAME=PATH")
    one.set_defaults(run=publish)
    two = commands.add_parser("body")
    two.add_argument("--tag", required=True)
    two.add_argument("--body", required=True)
    two.add_argument("--manifest", required=True)
    two.add_argument("--out", required=True)
    two.set_defaults(run=body)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
