using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

[Generator]
public class DeserializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Get all classes/structs that has attribute NinoType
        var ninoTypeModels = context.GetNinoTypeModels();
        var compilationAndClasses = context.CompilationProvider.Combine(ninoTypeModels.Collect());
        context.RegisterSourceOutput(compilationAndClasses, (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static void Execute(Compilation compilation, ImmutableArray<TypeDeclarationSyntax> models,
        SourceProductionContext spc)
    {
        // get type full names from models (namespaces + type names)
        var typeFullNames = models.Where(m => m is ClassDeclarationSyntax)
            .Select(m => m.GetTypeFullName()).ToList();
        //sort by typename
        typeFullNames.Sort();

        int GetId(string typeFullName)
        {
            int index = typeFullNames.IndexOf(typeFullName);
            return index + 4;
        }

        var types = new StringBuilder();
        foreach (var typeFullName in typeFullNames)
        {
            types.AppendLine($"    * {GetId(typeFullName)} - {typeFullName}");
        }

        var ninoTypeModels = models.Select(m => m.GetTypeFullName()).ToImmutableArray();
        Dictionary<string, List<string>> inheritanceMap = new(); // type -> all base types
        Dictionary<string, List<string>> subTypeMap = new(); //top type -> all subtypes
        //get top nino types (i.e. types that are not inherited by other nino types)
        var topNinoTypes = ninoTypeModels.Where(ninoTypeFullName =>
        {
            List<string> inheritedTypes = new();
            inheritanceMap.Add(ninoTypeFullName, inheritedTypes);
            INamedTypeSymbol? subTypeSymbol = compilation.GetTypeByMetadataName(ninoTypeFullName);
            if (subTypeSymbol == null)
            {
                //check if is a nested type
                TypeDeclarationSyntax? typeDeclarationSyntax = models.FirstOrDefault(m =>
                    string.Equals(m.GetTypeFullName(), ninoTypeFullName, StringComparison.Ordinal));

                if (typeDeclarationSyntax == null)
                    return false;

                var ninoTypeFullName2 = typeDeclarationSyntax.GetTypeFullName("+");
                subTypeSymbol = compilation.GetTypeByMetadataName(ninoTypeFullName2);
                if (subTypeSymbol == null)
                    return false;
            }

            //get toppest ninotype base type
            INamedTypeSymbol? baseType = subTypeSymbol;
            while (baseType.BaseType != null)
            {
                baseType = baseType.BaseType;
                string baseTypeFullName = baseType.ToString();
                if (ninoTypeModels.Contains(baseTypeFullName))
                {
                    if (subTypeMap.ContainsKey(baseTypeFullName))
                    {
                        subTypeMap[baseTypeFullName].Add(ninoTypeFullName);
                    }
                    else
                    {
                        subTypeMap.Add(baseTypeFullName, [ninoTypeFullName]);
                    }

                    inheritedTypes.Add(baseTypeFullName);
                }
                else
                {
                    break;
                }
            }

            return inheritedTypes.Count == 0;
        }).ToImmutableArray();

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

        foreach (var model in models)
        {
            try
            {
                string typeFullName = model.GetTypeFullName();

                //only generate for top nino types
                if (!topNinoTypes.Contains(typeFullName))
                {
                    var topType = topNinoTypes.FirstOrDefault(t =>
                        subTypeMap.ContainsKey(t) && subTypeMap[t].Contains(typeFullName));
                    if (topType == null)
                        throw new Exception("topType is null");

                    continue;
                }

                sb.GenerateClassDeserializeMethods(typeFullName);

                // only applicable for reference types
                bool isReferenceType = model is ClassDeclarationSyntax;
                if (isReferenceType)
                {
                    sb.AppendLine($$"""
                                            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
                                            private static void Deserialize(out {{typeFullName}} value, ref Reader reader)
                                            {
                                                value = null;
                                                reader.Read(out ushort typeId);
                                                if (typeId == TypeCollector.NullTypeId)
                                                {
                                                    return;
                                                }
                                            
                                    """);
                }
                else
                {
                    var structSymbol = compilation.GetTypeByMetadataName(typeFullName);
                    if (structSymbol == null)
                    {
                        //check if is a nested type
                        TypeDeclarationSyntax? typeDeclarationSyntax = models.FirstOrDefault(m =>
                            string.Equals(m.GetTypeFullName(), typeFullName, StringComparison.Ordinal));
                        if (typeDeclarationSyntax == null)
                            throw new Exception("typeDeclarationSyntax is null");

                        var typeFullName2 = typeDeclarationSyntax.GetTypeFullName("+");
                        structSymbol = compilation.GetTypeByMetadataName(typeFullName2);
                        if (structSymbol == null)
                            throw new Exception("structSymbol is null");
                    }

                    // check if struct is unmanged
                    if (structSymbol.IsUnmanagedType)
                    {
                        continue;
                    }

                    sb.AppendLine($$"""
                                            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
                                            private static void Deserialize(out {{typeFullName}} value, ref Reader reader)
                                            {
                                                value = default;
                                                reader.Read(out ushort typeId);
                                            
                                    """);
                }

                void WriteMembers(List<MemberDeclarationSyntax> members, string valName)
                {
                    foreach (var memberDeclarationSyntax in members)
                    {
                        var name = memberDeclarationSyntax.GetMemberName();
                        // see if declaredType is a NinoType
                        var declaredType = memberDeclarationSyntax.GetDeclaredTypeFullName(compilation);
                        //check if declaredType is a NinoType
                        if (declaredType == null)
                            throw new Exception("declaredType is null");

                        if (memberDeclarationSyntax is FieldDeclarationSyntax)
                            sb.AppendLine($"                    Deserialize(out {valName}.{name}, ref reader);");
                        else
                        {
                            var tempName = $"temp_{name}";
                            sb.AppendLine(
                                $"                    Deserialize(out {declaredType.ToDisplayString()} {tempName}, ref reader);");
                            sb.AppendLine($"                    {valName}.{name} = {tempName};");
                        }
                    }
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


                sb.AppendLine($"            switch (typeId)");
                sb.AppendLine("            {");

                foreach (var subType in lst)
                {
                    subTypes.AppendLine(
                        subType.GeneratePublicDeserializeMethodBodyForSubType(typeFullName, "        "));
                    string valName = subType.Replace(".", "_").ToLower();
                    int id = GetId(subType);
                    sb.AppendLine($"                case {id}:");
                    sb.AppendLine($"                    {subType} {valName} = new {subType}();");


                    List<TypeDeclarationSyntax> subTypeModels =
                        models.Where(m => inheritanceMap[subType]
                            .Contains(m.GetTypeFullName())).ToList();

                    var members = models.First(m => m.GetTypeFullName() == subType).GetNinoTypeMembers(subTypeModels);
                    //get distinct members
                    members = members.Distinct().ToList();
                    WriteMembers(members, valName);
                    sb.AppendLine($"                    value = {valName};");
                    sb.AppendLine("                    break;");
                }

                sb.AppendLine($"                case {GetId(typeFullName)}:");
                sb.AppendLine($"                    value = new {typeFullName}();");
                var defaultMembers = model.GetNinoTypeMembers(null);
                WriteMembers(defaultMembers, "value");
                sb.AppendLine("                    break;");

                sb.AppendLine("                default:");
                sb.AppendLine(
                    "                    throw new InvalidOperationException($\"Invalid type id {typeId}\");");

                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            catch (Exception e)
            {
                sb.AppendLine($"// Error: {e.Message} for type {model.GetTypeFullName()}: {e.StackTrace}");
            }
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
                    private static void Deserialize{{typeParam}}(out {{typeName}} value, ref Reader reader) {{genericConstraint}}
                    {
                        reader.Read(out value);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }
}