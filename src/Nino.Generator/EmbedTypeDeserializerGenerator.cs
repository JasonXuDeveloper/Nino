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
public class EmbedTypeDeserializerGenerator : IIncrementalGenerator
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
        if (!compilation.IsValidCompilation()) return;

        var typeSymbols = types.Select(t =>
            {
                var model = compilation.GetSemanticModel(t.SyntaxTree);
                var typeInfo = model.GetTypeInfo(t);
                if (typeInfo.Type != null) return typeInfo.Type;

                return null;
            })
            .Concat(NinoTypeHelper.GetAllNinoRequiredTypes(compilation))
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
                //we dont want any of the type arguments to be a type parameter
                if (ts is INamedTypeSymbol s)
                {
                    bool IsTypeParameter(ITypeSymbol typeSymbol)
                    {
                        if (typeSymbol.TypeKind == TypeKind.TypeParameter) return true;
                        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
                        {
                            return namedTypeSymbol.TypeArguments.Any(IsTypeParameter);
                        }

                        return false;
                    }

                    if (s.TypeArguments.Any(IsTypeParameter)) return false;
                }

                //we dont want IList of unmanaged
                var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == "IList" && namedTypeSymbol.TypeArguments.Length == 1);
                if (i != null)
                {
                    if (i.TypeArguments[0].IsUnmanagedType) return false;
                }

                //we dont want array of unmanaged
                if (ts is IArrayTypeSymbol arrayTypeSymbol)
                {
                    if (arrayTypeSymbol.ElementType.TypeKind == TypeKind.TypeParameter) return false;
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
                        //we also dont want nullable of reference type, as it already has a null check
                        if (namedTypeSymbol.TypeArguments[0].IsReferenceType) return false;
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

                //we dont want IDictionary of unmanaged
                i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == "IDictionary" && namedTypeSymbol.TypeArguments.Length == 2);
                if (i != null)
                {
                    if (i.TypeArguments[0].IsUnmanagedType && i.TypeArguments[1].IsUnmanagedType) return false;
                }

                return true;
            }).ToList();
        typeSymbols.Sort((t1, t2) =>
            String.Compare(t1.ToDisplayString(), t2.ToDisplayString(), StringComparison.Ordinal));

        var sb = new StringBuilder();
        HashSet<string> addedType = new HashSet<string>();
        HashSet<string> addedElemType = new HashSet<string>();
        foreach (var type in typeSymbols)
        {
            var typeFullName = type.ToDisplayString();
            if (!addedType.Add(typeFullName)) continue;

            //if type is nullable
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                if (type is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                {
                    var fullName = namedTypeSymbol.TypeArguments[0].ToDisplayString();
                    GenerateNullableStructMethods(sb, namedTypeSymbol.TypeArguments[0].GetDeserializePrefix(),
                        fullName);
                    sb.GenerateClassDeserializeMethods(typeFullName);
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
                    GenerateKvpStructMethods(sb, namedTypeSymbol.TypeArguments[0].GetDeserializePrefix(), type1,
                        namedTypeSymbol.TypeArguments[1].GetDeserializePrefix(), type2);
                    sb.GenerateClassDeserializeMethods(typeFullName);
                    continue;
                }
            }

            //if type implements IDictionary only
            if (type.AllInterfaces.Any(namedTypeSymbol =>
                    namedTypeSymbol.Name == "IDictionary" && namedTypeSymbol.TypeArguments.Length == 2))
            {
                if (type is INamedTypeSymbol { TypeArguments.Length: 2 } namedTypeSymbol)
                {
                    var type1 = namedTypeSymbol.TypeArguments[0].ToDisplayString();
                    var type2 = namedTypeSymbol.TypeArguments[1].ToDisplayString();
                    sb.AppendLine(GenerateDictionarySerialization(type1, type2, typeFullName, "        "));
                    sb.GenerateClassDeserializeMethods(typeFullName);
                    continue;
                }
            }

            //if type is array
            if (type is IArrayTypeSymbol)
            {
                var elemType = ((IArrayTypeSymbol)type).ElementType.ToDisplayString();
                if (addedElemType.Add(elemType))
                    sb.AppendLine(GenerateArraySerialization(
                        ((IArrayTypeSymbol)type).ElementType.GetDeserializePrefix(),
                        elemType, "        "));
                sb.GenerateClassDeserializeMethods(typeFullName);
                continue;
            }

            //ICollection<T>
            if (type is INamedTypeSymbol { TypeArguments.Length: 1 } s)
            {
                var elemType = s.TypeArguments[0].ToDisplayString();
                if (addedElemType.Add(elemType))
                    sb.AppendLine(GenerateArraySerialization(s.TypeArguments[0].GetDeserializePrefix(),
                        elemType,
                        "        "));
                if (type.TypeKind != TypeKind.Interface)
                {
                    //if is List<T>
                    if (type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
                    {
                        sb.AppendLine(GenerateListSerialization(s.TypeArguments[0].GetDeserializePrefix(), elemType,
                            typeFullName, typeFullName, "        "));
                    }
                    else
                    {
                        sb.AppendLine(GenerateCollectionSerialization(s.TypeArguments[0].GetDeserializePrefix(),
                            elemType,
                            typeFullName, typeFullName, "        "));
                    }
                }
                else
                {
                    var newFullName = $"System.Collections.Generic.List<{elemType}>";
                    sb.AppendLine(GenerateListSerialization(s.TypeArguments[0].GetDeserializePrefix(), elemType,
                        typeFullName, newFullName, "        ", true));
                }

                sb.GenerateClassDeserializeMethods(typeFullName);
                continue;
            }

            //otherwise we add a comment of the error type
            sb.AppendLine($"// Type: {typeFullName} is not supported");
        }

        var curNamespace = compilation.AssemblyName!.GetNamespace();

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using global::Nino.Core;
                     using System.Buffers;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class Deserializer
                         {
                     {{sb}}    }
                     }
                     """;

        context.AddSource("NinoDeserializerExtension.Ext.g.cs", code);
    }

    private static string GenerateArraySerialization(string prefix, string elemType, string indent)
    {
        var creationDecl = elemType.EndsWith("[]")
            ? elemType.Insert(elemType.IndexOf("[]", StringComparison.Ordinal), "[length]")
            : $"{elemType}[length]";
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(out {{elemType}}[] value, ref Reader reader)
                    {
                    #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                         if (reader.Eof)
                         {
                            value = default;
                            return;
                         }
                    #endif
                        
                        if (!reader.ReadCollectionHeader(out var length))
                        {
                            value = default;
                            return;
                        }
                        
                        value = new {{creationDecl}};
                        var span = value.AsSpan();
                        for (int i = 0; i < length; i++)
                        {
                            {{prefix}}(out {{elemType}} val, ref reader);
                            span[i] = val;
                        }
                    }

                    """;
        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private static string GenerateDictionarySerialization(string type1, string type2, string typeFullName,
        string indent = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(out {{typeFullName}} value, ref Reader reader)
                    {
                    #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                         if (reader.Eof)
                         {
                            value = default;
                            return;
                         }
                    #endif
                        
                        if (!reader.ReadCollectionHeader(out var length))
                        {
                            value = default;
                            return;
                        }
                        
                        value = new {{typeFullName}}({{(typeFullName.StartsWith("System.Collections.Generic.Dictionary") ? "length" : "")}});
                        for (int i = 0; i < length; i++)
                        {
                            Deserialize(out KeyValuePair<{{type1}}, {{type2}}> kvp, ref reader);
                            value[kvp.Key] = kvp.Value;
                        }
                    }

                    """;
        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private static string GenerateCollectionSerialization(string prefix, string elemType, string sigTypeFullname,
        string typeFullname,
        string indent)
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(out {{sigTypeFullname}} value, ref Reader reader)
                    {
                    #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                         if (reader.Eof)
                         {
                            value = default;
                            return;
                         }
                    #endif
                        
                        {{prefix}}(out {{elemType}}[] arr, ref reader);
                        if (arr == null)
                        {
                            value = default;
                            return;
                        }
                        
                        value = new {{typeFullname}}(arr);
                    }

                    """;
        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private static string GenerateListSerialization(string prefix, string elemType, string sigTypeFullname,
        string typeFullname,
        string indent,
        bool isInterface = false)
    {
        var decl = isInterface ? $"var ret = new {typeFullname}(0);" : $"value = new {typeFullname}(0);";
        var val = isInterface ? "ret" : "value";
        var end = isInterface ? "value = ret;" : "";
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(out {{sigTypeFullname}} value, ref Reader reader)
                    {
                    #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                         if (reader.Eof)
                         {
                            value = default;
                            return;
                         }
                    #endif
                        
                        {{prefix}}(out {{elemType}}[] arr, ref reader);
                        if (arr == null)
                        {
                            value = default;
                            return;
                        }
                        
                        {{decl}}
                        ref var lst = ref Unsafe.As<List<{{elemType}}>, TypeCollector.ListView<{{elemType}}>>(ref {{val}});
                        lst._size = arr.Length;
                        lst._items = arr;
                        {{end}}
                    }

                    """;
        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private static void GenerateNullableStructMethods(StringBuilder sb, string prefix, string typeFullName)
    {
        sb.AppendLine($$"""
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static void Deserialize(out {{typeFullName}}? value, ref Reader reader)
                                {
                                #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                     if (reader.Eof)
                                     {
                                        value = default;
                                        return;
                                     }
                                #endif
                                    
                                    reader.Read(out bool hasValue);
                                    if (!hasValue)
                                    {
                                        value = default;
                                        return;
                                    }
                                    
                                    {{prefix}}(out {{typeFullName}} ret, ref reader);
                                    value = ret;
                                }
                                
                        """);
    }

    private static void GenerateKvpStructMethods(StringBuilder sb, string prefix1, string type1, string prefix2,
        string type2)
    {
        sb.AppendLine($$"""
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static void Deserialize(out KeyValuePair<{{type1}}, {{type2}}> value, ref Reader reader)
                                {
                                #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                     if (reader.Eof)
                                     {
                                        value = default;
                                        return;
                                     }
                                #endif
                                    
                                    {{type1}} key;
                                    {{type2}} val;
                                    {{prefix1}}(out key, ref reader);
                                    {{prefix2}}(out val, ref reader);
                                    value = new KeyValuePair<{{type1}}, {{type2}}>(key, val);
                                }
                                
                        """);
    }
}