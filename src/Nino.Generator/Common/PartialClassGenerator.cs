using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.Common;

public class PartialClassGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
    : NinoCommonGenerator(compilation, ninoGraph, ninoTypes)
{
    string WriteMembers(Compilation compilation, HashSet<string> generatedTypes, NinoType type)
    {
        //ensure type is in this compilation, not from referenced assemblies
        if (!type.TypeSymbol.ContainingAssembly.Equals(compilation.Assembly, SymbolEqualityComparer.Default))
        {
            return "";
        }

        var sb = new StringBuilder();
        bool hasPrivateMembers = false;
        var ts = type.TypeSymbol;
        Dictionary<string, ITypeSymbol> memberToDeclaringType = new();
        if (ts is INamedTypeSymbol nts)
        {
            ts = nts.ConstructedFrom;
            var members = new List<ISymbol>();
            var curType = ts;
            while (curType != null && curType.IsNinoType())
            {
                members.AddRange(curType.GetMembers());
                curType = curType.BaseType;
            }

            foreach (var member in type.Members)
            {
                var m = members.FirstOrDefault(m => m.Name == member.Name);
                if (m != null)
                {
                    memberToDeclaringType[member.Name] = m switch
                    {
                        IFieldSymbol fieldSymbol => fieldSymbol.Type,
                        IPropertySymbol propertySymbol => propertySymbol.Type,
                        _ => member.Type
                    };
                }
                else
                {
                    memberToDeclaringType[member.Name] = member.Type;
                }
            }
        }

        try
        {
            foreach (var typeMember in type.Members)
            {
                var name = typeMember.Name;
                var declaredType = memberToDeclaringType[name];
                var isPrivate = typeMember.IsPrivate;
                var isProperty = typeMember.IsProperty;

                if (!isPrivate)
                {
                    continue;
                }

                var declaringType = declaredType.GetDisplayString();
                var member = ts.GetMembers().FirstOrDefault(m => m.Name == name);
                if (member != null)
                {
                    if (member is IFieldSymbol fieldSymbol)
                    {
                        declaringType = fieldSymbol.Type.GetDisplayString();
                    }
                    else if (member is IPropertySymbol propertySymbol)
                    {
                        declaringType = propertySymbol.Type.GetDisplayString();
                    }
                }

                hasPrivateMembers = true;
                var accessor = $$$"""
                                          [Nino.Core.NinoPrivateProxy(nameof({{{name}}}), {{{isProperty.ToString().ToLower()}}})]
                                          public new {{{declaringType}}} __nino__generated__{{{name}}}
                                          {
                                              [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                              get => {{{name}}};
                                              [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                              set => {{{name}}} = value;
                                          }
                                  """;
                sb.AppendLine(accessor);
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"/* Error: {e.Message} for type {type.TypeSymbol.GetTypeFullName()}");
            //add stacktrace
            foreach (var line in (e.StackTrace ?? "").Split('\n'))
            {
                sb.AppendLine($" * {line}");
            }

            //end error
            sb.AppendLine(" */");
        }

        if (!hasPrivateMembers)
        {
            return "";
        }

        var hasNamespace = !ts.ContainingNamespace.IsGlobalNamespace &&
                           !string.IsNullOrEmpty(ts.ContainingNamespace.ToDisplayString());
        var typeNamespace = ts.ContainingNamespace.ToDisplayString();
        var modifer = ts.GetTypeModifiers();
        //get typename, including type parameters if any
        var typeSimpleName = ts.Name;
        //type arguments to type parameters
        if (ts is INamedTypeSymbol namedTypeSymbol)
        {
            var typeParameters = namedTypeSymbol.TypeParameters;
            if (typeParameters.Length > 0)
            {
                typeSimpleName += $"<{string.Join(",", typeParameters.Select(t => t.GetDisplayString()))}>";
            }
        }

        if (!generatedTypes.Add(typeSimpleName))
        {
            return "";
        }

        var order = string.Join(", ", type.Members.Select(m => $"nameof({m.Name})"));

        var namespaceStr = hasNamespace ? $"namespace {typeNamespace}\n" : "";
        if (hasNamespace)
        {
            namespaceStr += "{";
        }

        // generate code
        var code = $$"""
                     {{namespaceStr}}
                     #if !NET8_0_OR_GREATER
                         [Nino.Core.NinoExplicitOrder({{order}})]
                         public partial {{modifer}} {{typeSimpleName}}
                         {
                     {{sb}}    }
                     #endif
                     """;
        if (hasNamespace)
        {
            code += "\n}";
        }

        return code;
    }

    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;

        HashSet<string> generatedTypes = new();
        List<string> generatedCode = new();

        foreach (var ninoType in NinoTypes)
        {
            bool isPolymorphicType = ninoType.IsPolymorphic();

            // check if struct is unmanaged
            if (ninoType.TypeSymbol.IsUnmanagedType && !isPolymorphicType)
            {
                continue;
            }

            if (NinoGraph.SubTypes.TryGetValue(ninoType, out var lst))
            {
                //sort lst by how deep the inheritance is (i.e. how many levels of inheritance), the deepest first
                lst.Sort((a, b) =>
                {
                    int aCount = NinoGraph.BaseTypes[a].Count;
                    int bCount = NinoGraph.BaseTypes[b].Count;
                    return bCount.CompareTo(aCount);
                });

                foreach (var subType in lst)
                {
                    if (subType.TypeSymbol.IsInstanceType())
                    {
                        if (subType.TypeSymbol.IsUnmanagedType)
                        {
                            continue;
                        }

                        generatedCode.Add(WriteMembers(compilation, generatedTypes, subType));
                    }
                }
            }

            if (ninoType.TypeSymbol.IsInstanceType())
            {
                if (ninoType.TypeSymbol.IsUnmanagedType)
                {
                    continue;
                }

                generatedCode.Add(WriteMembers(compilation, generatedTypes, ninoType));
            }
        }

        var code = $$"""
                     // <auto-generated/>
                     #pragma warning disable CS0109, CS8669
                     using System;
                     using System.Runtime.CompilerServices;

                     {{string.Join("\n", generatedCode.Where(c => !string.IsNullOrEmpty(c)))}}
                     """;
        spc.AddSource($"{Compilation.AssemblyName!.GetNamespace()}.PartialClass.g.cs", code);
    }
}