#!/usr/bin/env python3
"""The release body's Unity section, from the lines the benchmark's IL2CPP player and its editor log.

usage: unity_report.py <tag> <lines file>...

Each file holds one Unity version's lines (unity.yml, benchmark on: grep of the player's and the editor's logs):

    NINO_UNITY_BUILD|key=value|...   the editor that built the player (Test.Editor.Burst.BenchmarkPlayerBuild):
                                     unity, target, backend, il2cpp_config, il2cpp_codegen, stripping, development, burst
    NINO_UNITY_ENV|key=value|...     the player (Test.Runtime.Burst.BurstBenchmark): unity, backend, development, isa,
                                     cpu, cores, os, family, arch, rounds, batch_ms
    NINO_UNITY_CI|key=value|...      the workflow: nino_core (the build configuration of Nino.Core.dll), runner
    NINO_UNITY_BENCH|case|operation|n|ns without Burst|ns with Burst

It prints one section: the heading (HEADING, never the words "Performance Report", by which the docs site finds the
.NET report), a line per Unity version naming the player and the machine, then a table per Unity version. No table has
a Method or Mean column, so nothing that reads the .NET tables (nino.docs scripts/sync-benchmarks.ts) takes these rows
for its own. A file without NINO_UNITY_BENCH lines is left out with a warning on stderr; with no rows at all the script
fails, and the workflow leaves the section out.
"""
import sys

from runtimes_report import time

HEADING = "## 🎮 Unity benchmark (Burst, IL2CPP)"
LEGEND = 'Each time is the median per call; "Speedup" is without Burst divided by with Burst.'


def fields(parts):
    out = {}
    for part in parts:
        key, sep, value = part.partition("=")
        if sep:
            out[key.strip()] = value.strip()
    return out


def parse(path):
    """{"build", "env", "ci"}: dicts; "rows": [(case, op, n, scalar ns, burst ns)]."""
    run = {"build": {}, "env": {}, "ci": {}, "rows": []}
    for line in open(path, encoding="utf-8", errors="replace"):
        # The lines may carry a prefix (a timestamp, a docker log tag): each starts at its marker.
        at = line.find("NINO_UNITY_")
        if at < 0:
            continue
        parts = line[at:].rstrip("\r\n").split("|")
        kind = parts[0]
        if kind == "NINO_UNITY_BENCH" and len(parts) == 6:
            try:
                run["rows"].append((parts[1], parts[2], int(parts[3]), float(parts[4]), float(parts[5])))
            except ValueError:
                print(f"warning: {path}: unreadable line {line.strip()!r}", file=sys.stderr)
        elif kind in ("NINO_UNITY_BUILD", "NINO_UNITY_ENV", "NINO_UNITY_CI"):
            run[kind[len("NINO_UNITY_"):].lower()].update(fields(parts[1:]))
    return run


ARCH = {"x64": "x64", "arm64": "Arm64", "x86": "x86", "arm": "Arm32"}


def player(run):
    """The player's configuration, as each part was reported: only what a log says, never a default."""
    build, env, ci = run["build"], run["env"], run["ci"]
    parts = []
    if build.get("il2cpp_config"):
        parts.append(f"C++ {build['il2cpp_config']}")
    if build.get("il2cpp_codegen"):
        parts.append(f"code generation {build['il2cpp_codegen']}")
    if build.get("stripping"):
        parts.append(f"stripping {build['stripping']}")
    development = env.get("development", build.get("development"))
    if development is not None:
        parts.append("Development build" if development == "true" else "not a Development build")
    if ci.get("nino_core"):
        parts.append(f"Nino.Core {ci['nino_core']}")
    backend = env.get("backend") or build.get("backend") or "unknown backend"
    return f"{backend} player" + (f" ({', '.join(parts)})" if parts else "")


def environment(run):
    env, build = run["env"], run["build"]
    unity = env.get("unity") or build.get("unity") or "?"
    family = env.get("family") or env.get("os", "").split(" ")[0] or "?"
    arch = ARCH.get(env.get("arch", "").lower(), env.get("arch", "?"))
    cpu = env.get("cpu", "unknown CPU")
    if env.get("cores"):
        cpu += f" ({env['cores']} cores)"
    items = [f"Unity {unity}", player(run), f"{family} {arch}", cpu]
    if build.get("burst"):
        items.append(f"Burst {build['burst']}")
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
    for case, op, _, scalar, burst in run["rows"]:
        rows.append(f"| {cell(case)} | {op} | {time(scalar)} | {time(burst)} | {speedup(scalar, burst)} |")
    return rows


def method(tag, runs):
    env = runs[0]["env"]
    rounds, batch = env.get("rounds"), env.get("batch_ms")
    timing = (f"the median of {rounds} batches of about {batch} ms each" if rounds and batch
              else "the median of its timed batches")
    return (f"{tag}, measured in the IL2CPP test player that `.github/workflows/unity.yml` builds and runs on a "
            "GitHub-hosted runner (`Test.Runtime.Burst.BurstBenchmark`): `NinoSerializer.Serialize` to a new array, "
            "`NinoDeserializer.Deserialize` into an array of the same length (arrays) or to a new value (strings, the "
            "list), each call without the Burst kernels (`NinoKernels.Register(null)`: Nino.Core's own paths, as "
            f"without `com.unity.burst`) and with them, the two alternately, after a warm-up; each time is {timing}. "
            "Compare within a table: the runners vary between releases.")


def report(tag, paths):
    runs = []
    for path in paths:
        run = parse(path)
        if run["rows"]:
            runs.append(run)
        else:
            print(f"warning: {path}: no NINO_UNITY_BENCH lines, left out", file=sys.stderr)
    if not runs:
        sys.exit("no Unity benchmark rows")
    out = [HEADING, ""]
    out += [environment(run) for run in runs]
    out += [LEGEND, "", method(tag, runs), ""]
    for run in runs:
        unity = run["env"].get("unity") or run["build"].get("unity") or "?"
        out += [f"### Unity {unity}", "", *table(run), ""]
    return "\n".join(out).rstrip("\n") + "\n"


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    print(report(sys.argv[1], sys.argv[2:]), end="")


if __name__ == "__main__":
    main()
