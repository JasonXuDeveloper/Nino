# nino-gen: how code generation for other languages works

`nino-gen` writes the `[NinoType]`s of a built .NET assembly in TypeScript, Go or Rust: one type per `[NinoType]`, with
codecs that read and write the bytes Nino writes in .NET. This document is for contributors: how it works, where each
decision is made, how to add a type and how it is tested.

**Using it** is documented once, on the site: [TypeScript, Go and
Rust](https://nino.xgamedev.net/en/docs/v4/advanced/codegen) (the build package and its `<NinoCodegen>` items, the
command line, every option, the diagnostics, migrating from Nino.TypeScript, the type mapping and generated API of each
language, generic types, reference tracking, `[NinoType]` collections, untrusted data and what is not generated).
Each runtime's API is in its README: [TypeScript](../runtimes/typescript/README.md), [Go](../runtimes/go/README.md),
[Rust](../runtimes/rust/README.md). The NINOGEN diagnostics are in
[diagnostics.md](diagnostics.md#code-generation-for-other-languages).

## Where it lives

- `src/Nino.Codegen` is the command line (`Program.cs`) and the build package **Nino.Codegen**:
  `build/Nino.Codegen.targets` runs the same entry point after `CoreCompile`, once per `<NinoCodegen>` item, passing
  the compiled assembly, a file listing its references (`obj/<configuration>/<framework>/nino-gen/references`) and each
  metadata as the option of the same name. Its inputs and outputs make an item run again only when the assembly, its
  references, the tool or the item's metadata change, and design-time builds skip it.
- `src/Nino.Codegen.Tool` packs the same program as the .NET tool **Nino.Codegen.Tool** (command `nino-gen`).
- The file map is `src/Nino.Codegen/AGENTS.md`.

nino-gen reads nothing but the `[assembly: NinoTypeLayout]` attributes the Nino generator publishes in every assembly
it compiles ([v4-design.md](v4-design.md#generated-code-per-type); the grammar is
[spec/wire-format.md §8](spec/wire-format.md)), through a `MetadataLoadContext`: no source, no loading, no running
code.

## The pipeline

1. **Layouts** (`Layouts/`): `LayoutReader` reads the `NinoTypeLayout` attributes, and `WireEncoding` parses each
   member's encoding (`raw(4)`, `seq(string)`, `map(K,V)`, `type(T)`, ...; only `raw`, `type`, `formatter` and `grid`
   take text rather than encodings as arguments). `ClosedTypeScan` reads the closed generic types the assembly's
   metadata names ([Closed generic types](#closed-generic-types)).
2. **The IR** (`Model/`): `CodegenModel` turns the layouts into a language-neutral model: each type's kind (raw
   struct, struct, sealed or open class, interface or abstract slot), format (positional, version-tolerant, keyed),
   header, deferral, members with their `ValueModel`s, the subtypes a slot allows (`Slots`), enums, and the generic
   definitions. Every decision about the bytes is made here, once, for all languages. An encoding no language can
   express is a `ValueKind.Unsupported` value whose `Problem` says why.
3. **Targets** (`Targets/`): each target maps the IR to its language in C#: names, types, which runtime helper reads
   and writes a value, and what the language cannot express. `CodegenTarget` (their base) decides which types are
   written: a target throws `UnsupportedException` for a value it cannot express, and the type is left out with
   NINOGEN002 naming it and the reason, with every type that holds it. Never write code that reads differently.
4. **Templates** (`Templates/<language>/*.sbn`, Scriban, embedded in the tool): they only assemble text, looping over
   what the target hands them and calling its functions; they make no decision of their own.

A change to the bytes goes into the IR; a change to how a language spells something into its target; a template
changes only the text around them. The options are defined once, in `Options.cs` (`CodegenOptions.All`): the name on
the command line, the `<NinoCodegen>` metadata that sets it in a build, the language and the values it takes. The
command line, its usage text and the build targets all work from that list.

`Program.cs` writes a file only when its text changes and lists what it wrote in `.nino-gen` in the output
directory, deleting what the list names but this run no longer writes.

## What each target writes, and why

The generated API of each language is on the site; these are the decisions behind it.

- **TypeScript** (`TypeScriptTarget`): the module of an assembly, with `nino.ts` (embedded in the tool from
  `runtimes/typescript/src/nino.ts`) written next to it unless `--runtime` names a module. The bodies of types without
  members (`readBody$Empty`, `writeBody$Empty` and the version-tolerant ones) name their parameters `_r`, `_w` and
  `_v`, so the code compiles under `noUnusedParameters`. With `--numeric-arrays typed`,
  `TypeScriptTarget.NumberArrays` chooses the typed array of each fixed-size number.
- **Go** (`GoTarget`): read and write functions in the shape of the hand-written codecs in
  `runtimes/go/internal/sample`: one `Take`/`Extend` per run of fixed-size members, no reflection, and a direct call
  for each class body and each element of a collection (no function values; `optimize-runtimes`).
- **Rust** (`RustTarget`): the codecs written out in full (`impl NinoReadBody`, `NinoWriteBody`, `NinoRead`,
  `NinoWrite` for each type) rather than `#[derive(NinoRead, NinoWrite)]` attributes, because:
  - it needs only the `nino-serialization` crate, not the derive crate and its proc-macro build;
  - the derive takes former names as strings, and a layout carries only their hashes, which the expanded code checks
    directly;
  - the shape is the one `runtimes/rust/tests/conformance/gen.py` proved on the whole corpus (and which the derive
    generates), so the performance and safety rules of runtimes/rust hold: one bounds check per run of fixed-size
    members, collections reserve through `r.capacity`, structs count depth through `enter_struct`/`exit_struct`, raw
    structs check padding, and a struct whose members are all of fixed size has a `FIXED_SIZE` (`nino::fixed_size` of
    theirs), so a list of it is one block.

  A generic type's impls are bounded `impl<T: NinoRead + NinoWrite + Default + 'static>`.

### Generic types

A generic `[NinoType]` is a generic type in each language, whose codec is built from the codecs of its type arguments,
as .NET builds `NinoFormatter<Box<T>>` from the formatter of `T`. The IR gives each type argument the encoding .NET's
formatter for it writes: a `T?[]` or `List<T?>` of an unmanaged `T` as raw `T`s with a flag each, a `HashSet<T?>` or
a dictionary's values item by item, a `KeyValuePair` as its key then its value (`CodegenModel.Generic`).

A generic type that holds itself with other arguments (`Nest<T>` holding `Nest<List<T>>`) needs a new instantiation for
every level of the data. TypeScript builds the codec of the next level when it first reads or writes one. Go and Rust
instantiate generics at compile time and cannot: Go rejects a generic type whose instantiation cycle grows its
arguments (`instantiation cycle`), and Rust a type that needs ever more instantiations (E0320, "overflow while adding
drop-check rules"), so `CodegenModel.GenericProblem` names the path through which it holds itself, and both targets
leave it out with that reason (a generic slot whose slots grow with every level likewise has a
`TypeModel.SlotProblem`).

A generic class that is not sealed, an abstract generic class or a generic interface is written behind the type id of
its closed .NET name, so each closed type is a slot of its own. The IR builds the slots (`CodegenModel.Slots`), which
closed types each allows (`SlotModel.ClosedAssignable`) and, for a generic definition holding a slot of its own type
parameters (`OpenHolder<T>` with an `Open<T>` member), its slot parameters (`TypeModel.SlotParameters`): each closed
use of the definition (`ValueModel.SlotArguments`) passes it the slots of its arguments, as .NET's formatter looks the
id up by the closed name. It adds the closed types a slot of a closed type needs although no code names them
(`OpenHolder<long>` needs `Open<long>` and its subtypes).

### Closed generic types

A generic subtype in a polymorphic or `object` slot is written behind the hash of its closed .NET name
(`Conformance.Box<System.Int32>`), which another language cannot form from the definition the layouts carry.
`ClosedTypeScan` therefore reads every closed type the assembly's metadata names: in a signature, a type specification
(`new Box<int>()`, `typeof(Box<int>)`) or a generic method's arguments (`NinoPolymorphic.Register<Box<int>>()`). Those
whose definition is a concrete generic `[NinoType]` become cases of the slots they are assignable to. A closed generic
`[NinoType]` struct is written member by member wherever it appears, as .NET's formatter for its definition writes it:
a field of a type parameter is never copied as raw memory.

### Reference tracking and `[NinoType]` collections

Both are encoded as the specification says ([§7.9](spec/wire-format.md), [§7.7](spec/wire-format.md)); the IR marks
tracked types (`TypeModel.TrackReferences`) and each target calls its runtime's tracking helpers (the runtime READMEs
list them). A generic type with `trackReferences` is left out.

## Adding a type or an encoding

1. **The encoding.** A new kind of encoding in the layouts (the Nino generator's side, a wire-format change with
   everything the `wire-format` skill lists) is parsed by `WireEncoding` as it stands unless its arguments are text.
   `CodegenModel.Value` resolves it against the member's .NET type: a new `ValueKind`, or for a fixed-size BCL value a
   `PrimitiveKind` and its .NET name in `CodegenModel.PrimitiveOf` (with its size). A .NET type the encoding cannot
   describe is `Unsupported` with its reason.
2. **Each target.** Map it in `TypeScriptTarget`, `GoTarget` and `RustTarget` (the language's type and the helper that
   reads and writes it), or throw `UnsupportedException` with the reason where the language cannot express it; the
   template needs a change only when the text around a value differs.
3. **Each runtime.** The helper the targets call exists in each runtime, with its bounds and validation checks
   (`audit-security`), and its README's API lists it.
4. **A sample.** A member of the new type in `conformance/Nino.Conformance/Types.cs` (the corpus is regenerated:
   conformance/AGENTS.md), or, for a way of declaring a type the corpus does not cover, a shape in
   `tests/Nino.Codegen.Tests/Shapes.cs`, which every runtime compiles.
5. **Regenerate and test** ([Tests](#tests)), then the site: the type mapping of each language, and its "What Is Not
   Generated" list for a language that leaves it out (`audit-docs`).

## Tests

`tests/Nino.Codegen.Tests` (in Nino.slnx) checks the IR, each target and the command line, and that the generated files
committed under the runtimes match what nino-gen writes today. `tests/Nino.Codegen.Integration.Tests` runs the
TypeScript of its samples under Node against the bytes .NET writes, round-trips its generic samples through the
generated Go (`go run`) and Rust (`cargo run`, with each `--maps`) (inconclusive without `go` or `cargo` on the
PATH, a failure in CI, which sets them up), and builds scratch projects with
`build/Nino.Codegen.targets` (every language, incremental runs, the `NinoTypeScript*` properties, design-time builds,
and that the targets pass every option `Options.cs` defines). Each runtime then runs its corpus check over the generated
code (every valid case written back byte for byte, every malicious case and truncated prefix rejected):

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
with `benchmarks/runtimes_report.py`, after the .NET sections: x64, then the same on an Arm64 runner. The site shows
them on [Benchmarks in Other Languages](https://nino.xgamedev.net/en/docs/v4/benchmarks-runtimes).
