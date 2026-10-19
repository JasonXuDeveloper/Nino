#!/usr/bin/env python3
"""Write the benchmark numbers of README.md / README.zh.md, the README chart and the runtime tables of
docs/v4-benchmarks.md from BenchmarkDotNet reports.

usage: python3 benchmarks/readme.py --v4 <joined report-github.md> [--v3 <v3 report-github.md>]... [--tag <tag>]
                                    [--png-html <file>]

  --v4        the joined report of src/Nino.Benchmark (every class in one table), as benchmarks/run.sh and
              report.yml produce it
  --v3        a report of benchmarks/v3 (Nino 3.3.13, SimpleTest); repeat it for several runs,
              v3 is then the mean of them. Without it the README's v3 runtime numbers are left as they are.
  --tag       the release the reports belong to (report.yml); without it docs/v4-benchmarks.md names a local run
  --png-html  also write the chart wrapped in an HTML page, to render benchmark.png from it (report.yml does
              this with headless Chrome: README.md is packed into the NuGet packages, and nuget.org shows no SVG)

The README carries each generated number between markers: <!--bench:key-->5.1<!--/bench-->; docs/v4-benchmarks.md
carries whole tables between such markers. Everything outside the markers (the build-time numbers among it) is
written by hand. Ratios follow the docs site (nino.docs
src/lib/benchmarks.ts): within 5% is "on par", and a case where v4 is slower is left out, never shown as slower.
A time under FLOOR_NS is below what BenchmarkDotNet can tell apart from an empty method: v4 under it against a
measurable v3 is "faster, too fast to measure" (no ratio: it would only reflect the floor); both under it is on par.
"""
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_NS = {"ns": 1.0, "μs": 1e3, "µs": 1e3, "us": 1e3, "ms": 1e6, "s": 1e9}
# Struct cases: a block copy in both libraries, so they stay out of the class/polymorphic ranges.
STRUCT_KEYS = {"SimpleStruct", "SimpleStructs", "Vectors"}
# About three clock cycles. Below it, a case cannot be told apart from another: a struct read of 40 bytes measured
# anywhere from 0.05 to 0.8 ns in Nino and MemoryPack alike, as BenchmarkDotNet subtracts its empty-method overhead.
FLOOR_NS = 1
CLASS_KEYS = ["SimpleClass", "SimpleClasses", "PolymorphicClass", "PolymorphicClassArray", "PolymorphicFooArray"]


def parse(path):
    """-> (environment lines, {method: row}); a row is {"mean": ns, "payload": str, "allocated": str}."""
    text = Path(path).read_text(encoding="utf-8")
    env = re.search(r"```\s*\n(.*?)```", text, re.S)
    rows, header = {}, None
    for line in text.splitlines():
        line = line.strip()
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if header is None:
            if "Method" in cells and "Mean" in cells:
                header = cells
            continue
        row = dict(zip(header, cells))
        method = row.get("Method", "").strip("*")
        mean = re.match(r"([\d,.]+)\s*(\S+)", row.get("Mean", "").strip("*"))
        if not method or not mean:
            continue  # separator or blank group row
        rows[method] = {
            "mean": float(mean.group(1).replace(",", "")) * UNITS_NS[mean.group(2)],
            "payload": row.get("Payload", "-"),
            "allocated": row.get("Allocated", "-"),
        }
    if not rows:
        sys.exit(f"no benchmark rows found in {path}")
    return (env.group(1).strip().splitlines() if env else []), rows


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
    return f"{r:.1f}"


def allocated(row):
    """BDN prints "-" for nothing allocated."""
    value = re.match(r"([\d,]+)\s*B$", row["allocated"])
    return f"{int(value.group(1).replace(',', '')):,} B" if value else "0 B"


def environment(env):
    """'BenchmarkDotNet 0.15.2, .NET 10.0.13, Intel Xeon 2.10 GHz' from the report header."""
    bdn = next((re.sub(r"^BenchmarkDotNet v?", "", l).split(",")[0] for l in env if l.startswith("BenchmarkDotNet")), "")
    cpu = env[1].split(",")[0] if len(env) > 1 else ""
    cpu = re.sub(r"\s*\(R\)|\s*\(TM\)|\s+(?:Processor|CPU)\b|\s+\d+-Core", "", cpu)
    cpu = re.sub(r"\s*@?\s*([\d.]+)\s*GHz", r" \1 GHz", cpu).strip()
    runtime = next((m.group(1) for l in env if (m := re.search(r"\[Host\]\s*:\s*(\.NET [\d.]+)", l))), "")
    return bdn, runtime, cpu


def numbers(v4_path, v3_paths, tag=None):
    env, v4 = parse(v4_path)

    def vs_memorypack(verb, key):
        return ratio(v4[f"MemoryPack{verb}{key}"]["mean"], v4[f"Nino{verb}{key}"]["mean"])

    def wins(verb):
        """Ratios of the cases v4 wins: above 1, and not on par."""
        return [r for r in (vs_memorypack(verb, k) for k in CLASS_KEYS) if r > 1 and not on_par(r)]

    def win_range(verb):
        won = wins(verb)
        return f"{one(min(won))}–{one(max(won))}" if won else "—"

    bdn, runtime, cpu = environment(env)
    out = {
        "ser.max": one(max(wins("Serialize"), default=1.0)),
        "reuse.inplace": one(ratio(v4["MemoryPackLeavesOverwrite"]["mean"], v4["NinoLeavesInPlace"]["mean"])),
        "ser.range": win_range("Serialize"),
        "deser.range": win_range("Deserialize"),
        "env.bdn": bdn,
        "env.runtime": runtime,
        "env.cpu": cpu,
        "payload.v4": v4["NinoSerializeSimpleClass"]["payload"].replace(" B", ""),
        "payload.memorypack": v4["MemoryPackSerializeSimpleClass"]["payload"].replace(" B", ""),
    }

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
    for verb, key, label in [("Serialize", "SimpleClass", "SimpleClass"),
                             ("Serialize", "SimpleClasses", "SimpleClass[100]"),
                             ("Serialize", "PolymorphicClassArray", "polymorphic class array"),
                             ("Deserialize", "SimpleClass", "SimpleClass"),
                             ("Deserialize", "SimpleClasses", "SimpleClass[100]"),
                             ("Deserialize", "PolymorphicClassArray", "polymorphic class array")]:
        memorypack = v4[f"MemoryPack{verb}{key}"]["mean"]
        bars = [("Nino v4", memorypack / v4[f"Nino{verb}{key}"]["mean"])]
        if f"Nino{verb}{key}" in v3:
            bars.append(("Nino v3", memorypack / v3[f"Nino{verb}{key}"]))
        chart.append((f"{verb} · {label}", bars))
    for title, nino, memorypack in [("pooled node", "NinoNodePooled", "MemoryPackNodeOverwrite"),
                                    ("leaves in place", "NinoLeavesInPlace", "MemoryPackLeavesOverwrite")]:
        chart.append((f"Deserialize into existing objects · {title} (Nino {allocated(v4[nino])}, "
                      f"MemoryPack {allocated(v4[memorypack])})", [("Nino v4", v4[memorypack]["mean"] / v4[nino]["mean"])]))

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
               ("Players", "`List<Player>`, 100"), ("Orders", "`List<Order>`, 100"),
               ("Shapes", "`List<Shape>`, 1,000 (abstract base)"), ("Tree", "`TreeNode`, 1,365 nodes"),
               ("Inventory", "`InventoryItem[1000]` (padded struct)"),
               ("StringDictionary", "`Dictionary<string, string>`, 1,000")]


def table(head, rows):
    """A Markdown table on lines of its own between its markers."""
    return "\n" + "\n".join(["| " + " | ".join(head) + " |", "|---" + "|---:" * (len(head) - 1) + "|"] +
                      ["| " + " | ".join(r) + " |" for r in rows]) + "\n"


def tables(v4, v3, v3_runs, env, tag):
    """The runtime section of docs/v4-benchmarks.md: one table per marker, and the line saying where they come from."""
    def mean(method):
        return v4[method]["mean"] if method in v4 else None

    out = {}
    for verb in ("Serialize", "Deserialize"):
        rows = []
        for key, label in SIMPLE_CASES:
            nino, mp = mean(f"Nino{verb}{key}"), mean(f"MemoryPack{verb}{key}")
            if nino is None or mp is None:
                continue
            old = v3.get(f"Nino{verb}{key}")
            rows.append([label, time(nino), time(mp), time(old) if old is not None else "—", versus(mp, nino),
                         versus(old, nino) if old is not None else "—"])
        out[f"table.{verb.lower()}"] = table(["Case", "v4", "MemoryPack", "v3", "v4 vs MemoryPack", "v4 vs v3"], rows)

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


def svg(chart):
    top = max(v for _, bars in chart for _, v in bars)
    scale = min(95, 640 / top)
    out, y = [], 84
    for title, bars in chart:
        out.append(f'<text x="0" y="{y}" class="g">{title}</text>')
        by = y + 22
        for name, v in bars + [("MemoryPack", 1.0)]:
            w = v * scale
            fill = "url(#v4)" if name == "Nino v4" else ("#8b95a7" if name == "Nino v3" else "#e0a030")
            cls = "v4" if name == "Nino v4" else "l"
            out.append(f'<text x="90" y="{by}" class="l" text-anchor="end">{name}</text>')
            out.append(f'<rect x="100" y="{by - 12}" width="{w:.1f}" height="16" rx="4" fill="{fill}"/>')
            out.append(f'<text x="{100 + w + 8:.1f}" y="{by}" class="{cls}">{v:.2f}×</text>')
            by += 21
        y = by + 16
    height = y - 12
    head = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 820 {height}" width="820" height="{height}" font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif">
<defs><linearGradient id="v4" x1="0" x2="1"><stop offset="0" stop-color="#22c3e6"/><stop offset="1" stop-color="#7c5cff"/></linearGradient>
<style>.t{{font-size:18px;font-weight:700;fill:#8b95a7}}.s{{font-size:12px;fill:#8b95a7}}.g{{font-size:13px;font-weight:600;fill:#8b95a7}}.l{{font-size:12px;fill:#8b95a7}}.v4{{font-size:12px;font-weight:700;fill:#7c5cff}}</style></defs>
<text x="0" y="22" class="t">Speed relative to MemoryPack (higher is faster)</text>
<text x="0" y="44" class="s">BenchmarkDotNet, .NET 10, server GC, same machine. MemoryPack 1.21.4 = 1.00× (its overwrite mode for existing objects). Nino v3 = 3.3.13.</text>
'''
    return head + "\n".join(out) + "\n</svg>\n", height


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
    parser.add_argument("--v4", required=True)
    parser.add_argument("--v3", action="append", default=[])
    parser.add_argument("--tag")
    parser.add_argument("--png-html")
    args = parser.parse_args()

    values, chart = numbers(args.v4, args.v3, args.tag)
    image, height = svg(chart)
    (ROOT / "docs/assets/benchmark.svg").write_text(image, encoding="utf-8")
    if args.png_html:
        Path(args.png_html).write_text(
            f'<html><body style="margin:0;background:transparent">{image}</body></html>', encoding="utf-8")
    for name, zh in (("README.md", False), ("README.zh.md", True), ("docs/v4-benchmarks.md", False)):
        keys = {k for k in values if k.startswith("doc.") == (name.startswith("docs/"))}
        missing = keys - fill(ROOT / name, {k: values[k] for k in keys}, zh)
        if missing:
            print(f"warning: {name} has no marker for {', '.join(sorted(missing))}", file=sys.stderr)
    print(f"chart height {height}")
    for key, value in values.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
