#!/usr/bin/env python3
"""The machine code of the Nino benchmark cases, and what changed in it between two commits.

usage: asm.py dump [--ref REF] [--filter TEXT] [--runs N] [--asm FILE] [--inline] [--boxes] OUT.json
       asm.py diff BASE.json HEAD.json
       asm.py compare [--filter TEXT] [--inline] REF    (dump REF and the working tree, then diff them)

`dump` builds runtimes/csharp/benchmarks/Nino.Benchmark (of the working tree, or of REF in a temporary git worktree) and runs it with the
startup hook in jitwarm/: every Nino case (or those whose name contains TEXT) is called until it is compiled at Tier1,
a few seconds per class, then the process exits. DOTNET_JitDisasm prints the Tier1 code of the cases and of the
generated formatters and Nino methods they call; `dump` writes, per method, its size, its stack accesses and the
methods it calls (`--asm` keeps the listing). It is the code BenchmarkDotNet times: everything a top-level call
reaches is inlined into the case, so a change anywhere on the read or write of a type shows there. Dump both sides
with the same TEXT: what the JIT inlines depends on what it compiled before, and on the profile data of the methods it
inlines, gathered while the process runs, which varies a little from run to run; of N runs (3 by default) each
method's leanest code is kept.

`diff` prints every method whose code grew by more than 15% and 64 bytes, or that accesses the stack more than 4
times more often (a reader, a writer or a cursor left in memory), and exits with 1 when there is one; it also lists
the methods each calls that it did not call before (one the JIT no longer inlined, or code moved out of line on
purpose). Dump both sides on the same machine (DOTNET_EnableAVX512=0 gives the code of a machine without AVX-512;
.NET 10 ignores DOTNET_EnableAVX512F); a method listed alone, in code the change does not reach, is usually the
variation above: dump again to tell.

`--inline` also writes OUT.inline.json: per root (the method being compiled, Nino's only), the inlines the JIT gave
up with its reason ({root: {"inlinee | reason": count}}), from the runtime's MethodJitInliningFailed events (jitwarm's
NINO_JIT_INLINE), kept when every run reported them (the fewest times of any run). `diff` then lists the failures
head has and base has not, as it lists new calls (when both OUT.inline.json files exist). The reason says which fix
applies: `optimize`, references/jit.md, "Inlining budget".

`--boxes` lists every box in the listings, per method, and exits with 1 when there is one (Nino boxes nothing on a
read or write path: `optimize`, "Never box"): the boxing helpers, and the allocations of a value type's method table
that the JIT expands a box into inline (jitwarm names each address's type in the process: NINO_JIT_BOXES; x64). With DOTNET_TieredCompilation=0 in the environment both
the listings and the failures are FullOpts (what NativeAOT and the first calls compile), one round per case.
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile

# The .NET root (runtimes/csharp) and the repository root above it.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REPO = os.path.dirname(os.path.dirname(ROOT))
LISTING = re.compile(r"^; Assembly listing for method (.+) \((Tier1|FullOpts)\)$")
# The generated interceptors' class names carry a hash of the file.
HASH = re.compile(r"[0-9A-F]{32,}")
METHODS = "Nino.Benchmark.*:Nino* NinoGen.*:* Nino.Core.*:* Nino.Immutable.*:*"
# Frame-relative memory operands: x64 [rbp-0x10] / [rsp+0x20], Arm64 [fp, #0x10] / [sp, #0x20].
STACK = re.compile(r"\[(rbp|rsp)[-+]|\[(fp|sp), #")
CALL = re.compile(r"\b(call|bl|blr)\s+(.*)$")
ADDRESS = re.compile(r"0x[0-9A-Fa-f]{6,}")


# An allocation helper and the method table address loaded for it on the line before (x64).
LOAD = re.compile(r"^\s+mov\s+(rdi|rcx), (0x[0-9A-F]+)\s*$")
ALLOCATE = re.compile(r"^\s+call\s+\[?CORINFO_HELP_(NEWSFAST|NEWSFAST_ALIGN8|BOX|BOX_NULLABLE)\]?")


def parse(listing, types=None):
    """-> {method: {size, stack, calls}} from DOTNET_JitDisasm output; the last Tier1 listing of each wins. With
    types ({address: (kind, name)} from jitwarm's NINO_JIT_BOXES), each method also gets "boxes": the value types it
    boxes (a boxing helper's, or an inline allocation of a value type's method table)."""
    methods, name, current, loaded = {}, None, None, None
    for line in listing.splitlines():
        match = LISTING.match(line)
        if match:
            name, current = HASH.sub("", match.group(1)), {"size": 0, "stack": 0, "calls": {}}
            if types is not None:
                current["boxes"] = []
        elif current is None:
            continue
        elif line.startswith("; Assembly listing"):
            current = None  # another tier
        elif line.startswith("; Total bytes of code"):
            # Kept only when complete: the background compiler may cut a listing short with another.
            current["size"] = int(line.split()[-1])
            methods[name], current = current, None
        elif STACK.search(line):
            current["stack"] += 1
        if current is not None and types is not None:
            if (allocate := ALLOCATE.match(line)) and (allocate.group(1).startswith("BOX") or loaded in types):
                kind, type_name = types.get(loaded, ("value", "?"))
                if kind == "value":
                    current["boxes"].append(type_name)
            load = LOAD.match(line)
            loaded = load.group(2) if load else None
        if current is not None and (call := CALL.search(line)) and not call.group(2).startswith("G_M"):
            target = HASH.sub("", ADDRESS.sub("", call.group(2))).strip()
            current["calls"][target] = current["calls"].get(target, 0) + 1
    return methods


def newest_build(project, dll):
    """-> the newest framework's Release build of dll in project: a REF from before the benchmarks moved to net11.0
    has only its net10.0 build."""
    builds = sorted(glob.glob(os.path.join(project, "bin", "Release", "net*", dll)),
                    key=lambda p: [int(n) for n in re.findall(r"\d+", os.path.basename(os.path.dirname(p)))])
    if not builds:
        sys.exit(f"no Release build of {dll} in {project}")
    return builds[-1]


def hook():
    """-> the path of the startup hook (jitwarm/), built from this tree."""
    project = os.path.join(ROOT, "benchmarks", "asm", "jitwarm")
    subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", project], check=True, stdout=subprocess.DEVNULL)
    return os.path.join(project, "bin", "Release", "net11.0", "JitWarm.dll")


def inline_path(out):
    """-> where the inline failures of the dump OUT.json go: OUT.inline.json."""
    return (out[:-5] if out.endswith(".json") else out) + ".inline.json"


def parse_inline(table):
    """-> {root: {"inlinee | reason": count}} from jitwarm's lines "root\tinlinee\treason\tcount"."""
    failures = {}
    for line in table.splitlines():
        root, inlinee, reason, count = line.split("\t")
        key = f"{HASH.sub('', inlinee)} | {reason}"
        root = failures.setdefault(HASH.sub("", root), {})
        root[key] = root.get(key, 0) + int(count)
    return failures


def parse_types(table):
    """-> {address: (kind, name)} from jitwarm's lines "address\tvalue|class\tname"."""
    return {address: (kind, name) for address, kind, name in (line.split("\t") for line in table.splitlines())}


def dump(ref, text, keep, runs=3, inline=False, boxes=False):
    """-> ({method: code}, {root: {failure: count}} or None)."""
    dotnet_env = dict(os.environ, NINO_BENCH_QUICK="1")
    tree, worktree = REPO, None
    if ref:
        worktree = tempfile.mkdtemp(prefix="nino-asm-")
        subprocess.run(["git", "-C", ROOT, "worktree", "add", "-q", "--detach", worktree, ref], check=True)
        tree = worktree
    try:
        # runtimes/csharp/benchmarks/Nino.Benchmark; a commit from before the move (#273) has it in benchmarks/, and one
        # from before the test and benchmark projects left src/ in src/
        project = next(p for p in (os.path.join(tree, d, "Nino.Benchmark")
                                   for d in ("runtimes/csharp/benchmarks", "benchmarks", "src"))
                       if os.path.isdir(p))
        build = subprocess.run(["dotnet", "build", "-c", "Release", project], capture_output=True, text=True)
        if build.returncode != 0:
            sys.exit(f"build of {ref or 'the working tree'} failed:\n{build.stdout[-4000:]}")
        startup_hook = hook()
        methods, failures, boxed = {}, None, {}
        for run in range(runs):
            with tempfile.TemporaryDirectory() as tmp:
                out = os.path.join(tmp, "jit.asm")
                env = dict(dotnet_env, DOTNET_JitStdOutFile=out, DOTNET_JitDisasm=METHODS,
                           DOTNET_STARTUP_HOOKS=startup_hook, NINO_JIT_FILTER=text or "")
                if inline:
                    env["NINO_JIT_INLINE"] = os.path.join(tmp, "inline.tsv")
                if boxes:
                    env["NINO_JIT_BOXES"] = os.path.join(tmp, "boxes.tsv")
                subprocess.run(["dotnet", newest_build(project, "Nino.Benchmark.dll")],
                               env=env, cwd=project, check=True)
                with open(out, encoding="utf-8") as f:
                    listing = f.read()
                types = None
                if boxes:
                    with open(env["NINO_JIT_BOXES"], encoding="utf-8") as f:
                        types = parse_types(f.read())
                if inline:
                    with open(env["NINO_JIT_INLINE"], encoding="utf-8") as f:
                        found = parse_inline(f.read())
                    # Kept when every run reported it: the fewest times of any run.
                    failures = found if failures is None else {
                        root: kept for root in failures.keys() & found.keys()
                        if (kept := {k: min(n, found[root][k]) for k, n in failures[root].items()
                                     if k in found[root]})}
            if keep and run == 0:
                with open(keep, "w", encoding="utf-8") as f:
                    f.write(listing)
            # What the JIT inlines also depends on the profile data of the methods it inlines, gathered while the
            # process ran: of a few runs, the leanest code of each method (the one with the fewest stack accesses).
            for name, code in parse(listing, types).items():
                if boxes:
                    # A box in any run's code is kept, whichever run's code is: the JIT may compile it so.
                    boxed[name] = max(boxed.get(name, []), code.pop("boxes"), key=len)
                if name not in methods or (code["stack"], code["size"]) < (methods[name]["stack"], methods[name]["size"]):
                    methods[name] = code
        for name, types in boxed.items():
            methods[name]["boxes"] = types
        return methods, failures
    finally:
        if worktree:
            subprocess.run(["git", "-C", ROOT, "worktree", "remove", "--force", worktree], check=False)


def report_boxes(methods):
    """Prints every method that boxes, with the value types; -> how many."""
    boxing = {m: c["boxes"] for m, c in sorted(methods.items()) if c.get("boxes")}
    for method, types in boxing.items():
        counts = {t: types.count(t) for t in types}
        print(f"{method}\n    boxes " + "; ".join(f"{t} x{n}" if n > 1 else t for t, n in sorted(counts.items())))
    print(f"{len(boxing)} of {len(methods)} methods box" if boxing else f"no method boxes ({len(methods)} listed)")
    return len(boxing)


def diff(base, head):
    """Prints the methods whose code got worse; -> how many."""
    worse = 0
    for method in sorted(head):
        now, before = head[method], base.get(method)
        if before is None:
            continue
        notes = []
        grown = now["size"] - before["size"]
        if grown > 64 and grown > 0.15 * before["size"]:
            notes.append(f"size {before['size']} -> {now['size']}")
        if now["stack"] > before["stack"] + 4:
            notes.append(f"stack accesses {before['stack']} -> {now['stack']}")
        if notes:
            worse += 1
        new_calls = sorted(c for c in now["calls"] if c not in before["calls"])
        if new_calls:
            notes.append("calls " + "; ".join(new_calls))
        if notes:
            print(f"{method}\n    " + "\n    ".join(notes))
    missing = sorted(m for m in set(base) - set(head) if m.startswith("Nino.Benchmark."))
    if missing:
        print("not compiled at Tier1 in head: " + ", ".join(missing))
    print(f"{worse} of {len(head)} methods worse" if worse else f"no method worse ({len(head)} compared)")
    return worse


def diff_inline(base, head):
    """Prints the inlines head gave up and base did not (root, inlinee, reason), sorted by root. Leaves out the
    callees marked NoInlining, and those an earlier verdict marked so ("noinline per IL/cached result"): a call
    that was not one before is in the calls diff above."""
    new = [(root, failure) for root in sorted(head) for failure in sorted(head[root])
           if failure not in base.get(root, {}) and not failure.endswith("| noinline per IL/cached result")]
    if not new:
        print("no new inline failures")
        return
    print(f"{len(new)} new inline failures (inlinee | reason):")
    for root in sorted({root for root, _ in new}):
        print(f"{root}\n    " + "\n    ".join(f for r, f in new if r == root))


def write(out, methods, failures):
    with open(out, "w", encoding="utf-8") as f:
        json.dump(methods, f, indent=1, sort_keys=True)
    if failures is not None:
        with open(inline_path(out), "w", encoding="utf-8") as f:
            json.dump(failures, f, indent=1, sort_keys=True)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("dump")
    p.add_argument("--ref")
    p.add_argument("--runs", type=int, default=3)
    p.add_argument("--filter")
    p.add_argument("--asm")
    p.add_argument("--inline", action="store_true")
    p.add_argument("--boxes", action="store_true")
    p.add_argument("out")
    p = commands.add_parser("diff")
    p.add_argument("base")
    p.add_argument("head")
    p = commands.add_parser("compare")
    p.add_argument("--filter")
    p.add_argument("--inline", action="store_true")
    p.add_argument("ref")
    args = parser.parse_args()

    if args.command == "dump":
        methods, failures = dump(args.ref, args.filter, args.asm, args.runs, args.inline, args.boxes)
        write(args.out, methods, failures)
        if args.boxes and report_boxes(methods):
            sys.exit(1)
    elif args.command == "diff":
        worse = diff(load(args.base), load(args.head))
        if os.path.exists(inline_path(args.base)) and os.path.exists(inline_path(args.head)):
            diff_inline(load(inline_path(args.base)), load(inline_path(args.head)))
        sys.exit(1 if worse else 0)
    else:
        base, base_failures = dump(args.ref, args.filter, None, inline=args.inline)
        head, head_failures = dump(None, args.filter, None, inline=args.inline)
        worse = diff(base, head)
        if args.inline:
            diff_inline(base_failures, head_failures)
        sys.exit(1 if worse else 0)


if __name__ == "__main__":
    main()
