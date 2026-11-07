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
public sealed class NinoGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Everything read from the whole compilation, in one step: it runs on every change (a keystroke), and each
        // part below only changes when its value does.
        var project = context.CompilationProvider.Select(static (c, ct) => ProjectFacts.Of(c, ct))
            .WithTrackingName("ProjectFacts");
        var enabled = project.Select(static (p, _) => p.Enabled);

        var models = TypeModels(context, project, enabled);
        RegisterFormatters(context, models);
        var calls = TopLevelCalls(context);
        // Built-in generic types used directly with the top-level APIs (e.g. NinoSerializer.Serialize(list)):
        // register closed formatters so AOT platforms have them and generator-only formatters are found.
        var usages = calls.Where(static c => c.Usage != null).Select(static (c, _) => c.Usage!).Collect()
            .WithTrackingName("Usages");
        RegisterNesting(context, models, usages, enabled);
        RegisterUsages(context, models, usages, project, enabled);
#if ROSLYN4_8
        RegisterInterceptors(context, models, calls, enabled);
#endif
        RegisterDispatch(context, models, enabled);
        RegisterRegistry(context, models, FunctionPointerFieldsOf(context, project), project, enabled);
    }

    /// <summary>
    /// The models of the <c>[NinoType]</c>s declared here, in two streams that produce the same models: the types
    /// marked directly, and those that inherit the attribute. Roslyn 4.3 cannot concatenate value providers, so every
    /// output is registered for (or collected from) each of them.
    /// </summary>
    private static IncrementalValuesProvider<TypeModel>[] TypeModels(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled)
    {
        // Types marked [NinoType] directly.
        var marked = context.SyntaxProvider.ForAttributeWithMetadataName(
            NinoNames.NinoTypeAttributeMetadataName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => Transform(ctx.TargetSymbol as INamedTypeSymbol, ctx.SemanticModel, ct))
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
        // Names of the types marked [NinoType], as the compiler binds the attribute: unlike the syntactic check
        // above, this sees aliases (using NT = Nino.Core.NinoTypeAttribute), so their subtypes are found too.
        var markedNames = context.SyntaxProvider.ForAttributeWithMetadataName(
                NinoNames.NinoTypeAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ((TypeDeclarationSyntax)ctx.TargetNode).Identifier.ValueText)
            .Collect();
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
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        return new[] { Gate(marked, enabled, prefetching), Gate(inherited, enabled, prefetching) };
    }

    /// <summary>One formatter file per model.</summary>
    private static void RegisterFormatters(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models)
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
            context.RegisterImplementationSourceOutput(stream.Combine(clashing),
                static (spc, x) => Emit(spc, x.Left, x.Right.Contains(x.Left.FormatterName)));
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
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled)
    {
        var registrations = Merge(usages, CollectAll(models, static m => m.Registrations.Formatters),
            static u => u.Formatter is { } f ? new[] { f } : Array.Empty<FormatterRegistration>(),
            static all => all.OrderBy(u => u.TypeName, StringComparer.Ordinal)
                .ThenBy(u => u.Formatter.ToString(), StringComparer.Ordinal))
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
            registrations.Combine(factories).Combine(generics).Combine(layouts).Combine(enabled),
            static (spc, x) =>
            {
                var ((((formatters, creators), closed), kept), on) = x;
                if (on)
                    spc.AddSource("NinoGen.Usages.g.cs",
                        RegistryEmitter.EmitUsages(formatters, creators, closed, kept));
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
        IncrementalValueProvider<bool> enabled)
    {
        var nesting = Merge(usages, CollectAll(models, static m => m.Registrations.Nesting), static u => u.Nesting,
                static all => all.GroupBy(static n => n.Name)
                    .Select(static g => g.OrderBy(static n => n.Body, StringComparer.Ordinal).First())
                    .OrderBy(static n => n.Name, StringComparer.Ordinal))
            .WithTrackingName("Nesting");
        context.RegisterImplementationSourceOutput(nesting.Combine(enabled), static (spc, x) =>
        {
            if (x.Right && x.Left.Length != 0)
                spc.AddSource("NinoGen.Nesting.g.cs", NestingEmitter.Emit(x.Left));
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
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<bool> enabled)
    {
        var dispatch = CollectAll(models, static m => new DispatchEntry(m.TypeName, m.FormatterName,
                m.TypeParameterList, m.TypeId, m.IsDispatchRoot, m.IsDispatchTarget, m.Inheritance.NinoBaseClasses,
                m.Inheritance.NinoInterfaces, m.Inheritance.NinoGenericAncestors, m.RawBody))
            .Select(static (x, _) => new EquatableArray<DispatchEntry>(x.Where(static e => e.IsBase || e.IsTarget)
                .Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)))
            .WithTrackingName("Dispatch");
        context.RegisterImplementationSourceOutput(dispatch.Combine(enabled), static (spc, x) =>
        {
            if (x.Right)
                spc.AddSource("NinoGen.Dispatch.g.cs", DispatchEmitter.Emit(x.Left));
        });
    }

    /// <summary>NinoGen.Registry.g.cs: the assembly's formatters, its options and the function pointer fields.</summary>
    private static void RegisterRegistry(IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<TypeModel>[] models, IncrementalValueProvider<EquatableArray<string>> pointerFields,
        IncrementalValueProvider<ProjectFacts> project, IncrementalValueProvider<bool> enabled)
    {
        var entries = CollectAll(models, static m => ToEntry(m))
            .Select(static (x, _) => new EquatableArray<RegistryEntry>(
                x.Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)))
            .WithTrackingName("RegistryEntries");
        var options = project.Select(static (p, _) => p.Options);
        context.RegisterImplementationSourceOutput(entries.Combine(options).Combine(pointerFields).Combine(enabled),
            static (spc, x) =>
            {
                var (((list, opts), pointers), on) = x;
                if (on)
                    spc.AddSource("NinoGen.Registry.g.cs", RegistryEmitter.EmitRegistry(opts, list, pointers));
            });
    }

    /// <summary>The models of a stream, in compilations that reference Nino.Core.</summary>
    private static IncrementalValuesProvider<TypeModel> Gate(IncrementalValuesProvider<TypeModel?> models,
        IncrementalValueProvider<bool> enabled, IncrementalValueProvider<bool> prefetching) =>
        models.Where(static m => m != null)
            .Combine(enabled.Combine(prefetching))
            .Where(static x => x.Right.Left)
            .Select(static (x, _) => x.Right.Right || x.Left!.PrefetchBytes == 0
                ? x.Left!
                : x.Left with { PrefetchBytes = 0, PrefetchMembers = EquatableArray<string>.Empty });

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

    /// <summary>
    /// Whether <paramref name="invocation"/> may call a <c>NinoSerializer</c> / <c>NinoDeserializer</c> method that
    /// names a type (<see cref="NamesType"/>), from syntax alone: the method is named so, and is called on a type named
    /// <c>NinoSerializer</c> or <c>NinoDeserializer</c> (qualified or not), on an identifier (which may be a
    /// <c>using</c> alias of one, declared in any file), or without a receiver (<c>using static</c>).
    /// </summary>
    private static bool MayCallNino(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Name: var name, Expression: var receiver } =>
            NamesType(name, invocation.ArgumentList) && (receiver is IdentifierNameSyntax || IsNinoReceiver(receiver)),
        SimpleNameSyntax name => NamesType(name, invocation.ArgumentList),
        _ => false,
    };

    /// <summary>
    /// Whether a call of <paramref name="name"/> names, from syntax, the type it needs a formatter for: by type
    /// argument (<c>Serialize</c> and <c>Deserialize</c>, inferred or not, and <c>GetFixedSize&lt;T&gt;</c>), or by a
    /// <c>typeof</c> argument (<c>SerializeBoxed</c>, <c>DeserializeBoxed</c> and <c>IsSupported</c>; one given a
    /// <see cref="Type"/> computed at run time names nothing, and is not bound).
    /// </summary>
    private static bool NamesType(SimpleNameSyntax name, ArgumentListSyntax arguments) => name.Identifier.ValueText switch
    {
        "Serialize" or "Deserialize" => true,
        "GetFixedSize" => name is GenericNameSyntax,
        "SerializeBoxed" or "DeserializeBoxed" or "IsSupported" => TypeOfArgument(arguments) != null,
        _ => false,
    };

    /// <summary>The first <c>typeof</c> argument of a call, or null.</summary>
    private static TypeOfExpressionSyntax? TypeOfArgument(ArgumentListSyntax arguments)
    {
        foreach (var argument in arguments.Arguments)
        {
            if (argument.Expression is TypeOfExpressionSyntax typeOf)
                return typeOf;
        }

        return null;
    }

    private static bool IsNinoReceiver(ExpressionSyntax receiver)
    {
        var name = receiver switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            TypeSyntax t => InheritedCandidates.SimpleName(t),
            _ => null,
        };
        return name is NinoNames.Serializer or NinoNames.Deserializer;
    }

    /// <summary>
    /// Whether the declaration has an attribute named like <c>[NinoType]</c>. A syntax-only hint, which keeps
    /// marked types from being bound as inheritance candidates: it misses aliased attributes, whose types then
    /// become candidates and are skipped once bound, and whose names reach <see cref="InheritedCandidates.Find"/>
    /// through the bound attribute instead.
    /// </summary>
    private static bool HasNinoTypeSyntax(TypeDeclarationSyntax syntax)
    {
        foreach (var list in syntax.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (InheritedCandidates.SimpleName(attribute.Name) is { } name &&
                    (name.EndsWith(NinoNames.NinoType, StringComparison.Ordinal) ||
                     name.EndsWith(NinoNames.NinoTypeAttribute, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    private static TypeModel? Transform(INamedTypeSymbol? symbol, SemanticModel semanticModel, CancellationToken ct)
    {
        if (symbol == null)
            return null;
        ct.ThrowIfCancellationRequested();
        var compilation = semanticModel.Compilation;
        var options = semanticModel.SyntaxTree.Options;
        return ModelCache.GetOrBuild(symbol, compilation, options,
            () => ModelBuilder.Build(symbol, TypeFacts.For(compilation, options), ct));
    }

    private static TopLevelCall? TransformCall(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var compilation = ctx.SemanticModel.Compilation;
        var options = ctx.SemanticModel.SyntaxTree.Options;
        return ModelCache.GetOrBuildCall(ctx.Node, compilation, () => BindCall(ctx, ct), (method, type) =>
        {
            var facts = TypeFacts.For(compilation, options);
            var usage = TransformUsage(type, facts);
#if ROSLYN4_8
            // Only Serialize and Deserialize have NinoDirect counterparts to intercept with.
            var intercepted = method.Name is "Serialize" or "Deserialize" && Interceptors.Enabled(options)
                ? Interceptors.Transform(ctx, method, facts, ct)
                : null;
#else
            InterceptedCall? intercepted = null;
#endif
            return usage == null && intercepted == null ? null : new TopLevelCall(usage, intercepted);
        });
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
        var owner = method.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) &&
            !SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer))
            return null;
        ITypeSymbol? type = method.IsGenericMethod
            ? method.TypeArguments[0]
            : TypeOfArgument(((InvocationExpressionSyntax)ctx.Node).ArgumentList) is { } typeOf
                ? ctx.SemanticModel.GetTypeInfo(typeOf.Type, ct).Type
                : null;
        if (type is null or { TypeKind: TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer } or
            { SpecialType: SpecialType.System_Void } or INamedTypeSymbol { IsUnboundGenericType: true })
            return null;
        // Vector<T>, alone or inside (NINO029): nothing to register or intercept, the regular call refuses it.
        if (facts.Raw.HostSizedVectorIn(type) != null)
            return null;
        return (method, type);
    }

    private static UsageRegistration? TransformUsage(ITypeSymbol type, TypeFacts facts)
    {
        if (TypeNames.ContainsTypeParameter(type))
            return null;
        var generics = new List<TypeRegistration>();
        ClosedGenerics.Collect(type, facts, generics);
        var layouts = new List<string>();
        LayoutTypes.Collect(type, facts, layouts);
        var formatter = facts.Resolver.Resolve(type, false);
        bool registered = formatter is { Registered: true } && !facts.Raw.IsRawUnmanaged(type) &&
                          facts.Visibility.CanName(type);
        // The nesting formatters the registration, the closed generics or the interceptor (the same formatter) name.
        var nesting = formatter is { } named
            ? facts.Resolver.NestingFormattersNamedIn(generics.Select(static g => g.Formatter).Append(named.Code.ToString()))
            : EquatableArray<NestingModel>.Empty;
        if (!registered && generics.Count == 0 && layouts.Count == 0 && nesting.Length == 0)
            return null;
        return new UsageRegistration(
            registered
                ? new FormatterRegistration(TypeNames.Global(type), formatter!.Value.Code, facts.TypeId(type))
                : null,
            registered ? formatter!.Value.Factories : EquatableArray<FactoryRegistration>.Empty,
            new EquatableArray<TypeRegistration>(generics.Distinct()),
            LayoutTypes.ToArray(layouts), nesting);
    }

    private static RegistryEntry ToEntry(TypeModel model) =>
        new(model.FormatterName, model.FormatterArity, RegistryEmitter.Registration(model));

    /// <summary>
    /// Emits the formatter of <paramref name="model"/>; <paramref name="clashes"/>: another formatter's name differs
    /// from its own only by case, so its hint name carries the hash of its exact name.
    /// </summary>
    private static void Emit(SourceProductionContext spc, TypeModel model, bool clashes) =>
        spc.AddSource(HintName(model.FormatterName, clashes), EmittedFormatters.Get(model, EmitSource));

    /// <summary>The source of the formatter of <paramref name="model"/>.</summary>
    private static string EmitSource(TypeModel model)
    {
        try
        {
            return FormatterEmitter.Emit(model);
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
