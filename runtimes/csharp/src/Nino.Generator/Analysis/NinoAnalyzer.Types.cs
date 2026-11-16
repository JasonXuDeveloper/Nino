using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;
using Nino.Generator.Common;
using Nino.Generator.Generation.Emit;

namespace Nino.Generator.Analysis;

public sealed partial class NinoAnalyzer
{
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
        ReportDuplicateCallbacks(sink, type, facts.Symbols);
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
        // Most types are as their section says: that is found without building and comparing their layout.
        if (old.Key == key && LayoutEmitter.Matches(model, old))
            return;
        var now = LayoutEmitter.Entry(model, key);
        var findings = LayoutRules.Compare(old, now, LayoutCheck.IdWritten(type, now),
            (a, b) => layouts.SameType(a, b, ct), layouts.RawSection);
        if (findings.Count == 0)
            return;
        string section = LayoutBaseline.Section(now);
        var baseline = new[] { layouts.BaselineLocation() };
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

        var baseline = new[] { layouts.BaselineLocation() };
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
}
