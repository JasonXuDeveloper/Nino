# Nino v4 Architecture

This document describes the v4 rewrite of Nino: its goals and target frameworks here, then, in three parts under
[v4-design/](#contents), the binary format, the runtime (`Nino.Core`) and the incremental source generator
(`Nino.Generator`). v4 is a major release and is **not** wire- or API-compatible with v3 or earlier v4 betas.

## Goals

1. **Runtime performance**: match or beat MemoryPack on serialization and deserialization, with zero
   allocations beyond the deserialized objects themselves.
2. **Compile time**: a truly incremental generator. Editing one type re-generates only that type's file,
   and generated code size stays linear in the number of members.
3. **Less generated code**: collections, strings, nullable values, tuples and other built-in types are
   implemented once in `Nino.Core` as generic formatters. The generator only emits code for `[NinoType]`
   types and composes formatter type names for members.
4. **Thread safety**: every public API is safe to call concurrently. Mutable shared state is published
   copy-on-write; hot paths never write shared memory.
5. **Identical output on every target**: `netstandard2.1`, `net8.0` and `net10.0`
   produce byte-identical payloads. Each target uses its best available primitives.
6. **Reuse deserialization that plays well with object pools**: existing instances are reused when possible,
   and any instance that is discarded is passed to `NinoRecycler`.

## Target frameworks

| Project | Targets |
| --- | --- |
| `Nino.Core` (NuGet package `Nino.Serialization`) | `netstandard2.1`, `net8.0`, `net10.0` |
| `Nino.Immutable` | `netstandard2.1`, `net8.0` |
| `Nino.UnityTypes` (Unity package only) | `netstandard2.1` |
| `Nino.AspNetCore` | `net8.0`, `net10.0` |
| `Nino.Codegen` (`nino-gen`, run by the build) | `net8.0`, rolled forward to the SDK's runtime |
| `Nino.Codegen.Tool` (`nino-gen`, a .NET tool) | `net8.0`, rolled forward to the installed runtime |
| `Nino.Generator` | `netstandard2.0` (Roslyn 4.3+): generator, analyzer, suppressor |
| `Nino.Generator.CodeFixes` | `netstandard2.0` (Roslyn 4.3+ Workspaces): code fixes, IDE only |

The generator and the code fixes ship in the `Nino.Generator` package, built three times from the same sources: against Roslyn 4.3
(`analyzers/dotnet/roslyn4.3/cs`), 4.8 and 4.12 (`roslyn4.8`, `roslyn4.12`). NuGet loads the newest build the
compiler can run. Roslyn 4.3 is the oldest compiler Unity supports, so the 4.3 build, which is also what Unity
uses, sticks to 4.3 APIs. The 4.8 and 4.12 builds add only call interceptors (see
[Top-level calls and interceptors](v4-design/generator.md#top-level-calls-and-interceptors)); everything else is shared.

The `Nino` package references `Nino.Serialization` and `Nino.Generator`; `Nino.Immutable` (`netstandard2.1`, `net8.0`)
adds formatters for the `System.Collections.Immutable` types (arrays, lists, sets, sorted sets, queues,
stacks, dictionaries and sorted dictionaries).

`Nino.AspNetCore` adds MVC input and output formatters (`application/x-nino`) and Minimal API bodies and results,
reading request bodies from the request's `PipeReader` and serializing responses into its `PipeWriter`;
it asks `NinoSerializer.IsSupported(Type)` which types to claim. `Nino.Codegen` is a build package: after
`CoreCompile` its targets run `nino-gen` out of process, once per `<NinoCodegen>` item (TypeScript, Go or Rust), which
reads the `[assembly: NinoTypeLayout]`s of the built assembly and its references with `MetadataLoadContext` (see
[Generated code per type](v4-design/generator.md#generated-code-per-type)) and writes the types and codecs (and, for TypeScript, the
`nino.ts` runtime), each file only when its text changes. `Nino.Codegen.Tool` is the same `nino-gen` as a .NET tool.
Both render through one engine: a language-neutral IR built from the layouts, and Scriban templates per language that
only assemble text ([codegen.md](codegen.md)).

Generated code is compiled in the user's project, which may be Unity (C# 9). Generated code therefore only
uses C# 9 features: no static abstract interface members, no ref fields, no raw string literals, no
file-scoped namespaces. Zero-cost composition uses the struct-constrained generic pattern instead
(`where TFormatter : struct, INinoFormatter<T>`), which every runtime (CoreCLR, Mono, IL2CPP, HybridCLR)
specializes per value-type argument.

Platform-specific code lives behind `#if` in `Nino.Core` only. The fallback paths (`netstandard2.1`, for Unity and
Mono) use `System.Runtime.CompilerServices.Unsafe` and are written to produce the same bytes. .NET Framework, which
implements only netstandard2.0, is not a target (dropped in 4.0.0-rc.11); the Roslyn components stay netstandard2.0,
as Roslyn hosts require.
The test-suite runs against each `Nino.Core` build and checks golden payloads.

## Contents

* [Binary format](v4-design/binary-format.md)
  * [Objects](v4-design/binary-format.md#objects)
  * [Collections](v4-design/binary-format.md#collections)
  * [Version tolerance](v4-design/binary-format.md#version-tolerance)
  * [Keyed members](v4-design/binary-format.md#keyed-members)
  * [Reference tracking](v4-design/binary-format.md#reference-tracking)
  * [Deep nesting](v4-design/binary-format.md#deep-nesting)
* [Runtime (`Nino.Core`)](v4-design/runtime.md)
  * [Writer](v4-design/runtime.md#writer)
  * [Reader](v4-design/runtime.md#reader)
  * [Formatters](v4-design/runtime.md#formatters)
  * [Formatter registrations](v4-design/runtime.md#formatter-registrations)
  * [Formatter cache and registration](v4-design/runtime.md#formatter-cache-and-registration)
  * [Polymorphism](v4-design/runtime.md#polymorphism)
  * [Concurrency and cache lines](v4-design/runtime.md#concurrency-and-cache-lines)
  * [Reuse deserialization and pooling](v4-design/runtime.md#reuse-deserialization-and-pooling)
  * [Serialization callbacks](v4-design/runtime.md#serialization-callbacks)
  * [Settings](v4-design/runtime.md#settings)
  * [Untrusted input](v4-design/runtime.md#untrusted-input)
* [Source generator](v4-design/generator.md)
  * [Pipeline](v4-design/generator.md#pipeline)
  * [Top-level calls and interceptors](v4-design/generator.md#top-level-calls-and-interceptors)
  * [The API generated code calls](v4-design/generator.md#the-api-generated-code-calls)
  * [Sources shared with the runtime](v4-design/generator.md#sources-shared-with-the-runtime)
  * [Source layout](v4-design/generator.md#source-layout)
  * [Analyzer, suppressor and code fixes](v4-design/generator.md#analyzer-suppressor-and-code-fixes)
  * [Generated code per type](v4-design/generator.md#generated-code-per-type)
