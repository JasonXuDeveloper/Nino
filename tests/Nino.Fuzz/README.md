# Nino.Fuzz

Fuzzing for Nino's deserializer: data from an untrusted source must be read or rejected with Nino's documented
exception (a `NinoDataException` whose message starts with `Nino: `), never anything else. CI runs it on every pull
request and nightly (`.github/workflows/fuzz.yml`).

It has three parts:

- **The mutational fuzzer** (`fuzz`): mutates the bytes of sample values of each root type (`Targets.cs`), reads
  them, and checks what happened. It runs each target in a worker process under a supervisor, so an input that kills
  the process is saved like any other finding.
- **Probes** (`probes`): hand-made inputs that mutation does not reach, such as 100,000 levels of nesting or 50,000
  keys that all hash to 0, each run in its own process with a timeout.
- **libFuzzer targets** (`libfuzzer/run.sh`): the same root types under coverage-guided fuzzing, through SharpFuzz.

## What counts as a finding

For each input, the fuzzer:

- places it against an inaccessible guard page, at the end or at the start (`mmap`/`mprotect` on Linux and macOS,
  `VirtualAlloc`/`VirtualProtect` on Windows; plain arrays elsewhere), so reading one byte outside the input crashes
  the worker. A third of the inputs go in plain arrays;
- reads it fresh, or (a quarter of them; every one for the `Stale` targets) into an existing value, either a fresh
  sample or the value read from another input (`prime.bin` in the finding);
- treats as findings:
  - `EXCEPTION`: any exception other than Nino's documented one;
  - `CRASH`: a stack overflow, an access violation (a read past a guard page) or another fatal error;
  - `HANG`: a read that does not return within `--hang-seconds` (20 s);
  - `SLOW`: a read slower than `--slow-ms` (250 ms) twice in a row;
  - `ALLOC`: a read that allocates more than `--alloc-factor` (64) bytes per input byte plus 1 MB
    (`GC.GetAllocatedBytesForCurrentThread`);
  - `INVARIANT`: a collection in the result that breaks its own invariants through its public API (`Invariants.cs`:
    Count against enumeration, every item found again by lookup, sorted collections in order, linked lists without
    loops). These catch collections Nino builds from their internals;
  - `STALE`: data rejected while read into an existing value left freed heap memory in it (`StaleMemory.cs`).
    Large arrays of unmanaged values are allocated uninitialized; one that takes its slot before a check that can
    still reject the data (a `bool`, a has-value byte, an `Untrusted` check) leaves the value holding whatever the
    memory held, which reads as valid values. So a target marked `Poisoned` (`Stale`, `StaleUntrusted`) reads each
    rejected input again over freed memory filled with a pattern tied to the address, and fails when any array of
    unmanaged values the existing value reaches (through private fields too) holds 16 or more bytes of a 64-byte line
    of it;
  - `WRITE`, `REREAD`, `REWRITE`, `ROUNDTRIP`: Nino cannot write what it read, or what it reads back differs. The
    comparison is of the bytes, then, when they differ, of the values with the items of hash sets and dictionaries
    sorted: a rebuilt `ConcurrentDictionary` enumerates in another order (it depends on its growth), and that is
    not a finding.

Each finding is minimized (chunks removed, then bytes zeroed, while the same signature still shows) and saved once per
signature in `findings/<target>/<kind>-<hash>/` (a crash whose signature is already saved is only counted in
`seen.txt`, not minimized again, so a crash that recurs does not use up the run):

- `input.bin`: the input as the fuzzer generated it, `min.bin` the minimized one, `prime.bin` the input whose value
  a reuse read went into;
- `info.txt`: the signature, the seed and iteration, the placement, the machine (runtime, SIMD support, `DOTNET_*`
  variables), the command that reproduces it, the minimized input as hex (ready for a unit test), and the stack trace
  or the crash's stderr.

## Running it locally

From the repository root:

```sh
dotnet run -c Release --project tests/Nino.Fuzz -- fuzz --seconds 300
```

The time is shared by all targets. Useful options (`dotnet run --project tests/Nino.Fuzz -- help` lists them all):

- `--targets Colls,Imm` runs only these targets (`list` shows them and what each covers);
- `--seed 7` changes the inputs: the same seed, target and start produce the same inputs on any machine;
- `--iterations 100000` stops each target after that many inputs instead of the time;
- `--findings dir` puts the findings elsewhere (default `tests/Nino.Fuzz/findings`, which git ignores);
- `--no-probes` skips the probes; `--strict-known` lets the known open bugs fail the run (below).

Run it with each SIMD level, as CI does, since every vector path is its own code:

```sh
DOTNET_EnableHWIntrinsic=0 dotnet run -c Release --project tests/Nino.Fuzz -- fuzz
DOTNET_EnableAVX512=0      dotnet run -c Release --project tests/Nino.Fuzz -- fuzz
DOTNET_EnableAVX2=0        dotnet run -c Release --project tests/Nino.Fuzz -- fuzz
```

`selftest` checks the fuzzer itself: hidden `SelfTest` targets with planted bugs must yield a stack overflow, an
access violation, a hang, an exception and stale memory. CI runs it first.

The exit code is 0 without findings and 1 with findings or a failed probe.

## Reproducing a finding

Run the command from its `info.txt`, with the same `DOTNET_*` variables:

```sh
dotnet run -c Release --project tests/Nino.Fuzz -- repro Colls tests/Nino.Fuzz/findings/Colls/exception-0123456789/min.bin --placement end
```

`repro` runs the input once with the same checks and prints the outcome with the full exception; a crash kills the
process as it did in the worker. Options: `--placement end|start|heap`, `--reuse` (read into a fresh sample),
`--prime prime.bin` (read into the value of that input), `--minimize` (writes the smallest input with the same
finding next to the file, as `<file>.min`: for libFuzzer's crash files). To keep a finding fixed, copy the hex from `info.txt` into a
unit test in `tests/Nino.UnitTests/Security/UntrustedInputTests.cs`.

## Known open bugs

`probes` lists two problems that are being fixed elsewhere:

- `tree`, `dtree`: a class deriving from a collection of itself (`class Tree : List<Tree>`,
  `class DTree : Dictionary<string, DTree>`) nests without Nino's depth limit, and 200,000 levels overflow the stack;
- `hash`, `hash-set`: a `Dictionary<long, int>` or `HashSet<long>` of 50,000 keys that all hash to 0 takes seconds
  to read (the limit is 1 s or 50 times the same number of spread keys).

They are marked `Known` in `Probes.cs`, which reports them as `KNOWN` without failing the run. Pass `--strict-known`
(or set `NINO_FUZZ_STRICT_KNOWN=1`; in CI, the repository variable `NINO_FUZZ_STRICT_KNOWN`) to fail on them. Once a
fix is merged the probe prints `PASS (known issue looks fixed ...)`: remove its `Known` note so it guards against the
bug coming back. Every other probe (nesting through a list, a struct array, a dictionary, `Lazy`, `KeyValuePair` and
`Tuple`, 100,000 levels on a 1 MB stack) is strict.

## Adding a root type

1. Declare the type in `Types.cs`. Give it members that reach the feature you want fuzzed: the fuzzer only mutates
   bytes, so a format the samples never write is barely reached.
2. Add a sample value in `Samples.cs`, with fixed contents (no random `Guid`, no clock): a non-empty instance of each
   member, and a second, larger or sparser sample if the type's paths depend on sizes (`CollsLarge`).
3. Add a line to `Targets.All` in `Targets.cs`: `Make("Name", "what it covers", Samples.X, Samples.XLarge)`.
4. If its collections have invariants the checks miss, add a `Check<Collection>` method to `Invariants.cs` (named after
   the generic type definition; it is found by name).
5. Run `fuzz --targets Name` for a few minutes, with and without SIMD. The worker first checks that every seed reads
   back, writes and reads again unchanged.

A collection or value whose read allocates its array uninitialized (`NinoUnsafe.NewArray`, from 6 KB) and can still
reject the data after that is a member of `Stale` in `Types.cs` (with its elements in `Samples.Stale`, an empty or
one-element value in `Samples.StaleExisting`), not a new target: a poisoned target costs a collection per rejected
input. `StaleUntrusted` reads the same type with `NinoSettings.Untrusted` on (`Target.Untrusted`).

A problem that needs a precise input instead of mutations (thousands of nesting levels, crafted hashes) is a probe:
add it to `Probes.All` in `Probes.cs`.

The golden-bytes target reads the pinned bytes of `tests/Nino.UnitTests/Serialization/GoldenBytesTests.cs`,
compiled into `Golden/Nino.Fuzz.Golden.csproj` with `WEAK_VERSION_TOLERANCE` as in the unit tests (an assembly of its
own, since that symbol makes every type of an assembly version tolerant).

## libFuzzer

`libfuzzer/run.sh [seconds per target] [target ...]` runs each target under libFuzzer, guided by the coverage of
Nino.Core, Nino.Immutable and the generated formatters:

```sh
tests/Nino.Fuzz/libfuzzer/run.sh 120 Colls Imm
```

It needs clang with libFuzzer (Ubuntu: `clang` and `libclang-rt-<version>-dev`) and installs the `sharpfuzz` tool
(SharpFuzz.CommandLine 2.3.0) if it is not on the `PATH`. It:

- builds `libfuzzer/driver.cc`, a small libFuzzer driver that speaks the protocol of SharpFuzz 2.x (shared memory for
  the coverage counters and the input, two pipes), the one libfuzzer-dotnet implements, so nothing is downloaded but
  NuGet packages;
- builds Nino.Fuzz into `bin/libfuzzer/out` and instruments it with `sharpfuzz` (all of Nino.Core and Nino.Immutable,
  the `NinoGen` namespace of the fuzzer's assemblies);
- starts it through `libfuzzer/Launcher`, which points SharpFuzz's coverage map at a scratch buffer before Nino.Fuzz
  loads: the generated registry is a module initializer that runs instrumented code before any `Main`, and would
  write through a null map otherwise;
- runs each target from its seeds (`seeds` command), keeping the corpus in `bin/libfuzzer/corpus/<target>` and
  crashing inputs in `bin/libfuzzer/findings/<target>/crash-*`. A crash is any finding of the mutational fuzzer
  except `SLOW` (libFuzzer's `-timeout` covers it); reproduce it with `repro`.

The targets read each input fresh, against the end guard page for an even length and the start one for an odd
length. SharpFuzz instruments .NET 10 assemblies fine; it runs on Linux only (its shared memory is System V).
