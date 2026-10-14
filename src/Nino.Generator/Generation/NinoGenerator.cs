using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator.Generation;

/// <summary>
/// Incremental generator: one formatter file per <c>[NinoType]</c>, plus an assembly registry.
/// <para>
/// Each type is turned into an equatable <see cref="TypeModel"/> inside the transform, so editing one type only
/// re-emits that type's file. The registry depends only on the list of formatter names.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class NinoGenerator : IIncrementalGenerator
{
    private const string NinoTypeAttribute = "Nino.Core.NinoTypeAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enabled = context.CompilationProvider.Select(static (c, _) =>
            c.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"));

        // Types marked [NinoType] directly.
        var marked = context.SyntaxProvider.ForAttributeWithMetadataName(
            NinoTypeAttribute,
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
        var candidates = declarations.Combine(referencedNinoTypes)
            .Select(static (x, _) => InheritedCandidates.Find(x.Left!, x.Right));

        var inherited = candidates.Combine(context.CompilationProvider)
            .SelectMany(static (x, ct) =>
            {
                var result = new System.Collections.Generic.List<TypeModel?>();
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

        var models = marked.Where(static m => m != null)
            .Combine(enabled)
            .Where(static x => x.Right)
            .Select(static (x, _) => x.Left!);
        var inheritedModels = inherited.Where(static m => m != null)
            .Combine(enabled)
            .Where(static x => x.Right)
            .Select(static (x, _) => x.Left!);

        context.RegisterSourceOutput(models, static (spc, model) => Emit(spc, model));
        context.RegisterSourceOutput(inheritedModels, static (spc, model) => Emit(spc, model));

        var options = context.CompilationProvider.Select(static (c, _) => new GeneratorOptions(
            c.AssemblyName ?? "Assembly",
            c.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute") != null,
            c.GetTypeByMetadataName("UnityEngine.RuntimeInitializeOnLoadMethodAttribute") != null,
            c.GetTypeByMetadataName("UnityEditor.InitializeOnLoadMethodAttribute") != null));

        // Formatters for collection types Nino.Core cannot reference (System.Collections.Immutable is not
        // available everywhere, PriorityQueue only exists on .NET 6+) are emitted into the assembly that uses them.
        var helpers = context.CompilationProvider.Select(static (c, _) => (
            Enabled: c.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"),
            Immutable: c.GetTypeByMetadataName("System.Collections.Immutable.ImmutableArray`1") != null,
            PriorityQueue: c.GetTypeByMetadataName("System.Collections.Generic.PriorityQueue`2") != null));
        context.RegisterSourceOutput(helpers, static (spc, x) =>
        {
            if (!x.Enabled)
                return;
            if (x.Immutable)
                spc.AddSource("NinoGen.ImmutableFormatters.g.cs", Emitter.EmitImmutableFormatters());
            if (x.PriorityQueue)
                spc.AddSource("NinoGen.PriorityQueueFormatter.g.cs", Emitter.EmitPriorityQueueFormatter());
        });

        var entries = models.Select(static (m, _) => ToEntry(m)).Collect()
            .Combine(inheritedModels.Select(static (m, _) => ToEntry(m)).Collect())
            .Select(static (x, _) => new DTOs.EquatableArray<RegistryEntry>(
                x.Left.Concat(x.Right).Distinct().OrderBy(e => e.FormatterName, System.StringComparer.Ordinal)));

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
        var memberFormatters = models.Select(static (m, _) => m.UsedFormatters).Collect()
            .Combine(inheritedModels.Select(static (m, _) => m.UsedFormatters).Collect());

        var registrations = usages.Combine(memberFormatters)
            .Select(static (x, _) => new DTOs.EquatableArray<FormatterRegistration>(
                x.Left.Concat(x.Right.Left.SelectMany(static a => a)).Concat(x.Right.Right.SelectMany(static a => a))
                    .Distinct()
                    .OrderBy(u => u.TypeName, System.StringComparer.Ordinal)
                    .ThenBy(u => u.Formatter, System.StringComparer.Ordinal)));

        context.RegisterSourceOutput(registrations.Combine(enabled), static (spc, x) =>
        {
            if (x.Right)
                spc.AddSource("NinoGen.Usages.g.cs", Emitter.EmitUsages(x.Left));
        });

        context.RegisterSourceOutput(entries.Combine(options).Combine(enabled), static (spc, x) =>
        {
            var ((list, opts), on) = x;
            if (!on)
                return;
            spc.AddSource("NinoGen.Registry.g.cs", Emitter.EmitRegistry(opts, list));
        });
    }

    private static bool IsNinoReceiver(ExpressionSyntax receiver)
    {
        var name = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => null,
        };
        return name is "NinoSerializer" or "NinoDeserializer";
    }

    private static bool HasNinoTypeSyntax(TypeDeclarationSyntax syntax)
    {
        foreach (var list in syntax.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.EndsWith("NinoType") || name.EndsWith("NinoTypeAttribute"))
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
        return ModelCache.GetOrBuild(symbol, compilation, options, () => ModelBuilder.Build(symbol, compilation, options));
    }

    private static FormatterRegistration? TransformUsage(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ModelBuilder.ResetMemo();
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol { IsGenericMethod: true } method)
            return null;
        var owner = method.ContainingType;
        if (owner == null || owner.Name is not ("NinoSerializer" or "NinoDeserializer") ||
            !NinoTypeHelper.IsNinoCoreNamespace(owner.ContainingNamespace))
            return null;
        var type = method.TypeArguments[0];
        if (ContainsTypeParameter(type))
            return null;
        bool weak = ctx.SemanticModel.SyntaxTree.Options.PreprocessorSymbolNames
            .Contains(NinoTypeHelper.WeakVersionToleranceSymbol);
        var assembly = ctx.SemanticModel.Compilation.Assembly;
        var formatter = new FormatterResolver(assembly, weak).Resolve(type, false);
        if (formatter == null || !ModelBuilder.IsBuiltInFormatter(formatter) ||
            FormatterResolver.IsRawUnmanaged(type, weak ? assembly : null))
            return null;
        return new FormatterRegistration(TypeNames.Global(type), formatter,
            TypeNames.ComputeId(TypeNames.RuntimeName(type)));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    private static RegistryEntry ToEntry(TypeModel model)
    {
        int arity = model.IsGeneric ? model.TypeParameters.Count(c => c == ',') + 1 : 0;
        return new RegistryEntry(model.FormatterName, model.OpenTypeName, arity, model.TypeId, model.NameId);
    }

    private static void Emit(SourceProductionContext spc, TypeModel model)
    {
        foreach (var diagnostic in model.Diagnostics)
            spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        spc.AddSource($"NinoGen.{model.FormatterName}.g.cs", Emitter.EmitType(model));
    }
}
