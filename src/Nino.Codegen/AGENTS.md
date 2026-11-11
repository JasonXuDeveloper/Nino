# Nino.Codegen: file map

> Read when: reading or changing nino-gen (the TypeScript, Go and Rust code generator), its templates, its options or the Nino.Codegen build package.

nino-gen (net8.0, rolls forward to any later runtime): reads the `[assembly: NinoTypeLayout]`s of a built assembly
through a MetadataLoadContext and writes TypeScript, Go or Rust types and codecs (docs/codegen.md). Packed as
`Nino.Codegen` (an MSBuild package that runs it after each compile) and, through `src/Nino.Codegen.Tool`, as the
dotnet tool `Nino.Codegen.Tool`. `audit-runtimes` audits it, `audit-generated-code` its templates.

```
Nino.Codegen.csproj          the exe (nino-gen.dll, no apphost) and the package: tools/ holds it and its assemblies,
                             build/ the targets; embeds Templates/**/*.sbn and runtimes/typescript/src/nino.ts
Program.cs                   the command line: --assembly <dll> --lang ts|go|rust --out <dir> [options]; writes a file
                             only when its text changes, and deletes what its manifest (.nino-gen) lists but this run
                             no longer writes
Options.cs                   CodegenOptions.All: every option once (command-line name, <NinoCodegen> metadata,
                             language, values); CodegenArguments (a parsed command line)
LayoutsCommand.cs            nino-gen layouts: writes an assembly's layout baseline (NinoLayouts.cs), or --check
                             compares it with the rules the analyzer uses (Nino.Generator's LayoutBaseline.cs and
                             LayoutRules.cs, linked)
InspectCommand.cs            nino-gen inspect: reads one message (a file or standard input, --hex, --offset) with an
                             assembly's layouts and prints the tree of its bytes, or --json; exit 2 on malformed data
TemplateRenderer.cs          renders the embedded Scriban templates of a language (include "x.sbn")
build/Nino.Codegen.targets   runs nino-gen after the compile for each <NinoCodegen Include="typescript|go|rust"> item,
                             passing each metadata as its option; NinoCodegenEnabled, NinoCodegenTargetFramework, the
                             old NinoTypeScript* properties (NINOGEN003)
Layouts/
  LayoutReader.cs            reads the [NinoTypeLayout]s without loading the assembly (TypeLayout, MemberLayout)
  WireEncoding.cs            one member encoding of a layout (seq, raw, type, ...), RawField
  ClosedTypeScan.cs          the closed generic types an assembly's metadata names (each a type id of its own)
Inspect/                     nino-gen inspect's reader: an interpreter of the IR, with the reader checks of spec §9
  Inspector.cs               reads a message into InspectNodes (InspectResult): the entry, depth limits, the value of
                             each ValueKind; its other parts, one kind of value each:
  Inspector.Raw.cs             raw values (primitives, raw structs, tuples) from bytes already taken
  Inspector.Strings.cs         UTF-16 and UTF-8 strings
  Inspector.Collections.cs     sequences, maps, grids, named and unsupported values
  Inspector.Objects.cs         [NinoType] values: headers, type ids, references, members, keyed entries
  Inspector.Regions.cs         version-tolerant and keyed regions, deferred bodies and sections (§7.10)
  InspectNode.cs             one part of the message (name, type, encoding, offset, length, value) as text or JSON
  Scalars.cs                 the fixed-size values as text and JSON, with their range checks (§9.3, §9.10)
  WireReader.cs              the bounds-checked reader (regions as limits), counts, string headers; InspectException
Model/                       the IR: language-neutral, built once, read by every target
  CodegenModel.cs            the assembly's [NinoType]s (and those of its references), encodings resolved to ValueModels;
                             types by runtime-form name and their top-level value (ResolveType, RootValue), structs
                             that hold themselves (HoldsItself); building the types from their layouts
  CodegenModel.Generics.cs   generic types behind type ids: slot parameters, closed types, substitution, unification
  CodegenModel.Values.cs     a layout's encoding as a ValueModel (generic, raw, raw struct, enum; unsupported ones)
  CodegenModel.Clr.cs        CLR helpers: primitive kinds and sizes, generic definitions, interfaces, fields
  TypeModel.cs               TypeModel, MemberModel, ClosedTypeModel; BodyFormat, HeaderKind
  ValueModel.cs              ValueModel (what a value is on the wire), SlotModel, RawStructModel, EnumModel
  CanonicalLayout.cs         the canonical alignment a layout does not publish, by Nino.Core's NinoLayoutCore (linked)
  TypeNames.cs               .NET type and member names (and the forms every target names a type with), the
                             FNV-1a hash of ids and keys
Targets/                     what each language can express and how (NINOGEN002 for what it cannot)
  CodegenTarget.cs           the base of the three: which types are written (the rest left out with a warning),
                             the closed generic types, the warnings, the names of the generated types; ResolveTypes,
                             how every Render starts
  TypeScript/                classes, `as const` enums and a NinoCodec per type
    TypeScriptTarget.cs        which types, names, helpers, Render (and the runtime copy with nino-gen's version)
    TypeScriptTarget.Shapes.cs   the TsShape of a value
    TypeScriptTarget.Nesting.cs  which values may open a class body (structs holding themselves included)
    TypeScriptOptions.cs       the options of the generated TypeScript
    TsShape.cs                 how a value is written in TypeScript
    TsTemplateTypes.cs         what types.sbn reads besides shapes (TsClass, TsSlot, TsEnum, TsRawStruct, ...)
  Go/                        structs with nino tags and NinoLayout, codecs in the style of runtimes/go/internal/sample
    GoTarget.cs                which types, names, helpers, Render
    GoTarget.Shapes.cs         the GoShape of a value, slots and markers, raw blocks
    GoOptions.cs               the options of the generated Go (TagNaming)
    GoShape.cs                 how a value is written in Go
    GoTemplateTypes.cs         what types.sbn reads besides shapes (GoType, GoSlot, GoField, GoRawBlock, ...)
  Rust/                      structs and NinoReadBody / NinoWriteBody impls in the shape of
                             runtimes/rust/tests/conformance/types.rs
    RustTarget.cs              which types, names, Render
    RustTarget.Shapes.cs       the RustShape of a value
    RustOptions.cs             the options of the generated Rust
    RustShape.cs               how a value is written in Rust
    RustTemplateTypes.cs       what types.sbn reads besides shapes (RustType, RustField, RustEnum, ...)
Templates/                   Scriban templates: assemble text only
  typescript/types.sbn, shapes.sbn   the module of an assembly; the code of one value from its shape
  go/types.sbn, shapes.sbn           the Go file; the code of one value from its shape
  rust/types.sbn                     the Rust module
```

## src/Nino.Codegen.Tool (no AGENTS.md of its own)

```
Nino.Codegen.Tool.csproj     packs the command line as a dotnet tool (PackAsTool, command nino-gen), referencing
                             src/Nino.Codegen
Program.cs                   calls Nino.Codegen's Program
```

## Code generation for other languages

`src/Nino.Codegen` (nino-gen: packed as the Nino.Codegen build package and, through `src/Nino.Codegen.Tool`, as the
Nino.Codegen.Tool .NET tool) writes TypeScript, Go and Rust from the published layouts (docs/codegen.md):
- Every option is defined once, in `Options.cs` (`CodegenOptions.All`: its command-line name, the `<NinoCodegen>`
  metadata that sets it, its language and values); `build/Nino.Codegen.targets` passes each metadata as that option,
  and `Nino.Codegen.Integration.Tests` checks the two agree and builds projects with the targets.
- Logic lives in the IR (`Model/`) and each target's C# (`Targets/`), never in the templates (`Templates/*/*.sbn`),
  which only assemble text from what the target hands them. A decision about the bytes goes into the IR once, for
  every language.
- Every template or target change is tested end to end against the corpus: regenerate the committed files
  (`NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests`), then run the TypeScript (`npm test`), Go
  (`go test -race ./...`) and Rust (`cargo test --workspace --all-features` with `NINO_CORPUS` set) checks, which read,
  write back and attack the generated code with the corpus's valid, malicious and truncated cases.
- Generated code follows each runtime's performance and safety rules above (bounds checked before allocating, depth
  counted as .NET does, one bounds check per run of fixed-size members, rare paths out of line): what the
  hand-written samples do, the templates write. Anything a language cannot express is a NINOGEN002 warning naming
  the type, never code that reads differently.
