using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

[Generator]
public class PartialClassGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.GetTypeSyntaxes();
        var compilationAndClasses = context.CompilationProvider.Combine(types.Collect());
        context.RegisterSourceOutput(compilationAndClasses, (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static void Execute(Compilation compilation, ImmutableArray<CSharpSyntaxNode> syntaxes,
        SourceProductionContext spc)
    {
        try
        {
            var result = compilation.IsValidCompilation();
            if (!result.isValid) return;
            compilation = result.newCompilation;

            var ninoSymbols = syntaxes.GetNinoTypeSymbols(compilation);
            var (inheritanceMap,
                subTypeMap,
                _) = ninoSymbols.GetInheritanceMap();

            HashSet<string> generatedTypes = new();

            foreach (var typeSymbol in ninoSymbols)
            {
                string typeFullName = typeSymbol.GetTypeFullName();
                bool isPolymorphicType = typeSymbol.IsPolymorphicType();

                // check if struct is unmanaged
                if (typeSymbol.IsUnmanagedType && !isPolymorphicType)
                {
                    continue;
                }

                void WriteMembers(List<NinoTypeHelper.NinoMember> members, ITypeSymbol type)
                {
                    //ensure type is in this compilation, not from referenced assemblies
                    if (!type.ContainingAssembly.Equals(compilation.Assembly, SymbolEqualityComparer.Default))
                    {
                        return;
                    }

                    var sb = new StringBuilder();
                    bool hasPrivateMembers = false;

                    try
                    {
                        foreach (var (name, declaredType, _, _, isPrivate, _) in members)
                        {
                            if (!isPrivate)
                            {
                                continue;
                            }

                            var declaringType = declaredType.ToDisplayString();

                            if (type is INamedTypeSymbol nts)
                            {
                                if (nts.TypeParameters.Length > 0)
                                {
                                    var member = nts.ConstructedFrom
                                        .GetMembers().FirstOrDefault(m => m.Name == name);
                                    if (member != null)
                                    {
                                        if(member is IFieldSymbol fieldSymbol)
                                        {
                                            declaringType = fieldSymbol.Type.ToDisplayString();
                                        }
                                        else if(member is IPropertySymbol propertySymbol)
                                        {
                                            declaringType = propertySymbol.Type.ToDisplayString();
                                        }
                                    }
                                }
                            }

                            hasPrivateMembers = true;
                            sb.AppendLine(
                                $"        internal {declaringType} __nino__generated__{name}");
                            sb.AppendLine("        {");
                            sb.AppendLine($"            get => {name};");
                            sb.AppendLine($"            set => {name} = value;");
                            sb.AppendLine("        }");
                            sb.AppendLine();
                        }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine($"/* Error: {e.Message} for type {typeSymbol.GetTypeFullName()}");
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
                    
                    var hasNamespace = !type.ContainingNamespace.IsGlobalNamespace && !string.IsNullOrEmpty(type.ContainingNamespace.ToDisplayString());
                    var typeNamespace = type.ContainingNamespace.ToDisplayString();
                    var modifer = type.GetTypeModifiers();
                    //get typename, including type parameters if any
                    var typeSimpleName = type.Name;
                    //type arguments to type parameters
                    if (type is INamedTypeSymbol namedTypeSymbol)
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

                    // generate code
                    var code = $$"""
                                 // <auto-generated/>

                                 using System;
                                 using System.Runtime.CompilerServices;

                                 {{namespaceStr}}
                                 #if !NET8_0_OR_GREATER
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

                if (subTypeMap.TryGetValue(typeFullName, out var lst))
                {
                    //sort lst by how deep the inheritance is (i.e. how many levels of inheritance), the deepest first
                    lst.Sort((a, b) =>
                    {
                        int aCount = inheritanceMap[a].Count;
                        int bCount = inheritanceMap[b].Count;
                        return bCount.CompareTo(aCount);
                    });

                    foreach (var subType in lst)
                    {
                        var subTypeSymbol = ninoSymbols.First(s => s.GetTypeFullName() == subType);
                        if (subTypeSymbol.IsInstanceType())
                        {
                            if (subTypeSymbol.IsUnmanagedType)
                            {
                                continue;
                            }

                            List<ITypeSymbol> subTypeParentSymbols =
                                ninoSymbols.Where(m => inheritanceMap[subType]
                                    .Contains(m.GetTypeFullName())).ToList();

                            var members = subTypeSymbol.GetNinoTypeMembers(subTypeParentSymbols);
                            //get distinct members
                            members = members.Distinct().ToList();
                            WriteMembers(members, subTypeSymbol);
                        }
                    }
                }

                if (typeSymbol.IsInstanceType())
                {
                    if (typeSymbol.IsUnmanagedType)
                    {
                        continue;
                    }

                    List<ITypeSymbol> parentTypeSymbols =
                        ninoSymbols.Where(m => inheritanceMap[typeFullName]
                            .Contains(m.GetTypeFullName())).ToList();
                    var defaultMembers = typeSymbol.GetNinoTypeMembers(parentTypeSymbols);
                    WriteMembers(defaultMembers, typeSymbol);
                }
            }
        }
        catch (Exception e)
        {
            string wrappedMessage = $@"""
            /*
            {
                e.Message
            }
            {
                e.StackTrace
            }
            */
""";
            spc.AddSource("PartialClassGeneratorError.g.cs", wrappedMessage);
        }
    }
}