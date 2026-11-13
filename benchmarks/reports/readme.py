#!/usr/bin/env python3
"""Write the benchmark numbers of README.md / README.zh.md, the README chart and the runtime and build tables of
docs/v4-benchmarks.md from BenchmarkDotNet reports and the release body's build section.

usage: python3 benchmarks/reports/readme.py [--v4 <joined report-github.md> [--v3 <v3 report-github.md>]...
                                    [--png-html <file>]] [--build <section.md>] [--tag <tag>]

  --v4        the joined report of benchmarks/Nino.Benchmark (every class in one table), as benchmarks/run.sh and
              report.yml produce it
  --v3        a report of the v3 run (Nino 3.3.13, SimpleTest); repeat it for several runs,
              v3 is then the mean of them. Without it the README's v3 runtime numbers are left as they are.
  --build     the build-time section of a release body (generator_report.py; the whole body will do): the README's
              Build row of v4 vs v3 (v3.build) and the build tables of docs/v4-benchmarks.md (doc.build.*)
  --tag       the release the reports belong to (report.yml); without it docs/v4-benchmarks.md names a local run
  --png-html  also write the chart wrapped in an HTML page (and <name>-dark.html for the dark theme), to render
              benchmark.png and benchmark-dark.png from them (report.yml does
              this with headless Chrome: README.md is packed into the NuGet packages, and nuget.org shows no SVG)

The README carries each generated number between markers: <!--bench:key-->5.1<!--/bench-->; docs/v4-benchmarks.md
carries whole tables between such markers. Everything outside the markers is written by hand. --v4 and --build fill
their own markers and leave the others as they are (report.yml runs them in different jobs). The headline numbers come from WorkloadTest's workloads: the ranges of how many times faster
Nino is than MemoryPack (ser.range, deser.range), MessagePack (msgpack.*), protobuf-net (protobuf.*), Google.Protobuf
(gprotobuf.*) and System.Text.Json (json.*), and the chart (light and dark), the geometric mean over them of each
library's time against Nino's. Ratios follow the docs site (nino.docs src/lib/benchmarks.ts): within 5% is "on
par", and a case where v4 is slower is left out of a range, never shown as slower; a mean within 5% has no bar.
A time under FLOOR_NS is below what BenchmarkDotNet can tell apart from an empty method: v4 under it against a
measurable v3 is "faster, too fast to measure" (no ratio: it would only reflect the floor); both under it is on par.
"""
import argparse
import math
import re
import sys
from pathlib import Path

import generator_report
from bdn import FLOOR_NS, mean_ns, rows
from bdn import cpu as short_cpu

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
# v3 runs SimpleTest only (3.3.13 cannot express the workloads): the chart's first group, these cases' geometric mean.
V3_CASES = ["SimpleClass", "SimpleClasses", "SimpleStruct", "SimpleStructs"]


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


def on_par(r):
    return round(abs(r - 1) * 100) <= 5


def against(slow, fast):
    """v3 (slow) against v4 (fast): "unmeasurable" when v4 is under FLOOR_NS and v3 is not, 1.0 when both are, else
    the ratio."""
    if fast < FLOOR_NS:
        return 1.0 if slow < FLOOR_NS else "unmeasurable"
    return 0.0 if slow < FLOOR_NS else ratio(slow, fast)


def one(r):
    """One decimal below 10 (7.2), whole numbers from 10 (23)."""
    return f"{r:.1f}" if r < 10 else f"{r:.0f}"


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

    # The chart: how many times faster Nino v4 is, as (title, [(label, colour, times)]): first v3 (an upgrade),
    # then each library, the geometric mean over the workloads (one Nino does not beat by more than 5% has no bar),
    # and into existing objects against MemoryPack's overwrite.
    chart = []
    v3 = {}
    if v3_paths:
        # How v3 ran next to v4 (README's environment line): once before it, or before and after it (older reports).
        out["v3.when"] = ("directly before it", "紧接其前跑一次") if len(v3_paths) == 1 else \
            ("directly before and after it", "紧接其前后各跑一次")
        runs = [parse(p)[1] for p in v3_paths]
        for method in runs[0]:
            if method.startswith("Nino") and all(method in run for run in runs):
                v3[method] = sum(run[method]["mean"] for run in runs) / len(runs)
    upgrade = []
    for verb in ("Serialize", "Deserialize"):
        # A case too fast to measure in v4 has no ratio (it would only reflect the floor): left out of the mean.
        times = [r for k in V3_CASES if f"Nino{verb}{k}" in v3 and f"Nino{verb}{k}" in v4
                 for r in [against(v3[f"Nino{verb}{k}"], v4[f"Nino{verb}{k}"]["mean"])] if isinstance(r, float) and r > 0]
        if times:
            mean = math.exp(sum(map(math.log, times)) / len(times))
            if mean > 1 and not on_par(mean):
                upgrade.append((f"{verb.lower()}", "Nino v3", mean))
    if upgrade:
        chart.append((f"vs Nino v3 3.3.13 · {len(V3_CASES)} SimpleTest cases (geometric mean)", upgrade))
    for verb in ("Serialize", "Deserialize"):
        bars = []
        for other, _, name in OTHERS:
            mean = math.exp(sum(math.log(v4[f"{other}{verb}{k}"]["mean"] / v4[f"Nino{verb}{k}"]["mean"])
                                for k in WORKLOAD_KEYS) / len(WORKLOAD_KEYS))
            if mean > 1 and not on_par(mean):
                bars.append((f"vs {name}", name, mean))
        chart.append((f"{verb} · {len(WORKLOAD_KEYS)} workloads (geometric mean)", bars))
    for title, nino, memorypack in [("pooled node", "NinoNodePooled", "MemoryPackNodeOverwrite"),
                                    ("leaves in place", "NinoLeavesInPlace", "MemoryPackLeavesOverwrite")]:
        chart.append((f"Deserialize into existing objects · {title} (Nino {allocated(v4[nino])}, "
                      f"MemoryPack {allocated(v4[memorypack])})",
                      [("vs MemoryPack", "MemoryPack", v4[memorypack]["mean"] / v4[nino]["mean"])]))

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


def time(ns):
    """As the docs site writes times: 151 ns, 17.0 µs, 1.23 ms; under FLOOR_NS, "< 1 ns"."""
    if ns < FLOOR_NS:
        return f"< {FLOOR_NS} ns"
    for unit, scale in (("ns", 1), ("µs", 1e3), ("ms", 1e6), ("s", 1e9)):
        if ns < scale * 1e3 or unit == "s":
            value = ns / scale
            text = f"{value:,.0f}" if value >= 100 else f"{value:.1f}" if value >= 10 else f"{value:.2f}"
            return f"{text} {unit}"


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
                ("SimpleStructs", "`SimpleStruct[100]`"), ("Vectors", "`Vector4[10000]`")]
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
    for verb in ("Serialize", "Deserialize"):
        rows = []
        for key, label in SIMPLE_CASES:
            nino, mp = mean(f"Nino{verb}{key}"), mean(f"MemoryPack{verb}{key}")
            if nino is None or mp is None:
                continue
            old = v3.get(f"Nino{verb}{key}")
            # MessagePack and protobuf-net: their times, for reference ("—" where a release did not run them).
            rows.append([label, time(nino), time(mp), maybe(f"MessagePack{verb}{key}"), maybe(f"Protobuf{verb}{key}"),
                         time(old) if old is not None else "—", versus(mp, nino),
                         versus(old, nino) if old is not None else "—"])
        out[f"table.{verb.lower()}"] = table(["Case", "v4", "MemoryPack", "MessagePack", "protobuf-net", "v3",
                                              "v4 vs MemoryPack", "v4 vs v3"], rows)

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


# The bar colours of the chart's libraries (OTHERS) and of v3.
BAR_COLORS = {"Nino v3": "#8c96a8", "MemoryPack": "#e3a03b", "MessagePack": "#2fa88f", "Google.Protobuf": "#4c8bf5",
              "protobuf-net": "#d9645c", "System.Text.Json": "#9a6dd7"}
# Colours of the two themes (README picks one with <picture> and prefers-color-scheme). Each image paints its own
# background (GitHub's page colours): prefers-color-scheme follows the reader's system, not the page, so a dark system
# shows the dark image on nuget.org's or GitHub's light page, and on a transparent background its light text vanished.
THEMES = {"light": {"text": "#1f2328", "muted": "#59636e", "grid": "#d1d9e0", "background": "#ffffff"},
          "dark": {"text": "#f0f6fc", "muted": "#9198a1", "grid": "#3d444d", "background": "#0d1117"}}
PAD = 20  # the background's margin around the chart


def svg(chart, theme="light"):
    """The chart as SVG: per case, a bar per library, its length the log of how many times faster Nino v4 is."""
    c = THEMES[theme]
    top = max(v for _, bars in chart for _, _, v in bars)
    decades = max(1, math.ceil(math.log10(top) - 1e-9))
    x0, width = 140, 580  # room for "vs System.Text.Json"
    scale = width / decades

    def x(v):
        return x0 + max(3.0, math.log10(v) * scale)

    out, y = [], 96
    for d in range(decades + 1):  # the scale, once above the first case
        out.append(f'<text x="{x0 + d * scale:.1f}" y="{y - 22}" class="s" text-anchor="middle">{10 ** d}×</text>')
    for title, bars in chart:
        if not bars:
            continue
        out.append(f'<text x="0" y="{y}" class="g">{title}</text>')
        by = y + 22
        for d in range(decades + 1):  # grid lines behind this case's bars only, never through its title
            gx = x0 + d * scale
            out.append(f'<line x1="{gx:.1f}" y1="{by - 16}" x2="{gx:.1f}" y2="{by + 20 * len(bars) - 16}" '
                       f'stroke="{c["grid"]}" stroke-dasharray="3 3"/>')
        for label, colour, v in bars:
            out.append(f'<text x="{x0 - 10}" y="{by}" class="l" text-anchor="end">{label}</text>')
            out.append(f'<rect x="{x0}" y="{by - 12}" width="{x(v) - x0:.1f}" height="15" rx="3" fill="{BAR_COLORS[colour]}"/>')
            label = f"{v:.2f}×" if v < 10 else f"{v:.0f}×"
            out.append(f'<text x="{x(v) + 8:.1f}" y="{by}" class="v">{label}</text>')
            by += 20
        y = by + 18
    height = y - 8 + 2 * PAD
    width = 820 + 2 * PAD
    head = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif">
<rect width="{width}" height="{height}" rx="8" fill="{c["background"]}"/>
<g transform="translate({PAD} {PAD})">
<style>.t{{font-size:18px;font-weight:700;fill:{c["text"]}}}.s{{font-size:12px;fill:{c["muted"]}}}.g{{font-size:13px;font-weight:600;fill:{c["text"]}}}.l{{font-size:12px;fill:{c["muted"]}}}.v{{font-size:12px;font-weight:700;fill:{c["text"]}}}</style>
<text x="0" y="22" class="t">How many times faster Nino v4 is (log scale)</text>
<text x="0" y="44" class="s">BenchmarkDotNet, .NET 10, server GC, one run on one machine; 12 real-world workloads, each library's fastest API.</text>
'''
    return head + "\n".join(out) + "\n</g>\n</svg>\n", height


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


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--v4")
    parser.add_argument("--v3", action="append", default=[])
    parser.add_argument("--build")
    parser.add_argument("--tag")
    parser.add_argument("--png-html")
    args = parser.parse_args()
    if not args.v4 and not args.build:
        parser.error("give --v4, --build or both")

    values = {}
    if args.v4:
        values, chart = numbers(args.v4, args.v3, args.tag)
        for theme, suffix in (("light", ""), ("dark", "-dark")):
            image, height = svg(chart, theme)
            (ROOT / f"docs/assets/benchmark{suffix}.svg").write_text(image, encoding="utf-8")
            if args.png_html:
                html = Path(args.png_html)
                html.with_name(html.stem + suffix + html.suffix).write_text(
                    f'<html><body style="margin:0;background:transparent">{image}</body></html>', encoding="utf-8")
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
