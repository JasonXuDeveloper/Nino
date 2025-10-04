using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using System.Linq;

namespace Nino.Generator.Parser;

public class CSharpParser(HashSet<ITypeSymbol> ninoSymbols) : NinoTypeParser
{
    protected override HashSet<NinoType> ParseTypes(Compilation compilation)
    {
        HashSet<NinoType> result = new();
        Dictionary<ITypeSymbol, NinoType> typeMap = new(TupleSanitizedEqualityComparer.Default);

        foreach (var ninoSymbol in ninoSymbols)
        {
            // type from referenced assemblies
            if (!SymbolEqualityComparer.Default.Equals(ninoSymbol.ContainingAssembly, compilation.Assembly))
            {
                continue;
            }

            // inaccessible types
            if (ninoSymbol.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

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

                    if (!ninoSymbols.Contains(ts))
                    {
                        result.Add(GetNinoType(ts));
                    }

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

                //check if this type has attribute NinoExplicitOrder
                var explicitOrder = typeSymbol.GetAttributesCache().FirstOrDefault(a =>
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
                var attr = typeSymbol.GetAttributesCache().FirstOrDefault(a =>
                    a.AttributeClass != null &&
                    a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
                bool autoCollect = attr == null || (bool)(attr.ConstructorArguments[0].Value ?? false);
                bool containNonPublic = attr != null && (bool)(attr.ConstructorArguments[1].Value ?? false);

                // members for each type in the inheritance chain
                List<(HashSet<ISymbol> members, List<IParameterSymbol> primaryCtorParms)> hierarchicalMembers =
                    new();

                void AddMembers(NinoType type)
                {
                    var entry = (new HashSet<ISymbol>(SymbolEqualityComparer.Default), new List<IParameterSymbol>());
                    hierarchicalMembers.Add(entry);
                    var (members, primaryConstructorParams) = entry;
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
                                        p.IsImplicitlyDeclared &&
                                        p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)
                                        && !p.IsStatic);

                                // If any parameter does not have a matching readonly property, it’s likely not a primary constructor
                                if (matchingProperty == null || !matchingProperty.IsReadOnly)
                                {
                                    break;
                                }
                            }

                            primaryConstructorParams.AddRange(constructor.Parameters);
                            members.UnionWith(primaryConstructorParams);
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

                //clean up
                var inserted = new HashSet<string>();
                foreach (var entry in hierarchicalMembers)
                {
                    var ninoMembers = new List<NinoMember>();
                    Dictionary<string, int> memberIndex = new Dictionary<string, int>();

                    var (members, primaryConstructorParams) = entry;
                    foreach (var symbol in members)
                    {
                        var attrList = symbol.GetAttributesCache();
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
                        bool isCtorParameter = false;
                        //we dont count primary constructor params as private
                        if (primaryConstructorParams.Contains(symbol, SymbolEqualityComparer.Default))
                        {
                            isPrivate = false;
                            isCtorParameter = true;
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

                        // init only param
                        if (symbol is IPropertySymbol ps)
                        {
                            isCtorParameter = ps.IsImplicitlyDeclared ||
                                              ps.IsReadOnly ||
                                              ps.SetMethod == null ||
                                              ps.SetMethod.IsInitOnly;
                            if (!isCtorParameter)
                            {
                                isCtorParameter = primaryConstructorParams.Any(p => p.Name.Equals(ps.Name));
                            }
                        }
                        else if (symbol is IFieldSymbol fs)
                        {
                            isCtorParameter = fs.IsReadOnly ||
                                              fs.IsConst ||
                                              fs.IsImplicitlyDeclared ||
                                              fs.IsStatic;
                        }
                        else if (symbol is IParameterSymbol parameterSymbol)
                        {
                            isCtorParameter = parameterSymbol.IsThis || parameterSymbol.IsParams ||
                                              parameterSymbol.RefKind != RefKind.None ||
                                              parameterSymbol.IsImplicitlyDeclared ||
                                              parameterSymbol.IsDiscard;
                        }

                        if (autoCollect || isPrivateProxy)
                        {
                            memberIndex[memberName] = memberIndex.Count;
                            ninoMembers.Add(new(memberName, memberType, symbol)
                            {
                                IsCtorParameter = isCtorParameter || symbol.IsImplicitlyDeclared,
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
                        ninoMembers.Add(new(memberName, memberType, symbol)
                        {
                            IsCtorParameter = isCtorParameter || symbol.IsImplicitlyDeclared,
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
                        if (!inserted.Add(member.Name)) continue;
                        ninoType.AddMember(member);
                    }
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