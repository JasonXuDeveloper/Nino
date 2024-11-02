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
public class DeserializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeModels = context.GetTypeSyntaxes();
        var compilationAndClasses = context.CompilationProvider.Combine(ninoTypeModels.Collect());
        context.RegisterSourceOutput(compilationAndClasses, (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static void Execute(Compilation compilation, ImmutableArray<TypeSyntax> syntaxes,
        SourceProductionContext spc)
    {
        var ninoSymbols = syntaxes
            .Select(s => s.GetTypeSymbol(compilation))
            .Where(s => s != null)
            .Distinct(SymbolEqualityComparer.Default)
            .Select(s => (ITypeSymbol)s!)
            .Where(s => s.IsNinoType())
            .Where(s => s is not ITypeParameterSymbol)
            .Where(s => s is not INamedTypeSymbol ||
                        (s is INamedTypeSymbol symbol &&
                         (!symbol.IsGenericType ||
                          symbol.TypeArguments.Length ==
                          symbol.TypeParameters.Length &&
                          symbol.TypeArguments.All(t => t is INamedTypeSymbol)
                         )))
            .ToList();

        // get type full names from models (namespaces + type names)
        var typeFullNames = ninoSymbols
            .Where(symbol => symbol.IsReferenceType())
            .Where(symbol => symbol.IsInstanceType())
            .Select(symbol => symbol.GetTypeFullName()).ToList();
        //sort by typename
        typeFullNames.Sort();

        var types = new StringBuilder();
        foreach (var typeFullName in typeFullNames)
        {
            types.AppendLine($"    * {typeFullNames.GetId(typeFullName)} - {typeFullName}");
        }

        var (inheritanceMap,
            subTypeMap,
            topNinoTypes) = ninoSymbols.GetInheritanceMap();

        var sb = new StringBuilder();
        var subTypes = new StringBuilder();

        sb.GenerateClassDeserializeMethods("T", "<T>", "where T : unmanaged");
        sb.GenerateClassDeserializeMethods("T?", "<T>", "where T : unmanaged");
        sb.GenerateClassDeserializeMethods("T[]", "<T>", "where T : unmanaged");
        sb.GenerateClassDeserializeMethods("List<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassDeserializeMethods("Dictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassDeserializeMethods("bool");
        sb.GenerateClassDeserializeMethods("string");

        foreach (var typeSymbol in ninoSymbols)
        {
            try
            {
                string typeFullName = typeSymbol.GetTypeFullName();

                //only generate for top nino types
                if (!topNinoTypes.Contains(typeFullName))
                {
                    var topType = topNinoTypes.FirstOrDefault(t =>
                        subTypeMap.ContainsKey(t) && subTypeMap[t].Contains(typeFullName));
                    if (topType == null)
                        throw new Exception("topType is null");

                    continue;
                }

                // check if struct is unmanged
                if (typeSymbol.IsUnmanagedType)
                {
                    continue;
                }

                sb.GenerateClassDeserializeMethods(typeFullName);

                sb.AppendLine($$"""
                                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                        public static void Deserialize(out {{typeFullName}} value, ref Reader reader)
                                        {
                                """);

                if (!typeSymbol.IsValueType)
                {
                    sb.AppendLine("            reader.Read(out ushort typeId);");
                    sb.AppendLine();
                }

                void WriteMembersWithCustomConstructor(ITypeSymbol symbol, List<CSharpSyntaxNode> members,
                    string typeName, string
                        valName, string[] constructorMember)
                {
                    List<(string, string)> vars = new List<(string, string)>();
                    Dictionary<string, string> args = new Dictionary<string, string>();
                    foreach (var memberDeclarationSyntax in members)
                    {
                        var name = memberDeclarationSyntax.GetMemberName();
                        var declaredType = memberDeclarationSyntax.GetDeclaredTypeFullName(compilation);
                        declaredType = declaredType?.ReplaceTypeParameters(symbol);
                        if (declaredType == null)
                            throw new Exception("declaredType is null");

                        var t = declaredType.ToDisplayString().Select(c => char.IsLetterOrDigit(c) ? c : '_')
                            .Aggregate("", (a, b) => a + b);
                        var tempName = $"{t}_temp_{name}";
                        if (constructorMember.Any(c => c.ToLower().Equals(name?.ToLower())))
                        {
                            sb.AppendLine(
                                $"                    {declaredType.GetDeserializePrefix()}(out {declaredType.ToDisplayString()} {tempName}, ref reader);");
                            args.Add(name!, tempName);
                        }
                        else
                        {
                            sb.AppendLine(
                                $"                    {declaredType.GetDeserializePrefix()}(out {declaredType.ToDisplayString()} {tempName}, ref reader);");

                            // we dont want init-only properties from the primary constructor
                            if (memberDeclarationSyntax is not ParameterSyntax)
                            {
                                vars.Add((name, tempName)!);
                            }
                        }
                    }


                    sb.AppendLine(
                        $"                    {valName} = new {typeName}({string.Join(", ",
                            constructorMember.Select(m =>
                                args[args.Keys
                                    .FirstOrDefault(k =>
                                        k.ToLower()
                                            .Equals(m.ToLower()))]
                            ))}){(vars.Count > 0 ? "" : ";")}");
                    if (vars.Count > 0)
                    {
                        sb.AppendLine($"                    {new string(' ', valName.Length)}   {{");
                        foreach (var (memberName, varName) in vars)
                        {
                            sb.AppendLine(
                                $"                 {new string(' ', valName.Length)}      \t{memberName} = {varName},");
                        }

                        sb.AppendLine($"                    {new string(' ', valName.Length)}   }};");
                    }
                }

                void CreateInstance(List<CSharpSyntaxNode> defaultMembers, ITypeSymbol symbol, string valName,
                    string typeName)
                {
                    //if this subtype contains a custom constructor, use it
                    //go through all constructors and find the one with the NinoConstructor attribute
                    //get constructors of the symbol
                    var constructors = (symbol as INamedTypeSymbol)?.Constructors.ToList();

                    if (constructors == null)
                    {
                        sb.AppendLine(
                            $"                    // no constructor found, symbol is not a named type symbol but a {symbol.GetType()}");
                        sb.AppendLine(
                            $"                    throw new InvalidOperationException(\"No constructor found for {typeName}\");");
                        return;
                    }

                    IMethodSymbol? constructor = null;

                    // if typesymbol is a record, try get the primary constructor
                    if (symbol.IsRecord)
                    {
                        constructor = constructors.FirstOrDefault(c => c.Parameters.Length == 0 || c.Parameters.All(p =>
                            defaultMembers.Any(m => m.GetMemberName() == p.Name)));
                    }

                    if (constructor == null)
                        constructor = constructors.OrderBy(c => c.Parameters.Length).FirstOrDefault();

                    if (constructor == null)
                    {
                        sb.AppendLine("                    // no constructor found");
                        sb.AppendLine(
                            $"                    throw new InvalidOperationException(\"No constructor found for {typeName}\");");
                        return;
                    }

                    var custom = constructors.FirstOrDefault(c => c.GetAttributes().Any(a =>
                        a.AttributeClass != null &&
                        a.AttributeClass.ToDisplayString().EndsWith("NinoConstructorAttribute")));
                    if (custom != null)
                    {
                        constructor = custom;
                    }

                    sb.AppendLine($"                    // use {constructor.ToDisplayString()}");

                    var attr = constructor.GetNinoConstructorAttribute();
                    string[] args;
                    if (attr != null)
                    {
                        //attr is         [NinoConstructor(nameof(a), nameof(b), nameof(c), ...)]
                        //we need to get a, b, c, ...
                        var args0 = attr.ConstructorArguments[0].Values;
                        //should be a string array
                        args = args0.Select(a =>
                            a.Value as string).ToArray()!;
                    }
                    else
                    {
                        args = constructor.Parameters.Select(p => p.Name).ToArray();
                    }

                    WriteMembersWithCustomConstructor(symbol, defaultMembers, typeName, valName, args);
                }

                if (!subTypeMap.TryGetValue(typeFullName, out var lst))
                {
                    lst = new List<string>();
                }

                //sort lst by how deep the inheritance is (i.e. how many levels of inheritance), the deepest first
                lst.Sort((a, b) =>
                {
                    int aCount = inheritanceMap[a].Count;
                    int bCount = inheritanceMap[b].Count;
                    return bCount.CompareTo(aCount);
                });

                // only applicable for reference types
                bool isReferenceType = typeSymbol.IsReferenceType();
                if (isReferenceType)
                {
                    sb.AppendLine("            switch (typeId)");
                    sb.AppendLine("            {");
                    sb.AppendLine("""
                                                  case TypeCollector.NullTypeId:
                                                      value = null;
                                                      return;
                                  """);
                }

                foreach (var subType in lst)
                {
                    var subTypeSymbol = ninoSymbols.First(s => s.GetTypeFullName() == subType);
                    subTypes.AppendLine(
                        subType.GeneratePublicDeserializeMethodBodyForSubType(typeFullName, "        "));
                    if (subTypeSymbol.IsInstanceType())
                    {
                        string valName = subType.Replace("global::", "").Replace(".", "_").ToLower();
                        int id = typeFullNames.GetId(subType);
                        sb.AppendLine($"                case {id}:");
                        sb.AppendLine("                {");

                        //get members
                        List<ITypeSymbol> subTypeSymbols =
                            ninoSymbols.Where(m => inheritanceMap[subType]
                                .Contains(m.GetTypeFullName())).ToList();

                        var members = subTypeSymbol.GetNinoTypeMembers(subTypeSymbols);
                        //get distinct members
                        members = members.Distinct().ToList();

                        sb.AppendLine($"                    {subType} {valName};");

                        CreateInstance(members, subTypeSymbol, valName, subType);

                        sb.AppendLine($"                    value = {valName};");
                        sb.AppendLine("                    return;");
                        sb.AppendLine("                }");
                    }
                }

                if (typeSymbol.IsInstanceType())
                {
                    if (isReferenceType)
                    {
                        sb.AppendLine($"                case {typeFullNames.GetId(typeFullName)}:");
                        sb.AppendLine("                {");
                    }

                    var defaultMembers = typeSymbol.GetNinoTypeMembers(null);
                    string valName = "value";
                    CreateInstance(defaultMembers, typeSymbol, valName, typeFullName);

                    if (isReferenceType)
                    {
                        sb.AppendLine("                    return;");
                        sb.AppendLine("                }");
                    }
                }

                if (isReferenceType)
                {
                    sb.AppendLine("                default:");
                    sb.AppendLine(
                        "                    throw new InvalidOperationException($\"Invalid type id {typeId}\");");
                    sb.AppendLine("            }");
                }

                sb.AppendLine("        }");
                sb.AppendLine();
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

        var curNamespace = $"{compilation.AssemblyName!}";
        if (!string.IsNullOrEmpty(curNamespace))
            curNamespace = $"{curNamespace}_";
        if (!char.IsLetter(curNamespace[0]))
            curNamespace = $"_{curNamespace}";
        //replace special characters with _
        curNamespace = new string(curNamespace.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        curNamespace += "Nino";

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
                         /*
                         * Type Id - Type
                         * 0 - Null
                         * 1 - System.String
                         * 2 - System.ICollection
                         * 3 - System.Nullable
                     {{types}}    */
                         public static partial class Deserializer
                         {

                     {{GeneratePrivateDeserializeImplMethodBody("T", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("T[]", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("List<T>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("IList<T>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("ICollection<T>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("T?", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("T?[]", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateDeserializeImplMethodBody("List<T?>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("IList<T?>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("ICollection<T?>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("Dictionary<TKey, TValue>", "        ", "<TKey, TValue>", "where TKey : unmanaged where TValue : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("IDictionary<TKey, TValue>", "        ", "<TKey, TValue>", "where TKey : unmanaged where TValue : unmanaged")}}

                     {{GeneratePrivateDeserializeImplMethodBody("string", "        ")}}

                     {{GeneratePrivateDeserializeImplMethodBody("bool", "        ")}}
                             
                     {{sb}}
                     {{subTypes}}    }
                     }
                     """;

        spc.AddSource("NinoDeserializerExtension.g.cs", code);
    }

    private static string GeneratePrivateDeserializeImplMethodBody(string typeName, string indent = "",
        string typeParam = "",
        string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize{{typeParam}}(out {{typeName}} value, ref Reader reader) {{genericConstraint}}
                    {
                        reader.Read(out value);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }
}