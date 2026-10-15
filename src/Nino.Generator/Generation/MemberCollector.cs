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
    /// <summary>NINO008: the <c>[NinoCustomFormatter]</c> cannot format the member's type.</summary>
    InvalidCustomFormatter,

    /// <summary>NINO009: <c>[NinoMember]</c> on a non-public member of a type without containNonPublicMembers.</summary>
    NonPublicMember,

    /// <summary>NINO010: <c>[NinoMember]</c> on a member that is collected automatically.</summary>
    RedundantMember,

    /// <summary>NINO011: <c>[NinoIgnore]</c> on a member that is never collected (members are opted in).</summary>
    RedundantIgnore,

    /// <summary>NINO012: both <c>[NinoMember]</c> and <c>[NinoIgnore]</c>.</summary>
    AmbiguousMember,

    /// <summary>NINO013: the member's index is already used by <see cref="MemberFinding.Other"/>.</summary>
    DuplicateIndex,
}

/// <summary>
/// A finding about a member declared by the collected type. <see cref="Formatter"/> is set for
/// <see cref="MemberFindingKind.InvalidCustomFormatter"/>, <see cref="Other"/> and <see cref="Index"/> for
/// <see cref="MemberFindingKind.DuplicateIndex"/>.
/// </summary>
internal readonly record struct MemberFinding(
    MemberFindingKind Kind,
    ISymbol Member,
    INamedTypeSymbol? Formatter = null,
    ISymbol? Other = null,
    int Index = 0);

/// <summary>
/// Finds the serialized members of a <c>[NinoType]</c>, in wire order, and on request what is wrong with the
/// annotations of the members it declares, in the same pass and by the same rules. Use <see cref="MemberCache"/>,
/// which memoizes both per compilation.
/// </summary>
/// <summary>
/// The serialized members of a type, root level first. A level is the type itself or one of its [NinoType] base
/// classes, led by the members of the [NinoType] interfaces it implements first; <see cref="LevelEnds"/> holds, per
/// level, the index just past its last member (levels without members included), which version-tolerant bodies use
/// to delimit each level.
/// </summary>
internal sealed class MemberList : List<ISymbol>
{
    public List<int> LevelEnds { get; } = new();
}

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
        bool containNonPublic = attr.ContainNonPublicMembers;

        var explicitOrder = NinoSymbols.Get(type, known.ExplicitOrder);
        var order = new Dictionary<string, int>();
        if (explicitOrder != null && explicitOrder.ConstructorArguments.Length > 0)
        {
            var values = explicitOrder.ConstructorArguments[0].Values;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].Value is string s)
                    order[s] = i;
            }
        }

        var levels = new List<INamedTypeSymbol>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        void AddLevel(INamedTypeSymbol t)
        {
            if (!visited.Add(t))
                return;
            // Base types first (root to leaf), so members appended to a derived type never shift base members.
            if (t.BaseType != null && t.BaseType.IsNinoType())
                AddLevel(t.BaseType);
            foreach (var i in t.AllInterfaces)
            {
                if (i.IsNinoType())
                    AddLevel(i);
            }

            levels.Add(t);
        }

        AddLevel(type);

        // Get-only auto-properties can only be restored through a constructor: they are members when one takes them.
        var constructorNames = ConstructorParameterNames(type, known);

        // The indices of the members the type declares, to find duplicates.
        var indices = findings != null && !autoCollect ? new Dictionary<int, ISymbol>() : null;
        HashSet<int>? duplicated = null;

        var inserted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var names = new HashSet<string>();
        var result = new MemberList();
        foreach (var level in levels)
        {
            // Findings cover every member the type declares, serialized or not.
            bool inspect = findings != null && SymbolEqualityComparer.Default.Equals(level, type);
            var candidates = new List<(ISymbol Symbol, int Index)>();
            int declarationIndex = 0;
            foreach (var member in level.GetMembers())
            {
                bool candidate = IsCandidate(member, containNonPublic, constructorNames);
                if (!candidate && !inspect)
                    continue;

                AttributeData? ninoMember = null, customFormatter = null;
                bool ignored = false;
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
                }

                // The [NinoMember] index; null without one.
                int? memberIndex = ninoMember is { ConstructorArguments: { Length: > 0 } args } &&
                                   args[0].Value is { } value
                    ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                    : null;

                if (inspect)
                    Inspect(member, attr, known, ninoMember != null, ignored, memberIndex, customFormatter, findings!,
                        indices, ref duplicated);

                if (!candidate || (autoCollect && ignored))
                    continue;

                // Private members of a base type cannot be reached from the derived type (protected can).
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type) &&
                    member.DeclaredAccessibility is Accessibility.Private)
                    continue;

                int index;
                if (autoCollect)
                    index = declarationIndex++;
                else if (memberIndex is { } i)
                    index = i;
                else
                    continue;

                candidates.Add((member, index));
            }

            var sorted = candidates
                .OrderBy(c => order.Count > 0 && order.TryGetValue(c.Symbol.Name, out var o) ? o : int.MaxValue)
                .ThenBy(c => c.Index)
                .ThenBy(c => c.Symbol.Name, StringComparer.Ordinal);

            foreach (var c in sorted)
            {
                // Interface members are serialized through the implementing type's member of the same name, once
                // whatever the number of interfaces declaring it.
                bool fromInterface = level.TypeKind == TypeKind.Interface;
                var symbol = fromInterface ? FindImplementation(type, c.Symbol) : c.Symbol;
                // An override is the member it overrides, already collected with its base. A member hiding a base
                // one (`new`) is another member: both are serialized, the base one first.
                if ((fromInterface && names.Contains(symbol.Name)) || Overrides(symbol, inserted) ||
                    !inserted.Add(symbol))
                    continue;
                names.Add(symbol.Name);
                result.Add(symbol);
            }

            // Interfaces are not levels of their own: their members open the level of the class implementing them.
            if (level.TypeKind != TypeKind.Interface)
                result.LevelEnds.Add(result.Count);
        }

        return result;
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
        int? index, AttributeData? customFormatter, List<MemberFinding> findings, Dictionary<int, ISymbol>? indices,
        ref HashSet<int>? duplicated)
    {
        if (customFormatter is { ConstructorArguments: { Length: > 0 } args } &&
            args[0].Value is INamedTypeSymbol formatter && MemberType(member) is { } memberType &&
            !known.IsCustomFormatterOf(formatter, memberType))
            findings.Add(new MemberFinding(MemberFindingKind.InvalidCustomFormatter, member, formatter));

        // Non-public members are only collected with containNonPublicMembers: [NinoMember] cannot opt one in.
        bool dropped = annotated && member.DeclaredAccessibility != Accessibility.Public &&
                       !attr.ContainNonPublicMembers;
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
                findings.Add(new MemberFinding(MemberFindingKind.DuplicateIndex, member, Other: existing, Index: i));
        }
    }

    private static ISymbol FindImplementation(INamedTypeSymbol type, ISymbol member)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var found = t.GetMembers(member.Name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol);
            if (found != null)
                return found;
        }

        return member;
    }

    private static bool Overrides(ISymbol member, HashSet<ISymbol> collected)
    {
        if (member is not IPropertySymbol { IsOverride: true } property)
            return false;
        for (var p = property.OverriddenProperty; p != null; p = p.OverriddenProperty)
        {
            if (collected.Contains(p))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The parameter names (any case) of the constructors and <c>[NinoConstructor]</c> factories of
    /// <paramref name="type"/> that generated code may call, and the member names a <c>[NinoConstructor]</c> lists.
    /// </summary>
    private static HashSet<string> ConstructorParameterNames(INamedTypeSymbol type, NinoSymbols known)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method ||
                !(method.MethodKind == MethodKind.Constructor && !method.IsStatic ||
                  method.IsStatic && method.MethodKind == MethodKind.Ordinary))
                continue;
            var marked = NinoSymbols.Get(method, known.Constructor);
            if (method.IsStatic && marked == null)
                continue;
            if (marked is { ConstructorArguments: { Length: > 0 } a } && a[0].Kind == TypedConstantKind.Array &&
                a[0].Values.Length > 0)
            {
                foreach (var v in a[0].Values)
                {
                    if (v.Value is string s)
                        names.Add(s);
                }
            }
            else if (method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                     or Accessibility.ProtectedOrInternal)
            {
                foreach (var parameter in method.Parameters)
                    names.Add(parameter.Name);
            }
        }

        return names;
    }

    private static bool IsCandidate(ISymbol member, bool containNonPublic, HashSet<string> constructorNames)
    {
        if (member.IsStatic || !(containNonPublic || member.DeclaredAccessibility == Accessibility.Public))
            return false;
        return member switch
        {
            // Implicit fields are backing fields: their properties are the members.
            IFieldSymbol field => !field.IsImplicitlyDeclared && !field.IsConst && !field.IsFixedSizeBuffer,
            // Explicit interface implementations belong to the interface, which serializes them if it is a
            // [NinoType]; they have no name generated code could use on the type. The only implicit properties with
            // a setter are a record's positional ones (EqualityContract has none).
            IPropertySymbol property => !property.IsIndexer && property.GetMethod != null &&
                                        property.ExplicitInterfaceImplementations.IsEmpty &&
                                        (property.SetMethod != null ||
                                         IsGetOnlyAutoProperty(property) && constructorNames.Contains(property.Name)),
            _ => false,
        };
    }

    /// <summary>A get-only property backed by a compiler-generated field (<c>public int Id { get; }</c>).</summary>
    private static bool IsGetOnlyAutoProperty(IPropertySymbol property)
    {
        if (property.SetMethod != null || property.IsImplicitlyDeclared || property.IsAbstract)
            return false;
        foreach (var member in property.ContainingType.GetMembers())
        {
            if (member is IFieldSymbol { IsImplicitlyDeclared: true } field &&
                SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property))
                return true;
        }

        return false;
    }
}
