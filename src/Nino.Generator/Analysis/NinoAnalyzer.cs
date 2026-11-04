using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nino.Generator.Generation;
using Nino.Generator.Common;

namespace Nino.Generator.Analysis;

/// <summary>
/// Reports every Nino diagnostic: attribute usage, and what the generator would find while building a type's model
/// (the same <see cref="ModelBuilder"/>). The generator itself reports nothing, so its models carry no locations.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NinoAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => NinoDiagnostics.All;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Nothing to check in compilations that do not reference Nino (checked before building any facts).
            if (start.Compilation.GetTypeByMetadataName(NinoNames.NinoTypeAttributeMetadataName) == null)
                return;
            var symbols = TypeFacts.For(start.Compilation, start.Compilation.SyntaxTrees.FirstOrDefault()?.Options).Symbols;
            // Symbol actions run once per type, not once per partial declaration.
            start.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
            if (symbols.Factory is { } factoryAttribute)
                start.RegisterSymbolAction(c => AnalyzeMethod(c, factoryAttribute), SymbolKind.Method);
            start.RegisterSyntaxNodeAction(AnalyzeAssemblyAttributes, SyntaxKind.AttributeList);
            if (symbols.IFormatter?.ContainingAssembly is { } core)
            {
                start.RegisterOperationAction(c => AnalyzeCall(c, symbols), OperationKind.Invocation);
                start.RegisterOperationAction(c => AnalyzeFormatter(c, core), OperationKind.ObjectCreation,
                    OperationKind.DefaultValue);
                start.RegisterSymbolAction(c => AnalyzeFormatterStorage(c, core), SymbolKind.Field, SymbolKind.Property);
                start.RegisterOperationAction(c => AnalyzeFormatterLocal(c, core), OperationKind.VariableDeclarator);
            }
        });
    }

    // [assembly: NinoFormatter(...)] registrations: one that FormatterRules cannot use is otherwise silently ignored.
    private static void AnalyzeAssemblyAttributes(SyntaxNodeAnalysisContext context)
    {
        var list = (AttributeListSyntax)context.Node;
        if (list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) != true)
            return;
        var symbols = TypeFacts.For(context.Compilation, list.SyntaxTree.Options).Symbols;
        if (symbols.FormatterAttribute == null)
            return;
        foreach (var attribute in context.Compilation.Assembly.GetAttributes())
        {
            if (attribute.ApplicationSyntaxReference is not { } syntax || syntax.SyntaxTree != list.SyntaxTree ||
                !list.Span.Contains(syntax.Span) ||
                !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.FormatterAttribute))
                continue;
            FormatterRules.Read(attribute, symbols, out var error);
            if (error == null)
                continue;
            var formatter = attribute.ConstructorArguments[0].Value as ITypeSymbol;
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFormatterRegistration,
                Location.Create(syntax.SyntaxTree, syntax.Span), formatter?.ToDisplayString(), error));
        }
    }

    /// <summary>
    /// A call whose value is or holds <c>Vector&lt;T&gt;</c> (NINO029): a top-level <c>NinoSerializer</c> /
    /// <c>NinoDeserializer</c> call, for which the generator registers and intercepts nothing, or a generic method of
    /// <c>Writer</c>, <c>Reader</c> or <c>NinoDirect</c> used by hand; each throws.
    /// </summary>
    private static void AnalyzeCall(OperationAnalysisContext context, NinoSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        // Cheapest rejection first: a Serialize / Deserialize name or a generic method, then the class.
        bool serializes = method.Name.StartsWith("Serialize", System.StringComparison.Ordinal) ||
                          method.Name.StartsWith("Deserialize", System.StringComparison.Ordinal);
        if (!serializes && !method.IsGenericMethod)
            return;
        var owner = method.ContainingType;
        ITypeSymbol? type, vector;
        if (serializes && (SymbolEqualityComparer.Default.Equals(owner, symbols.Serializer) ||
                           SymbolEqualityComparer.Default.Equals(owner, symbols.Deserializer)))
        {
            // The type the call needs a formatter for, as the generator finds it: its type argument, or a typeof
            // argument.
            type = method.IsGenericMethod
                ? method.TypeArguments[0]
                : invocation.Arguments.Select(static a => a.Value).OfType<ITypeOfOperation>().FirstOrDefault()
                    ?.TypeOperand;
            vector = type == null ? null : Raw(context, invocation).HostSizedVectorIn(type);
        }
        else if (method.IsGenericMethod && (SymbolEqualityComparer.Default.Equals(owner, symbols.Writer) ||
                                            SymbolEqualityComparer.Default.Equals(owner, symbols.Reader) ||
                                            SymbolEqualityComparer.Default.Equals(owner, symbols.Direct)))
            vector = Raw(context, invocation).HostSizedVectorWrittenBy(method, out type);
        else
            return;

        if (vector != null && type != null)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.HostSizedVector, invocation.Syntax.GetLocation(),
                $"The value of this {owner.Name}.{method.Name} call", type.ToDisplayString(),
                NinoDiagnostics.HoldsVector(type, vector)));
    }

    /// <summary>
    /// One of Nino.Core's generic formatters made by hand (<c>new UnmanagedFormatter&lt;Vector&lt;int&gt;&gt;()</c>,
    /// <c>default(...)</c>) whose value is or holds <c>Vector&lt;T&gt;</c> (NINO029): it throws when used. A value
    /// stored straight into a field, property or local of its own type is reported there instead
    /// (<see cref="AnalyzeFormatterStorage"/>), once.
    /// </summary>
    private static void AnalyzeFormatter(OperationAnalysisContext context, IAssemblySymbol core)
    {
        // Cheapest rejection first: a generic type of Nino.Core.
        if (!IsCoreGeneric(context.Operation.Type, core, out var formatter) ||
            StoredIntoItsOwnType(context.Operation, formatter))
            return;
        ReportFormatter(context.ReportDiagnostic, context.Compilation, context.Operation.Syntax, formatter,
            $"The value of this {Display(formatter)}");
    }

    /// <summary>
    /// A field or property whose type is one of Nino.Core's generic formatters over <c>Vector&lt;T&gt;</c> (NINO029):
    /// a formatter struct needs no constructor call (a field of one is ready to use as it is), so the declaration is
    /// reported, not only the <c>new</c> or <c>default</c> that may fill it.
    /// </summary>
    private static void AnalyzeFormatterStorage(SymbolAnalysisContext context, IAssemblySymbol core)
    {
        var symbol = context.Symbol;
        var type = symbol switch
        {
            IFieldSymbol { IsImplicitlyDeclared: false } field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };
        if (!IsCoreGeneric(type, core, out var formatter) ||
            symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not { } syntax)
            return;
        // A serialized member of a [NinoType] whose type holds the vector is reported by the member rule, once.
        var facts = TypeFacts.For(context.Compilation, syntax.SyntaxTree.Options);
        if (symbol.ContainingType.GetNinoTypeOptions() is { } attr &&
            !facts.Symbols.HasCustomFormatter(symbol) && facts.Raw.HostSizedVectorIn(formatter) != null &&
            facts.Members.Of(symbol.ContainingType, attr).Contains(symbol, SymbolEqualityComparer.Default))
            return;
        ReportFormatter(context.ReportDiagnostic, context.Compilation, syntax, formatter,
                $"The {Display(formatter)} in '{symbol.ContainingType.Name}.{symbol.Name}'", symbol.Locations[0]);
    }

    /// <summary><see cref="AnalyzeFormatterStorage"/> for a local (<c>UnmanagedFormatter&lt;Vector&lt;int&gt;&gt; f;</c>).</summary>
    private static void AnalyzeFormatterLocal(OperationAnalysisContext context, IAssemblySymbol core)
    {
        var local = ((IVariableDeclaratorOperation)context.Operation).Symbol;
        if (IsCoreGeneric(local.Type, core, out var formatter))
            ReportFormatter(context.ReportDiagnostic, context.Compilation, context.Operation.Syntax, formatter,
                $"The {Display(formatter)} in '{local.Name}'", local.Locations[0]);
    }

    private static bool IsCoreGeneric(ITypeSymbol? type, IAssemblySymbol core, out INamedTypeSymbol formatter)
    {
        formatter = (type as INamedTypeSymbol)!;
        return formatter is { IsGenericType: true } &&
               SymbolEqualityComparer.Default.Equals(formatter.ContainingAssembly, core);
    }

    private static string Display(INamedTypeSymbol formatter) =>
        formatter.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    /// <summary>
    /// Whether <paramref name="value"/> goes straight into a field, property or local declared in source with its own
    /// type <paramref name="formatter"/>, whose declaration NINO029 reports.
    /// </summary>
    private static bool StoredIntoItsOwnType(IOperation value, INamedTypeSymbol formatter)
    {
        // A default literal reaches its target through an implicit conversion.
        while (value.Parent is IConversionOperation { IsImplicit: true } conversion)
            value = conversion;
        ISymbol? target = value.Parent switch
        {
            IFieldInitializerOperation initializer => initializer.InitializedFields.FirstOrDefault(),
            IPropertyInitializerOperation initializer => initializer.InitializedProperties.FirstOrDefault(),
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } => declarator.Symbol,
            ISimpleAssignmentOperation assignment when assignment.Value == value => assignment.Target switch
            {
                IFieldReferenceOperation field => field.Field,
                IPropertyReferenceOperation property => property.Property,
                ILocalReferenceOperation local => local.Local,
                _ => null,
            },
            _ => null,
        };
        var type = target switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            ILocalSymbol local => local.Type,
            _ => null,
        };
        return type != null && SymbolEqualityComparer.Default.Equals(type, formatter) &&
               target!.Locations.Any(static l => l.IsInSource);
    }

    private static void ReportFormatter(System.Action<Diagnostic> report, Compilation compilation, SyntaxNode syntax,
        INamedTypeSymbol formatter, string what, Location? location = null)
    {
        if (TypeFacts.For(compilation, syntax.SyntaxTree.Options).Raw.HostSizedVectorWrittenBy(formatter, out var type)
                is { } vector && type != null)
            report(Diagnostic.Create(NinoDiagnostics.HostSizedVector, location ?? syntax.GetLocation(), what,
                type.ToDisplayString(), NinoDiagnostics.HoldsVector(type, vector)));
    }

    private static RawRules Raw(OperationAnalysisContext context, IOperation operation) =>
        TypeFacts.For(context.Compilation, operation.Syntax.SyntaxTree.Options).Raw;

    /// <summary>
    /// NINO032: a positional NinoType of a compilation that defines <c>WEAK_VERSION_TOLERANCE</c>, which made it
    /// version tolerant until 4.0. Reported where the fix goes: on the type's own <c>[NinoType]</c>; for a type
    /// that takes its <c>[NinoType]</c> from another of this compilation's types, nowhere (that type's fix covers
    /// it); for one that takes it from another assembly, on the type, with the <c>[NinoType]</c> to declare (the
    /// options it takes, and the format) in <see cref="NinoDiagnostics.NinoTypeToAdd"/>: the generated code is the
    /// same as with the symbol.
    /// </summary>
    private static void ReportRemovedWeakTolerance(SymbolAnalysisContext context, INamedTypeSymbol type,
        NinoTypeOptions attr, TypeFacts facts)
    {
        var source = type.NinoTypeSource();
        if (SymbolEqualityComparer.Default.Equals(source, type) &&
            type.GetDirectNinoTypeAttribute()?.ApplicationSyntaxReference is { } syntax)
        {
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.WeakToleranceRemoved,
                Location.Create(syntax.SyntaxTree, syntax.Span), type.ToDisplayString(),
                "add format: NinoFormat.VersionTolerant to its [NinoType]"));
            return;
        }

        if (source == null || SymbolEqualityComparer.Default.Equals(source.ContainingAssembly, context.Compilation.Assembly) ||
            !facts.Visibility.IsGeneratedHere(type))
            return;
        var arguments = new List<string>();
        if (!attr.AutoCollect)
            arguments.Add("autoCollect: false");
        if (attr.IncludeNonPublicMembers)
            arguments.Add("includeNonPublicMembers: true");
        if (!attr.InheritedBySubtypes)
            arguments.Add("inheritedBySubtypes: false");
        arguments.Add("format: NinoFormat.VersionTolerant");
        if (attr.TrackReferences)
            arguments.Add("trackReferences: true");
        string ninoType = $"NinoType({string.Join(", ", arguments)})";
        context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.WeakToleranceRemoved, type.Locations.FirstOrDefault(),
            ImmutableDictionary<string, string?>.Empty.Add(NinoDiagnostics.NinoTypeToAdd, ninoType),
            type.ToDisplayString(),
            $"declare [{ninoType}] on it (the options it takes from '{source.ToDisplayString()}' in another assembly)"));
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var sink = new DiagnosticSink(context.ReportDiagnostic);
        var attr = type.GetNinoTypeOptions();
        if (attr == null)
        {
            ReportMissingNinoType(sink, type);
            return;
        }

        // The Nino attribute types and the members collected are shared with the generator's model builds.
        var options = type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.Options;
        var facts = TypeFacts.For(context.Compilation, options);
        if (attr.Format == NinoFormat.Positional &&
            options?.PreprocessorSymbolNames.Contains(NinoDiagnostics.RemovedWeakTolerance) == true)
            ReportRemovedWeakTolerance(context, type, attr, facts);
        // Collecting the findings also caches the members, so the model build below reuses them.
        var findings = facts.Members.FindingsOf(type, attr);

        // The generator skips types it cannot name from outside them.
        if (!Visibility.IsEligible(type))
            sink.Report(NinoDiagnostics.InaccessibleType, type, type.ToDisplayString());
        else
            ModelBuilder.Build(type, facts, context.CancellationToken, sink);

        if (NinoSymbols.Has(type, facts.Symbols.Pooled))
        {
            var missing = PoolHooks.Missing(type, context.CancellationToken).ToList();
            if (missing.Count > 0)
                sink.Report(NinoDiagnostics.PoolHooksNotImplemented, type, type.ToDisplayString(),
                    string.Join(" and ", missing.Select(name => name + "()")));
        }

        ReportFormerNames(sink, type, facts.Symbols);

        int duplicates = 0;
        foreach (var finding in findings)
            Report(sink, type, finding, ref duplicates);
    }

    /// <summary>
    /// The type's <c>[NinoFormerName]</c>s. Its type id comes from one: the generator would take the first,
    /// <c>NinoTypeId.Of</c> would throw. And one name gives every instantiation of a generic type the same id, which
    /// collides once two of them are written with their id (<c>NinoSubtypes</c> registers each by it): a type with
    /// a <c>[NinoType]</c> base or interface, or one that has subtypes (an interface, an abstract or unsealed class).
    /// </summary>
    private static void ReportFormerNames(DiagnosticSink sink, INamedTypeSymbol type, NinoSymbols known)
    {
        var formerNames = type.GetAttributes()
            .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, known.FormerName)).ToList();
        if (formerNames.Count > 1)
            sink.Report(NinoDiagnostics.MultipleTypeFormerNames, formerNames[1], type, type.ToDisplayString());
        if (formerNames.Count != 0 && TypeNames.AllTypeParameters(type).Count != 0 &&
            (TypeRules.HasSubtypes(TypeRules.Kind(type)) || type.NinoAncestors().Any()))
            sink.Report(NinoDiagnostics.GenericTypeFormerName, formerNames[0], type, type.ToDisplayString(),
                formerNames[0].ConstructorArguments.FirstOrDefault().Value as string ?? "");
    }

    /// <summary>Reports what <see cref="MemberCollector"/> found about a member <paramref name="type"/> declares.</summary>
    private static void Report(DiagnosticSink sink, INamedTypeSymbol type, MemberFinding finding, ref int duplicates)
    {
        var member = finding.Member;
        switch (finding.Kind)
        {
            case MemberFindingKind.InvalidCustomFormatter:
                sink.Report(NinoDiagnostics.InvalidCustomFormatter, member, finding.Formatter!.ToDisplayString(),
                    MemberCollector.MemberType(member)!.ToDisplayString(), member.Name, type.Name);
                break;
            case MemberFindingKind.NonPublicMember:
                sink.Report(NinoDiagnostics.PrivateMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.RedundantMember:
                sink.Report(NinoDiagnostics.RedundantMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.RedundantIgnore:
                sink.Report(NinoDiagnostics.RedundantIgnore, member, member.Name, type.Name);
                break;
            case MemberFindingKind.NotSerializable:
                // [field: NinoMember] on an auto-property or event: reported on what the user declared.
                var declared = (member as IFieldSymbol)?.AssociatedMember() ?? member;
                sink.Report(NinoDiagnostics.NotSerializableMember, declared, declared.Name, type.Name, finding.Reason!);
                break;
            case MemberFindingKind.AmbiguousMember:
                sink.Report(NinoDiagnostics.AmbiguousMember, member, member.Name, type.Name);
                break;
            case MemberFindingKind.DuplicateIndex:
                var other = finding.Other!;
                sink.ReportWith(NinoDiagnostics.DuplicateIndex, member,
                    ImmutableDictionary<string, string?>.Empty.Add(NinoDiagnostics.DuplicateRank,
                        (duplicates++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    type.Name, member.Name, other.ContainingType.Name, other.Name);
                break;
        }
    }

    /// <summary>A type deriving from a NinoType that does not pass its attribute on must declare its own.</summary>
    private static void ReportMissingNinoType(DiagnosticSink sink, INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface &&
            type.NinoAncestors().FirstOrDefault() is { } ancestor)
            sink.Report(NinoDiagnostics.SubtypeWithoutNinoType, type, type.Name, ancestor.Name);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol attribute)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!NinoSymbols.Has(method, attribute))
            return;

        var errors = NinoSymbols.FactoryErrors(method);
        if (errors.Count > 0)
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.InvalidFactory,
                method.Locations.FirstOrDefault(), method.Name, string.Join(", ", errors)));
    }
}
