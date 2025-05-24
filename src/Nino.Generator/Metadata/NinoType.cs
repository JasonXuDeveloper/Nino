using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoType
{
    public ITypeSymbol TypeSymbol { get; }
    public ImmutableList<NinoMember> Members { get; set; }
    public ImmutableList<NinoType> Parents { get; set; }

    public NinoType(ITypeSymbol typeSymbol, ImmutableList<NinoMember>? members,
        ImmutableList<NinoType>? parents)
    {
        TypeSymbol = typeSymbol;
        Members = members ?? ImmutableList<NinoMember>.Empty;
        Parents = parents ?? ImmutableList<NinoType>.Empty;

        if (TypeSymbol.TypeKind == TypeKind.Dynamic)
        {
            Parents = ImmutableList<NinoType>.Empty;
        }

        if (Members.Count == 0)
        {
            Members = ImmutableList<NinoMember>.Empty;
        }
    }
    
    public IEnumerable<List<NinoMember>> GroupByPrimitivity()
    {
        List<NinoMember> unmanagedGroup = new();
        foreach (var member in Members)
        {
            if (member.Type.IsUnmanagedType)
            {
                unmanagedGroup.Add(member);
            }
            else
            {
                // If any unmanaged members were accumulated, yield them first.
                if (unmanagedGroup.Count > 0)
                {
                    yield return unmanagedGroup;
                    unmanagedGroup = new List<NinoMember>();
                }
                // Yield the managed member as its own group.
                yield return new List<NinoMember> { member };
            }
            
            // one group can contain at most 8 members
            if (unmanagedGroup.Count >= 8)
            {
                yield return unmanagedGroup;
                unmanagedGroup = new List<NinoMember>();
            }
        }

        // Yield any remaining unmanaged members.
        if (unmanagedGroup.Count > 0)
        {
            yield return unmanagedGroup;
        }
    }

    public bool IsPolymorphic()
    {
        if (Parents.IsEmpty)
        {
            return TypeSymbol.IsReferenceType || TypeSymbol is { IsRecord: true, IsValueType: false } ||
                   TypeSymbol.TypeKind == TypeKind.Interface;
        }

        return true;
    }

    public void AddParent(NinoType? parent)
    {
        if (parent == null || parent == this || TypeSymbol.TypeKind == TypeKind.Dynamic)
        {
            return;
        }

        // Check if this parent is already in the Parents collection
        if (Parents.Any(p => SymbolEqualityComparer.Default.Equals(p.TypeSymbol, parent.TypeSymbol)))
        {
            return;
        }

        Parents = Parents.Add(parent);
    }

    public void AddMember(NinoMember member)
    {
        Members = Members.Add(member);
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"Type: {TypeSymbol.ToDisplayString()}");
        sb.AppendLine("Parents:");
        foreach (var parent in Parents)
        {
            sb.AppendLine($"\t{parent.TypeSymbol.ToDisplayString()}");
        }

        sb.AppendLine("Members:");
        foreach (var member in Members)
        {
            sb.AppendLine($"\t{member}");
        }

        return sb.ToString();
    }
}