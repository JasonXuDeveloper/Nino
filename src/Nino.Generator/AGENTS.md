# Nino.Generator: file map

> Read when: reading or changing the source generator, the emitted formatter code, the analyzer, the code fixes or the Roslyn 4.8 / 4.12 builds.

The incremental generator and the analyzer (netstandard2.0, loaded by every Roslyn host and by Unity), packed as
`Nino.Generator` with its other builds and the code fixes (section below). `audit-generator` audits the
pipeline and models, `audit-generated-code` `Generation/Emit`, `audit-analyzer` `Analysis/` and the code fixes.

When you add, rename or delete a file here, update its line (`tools/agents.sh --check` fails on a `.cs` file it does
not find named here).

## Root

```
Nino.Generator.csproj        the Roslyn 4.3 build (the oldest Roslyn Unity has) and the package: packs the 4.8 and
                             4.12 builds and the code fixes into analyzers/dotnet/roslyn<version>/cs
Generator.Build.props        what the three builds share (set NinoRoslynVersion first): ROSLYN4_8 / ROSLYN4_12, the
                             sources the newer builds link, the Nino.Core files compiled in (NINO_GENERATOR)
Roslyn.Build.props           what every Roslyn component shares (generator builds and code fixes): netstandard2.0,
                             analyzer rules, the AnalyzerReleases files
build/Nino.Generator.props   adds NinoGen to InterceptorsNamespaces / InterceptorsPreviewNamespaces in consumers; makes
                             the NinoPrefetchLargeCollections property visible to the generator
AnalyzerReleases.Shipped.md  diagnostics of released versions (Roslyn release tracking)
AnalyzerReleases.Unshipped.md  diagnostics added since the last release
Properties/launchSettings.json  debug profile: runs the generator on tests/Nino.UnitTests
```

## Analysis/

```
NinoAnalyzer.cs              the DiagnosticAnalyzer: attribute usage, and what ModelBuilder finds for a type (the
                             generator reports nothing)
NinoDiagnostics.cs           every NINO0xx descriptor, grouped by id range (also compiled into the code fixes)
NinoSuppressor.cs            suppresses "never used / never assigned / make readonly" on private members Nino serializes
```

## Common/

```
EquatableArray.cs            immutable array with value equality, for incremental caching
NinoNames.cs                 Nino.Core names for syntax-only filters and metadata lookups (also in the code fixes)
NinoTypeHelper.cs            which [NinoType] applies to a type, context-free (also over referenced assemblies);
                             NinoTypeOptions
PoolHooks.cs                 the partial methods a [NinoPooled] class implements (NINO019; also in the code fixes)
```

## Generation/ (pipeline and model)

```
NinoGenerator.cs             the IIncrementalGenerator: one formatter file per [NinoType], NinoGen.Registry.g.cs,
                             NinoGen.Usages.g.cs, NinoGen.Dispatch.g.cs, NinoGen.Interceptors.g.cs
ProjectFacts.cs              what is read from the whole compilation in one step (enabled, options, referenced NinoTypes)
AssemblyReferences.cs        the assemblies an assembly references (only those referencing Nino.Core matter)
InheritedCandidates.cs       types that may inherit [NinoType], found from syntax without binding
ClosedGenerics.cs            the closed generic [NinoType]s a compilation names, for registration by type id
TypeFacts.cs                 everything model builds ask about one compilation's symbols, memoized and shared
NinoSymbols.cs               the Nino.Core types compared against, resolved once per compilation
TypeShape.cs                 TypeShape (what a [NinoType] is: kind, format, tracking, raw, ...) and TypeRules (the rules
                             that decide what its formatter has)
Model.cs                     the equatable model records (TypeModel, MemberModel, registrations, intercepted calls,
                             dispatch); TypeModel.Need, DispatchesSubtypes, IsDispatchRoot
ModelBuilder.cs              turns a [NinoType] symbol into a TypeModel (reports through a DiagnosticSink)
ModelCache.cs                reuses TypeModels across compilations while nothing they depend on changed
MemberCollector.cs           the serialized members in wire order, and the findings about their annotations
MemberCache.cs               MemberCollector's results memoized per compilation
ConstructionPlanner.cs       how a type is instantiated when read: reused, a constructor, or a factory
FormatterResolver.cs         the formatter (as C#) for a member type: built-ins, then [NinoFormatter] registrations
FormatterRules.cs            the [assembly: NinoFormatter] registrations visible to a compilation, indexed
FormatterRef.cs              a formatter name kept in parts (twins, type parameters), rendered when written
FormatterTraits.cs           flags of what a formatter is and how it reads and writes, decided once from symbols
SymbolTypeSystem.cs          ITypeSymbol for NinoFormatterBinding (the binding shared with the runtime)
RawRules.cs                  which values are written as raw bytes, memoized per compilation
RawLayout.cs                 whether a struct's memory is the same on every runtime (the canonical layout)
PrefetchRules.cs             which classes' top-level arrays and lists prefetch their objects (INinoPrefetchFormatter):
                             the object's estimated bytes for the cache budget, and which members' objects it prefetches
LayoutTypes.cs               unmanaged structs whose layout the runtime measures ([DynamicDependency] for NativeAOT)
WireEncoding.cs              a member's encoding as the published layout shows it ([NinoTypeLayout])
TypeNames.cs                 type names as generated code and the runtime spell them, and the ids hashed from them
Visibility.cs                what generated code in one assembly can name, read and assign
Interceptors.cs              interceptors of top-level NinoSerializer / NinoDeserializer calls (Roslyn 4.8+ builds;
                             arrays and lists of prefetching classes call NinoDirect's ...Prefetched methods, in a
                             project built with NinoPrefetchLargeCollections)
EmittedFormatters.cs         emitted SourceTexts reused by model value
DiagnosticSink.cs            receives diagnostics while a model is built (the analyzer passes one; the generator none)
```

## Generation/Emit/ (generated text)

```
FormatterEmitter.cs          the source file of one type's formatter
BodyEmitter.cs               WriteBody / ReadBody and WriteMembers / ReadMembers: positional, version-tolerant and
                             keyed layouts, raw blocks, __Shallow and fresh bodies
RawBlock.cs                  consecutive raw members in one reservation (offsets as expressions)
MemberCode.cs                expressions and statements reading or assigning a member, whatever its accessibility
AccessorEmitter.cs           non-public member access: [UnsafeAccessor] (.NET 8+, generic .NET 9+) or a partial class
DispatchEmitter.cs           per-assembly subtype dispatch of base formatters (type checks on write, id switch on read)
RegistryEmitter.cs           the assembly registry and the registrations of built-in formatters it uses
LayoutEmitter.cs             the [assembly: NinoTypeLayout] of a type
FormatterNames.cs            file-local using aliases for long formatter names
GeneratedCode.cs             the header and aliases every generated file shares
Signature.cs                 the signatures of the formatter methods generated code implements
CodeWriter.cs                C# source line by line with indentation
```

## The other Roslyn projects (no AGENTS.md of their own)

```
src/Nino.Generator.Roslyn4.8/    csproj only: compiles this directory's sources against Roslyn 4.8 with ROSLYN4_8
                                 (preview [InterceptsLocation(path, line, column)]); packed into roslyn4.8/cs
src/Nino.Generator.Roslyn4.12/   csproj only: the same against Roslyn 4.12 with ROSLYN4_8 and ROSLYN4_12 (stable
                                 [InterceptsLocation(version, data)]); packed into roslyn4.12/cs
src/Nino.Generator.CodeFixes/    NinoCodeFixProvider.cs: the IDE code fixes, a separate assembly because they need
                                 Microsoft.CodeAnalysis.Workspaces, which the compiler and Unity never load; links
                                 NinoDiagnostics.cs, NinoNames.cs and PoolHooks.cs from here; packed in every
                                 roslyn<version>/cs folder
```

## Rules for these files

- One set of sources, three Roslyn builds: anything newer than Roslyn 4.3 goes behind `#if ROSLYN4_8` /
  `ROSLYN4_12`. Unity runs the 4.3 build, without interceptors.
- Diagnostics are reported by the analyzer only; models carry no locations, so an edit that moves code re-emits
  nothing. A new diagnostic goes into `NinoDiagnostics.cs`, `AnalyzerReleases.Unshipped.md` (AnalyzerTests checks
  both agree) and docs/diagnostics.md (its help link).
- The Nino.Core files compiled in (`NinoHash`, `NinoLayoutCore`, `NinoWire`, `NinoFormat`, `NinoFormatterBinding`)
  are the one definition of ids, layouts and formatter binding: change them in Nino.Core, never copy them here.
