using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Template;

namespace Nino.Generator.Common;

public class SerializerGenerator(
    Compilation compilation,
    List<ITypeSymbol> ninoSymbols,
    Dictionary<string, List<string>> inheritanceMap,
    Dictionary<string, List<string>> subTypeMap,
    ImmutableArray<string> topNinoTypes)
    : NinoCommonGenerator(compilation, ninoSymbols, inheritanceMap, subTypeMap, topNinoTypes)
{
    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;
        var ninoSymbols = NinoSymbols;
        var inheritanceMap = InheritanceMap;
        var subTypeMap = SubTypeMap;

        var sb = new StringBuilder();

        sb.GenerateClassSerializeMethods("Span<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("Span<T?>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("T[]", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("T?", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("T?[]", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("List<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("List<T?>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("Dictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("IDictionary<TKey, TValue>", "<TKey, TValue>",
            "where TKey : unmanaged where TValue : unmanaged");
        sb.GenerateClassSerializeMethods("ICollection<T>", "<T>", "where T : unmanaged");
        sb.GenerateClassSerializeMethods("string");

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

                sb.GenerateClassSerializeMethods(typeFullName);

                sb.AppendLine($"        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(
                    $"        public static void Serialize(this {typeFullName} value, ref Writer writer)");
                sb.AppendLine("        {");
                if (isPolymorphicType && typeSymbol.IsReferenceType)
                {
                    sb.AppendLine("            switch (value)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                case null:");
                    sb.AppendLine("                    writer.Write(TypeCollector.Null);");
                    sb.AppendLine("                    return;");
                }

                void WriteMembers(List<NinoTypeHelper.NinoMember> members, ITypeSymbol type,
                    string valName)
                {
                    foreach (var (name, declaredType, attrs, _, isPrivate, isProperty) in members)
                    {
                        var val = $"{valName}.{name}";

                        if (isPrivate)
                        {
                            var accessName = valName;
                            if (type.IsValueType)
                            {
                                accessName = $"ref {valName}";
                            }

                            val = isProperty
                                ? $"PrivateAccessor.__get__{name}__({accessName})"
                                : $"PrivateAccessor.__{name}__({accessName})";
                            var legacyVal = $"{valName}.__nino__generated__{name}";
                            val = $"""

                                   #if NET8_0_OR_GREATER
                                                           {val}
                                   #else
                                                           {legacyVal}
                                   #endif 
                                                       
                                   """;
                        }

                        //check if the typesymbol declaredType is string
                        if (declaredType.SpecialType == SpecialType.System_String)
                        {
                            //check if this member is annotated with [NinoUtf8]
                            var isUtf8 = attrs.Any(a => a.AttributeClass!.Name == "NinoUtf8Attribute");

                            sb.AppendLine(
                                isUtf8
                                    ? $"                    writer.WriteUtf8({val});"
                                    : $"                    writer.Write({val});");

                            continue;
                        }

                        sb.AppendLine(
                            $"                    {declaredType.GetSerializePrefix()}({val}, ref writer);");
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
                            string valName = subType.Replace("global::", "").Replace(".", "_").ToLower();
                            sb.AppendLine($"                case {subType} {valName}:");
                            sb.AppendLine(
                                $"                    writer.Write(NinoTypeConst.{subTypeSymbol.GetTypeFullName().GetTypeConstName()});");
                            if (subTypeSymbol.IsUnmanagedType)
                            {
                                sb.AppendLine(
                                    $"                    writer.Write({valName});");
                            }
                            else
                            {
                                List<ITypeSymbol> subTypeParentSymbols =
                                    ninoSymbols.Where(m => inheritanceMap[subType]
                                        .Contains(m.GetTypeFullName())).ToList();

                                var members = subTypeSymbol.GetNinoTypeMembers(subTypeParentSymbols);
                                //get distinct members
                                members = members.Distinct().ToList();
                                WriteMembers(members, subTypeSymbol, valName);
                            }

                            sb.AppendLine("                    return;");
                        }
                    }
                }

                if (typeSymbol.IsInstanceType())
                {
                    if (typeSymbol.IsReferenceType)
                    {
                        sb.AppendLine("                default:");
                    }

                    if (isPolymorphicType)
                    {
                        sb.AppendLine(
                            $"                    writer.Write(NinoTypeConst.{typeSymbol.GetTypeFullName().GetTypeConstName()});");
                    }


                    if (typeSymbol.IsUnmanagedType)
                    {
                        sb.AppendLine("                    writer.Write(value);");
                    }
                    else
                    {
                        List<ITypeSymbol> parentTypeSymbols =
                            ninoSymbols.Where(m => inheritanceMap[typeFullName]
                                .Contains(m.GetTypeFullName())).ToList();
                        var defaultMembers = typeSymbol.GetNinoTypeMembers(parentTypeSymbols);
                        WriteMembers(defaultMembers, typeSymbol, "value");
                    }

                    if (isPolymorphicType && typeSymbol.IsReferenceType)
                    {
                        sb.AppendLine("                    return;");
                    }
                }
                else
                {
                    sb.AppendLine("                default:");
                    sb.AppendLine("                    throw new InvalidOperationException($\"Invalid type: {value.GetType().FullName}\");");
                }

                if (isPolymorphicType && typeSymbol.IsReferenceType)
                {
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

        var curNamespace = compilation.AssemblyName!.GetNamespace();

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using System.Buffers;
                     using System.Threading;
                     using global::Nino.Core;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class Serializer
                         {
                             private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> BufferWriters =
                                 new ConcurrentQueue<ArrayBufferWriter<byte>>();
                     
                             private static readonly ArrayBufferWriter<byte> DefaultBufferWriter = new ArrayBufferWriter<byte>(1024);
                             private static int _defaultUsed;
                     
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static ArrayBufferWriter<byte> GetBufferWriter()
                             {
                                 // Fast path
                                 if (Interlocked.CompareExchange(ref _defaultUsed, 1, 0) == 0)
                                 {
                                     return DefaultBufferWriter;
                                 }
                     
                                 if (BufferWriters.Count == 0)
                                 {
                                     return new ArrayBufferWriter<byte>(1024);
                                 }
                     
                                 if (BufferWriters.TryDequeue(out var bufferWriter))
                                 {
                                     return bufferWriter;
                                 }
                     
                                 return new ArrayBufferWriter<byte>(1024);
                             }
                     
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void ReturnBufferWriter(ArrayBufferWriter<byte> bufferWriter)
                             {
                     #if NET8_0_OR_GREATER
                                 bufferWriter.ResetWrittenCount();
                     #else
                                 bufferWriter.Clear();
                     #endif
                                 // Check if the buffer writer is the default buffer writer
                                 if (bufferWriter == DefaultBufferWriter)
                                 {
                                     // Ensure it is in use, otherwise throw an exception
                                     if (Interlocked.CompareExchange(ref _defaultUsed, 0, 1) == 0)
                                     {
                                         throw new InvalidOperationException("The returned buffer writer is not in use.");
                                     }
                     
                                     return;
                                 }
                     
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
                                 int size = Unsafe.SizeOf<T>();
                                 var span = bufferWriter.GetSpan(size);
                                 Unsafe.WriteUnaligned(ref span[0], value);
                                 bufferWriter.Advance(size);
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize(this bool value)
                             {
                                 if (value)
                                     return new byte[1] { 1 };
                                
                                 return new byte[1] { 0 };
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize(this byte value)
                             {
                                 return new byte[1] { value };
                             }

                     {{GenerateWriterAccessMethodBody("T", "        ", "<T>", "where T : unmanaged")}}
                     
                     {{GenerateWriterAccessMethodBody("T?", "        ", "<T>", "where T : unmanaged")}}
                     
                     {{GenerateWriterAccessMethodBody("T[]", "        ", "<T>", "where T : unmanaged")}}

                     {{GenerateWriterAccessMethodBody("T?[]", "        ", "<T>", "where T : unmanaged")}}

                     {{GenerateWriterAccessMethodBody("List<T>", "        ", "<T>", "where T : unmanaged")}}
                     
                     {{GenerateWriterAccessMethodBody("List<T?>", "        ", "<T>", "where T : unmanaged")}}
                     
                     {{GenerateWriterAccessMethodBody("Span<T>", "        ", "<T>", "where T : unmanaged")}}
                     
                     {{GenerateWriterAccessMethodBody("Span<T?>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GenerateWriterAccessMethodBody("ICollection<T>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GenerateWriterAccessMethodBody("ICollection<T?>", "        ", "<T>", "where T : unmanaged")}}
                             
                     {{GenerateWriterAccessMethodBody("string", "        ")}}

                     {{GenerateWriterAccessMethodBody("Dictionary<TKey, TValue>", "        ", "<TKey, TValue>", "where TKey : unmanaged where TValue : unmanaged")}}

                     {{sb}}    }
                     }
                     """;

        spc.AddSource("NinoSerializer.g.cs", code);
    }


    private static string GenerateWriterAccessMethodBody(string typeName, string indent = "",
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