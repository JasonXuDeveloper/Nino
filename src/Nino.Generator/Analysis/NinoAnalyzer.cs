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
using Nino.Generator.Generation.Emit;

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
    // [assembly: NinoLayouts(...)]: the layout baseline, unreadable or holding types the code no longer has.
    private static void AnalyzeAssemblyAttributes(SyntaxNodeAnalysisContext context)
    {
        var list = (AttributeListSyntax)context.Node;
        if (list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) != true)
            return;
        var facts = TypeFacts.For(context.Compilation, list.SyntaxTree.Options);
        var symbols = facts.Symbols;
        if (LayoutCheck.For(context.Compilation, facts) is { } layouts &&
            layouts.Attribute.ApplicationSyntaxReference is { } layoutsSyntax &&
            layoutsSyntax.SyntaxTree == list.SyntaxTree && list.Span.Contains(layoutsSyntax.Span))
            ReportBaseline(context, layouts);
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
            // The type the call needs a formatter for, as the generator finds it.
            type = invocation.Syntax is InvocationExpressionSyntax call && invocation.SemanticModel is { } model
                ? symbols.CallType(method, call, model, context.CancellationToken)
                : null;
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
    private static void ReportWeakTolerance(SymbolAnalysisContext context, INamedTypeSymbol type,
        NinoTypeOptions attr, TypeFacts facts)
    {
        var source = type.NinoTypeSource();
        if (SymbolEqualityComparer.Default.Equals(source, type) &&
            type.GetDirectNinoTypeAttribute()?.ApplicationSyntaxReference is { } syntax)
        {
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.WeakToleranceWithoutEffect,
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
        context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.WeakToleranceWithoutEffect, type.Locations.FirstOrDefault(),
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
            options?.PreprocessorSymbolNames.Contains(NinoDiagnostics.WeakToleranceSymbol) == true)
            ReportWeakTolerance(context, type, attr, facts);
        // Collecting the findings also caches the members, so the model build below reuses them.
        var findings = facts.Members.FindingsOf(type, attr);

        // The generator skips types it cannot name from outside them.
        if (!Visibility.IsEligible(type))
            sink.Report(NinoDiagnostics.InaccessibleType, type, type.ToDisplayString());
        else if (ModelBuilder.Build(type, facts, context.CancellationToken, sink) is { } model &&
                 LayoutCheck.For(context.Compilation, facts) is { Error: null } layouts)
            CheckLayout(context, type, model, layouts, facts);

        if (NinoSymbols.Has(type, facts.Symbols.Pooled))
        {
            var missing = PoolHooks.Missing(type, context.CancellationToken).ToList();
            if (missing.Count > 0)
                sink.Report(NinoDiagnostics.PoolHooksNotImplemented, type, type.ToDisplayString(),
                    string.Join(" and ", missing.Select(name => name + "()")));
        }

        ReportFormerNames(sink, type, facts.Symbols);
        if (type.TypeKind == TypeKind.Interface)
            ReportInterfaceAttributes(sink, type, facts, findings);
        else
            ReportUtf8(sink, type, attr, facts, findings);

        int duplicates = 0;
        foreach (var finding in findings)
            Report(sink, type, finding, facts.Symbols, ref duplicates);
    }

    /// <summary>
    /// NINO034-NINO036: the layout of <paramref name="type"/> (<paramref name="model"/>'s, as published) against its
    /// section in the layout baseline, if it has one, by <see cref="LayoutRules.Compare"/>. Reported on the member
    /// that changed, else on the type; each carries the type's section as it is now, which accepting writes.
    /// </summary>
    private static void CheckLayout(SymbolAnalysisContext context, INamedTypeSymbol type, TypeModel model,
        LayoutCheck layouts, TypeFacts facts)
    {
        var ct = context.CancellationToken;
        string key = LayoutCheck.Key(type);
        if (layouts.SectionOf(type, key, ct) is not { } old)
            return;
        var now = LayoutEmitter.Entry(model, key);
        var findings = LayoutRules.Compare(old, now, LayoutCheck.IdWritten(type, now),
            (a, b) => layouts.SameType(a, b, ct), layouts.RawSection);
        if (findings.Count == 0)
            return;
        string section = LayoutBaseline.Section(now);
        var baseline = new[] { layouts.BaselineLocation(ct) };
        var members = findings.Any(f => f.Member != null) ? facts.Members.Of(type, type.GetNinoTypeOptions()!) : null;
        foreach (var finding in findings)
        {
            var member = finding.Member == null
                ? null
                : members!.LastOrDefault(m => m.Name == finding.Member && m.Locations.Any(l => l.IsInSource));
            var location = (member ?? type).Locations.FirstOrDefault(l => l.IsInSource) ?? type.Locations[0];
            context.ReportDiagnostic(Diagnostic.Create(Descriptor(finding.Severity), location, baseline,
                LayoutCheck.Properties(key, section, old.Key, Fixable(finding, type, member, ct), finding.Argument),
                member == null ? type.ToDisplayString() : $"{type.ToDisplayString()}.{member.Name}", finding.Change,
                finding.Fix));
        }
    }

    /// <summary>
    /// The fix of <paramref name="finding"/> where its code fix can apply it: an attribute goes on a field or property
    /// declared in source, and a member moves to the end only within a type declared once that orders its members
    /// by declaration (autoCollect), where the end of the declaration is the end of the layout. Else only accepting.
    /// </summary>
    private static LayoutFix Fixable(LayoutFinding finding, INamedTypeSymbol type, ISymbol? member,
        System.Threading.CancellationToken ct)
    {
        if (finding.Member == null)
            return finding.FixKind;
        var syntax = member?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
        bool declared = syntax is PropertyDeclarationSyntax ||
                        syntax is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1 } };
        return finding.FixKind switch
        {
            LayoutFix.FormerName when declared => LayoutFix.FormerName,
            LayoutFix.MoveToEnd when declared && type.GetNinoTypeOptions()?.AutoCollect == true &&
                                     type.DeclaringSyntaxReferences.Length == 1 &&
                                     SymbolEqualityComparer.Default.Equals(member!.ContainingType, type) =>
                LayoutFix.MoveToEnd,
            _ => LayoutFix.Accept,
        };
    }

    private static DiagnosticDescriptor Descriptor(LayoutSeverity severity) => severity switch
    {
        LayoutSeverity.Breaking => NinoDiagnostics.LayoutBreakingChange,
        LayoutSeverity.SilentLoss => NinoDiagnostics.LayoutSilentLoss,
        _ => NinoDiagnostics.LayoutChangeNote,
    };

    /// <summary>
    /// NINO037 for a baseline that cannot be read, at the line that cannot; else NINO034 for each section whose type
    /// the code no longer has (not renamed or moved: <see cref="LayoutCheck.Removed"/>), at its table.
    /// </summary>
    private static void ReportBaseline(SyntaxNodeAnalysisContext context, LayoutCheck layouts)
    {
        var ct = context.CancellationToken;
        if (layouts.Error is { } error)
        {
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.LayoutBaselineUnreadable,
                layouts.LocationOf(error.Line, ct), error.Message, error.Line));
            return;
        }

        var baseline = new[] { layouts.BaselineLocation(ct) };
        foreach (var old in layouts.Removed(ct))
        {
            var finding = LayoutRules.Removed(old);
            context.ReportDiagnostic(Diagnostic.Create(NinoDiagnostics.LayoutBreakingChange,
                layouts.LocationOf(old.Line, ct), baseline,
                LayoutCheck.Properties(old.Key, null, null, LayoutFix.Accept, null), old.Key, finding.Change,
                finding.Fix));
        }
    }

    /// <summary>
    /// NINO033: <c>[NinoUtf8]</c>, <c>[NinoMemberFormatter]</c> or <c>[NinoIgnore]</c> on a member of an interface.
    /// Types implementing it implicitly write their own member with its own attributes (the generator's
    /// <see cref="MemberCollector"/> takes the interface member's <c>[NinoMember]</c> index only), so each is reported,
    /// once, where it is written; not when a type of the compilation implements it explicitly (or by its default
    /// implementation), which writes the interface's member with these attributes. A member with a finding of its own
    /// is left to it, as in <see cref="ReportUtf8"/>, except that its <c>[NinoUtf8]</c> is never serialized: the
    /// interface's members are not what implicit implementations write.
    /// </summary>
    private static void ReportInterfaceAttributes(DiagnosticSink sink, INamedTypeSymbol type, TypeFacts facts,
        IReadOnlyList<MemberFinding> findings)
    {
        var known = facts.Symbols;
        foreach (var member in type.GetMembers())
        {
            if (member is not (IFieldSymbol or IPropertySymbol) || member.IsImplicitlyDeclared ||
                findings.Any(f => f.Kind != MemberFindingKind.Utf8NotSerialized &&
                                  SymbolEqualityComparer.Default.Equals(f.Member, member)))
                continue;
            foreach (var attribute in member.GetAttributes())
            {
                var name = attribute.AttributeClass;
                if (!(SymbolEqualityComparer.Default.Equals(name, known.Utf8) ||
                      SymbolEqualityComparer.Default.Equals(name, known.CustomFormatter) ||
                      SymbolEqualityComparer.Default.Equals(name, known.Ignore)) ||
                    facts.ImplementsAsInterface(member))
                    continue;
                sink.Report(NinoDiagnostics.InterfaceMemberAttribute, attribute, member,
                    name!.Name.Substring(0, name.Name.Length - "Attribute".Length), member.Name,
                    type.ToDisplayString());
            }
        }
    }

    /// <summary>
    /// The UTF-8 attributes of the members <paramref name="type"/> declares: NINO031 for a
    /// <c>[NinoMemberFormatter(typeof(Utf8StringFormatter))]</c> (read as <c>[NinoUtf8]</c>), NINO030 for a
    /// <c>[NinoUtf8]</c> on a serialized member that changes nothing (one on a member never serialized is a
    /// <see cref="MemberFindingKind.Utf8NotSerialized"/> finding). Reported once, by the declaring type, abstract or not;
    /// an interface's are NINO033 (<see cref="ReportInterfaceAttributes"/>).
    /// </summary>
    private static void ReportUtf8(DiagnosticSink sink, INamedTypeSymbol type, NinoTypeOptions attr, TypeFacts facts,
        IReadOnlyList<MemberFinding> findings)
    {
        var known = facts.Symbols;
        HashSet<ISymbol>? serialized = null;
        foreach (var member in type.GetMembers())
        {
            if (member is not (IFieldSymbol or IPropertySymbol) || member.IsImplicitlyDeclared)
                continue;
            if (NinoSymbols.Get(member, known.CustomFormatter) is { } formatter && known.IsUtf8Formatter(member, formatter))
            {
                sink.Report(NinoDiagnostics.Utf8FormatterByHand, formatter, member, member.Name, type.ToDisplayString());
                continue;
            }

            // A member with a finding of its own is left to it: fixing it may change what the attribute does.
            if (NinoSymbols.Get(member, known.Utf8) is not { } utf8 ||
                findings.Any(f => SymbolEqualityComparer.Default.Equals(f.Member, member)))
                continue;
            serialized ??= new HashSet<ISymbol>(facts.Members.Of(type, attr), SymbolEqualityComparer.Default);
            if (serialized.Contains(member) && Utf8WithoutEffect(member, facts) is { } reason)
                sink.Report(NinoDiagnostics.Utf8WithoutEffect, utf8, member, member.Name, type.ToDisplayString(),
                    reason);
        }
    }

    /// <summary>
    /// Why <c>[NinoUtf8]</c> changes nothing on a serialized <paramref name="member"/> of a class or struct: a
    /// <c>[NinoMemberFormatter]</c> writes it, or the formatter Nino resolves for its type is the same with and without it (the generator's
    /// <see cref="FormatterResolver"/> decides). Null when it takes effect, or when the type has no formatter (NINO007,
    /// NINO029).
    /// </summary>
    private static string? Utf8WithoutEffect(ISymbol member, TypeFacts facts)
    {
        if (facts.Symbols.HasCustomFormatter(member))
            return "its [NinoMemberFormatter] writes it";
        var type = MemberCollector.MemberType(member)!;
        if (facts.Resolver.Resolve(type, true) is not { } utf8 || facts.Resolver.Resolve(type, false) is not { } plain ||
            utf8.Encoding != plain.Encoding)
            return null;
        return WritesItsOwnStrings(type)
            ? $"it does not reach the strings a [NinoType], object or type parameter in its type '{type.ToDisplayString()}' writes"
            : $"its type '{type.ToDisplayString()}' holds no string";
    }

    /// <summary>
    /// Whether <paramref name="type"/> is or holds a value whose own formatter writes its strings: a
    /// <c>[NinoType]</c>, <c>object</c> or a type parameter.
    /// </summary>
    private static bool WritesItsOwnStrings(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol or { SpecialType: SpecialType.System_Object } => true,
        _ when type.IsNinoType() => true,
        IArrayTypeSymbol array => WritesItsOwnStrings(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(WritesItsOwnStrings),
        _ => false,
    };

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
    private static void Report(DiagnosticSink sink, INamedTypeSymbol type, MemberFinding finding, NinoSymbols known,
        ref int duplicates)
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
            // An interface's [NinoUtf8] is NINO033's, whatever the interface collects: implementations write theirs.
            case MemberFindingKind.Utf8NotSerialized when type.TypeKind != TypeKind.Interface:
                sink.Report(NinoDiagnostics.Utf8WithoutEffect, NinoSymbols.Get(member, known.Utf8)!, member, member.Name,
                    type.ToDisplayString(), finding.Reason!);
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
