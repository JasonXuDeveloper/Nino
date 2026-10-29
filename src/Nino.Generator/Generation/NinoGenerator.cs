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

        // Both streams produce the same models; Roslyn 4.3 cannot concatenate value providers, so every output
        // below is registered for (or collected from) each of them.
        var models = new[] { Gate(marked, enabled), Gate(inherited, enabled) };
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

        var options = project.Select(static (p, _) => p.Options);

        var entries = CollectAll(models, static m => ToEntry(m))
            .Select(static (x, _) => new EquatableArray<RegistryEntry>(
                x.Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)))
            .WithTrackingName("RegistryEntries");

        // The top-level NinoSerializer/NinoDeserializer calls, bound once for what they register and (Roslyn 4.8+)
        // how they are intercepted. Narrowed by syntax first (binding every Serialize call of other libraries on each
        // edit is not free), to the receivers that may name them: NinoSerializer or NinoDeserializer, an identifier
        // (a using alias), or none (using static); the bound method decides.
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && MayCallNino(invocation),
                static (ctx, ct) => TransformCall(ctx, ct))
            .WithTrackingName("TopLevelCall")
            .Where(static c => c != null)
            .Select(static (c, _) => c!);

        // Built-in generic types used directly with the top-level APIs (e.g. NinoSerializer.Serialize(list)):
        // register closed formatters so AOT platforms have them and generator-only formatters are found.
        var usages = calls.Where(static c => c.Usage != null).Select(static (c, _) => c.Usage!).Collect()
            .WithTrackingName("Usages");

        // Closed built-in formatters used by members: registered once per assembly here rather than by every
        // type that uses them (the same List<int> formatter is typically used by many types); creators of the
        // collections those formatters create (NinoActivator<T>), registered the same way; and closed generic
        // [NinoType]s named anywhere above, registered by type id (they have no registration of their own: a
        // polymorphic read must be able to map their id back to the type).
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

#if ROSLYN4_8
        // Top-level calls with a known type argument call the resolved formatter directly (where enabled).
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
        context.RegisterImplementationSourceOutput(
            intercepted.Combine(fileLocal).Combine(enabled).Combine(top).Combine(shallow),
            static (spc, x) =>
            {
                var ((((calls, local), on), tops), twins) = x;
                if (on && calls.Length != 0)
                    spc.AddSource("NinoGen.Interceptors.g.cs", Interceptors.Emit(calls, local, tops, twins));
            });

#endif
        // Bases dispatch the subtypes of this assembly through generated code; any other subtype through the registry.
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

        context.RegisterImplementationSourceOutput(entries.Combine(options).Combine(enabled), static (spc, x) =>
        {
            var ((list, opts), on) = x;
            if (!on)
                return;
            spc.AddSource("NinoGen.Registry.g.cs", RegistryEmitter.EmitRegistry(opts, list));
        });
    }

    /// <summary>The models of a stream, in compilations that reference Nino.Core.</summary>
    private static IncrementalValuesProvider<TypeModel> Gate(IncrementalValuesProvider<TypeModel?> models,
        IncrementalValueProvider<bool> enabled) =>
        models.Where(static m => m != null)
            .Combine(enabled)
            .Where(static x => x.Right)
            .Select(static (x, _) => x.Left!);

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
    /// Whether <paramref name="invocation"/> may call <c>NinoSerializer.Serialize</c> or
    /// <c>NinoDeserializer.Deserialize</c>, from syntax alone: the method is named so, and is called on a type named
    /// <c>NinoSerializer</c> or <c>NinoDeserializer</c> (qualified or not), on an identifier (which may be a
    /// <c>using</c> alias of one, declared in any file), or without a receiver (<c>using static</c>).
    /// </summary>
    private static bool MayCallNino(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Name: var name, Expression: var receiver } =>
            IsTopLevelMethod(name) && (receiver is IdentifierNameSyntax || IsNinoReceiver(receiver)),
        SimpleNameSyntax name => IsTopLevelMethod(name),
        _ => false,
    };

    private static bool IsTopLevelMethod(SimpleNameSyntax name) =>
        name.Identifier.ValueText is "Serialize" or "Deserialize";

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
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol { IsGenericMethod: true } method)
            return null;
        var options = ctx.SemanticModel.SyntaxTree.Options;
        var facts = TypeFacts.For(ctx.SemanticModel.Compilation, options);
        var owner = method.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) &&
            !SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer))
            return null;
        return ModelCache.GetOrBuildCall(ctx.Node, method, ctx.SemanticModel.Compilation, () =>
        {
            var usage = TransformUsage(method, facts);
#if ROSLYN4_8
            var intercepted = Interceptors.Enabled(options) ? Interceptors.Transform(ctx, method, facts, ct) : null;
#else
            InterceptedCall? intercepted = null;
#endif
            return usage == null && intercepted == null ? null : new TopLevelCall(usage, intercepted);
        });
    }

    private static UsageRegistration? TransformUsage(IMethodSymbol method, TypeFacts facts)
    {
        var type = method.TypeArguments[0];
        if (TypeNames.ContainsTypeParameter(type))
            return null;
        var generics = new List<TypeRegistration>();
        ClosedGenerics.Collect(type, facts, generics);
        var layouts = new List<string>();
        LayoutTypes.Collect(type, facts, layouts);
        var formatter = facts.Resolver.Resolve(type, false);
        bool registered = formatter is { Registered: true } && !facts.Raw.IsRawUnmanaged(type) &&
                          facts.Visibility.CanName(type);
        if (!registered && generics.Count == 0 && layouts.Count == 0)
            return null;
        return new UsageRegistration(
            registered
                ? new FormatterRegistration(TypeNames.Global(type), formatter!.Value.Code, facts.TypeId(type))
                : null,
            registered ? formatter!.Value.Factories : EquatableArray<FactoryRegistration>.Empty,
            new EquatableArray<TypeRegistration>(generics.Distinct()),
            LayoutTypes.ToArray(layouts));
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
