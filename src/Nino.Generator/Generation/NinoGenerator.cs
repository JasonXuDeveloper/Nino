using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.DTOs;
using Nino.Generator.Generation.Emit;

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
        var enabled = context.CompilationProvider.Select(static (c, _) =>
            c.ReferencedAssemblyNames.Any(static a => a.Name == NinoNames.CoreAssembly));

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
        var referencedNinoTypes = context.CompilationProvider
            .Select(static (c, ct) => InheritedCandidates.ReferencedNinoTypes(c, ct));
        // Names of the types marked [NinoType], as the compiler binds the attribute: unlike the syntactic check
        // above, this sees aliases (using NT = Nino.Core.NinoTypeAttribute), so their subtypes are found too.
        var markedNames = context.SyntaxProvider.ForAttributeWithMetadataName(
                NinoNames.NinoTypeAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ((TypeDeclarationSyntax)ctx.TargetNode).Identifier.ValueText)
            .Collect();
        var candidates = declarations.Combine(referencedNinoTypes).Combine(markedNames)
            .Select(static (x, _) => InheritedCandidates.Find(x.Left.Left!, x.Left.Right, x.Right));

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
        foreach (var stream in models)
            context.RegisterImplementationSourceOutput(stream, static (spc, model) => Emit(spc, model));

        var options = context.CompilationProvider.Select(static (c, _) => new GeneratorOptions(
            c.AssemblyName ?? "Assembly",
            c.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute") != null,
            c.GetTypeByMetadataName("UnityEngine.RuntimeInitializeOnLoadMethodAttribute") != null,
            c.GetTypeByMetadataName("UnityEditor.InitializeOnLoadMethodAttribute") != null,
            new EquatableArray<string>(FormatterRules.RegisteringReferences(c)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray())));

        var entries = CollectAll(models, static m => ToEntry(m))
            .Select(static (x, _) => new EquatableArray<RegistryEntry>(
                x.Distinct().OrderBy(e => e.FormatterName, StringComparer.Ordinal)));

        // Built-in generic types used directly with the top-level APIs (e.g. NinoSerializer.Serialize(list)):
        // register closed formatters so AOT platforms have them and generator-only formatters are found.
        var usages = context.SyntaxProvider.CreateSyntaxProvider(
                // Receiver checked syntactically too: binding every Serialize call of other libraries
                // (JsonSerializer...) on each edit is not free.
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "Serialize" or "Deserialize" or "DeserializeRef"
                    } access
                } && IsNinoReceiver(access.Expression),
                static (ctx, ct) => TransformUsage(ctx, ct))
            .WithTrackingName("Usage")
            .Where(static u => u != null)
            .Select(static (u, _) => u!)
            .Collect();

        // Closed built-in formatters used by members: registered once per assembly here rather than by every
        // type that uses them (the same List<int> formatter is typically used by many types).
        var memberFormatters = CollectAll(models, static m => m.UsedFormatters);
        // Creators of the collections those formatters create (NinoFactory<T>), registered the same way.
        var memberFactories = CollectAll(models, static m => m.UsedFactories);
        // Closed generic [NinoType]s named anywhere above, registered by type id (they have no registration of
        // their own: a polymorphic read must be able to map their id back to the type).
        var memberGenerics = CollectAll(models, static m => m.UsedGenericTypes);

        var registrations = usages.Combine(memberFormatters)
            .Select(static (x, _) => new EquatableArray<FormatterRegistration>(
                x.Left.Where(static u => u.Formatter != null).Select(static u => u.Formatter!)
                    .Concat(x.Right.SelectMany(static a => a))
                    .Distinct()
                    .OrderBy(u => u.TypeName, StringComparer.Ordinal)
                    .ThenBy(u => u.Formatter, StringComparer.Ordinal)));
        var factories = usages.Combine(memberFactories)
            .Select(static (x, _) => new EquatableArray<FactoryRegistration>(
                x.Left.SelectMany(static u => u.Factories).Concat(x.Right.SelectMany(static a => a))
                    .Distinct()
                    .OrderBy(f => f.TypeName, StringComparer.Ordinal)));

        var generics = usages.Combine(memberGenerics)
            .Select(static (x, _) => new EquatableArray<ClosedTypeRegistration>(
                x.Left.SelectMany(static u => u.GenericTypes).Concat(x.Right.SelectMany(static a => a))
                    .Distinct()
                    .OrderBy(g => g.TypeName, StringComparer.Ordinal)));

        context.RegisterImplementationSourceOutput(registrations.Combine(factories).Combine(generics).Combine(enabled),
            static (spc, x) =>
            {
                var (((formatters, creators), closed), on) = x;
                if (on)
                    spc.AddSource("NinoGen.Usages.g.cs", RegistryEmitter.EmitUsages(formatters, creators, closed));
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
            () => ModelBuilder.Build(symbol, TypeFacts.For(compilation, options)));
    }

    private static UsageRegistration? TransformUsage(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol { IsGenericMethod: true } method)
            return null;
        var facts = TypeFacts.For(ctx.SemanticModel.Compilation, ctx.SemanticModel.SyntaxTree.Options);
        var owner = method.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) &&
            !SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer))
            return null;
        var type = method.TypeArguments[0];
        if (ContainsTypeParameter(type))
            return null;
        var generics = new List<ClosedTypeRegistration>();
        ClosedGenerics.Collect(type, facts, generics);
        var formatter = facts.Resolver.Resolve(type, false);
        bool registered = formatter is { Registered: true } && !facts.Raw.IsRawUnmanaged(type);
        if (!registered && generics.Count == 0)
            return null;
        return new UsageRegistration(
            registered
                ? new FormatterRegistration(TypeNames.Global(type), formatter!.Value.Code, facts.TypeId(type))
                : null,
            registered ? formatter!.Value.Factories : EquatableArray<FactoryRegistration>.Empty,
            new EquatableArray<ClosedTypeRegistration>(generics.Distinct()));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    private static RegistryEntry ToEntry(TypeModel model) =>
        new(model.FormatterName, model.TypeParameters.Length, model.TypeId, model.NameId);

    private static void Emit(SourceProductionContext spc, TypeModel model)
    {
        spc.AddSource($"NinoGen.{model.FormatterName}.g.cs", FormatterEmitter.Emit(model));
    }
}
