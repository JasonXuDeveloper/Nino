# Code generation for other languages

`nino-gen` writes the `[NinoType]`s of a built .NET assembly in TypeScript, Go or Rust: one type per `[NinoType]`, with
codecs that read and write the bytes Nino writes in .NET. It reads nothing but the `[assembly: NinoTypeLayout]`
attributes the Nino generator publishes in every assembly it compiles
([v4-design.md](v4-design.md#generated-code-per-type)), so it needs no source and no running code: only the built
`.dll` and the assemblies it references.

It comes in two packages, with the same options and the same output:

- **Nino.Codegen**, a build package: after each compile of the project that references it, it writes what the
  project's `<NinoCodegen>` items ask for ([In a build](#in-a-build)).
- **Nino.Codegen.Tool**, the `nino-gen` .NET tool, for a built assembly from the command line ([Usage](#usage)).

```sh
dotnet add package Nino.Codegen --prerelease
```

```xml
<ItemGroup>
  <NinoCodegen Include="typescript" Out="../web/src/model" ImportExtension=".js" />
  <NinoCodegen Include="go" Out="../server/model" Package="model" Tags="json" />
  <NinoCodegen Include="rust" Out="../client/src" Name="model.rs" Serde="true" />
</ItemGroup>
```

```sh
dotnet tool install -g Nino.Codegen.Tool --prerelease
nino-gen --assembly bin/Release/net8.0/MyGame.Shared.dll --lang go --out ../server/protocol
```

Each language needs its runtime next to the generated code:

- **TypeScript**: `nino.ts`, which nino-gen writes next to the generated file (unless `--runtime` names a module to
  import it from, such as the npm package `nino-serialization`).
- **Go**: the module `github.com/JasonXuDeveloper/Nino/runtimes/go/v4` (package `nino`), a dependency of your module
  (`go get github.com/JasonXuDeveloper/Nino/runtimes/go/v4@v<Nino's version>`).
- **Rust**: the crate `nino-serialization` (`runtimes/rust`, on crates.io with Nino's version) with its `indexmap` feature, and `indexmap` itself (maps
  and sets are `IndexMap` and `IndexSet`, which keep the wire order); with `--maps std`, the crate alone (maps and sets
  are `std::collections::HashMap` and `HashSet`); with `--serde`, a `serde` feature of your crate that turns on `serde`
  and `nino-serialization/serde`.

## Usage

```
nino-gen --assembly <dll> --lang ts|go|rust --out <dir> [--references <file>] [--name <file name>]
  ts:   [--names camel|keep] [--import-extension <.js|.ts|.mjs|.mts>] [--runtime <module>]
  go:   [--package <name>] [--tags json,yaml] [--tag-names camel|snake|original]
  rust: [--serde] [--serde-names camel|snake|original] [--strings string|wide] [--maps indexmap|std]
```

| Option | Meaning |
| --- | --- |
| `--assembly` | The built assembly. Its `[NinoType]`s are generated, and the ones of the assemblies it references that it uses. |
| `--lang` | `ts` (or `typescript`), `go` or `rust`. |
| `--references` | A file with one path per line: assemblies to resolve references from (the `.dll`'s directory and the running runtime are searched anyway). A class library's `bin/` holds no copy of its packages (`Nino.Core.dll` among them): pass them here (a build with Nino.Codegen writes this file as `obj/<configuration>/<framework>/nino-gen/references`). |
| `--out` | The directory to write to. A file is written only when its text changes, so watchers and incremental builds see no change. |
| `--name` | The generated file's name: `<Assembly>.ts`, `nino_types.go` or `nino_types.rs` by default. |
| `--names` (ts) | `camel` (default) or `keep`: the property names. |
| `--import-extension` (ts) | The extension of the runtime import, for ESM resolvers that need one. |
| `--runtime` (ts) | Import the runtime from this module instead of writing `nino.ts` next to the code. |
| `--package` (go) | The package name; by default the assembly name's letters and digits, lower-cased. |
| `--tags` (go) | The struct tags after `nino`, in order; `json,yaml` by default, empty for none. |
| `--tag-names` (go) | How those tags name a member: `camel` (default), `snake` or `original` (runtimes/go/README.md, "Tags on generated structs"). |
| `--serde` (rust) | Adds `#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize))]` and a rename per field. |
| `--serde-names` (rust) | The serde names: `camel` (default, as `System.Text.Json`), `snake` or `original`. |
| `--strings` (rust) | `string` (default): C# strings are `String`, and an unpaired surrogate is read as U+FFFD. `wide`: `nino::WideString`, which keeps any UTF-16. |
| `--maps` (rust) | `indexmap` (default): dictionaries and sets are `indexmap::IndexMap` and `IndexSet`, which keep the order they were read in, so a value read is written back byte for byte. `std`: `std::collections::HashMap` and `HashSet`, with no dependency but the crate; they are written in `HashMap`'s iteration order, which is not the order .NET wrote and changes from run to run (.NET reads them the same, but the bytes differ). |

An option that is unknown, given twice, of another language or with a value it does not take is an error, as are
missing arguments. `--serde` may be followed by `true` or `false`. Warnings (a type left out, and why) are printed as
`nino-gen : warning NINOGEN002: ...`; errors as `nino-gen : error NINOGEN001: ...` with exit code 1
([diagnostics.md](diagnostics.md#code-generation-for-other-languages)).

Each file is written only when its text changes. The files a run writes are listed in `.nino-gen` in the output
directory, and the next run deletes those it no longer writes (after a renamed assembly, a new `--name`, or `--runtime`
given). Other files in the directory are left alone.

## In a build

The Nino.Codegen package runs nino-gen after `CoreCompile`, once per `<NinoCodegen>` item. The item is the language
(`typescript` or `ts`, `go`, `rust`); every other metadata is the option of the same name, in PascalCase:

| Metadata | Option | Languages |
| --- | --- | --- |
| `Out` | `--out`: relative to the project; `ts/`, `go/` or `rust/` next to it by default | all |
| `Name` | `--name` | all |
| `Names`, `ImportExtension`, `Runtime` | `--names`, `--import-extension`, `--runtime` | TypeScript |
| `Package`, `Tags`, `TagNames` | `--package`, `--tags`, `--tag-names` | Go |
| `Serde`, `SerdeNames`, `Strings`, `Maps` | `--serde` (`true` or `false`), `--serde-names`, `--strings`, `--maps` | Rust |

(MSBuild reserves the metadata name `Output`, hence `Out`.) The build passes the assembly it compiled and its references
itself, and nino-gen checks the metadata as it checks the command line: an option of another language, or a value it
does not take, fails the build with NINOGEN001.

| Property | Meaning |
| --- | --- |
| `NinoCodegenTargetFramework` | The target framework whose build writes the files, when the project has several (default: the first in `TargetFrameworks`). |
| `NinoCodegenEnabled` | `false` turns it off. |

An item runs again only when the assembly, its references, the tool or the item's metadata change; a design-time build
(an IDE loading the project) never runs it. A project that references the package without an item gets warning
NINOGEN004. The package is a development dependency: nothing flows to the projects that reference the project, which
declare their own items if they want their own types written.

### From Nino.TypeScript

Nino.TypeScript (`nino-ts`, 4.0 release candidates up to rc.7) is replaced by Nino.Codegen: replace the package
reference, and declare the item its properties described:

| Nino.TypeScript | Nino.Codegen |
| --- | --- |
| `<PackageReference Include="Nino.TypeScript" ... />` | `<PackageReference Include="Nino.Codegen" ... />` |
| (always on) | `<NinoCodegen Include="typescript" Out="ts/" />` |
| `NinoTypeScriptOutput` | `Out` |
| `NinoTypeScriptImportExtension` | `ImportExtension` |
| `NinoTypeScriptNames` | `Names` |
| `NinoTypeScriptTargetFramework` | `NinoCodegenTargetFramework` |
| `NinoTypeScriptEnabled` | `NinoCodegenEnabled` |

The output is the same, except that the header comment names `nino-gen` rather than `Nino.TypeScript`; the `.nino-ts`
manifest is taken over (and deleted) by the first run. Until the item is declared, the `NinoTypeScript*` properties
still work: without a `<NinoCodegen>` item they stand for `<NinoCodegen Include="typescript" ... />` (with `ts/` as
before when `NinoTypeScriptOutput` is not set), with warning NINOGEN003. The diagnostics `NINOTS001` and `NINOTS002`
are now `NINOGEN001` and `NINOGEN002`.

## How it works

`src/Nino.Codegen` has three parts:

1. **Layouts** (`Layouts/`): `LayoutReader` reads the `NinoTypeLayout` attributes with `MetadataLoadContext`, and
   `WireEncoding` parses each member's encoding (`raw(4)`, `seq(string)`, `map(K,V)`, `type(T)`, ...).
   `ClosedTypeScan` reads the closed generic types the assembly's metadata names ([Closed generic types in
   slots](#closed-generic-types-in-slots)).
2. **The IR** (`Model/`): `CodegenModel` turns the layouts into a language-neutral model: each type's kind (raw struct,
   struct, sealed or open class, interface or abstract slot), format (positional, version-tolerant, keyed), header,
   deferral, members with their value shapes, the subtypes a slot allows, enums, and the generic definitions. Every
   decision about the bytes is made here, once, for all languages.
3. **Targets and templates** (`Targets/`, `Templates/<language>/*.sbn`): each target maps the IR to its language
   (names, types, which helper reads a value, what cannot be expressed) in C#, and hands the result to Scriban
   templates embedded in the tool. The templates only assemble text: they loop over what the target gives them and
   call the target's functions; they make no decision of their own.

A change to the bytes goes into the IR; a change to how a language spells something goes into its target; a template
changes only the text around them. The options are defined once, in `Options.cs` (`CodegenOptions.All`): the name
on the command line, the `<NinoCodegen>` metadata that sets it in a build, the language and the values it takes. The
command line, its usage text and the build targets all work from that list.

## Generic types

A generic `[NinoType]` is generated as a generic type in each language, and its codec is built from the codecs of its
type arguments, as .NET builds `NinoFormatter<Box<T>>` from the formatter of `T`:

| | `Box<T>` | Its codec | `Box<int>` as a member |
| --- | --- | --- | --- |
| TypeScript | `class Box<T>` | `BoxCodec<T>(p0: NinoCodec<T>): NinoCodec<Box<T> \| null>` | `Box<number> \| null`, through a codec the module builds once |
| Go | `type Box[T any] struct` | `BoxCodec[T any](p0 NinoArg[T]) NinoArg[*Box[T]]` (`NinoArg` holds `Read`, `Write` and `MinSize`) | `*Box[int32]`, through a codec built in `init` |
| Rust | `struct BoxType<T>` (`Box` is taken) | `impl<T: NinoRead + NinoWrite + Default + 'static> NinoRead for BoxType<T>`... | `Option<Box<BoxType<i32>>>` |

Closed uses (`Box<int>`, `Pair<int, string>`, `Box<Box<int>>`, `List<Box<int>>`, `Bag<Box<long>>`) name the generic
type with its arguments; inside a generic type, its parameters may appear anywhere a member type can (`T`, `List<T>`,
`Dictionary<string, T>`, `T[]`, `T?` where `T : struct`, tuples, `Box<T>`, `Pair<int, T>`, the type itself as
`Node<T>`). A type argument may be a primitive, an enum, a string, a `[NinoType]` (generic ones included), an array,
`List<T>`, `HashSet<T>`, `Dictionary<TKey, TValue>`, a `ValueTuple`, and, outside collections, a `Nullable<T>` or
`KeyValuePair<TKey, TValue>`.

Not generated, with a warning (NINOGEN002):

- A generic type written behind a type id wherever it is declared (a non-sealed class, an abstract class or an
  interface): a member declared as `Base<int>` would be a slot of its own per closed type, which no target builds.
- A generic type that holds itself with other arguments (`Node<T>` holding `Node<List<T>>`), directly or through
  another generic type: its codecs would be built without end.
- A member whose type argument is a `Nullable<T>` or `KeyValuePair` inside a collection (`Box<int?[]>`), or any type
  not in the list above.

### Closed generic types in slots

A generic subtype (`sealed class Box<T> : Animal`, or a generic struct implementing a `[NinoType]` interface) is written
in a polymorphic or `object` slot behind the hash of its closed .NET name (`Conformance.Box<System.Int32>`). Another
language cannot form that name from the definition, and the layouts carry only the definition (`Box<T>`), so nino-gen
reads the closed types from the assembly's metadata instead: every closed type a signature, a type specification
(`new Box<int>()`, `typeof(Box<int>)`) or a generic method's arguments (`NinoPolymorphic.Register<Box<int>>()`) name.
The ones whose definition is a concrete generic `[NinoType]` are cases of the slots they are assignable to (`object`
slots take them all), each read and written with the codecs of its arguments:

| | `Box<int>` in an `Animal` slot |
| --- | --- |
| TypeScript | `class BoxInt32 extends Box<number> {}`: the slot's writer switches on the constructor, so values to write are `new BoxInt32()`, and a codec of `Box<int>` reads `BoxInt32`s (`BoxCodec` takes the class to build) |
| Go | a case `*Box[int32]` in `ReadAnimal`/`WriteAnimal`, through a `*boxCodec[int32]` built in `init`; `Box[T]` gets `NinoTypeID()`, `NinoTypeName()` (the closed name) and the slots' marker methods |
| Rust | a variant `AnimalRef::BoxInt32(Box<BoxType<i32>>)` (and in `AnyObject`) |

A closed type no code in the assembly names cannot be read: its id is unknown to every target, as it is to .NET in a
process that never registered or wrote it. The other way round, the targets accept every closed type the assembly
names, where .NET in a fresh process accepts only those it has registered or already written: data .NET would reject
for an id it has not seen yet can read. With its `NinoPolymorphic.Register<...>()` calls made, .NET accepts the same
set.

A closed generic `[NinoType]` struct (`Pair<byte, int>`) is written member by member wherever it appears, as a member,
an element or a type argument, as .NET's formatter for its definition writes it: a field of a type parameter is never
copied as raw memory, whatever the arguments.

## TypeScript

A class per type, an `as const` object per enum and a `NinoCodec` per type, with `nino.ts` next to them. The bodies of
types without members (`readBody$Empty`, `writeBody$Empty` and the version-tolerant ones) name their parameters `_r`,
`_w` and `_v`, so the code compiles under `noUnusedParameters`. See the
[TypeScript guide](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) for the generated API.

`nino.ts` embeds a small WebAssembly module (SIMD, GC and the JS String Builtins) that reads and writes longer text
in browsers and Deno; where WebAssembly is unavailable or blocked (a Content Security Policy without
`'wasm-unsafe-eval'`), the runtime keeps its JavaScript paths, which write the same bytes (runtimes/typescript/README.md,
"Text in browsers: WebAssembly").

A `Date` has no kind: the reader takes the ticks of every `DateTime` as UTC, so one written with
`DateTimeKind.Unspecified` (or `Local`) reads as the UTC time with the same clock reading, and is written back with
`DateTimeKind.Utc`.

## Go

A struct per `[NinoType]` with `nino` tags first, then the configured tags (runtimes/go/README.md, "Tags on generated
structs"), a `NinoLayout()` method returning the published layout, `NinoTypeName()`, `NinoTypeID()` on classes behind
a type id, and read and write functions in the shape `runtimes/go/internal/sample` shows: one `Take`/`Extend` per run
of fixed-size members, no reflection. Enums are named integer types with constants. Polymorphic slots are an
interface with one implementation per allowed subtype.

Helper types it declares are named `NinoTuple2`..., `NinoGrid`; a user type whose name starts with `Nino` gets a `Type`
suffix, and two types with the same simple name are prefixed with their namespace (`ShapesLeftItem`). `[NinoFormerName]`s
are not in the `nino` tag: layouts carry only their hashes, which `NinoLayout()` keeps, so a keyed type still reads data
written under a former name.

A C# `string` member is a `*string`, so null stays null. A Go `string` has no null: where one is read
(`Reader.String`, a `string` field of a struct with `nino` tags, dictionary keys and `HashSet<string>` elements), a
null string reads as `""`, and `""` is written back, not null. Use `*string` to tell null from empty.

## Rust

The Rust target writes the codecs out in full (`impl NinoReadBody`, `NinoWriteBody`, `NinoRead`, `NinoWrite` for
each type) rather than `#[derive(NinoRead, NinoWrite)]` attributes. The reasons:

- It needs only the `nino-serialization` crate, not the derive crate and its proc-macro build.
- The derive takes former names as strings; a layout carries only their hashes, which the expanded code checks
  directly.
- The shape is the one `tests/conformance/gen.py` proved on the whole corpus (and which the derive generates), so the
  performance and safety rules of runtimes/rust hold: one bounds check per run of fixed-size members, collections
  reserve through `r.capacity`, structs count depth through `enter_struct`/`exit_struct`, raw structs check padding.

Each type gets `TYPE_ID`, `NINO_TYPE_NAME` and `NINO_LAYOUT` constants. A slot holding several types is an enum
`<Base>Ref` with a boxed variant per allowed subtype; an `object` slot is `AnyObject`. Enums are integers with a unit
struct holding the named values as constants. `with_nino_type(name, visitor)` calls a `NinoVisit` visitor with the
type of a .NET name.

Include the file with rustfmt off, since it is generated:

```rust
#[rustfmt::skip]
#[path = "nino_types.rs"]
mod protocol;
```

## What is not generated

nino-gen warns and leaves a type out when its language cannot express it, and the types that use it with it:

- **Some generic types**: those written behind a type id, those that hold themselves with other arguments
  ([Generic types](#generic-types)), and, in slots, closed generic types the assembly does not name.
- **Reference tracking** (`[NinoType(trackReferences: true)]`) and **`[NinoType]` collections** (a class deriving from
  `List<T>`; the collection itself is generated as an element type when its elements can be).
- **TypeScript**: the types the TypeScript guide lists.
- **Rust**: maps and sets whose key or element is not hashable in Rust (`f32`, `f64`, and types containing them),
  raw tuples with padding, and nullable raw values outside sequences.
- `HashSet<string>` elements and dictionary keys are non-null strings in Go and Rust, since .NET never writes a null
  key there.

## Tests

`tests/Nino.Codegen.Tests` (in Nino.slnx) checks the IR, each target and the command line, and that the generated files
committed under the runtimes match what nino-gen writes today. `tests/Nino.Codegen.Integration.Tests` runs the
TypeScript of its samples under Node against the bytes .NET writes, round-trips its generic samples through the
generated Go (`go run`) and Rust (`cargo run`, with each `--maps`) (inconclusive without `go` or `cargo` on the
PATH, a failure in CI, which sets them up), and builds scratch projects with
`build/Nino.Codegen.targets` (every language, incremental runs, the `NinoTypeScript*` properties, design-time builds,
and that the targets pass every option `Options.cs` defines). Each runtime then runs its corpus check over the generated code
(every valid case written back byte for byte, every malicious case and truncated prefix rejected):

| Runtime | Generated files | Check |
| --- | --- | --- |
| TypeScript | `runtimes/typescript/test/generated/` | `runtimes/typescript/test/corpus.test.ts` (`npm test`) |
| Go | `runtimes/go/internal/generated/` | `go test ./internal/generated/...` |
| Rust | `runtimes/rust/tests/generated/` | `cargo test --features indexmap --test generated` (plus a short fuzz) |

After a change to the IR, a target or a template, regenerate them and commit the result with the change:

```sh
NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests
```

then run the three runtime checks. Each runtime must reject every malicious case (or may read it, where the case allows
either).

## Benchmarks

Each runtime's benchmark writes machine-readable output (`NINO_BENCH_JSON` for TypeScript, `go test -bench` text,
criterion's JSON), and the release workflow (`report.yml`) appends one section per language to the release notes
with `benchmarks/runtimes_report.py`, after the .NET sections: x64, then the same on an Arm64 runner.
