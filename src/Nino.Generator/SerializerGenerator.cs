using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

[Generator]
public class SerializerGenerator : IIncrementalGenerator
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

        sb.GenerateClassSerializeMethods("T", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("T?", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("List<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("Span<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("Dictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("IDictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("ICollection<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("bool");
        sb.GenerateClassSerializeMethods("string");

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

                sb.GenerateClassSerializeMethods(typeFullName);

                // only applicable for reference types
                bool isReferenceType = model is ClassDeclarationSyntax;
                if (isReferenceType)
                {
                    sb.AppendLine($"        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                    sb.AppendLine(
                        $"        private static void Serialize(this {typeFullName} value, ref Writer writer)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (value == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                writer.Write((ushort)TypeCollector.NullTypeId);");
                    sb.AppendLine("                return;");
                    sb.AppendLine("            }");
                    sb.AppendLine();
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

                    sb.AppendLine($"        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                    sb.AppendLine(
                        $"        private static void Serialize(this {typeFullName} value, ref Writer writer)");
                    sb.AppendLine("        {");
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

                        sb.AppendLine($"                    {valName}.{name}.Serialize(ref writer);");
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


                    sb.AppendLine($"            switch (value)");
                    sb.AppendLine("            {");

                    foreach (var subType in lst)
                    {
                        string valName = subType.Replace(".", "_").ToLower();
                        sb.AppendLine($"                case {subType} {valName}:");
                        sb.AppendLine($"                    writer.Write((ushort){GetId(subType)});");


                        List<TypeDeclarationSyntax> subTypeModels =
                            models.Where(m => inheritanceMap[subType]
                                .Contains(m.GetTypeFullName())).ToList();

                        var members = models.First(m => m.GetTypeFullName() == subType)
                            .GetNinoTypeMembers(subTypeModels);
                        //get distinct members
                        members = members.Distinct().ToList();
                        WriteMembers(members, valName);
                        sb.AppendLine("                    break;");
                    }

                    sb.AppendLine("                default:");
                    sb.AppendLine($"                    writer.Write((ushort){GetId(typeFullName)});");
                    var defaultMembers = model.GetNinoTypeMembers(null);
                    WriteMembers(defaultMembers, "value");
                    sb.AppendLine("                    break;");
                    sb.AppendLine("            }");
                }
                else
                {
                    sb.AppendLine($"                    writer.Write((ushort){GetId(typeFullName)});");
                    var members = model.GetNinoTypeMembers(null);
                    WriteMembers(members, "value");
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
        if(!string.IsNullOrEmpty(curNamespace))
            curNamespace = $"{curNamespace}_Nino";
        //replace special characters with _
        curNamespace = new string(curNamespace.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

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
                         public static partial class Serializer
                         {
                             private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> BufferWriters =
                                 new ConcurrentQueue<ArrayBufferWriter<byte>>();
                         
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static ArrayBufferWriter<byte> GetBufferWriter()
                             {
                                 if (BufferWriters.Count == 0)
                                 {
                                     return new ArrayBufferWriter<byte>();
                                 }
                         
                                 if (BufferWriters.TryDequeue(out var bufferWriter))
                                 {
                                     return bufferWriter;
                                 }
                         
                                 return new ArrayBufferWriter<byte>();
                             }
                         
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void ReturnBufferWriter(ArrayBufferWriter<byte> bufferWriter)
                             {
                                 bufferWriter.Clear();
                                 BufferWriters.Enqueue(bufferWriter);
                             }

                     {{GeneratePrivateSerializeImplMethodBody("T", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("List<T>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("Span<T>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("ICollection<T>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("T?", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("List<T?>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("Span<T?>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("ICollection<T?>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("string", "        ")}}

                     {{GeneratePrivateSerializeImplMethodBody("bool", "        ")}}
                             
                     {{sb}}    }
                     }
                     """;

        spc.AddSource("NinoSerializerExtension.g.cs", code);
    }


    private static string GeneratePrivateSerializeImplMethodBody(string typeName, string indent = "",
        string typeParam = "",
        string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static void Serialize{{typeParam}}(this {{typeName}} value, ref Writer writer) {{genericConstraint}}
                    {
                        writer.Write(value);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }
}