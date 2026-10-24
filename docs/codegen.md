# Code generation for other languages

`nino-gen` (the `Nino.Codegen` tool) writes the `[NinoType]`s of a built .NET assembly in TypeScript, Go or Rust: one
type per `[NinoType]`, with codecs that read and write the bytes Nino writes in .NET. It reads nothing but the
`[assembly: NinoTypeLayout]` attributes the Nino generator publishes in every assembly it compiles
([v4-design.md](v4-design.md#generated-code-per-type)), so it needs no source, no project and no running code: only
the built `.dll` and the assemblies it references.

```sh
dotnet tool install -g Nino.Codegen --prerelease
nino-gen --assembly bin/Release/net8.0/MyGame.Shared.dll --lang go --out ../server/protocol
```

`Nino.TypeScript` (the `nino-ts` build package) renders through the same code: its MSBuild targets, options and
output are unchanged ([TypeScript](#typescript)).

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
| `--references` | A file with one path per line: assemblies to resolve references from (the `.dll`'s directory and the running runtime are searched anyway). |
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

Warnings (a type left out, and why) are printed as `nino-gen : warning NINOGEN002: ...`; errors as
`nino-gen : error NINOGEN001: ...` with exit code 1.

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
changes only the text around them.

## TypeScript

The output is the same as `nino-ts` wrote before the port, byte for byte, with one change: the bodies of types without
members (`readBody$Empty`, `writeBody$Empty` and the version-tolerant ones) name their parameters `_r`, `_w` and `_v`,
so the code compiles under `noUnusedParameters`. Nothing else in the TypeScript, and none of its behavior, changed.
See the [TypeScript guide](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) for the generated API.

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
- **TypeScript**: the types `nino-ts` left out before (see the TypeScript guide).
- **Rust**: maps and sets whose key or element is not hashable in Rust (`f32`, `f64`, and types containing them),
  raw tuples with padding, and nullable raw values outside sequences.
- `HashSet<string>` elements and dictionary keys are non-null strings in Go and Rust, since .NET never writes a null
  key there.

## Tests

`tests/Nino.Codegen.Tests` (in Nino.slnx) checks the IR and each target, and that the generated files committed under
the runtimes match what nino-gen writes today. Each runtime then runs its corpus check over the generated code
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

then run the three runtime checks. The TypeScript check lists the malicious cases the TypeScript runtime still accepts
as `todo` (bytes other than 0 and 1 for booleans and presence flags, duplicate map keys and set elements, unchecked
deferred sections). They are reported, not failed; remove a case from the list (`looser`) when the runtime rejects
it.

## Benchmarks

Each runtime's benchmark writes machine-readable output (`NINO_BENCH_JSON` for TypeScript, `go test -bench` text,
criterion's JSON), and the release workflow (`report.yml`) appends one section per language to the release notes
with `benchmarks/runtimes_report.py`, after the .NET sections.
