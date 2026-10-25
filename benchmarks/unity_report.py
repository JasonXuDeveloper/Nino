#!/usr/bin/env python3
"""The release body's Unity section, from the results of the benchmark's IL2CPP player.

usage: unity_report.py <tag> <folder>...

Each folder holds one Unity version's results (unity.yml, benchmark on; artifact unity-bench-<version>):

    results.xml   the player's NUnit results. The test output holds what the Unity Performance Testing package
                  (com.unity.test-framework.performance) writes for each test of Test.Runtime.Burst.BurstBenchmark:
                  "##performancetestresult2:<json>" (the test and its sample groups: "Without Burst", "With Burst",
                  and "SSSE3 only" on AVX2 CPUs, each with its Median in its Unit) and once
                  "##performancetestruninfo2:<json>" (Unity version, scripting backend, Development or not, CPU,
                  operating system, packages); and the player's own line NINO_UNITY_ENV|isa=...|arch=....
    build.txt     NINO_UNITY_BUILD|il2cpp_config=|il2cpp_codegen=|stripping=|burst_x64_targets= (the editor that
                  built the player: what the package does not record) and NINO_UNITY_CI|nino_core=|runner= (the job).

It prints one section: the heading (HEADING, never the words "Performance Report", by which the docs site finds the
.NET report), a line per Unity version naming the player and the machine, then a table per Unity version. No table has
a Method or Mean column, so nothing that reads the .NET tables (nino.docs scripts/sync-benchmarks.ts) takes these rows
for its own. A folder without results is left out with a warning on stderr; with none at all the script fails, and the
workflow leaves the section out.
"""
import json
import os
import sys
import xml.etree.ElementTree as ElementTree

from runtimes_report import time

HEADING = "## 🎮 Unity benchmark (Burst, IL2CPP)"
LEGEND = 'Each time is the median per call; "Speedup" is without Burst divided by with Burst.'

RESULT = "##performancetestresult2:"
RUN_INFO = "##performancetestruninfo2:"
WITHOUT, WITH, SSSE3 = "Without Burst", "With Burst", "SSSE3 only"
OPERATIONS = ("Serialize", "Deserialize")
# Unity.PerformanceTesting.SampleUnit, as JsonUtility writes it (its index): nanoseconds per unit.
UNITS = {0: 1.0, 1: 1e3, 2: 1e6, 3: 1e9}


def fields(parts):
    out = {}
    for part in parts:
        key, sep, value = part.partition("=")
        if sep:
            out[key.strip()] = value.strip()
    return out


def lines(text):
    """Each line's NINO_UNITY_<kind> fields, from the line's marker on (a log line may carry a prefix)."""
    out = {}
    for line in text.splitlines():
        at = line.find("NINO_UNITY_")
        if at >= 0:
            parts = line[at:].rstrip("\r").split("|")
            out.setdefault(parts[0][len("NINO_UNITY_"):].lower(), {}).update(fields(parts[1:]))
    return out


def median_ns(group):
    factor = UNITS.get(group.get("Unit"))
    median = group.get("Median")
    if factor is None or not isinstance(median, (int, float)):
        return None
    return median * factor


def row(result):
    """(case, operation, {sample group: ns}) of a BurstBenchmark result; None for any other test's."""
    name, cls = result.get("Name", ""), result.get("ClassName", "")
    if not name.startswith(cls + "."):
        return None
    # Test.Runtime.Burst.BurstBenchmark.Serialize.(int, long)[32]: the test method, then the case's name.
    operation, _, case = name[len(cls) + 1:].partition(".")
    times = {g.get("Name"): median_ns(g) for g in result.get("SampleGroups", [])}
    if operation not in OPERATIONS or not case or times.get(WITHOUT) is None or times.get(WITH) is None:
        return None
    return case, operation, times


def parse(folder):
    """{"run": the run info, "env", "build", "ci": dicts, "rows": [(case, operation, {group: ns})]}."""
    run = {"run": {}, "env": {}, "build": {}, "ci": {}, "rows": []}
    results = os.path.join(folder, "results.xml")
    outputs = []
    try:
        outputs = [o.text or "" for o in ElementTree.parse(results).iter("output")]
    except (OSError, ElementTree.ParseError) as error:
        print(f"warning: {results}: {error}", file=sys.stderr)
    for text in outputs:
        for line in text.splitlines():
            line = line.strip()
            try:
                if line.startswith(RESULT):
                    found = row(json.loads(line[len(RESULT):]))
                    if found:
                        run["rows"].append(found)
                elif line.startswith(RUN_INFO) and not run["run"]:
                    run["run"] = json.loads(line[len(RUN_INFO):])
            except ValueError:
                print(f"warning: {results}: unreadable line {line[:120]!r}", file=sys.stderr)
        run["env"].update(lines(text).get("env", {}))
    try:
        with open(os.path.join(folder, "build.txt"), encoding="utf-8", errors="replace") as f:
            found = lines(f.read())
        run["build"], run["ci"] = found.get("build", {}), found.get("ci", {})
    except OSError:
        pass
    # Each case once, its operations in order (NUnit runs Deserialize's tests before Serialize's).
    order = {}
    for case, _, _ in run["rows"]:
        order.setdefault(case, len(order))
    run["rows"].sort(key=lambda r: (order[r[0]], OPERATIONS.index(r[1])))
    return run


ARCH = {"x64": "x64", "arm64": "Arm64", "x86": "x86", "arm": "Arm32"}


def unity(run):
    return run["run"].get("Editor", {}).get("Version") or "?"


def package(run, name):
    for dependency in run["run"].get("Dependencies", []):
        if dependency.startswith(name + "@"):
            return dependency[len(name) + 1:]
    return None


def player(run):
    """The player's configuration, as each part was reported: only what a log says, never a default."""
    build, ci, info = run["build"], run["ci"], run["run"].get("Player", {})
    parts = []
    if build.get("il2cpp_config"):
        parts.append(f"C++ {build['il2cpp_config']}")
    if build.get("il2cpp_codegen"):
        parts.append(f"code generation {build['il2cpp_codegen']}")
    if build.get("stripping"):
        parts.append(f"stripping {build['stripping']}")
    if isinstance(info.get("Development"), bool):
        parts.append("Development build" if info["Development"] else "not a Development build")
    if ci.get("nino_core"):
        parts.append(f"Nino.Core {ci['nino_core']}")
    backend = info.get("ScriptingBackend") or "unknown backend"
    return f"{backend} player" + (f" ({', '.join(parts)})" if parts else "")


def environment(run):
    env, build, hardware = run["env"], run["build"], run["run"].get("Hardware", {})
    family = (hardware.get("OperatingSystem") or "").split(" ")[0] or "?"
    arch = ARCH.get(env.get("arch", "").lower(), env.get("arch", "?"))
    cpu = hardware.get("ProcessorType") or "unknown CPU"
    if hardware.get("ProcessorCount"):
        cpu += f" ({hardware['ProcessorCount']} cores)"
    items = [f"Unity {unity(run)}", player(run), f"{family} {arch}", cpu]
    burst = package(run, "com.unity.burst")
    if burst:
        targets = build.get("burst_x64_targets", "")
        if targets == "default":
            items.append(f"Burst {burst} (its default x64 targets)")
        elif targets:
            items.append(f"Burst {burst} (x64 targets {', '.join(targets.split())})")
        else:
            items.append(f"Burst {burst}")
    items.append(f"kernels {env.get('isa', '?')}")
    return ", ".join(items) + "."


def speedup(scalar, burst):
    if burst <= 0:
        return ""
    ratio = scalar / burst
    return f"{ratio:.2f}×" if ratio < 10 else f"{ratio:.1f}×"


def cell(text):
    """A case name as Markdown table text: no HTML a renderer would take for a tag, no column break."""
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("|", "\\|")


def table(run):
    rows = ["| Case | Operation | Without Burst | With Burst | Speedup |", "|---|---|--:|--:|--:|"]
    for case, operation, times in run["rows"]:
        scalar, burst = times[WITHOUT], times[WITH]
        rows.append(f"| {cell(case)} | {operation} | {time(scalar)} | {time(burst)} | {speedup(scalar, burst)} |")
    return rows


def method(tag, runs):
    performance = package(runs[0], "com.unity.test-framework.performance")
    tool = "the Unity Performance Testing package" + (f" {performance}" if performance else "")
    return (f"{tag}, measured in the IL2CPP test player that `.github/workflows/unity.yml` builds and runs on a "
            "GitHub-hosted runner (`Test.Runtime.Burst.BurstBenchmark`) with "
            f"{tool} (`Measure.Method`: its warm-up, then 9 measurements of about 100 ms, each the mean time per "
            "call): `NinoSerializer.Serialize` to a new array, `NinoDeserializer.Deserialize` into an array of the "
            "same length (arrays) or to a new value (strings, the list), without the Burst kernels "
            "(`NinoKernels.Register(null)`: Nino.Core's own paths, as without `com.unity.burst`) and with them. "
            "Compare within a table: the runners vary between releases.")


def report(tag, folders):
    runs = []
    for folder in folders:
        run = parse(folder)
        if run["rows"]:
            runs.append(run)
        else:
            print(f"warning: {folder}: no performance test results, left out", file=sys.stderr)
    if not runs:
        sys.exit("no Unity benchmark results")
    out = [HEADING, ""]
    out += [environment(run) for run in runs]
    out += [LEGEND, "", method(tag, runs), ""]
    for run in runs:
        out += [f"### Unity {unity(run)}", "", *table(run), ""]
    return "\n".join(out).rstrip("\n") + "\n"


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    print(report(sys.argv[1], sys.argv[2:]), end="")


if __name__ == "__main__":
    main()
