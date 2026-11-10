using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>Why a member's annotations are wrong or pointless; the analyzer reports each as a diagnostic.</summary>
internal enum MemberFindingKind
{
    /// <summary>NINO008: the <c>[NinoMemberFormatter]</c> cannot format the member's type.</summary>
    InvalidCustomFormatter,

    /// <summary>NINO009: <c>[NinoMember]</c> on a non-public member of a type without includeNonPublicMembers.</summary>
    NonPublicMember,

    /// <summary>NINO010: <c>[NinoMember]</c> on a member that is collected automatically.</summary>
    RedundantMember,

    /// <summary>NINO011: <c>[NinoIgnore]</c> on a member that is never collected (members are opted in).</summary>
    RedundantIgnore,

    /// <summary>NINO012: both <c>[NinoMember]</c> and <c>[NinoIgnore]</c>.</summary>
    AmbiguousMember,

    /// <summary>NINO013: the member's index is already used by <see cref="MemberFinding.Other"/>.</summary>
    DuplicateIndex,

    /// <summary>
    /// NINO028: <c>[NinoMember]</c> on a member that is never serialized, whatever the type's options, for
    /// <see cref="MemberFinding.Reason"/>.
    /// </summary>
    NotSerializable,

    /// <summary>
    /// NINO030: <c>[NinoUtf8]</c> on a member the type never serializes, for <see cref="MemberFinding.Reason"/>; only
    /// when no other finding is about the member (fixing that one may make it serialized).
    /// </summary>
    Utf8NotSerialized,
}

/// <summary>
/// A finding about a member declared by the collected type. <see cref="Formatter"/> is set for
/// <see cref="MemberFindingKind.InvalidCustomFormatter"/>, <see cref="Other"/> for
/// <see cref="MemberFindingKind.DuplicateIndex"/>, <see cref="Reason"/> for <see cref="MemberFindingKind.NotSerializable"/>
/// and <see cref="MemberFindingKind.Utf8NotSerialized"/>.
/// </summary>
internal readonly record struct MemberFinding(
    MemberFindingKind Kind,
    ISymbol Member,
    INamedTypeSymbol? Formatter = null,
    ISymbol? Other = null,
    string? Reason = null);

/// <summary>
/// The serialized members of a type, root level first. A level is the type itself or one of its [NinoType] base
/// classes, led by the members of the [NinoType] interfaces it implements first.
/// </summary>
internal sealed class MemberList : List<ISymbol>
{
}

/// <summary>
/// Finds the serialized members of a <c>[NinoType]</c>, in wire order, and on request what is wrong with the
/// annotations of the members it declares, in the same pass and by the same rules. Use <see cref="MemberCache"/>,
/// which memoizes both per compilation.
/// </summary>
internal static class MemberCollector
{
    /// <param name="findings">
    /// Receives the findings about the members <paramref name="type"/> declares (base types have their own); null
    /// when only the members are needed, which skips the checks only diagnostics need.
    /// </param>
    public static MemberList Collect(INamedTypeSymbol type, NinoTypeOptions attr, NinoSymbols known,
        List<MemberFinding>? findings = null)
    {
        bool autoCollect = attr.AutoCollect;
        bool containNonPublic = attr.IncludeNonPublicMembers;

        List<INamedTypeSymbol>? levels = null;
        HashSet<INamedTypeSymbol>? visited = null;

        void AddAncestor(INamedTypeSymbol ancestor)
        {
            visited ??= new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { type };
            if (!visited.Add(ancestor))
                return;
            // Visit base types before their derived types to preserve their wire positions.
            AddAncestors(ancestor);
            (levels ??= new()).Add(ancestor);
        }

        void AddAncestors(INamedTypeSymbol current)
        {
            if (current.BaseType is { } parent && parent.IsNinoType())
                AddAncestor(parent);
            foreach (var i in current.AllInterfaces)
            {
                if (i.IsNinoType())
                    AddAncestor(i);
            }
        }

        AddAncestors(type);

        // Get-only auto-properties can only be restored through a constructor: they are members when one takes them.
        HashSet<string>? constructorNames = null;
        bool scannedConstructors = false;

        // The indices of the members the type declares, to find duplicates.
        var indices = findings != null && !autoCollect ? new Dictionary<int, ISymbol>() : null;
        HashSet<int>? duplicated = null;

        var inserted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var names = new HashSet<string>();
        var result = new MemberList();
        if (levels != null)
        {
            foreach (var level in levels)
                CollectLevel(level);
        }
        CollectLevel(type);
        return result;

        void CollectLevel(INamedTypeSymbol level)
        {
            // Findings cover every member the type declares, serialized or not.
            bool inspect = findings != null && SymbolEqualityComparer.Default.Equals(level, type);
            List<(ISymbol Symbol, int Index)>? candidates = null;
            bool fromInterface = level.TypeKind == TypeKind.Interface;
            foreach (var member in level.GetMembers())
            {
                if (!scannedConstructors && member is IPropertySymbol { SetMethod: null })
                {
                    constructorNames = ConstructorParameterNames(type, known);
                    scannedConstructors = true;
                }

                bool candidate = IsCandidate(member, containNonPublic, constructorNames);
                if (!candidate && !inspect)
                    continue;

                AttributeData? ninoMember = null, customFormatter = null;
                bool ignored = false, utf8 = false;
                foreach (var a in member.GetAttributes())
                {
                    var attributeClass = a.AttributeClass;
                    if (attributeClass == null)
                        continue;
                    if (SymbolEqualityComparer.Default.Equals(attributeClass, known.Member))
                        ninoMember ??= a;
                    else if (SymbolEqualityComparer.Default.Equals(attributeClass, known.Ignore))
                        ignored = true;
                    else if (inspect && SymbolEqualityComparer.Default.Equals(attributeClass, known.CustomFormatter))
                        customFormatter = a;
                    else if (inspect && SymbolEqualityComparer.Default.Equals(attributeClass, known.Utf8))
                        utf8 = true;
                }

                // The [NinoMember] index; null without one.
                int? memberIndex = ninoMember is { ConstructorArguments: { Length: > 0 } args } &&
                                   args[0].Value is { } value
                    ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                    : null;

                if (inspect)
                {
                    int found = findings!.Count;
                    Inspect(member, attr, known, ninoMember != null, ignored, memberIndex, customFormatter,
                        constructorNames, findings, indices, ref duplicated);
                    if (utf8 && findings.Count == found && member is IFieldSymbol or IPropertySymbol &&
                        NotCollected(member, attr, candidate, ignored, memberIndex, constructorNames) is { } why)
                        findings.Add(new MemberFinding(MemberFindingKind.Utf8NotSerialized, member, Reason: why));
                }

                if (!candidate || (autoCollect && ignored))
                    continue;

                // Private members of a base type cannot be reached from the derived type (protected can).
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type) &&
                    member.DeclaredAccessibility is Accessibility.Private)
                    continue;

                if (autoCollect)
                    AddCandidate(member, fromInterface);
                else if (memberIndex is { } index)
                    (candidates ??= new()).Add((member, index));
            }

            if (candidates != null)
            {
                foreach (var candidate in candidates.OrderBy(static c => c.Index)
                             .ThenBy(static c => c.Symbol.Name, StringComparer.Ordinal))
                    AddCandidate(candidate.Symbol, fromInterface);
            }
        }

        void AddCandidate(ISymbol member, bool fromInterface)
        {
            // Interface members are serialized through the implementing type's member of the same name, once
            // whatever the number of interfaces declaring it.
            var symbol = fromInterface ? FindImplementation(type, member) : member;
            // An override is the member it overrides, already collected with its base. A member hiding a base
            // one (`new`) is another member: both are serialized, the base one first.
            if ((fromInterface && names.Contains(symbol.Name)) || Overrides(symbol, inserted) || !inserted.Add(symbol))
                return;
            names.Add(symbol.Name);
            result.Add(symbol);
        }
    }

    /// <summary>
    /// The member a field stores, among those <paramref name="collected"/> accepts: the field, or the property it
    /// backs; for a property implementing interface members explicitly, the one <see cref="Collect"/> collected in its
    /// place. Null when it stores none of them.
    /// </summary>
    public static ISymbol? StoredMember(IFieldSymbol field, Func<ISymbol, bool> collected)
    {
        var member = field.AssociatedMember() ?? field;
        if (collected(member))
            return member;
        return member is IPropertySymbol property
            ? Enumerable.FirstOrDefault(property.ExplicitInterfaceImplementations, collected)
            : null;
    }

    /// <summary>The type of a field or property; null for other members.</summary>
    public static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    // The findings about one member the type declares, in the order the analyzer reports them.
    private static void Inspect(ISymbol member, NinoTypeOptions attr, NinoSymbols known, bool annotated, bool ignored,
        int? index, AttributeData? customFormatter, HashSet<string>? constructorNames, List<MemberFinding> findings,
        Dictionary<int, ISymbol>? indices, ref HashSet<int>? duplicated)
    {
        // A member no option makes serializable: this is the only finding about its [NinoMember], whose other
        // findings (accessibility, index) would only lead to a change that still serializes nothing.
        if (annotated && NotSerializable(member, constructorNames) is { } reason)
        {
            findings.Add(new MemberFinding(MemberFindingKind.NotSerializable, member, Reason: reason));
            return;
        }

        if (customFormatter is { ConstructorArguments: { Length: > 0 } args } &&
            args[0].Value is INamedTypeSymbol formatter && MemberType(member) is { } memberType &&
            !known.IsCustomFormatterOf(formatter, memberType))
            findings.Add(new MemberFinding(MemberFindingKind.InvalidCustomFormatter, member, formatter));

        // Non-public members are only collected with includeNonPublicMembers: [NinoMember] cannot opt one in.
        bool dropped = annotated && member.DeclaredAccessibility != Accessibility.Public &&
                       !attr.IncludeNonPublicMembers;
        if (dropped)
            findings.Add(new MemberFinding(MemberFindingKind.NonPublicMember, member));
        if (attr.AutoCollect)
        {
            if (annotated && !dropped)
                findings.Add(new MemberFinding(MemberFindingKind.RedundantMember, member));
            return;
        }

        if (annotated && ignored)
            findings.Add(new MemberFinding(MemberFindingKind.AmbiguousMember, member));
        else if (ignored)
            findings.Add(new MemberFinding(MemberFindingKind.RedundantIgnore, member));

        // Each duplicated index is reported once, on the member that repeats it first.
        if (index is { } i)
        {
            if (!indices!.TryGetValue(i, out var existing))
                indices[i] = member;
            else if ((duplicated ??= new HashSet<int>()).Add(i))
                findings.Add(new MemberFinding(MemberFindingKind.DuplicateIndex, member, Other: existing));
        }
    }

    /// <summary>
    /// Why a member the type declares is not collected, by the same tests as <see cref="Collect"/>; null when it is.
    /// </summary>
    private static string? NotCollected(ISymbol member, NinoTypeOptions attr, bool candidate, bool ignored, int? index,
        HashSet<string>? constructorNames)
    {
        if (!candidate)
            return NotSerializable(member, constructorNames) ??
                   "it is not public, and the type does not set includeNonPublicMembers";
        if (attr.AutoCollect)
            return ignored ? "it is [NinoIgnore]" : null;
        return index == null ? "it has no [NinoMember], and the type collects only those (autoCollect: false)" : null;
    }

    /// <summary>
    /// The member of a type that implements an interface member: the type's own of the same name (an implicit
    /// implementation, maybe in a base class); without one (an explicit or default implementation), the interface
    /// member itself, with its attributes, which generated code reaches through the interface
    /// (<see cref="MemberAccess.Cast"/>, <see cref="MemberAccess.Interface"/>).
    /// </summary>
    public static ISymbol FindImplementation(INamedTypeSymbol type, ISymbol member)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var found = t.GetMembers(member.Name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol);
            if (found != null)
                return found;
        }

        return member;
    }

    private static bool Overrides(ISymbol member, HashSet<ISymbol> collected) =>
        NinoSymbols.OverriddenProperties(member).Any(collected.Contains);

    /// <summary>
    /// The parameter names (any case) of the constructors and <c>[NinoConstructor]</c> factories of
    /// <paramref name="type"/> that generated code may call, and the member names a <c>[NinoConstructor]</c> lists.
    /// </summary>
    private static HashSet<string>? ConstructorParameterNames(INamedTypeSymbol type, NinoSymbols known)
    {
        HashSet<string>? names = null;
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method ||
                !(method.MethodKind == MethodKind.Constructor && !method.IsStatic ||
                  method.IsStatic && method.MethodKind == MethodKind.Ordinary))
                continue;
            var marked = NinoSymbols.Get(method, known.Constructor);
            if (method.IsStatic && marked == null)
                continue;
            if (marked != null && NinoSymbols.ConstructorNames(marked) is { } listed)
                (names ??= new(StringComparer.OrdinalIgnoreCase)).UnionWith(listed);
            else if (method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                     or Accessibility.ProtectedOrInternal)
            {
                foreach (var parameter in method.Parameters)
                    (names ??= new(StringComparer.OrdinalIgnoreCase)).Add(parameter.Name);
            }
        }

        return names;
    }

    private static bool IsCandidate(ISymbol member, bool containNonPublic, HashSet<string>? constructorNames) =>
        member is IFieldSymbol or IPropertySymbol &&
        (containNonPublic || member.DeclaredAccessibility == Accessibility.Public) &&
        NotSerializable(member, constructorNames) == null;

    /// <summary>
    /// Why a field or property is never serialized, whatever its accessibility and the type's options (the end of
    /// the sentence "[NinoMember] has no effect: ..."); null when it can be.
    /// </summary>
    private static string? NotSerializable(ISymbol member, HashSet<string>? constructorNames)
    {
        if (member.IsStatic)
            return member is IFieldSymbol { IsConst: true } ? "it is a constant" : "it is static";
        return member switch
        {
            // Backing fields: their properties are the members (from metadata, only the name tells them apart).
            IFieldSymbol { IsFixedSizeBuffer: true } => "it is a fixed-size buffer",
            IFieldSymbol field when field.IsImplicitlyDeclared || field.AssociatedMember() != null =>
                "it is the backing field of a property or event; mark the property instead",
            // Explicit interface implementations have no name generated code could use on the type: the interface's
            // member is collected in their place when the interface is a [NinoType] (FindImplementation). The only
            // implicit properties with a setter are a record's positional ones (EqualityContract has none).
            IPropertySymbol { IsIndexer: true } => "it is an indexer",
            IPropertySymbol { GetMethod: null } => "it has no getter",
            IPropertySymbol { ExplicitInterfaceImplementations.IsEmpty: false } =>
                "it implements an interface member explicitly (a [NinoType] interface's member is written in its place, with the interface's attributes)",
            IPropertySymbol { SetMethod: null } property when
                !(constructorNames?.Contains(property.Name) == true && IsGetOnlyAutoProperty(property)) =>
                "it has no setter, and no constructor takes it",
            _ => null,
        };
    }

    /// <summary>A get-only property backed by a compiler-generated field (<c>public int Id { get; }</c>).</summary>
    private static bool IsGetOnlyAutoProperty(IPropertySymbol property)
    {
        if (property.SetMethod != null || property.IsImplicitlyDeclared || property.IsAbstract)
            return false;
        foreach (var member in property.ContainingType.GetMembers())
        {
            if (member is IFieldSymbol field && SymbolEqualityComparer.Default.Equals(field.AssociatedMember(), property))
                return true;
        }

        return false;
    }
}
