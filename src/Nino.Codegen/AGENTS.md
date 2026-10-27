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
TemplateRenderer.cs          renders the embedded Scriban templates of a language (include "x.sbn")
build/Nino.Codegen.targets   runs nino-gen after the compile for each <NinoCodegen Include="typescript|go|rust"> item,
                             passing each metadata as its option; NinoCodegenEnabled, NinoCodegenTargetFramework, the
                             old NinoTypeScript* properties (NINOGEN003)
Layouts/
  LayoutReader.cs            reads the [NinoTypeLayout]s without loading the assembly (TypeLayout, MemberLayout)
  WireEncoding.cs            one member encoding of a layout (seq, raw, type, ...), RawField
  ClosedTypeScan.cs          the closed generic types an assembly's metadata names (each a type id of its own)
Model/                       the IR: language-neutral, built once, read by every target
  CodegenModel.cs            the assembly's [NinoType]s (and those of its references), encodings resolved to ValueModels
  TypeModel.cs               TypeModel, MemberModel, ClosedTypeModel; BodyFormat, HeaderKind
  ValueModel.cs              ValueModel (what a value is on the wire), SlotModel, RawStructModel, EnumModel
  TypeNames.cs               .NET type and member names, and the FNV-1a hash of ids and keys
Targets/                     what each language can express and how (NINOGEN002 for what it cannot)
  TypeScriptTarget.cs        classes, `as const` enums and a NinoCodec per type (TsShape, TsClass, ...)
  GoTarget.cs                structs with nino tags and NinoLayout, codecs in the style of runtimes/go/internal/sample
  RustTarget.cs              structs and NinoReadBody / NinoWriteBody impls in the shape of
                             runtimes/rust/tests/conformance/types.rs
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
