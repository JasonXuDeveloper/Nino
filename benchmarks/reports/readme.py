#!/usr/bin/env python3
"""Write the benchmark numbers of README.md / README.zh.md, the README chart and the runtime and build tables of
docs/v4-benchmarks.md from a release's assets (release_assets.py) or from BenchmarkDotNet reports and a build section.

usage: python3 benchmarks/reports/readme.py (--release <tag> | --assets <dir> | [--v4 <joined report-github.md>
                                    [--v3 <v3 report-github.md>]...] [--build <section.md>]) [--tag <tag>]
                                    [--png [--chrome <path>]]

  --release   download the release's assets (dotnet-x64.md, dotnet-v3-x64.md, build-x64.md; gh) and read them
  --assets    a directory of release assets and their manifest (report.yml: the files its job adds); each of the
              three is read when the directory has it
  --v4        the joined report of benchmarks/Nino.Benchmark (every class in one table), as benchmarks/run.sh and
              report.yml produce it
  --v3        a report of the v3 run (Nino 3.3.13: SimpleTest, PolymorphicTest, NullableTest and CollectionsTest's
              nullable collections); repeat it for several runs,
              v3 is then the mean of them. Without it the README's v3 runtime numbers are left as they are.
  --build     the build section (generator_report.py, build-x64.md; a release body from before the assets will do):
              the README's Build row of v4 vs v3 (v3.build) and the build tables of docs/v4-benchmarks.md (doc.build.*)
  --tag       the release the reports belong to (default with --release/--assets: the manifest's); without one
              docs/v4-benchmarks.md names a local run
  --png       also render benchmark.png and benchmark-dark.png with headless Chrome (charts.py; README.md is packed
              into the NuGet packages, and nuget.org shows no SVG)

The README carries each generated number between markers: <!--bench:key-->5.1<!--/bench-->; docs/v4-benchmarks.md
carries whole tables between such markers. Everything outside the markers is written by hand. --v4 and --build fill
their own markers and leave the others as they are (report.yml runs them in different jobs). The headline numbers come from WorkloadTest's workloads: the ranges of how many times faster
Nino is than MemoryPack (ser.range, deser.range), MessagePack (msgpack.*), protobuf-net (protobuf.*), Google.Protobuf
(gprotobuf.*) and System.Text.Json (json.*). The chart (light and dark) shows each serializer's time per operation in
CHART_CASES, with v3 where it ran. Ratios follow the docs site (nino.docs src/lib/benchmarks.ts): within 5% is "on
par", and a case where v4 is slower is left out of a range, never shown as slower.
A time under FLOOR_NS is below what BenchmarkDotNet can tell apart from an empty method: v4 under it against a
measurable v3 is "faster, too fast to measure" (no ratio: it would only reflect the floor); both under it is on par.
"""
import argparse
import math
import os
import re
import sys
import tempfile
from pathlib import Path

import generator_report
import release_assets
from bdn import FLOOR_NS, mean_ns, rows
from bdn import cpu as short_cpu
from charts import on_par, one, png, svg, time

ROOT = Path(__file__).resolve().parents[2]
# The headline cases: WorkloadTest's workloads, with their names in the docs table.
WORKLOADS = [("MediaContent", "`MediaContent` (jvm-serializers)"),
             ("StackOverflowPage", "Stack Exchange API page, 20 questions"),
             ("RpcCall", "RPC call, interface result"), ("ProfileCache", "Cached profile, previous version"),
             ("EventStream", "Event store page, 500 events in generic envelopes"),
             ("LogBatch", "Log batch, 1,000 events"), ("StateSnapshot", "State-sync snapshot, in place"),
             ("LockstepFrames", "Lockstep, 10 frames of 8 players"), ("AoiUpdate", "MMO AOI update, 200 events"),
             ("WorldRegion", "SLG map region, shared alliances"), ("CardBattleLog", "Card battle replay, shared cards"),
             ("IdleSave", "Idle save, previous version, in place")]
WORKLOAD_KEYS = [key for key, _ in WORKLOADS]
# The other libraries of the headline, as (method prefix, key prefix of the README markers, name).
# In the chart's order (the owner's): the binary serializers closest to Nino first, then both Protobuf libraries, then JSON.
OTHERS = [("MemoryPack", None, "MemoryPack"), ("MessagePack", "msgpack", "MessagePack"),
          ("GoogleProtobuf", "gprotobuf", "Google.Protobuf"), ("Protobuf", "protobuf", "protobuf-net"),
          ("Json", "json", "System.Text.Json")]
# Nullable values against every library and v3 (the owner's choice): NullableTest's class with half its members null,
# 100 of them, and CollectionsTest's long?[] with every fifth element null.
NULLABLE_CASES = [("NullableClasses", "100 objects with nullable fields"), ("NullableLongs", "long?[] with nulls")]
# The chart's groups (the owner's choice), each serialized and deserialized: three workloads, then what Nino is built
# for: a block of raw structs (one vector copy), a polymorphic array, nullable values, and reading into existing objects.
CHART_CASES = [("MediaContent", "MediaContent"), ("RpcCall", "RPC call, interface result"),
               ("LogBatch", "log batch, 1,000 events"), ("SimpleStructs", "100 structs, one block copy"),
               ("PolymorphicClassArray", "polymorphic class array")] + NULLABLE_CASES


def parse(path):
    """-> (environment lines, {method: row}); a row is {"mean": ns, "payload": str, "allocated": str}."""
    before, table = rows(path)
    env = re.search(r"```\s*\n(.*?)```", "\n".join(before) + "\n", re.S)
    out = {}
    for row in table:
        method, mean = row.get("Method", ""), mean_ns(row.get("Mean", ""))
        if method and mean is not None:
            out[method] = {"mean": mean, "payload": row.get("Payload", "-"), "allocated": row.get("Allocated", "-")}
    if not out:
        sys.exit(f"no benchmark rows found in {path}")
    return (env.group(1).strip().splitlines() if env else []), out


def ratio(slow, fast):
    return round(slow / fast, 2)


def against(slow, fast):
    """v3 (slow) against v4 (fast): "unmeasurable" when v4 is under FLOOR_NS and v3 is not, 1.0 when both are, else
    the ratio."""
    if fast < FLOOR_NS:
        return 1.0 if slow < FLOOR_NS else "unmeasurable"
    return 0.0 if slow < FLOOR_NS else ratio(slow, fast)


def allocated(row):
    """BDN prints "-" for nothing allocated; merge_reports.py writes "?" for a failed measurement."""
    if row["allocated"] == "?":
        return "? B"
    value = re.match(r"([\d,]+)\s*B$", row["allocated"])
    return f"{int(value.group(1).replace(',', '')):,} B" if value else "0 B"


def environment(env):
    """'BenchmarkDotNet 0.15.2, .NET 10.0.13, Intel Xeon 2.10 GHz' from the report header."""
    bdn = next((re.sub(r"^BenchmarkDotNet v?", "", l).split(",")[0] for l in env if l.startswith("BenchmarkDotNet")), "")
    cpu = short_cpu(env[1].split(",")[0]) if len(env) > 1 else ""
    runtime = next((m.group(1) for l in env if (m := re.search(r"\[Host\]\s*:\s*(\.NET [\d.]+)", l))), "")
    return bdn, runtime, cpu


def numbers(v4_path, v3_paths, tag=None):
    env, v4 = parse(v4_path)

    def vs(other, verb, key):
        return ratio(v4[f"{other}{verb}{key}"]["mean"], v4[f"Nino{verb}{key}"]["mean"])

    def wins(verb, other="MemoryPack"):
        """Ratios of the workloads v4 wins: above 1, and not on par."""
        return [r for r in (vs(other, verb, k) for k in WORKLOAD_KEYS) if r > 1 and not on_par(r)]

    def win_range(verb, other="MemoryPack"):
        won = wins(verb, other)
        return f"{one(min(won))}–{one(max(won))}" if won else "—"

    bdn, runtime, cpu = environment(env)
    out = {
        "reuse.inplace": one(ratio(v4["MemoryPackLeavesOverwrite"]["mean"], v4["NinoLeavesInPlace"]["mean"])),
        "ser.range": win_range("Serialize"),
        "deser.range": win_range("Deserialize"),
        "env.bdn": bdn,
        "env.runtime": runtime,
        "env.cpu": cpu,
    }
    for other, prefix, _ in OTHERS:
        if prefix:
            out[f"{prefix}.ser.range"] = win_range("Serialize", other)
            out[f"{prefix}.deser.range"] = win_range("Deserialize", other)
    # The bytes of the Stack Exchange page, each library's (the README's environment line).
    # (Google.Protobuf writes the same messages as protobuf-net: one number for both.)
    for other, prefix in [("Nino", "v4")] + [(o, p or "memorypack") for o, p, _ in OTHERS if o != "GoogleProtobuf"]:
        out[f"payload.{prefix}"] = v4[f"{other}SerializeStackOverflowPage"]["payload"].replace(" B", "")

    v3 = {}
    if v3_paths:
        # How v3 ran next to v4 (README's environment line): once before it, or before and after it (older reports).
        out["v3.when"] = ("directly before it", "紧接其前跑一次") if len(v3_paths) == 1 else \
            ("directly before and after it", "紧接其前后各跑一次")
        runs = [parse(p)[1] for p in v3_paths]
        for method in runs[0]:
            if method.startswith("Nino") and all(method in run for run in runs):
                v3[method] = sum(run[method]["mean"] for run in runs) / len(runs)
    chart = chart_groups(v4, v3)

    if v3:
        cases = {"ser": [("Serialize", "SimpleClass", "class", "类"),
                         ("Serialize", "SimpleClasses", "class array", "类数组"),
                         ("Serialize", "SimpleStruct", "struct", "结构体"),
                         ("Serialize", "SimpleStructs", "struct array", "结构体数组")],
                 "deser": [("Deserialize", "SimpleClass", "class", "类"),
                           ("Deserialize", "SimpleClasses", "class array", "类数组"),
                           ("Deserialize", "SimpleStruct", "struct", "结构体"),
                           ("Deserialize", "SimpleStructs", "struct array", "结构体数组")]}
        for op, items in cases.items():
            en, zh = [], []
            for verb, key, label_en, label_zh in items:
                method = f"Nino{verb}{key}"
                if method not in v3 or method not in v4:
                    continue
                r = against(v3[method], v4[method]["mean"])
                if r == "unmeasurable":
                    en.append(f"**faster** ({label_en}, too fast to measure: under {FLOOR_NS} ns)")
                    zh.append(f"{label_zh}**更快**（快到测不出，不到 {FLOOR_NS} ns）")
                elif on_par(r):
                    en.append(f"on par ({label_en})")
                    zh.append(f"{label_zh}持平")
                elif r > 1:
                    en.append(f"**{r:.2f}× faster** ({label_en})")
                    zh.append(f"{label_zh}**快 {r:.2f} 倍**")
            out[f"v3.{op}"] = (", ".join(en), "，".join(zh))
    out.update({f"doc.{k}": v for k, v in tables(v4, v3, len(v3_paths or []), env, tag).items()})
    return out, chart


def chart_groups(v4, v3, cases=CHART_CASES, in_place=True):
    """The chart (charts.svg): per case, a row of two groups, serialize and deserialize; a group is (title,
    [(name, ns or None)]): the time of Nino v4, v3 and each library. v3 3.3.13 runs only the cases it can express:
    None (n/a) elsewhere; v3=None leaves its bars out (the release body's .NET charts). A library a case has no row
    for is left out. in_place adds reading into existing objects (Nino against MemoryPack's overwrite; v3 reads into the
    list it holds, its elements new: 3.3.13 reads no object in place, V3/PooledGraphTest.cs)."""
    def group(verb, key, title):
        rows = [("Nino v4", v4[f"Nino{verb}{key}"]["mean"])]
        if v3 is not None:
            rows.append(("Nino v3", v3.get(f"Nino{verb}{key}")))
        rows += [(name, v4[f"{other}{verb}{key}"]["mean"]) for other, _, name in OTHERS if f"{other}{verb}{key}" in v4]
        return f"{verb} · {title}", rows

    chart = [tuple(group(verb, key, title.replace("`", "")) for verb in ("Serialize", "Deserialize"))
             for key, title in cases if f"NinoSerialize{key}" in v4 and f"NinoDeserialize{key}" in v4]
    nino, memorypack = v4.get("NinoLeavesInPlace"), v4.get("MemoryPackLeavesOverwrite")
    if in_place and nino and memorypack:
        chart.append((None, (f"Into existing objects · Nino {allocated(nino)}, MemoryPack {allocated(memorypack)}",
                             [("Nino v4", nino["mean"])] +
                             ([("Nino v3", v3.get("NinoLeavesInPlace"))] if v3 is not None else []) +
                             [("MemoryPack", memorypack["mean"])])))
    return chart


def versus(other, v4):
    """A table cell: v4 against another time, by the rules above ("—": v4 is not ahead)."""
    r = against(other, v4)
    if r == "unmeasurable":
        return "**faster** (too fast to measure)"
    if on_par(r):
        return "on par"
    return f"**{r:.2f}× faster**" if r > 1 else "—"


SIMPLE_CASES = [("SimpleClass", "`SimpleClass`"), ("SimpleClasses", "`SimpleClass[100]`"),
                ("PolymorphicClass", "Polymorphic class"), ("PolymorphicClassArray", "Polymorphic class array"),
                ("PolymorphicFooArray", "Polymorphic `Foo[]`"), ("SimpleStruct", "`SimpleStruct` (`int` + `DateTime`)"),
                ("SimpleStructs", "`SimpleStruct[100]`"), ("Vectors", "`Vector4[10000]`"),
                ("NullableClass", "`NullableFields` (half null)"), ("NullableClasses", "`NullableFields[100]`"),
                ("NullableLongs", "`long?[1000]`, every fifth null"), ("NullableInts", "`List<int?>`, 1,000"),
                ("NullableDecimals", "`decimal?[1000]`")]
REUSE_CASES = [("Pooled node", "NinoNodeNew", "NinoNodePooled", "MemoryPackNodeNew", "MemoryPackNodeOverwrite"),
               ("Pooled leaves, in place", "NinoLeavesNew", "NinoLeavesInPlace", "MemoryPackLeavesNew",
                "MemoryPackLeavesOverwrite"),
               ("Pooled leaves, pooled elements", "NinoLeavesNew", "NinoLeavesPooledElements", "MemoryPackLeavesNew",
                "MemoryPackLeavesOverwrite"),
               ("Pooled class", "NinoDeserializeNew", "NinoDeserializePooled", "MemoryPackDeserializeNew",
                "MemoryPackDeserializeOverwrite")]
EXTRA_CASES = [("Graph", "Object graph, 200 tracked nodes"), ("TolerantClass", "`TolerantClass`"),
               ("TolerantClasses", "`TolerantClass[100]`"), ("FixedStruct", "`FixedStruct`"),
               ("FixedClass", "`FixedClass`"), ("FixedClasses", "`FixedClass[100]`"),
               ("Tree", "`TreeNode`, 1,365 nodes"),
               ("Inventory", "`InventoryItem[1000]` (padded struct)"),
               ("Waypoints", "`Waypoint[1000]` (fixed size, not raw)"),
               ("StringDictionary", "`Dictionary<string, string>`, 1,000")]


def table(head, rows):
    """A Markdown table on lines of its own between its markers."""
    return "\n" + "\n".join(["| " + " | ".join(head) + " |", "|---" + "|---:" * (len(head) - 1) + "|"] +
                      ["| " + " | ".join(r) + " |" for r in rows]) + "\n"


def tables(v4, v3, v3_runs, env, tag):
    """The runtime section of docs/v4-benchmarks.md: one table per marker, and the line saying where they come from."""
    def mean(method):
        return v4[method]["mean"] if method in v4 else None

    def maybe(method):
        return time(mean(method)) if method in v4 else "—"

    out = {}
    names = ["Nino"] + [o for o, _, _ in OTHERS]
    head = ["Nino"] + [name for _, _, name in OTHERS]
    for verb in ("Serialize", "Deserialize"):
        out[f"table.workloads.{verb.lower()}"] = table(
            ["Workload"] + head,
            [[label] + [time(mean(f"{n}{verb}{key}")) + ("" if n == "Nino" else
                        f" ({versus(mean(f'{n}{verb}{key}'), mean(f'Nino{verb}{key}'))})") for n in names]
             for key, label in WORKLOADS])
    out["table.workloads.payload"] = table(
        ["Workload"] + head,
        [[label] + [f"{v4[f'{n}Serialize{key}']['payload']}" for n in names] for key, label in WORKLOADS])
    # The other libraries' times, for reference, in OTHERS' order ("—" where a release did not run one): MemoryPack,
    # MessagePack and protobuf-net always, Google.Protobuf and System.Text.Json once a report has them in these cases
    # (earlier releases ran them in WorkloadTest only).
    simple = [(other, name) for other, _, name in OTHERS
              if other in ("MemoryPack", "MessagePack", "Protobuf") or
              any(f"{other}{verb}{key}" in v4 for verb in ("Serialize", "Deserialize") for key, _ in SIMPLE_CASES)]
    for verb in ("Serialize", "Deserialize"):
        rows = []
        for key, label in SIMPLE_CASES:
            nino, mp = mean(f"Nino{verb}{key}"), mean(f"MemoryPack{verb}{key}")
            if nino is None or mp is None:
                continue
            old = v3.get(f"Nino{verb}{key}")
            # v3 right after v4, then the libraries (the owner's order, as in the chart).
            rows.append([label, time(nino), time(old) if old is not None else "—"] +
                        [maybe(f"{other}{verb}{key}") for other, _ in simple] +
                        [versus(old, nino) if old is not None else "—", versus(mp, nino)])
        out[f"table.{verb.lower()}"] = table(["Case", "v4", "v3"] + [name for _, name in simple] +
                                             ["v4 vs v3", "v4 vs MemoryPack"], rows)

    rows = []
    for label, new, reuse, mp_new, overwrite in REUSE_CASES:
        if all(m in v4 for m in (new, reuse, mp_new, overwrite)):
            rows.append([label, time(mean(new)), f"**{time(mean(reuse))}** ({allocated(v4[reuse])})", time(mean(mp_new)),
                         f"{time(mean(overwrite))} ({allocated(v4[overwrite])})", versus(mean(overwrite), mean(reuse))])
    out["table.reuse"] = table(["Case", "Nino new", "Nino reusing", "MemoryPack new", "MemoryPack overwrite",
                                "Nino reusing vs overwrite"], rows)

    rows = []
    for key, label in EXTRA_CASES:
        cells = [label]
        for verb in ("Serialize", "Deserialize"):
            nino, mp = mean(f"Nino{verb}{key}"), mean(f"MemoryPack{verb}{key}")
            cells += [time(nino), time(mp), versus(mp, nino)] if nino is not None and mp is not None else ["—"] * 3
        if cells[1:] != ["—"] * 6:
            rows.append(cells)
    out["table.extra"] = table(["Case", "Serialize v4", "MemoryPack", "v4 vs MemoryPack", "Deserialize v4",
                                "MemoryPack", "v4 vs MemoryPack"], rows)

    bdn, runtime, cpu = environment(env)
    source = f"the performance report of [{tag}](https://github.com/JasonXuDeveloper/Nino/releases/tag/{tag})" \
        if tag else "a local `benchmarks/run.sh` run"
    out["source"] = (f"{source}: BenchmarkDotNet {bdn}, {runtime}, {cpu}; v3 3.3.13 from NuGet ran "
                     f"{('directly before v4' if v3_runs == 1 else 'directly before and after v4') if v3 else 'not at all'} on the same machine")
    return out


def build(path, tag=None):
    """The build-time markers from a release body's build section (generator_report.py): the README's Build row of v4
    vs v3 (from the workload with the most types) and, per workload, docs/v4-benchmarks.md's table and its source."""
    found = generator_report.parse(Path(path).read_text(encoding="utf-8"))
    if found is None:
        sys.exit(f"no build section ({generator_report.HEADING}) in {path}")
    out = {f"doc.build.{t['roots']}": "\n" + t["table"] + "\n" for t in found["tables"]}
    source = f"the build section of [{tag}](https://github.com/JasonXuDeveloper/Nino/releases/tag/{tag})" \
        if tag else "a local run"
    out["doc.build.source"] = f"{source}: {found['environment']}"

    largest = found["tables"][-1]
    types = f"{largest['types']:,}"
    v3_emit, v4_emit, emit = largest["rows"]["emit"]
    smaller = re.match(r"\*\*([\d.]+)× smaller\*\*$", largest["rows"]["source"][2])
    faster = re.match(r"\*\*([\d.]+)× faster\*\*$", emit)
    zh_v3, zh_v4 = (t.replace(" s", " 秒") for t in (v3_emit, v4_emit))
    if faster:
        en = [f"generated code compiles **{faster.group(1)}× faster** at {types} types ({v3_emit} → {v4_emit})"]
        zh = [f"{types} 个类型时生成代码的编译**快 {faster.group(1)} 倍**（{zh_v3} → {zh_v4}）"]
    else:  # never written as slower
        en = [f"generated code compiles in {v4_emit} at {types} types (v3: {v3_emit})"]
        zh = [f"{types} 个类型时生成代码编译用时 {zh_v4}（v3 为 {zh_v3}）"]
    if smaller:
        en.append(f"**{smaller.group(1)}× smaller** source")
        zh.append(f"源码**小 {smaller.group(1)} 倍**")
    out["v3.build"] = ("; ".join(en), "；".join(zh))
    return out


def fill(path, values, zh):
    text = path.read_text(encoding="utf-8")
    seen = set()

    def replace(match):
        key = match.group(1)
        if key not in values:
            return match.group(0)  # e.g. no v3 report given: keep what is there
        seen.add(key)
        value = values[key]
        if isinstance(value, tuple):
            value = value[1] if zh else value[0]
        return f"<!--bench:{key}-->{value}<!--/bench-->"

    text = re.sub(r"<!--bench:([\w.]+)-->.*?<!--/bench-->", replace, text, flags=re.S)
    path.write_text(text, encoding="utf-8")
    return seen


def from_assets(directory):
    """The --v4, --v3 and --build files of a directory of release assets (release_assets.py: the manifest and the
    files it lists), each only when the directory has it: report.yml's jobs hold only the assets they add."""
    manifest = release_assets.load(directory)
    present = {e["kind"]: os.path.join(directory, e["name"]) for e in manifest["assets"]
               if e.get("arch") == "x64" and os.path.isfile(os.path.join(directory, e["name"]))}
    return present.get("dotnet"), [present["dotnet-v3"]] if "dotnet-v3" in present else [], present.get("build"), \
        manifest.get("tag")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--v4")
    parser.add_argument("--v3", action="append", default=[])
    parser.add_argument("--build")
    parser.add_argument("--assets", help="a directory of release assets (release_assets.py) instead of the files")
    parser.add_argument("--release", help="a release's assets, downloaded (gh) instead of the files")
    parser.add_argument("--tag")
    parser.add_argument("--png", action="store_true", help="render benchmark.png and benchmark-dark.png too")
    parser.add_argument("--chrome", help="Chrome or Chromium for --png (default: $CHROME, google-chrome)")
    args = parser.parse_args()
    if args.release:
        args.assets = tempfile.mkdtemp(prefix="nino-assets-")
        if release_assets.fetch(release_assets.release(args.release), args.assets,
                                ["dotnet-x64.md", "dotnet-v3-x64.md", "build-x64.md"]) is None:
            sys.exit(f"{args.release} has no {release_assets.MANIFEST}")
    if args.assets:
        args.v4, args.v3, args.build, tag = from_assets(args.assets)
        args.tag = args.tag or tag
    if not args.v4 and not args.build:
        parser.error("give --v4, --build, both, or --assets/--release with either")

    values = {}
    if args.v4:
        values, chart = numbers(args.v4, args.v3, args.tag)
        for theme, suffix in (("light", ""), ("dark", "-dark")):
            image, height = svg(chart, theme)
            path = ROOT / f"docs/assets/benchmark{suffix}.svg"
            path.write_text(image, encoding="utf-8")
            if args.png:
                png(str(path), args.chrome)
        print(f"chart height {height}")
    if args.build:
        values.update(build(args.build, args.tag))
    for name, zh in (("README.md", False), ("README.zh.md", True), ("docs/v4-benchmarks.md", False)):
        keys = {k for k in values if k.startswith("doc.") == (name.startswith("docs/"))}
        missing = keys - fill(ROOT / name, {k: values[k] for k in keys}, zh)
        if missing:
            print(f"warning: {name} has no marker for {', '.join(sorted(missing))}", file=sys.stderr)
    for key, value in values.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
