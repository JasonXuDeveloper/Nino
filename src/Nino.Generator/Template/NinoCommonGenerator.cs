using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Template;

public abstract class NinoCommonGenerator : NinoGenerator
{
    protected readonly List<ITypeSymbol> NinoSymbols;
    protected readonly Dictionary<string, List<string>> InheritanceMap;
    protected readonly Dictionary<string, List<string>> SubTypeMap;
    protected readonly ImmutableArray<string> TopNinoTypes;

    protected NinoCommonGenerator(Compilation compilation, List<ITypeSymbol> ninoSymbols,
        Dictionary<string, List<string>> inheritanceMap, Dictionary<string, List<string>> subTypeMap,
        ImmutableArray<string> topNinoTypes)
        : base(compilation)
    {
        NinoSymbols = ninoSymbols.ToList();
        InheritanceMap = inheritanceMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        SubTypeMap = subTypeMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        TopNinoTypes = topNinoTypes.ToImmutableArray();
    }
}