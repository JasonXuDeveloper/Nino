using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Nino.Generator.Generation.TypeFacts;

namespace Nino.Generator.Generation;

/// <summary>Finds the serialized members of a <c>[NinoType]</c>, in wire order.</summary>
internal static class MemberCollector
{
    private sealed class Candidate
    {
        public ISymbol Symbol = null!;
        public string Name = null!;
        public int Index;
    }

    public static List<ISymbol> Collect(INamedTypeSymbol type, NinoTypeAttribute attr)
    {
        bool autoCollect = attr.autoCollect;
        bool containNonPublic = attr.containNonPublicMembers;

        var explicitOrder = GetAttribute(type, "NinoExplicitOrderAttribute");
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
            var positional = PositionalParameters(level);
            var candidates = new List<Candidate>();
            int declarationIndex = 0;
            foreach (var member in level.GetMembers())
            {
                if (!IsCandidate(member, containNonPublic, positional))
                    continue;

                var attrs = member.GetAttributes();
                if (attrs.Any(a => a.AttributeClass?.Name == "NinoPrivateProxyAttribute"))
                    continue;
                if (autoCollect && attrs.Any(a => a.AttributeClass?.Name == "NinoIgnoreAttribute"))
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
                    var ninoMember = attrs.FirstOrDefault(a => a.AttributeClass?.Name == "NinoMemberAttribute");
                    if (ninoMember == null || ninoMember.ConstructorArguments.Length == 0 ||
                        ninoMember.ConstructorArguments[0].Value == null)
                        continue;
                    index = Convert.ToInt32(ninoMember.ConstructorArguments[0].Value);
                }

                candidates.Add(new Candidate { Symbol = member, Name = member.Name, Index = index });
            }

            var sorted = candidates
                .OrderBy(c => order.Count > 0 && order.TryGetValue(c.Name, out var o) ? o : int.MaxValue)
                .ThenBy(c => c.Index)
                .ThenBy(c => c.Name, StringComparer.Ordinal);

            foreach (var c in sorted)
            {
                if (!inserted.Add(c.Name))
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

    private static HashSet<string> PositionalParameters(INamedTypeSymbol type)
    {
        var result = new HashSet<string>();
        if (!type.IsRecord)
            return result;
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is RecordDeclarationSyntax { ParameterList: { } list })
            {
                foreach (var p in list.Parameters)
                    result.Add(p.Identifier.ValueText);
            }
        }

        return result;
    }

    private static bool IsCandidate(ISymbol member, bool containNonPublic, HashSet<string> positional)
    {
        if (member.IsStatic)
            return false;
        bool isPublic = member.DeclaredAccessibility == Accessibility.Public;
        switch (member)
        {
            case IFieldSymbol field:
                return !field.IsImplicitlyDeclared && !field.IsConst && !field.IsFixedSizeBuffer &&
                       (containNonPublic || isPublic);
            case IPropertySymbol property:
                if (property.IsIndexer || property.GetMethod == null || property.SetMethod == null)
                    return false;
                if (property.IsImplicitlyDeclared && !positional.Contains(property.Name))
                    return false;
                return containNonPublic || isPublic;
            default:
                return false;
        }
    }
}
