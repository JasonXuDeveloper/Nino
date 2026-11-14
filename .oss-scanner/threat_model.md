# Nino threat model

## What Nino does and where untrusted input enters

Nino is a binary serializer. A C# source generator writes a formatter for each `[NinoType]`; `Nino.Core` holds the
runtime (`NinoSerializer`, `NinoDeserializer`, `Reader`/`Writer`, the formatters for built-in and BCL types). The
same bytes are read and written by runtimes in Go (`runtimes/go`), Rust (`runtimes/rust`) and TypeScript
(`runtimes/typescript`), plus the code `nino-gen` (`src/Nino.Codegen`) writes for them. It runs on .NET
(netstandard2.1 to net10.0), Mono, Unity (Mono and IL2CPP) and NativeAOT.

**The bytes passed to any deserializer are untrusted**: network messages, save files, request bodies
(`Nino.AspNetCore`'s input formatter reads HTTP request bodies). Everything that reads them is in scope:
`NinoDeserializer.Deserialize` and its overloads (spans, `ReadOnlySequence`, streams, async streams), deserializing
into an existing instance (`ref` overloads, pooled and recycled objects), polymorphic reads by type id, keyed and
version-tolerant members, reference tracking, deferred bodies, every built-in formatter (collections, immutable
collections, `BigInteger`, `BitArray`, `CultureInfo`, `TimeZoneInfo`, multi-dimensional arrays, Unity types), and
the readers in Go, Rust, TypeScript and the generated code.

Trusted: the types a developer declares (generator and analyzer input), `nino-gen`'s input assembly and options,
`NinoSettings` values the application chooses, and what the application passes to the serializer.

## The contract a read must keep

Crafted bytes must be read or rejected with `NinoDataException` (.NET; messages start with `Nino: `) or the
runtime's documented error (Go `error`, Rust `Err`, TypeScript a thrown `NinoError`). A read must never:

- read or write outside the input or its own buffers (the .NET runtime uses `Unsafe`, pointers and SIMD; Rust has
  `unsafe` SIMD kernels; TypeScript has WebAssembly kernels);
- leave uninitialized or stale memory where the caller can see it (an array stored in the caller's slot before a
  later check rejects the data);
- allocate out of proportion to the input (lengths are checked against the bytes left before allocating);
- take time out of proportion to the input (hash flooding beyond `NinoSettings.MaxHashCollisions`, default 100;
  quadratic loops);
- recurse without a bound (`NinoSettings.MaxDepth`, default 512) or overflow the stack;
- throw any other exception type (`IndexOutOfRangeException`, `OverflowException`, `ArgumentException` from a BCL
  constructor, `OutOfMemoryException`; a Go panic, a Rust panic, a TypeScript `RangeError`);
- grow a process-wide cache without bound across many reads (names looked up from the data);
- leak memory across reads (Rust's shared-reference arena for cyclic graphs);
- accept bytes that no writer writes where the spec requires rejection (non-minimal headers, bool or presence bytes
  other than 0 or 1, keyed members that do not fill their entry), or read the same bytes differently in two runtimes.

`docs/spec/wire-format.md` §9 lists every validation requirement and states where the .NET reader is weaker.

## Modes

- **`NinoSettings.Untrusted`** (.NET) adds the checks that cost time on every read: value checks of raw structs
  (`bool` and `Nullable` flags, enum and `DateTime` ranges inside block-copied structs), seeded comparers, trailing
  bytes. Applications are told to read untrusted data with it. Go, Rust and TypeScript always check.
- **Default mode** (.NET) copies raw (unmanaged) structs as one block. A `bool` field or `Nullable` flag inside such a
  struct is not checked there (spec §9.3); that is documented behavior, not a finding (see below). Every other check
  holds in default mode too.

## Components that matter most / least

- Most: the read paths of `src/Nino.Core` (`Reader`, `Internal/` fast paths, `Internal/Simd`, the collection and
  hash-table fills in `Internal/Collections`, reads through runtime internals in `Internal/Collections/Traversal`),
  the code the generator emits for readers (`src/Nino.Generator`), `Nino.Immutable`, `Nino.AspNetCore`'s input
  formatter, `Nino.UnityTypes`, and the Go, Rust and TypeScript readers and their SIMD / WebAssembly kernels.
- Less: the write side (only the application's own objects reach it; a graph too deep or cyclic must throw
  `InvalidOperationException`, not crash), the Unity Burst kernels (`src/Nino.Unity`, must write the same bytes).
- Out of scope: `benchmarks/`, `tools/`, `.github/`, the analyzer and code fixes, `nino-gen`'s handling of its own
  input, the generator's handling of hostile source code, test-only code under `tests/`.

## How to exercise it

- `tests/Nino.UnitTests/Security` holds the hostile-input tests (`StrictReadTests`, `HashFloodingTests`,
  `UntrustedModeTests`, `UntrustedInputTests`, `ValueCheckLayoutTests`, the nesting tests);
  `Infrastructure/GuardedMemory` places bytes against an inaccessible page so an over-read crashes.
  `dotnet test tests/Nino.UnitTests -f net10.0 --filter 'FullyQualifiedName~Untrusted|FullyQualifiedName~HashFlooding|FullyQualifiedName~Strict'`.
- `tests/Nino.Fuzz` is the .NET fuzzer (`README.md`): `dotnet run -c Release -f net10.0 --project tests/Nino.Fuzz -- selftest`,
  then `-- fuzz --seconds 300` (guard pages, stale-memory poisoning, allocation and time limits) and `-- probes`.
- `tests/conformance/corpus` holds valid and malicious cases every runtime must read or reject (`index.json`); `untrusted`
  cases are ones .NET's default mode may accept.
- Platforms in the image: CoreCLR (`DOTNET_EnableHWIntrinsic=0`, `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`
  select the vector paths), Mono 6.12 (`mono tests/Nino.RuntimeTests/bin/Debug/net472/Nino.RuntimeTests.exe`, the
  netstandard2.1 build Unity uses), the netstandard2.1 build (`-p:NinoCoreTfm=netstandard2.1`).
- Go: `cd runtimes/go && go test ./...` (fuzz targets such as `FuzzReader`, `FuzzDecode`, `FuzzTagged`, `FuzzTaggedDecode`, `FuzzUtf8`).
  Rust: `cd runtimes/rust && cargo test --workspace --all-features`, `cd fuzz && cargo +nightly fuzz run check` (also `conformance`, `generated`).
  TypeScript: `cd runtimes/typescript && npm run test:node`.

## How we rate severity

- **Critical**: a write outside memory Nino owns, or code execution, from crafted bytes, in any runtime or mode.
- **High**: a read outside the input or Nino's buffers (including a crash on a guard page); stale or uninitialized
  memory reaching the caller; a crash that cannot be caught (stack overflow, access violation, Go fatal error,
  Rust abort); memory corruption in Rust `unsafe` code; unbounded allocation, time or recursion from a small input
  with default settings; unbounded cache growth across reads.
- **Medium**: an exception or panic other than the documented one escaping a read (catchable but breaks the
  contract); hash flooding or quadratic time that stays bounded but far out of proportion (more than 100 times the
  time of ordinary input of the same size); memory leaked per read; two runtimes reading the same bytes as different
  values; a required rejection (spec §9) missing in a way that changes the value read.
- **Low**: a non-canonical encoding accepted with no effect on the value; an unhelpful error message.
- A crash reached only under `Untrusted` mode rates the same as one in default mode.

## Anything to leave alone

- Unchecked `bool` fields and `Nullable` flags inside raw structs in .NET's default mode (spec §9.3, §9.10): known
  and documented; untrusted data is read with `NinoSettings.Untrusted`. The same values must still be rejected
  under `Untrusted` and in Go, Rust and TypeScript; report it there.
- Limits the application raises itself (`MaxDepth`, `MaxHashCollisions`) and the cost of data within them.
- `SHOULD` items of spec §9 that the spec already marks as unmet by the .NET reader.
- Behavior when the application registers its own unsafe custom formatter.
