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

                var typeSymbol = compilation.GetTypeSymbol(typeFullName, models);

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
                            sb.AppendLine($"                    {declaredType.GetDeserializePrefix()}(out {valName}.{name}, ref reader);");
                        else
                        {
                            var t = declaredType.ToDisplayString().Select(c => char.IsLetterOrDigit(c) ? c : '_')
                                .Aggregate("", (a, b) => a + b);
                            var tempName = $"{t}_temp_{name}";
                            sb.AppendLine(
                                $"                    {declaredType.GetDeserializePrefix()}(out {declaredType.ToDisplayString()} {tempName}, ref reader);");
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

                // only applicable for reference types
                bool isReferenceType = typeSymbol.IsReferenceType;
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
                    var subTypeSymbol = compilation.GetTypeSymbol(subType, models);
                    if (!subTypeSymbol.IsAbstract)
                    {
                        subTypes.AppendLine(
                            subType.GeneratePublicDeserializeMethodBodyForSubType(typeFullName, "        "));
                        string valName = subType.Replace(".", "_").ToLower();
                        int id = GetId(subType);
                        sb.AppendLine($"                case {id}:");
                        sb.AppendLine("                {");
                        sb.AppendLine($"                    {subType} {valName} = new {subType}();");


                        List<TypeDeclarationSyntax> subTypeModels =
                            models.Where(m => inheritanceMap[subType]
                                .Contains(m.GetTypeFullName())).ToList();

                        var members = models.First(m => m.GetTypeFullName() == subType).GetNinoTypeMembers(subTypeModels);
                        //get distinct members
                        members = members.Distinct().ToList();
                        WriteMembers(members, valName);
                        sb.AppendLine($"                    value = {valName};");
                        sb.AppendLine("                    return;");
                        sb.AppendLine("                }");
                    }
                }

                if (!typeSymbol.IsAbstract)
                {
                    if (isReferenceType)
                    {
                        sb.AppendLine($"                case {GetId(typeFullName)}:");
                        sb.AppendLine("                {");
                    }
                    sb.AppendLine($"                    value = new {typeFullName}();");
                    var defaultMembers = model.GetNinoTypeMembers(null);
                    WriteMembers(defaultMembers, "value");
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
                sb.AppendLine($"// Error: {e.Message} for type {model.GetTypeFullName()}: {e.StackTrace}");
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