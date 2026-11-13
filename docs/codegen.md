# nino-gen: how code generation for other languages works

`nino-gen` writes the `[NinoType]`s of a built .NET assembly in TypeScript, Go or Rust: one type per `[NinoType]`, with
codecs that read and write the bytes Nino writes in .NET. This document is for contributors: how it works, where each
decision is made, how to add a type and how it is tested.

**Using it** is documented once, on the site: [TypeScript, Go and
Rust](https://nino.xgamedev.net/en/docs/v4/advanced/codegen) (the build package and its `<NinoCodegen>` items, the
command line, every option, the diagnostics, generic types, reference tracking, `[NinoType]` collections, untrusted
data and what is not generated), and a page per language with its type mapping, generated API and limits:
[TypeScript](https://nino.xgamedev.net/en/docs/v4/advanced/typescript) (and migrating from Nino.TypeScript),
[Go](https://nino.xgamedev.net/en/docs/v4/advanced/go), [Rust](https://nino.xgamedev.net/en/docs/v4/advanced/rust).
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
- `nino-gen layouts` writes and checks layout baselines ([Layout baselines](#layout-baselines)).
- `nino-gen inspect` prints what each byte of a message is ([Inspecting a message](#inspecting-a-message)).
- The file map is `src/Nino.Codegen/AGENTS.md`.

nino-gen reads nothing but the `[assembly: NinoTypeLayout]` attributes the Nino generator publishes in every assembly
it compiles ([v4-design, "Generated code per type"](v4-design/generator.md#generated-code-per-type); the grammar is
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
  `NinoWrite` for each type) rather than `#[derive(NinoRead, NinoWrite)]` attributes; what is the same for every type
  is a macro of the crate, as the derive uses it: `nino::raw_read!()` / `raw_write!()` for a raw struct's impls,
  `sealed_read!` / `sealed_write!` for a sealed class's, and `nino::type_consts!` for the `NINO_TYPE_ID`,
  `NINO_TYPE_NAME` and `NINO_LAYOUT` constants (documented once in the crate, not per type; `NINO_`-prefixed because they
  share the type's namespace with the user's own impls, as Go's methods are `NinoTypeID`, `NinoTypeName` and
  `NinoLayout`); the body impls set their `TYPE_ID` to `NINO_TYPE_ID`, as the derive's set theirs. Written out, because:
  - it needs only the `nino-serialization` crate, not the derive crate and its proc-macro build;
  - the derive takes former names as strings, and a layout carries only their hashes, which the expanded code checks
    directly;
  - the shape is the one `runtimes/rust/tests/conformance/gen.py` proved on the whole corpus (and which the derive
    generates), so the performance and safety rules of runtimes/rust hold: one bounds check per run of fixed-size
    members, collections reserve through `r.capacity`, structs count depth through `enter_struct`/`exit_struct`, raw
    structs check padding, and a struct whose members are all of fixed size has a `FIXED_SIZE` (`nino::sum_fixed_sizes` of
    theirs), so a list of it is one block.

  A generic type's impls are bounded `impl<T: NinoRead + NinoWrite + Default + 'static>`.

- **Host-sized and BCL fields inside raw structs** (`CanonicalLayout`, `CodegenModel.Raw`): every target reads the
  fields the layout publishes at their canonical offsets (spec §5.2, §8.3). `nint` becomes a signed 64-bit integer
  (`int64`, `i64`, `bigint`) and `nuint`, pointers and function pointers (`delegate*`) an unsigned one (`uint64`, `u64`, `bigint`), because the wire
  holds them as 8 bytes, sign- or zero-extended; `Int128` sits at its 16-byte-aligned offset, and a `Nullable<Int128>`
  element of a fixed-stride array has a stride of 32. `DateTimeOffset` is a struct `{ dateTime, offsetMinutes }` in Go
  and Rust; TypeScript leaves it out with NINOGEN002, since a `Date` holds milliseconds as UTC and cannot give back
  the ticks and kind. `Vector64<T>` to `Vector512<T>` (the bare `raw`) and layouts from an older build that hold a
  `System.Numerics.Vector<T>` (`IsHostSizedVector`) are NINOGEN002 too.

### Generic types

A generic `[NinoType]` is a generic type in each language, whose codec is built from the codecs of its type arguments,
as .NET builds the formatter of `Box<T>` from the formatter of `T`. The IR gives each type argument the encoding .NET's
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
(`new Box<int>()`, `typeof(Box<int>)`) or a generic method's arguments (`NinoPolymorphic.TryRegister<Box<int>>()`). Those
whose definition is a concrete generic `[NinoType]` become cases of the slots they are assignable to. A closed generic
`[NinoType]` struct is written member by member wherever it appears, as .NET's formatter for its definition writes it:
a field of a type parameter is never copied as raw memory.

### Reference tracking and `[NinoType]` collections

Both are encoded as the specification says ([§7.9](spec/wire-format.md), [§7.7](spec/wire-format.md)); the IR marks
tracked types (`TypeModel.TrackReferences`) and each target calls its runtime's tracking helpers (the runtime READMEs
list them). A generic type with `trackReferences` is left out.

### Serialization callbacks

`[NinoOnSerializing]`, `[NinoOnSerialized]`, `[NinoOnDeserializing]` and `[NinoOnDeserialized]` are C# methods the
.NET formatter calls; they change no byte, so the published layout does not show them and nino-gen writes the same
code with or without them. The generated TypeScript, Go and Rust code calls nothing of the kind: work a callback does
in .NET (filling a cache, checking values, migrating a field) is done by the code that calls the generated codec there.

## Layout baselines

`nino-gen layouts` writes and checks the layout baseline of an assembly (`[assembly: NinoLayouts(@"...")]`,
[diagnostics.md](diagnostics.md#the-layout-baseline)) from the same layouts, for builds without an IDE (CI, Unity in
batch mode):

```sh
nino-gen layouts --assembly bin/Release/net8.0/MyGame.dll --out NinoLayouts.cs           # write it (again)
nino-gen layouts --assembly bin/Release/net8.0/MyGame.dll --out NinoLayouts.cs --check   # compare; exit 1 on NINO034/NINO035
```

Writing takes the assembly's own layouts (not its references') of the types the baseline holds and replaces only the
string of an existing `NinoLayouts(@"...")`, keeping the rest of the file. `--check` prints each finding in MSBuild's
format at its line in the file (`NinoLayouts.cs(12): warning NINO034: ...`) and exits with 1 when one breaks data or
loses it silently; `--references <file>` is the codegen command's. `LayoutsCommand.cs` is the command; the baseline's
format (`LayoutBaseline.cs`) and its rules (`LayoutRules.cs`: which types it holds, which section a renamed type
matches, what a change does to each reader) are `src/Nino.Generator/Common`'s, compiled into the analyzer, its code
fixes and nino-gen alike, so the IDE, the build and this command report the same findings. A type's name in a baseline
is its runtime-form name, `TypeNames.Runtime` here and `TypeNames.RuntimeName` in the generator.

## Inspecting a message

`nino-gen inspect` reads one message with the layouts an assembly publishes and prints a tree of what each byte is:
every value with its member name, its type, its encoding, its offset and length, and what it holds (numbers, strings,
enums as numbers with the constant's name, collections with their counts, the type a type id names, the keys of keyed
entries and the bytes of unknown ones, reference headers, the members a version-tolerant body of older data lacks, the
section of deferred bodies and each body at the slot it belongs to). It is a protocol debugging tool: bytes from a
capture, a log or a save file that will not read, or that read to something unexpected.

```sh
nino-gen inspect --assembly bin/Release/net8.0/MyGame.Shared.dll --type MyGame.Messages.Login login.bin
xxd -p frame.bin | nino-gen inspect --assembly MyGame.Shared.dll --type MyGame.Messages.Login --hex --offset 4
nino-gen inspect --assembly MyGame.Shared.dll --type MyGame.Messages.Login --json login.bin > login.json
```

- `--type` is the runtime-form name the corpus and type ids use (`Namespace.Outer+Inner`,
  `Namespace.Box<System.Int32>`, `System.Collections.Generic.List<Namespace.Point>`): any type Nino reads at the top
  level (`CodegenModel.ResolveType`, then `CodegenModel.RootValue`, the encoding of the formatter .NET finds for it,
  as for a type argument).
- The message is a file, or standard input (no file, or `-`). `--hex` takes hex text instead of bytes (whitespace,
  `,`, `:`, `-` and `0x` between pairs are ignored); `--offset <n>` starts at byte `n`, for a frame inside a bigger
  buffer (offsets stay the input's). Bytes after the value are reported, not read, so the next frame's offset is in
  the output. There is no `--length`: a message ends where its value does.
- `--json` writes the same tree for tools (`name`, `type`, `encoding`, `offset`, `length`, `value` as JSON in the
  corpus's forms, `text`, `note`, `children`), with `error` (`offset`, `message`) and `trailing`.
- Exit codes: 0 read, 2 malformed data (the tree up to there, then `malformed data at offset N: <the check>`), 1 a
  mistake of the command line (NINOGEN001).

It is an interpreter of the IR, not a fourth target: `Inspect/Inspector.cs` walks the `ValueModel`s the targets write
code from, with the reader's state of the specification (§7.10 deferral and depth, §7.9 references, regions and
sections), so nothing about the bytes is decided twice and no second layout parser exists. A generic type's members
are read with an environment of its type arguments (`Parameter` values resolve through it, open types close with
`CodegenModel.Substitute`); a type id resolves among the concrete types with layouts and `CodegenModel.ClosedTypes`,
assignable to the declared type (`CodegenModel.AssignableTo`). It makes every check a reader in untrusted mode makes
(spec §9: bounds, lengths against the bytes left, minimal string headers, strict UTF-8, presence and `bool` bytes, raw
flags, value ranges, type ids, back references, regions and sections, repeated set elements and map keys, depth 128
and 512 with the struct bodies `CodegenModel.HoldsItself` counts) and runs on a thread with a 256 MB stack, so deep
data nests the tree as deep as the limits allow. The tree is bounded too: at most 16 values per input byte (a value of
no bytes, the body of a struct without members, repeated by counts could otherwise grow it with the square of the
input).

A value no layout describes (`formatter(F)`, a custom formatter's bytes, spec §7.11) is shown as opaque bytes when its
length is known (the whole of a keyed entry, the last member of a version-tolerant body, a `raw(n)`); anywhere else
its end is unknown and the read stops there with that reason. `BitArray` and `TimeZoneInfo` (`bits`, `timezone`) are
read as spec §3.5 says, though no target writes them. Without the assembly nothing can be read: positional bodies
carry no names, lengths or types.

`tests/Nino.Codegen.Tests/InspectCommandTests.cs` reads every valid corpus case to its end and to the value its JSON
records, every malicious case and truncated prefix to an error (the untrusted ones too; a `trailing` case reports its
bytes), mutated corpus inputs to a value or an error and nothing else, and compares the text of representative
messages with `tests/Nino.Codegen.Tests/Inspect/*.txt` (`NINO_UPDATE_GENERATED=1` rewrites them: review the diff).
`InspectTypes.cs` holds the custom-formatter types, which every target leaves out (NINOGEN002).

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
5. **Inspect.** `nino-gen inspect` reads a new `ValueKind` in `Inspect/Inspector.cs` (and a new `PrimitiveKind` in
   `Inspect/Scalars.cs`, with its range checks); the corpus tests fail until it does.
6. **Regenerate and test** ([Tests](#tests)), then the site: the type mapping of each language, and its "What Is Not
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
criterion's JSON), and the release workflow (`report.yml`) turns each into a section per language and architecture
with `benchmarks/reports/runtimes_report.py`, attached to the release as `runtime-<lang>-<x64|arm64>.md`
(`benchmarks/reports/release_assets.py`) and charted in its notes. The site shows
them on [Benchmarks in Other Languages](https://nino.xgamedev.net/en/docs/v4/benchmarks-runtimes).
