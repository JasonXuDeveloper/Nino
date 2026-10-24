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
  import it from).
- **Go**: the module `github.com/JasonXuDeveloper/Nino/runtimes/go` (package `nino`), a dependency of your module.
- **Rust**: the crate `nino-serialization` (`runtimes/rust`) with its `indexmap` feature, and `indexmap` itself (maps
  and sets are `IndexMap` and `IndexSet`, which keep the wire order); with `--serde`, a `serde` feature of your crate
  that turns on `serde` and `nino-serialization/serde`.

## Usage

```
nino-gen --assembly <dll> --lang ts|go|rust --out <dir> [--references <file>] [--name <file name>]
  ts:   [--names camel|keep] [--import-extension <.js|.ts|.mjs|.mts>] [--runtime <module>]
  go:   [--package <name>] [--tags json,yaml] [--tag-names camel|snake|original]
  rust: [--serde] [--serde-names camel|snake|original] [--strings string|wide]
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
| `--strings` (rust) | `string` (default): C# strings are `String`, and a string with an unpaired surrogate fails to read. `wide`: `nino::WideString`, which keeps any UTF-16. |

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
| `Serde`, `SerdeNames`, `Strings` | `--serde` (`true` or `false`), `--serde-names`, `--strings` | Rust |

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

## TypeScript

A class per type, an `as const` object per enum and a `NinoCodec` per type, with `nino.ts` next to them. The bodies of
types without members (`readBody$Empty`, `writeBody$Empty` and the version-tolerant ones) name their parameters `_r`,
`_w` and `_v`, so the code compiles under `noUnusedParameters`. See the
[TypeScript guide](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) for the generated API.

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

- **Generic types.** Only a generic type's definition has a published layout, not `Box<int>`; neither open nor closed
  generic types are generated (in any language), nor slots that allow one.
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
TypeScript of its samples under Node against the bytes .NET writes, and builds scratch projects with
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
with `benchmarks/runtimes_report.py`, after the .NET sections.
