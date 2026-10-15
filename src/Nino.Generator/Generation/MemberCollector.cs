using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Finds the serialized members of a <c>[NinoType]</c>, in wire order. Use <see cref="TypeFacts.Members"/>, which
/// memoizes the result per compilation.
/// </summary>
internal static class MemberCollector
{
    public static List<ISymbol> Collect(INamedTypeSymbol type, NinoTypeOptions attr, NinoSymbols known)
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

        var inserted = new HashSet<string>();
        var result = new List<ISymbol>();
        foreach (var level in levels)
        {
            var candidates = new List<(ISymbol Symbol, int Index)>();
            int declarationIndex = 0;
            foreach (var member in level.GetMembers())
            {
                if (!IsCandidate(member, containNonPublic))
                    continue;

                if (autoCollect && NinoSymbols.Has(member, known.Ignore))
                    continue;

                // Private members of a base type cannot be reached from the derived type (protected can).
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type) &&
                    member.DeclaredAccessibility is Accessibility.Private)
                    continue;

                int index;
                if (autoCollect)
                {
                    index = declarationIndex++;
                }
                else
                {
                    var ninoMember = NinoSymbols.Get(member, known.Member);
                    if (ninoMember == null || ninoMember.ConstructorArguments.Length == 0 ||
                        ninoMember.ConstructorArguments[0].Value == null)
                        continue;
                    index = Convert.ToInt32(ninoMember.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                }

                candidates.Add((member, index));
            }

            var sorted = candidates
                .OrderBy(c => order.Count > 0 && order.TryGetValue(c.Symbol.Name, out var o) ? o : int.MaxValue)
                .ThenBy(c => c.Index)
                .ThenBy(c => c.Symbol.Name, StringComparer.Ordinal);

            foreach (var c in sorted)
            {
                if (!inserted.Add(c.Symbol.Name))
                    continue;
                // Interface members are serialized through the implementing type's member of the same name.
                result.Add(level.TypeKind == TypeKind.Interface ? FindImplementation(type, c.Symbol) : c.Symbol);
            }
        }

        return result;
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

    private static bool IsCandidate(ISymbol member, bool containNonPublic)
    {
        if (member.IsStatic || !(containNonPublic || member.DeclaredAccessibility == Accessibility.Public))
            return false;
        return member switch
        {
            // Implicit fields are backing fields: their properties are the members.
            IFieldSymbol field => !field.IsImplicitlyDeclared && !field.IsConst && !field.IsFixedSizeBuffer,
            // The only implicit properties with a setter are a record's positional ones (EqualityContract has none).
            IPropertySymbol property => !property.IsIndexer && property.GetMethod != null &&
                                        property.SetMethod != null,
            _ => false,
        };
    }
}
