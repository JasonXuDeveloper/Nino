using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Parser;

public abstract class NinoTypeParser
{
    protected abstract HashSet<NinoType> ParseTypes(Compilation compilation);

    public (NinoGraph graph, HashSet<NinoType> types) Parse(Compilation compilation)
    {
        var types = ParseTypes(compilation);
        var graph = new NinoGraph(compilation, types);
        return (graph, types);
    }
}