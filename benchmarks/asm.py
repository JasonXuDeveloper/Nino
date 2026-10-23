#!/usr/bin/env python3
"""The machine code of the Nino benchmark cases, and what changed in it between two commits.

usage: asm.py dump [--ref REF] [--filter TEXT] [--runs N] [--asm FILE] OUT.json
       asm.py diff BASE.json HEAD.json
       asm.py compare [--filter TEXT] REF          (dump REF and the working tree, then diff them)

`dump` builds src/Nino.Benchmark (of the working tree, or of REF in a temporary git worktree) and runs it with the
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
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LISTING = re.compile(r"^; Assembly listing for method (.+) \((Tier1|FullOpts)\)$")
# The generated interceptors' class names carry a hash of the file.
HASH = re.compile(r"[0-9A-F]{32,}")
METHODS = "Nino.Benchmark.*:Nino* NinoGen.*:* Nino.Core.*:* Nino.Immutable.*:*"
# Frame-relative memory operands: x64 [rbp-0x10] / [rsp+0x20], Arm64 [fp, #0x10] / [sp, #0x20].
STACK = re.compile(r"\[(rbp|rsp)[-+]|\[(fp|sp), #")
CALL = re.compile(r"\b(call|bl|blr)\s+(.*)$")
ADDRESS = re.compile(r"0x[0-9A-Fa-f]{6,}")


def parse(listing):
    """-> {method: {size, stack, calls}} from DOTNET_JitDisasm output; the last Tier1 listing of each wins."""
    methods, name, current = {}, None, None
    for line in listing.splitlines():
        match = LISTING.match(line)
        if match:
            name, current = HASH.sub("", match.group(1)), {"size": 0, "stack": 0, "calls": {}}
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
        if current is not None and (call := CALL.search(line)) and not call.group(2).startswith("G_M"):
            target = HASH.sub("", ADDRESS.sub("", call.group(2))).strip()
            current["calls"][target] = current["calls"].get(target, 0) + 1
    return methods


def hook():
    """-> the path of the startup hook (jitwarm/), built from this tree."""
    project = os.path.join(ROOT, "benchmarks", "jitwarm")
    subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q", project], check=True, stdout=subprocess.DEVNULL)
    return os.path.join(project, "bin", "Release", "net10.0", "JitWarm.dll")


def dump(ref, text, keep, runs=3):
    dotnet_env = dict(os.environ, NINO_BENCH_QUICK="1")
    tree, worktree = ROOT, None
    if ref:
        worktree = tempfile.mkdtemp(prefix="nino-asm-")
        subprocess.run(["git", "-C", ROOT, "worktree", "add", "-q", "--detach", worktree, ref], check=True)
        tree = worktree
    try:
        project = os.path.join(tree, "src", "Nino.Benchmark")
        build = subprocess.run(["dotnet", "build", "-c", "Release", project], capture_output=True, text=True)
        if build.returncode != 0:
            sys.exit(f"build of {ref or 'the working tree'} failed:\n{build.stdout[-4000:]}")
        startup_hook = hook()
        methods = {}
        for run in range(runs):
            with tempfile.TemporaryDirectory() as tmp:
                out = os.path.join(tmp, "jit.asm")
                env = dict(dotnet_env, DOTNET_JitStdOutFile=out, DOTNET_JitDisasm=METHODS,
                           DOTNET_STARTUP_HOOKS=startup_hook, NINO_JIT_FILTER=text or "")
                subprocess.run(["dotnet", os.path.join(project, "bin", "Release", "net10.0", "Nino.Benchmark.dll")],
                               env=env, cwd=project, check=True)
                with open(out, encoding="utf-8") as f:
                    listing = f.read()
            if keep and run == 0:
                with open(keep, "w", encoding="utf-8") as f:
                    f.write(listing)
            # What the JIT inlines also depends on the profile data of the methods it inlines, gathered while the
            # process ran: of a few runs, the leanest code of each method (the one with the fewest stack accesses).
            for name, code in parse(listing).items():
                if name not in methods or (code["stack"], code["size"]) < (methods[name]["stack"], methods[name]["size"]):
                    methods[name] = code
        return methods
    finally:
        if worktree:
            subprocess.run(["git", "-C", ROOT, "worktree", "remove", "--force", worktree], check=False)


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


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("dump")
    p.add_argument("--ref")
    p.add_argument("--runs", type=int, default=3)
    p.add_argument("--filter")
    p.add_argument("--asm")
    p.add_argument("out")
    p = commands.add_parser("diff")
    p.add_argument("base")
    p.add_argument("head")
    p = commands.add_parser("compare")
    p.add_argument("--filter")
    p.add_argument("ref")
    args = parser.parse_args()

    if args.command == "dump":
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(dump(args.ref, args.filter, args.asm, args.runs), f, indent=1, sort_keys=True)
    elif args.command == "diff":
        with open(args.base, encoding="utf-8") as f:
            base = json.load(f)
        with open(args.head, encoding="utf-8") as f:
            head = json.load(f)
        sys.exit(1 if diff(base, head) else 0)
    else:
        base = dump(args.ref, args.filter, None)
        sys.exit(1 if diff(base, dump(None, args.filter, None)) else 0)


if __name__ == "__main__":
    main()
