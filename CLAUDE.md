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
- Mark every small helper a read or write path calls `AggressiveInlining`, even one whose arguments are constants
  and that looks certain to fold: in a large method the JIT's inliner gives up on it. `NinoLanes.Paired`, called
  with constants in the AVX2 group loop, stayed a call per load and spilled the vectors around it: AVX2 writes took
  twice as long as SSSE3's. Look for such calls in the Tier1 listing of every ISA a path takes (`--asm`, with
  `DOTNET_EnableAVX512=0` and `DOTNET_EnableAVX2=0` too).
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
- Every optimization has to hold on each platform Nino runs on: x64 with AVX-512, AVX2 or SSSE3 only, Arm64, Arm32
  and other 32-bit targets, WebAssembly and anything without SIMD (`DOTNET_EnableHWIntrinsic=0`), Mono and Unity
  (IL2CPP included), .NET Framework. Fix a problem everywhere it occurs, not only in the case that showed it, and say
  in the commit body which platforms a change speeds up and which keep the old path. A path through a runtime's
  internals (`CollectionViews`: the fields of `List<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>`, ...) declares a
  layout for each implementation (.NET 64-bit and 32-bit, .NET Framework, Mono), checks it against an instance with
  known contents at startup and uses the public API when none matches; run the tests on .NET, the `net472` build under
  Mono and the `linux-arm` and `linux-arm64` builds under qemu, and keep the fallback (the enumerator) out of line.
- Building a runtime's own objects without its public API (`Nino.Immutable/HashTrees.cs`: an `ImmutableDictionary`
  or `ImmutableHashSet` built as its tree, instead of one builder `Add` at a time; `SortedSetNodes` in Nino.Core: a
  `SortedSet<T>`'s red-black tree; `ReadLinked`: a `LinkedList<T>`'s ring of nodes): one way for every runtime where
  one works, and a separate way per runtime (behind `#if` or a startup check) only where none does. What works
  everywhere so far:
  - Mirror classes that declare the fields of the real class in its order, read and written through `Unsafe.As`:
    every runtime lays out two classes with the same field list the same way. Check the field lists of every version
    of the library users may have (System.Collections.Immutable 1.5 to 10.0 have the same fields), and check the
    layout at startup against an instance with known contents, through the public API; use the public API when it
    fails.
  - Never call an instance method on a mirrored object (a `MemberwiseClone` helper in the mirror class): Mono and
    IL2CPP take a shared generic method's context from `this`'s class, and Mono aborts (an assertion, not an
    exception, so no `catch` helps). Static code only.
  - Allocate a mirror instance with `new` and set its type word to the real type's (`ObjectTypes.Retype`): the same
    fields mean the same size and the same references for the GC. Faster than `RuntimeHelpers.GetUninitializedObject`
    (13 ns on .NET 10 but 77 on .NET 8, FormatterServices 59 on Mono) and needs nothing IL2CPP may lack. The type word
    sits where the reference points (CoreCLR: method table, one header word; Mono and IL2CPP: vtable or class, then
    the monitor, two words), found at startup; call `GetType()` on a retyped object out of line (`NoInlining`), or
    the JIT folds it to the type it allocated (Arm32 did).
  - Run it on .NET 8 and 10, the `net472` build under Mono (also `MONO_GC_DEBUG=verify-before-collections`), Arm32
    under qemu, with a GC stress (forced compacting collections between builds, then every object checked).
    qemu-aarch64 crashes on allocation-heavy runs with or without Nino's code (in `ToImmutableDictionary`): keep
    Arm64 runs small there, or use an Arm64 runner.
- The signs in the listing: `Reader:Skip`, `Reader:CopyTo` or `NinoUnsafe:Read` as calls (the budget ran out);
  `NinoReadContext:Return` or `NinoWriteContext:Return` calls and `[rbp-0x..]` loads and stores of the cursor (the
  reader or writer was not kept in registers).

## Source generator

- A generator bug shows up as code that does not compile for one shape of type nobody tested (#203: every generic
  abstract class or interface). Take a name the model may leave empty (`BaseFormatter`, `DispatchRootFormatter`,
  `Construction.Creator`, ...) through `TypeModel.Need`, never by interpolating it directly. Decide what a type gets
  from the model's rules (`DispatchesSubtypes`, `IsDispatchRoot`, ...), not from a new check next to them.
- A feature that adds a way to declare a type (a kind, an attribute option, a place) adds a dimension to
  `src/Nino.UnitTests/Corner/ShapeMatrixTests.cs`, so it is compiled and round-tripped with every other one.
- Generated registration code may call back into Nino.Core (`NinoPolymorphic.Register(Type)`), which asks every
  registry again: anything a registry calls must be re-entrant (`NinoRegistry.RegisterType` guards it per thread).

## Go runtime

`runtimes/go` (package `nino`, see its README.md) reads and writes the wire format for Go; `internal/sample` holds the
corpus's types with codecs in the style a generator would write. Both performance and safety on untrusted input are
requirements:
- Never panic on any input, and never rely on `recover`: check every bound, and keep errors sticky (after a failure
  the reader returns zeros, empty slices and null, so every loop ends). Check a length against the bytes left
  before allocating, with .NET's caps (`SliceCap`, `capacity`); grow, never presize, what the data only claims.
- Count depth exactly as .NET does (`ReadClass`, `WriteClass`, regions and sections): the bytes must match, and
  the stack must stay bounded however deep the data. A polymorphic slot accepts only the ids of the types it allows.
- Hot paths: no reflection, no boxing into `any` outside polymorphic slots; a run of fixed-size members is one
  `Take`/`Extend` and `binary.LittleEndian` at constant offsets; blocks of primitives are one copy (guarded for
  big-endian hosts). Keep rare paths (`Fail`, growth, deferral) in `//go:noinline` functions. Check
  `go build -gcflags=-d=ssa/check_bce/debug=1` and `-gcflags=-m` on what you change, and keep writing a value free
  of allocations (`bench`, `-benchmem`).
- Struct tags (`tagged.go`, `layout.go`, `marshal.go`) are the one place reflection is allowed: a plan built once
  per type, then walked. Anything the wire format cannot express is a `*TagError` naming the field when the plan
  is built, never a silent default; reads go through the same `Reader` calls as generated code, so the guarantees
  above hold there too. The sample types carry the tags and `NinoLayout` a generator emits (README, "Tags on
  generated structs"); keep them and `internal/sample/tagged_test.go` in step with the hand-written codecs.
- Before committing: `go vet ./...`, `go test -race ./...` with the conformance corpus present, a short run of
  `FuzzReader`, `FuzzDecode`, `FuzzTagged` and `FuzzTaggedDecode`, and the benchmarks back to back against the
  previous commit for a change to a read or write path. The runtime's module has no dependencies; competitors live in the `bench` module.

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
