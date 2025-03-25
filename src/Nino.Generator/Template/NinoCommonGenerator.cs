using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Template;

public abstract class NinoCommonGenerator : NinoGenerator
{
    protected readonly NinoGraph NinoGraph;
    protected readonly List<NinoType> NinoTypes;

    protected NinoCommonGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes) :
        base(compilation)
    {
        NinoGraph = ninoGraph;
        NinoTypes = ninoTypes;
    }
}