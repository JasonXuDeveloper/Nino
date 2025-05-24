using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Parser;

public abstract class NinoTypeParser
{
    protected abstract List<NinoType> ParseTypes();

    public (NinoGraph graph, List<NinoType> types) Parse(Compilation compilation)
    {
        var types = ParseTypes();
        var graph = new NinoGraph(compilation, types);
        return (graph, types);
    }
}