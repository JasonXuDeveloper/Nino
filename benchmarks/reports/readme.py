#!/usr/bin/env python3
"""Write the benchmark numbers and charts of README.md / README.zh.md, and the charts and the runtime and build tables
of docs/v4-benchmarks.md from a release's assets (release_assets.py) or from BenchmarkDotNet reports and a build section.

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
  --png       also render the overview chart benchmark.png with headless Chrome (charts.py; the NuGet packages'
              README, src/nuget-readme.md, shows it by its raw URL on main: nuget.org shows no SVG and no HTML)

The README carries each generated number between markers: <!--bench:key-->5.1<!--/bench-->; docs/v4-benchmarks.md
carries whole tables between such markers. Everything outside the markers is written by hand. --v4 and --build fill
their own markers and leave the others as they are (report.yml runs them in different jobs). The headline numbers come from WorkloadTest's workloads: the ranges of how many times faster
Nino is than MemoryPack (ser.range, deser.range), MessagePack (msgpack.*), protobuf-net (protobuf.*), Google.Protobuf
(gprotobuf.*) and System.Text.Json (json.*). The charts (chart_groups: one per group of GROUPS, a light and a
dark SVG in docs/assets, between the charts and doc.charts markers) show each serializer's time per operation in
every case, v3 n/a where it did not run; the overview chart (overview_chart: CHART_CASES) is the image for nuget.org. Ratios follow the docs site (nino.docs src/lib/benchmarks.ts): within 5% is "on
par", and a case where v4 is slower is left out of a range, never shown as slower.
A time under FLOOR_NS is below what BenchmarkDotNet can tell apart from an empty method: v4 under it against a
measurable time is faster by more than that time / FLOOR_NS (bdn.lower_bound: "> 1.4× faster"), since v4 took less
than the floor; both under it is on par. A range (ser.range, ...) leaves such a case out: its ends are measured ratios.
Every multiple is written by bdn.times: one decimal below 10, a whole number from 10, rounded half up ("1.1–1.4",
one value when both ends round alike).
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
from bdn import FLOOR_NS, lower_bound, mean_ns, on_par, rows, times
from bdn import cpu as short_cpu
from charts import README_TITLE, details, picture, png, svg, time

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
# 100 of them, its SimpleStruct?[100] with every fifth null, and CollectionsTest's long?[] with every fifth null.
NULLABLE_CASES = [("NullableFieldsArray", "100 objects with nullable fields"),
                  ("NullableStructs", "100 structs?, every fifth null"), ("NullableLongs", "long?[] with nulls")]
# The overview chart's cases (the owner's choice; the image nuget.org shows), each serialized and deserialized: three
# workloads, then what Nino is built for: a block of raw structs (one vector copy), a polymorphic array, nullable
# values, and reading into existing objects.
CHART_CASES = [("MediaContent", "MediaContent"), ("RpcCall", "RPC call, interface result"),
               ("LogBatch", "log batch, 1,000 events"), ("SimpleStructs", "100 structs, one block copy"),
               ("PolymorphicClassArray", "polymorphic class array")] + NULLABLE_CASES


def parse(path):
    """-> (environment lines, {method: row}); a row is {"mean": ns, "payload": str, "allocated": str, "type": its
    benchmark class, or None in a report of one class}."""
    before, table = rows(path)
    env = re.search(r"```\s*\n(.*?)```", "\n".join(before) + "\n", re.S)
    out = {}
    for row in table:
        method, mean = row.get("Method", ""), mean_ns(row.get("Mean", ""))
        if method and mean is not None:
            out[method] = {"mean": mean, "payload": row.get("Payload", "-"), "allocated": row.get("Allocated", "-"),
                           "type": row.get("Type")}
    if not out:
        sys.exit(f"no benchmark rows found in {path}")
    return (env.group(1).strip().splitlines() if env else []), out


def ratio(slow, fast):
    """Unrounded: times() rounds once, where a multiple is written."""
    return slow / fast


def against(slow, fast):
    """v3 (slow) against v4 (fast): "unmeasurable" when v4 is under FLOOR_NS and v3 is not (the callers write the
    lower bound, lower_bound(slow)), 1.0 when both are, else the ratio."""
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
        """Ratios of the workloads v4 wins: above 1, and not on par; a workload v4 ran under FLOOR_NS has no ratio."""
        return [r for r in (vs(other, verb, k) for k in WORKLOAD_KEYS if v4[f"Nino{verb}{k}"]["mean"] >= FLOOR_NS)
                if r > 1 and not on_par(r)]

    def win_range(verb, other="MemoryPack"):
        won = wins(verb, other)
        if not won:
            return "—"
        low, high = times(min(won)), times(max(won))
        return low if low == high else f"{low}–{high}"

    bdn, runtime, cpu = environment(env)
    out = {
        "reuse.inplace": times(ratio(v4["MemoryPackLeavesOverwrite"]["mean"], v4["NinoLeavesInPlace"]["mean"])),
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
    # Without a v3 report the charts leave v3 out rather than show it n/a in every case.
    chart = overview_chart(v4, v3 if v3_paths else None), chart_groups(v4, v3 if v3_paths else None)

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
                    bound = lower_bound(v3[method])
                    en.append(f"**> {times(bound)}× faster** ({label_en}, v4 under {FLOOR_NS} ns)")
                    zh.append(f"{label_zh}**快 {times(bound)} 倍以上**（v4 不到 {FLOOR_NS} ns）")
                elif on_par(r):
                    en.append(f"on par ({label_en})")
                    zh.append(f"{label_zh}持平")
                elif r > 1:
                    en.append(f"**{times(r)}× faster** ({label_en})")
                    zh.append(f"{label_zh}**快 {times(r)} 倍**")
            out[f"v3.{op}"] = (", ".join(en), "，".join(zh))
    out.update({f"doc.{k}": v for k, v in tables(v4, v3, len(v3_paths or []), env, tag).items()})
    return out, chart


# The chart groups (the owner's choice): README.md, README.zh.md, docs/v4-benchmarks.md and each .NET section of a
# release body draw one chart per group, of every case of its benchmark classes; the docs site groups its interactive
# charts alike (nino.docs src/lib/benchmarks.ts). (id, title, Chinese title, classes); CollectionsTest's nullable
# collections are in "nullable", a case of a class no group names in "others".
GROUPS = [("workloads", "Real workloads", "真实场景", ["WorkloadTest"]),
          ("basic", "Basic types and structs", "基础类型与结构体", ["SimpleTest", "FixedSizeTest"]),
          ("polymorphism", "Polymorphism", "多态", ["PolymorphicTest", "GenericPolymorphicTest"]),
          ("nullable", "Nullable values", "可空值", ["NullableTest"]),
          ("collections", "Collections and common types", "集合与常用类型",
           ["CollectionsTest", "CommonTypesTest", "BclTypesTest"]),
          ("reuse", "Reuse and pooling", "复用与对象池", ["PooledTest", "PooledGraphTest"]),
          ("tracking", "Reference tracking and version tolerance", "引用跟踪与版本容错",
           ["ReferenceTest", "VersionTolerantTest"]),
          ("others", "Others", "其他", ["ScatteredTest", "UntrustedTest", "ExternalBufferTest"])]
# Reading new objects against reading into existing ones (PooledTest, PooledGraphTest), one row each: (case, the
# method of new objects, Nino's of existing ones, MemoryPack's overwrite; without the library prefix).
REUSE_ROWS = [("pooled class", "DeserializeNew", "DeserializePooled", "DeserializeOverwrite"),
              ("pooled node", "NodeNew", "NodePooled", "NodeOverwrite"),
              ("leaves", "LeavesNew", "LeavesInPlace", "LeavesOverwrite"),
              ("leaves, pooled elements", None, "LeavesPooledElements", "LeavesOverwrite"),
              ("plain list", "PlainNew", None, None)]
VERBS = ("Serialize", "Deserialize")
# Method prefixes, the longest first (GoogleProtobuf before Protobuf, which it starts with).
PREFIXES = sorted(["Nino"] + [o for o, _, _ in OTHERS], key=len, reverse=True)


def split_method(method):
    """NinoSerializeSimpleClass -> ("Nino", "SerializeSimpleClass"); (None, method) without a library prefix."""
    lib = next((p for p in PREFIXES if method.startswith(p)), None)
    return (lib, method[len(lib):]) if lib else (None, method)


def group_of(cls, key):
    """The id of the group a case of a class belongs to."""
    if cls == "CollectionsTest" and key.startswith("Nullable"):
        return "nullable"
    return next((gid for gid, _, _, classes in GROUPS if cls in classes), "others")


def case_label(key):
    """A case's name in a chart: README's label where it has one, else its key."""
    labels = dict(SIMPLE_CASES + EXTRA_CASES)
    return labels.get(key, key).replace("`", "")


def bars(v4, v3, method_of, first="Nino v4"):
    """[(name, ns or None)] of one operation: Nino v4, v3 (None: n/a) unless v3 is None, then every library that has
    the method; method_of(library prefix) -> the method or None. None when Nino v4 has no such method."""
    nino = method_of("Nino")
    if nino not in v4:
        return None
    out = [(first, v4[nino]["mean"])]
    if v3 is not None:
        out.append(("Nino v3", v3.get(nino)))
    for other, _, name in OTHERS:
        method = method_of(other)
        if method and method in v4:
            out.append((name, v4[method]["mean"]))
    return out


def reuse_chart(v4, v3):
    """The reuse group: per row of REUSE_ROWS, new objects (every library) beside reading into existing ones (Nino
    against MemoryPack's overwrite, each bar with what it allocates; v3 3.3.13 reads its leaves into the list it
    holds, its elements new: V3/PooledGraphTest.cs)."""
    chart = []
    for case, new, reuse, overwrite in REUSE_ROWS:
        left = bars(v4, v3, lambda lib: f"{lib}{new}") if new else None
        right = None
        if reuse and f"Nino{reuse}" in v4:
            def bar(name, row):
                return name, row["mean"], f"{time(row['mean'])}, {allocated(row)}"
            rows = [bar("Nino v4", v4[f"Nino{reuse}"])]
            if v3 is not None:
                rows.append(("Nino v3", v3.get(f"Nino{reuse}")))
            if f"MemoryPack{overwrite}" in v4:
                rows.append(bar("MemoryPack", v4[f"MemoryPack{overwrite}"]))
            right = (f"Into existing objects · {case}", rows)
        if left or right:
            chart.append((("New objects · " + case, left) if left else None, right))
    return chart


def pack(chart):
    """A chart's rows with the cases that have one operation two to a row, after the cases with both, so no half of a
    row is left empty (each panel's title names its operation)."""
    full = [pair for pair in chart if None not in pair]
    single = [next(p for p in pair if p is not None) for pair in chart if None in pair]
    return full + [tuple(single[i:i + 2]) + (None,) * (2 - len(single[i:i + 2])) for i in range(0, len(single), 2)]


def chart_groups(v4, v3):
    """The charts of GROUPS -> [(id, title, Chinese title, chart)] (charts.svg), each group with at least one case: per
    case a row of serialize and deserialize (either None when the case has only the other), each the time of Nino v4,
    v3 3.3.13 (n/a where it did not run the case; v3=None leaves its bars out: Arm64) and every library the case has a
    row for. Cases in a known order (the workloads, README's cases, the rest), then the report's."""
    order = WORKLOAD_KEYS + [k for k, _ in SIMPLE_CASES] + [k for k, _ in EXTRA_CASES]
    keys = {}
    for method, row in v4.items():
        lib, rest = split_method(method)
        verb = next((v for v in VERBS if rest.startswith(v)), None)
        if lib != "Nino" or verb is None or group_of(row.get("type"), "") == "reuse":
            continue
        key = rest[len(verb):]
        keys.setdefault(group_of(row.get("type"), key), {})[key] = None
    out = []
    for gid, title, title_zh, _ in GROUPS:
        if gid == "reuse":
            chart = reuse_chart(v4, v3)
        else:
            found = list(keys.get(gid, {}))
            chart = []
            for key in sorted(found, key=lambda k: order.index(k) if k in order else len(order) + found.index(k)):
                pair = tuple((f"{verb} · {case_label(key)}", rows) if rows else None
                             for verb in VERBS
                             for rows in [bars(v4, v3, lambda lib, verb=verb: f"{lib}{verb}{key}")])
                chart.append(pair)
        if chart:
            out.append((gid, title, title_zh, pack(chart)))
    return out


def overview_chart(v4, v3, cases=CHART_CASES, in_place=True):
    """The overview chart (charts.svg; docs/assets/benchmark.png, for nuget.org): per case, a row of two groups, serialize and deserialize; a group is (title,
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
        return f"**> {times(lower_bound(other))}× faster** (v4 under {FLOOR_NS} ns)"
    if on_par(r):
        return "on par"
    return f"**{times(r)}× faster**" if r > 1 else "—"


SIMPLE_CASES = [("SimpleClass", "`SimpleClass`"), ("SimpleClasses", "`SimpleClass[100]`"),
                ("PolymorphicClass", "Polymorphic class"), ("PolymorphicClassArray", "Polymorphic class array"),
                ("PolymorphicFooArray", "Polymorphic `Foo[]`"), ("SimpleStruct", "`SimpleStruct` (`int` + `DateTime`)"),
                ("SimpleStructs", "`SimpleStruct[100]`"), ("Vectors", "`Vector4[10000]`"),
                ("NullableFields", "`NullableFields` (half null)"), ("NullableFieldsArray", "`NullableFields[100]`"),
                ("NullableStruct", "`SimpleStruct?`"), ("NullableStructs", "`SimpleStruct?[100]`, every fifth null"),
                ("NullableObject", "`SimpleClass` reference, null"),
                ("NullableObjects", "`SimpleClass[100]`, every fifth null"),
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


ASSETS = "https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets"


def write_charts(overview, groups, render_png=False, chrome=None):
    """Writes docs/assets/benchmark-<group>.svg and -dark.svg per chart group, and the overview chart
    benchmark.svg (and with render_png benchmark.png, the one light image of the NuGet packages' README,
    src/nuget-readme.md: nuget.org shows no SVG and no HTML). -> the "charts" (README.md, README.zh.md) and "doc.charts"
    (docs/v4-benchmarks.md) markers: a <details> per group (README opens the first, the docs every one), its chart a <picture> of both themes (the first
    group's fallback image is the overview PNG)."""
    image, _ = svg(overview, "light")
    path = ROOT / "docs/assets/benchmark.svg"
    path.write_text(image, encoding="utf-8")
    if render_png:
        png(str(path), chrome)
    en, zh, doc = [], [], []
    for i, (gid, title, title_zh, chart) in enumerate(groups):
        for theme, suffix in (("light", ""), ("dark", "-dark")):
            image, _ = svg(chart, theme, f"{title}: {README_TITLE[0].lower()}{README_TITLE[1:]}")
            (ROOT / f"docs/assets/benchmark-{gid}{suffix}.svg").write_text(image, encoding="utf-8")
        light, dark = f"{ASSETS}/benchmark-{gid}.svg", f"{ASSETS}/benchmark-{gid}-dark.svg"
        fallback = f"{ASSETS}/benchmark.png" if i == 0 else None
        alt = f"{title}: time per operation of Nino v4, v3 and every library, serialize and deserialize"
        # README opens only the first group, so the page stays short; docs/v4-benchmarks.md opens them all.
        en.append(details(title, picture(light, dark, alt, fallback), i == 0))
        doc.append(details(title, picture(light, dark, alt, fallback), True))
        zh.append(details(title_zh, picture(light, dark, f"{title_zh}：Nino v4、v3 与各库每次操作的耗时，序列化与反序列化",
                                            fallback), i == 0))
    join = lambda parts: "\n" + "\n\n".join(parts) + "\n"
    return {"charts": (join(en), join(zh)), "doc.charts": join(doc)}


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
        args.v4, args.v3, build_asset, tag = from_assets(args.assets)
        args.build = args.build or build_asset  # a --build given beside --assets wins (a section not attached)
        args.tag = args.tag or tag
    if not args.v4 and not args.build:
        parser.error("give --v4, --build, both, or --assets/--release with either")

    values = {}
    if args.v4:
        values, (overview, groups) = numbers(args.v4, args.v3, args.tag)
        values.update(write_charts(overview, groups, args.png, args.chrome))
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
