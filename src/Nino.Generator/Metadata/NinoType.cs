using System.Collections.Immutable;
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

    public bool IsPolymorphic()
    {
        if (Parents.IsEmpty)
        {
            return TypeSymbol.IsReferenceType || TypeSymbol is { IsRecord: true, IsValueType: false } ||
                   TypeSymbol.TypeKind == TypeKind.Interface;
        }

        return true;
    }

    public void AddParent(NinoType parent)
    {
        if (parent == this || TypeSymbol.TypeKind == TypeKind.Dynamic)
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