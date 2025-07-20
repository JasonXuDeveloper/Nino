using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Template;

public abstract class NinoCommonGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
    : NinoGenerator(compilation)
{
    protected readonly NinoGraph NinoGraph = ninoGraph;
    protected readonly List<NinoType> NinoTypes = ninoTypes;
}