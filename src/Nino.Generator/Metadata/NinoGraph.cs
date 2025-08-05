using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoGraph
{
    public Dictionary<NinoType, List<NinoType>> BaseTypes { get; set; } = new();
    public Dictionary<NinoType, List<NinoType>> SubTypes { get; set; } = new();
    public HashSet<NinoType> TopTypes { get; set; } = new();
    public HashSet<NinoType> CircularTypes { get; set; } = new();
    public Dictionary<string, NinoType> TypeMap { get; set; } = new();

    public NinoGraph(Compilation compilation, List<NinoType> ninoTypes)
    {
        foreach (var ninoType in ninoTypes)
        {
            TypeMap[ninoType.TypeSymbol.GetDisplayString()] = ninoType;

            List<NinoType> baseTypes = new();
            BaseTypes[ninoType] = baseTypes;

            // find base types
            void TraverseBaseTypes(NinoType type)
            {
                if (type.Parents.Count == 0)
                {
                    return;
                }

                foreach (var parent in type.Parents)
                {
                    if (baseTypes.Contains(parent))
                    {
                        continue;
                    }

                    baseTypes.Add(parent);
                    TraverseBaseTypes(parent);
                }
            }

            TraverseBaseTypes(ninoType);

            // if no base types, it's a top type
            if (baseTypes.Count == 0)
            {
                TopTypes.Add(ninoType);
            }
        }

        // add sub types based on base types
        foreach (var kvp in BaseTypes)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            foreach (var baseType in value)
            {
                if (!SubTypes.ContainsKey(baseType))
                {
                    SubTypes.Add(baseType, new());
                }

                if (SubTypes[baseType].Contains(key))
                {
                    continue;
                }

                SubTypes[baseType].Add(key);
            }
        }

        // find circular types, i.e. a member (or member's member) is of the same type, or base type
        void TraverseCircularTypes(NinoType type, NinoType desiredType)
        {
            bool Related(ITypeSymbol typeSymbol)
            {
                switch (typeSymbol)
                {
                    case INamedTypeSymbol namedTypeSymbol:
                        if (namedTypeSymbol.IsGenericType)
                        {
                            return namedTypeSymbol.TypeArguments.Any(Related);
                        }

                        return compilation.HasImplicitConversion(desiredType.TypeSymbol, namedTypeSymbol) ||
                               compilation.HasImplicitConversion(namedTypeSymbol, desiredType.TypeSymbol);

                    case IArrayTypeSymbol arrayTypeSymbol:
                        return Related(arrayTypeSymbol.ElementType);

                    default:
                        return compilation.HasImplicitConversion(desiredType.TypeSymbol, typeSymbol) ||
                               compilation.HasImplicitConversion(typeSymbol, desiredType.TypeSymbol);
                }
            }

            foreach (var member in type.Members)
            {
                if (member.Type.IsUnmanagedType)
                {
                    continue;
                }

                if (Related(member.Type))
                {
                    CircularTypes.Add(desiredType);
                    return;
                }

                //if member.type is a nino type, check if it's a circular type
                foreach (var ninoType in ninoTypes)
                {
                    if (SymbolEqualityComparer.Default.Equals(ninoType.TypeSymbol, member.Type))
                    {
                        TraverseCircularTypes(ninoType, desiredType);
                    }
                }
            }
        }

        foreach (var ninoType in ninoTypes)
        {
            if (ninoType.TypeSymbol.IsValueType)
            {
                continue;
            }

            TraverseCircularTypes(ninoType, ninoType);
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine("Base Types:");
        foreach (var kvp in BaseTypes.Where(t => t.Value.Count > 0))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            sb.AppendLine(
                $"{key.TypeSymbol.GetDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.GetDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Sub Types:");
        foreach (var kvp in SubTypes.Where(t => t.Value.Count > 0))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            sb.AppendLine(
                $"{key.TypeSymbol.GetDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.GetDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Top Types:");
        sb.AppendLine(string.Join("\n",
            TopTypes.Where(t => t.Members.Count > 0 && !t.TypeSymbol.IsUnmanagedType)
                .Select(x => x.TypeSymbol.GetDisplayString())));

        sb.AppendLine();
        sb.AppendLine("Circular Types:");
        sb.AppendLine(string.Join("\n",
            CircularTypes.Where(t => t.Members.Count > 0).Select(x => x.TypeSymbol.GetDisplayString())));

        return sb.ToString();
    }
}