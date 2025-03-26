using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.Common;

public class PartialClassGenerator : NinoCommonGenerator
{
    public PartialClassGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
        : base(compilation, ninoGraph, ninoTypes)
    {
    }

    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;

        HashSet<string> generatedTypes = new();

        foreach (var ninoType in NinoTypes)
        {
            bool isPolymorphicType = ninoType.IsPolymorphic();

            // check if struct is unmanaged
            if (ninoType.TypeSymbol.IsUnmanagedType && !isPolymorphicType)
            {
                continue;
            }

            void WriteMembers(NinoType type)
            {
                //ensure type is in this compilation, not from referenced assemblies
                if (!type.TypeSymbol.ContainingAssembly.Equals(compilation.Assembly, SymbolEqualityComparer.Default))
                {
                    return;
                }

                var sb = new StringBuilder();
                bool hasPrivateMembers = false;

                try
                {
                    foreach (var typeMember in type.Members)
                    {
                        var name = typeMember.Name;
                        var declaredType = typeMember.Type;
                        var isPrivate = typeMember.IsPrivate;
                        var isProperty = typeMember.IsProperty;

                        if (!isPrivate)
                        {
                            continue;
                        }

                        var declaringType = declaredType.ToDisplayString();

                        if (type.TypeSymbol is INamedTypeSymbol nts)
                        {
                            if (nts.TypeParameters.Length > 0)
                            {
                                var member = nts.ConstructedFrom
                                    .GetMembers().FirstOrDefault(m => m.Name == name);
                                if (member != null)
                                {
                                    if (member is IFieldSymbol fieldSymbol)
                                    {
                                        declaringType = fieldSymbol.Type.ToDisplayString();
                                    }
                                    else if (member is IPropertySymbol propertySymbol)
                                    {
                                        declaringType = propertySymbol.Type.ToDisplayString();
                                    }
                                }
                            }
                        }

                        hasPrivateMembers = true;
                        var accessor = $$$"""
                                                  [Nino.Core.NinoPrivateProxy(nameof({{{name}}}), {{{isProperty.ToString().ToLower()}}})]
                                                  public {{{declaringType}}} __nino__generated__{{{name}}}
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
                    sb.AppendLine($"/* Error: {e.Message} for type {ninoType.TypeSymbol.GetTypeFullName()}");
                    //add stacktrace
                    foreach (var line in e.StackTrace.Split('\n'))
                    {
                        sb.AppendLine($" * {line}");
                    }

                    //end error
                    sb.AppendLine(" */");
                }

                if (!hasPrivateMembers)
                {
                    return;
                }

                var hasNamespace = !type.TypeSymbol.ContainingNamespace.IsGlobalNamespace &&
                                   !string.IsNullOrEmpty(type.TypeSymbol.ContainingNamespace.ToDisplayString());
                var typeNamespace = type.TypeSymbol.ContainingNamespace.ToDisplayString();
                var modifer = type.TypeSymbol.GetTypeModifiers();
                //get typename, including type parameters if any
                var typeSimpleName = type.TypeSymbol.Name;
                //type arguments to type parameters
                if (type.TypeSymbol is INamedTypeSymbol namedTypeSymbol)
                {
                    var typeParameters = namedTypeSymbol.TypeParameters;
                    if (typeParameters.Length > 0)
                    {
                        typeSimpleName += $"<{string.Join(",", typeParameters.Select(t => t.ToDisplayString()))}>";
                    }
                }

                if (!generatedTypes.Add(typeSimpleName))
                {
                    return;
                }

                var namespaceStr = hasNamespace ? $"namespace {typeNamespace}\n" : "";
                if (hasNamespace)
                {
                    namespaceStr += "{";
                }

                var order = string.Join(", ", type.Members.Select(m => $"nameof({m.Name})"));

                // generate code
                var code = $$"""
                             // <auto-generated/>

                             using System;
                             using System.Runtime.CompilerServices;

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

                spc.AddSource($"{typeSimpleName.Replace("<", "_").Replace(">", "_").Replace(",", "_")}.g.cs", code);
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

                        WriteMembers(subType);
                    }
                }
            }

            if (ninoType.TypeSymbol.IsInstanceType())
            {
                if (ninoType.TypeSymbol.IsUnmanagedType)
                {
                    continue;
                }

                WriteMembers(ninoType);
            }
        }
    }
}