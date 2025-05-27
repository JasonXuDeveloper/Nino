using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using System.Linq;

namespace Nino.Generator.Parser;

public class CSharpParser : NinoTypeParser
{
    private readonly List<ITypeSymbol> _ninoSymbols;

    private const string NinoExplicitOrderAttributeFullName = "Nino.NinoExplicitOrderAttribute";
    private const string NinoTypeAttributeFullName = "Nino.NinoTypeAttribute";
    private const string NinoIgnoreAttributeFullName = "Nino.NinoIgnoreAttribute";
    private const string NinoMemberAttributeFullName = "Nino.NinoMemberAttribute";
    private const string NinoPrivateProxyAttributeFullName = "Nino.NinoPrivateProxyAttribute";
    private const string NinoUtf8AttributeFullName = "Nino.NinoUtf8Attribute";

    public CSharpParser(List<ITypeSymbol> ninoSymbols)
    {
        _ninoSymbols = ninoSymbols;
    }

    protected (List<NinoType> types, Dictionary<ITypeSymbol, NinoType> typeMap) ParseTypesAndMap(Compilation compilation)
    {
        List<NinoType> result = new();
        var processedOrKnownNinoSymbols = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        processedOrKnownNinoSymbols.UnionWith(_ninoSymbols);
        
        Dictionary<ITypeSymbol, NinoType> typeMap = new(SymbolEqualityComparer.Default);

        // Pre-resolve attribute symbols
        var ninoExplicitOrderAttrSymbol = compilation.GetTypeByMetadataName(NinoExplicitOrderAttributeFullName);
        var ninoTypeAttrSymbol = compilation.GetTypeByMetadataName(NinoTypeAttributeFullName);
        var ninoIgnoreAttrSymbol = compilation.GetTypeByMetadataName(NinoIgnoreAttributeFullName);
        var ninoMemberAttrSymbol = compilation.GetTypeByMetadataName(NinoMemberAttributeFullName);
        var ninoPrivateProxyAttrSymbol = compilation.GetTypeByMetadataName(NinoPrivateProxyAttributeFullName);
        var ninoUtf8AttrSymbol = compilation.GetTypeByMetadataName(NinoUtf8AttributeFullName);

        foreach (var ninoSymbol in _ninoSymbols)
        {
            result.Add(GetNinoType(ninoSymbol.GetPureType(), compilation, ninoExplicitOrderAttrSymbol, ninoTypeAttrSymbol, ninoIgnoreAttrSymbol, ninoMemberAttrSymbol, ninoPrivateProxyAttrSymbol, ninoUtf8AttrSymbol));
        }

        NinoType GetNinoType(ITypeSymbol typeSymbol, Compilation comp, 
            INamedTypeSymbol? explicitOrderSym, 
            INamedTypeSymbol? typeSym, 
            INamedTypeSymbol? ignoreSym, 
            INamedTypeSymbol? memberSym, 
            INamedTypeSymbol? privateProxySym, 
            INamedTypeSymbol? utf8Sym)
        {
            var pureTypeSymbol = typeSymbol.GetPureType();
            if (typeMap.TryGetValue(pureTypeSymbol, out var ninoType))
            {
                return ninoType;
            }

            ninoType = new NinoType(pureTypeSymbol, null, null);
            typeMap[pureTypeSymbol] = ninoType; 
            
            bool IsNinoTypeAndProcessIfNeeded(ITypeSymbol ts)
            {
                // Check IsNinoType using pre-resolved symbol
                bool isType = ts.IsUnmanagedType || ts.GetAttributes().Any(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, typeSym));
                if (!isType) return false;
                
                var pureTs = ts.GetPureType();
                if (!typeMap.ContainsKey(pureTs)) 
                {
                     GetNinoType(pureTs, comp, explicitOrderSym, typeSym, ignoreSym, memberSym, privateProxySym, utf8Sym);
                }
                return true;
            }
            
            if (pureTypeSymbol.BaseType != null && IsNinoTypeAndProcessIfNeeded(pureTypeSymbol.BaseType))
            {
                ninoType.AddParent(GetNinoType(pureTypeSymbol.BaseType.GetPureType(), comp, explicitOrderSym, typeSym, ignoreSym, memberSym, privateProxySym, utf8Sym));
            }

            foreach (var interfaceType in pureTypeSymbol.AllInterfaces)
            {
                if (IsNinoTypeAndProcessIfNeeded(interfaceType))
                {
                    ninoType.AddParent(GetNinoType(interfaceType.GetPureType(), comp, explicitOrderSym, typeSym, ignoreSym, memberSym, privateProxySym, utf8Sym));
                }
            }

            List<NinoMember> ninoMembers = new();
            var explicitOrderAttrData = pureTypeSymbol.GetAttributes().FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, explicitOrderSym));
            Dictionary<string, int> order = new();
            if (explicitOrderAttrData != null && explicitOrderAttrData.ConstructorArguments.Length > 0)
            {
                foreach(var val in explicitOrderAttrData.ConstructorArguments[0].Values)
                {
                    if(val.Value is string strVal)
                    {
                        order[strVal] = order.Count; // Keep original logic of using .Count for index
                    }
                }
            }
            
            var ninoTypeAttrData = pureTypeSymbol.GetAttributes().FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, typeSym));
            bool autoCollect = ninoTypeAttrData == null || (ninoTypeAttrData.ConstructorArguments.Length > 0 && (bool)(ninoTypeAttrData.ConstructorArguments[0].Value ?? false));
            bool containNonPublic = ninoTypeAttrData != null && (ninoTypeAttrData.ConstructorArguments.Length > 1 && (bool)(ninoTypeAttrData.ConstructorArguments[1].Value ?? false));

            Dictionary<string, int> memberNinoIndex = new Dictionary<string, int>(); // Renamed to avoid confusion with 'order'
            HashSet<ISymbol> members = new(SymbolEqualityComparer.Default);
            var primaryConstructorParams = new List<IParameterSymbol>();

            void AddMembersRecursively(NinoType typeToScanMembersFrom)
            {
                var symbolToScan = typeToScanMembersFrom.TypeSymbol;
                foreach (var member in symbolToScan.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;
                    bool valid = false;
                    if (member is IFieldSymbol fieldSymbol)
                    {
                        valid = (containNonPublic || fieldSymbol.DeclaredAccessibility == Accessibility.Public) && !fieldSymbol.IsStatic;
                    }
                    else if (member is IPropertySymbol propertySymbol)
                    {
                        valid = propertySymbol.GetMethod != null && propertySymbol.SetMethod != null &&
                                (containNonPublic || propertySymbol.DeclaredAccessibility == Accessibility.Public) && !propertySymbol.IsStatic;
                    }
                    if (valid) members.Add(member);
                }
                if (symbolToScan is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsRecord)
                {
                    var publicConstructors = namedTypeSymbol.InstanceConstructors
                        .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared && !c.IsStatic)
                        .ToList();
                    foreach (var constructor in publicConstructors)
                    {
                        bool isPrimaryLike = true;
                        foreach (var parameter in constructor.Parameters)
                        {
                            var matchingProperty = namedTypeSymbol.GetMembers()
                                .OfType<IPropertySymbol>()
                                .FirstOrDefault(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase) && !p.IsStatic);
                            if (matchingProperty == null || !matchingProperty.IsReadOnly) { isPrimaryLike = false; break; }
                        }
                        if (isPrimaryLike) { primaryConstructorParams.AddRange(constructor.Parameters); break; }
                    }
                }
                foreach (var parentNinoType in typeToScanMembersFrom.Parents)
                {
                    AddMembersRecursively(parentNinoType);
                }
            }

            AddMembersRecursively(ninoType);
            members.UnionWith(primaryConstructorParams.Cast<ISymbol>());

            foreach (var symbol in members)
            {
                var attrList = symbol.GetAttributes();
                if (autoCollect && attrList.Any(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, ignoreSym))) continue;

                var ninoPrivateProxyAttributeData = attrList.FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, privateProxySym));
                bool isPrivateProxy = false;
                bool isProxyProperty = false;
                string proxyName = "";

                if (ninoPrivateProxyAttribute != null)
                {
                    var args = ninoPrivateProxyAttribute.ConstructorArguments;
                    isPrivateProxy = true;
                    isProxyProperty = (args.Length > 1 && (bool)(args[1].Value ?? false));
                    proxyName = (args.Length > 0 && args[0].Value is string pName) ? pName : "";
                }

                var memberName = isPrivateProxy ? proxyName : symbol.Name;
                if (memberNinoIndex.ContainsKey(memberName)) continue; // Already processed (e.g. from a base class but explicitly re-declared)

                bool isPrivate = symbol.DeclaredAccessibility != Accessibility.Public;
                if (primaryConstructorParams.Contains(symbol, SymbolEqualityComparer.Default)) isPrivate = false;
                if (isPrivateProxy) isPrivate = true;

                var memberTypeSymbol = symbol switch { IFieldSymbol fs => fs.Type, IPropertySymbol ps => ps.Type, IParameterSymbol pms => pms.Type, _ => null };
                if (memberTypeSymbol == null) continue;

                memberTypeSymbol = memberTypeSymbol.GetPureType();
                var isProperty = isPrivateProxy ? isProxyProperty : symbol is IPropertySymbol;
                bool isUtf8 = attrList.Any(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, utf8Sym));

                if (autoCollect || isPrivateProxy)
                {
                    memberNinoIndex[memberName] = memberNinoIndex.Count; // Auto-increment index for auto-collected members
                    ninoMembers.Add(new NinoMember(memberName, memberTypeSymbol)
                    {
                        IsCtorParameter = symbol is IParameterSymbol, IsPrivate = isPrivate, IsProperty = isProperty, IsUtf8String = isUtf8,
                    });
                }
                else 
                {
                    var ninoMemberAttrData = attrList.FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, memberSym));
                    if (ninoMemberAttrData?.ConstructorArguments.FirstOrDefault().Value is ushort indexValue)
                    {
                        memberNinoIndex[memberName] = indexValue;
                        ninoMembers.Add(new NinoMember(memberName, memberTypeSymbol)
                        {
                            IsCtorParameter = symbol is IParameterSymbol, IsPrivate = isPrivate, IsProperty = isProperty, IsUtf8String = isUtf8,
                        });
                    }
                }
            }
            
            ninoMembers.Sort((a, b) => {
                int valA = order.TryGetValue(a.Name, out int oa) ? oa : (memberNinoIndex.TryGetValue(a.Name, out int ia) ? ia : int.MaxValue);
                int valB = order.TryGetValue(b.Name, out int ob) ? ob : (memberNinoIndex.TryGetValue(b.Name, out int ib) ? ib : int.MaxValue);
                int comparison = valA.CompareTo(valB);
                return comparison == 0 ? string.Compare(a.Name, b.Name, StringComparison.Ordinal) : comparison;
            });

            foreach (var member in ninoMembers)
            {
                ninoType.AddMember(member);
            }
            return ninoType;
        }
        return (result.Select(nt => typeMap[nt.TypeSymbol.GetPureType()]).Distinct().ToList(), typeMap);
    }

    public (NinoGraph graph, List<NinoType> types) Parse(Compilation compilation)
    {
        var (ninoTypes, typeMap) = ParseTypesAndMap(compilation); // Pass compilation here
        NinoGraph graph = new NinoGraph(compilation, ninoTypes, typeMap);
        return (graph, ninoTypes);
    }
}