using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoType
{
    public ITypeSymbol TypeSymbol { get; }
    public ImmutableList<NinoMember> Members { get; set; }
    public ImmutableList<NinoType> Parents { get; set; }
    public string CustomSerializer { get; set; }
    public string CustomDeserializer { get; set; }

    public NinoType(Compilation compilation, ITypeSymbol typeSymbol, ImmutableList<NinoMember>? members,
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

        CustomSerializer = "";
        CustomDeserializer = "";

        var declaredTypeAssembly = typeSymbol.ContainingAssembly;
        bool isSameAssembly = declaredTypeAssembly.Equals(compilation.Assembly,
            SymbolEqualityComparer.Default);
        if (!isSameAssembly)
        {
            // check if the referenced assembly has nino generated code (NinoGen.Serializer)
            var ninoGen =
                declaredTypeAssembly.GetTypeByMetadataName(
                    $"{declaredTypeAssembly.Name.GetNamespace()}.Serializer");
            if (ninoGen != null)
            {
                CustomSerializer = ninoGen.GetDisplayString();
            }

            ninoGen =
                declaredTypeAssembly.GetTypeByMetadataName(
                    $"{declaredTypeAssembly.Name.GetNamespace()}.Deserializer");
            if (ninoGen != null)
            {
                CustomDeserializer = ninoGen.GetDisplayString();
            }
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

            // one group can contain at most 16 members
            if (unmanagedGroup.Count >= 16)
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
            return TypeSymbol.IsPolyMorphicType();
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
        sb.AppendLine($"Type: {TypeSymbol.GetDisplayString()}");

        if (!string.IsNullOrEmpty(CustomSerializer))
        {
            sb.AppendLine($"CustomSerializer: {CustomSerializer}");
        }

        if (!string.IsNullOrEmpty(CustomDeserializer))
        {
            sb.AppendLine($"CustomDeserializer: {CustomDeserializer}");
        }

        sb.AppendLine("Parents:");
        foreach (var parent in Parents)
        {
            sb.AppendLine($"\t{parent.TypeSymbol.GetDisplayString()}");
        }

        sb.AppendLine("Members:");
        foreach (var member in Members)
        {
            sb.AppendLine($"\t{member}");
        }

        return sb.ToString();
    }
}