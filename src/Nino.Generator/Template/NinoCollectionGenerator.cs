using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Template;

public abstract class NinoCollectionGenerator(Compilation compilation, List<ITypeSymbol> potentialCollectionSymbols)
    : NinoGenerator(compilation)
{
    protected readonly List<ITypeSymbol> PotentialCollectionSymbols = potentialCollectionSymbols;
}