# TypeScript, Go and Rust (Nino.Codegen, nino-gen)

nino-gen writes the `[NinoType]`s of a built .NET assembly as TypeScript, Go or Rust types with codecs that read and
write the bytes .NET writes. It reads only the `[assembly: NinoTypeLayout]` metadata the generator publishes (no
source, no running code), from the assembly and the referenced assemblies it uses. It runs on .NET 8+. Full guide:
https://nino.xgamedev.net/en/docs/v4/advanced/codegen.md

## In a build

```shell
dotnet add package Nino --prerelease
dotnet add package Nino.Codegen --prerelease   # development dependency (PrivateAssets="all")
```

```xml
<ItemGroup>
  <NinoCodegen Include="typescript" Out="../web/src/model" ImportExtension=".js" />
  <NinoCodegen Include="go" Out="../server/model" Package="model" Tags="json" />
  <NinoCodegen Include="rust" Out="../client/src" Name="model.rs" Serde="true" />
</ItemGroup>
```

- One item per output; the item is the language (`typescript` or `ts`, `go`, `rust`), other metadata are the options
  in PascalCase (`Out`, `Name`; TS `Names`, `ImportExtension`, `Runtime`; Go `Package`, `Tags`, `TagNames`; Rust
  `Serde`, `SerdeNames`, `Strings`, `Maps`). Without `Out`: `ts/`, `go/` or `rust/` next to the project.
- Properties: `NinoCodegenTargetFramework` (which target framework's build writes, default the first),
  `NinoCodegenEnabled=false` to turn it off.
- Files are written only when their text changes; stale files listed in `.nino-gen` are deleted.

## From the command line

```shell
dotnet tool install -g Nino.Codegen.Tool --prerelease
nino-gen --assembly bin/Release/net8.0/MyGame.Shared.dll --lang go --out ../server/protocol
```

```
nino-gen --assembly <dll> --lang ts|go|rust --out <dir> [--references <file>] [--name <file name>]
  ts:   [--names camel|keep] [--import-extension <.js|.ts|.mjs|.mts>] [--runtime <module>]
  go:   [--package <name>] [--tags json,yaml] [--tag-names camel|snake|original]
  rust: [--serde] [--serde-names camel|snake|original] [--strings string|wide] [--maps indexmap|std]
```

A class library's `bin/` has no copy of its packages (`Nino.Core.dll`): pass them with `--references` (a file with
one path per line).

## Runtimes

| Language | Runtime | Use |
| --- | --- | --- |
| TypeScript (5.7+, ES2020) | `nino.ts`, written next to the code; or npm `nino-serialization` with `Runtime="nino-serialization"` | `serialize(OrderCodec, order)`, `serializeInto(OrderCodec, order, writer)` (a reused `NinoWriter`, or a `Uint8Array`), `deserialize(OrderCodec, bytes)`, `deserializeExact` |
| Go (1.22+) | `go get github.com/JasonXuDeveloper/Nino/runtimes/go/v4` (package `nino`, no dependencies) | `nino.Serialize(p, model.WritePlayer)`, `nino.Append(buf[:0], p, model.WritePlayer)`, a reused `*nino.Writer` (`Reset`, `Bytes`), `nino.Deserialize(data, model.ReadPlayer)`, `nino.DeserializeExact` |
| Rust (1.88+) | crate `nino-serialization` (library `nino`) with feature `indexmap`, plus `indexmap`; `Maps="std"` needs the crate alone | `nino::serialize(&v)?`, `nino::serialize_into(&v, &mut buf)?` (appends to a reused `Vec`), `nino::deserialize_exact::<T>(&bytes)?` |

The runtimes are versioned with Nino. Include the generated Rust file with rustfmt off:

```rust
#[rustfmt::skip]
#[path = "nino_types.rs"]
mod protocol;
```

## Mapping notes

- Polymorphic slots: TypeScript classes switched on by constructor, Go interfaces, Rust enums `<Base>Ref` (`AnyObject`
  for `object`). A type id the slot does not allow is rejected.
- Reference tracking: TypeScript returns the same object, Go the same pointer, Rust `nino::Shared<T>`.
- TypeScript: arrays and lists of fixed-size numbers are `number[]` (`bigint[]` for 64-bit integers); `byte` sequences
  are `Uint8Array`. With `--numeric-arrays typed` (`NumericArrays="typed"`) they are typed arrays (`int[]` is
  `Int32Array`, `long[]` `BigInt64Array`, `double[]` `Float64Array`), copied in one block: assign `Int32Array.of(...)`,
  not `[...]`, and `slice()` one read before transferring its `.buffer` (they share a 64 KB buffer). Faster to
  serialize everywhere and to deserialize in Node; Chromium deserializes them 2-3x slower than arrays.
- Go: a C# `string` member is `*string` (null stays null); a TypeScript `Date` reads every `DateTime` as UTC.
- `deserialize` leaves trailing bytes unread; the `Exact` variants reject them.
- Caller-owned buffers, as `NinoSerializer.Serialize(value, bufferWriter)` with a reused `NinoArrayBufferWriter`:
  TypeScript `serializeInto` into a `NinoWriter` reset per value (in Node, a new `ArrayBuffer` per value adds up to
  full garbage collections) or into a `Uint8Array` (returns the size; larger than the array: nothing written), Go
  `Append` or a reused `Writer`, Rust `serialize_into`. Reading into an existing value
  (`Deserialize(data, ref value)`) is .NET only: the other runtimes return new values.
- Every reader takes hostile input with the .NET checks (bounds before allocating, depth counted as .NET does, strict
  bytes) and throws or returns one error type.

## Left out (NINOGEN002)

- TypeScript: multi-dimensional arrays, `DateTimeOffset`, `BigInteger`, `Version`, `BitArray`, `TimeZoneInfo`, `Uri`,
  `CultureInfo`, `StringBuilder`, `Memory<T>`, `ReadOnlyMemory<T>`, `ReadOnlySequence<T>`, `PriorityQueue`, `ILookup`,
  `IGrouping`, tuples over 7 items, and members written by custom formatters, `Nino.Immutable` or the Unity classes.
- Go and Rust: generic types that hold themselves with other type arguments. Go: closed generic types of one
  definition that map to the same Go type, after the first.
- All: generic types with `trackReferences`; closed generic types nobody names in the assembly.
- Rust: maps and sets keyed by `f32`/`f64` (or types containing them), raw tuples with padding, nullable raw values
  outside sequences.

The types that hold a left-out type are left out too. For a browser client that needs them, run the C# types on .NET
WebAssembly instead (https://nino.xgamedev.net/en/docs/v4/advanced/browser.md).
