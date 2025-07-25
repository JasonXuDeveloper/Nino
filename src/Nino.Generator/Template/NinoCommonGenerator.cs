using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Template;

public abstract class NinoCommonGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
    : NinoGenerator(compilation)
{
    protected readonly NinoGraph NinoGraph = ninoGraph;
    protected readonly List<NinoType> NinoTypes = ninoTypes;

    protected bool ValidType(ITypeSymbol type, HashSet<ITypeSymbol> validTypeNames)
    {
        if (type.SpecialType == SpecialType.System_String) return true;
        if (type.IsUnmanagedType) return true;
        if (validTypeNames.Contains(type)) return true;
        if (NinoGraph.TypeMap.ContainsKey(type.GetDisplayString())) return true;
        return false;
    }
}