using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using System.Linq;

namespace Nino.Generator.Parser;

public class CSharpParser : NinoTypeParser
{
    private readonly List<ITypeSymbol> _ninoSymbols;

    public CSharpParser(List<ITypeSymbol> ninoSymbols)
    {
        _ninoSymbols = ninoSymbols;
    }

    protected override List<NinoType> ParseTypes(Compilation compilation)
    {
        Dictionary<ITypeSymbol, NinoType> typeMap = new(SymbolEqualityComparer.Default);

        Dictionary<ITypeSymbol, bool> scannableMap = new(SymbolEqualityComparer.Default);
        var obsoleteAttr = compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
        var previewAttr = compilation.GetTypeByMetadataName(
            "System.Runtime.Versioning.RequiresPreviewFeaturesAttribute");

        bool IsSystemType(ITypeSymbol type)
        {
            if (type.IsTupleType)
                return true;
            if (type is INamedTypeSymbol ns &&
                ns.ConstructedFrom.SpecialType is SpecialType.System_Enum
                    or SpecialType.System_Collections_Generic_ICollection_T
                    or SpecialType.System_Collections_Generic_IEnumerable_T
                    or SpecialType.System_Collections_Generic_IList_T
                    or SpecialType.System_Collections_Generic_IReadOnlyCollection_T
                    or SpecialType.System_Collections_Generic_IReadOnlyList_T
                    or SpecialType.System_Collections_IEnumerable
                    or SpecialType.System_Collections_IEnumerator
                    or SpecialType.System_Nullable_T)
            {
                return true;
            }

            // KeyValuePair, ValueTuple, Tuple, should be considered system types
            if (type.Name.Contains(".KeyValuePair<")
                || type.Name.Contains(".ValueTuple<")
                || type.Name.Contains(".Tuple<"))
            {
                return true;
            }

            return false;
        }

        bool IsScannable(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Object)
                return true;
            if (type.SpecialType == SpecialType.System_Enum)
                return false;
            if (!type.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute"))
            {
                if (IsSystemType(type))
                    return false;

                var interfaces = type.AllInterfaces;
                foreach (var interfaceType in interfaces)
                {
                    if (IsSystemType(interfaceType))
                        return false;
                }
            }

            // check if already scanned
            if (scannableMap.TryGetValue(type, out var isScannable))
            {
                return isScannable;
            }

            // accessibility check
            if (!type.IsAccessibleFromCurrentAssembly(compilation))
            {
                scannableMap[type] = false;
                return false;
            }

            // system types check
            if (type.SpecialType != SpecialType.None)
            {
                scannableMap[type] = false;
                return false;
            }

            // exclude [Obsolete] types
            if (obsoleteAttr != null
                && type.GetAttributes().Any(ad =>
                    SymbolEqualityComparer.Default.Equals(ad.AttributeClass, obsoleteAttr)))
            {
                scannableMap[type] = false;
                return false;
            }

            // exclude preview‐only APIs
            if (previewAttr != null)
            {
                if (type.GetAttributes().Any(ad =>
                        SymbolEqualityComparer.Default.Equals(ad.AttributeClass, previewAttr)))
                {
                    scannableMap[type] = false;
                    return false;
                }

                if (type.ContainingAssembly.GetAttributes().Any(ad =>
                        SymbolEqualityComparer.Default.Equals(ad.AttributeClass, previewAttr)))
                {
                    scannableMap[type] = false;
                    return false;
                }
            }

            scannableMap[type] = true;
            return true;
        }

        void GetNinoType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.IsTupleType && typeSymbol is INamedTypeSymbol nts)
            {
                typeSymbol = nts.TupleUnderlyingType ?? typeSymbol;
            }

            if (!IsScannable(typeSymbol))
                return;

            if (typeMap.TryGetValue(typeSymbol, out var ninoType))
                return;

            // scan generic type arguments first
            if (typeSymbol is INamedTypeSymbol { IsGenericType: true } ns)
            {
                foreach (var arg in ns.TypeArguments)
                {
                    if (IsScannable(arg))
                    {
                        GetNinoType(arg);
                    }
                }
            }

            ninoType = new NinoType(typeSymbol, null, null);
            //add to map
            typeMap[typeSymbol] = ninoType;

            bool IsValidType(ITypeSymbol ts)
            {
                if (ts.IsTupleType && ts is INamedTypeSymbol nss)
                {
                    ts = nss.TupleUnderlyingType ?? ts;
                }

                if (!IsScannable(ts))
                    return false;

                GetNinoType(ts);
                return true;
            }

            // collect base type
            if (typeSymbol.BaseType != null && IsValidType(typeSymbol.BaseType))
            {
                ninoType.AddParent(typeMap[typeSymbol.BaseType]);
            }

            // collect base interfaces
            foreach (var interfaceType in typeSymbol.AllInterfaces)
            {
                if (IsValidType(interfaceType))
                {
                    ninoType.AddParent(typeMap[interfaceType]);
                }
            }

            // collect members
            List<NinoMember> ninoMembers = new();

            //check if this type has attribute NinoExplicitOrder
            var explicitOrder = typeSymbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass != null &&
                a.AttributeClass.ToDisplayString().EndsWith("NinoExplicitOrderAttribute"));
            Dictionary<string, int> order = new();
            if (explicitOrder != null)
            {
                //first arg of NinoExplicitOrderAttribute is param string[] order
                for (var index = 0; index < explicitOrder.ConstructorArguments[0].Values.Length; index++)
                {
                    var value = explicitOrder.ConstructorArguments[0].Values[index];
                    order[(string)value.Value!] = index;
                }
            }

            //get NinoType attribute first argument value from typeSymbol
            var attr = typeSymbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass != null &&
                a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
            bool autoCollect = attr == null || (bool)(attr.ConstructorArguments[0].Value ?? false);
            bool containNonPublic = attr != null && (bool)(attr.ConstructorArguments[1].Value ?? false);

            Dictionary<string, int> memberIndex = new Dictionary<string, int>();
            HashSet<ISymbol> members = new(SymbolEqualityComparer.Default);
            var primaryConstructorParams = new List<IParameterSymbol>();

            void AddMembers(NinoType type)
            {
                // direct members
                foreach (var member in type.TypeSymbol.GetMembers())
                {
                    if (member.IsImplicitlyDeclared)
                        continue;

                    bool valid = false;
                    if (member is IFieldSymbol fieldSymbol)
                    {
                        valid = (containNonPublic || fieldSymbol.DeclaredAccessibility == Accessibility.Public) &&
                                !fieldSymbol.IsStatic;
                    }

                    if (member is IPropertySymbol propertySymbol)
                    {
                        valid = propertySymbol.GetMethod != null &&
                                propertySymbol.SetMethod != null &&
                                (containNonPublic ||
                                 propertySymbol.DeclaredAccessibility == Accessibility.Public) &&
                                !propertySymbol.IsStatic;
                    }

                    if (!valid)
                        continue;

                    members.Add(member);
                }

                //record ctor members
                if (type.TypeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsRecord)
                {
                    // Retrieve all public instance constructors
                    var publicConstructors = namedTypeSymbol.InstanceConstructors
                        .Where(c =>
                            c.DeclaredAccessibility == Accessibility.Public
                            && !c.IsImplicitlyDeclared
                            && !c.IsStatic
                        )
                        .ToList();

                    foreach (var constructor in publicConstructors)
                    {
                        // Check that each parameter in the constructor has a matching readonly property with the same name
                        foreach (var parameter in constructor.Parameters)
                        {
                            var matchingProperty = namedTypeSymbol.GetMembers()
                                .OfType<IPropertySymbol>()
                                .FirstOrDefault(p =>
                                    p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)
                                    && !p.IsStatic);

                            // If any parameter does not have a matching readonly property, it’s likely not a primary constructor
                            if (matchingProperty == null || !matchingProperty.IsReadOnly)
                            {
                                break;
                            }
                        }

                        primaryConstructorParams.AddRange(constructor.Parameters);
                        break;
                    }
                }

                // Parent members
                foreach (var parent in type.Parents)
                {
                    AddMembers(parent);
                }
            }

            AddMembers(ninoType);
            members.UnionWith(primaryConstructorParams);

            //clean up
            foreach (var symbol in members)
            {
                var attrList = symbol.GetAttributes();
                //if has ninoignore attribute, ignore this member
                if (autoCollect &&
                    attrList.Any(a => a.AttributeClass?.Name.EndsWith("NinoIgnoreAttribute") ?? false))
                {
                    continue;
                }

                //if has NinoPrivateProxyAttribute, it is a private proxy
                var ninoPrivateProxyAttribute = attrList.FirstOrDefault(a =>
                    a.AttributeClass?.Name.EndsWith("NinoPrivateProxyAttribute") ?? false);
                bool isPrivateProxy = false;
                bool isProxyProperty = false;
                string proxyName = "";

                if (ninoPrivateProxyAttribute != null)
                {
                    var args = ninoPrivateProxyAttribute.ConstructorArguments;
                    isPrivateProxy = true;
                    isProxyProperty = (bool)(args[1].Value ?? throw new InvalidOperationException());
                    proxyName = (string)(args[0].Value ?? throw new InvalidOperationException());
                }

                var memberName = isPrivateProxy ? proxyName : symbol.Name;
                if (memberIndex.ContainsKey(memberName))
                {
                    continue;
                }

                bool isPrivate = symbol.DeclaredAccessibility != Accessibility.Public;
                //we dont count primary constructor params as private
                if (primaryConstructorParams.Contains(symbol, SymbolEqualityComparer.Default))
                {
                    isPrivate = false;
                }

                if (isPrivateProxy)
                    isPrivate = true;

                var memberType = symbol switch
                {
                    IFieldSymbol fieldSymbol => fieldSymbol.Type,
                    IPropertySymbol propertySymbol => propertySymbol.Type,
                    IParameterSymbol parameterSymbol => parameterSymbol.Type,
                    _ => null
                };

                if (memberType == null)
                {
                    continue;
                }

                //nullability check
                memberType = memberType.GetPureType();
                var isProperty = isPrivateProxy ? isProxyProperty : symbol is IPropertySymbol;

                if (autoCollect || isPrivateProxy)
                {
                    memberIndex[memberName] = memberIndex.Count;
                    ninoMembers.Add(new(memberName, memberType)
                    {
                        IsCtorParameter = symbol is IParameterSymbol,
                        IsPrivate = isPrivate,
                        IsProperty = isProperty,
                        IsUtf8String = attrList.Any(a =>
                            a.AttributeClass?.Name.EndsWith("NinoUtf8Attribute") ?? false),
                    });
                    continue;
                }

                //get nino member attribute's first argument on this member
                var arg = attrList.FirstOrDefault(a
                        => a.AttributeClass?.Name.EndsWith("NinoMemberAttribute") ?? false)?
                    .ConstructorArguments.FirstOrDefault();
                if (arg == null)
                {
                    continue;
                }

                //get index value from NinoMemberAttribute
                var indexValue = arg.Value.Value;
                if (indexValue == null)
                {
                    continue;
                }

                memberIndex[memberName] = (ushort)indexValue;
                ninoMembers.Add(new(memberName, memberType)
                {
                    IsCtorParameter = symbol is IParameterSymbol,
                    IsPrivate = isPrivate,
                    IsProperty = isProperty,
                    IsUtf8String = attrList.Any(a =>
                        a.AttributeClass?.Name.EndsWith("NinoUtf8Attribute") ?? false),
                });
            }

            //sort by name
            ninoMembers.Sort((a, b) =>
            {
                var aName = a.Name;
                var bName = b.Name;
                return string.Compare(aName, bName, StringComparison.Ordinal);
            });
            //sort by index
            ninoMembers.Sort((a, b) =>
            {
                var aName = a.Name;
                var bName = b.Name;
                return memberIndex[aName].CompareTo(memberIndex[bName]);
            });

            if (order.Count > 0)
            {
                ninoMembers.Sort((a, b) =>
                {
                    var aName = a.Name;
                    var bName = b.Name;
                    return order[aName].CompareTo(order[bName]);
                });
            }

            //add members
            foreach (var member in ninoMembers)
            {
                var memberType = member.Type;
                IsValidType(memberType);
                ninoType.AddMember(member);
            }
        }

        foreach (var ninoSymbol in _ninoSymbols)
        {
            if (ninoSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute"))
                GetNinoType(ninoSymbol);
        }

        List<NinoType> result = new();
        foreach (var kvp in typeMap)
        {
            result.Add(kvp.Value);
        }

        return result;
    }
}