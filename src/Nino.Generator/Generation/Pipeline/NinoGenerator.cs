using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;
using Nino.Generator.Generation.Emit;
using Nino.Generator.Analysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Incremental generator: one formatter file per <c>[NinoType]</c>, plus an assembly registry.
/// <para>
/// Each type is turned into an equatable <see cref="TypeModel"/> inside the transform, so editing one type only
/// re-emits that type's file. The registry depends only on the list of formatter names.
/// </para>
/// <para>
/// Nothing generated is referenced by user code, so every output is an implementation output: the IDE never runs
/// the emitters while typing, only builds do. Diagnostics come from <see cref="NinoAnalyzer"/>.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class NinoGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Everything read from the whole compilation, in one step: it runs on every change (a keystroke), and each
        // part below only changes when its value does.
        var project = context.CompilationProvider.Select(static (c, ct) => ProjectFacts.Of(c, ct))
            .WithTrackingName("ProjectFacts");
        var enabled = project.Select(static (p, _) => p.Enabled);

        // C# 10 and later: files that declare one namespace declare it for the whole file, one indentation level less
        // on every line (Unity compiles C# 9).
        var fileScoped = context.ParseOptionsProvider.Select(static (o, _) =>
            o is CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp10 });

        var models = TypeModels(context, project, enabled);
        RegisterFormatters(context, models, fileScoped);
        var calls = TopLevelCalls(context);
        // Built-in generic types used directly with the top-level APIs (e.g. NinoSerializer.Serialize(list)):
        // register closed formatters so AOT platforms have them and generator-only formatters are found.
        var usages = calls.Where(static c => c.Usage != null).Select(static (c, _) => c.Usage!).Collect()
            .WithTrackingName("Usages");
        RegisterNesting(context, models, usages, enabled, fileScoped);
        RegisterUsages(context, models, usages, project, enabled, fileScoped);
#if ROSLYN4_8
        RegisterInterceptors(context, models, calls, enabled);
#endif
        RegisterDispatch(context, models, enabled, fileScoped);
        RegisterRegistry(context, models, FunctionPointerFieldsOf(context, project), project, enabled, fileScoped);
    }

    /// <summary>
    /// The models of the <c>[NinoType]</c>s declared here, in two streams that produce the same models: the types
    /// marked directly, and those that inherit the attribute. Roslyn 4.3 cannot concatenate value providers, so every
    /// output is registered for (or collected from) each of them.
    /// </summary>
    private static IncrementalValuesProvider<TypeModel>[] TypeModels(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled)
    {
        // Types marked [NinoType] directly, with their names as the compiler binds the attribute: unlike the
        // syntactic check below, this sees aliases (using NT = Nino.Core.NinoTypeAttribute), so their subtypes are
        // found too.
        var marked = context.SyntaxProvider.ForAttributeWithMetadataName(
            NinoNames.NinoTypeAttributeMetadataName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => (Name: ((TypeDeclarationSyntax)ctx.TargetNode).Identifier.ValueText,
                Model: Transform(ctx.TargetSymbol as INamedTypeSymbol, ctx.SemanticModel, ct)))
            .WithTrackingName("NinoType");

        // Types that inherit [NinoType] from a base class or an interface. Candidates are found from syntax alone
        // (type and base list names), so only types whose base list can reach a NinoType are ever bound.
        var declarations = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax t && (t.BaseList != null || t.AttributeLists.Count != 0),
                static (ctx, _) =>
                {
                    var syntax = (TypeDeclarationSyntax)ctx.Node;
                    bool marked = HasNinoTypeSyntax(syntax);
                    return marked || syntax.BaseList != null ? InheritedCandidates.From(syntax, marked) : null;
                })
            .Where(static d => d != null)
            .Collect();
        var referencedNinoTypes = project.Select(static (p, _) => p.ReferencedNinoTypes);
        var markedNames = marked.Select(static (m, _) => m.Name).Collect();
        var candidates = declarations.Combine(referencedNinoTypes).Combine(markedNames)
            .Select(static (x, _) => InheritedCandidates.Find(x.Left.Left!, x.Left.Right, x.Right))
            .WithTrackingName("InheritedCandidates");

        var inherited = candidates.Combine(context.CompilationProvider)
            .SelectMany(static (x, ct) =>
            {
                var result = new List<TypeModel?>();
                foreach (var (syntax, model) in InheritedCandidates.Resolve(x.Left, x.Right, ct))
                {
                    if (model.GetDeclaredSymbol(syntax, ct) is not INamedTypeSymbol symbol)
                        continue;
                    if (symbol.GetDirectNinoTypeAttribute() != null || !symbol.IsNinoType())
                        continue;
                    // Partial types: only the first declaration with a base list produces the model.
                    var first = symbol.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax(ct))
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault(t => t.BaseList != null);
                    if (first != syntax)
                        continue;
                    result.Add(Transform(symbol, model, ct));
                }

                return result;
            })
            .WithTrackingName("InheritedNinoType");

        // NinoPrefetchLargeCollections (an MSBuild property): only then do classes' formatters implement
        // INinoPrefetchFormatter and the interceptors of their arrays and lists prefetch; otherwise the models carry
        // no prefetch, and the generated code is what it is without the property.
        var prefetching = context.AnalyzerConfigOptionsProvider.Select(static (o, _) =>
            o.GlobalOptions.TryGetValue("build_property." + PrefetchProperty, out var value) &&
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            .Combine(project.Select(static (p, _) => p.ObjectPrefetch));
        return new[]
        {
            Gate(marked.Select(static (m, _) => m.Model), enabled, prefetching), Gate(inherited, enabled, prefetching),
        };
    }

    /// <summary>One formatter file per model.</summary>
    private static void RegisterFormatters(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<bool> fileScoped)
    {
        // Hint names are compared ignoring case (and so are file names on Windows and macOS): formatter names that
        // differ only by case (types RawS and Raws) take a suffix of their own. Usually empty, so an edit that changes
        // no such name re-emits nothing.
        var clashing = CollectAll(models, static m => m.FormatterName)
            .Select(static (names, _) => new EquatableArray<string>(names.Distinct()
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .SelectMany(g => g).OrderBy(n => n, StringComparer.Ordinal)))
            .WithTrackingName("ClashingNames");
        foreach (var stream in models)
            context.RegisterImplementationSourceOutput(stream.Combine(clashing).Combine(fileScoped),
                static (spc, x) => Emit(spc, x.Left.Left, x.Left.Right.Contains(x.Left.Left.FormatterName), x.Right));
    }

    /// <summary>
    /// The top-level NinoSerializer/NinoDeserializer calls that name a type (Serialize, Deserialize, GetFixedSize&lt;T&gt;,
    /// and the boxed methods and IsSupported given a typeof), bound once for what they register and (Roslyn 4.8+,
    /// Serialize and Deserialize) how they are intercepted. Narrowed by syntax first (binding every Serialize call of
    /// other libraries on each edit is not free), to the receivers that may name them: NinoSerializer or
    /// NinoDeserializer, an identifier (a using alias), or none (using static); the bound method decides.
    /// </summary>
    private static IncrementalValuesProvider<TopLevelCall> TopLevelCalls(IncrementalGeneratorInitializationContext context) =>
        context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && MayCallNino(invocation),
                static (ctx, ct) => TransformCall(ctx, ct))
            .WithTrackingName("TopLevelCall")
            .Where(static c => c != null)
            .Select(static (c, _) => c!);

    /// <summary>
    /// NinoGen.Usages.g.cs: closed built-in formatters used by members, registered once per assembly here rather than
    /// by every type that uses them (the same List&lt;int&gt; formatter is typically used by many types); creators of
    /// the collections those formatters create (NinoActivator&lt;T&gt;), registered the same way; closed generic
    /// [NinoType]s named anywhere, registered by type id (they have no registration of their own: a polymorphic read
    /// must be able to map their id back to the type); and the structs whose layout the runtime measures.
    /// </summary>
    private static void RegisterUsages(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<ImmutableArray<UsageRegistration>> usages,
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled,
        IncrementalValueProvider<bool> fileScoped)
    {
        var registrations = usages.Combine(CollectAll(models, static m => m.Registrations.Formatters))
            .Select(static (x, _) => new EquatableArray<FormatterRegistration>(
                x.Left.Select(static u => u.Formatter).OfType<FormatterRegistration>()
                    .Concat(x.Right.SelectMany(static a => a)).Distinct()
                    .OrderBy(static u => u, FormatterRegistrationOrder)))
            .WithTrackingName("FormatterRegistrations");
        var factories = Merge(usages, CollectAll(models, static m => m.Registrations.Factories), static u => u.Factories,
            static all => all.OrderBy(f => f.TypeName, StringComparer.Ordinal))
            .WithTrackingName("FactoryRegistrations");
        var generics = Merge(usages, CollectAll(models, static m => m.Registrations.GenericTypes), static u => u.GenericTypes,
            static all => all.OrderBy(g => g.TypeName, StringComparer.Ordinal))
            .WithTrackingName("GenericTypeRegistrations");

        // Structs whose layout the runtime measures: their field metadata is kept for NativeAOT (LayoutTypes), where
        // the compilation can name [DynamicDependency].
        var memberLayouts = CollectAll(models, static m => m.Registrations.LayoutTypes);
        var canKeepLayouts = project.Select(static (p, _) => p.CanKeepLayouts);
        var layouts = usages.Combine(memberLayouts).Combine(canKeepLayouts)
            .Select(static (x, _) => x.Right
                ? LayoutTypes.ToArray(x.Left.Left.SelectMany(static u => u.LayoutTypes)
                    .Concat(x.Left.Right.SelectMany(static a => a)))
                : EquatableArray<string>.Empty)
            .WithTrackingName("LayoutTypes");

        context.RegisterImplementationSourceOutput(
            registrations.Combine(factories).Combine(generics).Combine(layouts).Combine(enabled).Combine(fileScoped),
            static (spc, x) =>
            {
                var (((((formatters, creators), closed), kept), on), scoped) = x;
                if (on)
                    spc.AddSource("NinoGen.Usages.g.cs",
                        RegistryEmitter.EmitUsages(formatters, creators, closed, kept, scoped));
            });
    }

    /// <summary>
    /// The function pointer fields of structs, registered by the registry for Nino.Core's layouts
    /// (NinoFunctionPointerFields): of every struct this compilation can see, named here or not, since generic code
    /// (Save&lt;T&gt; calling Serialize&lt;T&gt;) reaches structs no member or call names.
    /// </summary>
    private static IncrementalValueProvider<EquatableArray<string>> FunctionPointerFieldsOf(
        IncrementalGeneratorInitializationContext context, IncrementalValueProvider<ProjectFacts> project)
    {
        // Those declared here, found from syntax; with an alias of a function pointer type (C# 12), every struct
        // declared here, from the compilation.
        var declaredPointers = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => FunctionPointerFields.IsDeclaredIn(node),
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type
                    ? FunctionPointerFields.Of(type)
                    : null)
            .Where(static p => p != null)
            .Collect();
#if ROSLYN4_8
        var aliasedPointers = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => FunctionPointerFields.IsAlias(node), static (_, _) => true)
            .Collect()
            .Select(static (aliases, _) => aliases.Length != 0)
            .Combine(context.CompilationProvider)
            .Select(static (x, ct) => x.Left
                ? LayoutTypes.ToArray(FunctionPointerFields.In(x.Right.Assembly, ct))
                : EquatableArray<string>.Empty)
            .WithTrackingName("AliasedFunctionPointerFields");
#endif
        // And those of every reference, read once per reference (unchanged while typing), in a compilation that
        // references Nino.Core: they are the structs generic code reaches from another assembly, and private fields
        // are in their metadata only.
        var referencedPointers = context.MetadataReferencesProvider
            .Combine(project.Select(static (p, _) => p.Enabled ? p.Options.AssemblyName : null))
            .Select(static (x, ct) => x.Right != null
                ? FunctionPointerFields.In(x.Left, x.Right, ct)
                : EquatableArray<string>.Empty)
            .WithTrackingName("ReferencedFunctionPointerFields")
            .Collect();
        return declaredPointers.Combine(referencedPointers)
#if ROSLYN4_8
            .Combine(aliasedPointers)
            .Select(static (x, _) => LayoutTypes.ToArray(x.Left.Left.Select(static p => p!)
                .Concat(x.Left.Right.SelectMany(static r => r)).Concat(x.Right)))
#else
            .Select(static (x, _) => LayoutTypes.ToArray(x.Left.Select(static p => p!)
                .Concat(x.Right.SelectMany(static r => r))))
#endif
            .WithTrackingName("FunctionPointerFields");
    }

    /// <summary>
    /// NinoGen.Nesting.g.cs: the nesting formatters of the types that hold themselves
    /// (FormatterResolver.NestingFormattersNamedIn), declared once: one per name, whose body models built in different
    /// compilations may have resolved differently (both correct).
    /// </summary>
    private static void RegisterNesting(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<ImmutableArray<UsageRegistration>> usages,
        IncrementalValueProvider<bool> enabled, IncrementalValueProvider<bool> fileScoped)
    {
        var nesting = Merge(usages, CollectAll(models, static m => m.Registrations.Nesting), static u => u.Nesting,
                static all => all.GroupBy(static n => n.Name)
                    .Select(static g => g.OrderBy(static n => n.Body, StringComparer.Ordinal).First())
                    .OrderBy(static n => n.Name, StringComparer.Ordinal))
            .WithTrackingName("Nesting");
        context.RegisterImplementationSourceOutput(nesting.Combine(enabled).Combine(fileScoped), static (spc, x) =>
        {
            var ((formatters, on), scoped) = x;
            if (on && formatters.Length != 0)
                spc.AddSource("NinoGen.Nesting.g.cs", NestingEmitter.Emit(formatters, scoped));
        });
    }

#if ROSLYN4_8
    /// <summary>
    /// NinoGen.Interceptors.g.cs: top-level calls with a known type argument call the resolved formatter directly
    /// (where enabled).
    /// </summary>
    private static void RegisterInterceptors(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValuesProvider<TopLevelCall> calls,
        IncrementalValueProvider<bool> enabled)
    {
        var intercepted = calls.Where(static c => c.Intercepted != null).Select(static (c, _) => c.Intercepted!)
            .Collect()
            .Select(static (x, _) => new EquatableArray<InterceptedCall>(x.Distinct()
                .OrderBy(c => c.Signature, StringComparer.Ordinal).ThenBy(c => c.Call, StringComparer.Ordinal)
                .ThenBy(c => c.Location, StringComparer.Ordinal)))
            .WithTrackingName("InterceptedCalls");
        var fileLocal = context.ParseOptionsProvider.Select(static (o, _) =>
            o is CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp11 });
        // Formatters with a __Top twin for these calls (see TypeModel.HasTop), and with a __Shallow twin
        // (see DepthModel.Shallow).
        var top = NamesWhere(models, static m => m.HasTop).WithTrackingName("TopTwins");
        var shallow = NamesWhere(models, static m => m.Depth.Shallow).WithTrackingName("ShallowTwins");
        // Formatters whose arrays and lists prefetch at the top level (see TypeModel.PrefetchBytes).
        var prefetch = NamesWhere(models, static m => m.PrefetchBytes > 0).WithTrackingName("Prefetchers");
        context.RegisterImplementationSourceOutput(
            intercepted.Combine(fileLocal).Combine(enabled).Combine(top).Combine(shallow).Combine(prefetch),
            static (spc, x) =>
            {
                var (((((calls, local), on), tops), twins), prefetchers) = x;
                if (on && calls.Length != 0)
                    spc.AddSource("NinoGen.Interceptors.g.cs",
                        Interceptors.Emit(calls, local, tops, twins, prefetchers));
            });
    }
#endif

    /// <summary>
    /// NinoGen.Dispatch.g.cs: bases dispatch the subtypes of this assembly through generated code; any other subtype
    /// through the registry.
    /// </summary>
    private static void RegisterDispatch(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<bool> enabled,
        IncrementalValueProvider<bool> fileScoped)
    {
        var dispatch = CollectAll(models, static m =>
            {
                bool isBase = m.IsDispatchRoot;
                bool isTarget = m.IsDispatchTarget;
                return isBase || isTarget
                    ? new DispatchEntry(m.TypeName, m.FormatterName, m.TypeParameterList, m.TypeId, isBase, isTarget,
                        m.Inheritance.NinoBaseClasses, m.Inheritance.NinoInterfaces, m.Inheritance.NinoGenericAncestors,
                        m.RawBody)
                    : null;
            })
            .Select(static (x, _) => new EquatableArray<DispatchEntry>(
                x.OfType<DispatchEntry>().Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)))
            .WithTrackingName("Dispatch");
        context.RegisterImplementationSourceOutput(dispatch.Combine(enabled).Combine(fileScoped), static (spc, x) =>
        {
            var ((entries, on), scoped) = x;
            if (on)
                spc.AddSource("NinoGen.Dispatch.g.cs", DispatchEmitter.Emit(entries, scoped));
        });
    }

    /// <summary>NinoGen.Registry.g.cs: the assembly's formatters, its options and the function pointer fields.</summary>
    private static void RegisterRegistry(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<EquatableArray<string>> pointerFields,
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled,
        IncrementalValueProvider<bool> fileScoped)
    {
        var entries = CollectAll(models, static m => ToEntry(m))
            .Select(static (x, _) => new EquatableArray<RegistryEntry>(
                x.Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)))
            .WithTrackingName("RegistryEntries");
        var options = project.Select(static (p, _) => p.Options);
        context.RegisterImplementationSourceOutput(
            entries.Combine(options).Combine(pointerFields).Combine(enabled).Combine(fileScoped),
            static (spc, x) =>
            {
                var ((((list, opts), pointers), on), scoped) = x;
                if (on)
                    spc.AddSource("NinoGen.Registry.g.cs", RegistryEmitter.EmitRegistry(opts, list, pointers, scoped));
            });
    }

    private static readonly IComparer<FormatterRegistration> FormatterRegistrationOrder =
        Comparer<FormatterRegistration>.Create(static (left, right) =>
        {
            int byName = StringComparer.Ordinal.Compare(left.TypeName, right.TypeName);
            return byName != 0 ? byName :
                StringComparer.Ordinal.Compare(left.Formatter.ToString(), right.Formatter.ToString());
        });

    /// <summary>The models of a stream, in compilations that reference Nino.Core.</summary>
    private static IncrementalValuesProvider<TypeModel> Gate(IncrementalValuesProvider<TypeModel?> models,
        IncrementalValueProvider<bool> enabled, IncrementalValueProvider<(bool Left, bool Right)> prefetching) =>
        models.Where(static m => m != null)
            .Combine(enabled.Combine(prefetching))
            .Where(static x => x.Right.Left)
            .Select(static (x, _) =>
            {
                var model = x.Left!;
                if (model.PrefetchBytes == 0)
                    return model;
                var (prefetchEnabled, objectOnly) = x.Right.Right;
                if (!prefetchEnabled)
                    return model with { PrefetchBytes = 0, PrefetchMembers = EquatableArray<string>.Empty };
                return objectOnly && model.PrefetchMembers.Length == 0
                    ? model with { ObjectOnlyPrefetch = true }
                    : model;
            });

    /// <summary>The MSBuild property a project sets to true to prefetch its long top-level collections.</summary>
    public const string PrefetchProperty = "NinoPrefetchLargeCollections";

    /// <summary><paramref name="select"/> of every model of every stream, collected.</summary>
    private static IncrementalValueProvider<ImmutableArray<T>> CollectAll<T>(
        IncrementalValuesProvider<TypeModel>[] streams, Func<TypeModel, T> select)
    {
        var all = streams[0].Select((m, _) => select(m)).Collect();
        for (int i = 1; i < streams.Length; i++)
            all = all.Combine(streams[i].Select((m, _) => select(m)).Collect())
                .Select(static (x, _) => x.Left.AddRange(x.Right));
        return all;
    }

    /// <summary>The names of the formatters of the models <paramref name="where"/> holds for, sorted.</summary>
    private static IncrementalValueProvider<EquatableArray<string>> NamesWhere(
        IncrementalValuesProvider<TypeModel>[] streams, Func<TypeModel, bool> where) =>
        CollectAll(streams, m => where(m) ? m.FormatterName : "")
            .Select(static (x, _) => new EquatableArray<string>(x.Where(static n => n.Length != 0).Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)));

    /// <summary>
    /// What the top-level calls (<paramref name="fromUsage"/>) and the models register, once each, in the order of
    /// <paramref name="order"/>.
    /// </summary>
    private static IncrementalValueProvider<EquatableArray<T>> Merge<T>(
        IncrementalValueProvider<ImmutableArray<UsageRegistration>> usages,
        IncrementalValueProvider<ImmutableArray<EquatableArray<T>>> models,
        Func<UsageRegistration, IEnumerable<T>> fromUsage, Func<IEnumerable<T>, IEnumerable<T>> order) =>
        usages.Combine(models).Select((x, _) => new EquatableArray<T>(
            order(x.Left.SelectMany(fromUsage).Concat(x.Right.SelectMany(static a => a)).Distinct())));

    private static TypeModel? Transform(INamedTypeSymbol? symbol, SemanticModel semanticModel, CancellationToken ct)
    {
        if (symbol == null)
            return null;
        ct.ThrowIfCancellationRequested();
        var compilation = semanticModel.Compilation;
        var options = semanticModel.SyntaxTree.Options;
        return ModelCache.GetOrBuild(symbol, compilation, options, ct);
    }

    private static TopLevelCall? TransformCall(GeneratorSyntaxContext ctx, CancellationToken ct) =>
        ModelCache.GetOrBuildCall(ctx, static (context, token) => BindCall(context, token),
            static (context, method, type, token) => BuildCall(context, method, type, token), ct);

    private static TopLevelCall? BuildCall(GeneratorSyntaxContext ctx, IMethodSymbol method, ITypeSymbol type,
        CancellationToken ct)
    {
        var compilation = ctx.SemanticModel.Compilation;
        var options = ctx.SemanticModel.SyntaxTree.Options;
        var facts = TypeFacts.For(compilation, options);
        var usage = facts.TopLevelUsage(type);
#if ROSLYN4_8
        // Only Serialize and Deserialize (and their Stream forms' SerializeAsync and DeserializeAsync) have
        // NinoDirect counterparts to intercept with.
        var intercepted = method.Name is "Serialize" or "Deserialize" or "SerializeAsync" or "DeserializeAsync" &&
                          Interceptors.Enabled(options)
            ? Interceptors.Transform(ctx, method, facts, ct)
            : null;
#else
        InterceptedCall? intercepted = null;
#endif
        return usage == null && intercepted == null ? null : new TopLevelCall(usage, intercepted);
    }

    /// <summary>
    /// The <c>NinoSerializer</c> / <c>NinoDeserializer</c> method a call binds to and the type it needs a formatter
    /// for (its type argument, or the type of its <c>typeof</c> argument), or null when it is not such a call or names
    /// no type Nino can register.
    /// </summary>
    private static (IMethodSymbol Method, ITypeSymbol Type)? BindCall(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol method)
            return null;
        var facts = TypeFacts.For(ctx.SemanticModel.Compilation, ctx.SemanticModel.SyntaxTree.Options);
        var type = facts.Symbols.CallType(method, (InvocationExpressionSyntax)ctx.Node, ctx.SemanticModel, ct);
        if (type is null or { TypeKind: TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer } or
            { SpecialType: SpecialType.System_Void } or INamedTypeSymbol { IsUnboundGenericType: true })
            return null;
        // Vector<T>, alone or inside (NINO029): nothing to register or intercept, the regular call refuses it.
        if (facts.Raw.HostSizedVectorIn(type) != null)
            return null;
        return (method, type);
    }

    private static RegistryEntry ToEntry(TypeModel model) =>
        new(model.FormatterName, model.FormatterArity, RegistryEmitter.Registration(model));

    /// <summary>
    /// Emits the formatter of <paramref name="model"/>; <paramref name="clashes"/>: another formatter's name differs
    /// from its own only by case, so its hint name carries the hash of its exact name.
    /// </summary>
    private static void Emit(SourceProductionContext spc, TypeModel model, bool clashes, bool fileScoped) =>
        spc.AddSource(HintName(model.FormatterName, clashes), EmittedFormatters.Get(model, fileScoped, EmitSource));

    /// <summary>The source of the formatter of <paramref name="model"/> (see <see cref="GeneratedCode.File"/>).</summary>
    private static string EmitSource(TypeModel model, bool fileScoped)
    {
        try
        {
            return FormatterEmitter.Emit(model, fileScoped);
        }
        catch (Exception e)
        {
            // A rule that emits code the model has no name for (TypeModel.Need), or any other generator bug: one
            // build error naming the type, instead of a generator warning and no formatter, or code that does not
            // compile at all.
            return $"#error Nino generator failed on {model.TypeName} ({e.GetType().Name}: " +
                   $"{e.Message.Replace('\n', ' ').Replace('\r', ' ')}). Please report it at https://github.com/JasonXuDeveloper/Nino/issues\n";
        }
    }

    /// <summary>The hint name of a formatter's file (see <see cref="Emit"/>).</summary>
    internal static string HintName(string formatterName, bool clashes) => clashes
        ? $"NinoGen.{formatterName}.{TypeNames.ComputeId(formatterName):X8}.g.cs"
        : $"NinoGen.{formatterName}.g.cs";
}
