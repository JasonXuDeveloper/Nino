# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Benchmarks

- During development, run benchmarks in quick mode: `QUICK=1 benchmarks/run.sh`, with `FILTER='*SimpleTest*'`
  (any BenchmarkDotNet filter) to run only the cases you are working on. Quick mode measures each case for 6 to 10
  iterations, stopping once its error is under 2%, so it takes about 15–20 minutes for everything.
- Both modes run v3 3.3.13 (`benchmarks/v3`, from NuGet) once, directly before v4, on the same machine; MemoryPack runs
  in both, so its two results show how much the machine moved.
- Quick numbers are for comparing your own changes back to back on one machine. Never publish them: the numbers
  in README.md, docs/v4-benchmarks.md, the chart and the release notes come from a full `benchmarks/run.sh` run
  (or the release workflow), whose job id stays `ShortRun-.NET 10.0`.
- See benchmarks/README.md for the options.

## Machine code

A change to a read or write path (Nino.Core, Nino.Immutable, the code the generator emits) can slow cases it does not
touch: everything a top-level call reaches is inlined into one method, under one inlining budget. Before committing
one, run `python3 benchmarks/asm.py compare origin/main` (about two minutes, see benchmarks/README.md) and explain or fix
every method it lists. What has gone wrong before:
- A branch whose condition is a method call (`RawFormatter.Is`, `NinoBlock.Size`, ...) is compiled and inlined even
  for the types where it is dead, and uses up the budget there. Keep rare or large paths (vector loops, element by
  element loops, string allocation, recycling) out of line (`NoInlining`), with only the common path inline.
- Never pass `ref reader` or `ref writer` to a method that is not inlined, even in a dead branch: the address keeps
  the struct in memory for the whole method. Pass it by value and return it (`reader = ReadEach(values, reader)`).
- A JIT-time check made of several helper calls is one `static readonly` field instead (`NinoElements.Traits`,
  `NinoBlock<T, TFormatter>.Size`): one load, nothing to inline.
- Vector loops: bump `ref` cursors and count down (`at = ref Unsafe.Add(ref at, step)`, `while (--n != 0)`), never
  `(nuint)i * size` per iteration; take everything that does not change per iteration (masks, indices) out of the
  loop into locals.
- On Arm64 (and anywhere without AVX-512) `Vector512<T>` and `Vector256<T>` are structs of 16-byte vectors: passed
  into an inlined loop body they are copied through the stack every iteration. Take their `Vector128` parts out once,
  before the loop, and pass those.
- Arm64 `tbl` with two to four table registers needs consecutive registers: build the tuples from loads made once
  per iteration, before any lookup (`(w0, w1)`, `(w1, w2)`), never from per-lookup loads, or the JIT adds `mov`s.
  Each table register is a micro-op on Neoverse cores: give each lookup only the registers its bytes come from, and
  store only the bytes the values fill (`NinoBlock.WireTables64`); check each shape, as types that compile to the
  same code can differ by the data (`(byte, int)` took twice as long as `(short, int)`).
- Look at Arm64 code too, not only x64: build for `linux-arm64` and run under `qemu-aarch64-static` with
  `DOTNET_TieredCompilation=0 DOTNET_JitDisasm='<method>' DOTNET_JitStdOutFile=<file>` (the code paths only a type
  outside the benchmark suite takes, such as `(byte, int)` for the 64-byte permutes, need a small program of their
  own). A clean loop is loads (`ldp`), lookups, stores (`stp`) and the cursor adds; `stp`/`ldp` to `[fp, ...]`, `mul`
  or `mov v..` in it are the signs above.
- The signs in the listing: `Reader:Skip`, `Reader:CopyTo` or `NinoUnsafe:Read` as calls (the budget ran out);
  `NinoReadContext:Return` or `NinoWriteContext:Return` calls and `[rbp-0x..]` loads and stores of the cursor (the
  reader or writer was not kept in registers).

## Commit messages and release notes

- Conventional Commits: `type(scope): summary`, plus a body that says why. Types: `feat`, `fix`, `perf`,
  `refactor`, `docs`, `test`, `ci`, `build`, `chore`; the scope is the area (`core`, `generator`, `collections`,
  `benchmarks`, ...). The release notes group commits by type (`.github/scripts/release_notes.py`, `classify`).
- Release notes are generated (`.github/scripts/release_notes.py`): the commit list, and highlights from trailers in
  the message's last paragraph, next to `Co-Authored-By`. Give a user-facing change one:
  - `Wire-Format:` anything that changes the bytes Nino writes, and for which types;
  - `Added:` a new API, attribute or behavior;
  - `Performance:` what got faster, for which types, and by how much (measured);
  - `Fixed:` a user-visible bug.
  Write each as one sentence or two for users, in Markdown, continuing over indented lines if needed. Put
  `Closes #N` in the body above the trailers; the issue is appended to the entry. Leave tests, CI, docs and
  refactors without trailers. The docs site's changelog (nino.docs) is written from the same trailers.
