using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

[Generator]
public class EmbedTypeSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the syntax receiver
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(IsTypeSyntaxNode, TransformTypeSyntaxNode)
            .Where(type => type != null);

        // Combine the results and generate source code
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndTypes, GenerateSource!);
    }

    private static bool IsTypeSyntaxNode(SyntaxNode node, CancellationToken ct)
    {
        return node is TypeSyntax || node is StackAllocArrayCreationExpressionSyntax;
    }

    private static TypeSyntax? TransformTypeSyntaxNode(GeneratorSyntaxContext context, CancellationToken ct)
    {
        if (context.Node is TypeSyntax typeSyntax)
        {
            return typeSyntax;
        }

        return null;
    }

    private void GenerateSource(SourceProductionContext context,
        (Compilation Compilation, ImmutableArray<TypeSyntax> Types) input)
    {
        var (compilation, types) = input;
        var typeSymbols = types.Select(t =>
            {
                var model = compilation.GetSemanticModel(t.SyntaxTree);
                var typeInfo = model.GetTypeInfo(t);
                if (typeInfo.Type != null) return typeInfo.Type;

                return null;
            })
            .Where(s => s != null)
            .Select(s => s!)
            .Distinct(SymbolEqualityComparer.Default)
            .Select(s => (ITypeSymbol)s!)
            .Where(symbol =>
            {
                bool IsSerializableType(ITypeSymbol ts)
                {
                    //we dont want void
                    if (ts.SpecialType == SpecialType.System_Void) return false;
                    //we accept string
                    if (ts.SpecialType == SpecialType.System_String) return true;
                    //we want nino type
                    if (ts.IsNinoType()) return true;

                    //we also want unmanaged type
                    if (ts.IsUnmanagedType) return true;

                    //we also want KeyValuePair
                    if (ts.OriginalDefinition.ToDisplayString() ==
                        "System.Collections.Generic.KeyValuePair<TKey, TValue>")
                    {
                        if (ts is INamedTypeSymbol { TypeArguments.Length: 2 } namedTypeSymbol)
                        {
                            return IsSerializableType(namedTypeSymbol.TypeArguments[0]) &&
                                   IsSerializableType(namedTypeSymbol.TypeArguments[1]);
                        }
                    }


                    //if ts implements IList and type parameter is what we want
                    var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                        namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
                    if (i != null)
                        return IsSerializableType(i.TypeArguments[0]);

                    //if ts is Span of what we want
                    if (ts.OriginalDefinition.ToDisplayString() == "System.Span<T>")
                    {
                        if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                        {
                            return IsSerializableType(namedTypeSymbol.TypeArguments[0]);
                        }
                    }

                    //if ts is nullable of what we want
                    if (ts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    {
                        //get type parameter
                        // Get the type argument of Nullable<T>
                        if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                        {
                            return IsSerializableType(namedTypeSymbol.TypeArguments[0]);
                        }
                    }

                    //otherwise, we dont want it
                    return false;
                }

                return IsSerializableType(symbol);
            })
            .ToList();
        //for typeSymbols implements ICollection<KeyValuePair<T1, T2>>, add type KeyValuePair<T1, T2> to typeSymbols
        var kvps = typeSymbols.Select(ts =>
        {
            var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
            if (i != null)
            {
                return i.TypeArguments[0];
            }

            return null;
        }).Where(ts => ts != null).Select(ts => ts!).ToList();
        typeSymbols.AddRange(kvps);
        typeSymbols = typeSymbols
            .Where(ts =>
            {
                //we dont want unmanaged
                if (ts.IsUnmanagedType) return false;
                //we dont want nino type
                if (ts.IsNinoType()) return false;
                //we dont want string
                if (ts.SpecialType == SpecialType.System_String) return false;
                //we dont want ICollection of unmanaged
                var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
                if (i != null)
                {
                    if (i.TypeArguments[0].IsUnmanagedType) return false;
                }

                //we dont want array of unmanaged
                if (ts is IArrayTypeSymbol arrayTypeSymbol)
                {
                    if (arrayTypeSymbol.ElementType.IsUnmanagedType) return false;
                }

                //we dont want nullable of unmanaged
                if (ts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                {
                    //get type parameter
                    // Get the type argument of Nullable<T>
                    if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                    {
                        if (namedTypeSymbol.TypeArguments[0].IsUnmanagedType) return false;
                    }
                }

                //we dont want span of unmanaged
                if (ts.OriginalDefinition.ToDisplayString() == "System.Span<T>")
                {
                    if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                    {
                        if (namedTypeSymbol.TypeArguments[0].IsUnmanagedType) return false;
                    }
                }

                return true;
            }).ToList();
        typeSymbols.Sort((t1, t2) =>
            String.Compare(t1.ToDisplayString(), t2.ToDisplayString(), StringComparison.Ordinal));

        var sb = new StringBuilder();
        HashSet<string> addedType = new HashSet<string>();
        foreach (var type in typeSymbols)
        {
            var typeFullName = type.ToDisplayString();
            if (!addedType.Add(typeFullName)) continue;
            sb.GenerateClassSerializeMethods(typeFullName);

            //if type is nullable
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                if (type is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                {
                    var fullName = namedTypeSymbol.TypeArguments[0].ToDisplayString();
                    GenerateNullableStructMethods(sb, fullName);
                    continue;
                }
            }

            //if type is KeyValuePair
            if (type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.KeyValuePair<TKey, TValue>")
            {
                if (type is INamedTypeSymbol { TypeArguments.Length: 2 } namedTypeSymbol)
                {
                    var type1 = namedTypeSymbol.TypeArguments[0].ToDisplayString();
                    var type2 = namedTypeSymbol.TypeArguments[1].ToDisplayString();
                    GenerateKvpStructMethods(sb, type1, type2);
                    continue;
                }
            }

            //if type is array
            if (type is IArrayTypeSymbol)
            {
                sb.AppendLine(GenerateCollectionSerialization(typeFullName, "Length", "        "));
                continue;
            }

            //if type is ICollection
            var i = type.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
            if (i != null)
            {
                sb.AppendLine(GenerateCollectionSerialization(typeFullName, "Count", "        "));
                continue;
            }

            //if type is Span
            if (type.OriginalDefinition.ToDisplayString() == "System.Span<T>")
            {
                if (type is INamedTypeSymbol { TypeArguments.Length: 1 })
                {
                    sb.AppendLine(GenerateCollectionSerialization(typeFullName, "Length", "        "));
                    continue;
                }
            }

            //otherwise we add a comment of the error type
            sb.AppendLine($"// Type: {typeFullName} is not supported");
        }

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using Nino.Core;
                     using System.Buffers;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace Nino
                     {
                         public static partial class Serializer
                         {
                     {{sb}}    }
                     }
                     """;

        context.AddSource("NinoSerialiserExtension.Ext.g.cs", code);
    }

    private static string GenerateCollectionSerialization(string collectionType, string lengthName, string indent = "",
        string typeParam = "", string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static void Serialize{{typeParam}}(this {{collectionType}} value, ref Writer writer) {{genericConstraint}}
                    {
                        if (value == null)
                        {
                            writer.Write((ushort)TypeCollector.NullTypeId);
                            return;
                        }
                        writer.Write((ushort)TypeCollector.CollectionTypeId);
                        writer.Write(value.{{lengthName}});
                        foreach (var item in value)
                        {
                            item.Serialize(ref writer);
                        }
                    }
                    
                    """;
        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private static void GenerateNullableStructMethods(StringBuilder sb, string typeFullName)
    {
        sb.AppendLine($$"""
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                private static void Serialize(this {{typeFullName}}? value, ref Writer writer)
                                {
                                    if (!value.HasValue)
                                    {
                                        writer.Write((ushort)TypeCollector.NullTypeId);
                                        return;
                                    }
                                    writer.Write((ushort)TypeCollector.NullableTypeId);
                                    value.Value.Serialize(ref writer);
                                }
                                
                        """);
    }

    private static void GenerateKvpStructMethods(StringBuilder sb, string type1, string type2)
    {
        sb.AppendLine($$"""
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                private static void Serialize(this KeyValuePair<{{type1}}, {{type2}}> value, ref Writer writer)
                                {
                                    value.Key.Serialize(ref writer);
                                    value.Value.Serialize(ref writer);
                                }
                                
                        """);
    }
}