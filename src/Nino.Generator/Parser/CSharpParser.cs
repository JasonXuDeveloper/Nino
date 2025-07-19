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
        List<NinoType> result = new();
        var types = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        types.UnionWith(_ninoSymbols);
        Dictionary<ITypeSymbol, NinoType> typeMap = new(SymbolEqualityComparer.Default);

        foreach (var ninoSymbol in _ninoSymbols)
        {
            NinoType GetNinoType(ITypeSymbol typeSymbol)
            {
                if (typeMap.TryGetValue(typeSymbol, out var ninoType))
                {
                    return ninoType;
                }

                ninoType = new NinoType(compilation, typeSymbol, null, null);

                bool IsNinoType(ITypeSymbol ts)
                {
                    if (!ts.IsNinoType())
                        return false;

                    if (!types.Contains(ts))
                        GetNinoType(ts);

                    return true;
                }

                // collect base type
                if (typeSymbol.BaseType != null && IsNinoType(typeSymbol.BaseType))
                {
                    ninoType.AddParent(GetNinoType(typeSymbol.BaseType));
                }

                // collect base interfaces
                foreach (var interfaceType in typeSymbol.AllInterfaces)
                {
                    if (IsNinoType(interfaceType))
                    {
                        ninoType.AddParent(GetNinoType(interfaceType));
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

                    // if is private and the type that contains this member is from another type, ignore it
                    // since this is not allowed in C#
                    if (isPrivate && !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, typeSymbol))
                    {
                        // in fact, protected is allowed, through the inheritance chain
                        if (symbol.DeclaredAccessibility != Accessibility.Protected)
                            continue;
                    }

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
                    ninoType.AddMember(member);
                }

                //add to map
                typeMap[typeSymbol] = ninoType;

                return ninoType;
            }

            result.Add(GetNinoType(ninoSymbol));
        }

        return result;
    }
}