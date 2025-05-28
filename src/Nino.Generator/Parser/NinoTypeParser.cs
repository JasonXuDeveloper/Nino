using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Parser;

public abstract class NinoTypeParser
{
    protected abstract (List<NinoType> types, Dictionary<ITypeSymbol, NinoType> typeMap) ParseTypes(Compilation compilation);

    public virtual (NinoGraph graph, List<NinoType> types) Parse(Compilation compilation)
    {
        var (types, typeMap) = ParseTypes(compilation);
        var graph = new NinoGraph(compilation, types, typeMap);
        return (graph, types);
    }
}