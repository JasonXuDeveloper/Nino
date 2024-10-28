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
        var typeFullNames = models.Where(m => m.IsReferenceType())
            .Select(m => m.GetTypeFullName()).ToList();
        //sort by typename
        typeFullNames.Sort();

        var types = new StringBuilder();
        foreach (var typeFullName in typeFullNames)
        {
            types.AppendLine($"    * {typeFullNames.GetId(typeFullName)} - {typeFullName}");
        }

        var (inheritanceMap,
            subTypeMap,
            topNinoTypes) = compilation.GetInheritanceMap(models);

        var sb = new StringBuilder();

        sb.GenerateClassSerializeMethods("T?", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("List<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("Dictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("IDictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("ICollection<T>", "<T>", "where T : unmanaged");
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

                var typeSymbol = compilation.GetTypeSymbol(typeFullName, models);

                // check if struct is unmanged
                if (typeSymbol.IsUnmanagedType)
                {
                    continue;
                }

                sb.GenerateClassSerializeMethods(typeFullName);

                sb.AppendLine($"        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(
                    $"        public static void Serialize(this {typeFullName} value, ref Writer writer)");
                sb.AppendLine("        {");
                // only applicable for reference types
                bool isReferenceType = model.IsReferenceType();
                if (isReferenceType)
                {
                    sb.AppendLine("            if (value == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                writer.Write(TypeCollector.NullTypeId);");
                    sb.AppendLine("                return;");
                    sb.AppendLine("            }");
                    sb.AppendLine();
                }

                void WriteMembers(List<CSharpSyntaxNode> members, string valName)
                {
                    foreach (var memberDeclarationSyntax in members)
                    {
                        var name = memberDeclarationSyntax.GetMemberName();
                        // see if declaredType is a NinoType
                        var declaredType = memberDeclarationSyntax.GetDeclaredTypeFullName(compilation);
                        //check if declaredType is a NinoType
                        if (declaredType == null)
                            throw new Exception("declaredType is null");

                        sb.AppendLine(
                            $"                    {declaredType.GetSerializePrefix()}({valName}.{name}, ref writer);");
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
                        var subTypeSymbol = compilation.GetTypeSymbol(subType, models);
                        if (subTypeSymbol.IsInstanceType())
                        {
                            string valName = subType.Replace(".", "_").ToLower();
                            sb.AppendLine($"                case {subType} {valName}:");
                            sb.AppendLine($"                    writer.Write((ushort){typeFullNames.GetId(subType)});");


                            List<TypeDeclarationSyntax> subTypeModels =
                                models.Where(m => inheritanceMap[subType]
                                    .Contains(m.GetTypeFullName())).ToList();

                            var members = models.First(m => m.GetTypeFullName() == subType)
                                .GetNinoTypeMembers(subTypeModels);
                            //get distinct members
                            members = members.Distinct().ToList();
                            WriteMembers(members, valName);
                            sb.AppendLine("                    return;");
                        }
                    }

                    if (typeSymbol.IsInstanceType())
                    {
                        sb.AppendLine("                default:");
                        sb.AppendLine(
                            $"                    writer.Write((ushort){typeFullNames.GetId(typeFullName)});");
                        var defaultMembers = model.GetNinoTypeMembers(null);
                        WriteMembers(defaultMembers, "value");
                        sb.AppendLine("                    return;");
                    }

                    sb.AppendLine("            }");
                }
                else if (typeSymbol.IsInstanceType())
                {
                    if (!typeSymbol.IsValueType)
                    {
                        sb.AppendLine(
                            $"                    writer.Write((ushort){typeFullNames.GetId(typeFullName)});");
                    }

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
                     #if NET8_0_OR_GREATER
                                 bufferWriter.ResetWrittenCount();
                     #else
                                 bufferWriter.Clear();
                     #endif
                                 BufferWriters.Enqueue(bufferWriter);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize<T>(this T value) where T : unmanaged
                             {
                                 byte[] ret = new byte[Unsafe.SizeOf<T>()];
                                 Unsafe.WriteUnaligned(ref ret[0], value);
                                 return ret;
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void Serialize<T>(this T value, IBufferWriter<byte> bufferWriter) where T : unmanaged
                             {
                                 Writer writer = new Writer(bufferWriter);
                                 value.Serialize(ref writer);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize<T>(this T[] value) where T : unmanaged
                             {
                                return Serialize((Span<T>)value);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void Serialize<T>(this T[] value, IBufferWriter<byte> bufferWriter) where T : unmanaged
                             {
                                 Writer writer = new Writer(bufferWriter);
                                 value.Serialize(ref writer);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize<T>(this Span<T> value) where T : unmanaged
                             {
                                if (value == null)
                                    return new byte[2];
                                int byteLength = value.Length * Unsafe.SizeOf<T>();
                        #if NET6_0_OR_GREATER
                                byte[] ret = GC.AllocateUninitializedArray<byte>(byteLength + 6);
                        #else
                                byte[] ret = new byte[byteLength + 6];
                        #endif
                                Unsafe.WriteUnaligned(ref ret[0], (ushort)TypeCollector.CollectionTypeId);
                                Unsafe.WriteUnaligned(ref ret[2], value.Length);
                                Unsafe.CopyBlockUnaligned(ref ret[6], ref Unsafe.As<T, byte>(ref value[0]), (uint)byteLength);
                                return ret;
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void Serialize<T>(this Span<T> value, IBufferWriter<byte> bufferWriter) where T : unmanaged
                             {
                                 Writer writer = new Writer(bufferWriter);
                                 value.Serialize(ref writer);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize(this bool value)
                             {
                                 if (value)
                                     return new byte[1] { 1 };
                                
                                 return new byte[1] { 0 };
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void Serialize(this bool value, IBufferWriter<byte> bufferWriter)
                             {
                                 Writer writer = new Writer(bufferWriter);
                                 value.Serialize(ref writer);
                             }

                     {{GeneratePrivateSerializeImplMethodBody("T", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("T[]", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("List<T>", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("Span<T>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("ICollection<T>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GeneratePrivateSerializeImplMethodBody("T?", "        ", "<T>", "where T : unmanaged")}}

                     {{GeneratePrivateSerializeImplMethodBody("List<T?>", "        ", "<T>", "where T : unmanaged")}}

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
                    public static void Serialize{{typeParam}}(this {{typeName}} value, ref Writer writer) {{genericConstraint}}
                    {
                        writer.Write(value);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }
}