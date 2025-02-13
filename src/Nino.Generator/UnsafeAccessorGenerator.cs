using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace Nino.Generator;

[Generator]
public class UnsafeAccessorGenerator : IIncrementalGenerator
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

            var sb = new StringBuilder();
            var generatedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var typeSymbol in ninoSymbols)
            {
                try
                {
                    string typeFullName = typeSymbol.GetTypeFullName();
                    bool isPolymorphicType = typeSymbol.IsPolymorphicType();

                    // check if struct is unmanaged
                    if (typeSymbol.IsUnmanagedType && !isPolymorphicType)
                    {
                        continue;
                    }

                    void WriteMembers(List<NinoTypeHelper.NinoMember> members, ITypeSymbol type,
                        string typeName)
                    {
                        if (!generatedTypes.Add(type))
                        {
                            return;
                        }
                        
                        foreach (var (name, declaredType, _, _, isPrivate, isProperty) in members)
                        {
                            if (!isPrivate)
                            {
                                continue;
                            }

                            if (type.IsValueType)
                            {
                                typeName = $"ref {typeName}";
                            }

                            if (isProperty)
                            {
                                sb.AppendLine(
                                    $"        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"get_{name}\")]");
                                sb.AppendLine(
                                    $"        internal extern static {declaredType.ToDisplayString()} __get__{name}__({typeName} @this);");
                                
                                sb.AppendLine(
                                    $"        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"set_{name}\")]");
                                sb.AppendLine(
                                    $"        internal extern static void __set__{name}__({typeName} @this, {declaredType.ToDisplayString()} value);");
                            }
                            else
                            {
                                sb.AppendLine(
                                    $"        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"{name}\")]");
                                sb.AppendLine(
                                    $"        internal extern static ref {declaredType.ToDisplayString()} __{name}__({typeName} @this);");
                            }
                        }
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
                                WriteMembers(members, subTypeSymbol,subTypeSymbol.ToDisplayString());
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
                        WriteMembers(defaultMembers, typeSymbol,typeSymbol.ToDisplayString());
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
            }

            var curNamespace = compilation.AssemblyName!.GetNamespace();

            // generate code
            var code = $$"""
                         // <auto-generated/>

                         using System;
                         using System.Runtime.CompilerServices;
                         
                         namespace {{curNamespace}}
                         {
                         #if NET8_0_OR_GREATER
                             internal static partial class PrivateAccessor
                             {
                         {{sb}}    }
                         #endif
                         }
                         """;

            spc.AddSource("NinoPrivateAccessor.g.cs", code);
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
            spc.AddSource("NinoPrivateAccessor.g.cs", wrappedMessage);
        }
    }
}