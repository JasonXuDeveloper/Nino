using System.Collections.Generic;
using Nino.Generator.Metadata;

namespace Nino.Generator.Parser;

public abstract class NinoTypeParser
{
    protected abstract List<NinoType> ParseTypes();

    public (NinoGraph graph, List<NinoType> types) Parse()
    {
        var types = ParseTypes();
        var graph = new NinoGraph(types);
        return (graph, types);
    }
}