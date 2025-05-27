using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoGraph
{
    public Dictionary<NinoType, List<NinoType>> BaseTypes { get; set; } = new();
    public Dictionary<NinoType, List<NinoType>> SubTypes { get; set; } = new();
    public List<NinoType> TopTypes { get; set; } = new();
    public HashSet<NinoType> CircularTypes { get; set; } = new();

    private readonly Compilation _compilation;
    private readonly Dictionary<ITypeSymbol, NinoType> _typeMap;
    private readonly Dictionary<(ITypeSymbol From, ITypeSymbol To), bool> _implicitConversionCache;
    // Memoizes (currentTypeSymbol, rootTypeSymbol) pairs to avoid re-processing entire sub-graphs for circularity.
    private readonly HashSet<(ITypeSymbol Current, ITypeSymbol Root)> _processedCircularPaths;


    public NinoGraph(Compilation compilation, List<NinoType> ninoTypes, Dictionary<ITypeSymbol, NinoType> typeMap)
    {
        _compilation = compilation;
        _typeMap = typeMap; // Use the passed-in typeMap
        _implicitConversionCache = new Dictionary<(ITypeSymbol From, ITypeSymbol To), bool>(SymbolConversionEqualityComparer.Default);
        _processedCircularPaths = new HashSet<(ITypeSymbol Current, ITypeSymbol Root)>(SymbolTupleEqualityComparer.Default);

        // Populate BaseTypes and TopTypes
        foreach (var ninoType in ninoTypes)
        {
            List<NinoType> baseTypes = new();
            BaseTypes.Add(ninoType, baseTypes);

            void TraverseBaseTypes(NinoType currentType) // Renamed 'type' to 'currentType' for clarity
            {
                if (currentType.Parents.Count == 0) return;
                foreach (var parent in currentType.Parents)
                {
                    if (!baseTypes.Contains(parent)) // Ensures each parent is added only once
                    {
                        baseTypes.Add(parent);
                        TraverseBaseTypes(parent); // Recurse for grandparents
                    }
                }
            }

            TraverseBaseTypes(ninoType);
            if (baseTypes.Count == 0)
            {
                TopTypes.Add(ninoType);
            }
        }

        // Populate SubTypes
        foreach (var kvp in BaseTypes)
        {
            var subType = kvp.Key; // Key is the subtype
            var bases = kvp.Value; // Value is the list of its base types
            foreach (var baseType in bases)
            {
                if (!SubTypes.TryGetValue(baseType, out var subTypeList))
                {
                    subTypeList = new List<NinoType>();
                    SubTypes.Add(baseType, subTypeList);
                }
                if (!subTypeList.Contains(subType))
                {
                    subTypeList.Add(subType);
                }
            }
        }
        
        // Find circular types
        foreach (var ninoType in ninoTypes)
        {
            if (ninoType.TypeSymbol.IsValueType) continue;
            // For each ninoType, start a new traversal to see if it's part of a cycle with itself as the root.
            // A new visitedOnPath set is created for each such root check.
            TraverseCircularTypes(ninoType, ninoType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        }
    }

    private bool CheckImplicitConversion(ITypeSymbol fromSymbol, ITypeSymbol toSymbol)
    {
        fromSymbol = fromSymbol.GetPureType();
        toSymbol = toSymbol.GetPureType();
        if (SymbolEqualityComparer.Default.Equals(fromSymbol, toSymbol)) return true;

        var key = (fromSymbol, toSymbol);
        if (_implicitConversionCache.TryGetValue(key, out var result))
        {
            return result;
        }
        result = _compilation.HasImplicitConversion(fromSymbol, toSymbol);
        _implicitConversionCache[key] = result;
        return result;
    }

    private bool InternalRelated(ITypeSymbol typeSymbol, ITypeSymbol desiredTypeSymbol)
    {
        typeSymbol = typeSymbol.GetPureType();
        desiredTypeSymbol = desiredTypeSymbol.GetPureType();

        if (SymbolEqualityComparer.Default.Equals(typeSymbol, desiredTypeSymbol)) return true;
        
        // Check direct implicit conversion (and cache it)
        if (CheckImplicitConversion(typeSymbol, desiredTypeSymbol) || CheckImplicitConversion(desiredTypeSymbol, typeSymbol))
        {
            return true;
        }

        switch (typeSymbol)
        {
            case INamedTypeSymbol namedTypeSymbol:
                if (namedTypeSymbol.IsGenericType)
                {
                    // For generics, check if any type argument is related to the desired type
                    // This is a simplification; true relatedness might depend on variance and specific generic structure.
                    // The original logic was `namedTypeSymbol.TypeArguments.Any(Related)`, which means `Any(ta => InternalRelated(ta, desiredTypeSymbol))`
                    foreach(var typeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (InternalRelated(typeArg, desiredTypeSymbol)) return true;
                    }
                }
                // No direct conversion for non-generic or if generic args didn't match
                return false; 

            case IArrayTypeSymbol arrayTypeSymbol:
                return InternalRelated(arrayTypeSymbol.ElementType, desiredTypeSymbol);

            default:
                // Already handled by CheckImplicitConversion above for non-array/non-named types.
                return false;
        }
    }
    
    private void TraverseCircularTypes(NinoType currentNinoType, NinoType rootNinoType, HashSet<ITypeSymbol> visitedOnPath)
    {
        // If this root type is already marked circular, no need to proceed.
        if (CircularTypes.Contains(rootNinoType)) return;

        ITypeSymbol currentTypeSymbolPure = currentNinoType.TypeSymbol.GetPureType();
        ITypeSymbol rootTypeSymbolPure = rootNinoType.TypeSymbol.GetPureType();

        // Memoization: Check if (currentNinoType, rootNinoType) has been fully explored.
        // If so, and rootNinoType wasn't marked circular by that exploration, then re-exploring won't change that.
        var processedPathKey = (currentTypeSymbolPure, rootTypeSymbolPure);
        if (_processedCircularPaths.Contains(processedPathKey))
        {
            return; 
        }

        // Path-specific cycle detection: if we re-visit currentNinoType in the current exploration path,
        // this path is looping on itself. Return to prevent infinite recursion for this specific path.
        if (!visitedOnPath.Add(currentTypeSymbolPure))
        {
            return;
        }

        try
        {
            foreach (var member in currentNinoType.Members)
            {
                if (member.Type.IsUnmanagedType) continue;

                // Check if member's type is directly related to the rootNinoType
                if (InternalRelated(member.Type, rootTypeSymbolPure))
                {
                    CircularTypes.Add(rootNinoType);
                    return; // rootNinoType is now circular, exit immediately.
                }

                // If member.Type is a known NinoType in our graph, recurse.
                if (_typeMap.TryGetValue(member.Type.GetPureType(), out var nextNinoTypeInGraph))
                {
                    TraverseCircularTypes(nextNinoTypeInGraph, rootNinoType, visitedOnPath);
                    // If the recursive call (or any subsequent one) marked rootNinoType as circular,
                    // no need to check other members of currentNinoType.
                    if (CircularTypes.Contains(rootNinoType)) return;
                }
            }
        }
        finally
        {
            // Backtrack: remove current type from path when returning from this exploration depth.
            visitedOnPath.Remove(currentTypeSymbolPure);
            
            // After fully exploring all paths from currentNinoType for rootNinoType:
            // If rootNinoType has NOT been marked circular through any of these paths,
            // then we can mark this (currentNinoType, rootNinoType) pair as "processed_and_did_not_lead_to_circularity".
            // This prevents re-exploring these same non-circular paths for this (current, root) pair later.
            if (!CircularTypes.Contains(rootNinoType))
            {
                _processedCircularPaths.Add(processedPathKey);
            }
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
                $"{key.TypeSymbol.ToDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.ToDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Sub Types:");
        foreach (var kvp in SubTypes.Where(t => t.Value.Count > 0))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            sb.AppendLine(
                $"{key.TypeSymbol.ToDisplayString()} -> {string.Join(", ", value.Select(x => x.TypeSymbol.ToDisplayString()))}");
        }

        sb.AppendLine();
        sb.AppendLine("Top Types:");
        sb.AppendLine(string.Join("\n",
            TopTypes.Where(t => t.Members.Count > 0 && !t.TypeSymbol.IsUnmanagedType)
                .Select(x => x.TypeSymbol.ToDisplayString())));

        sb.AppendLine();
        sb.AppendLine("Circular Types:");
        sb.AppendLine(string.Join("\n",
            CircularTypes.Where(t => t.Members.Count > 0).Select(x => x.TypeSymbol.ToDisplayString())));

        return sb.ToString();
    }
}