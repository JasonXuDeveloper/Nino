using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace Nino.Generator;

[Generator]
public class SerializerGenerator : IIncrementalGenerator
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
                         using global::Nino.Core;
                         using System.Buffers;
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
                             
                                 [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                 public static ArrayBufferWriter<byte> GetBufferWriter()
                                 {
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
                                     if (value == null)
                                         return new byte[2];
                                     var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
                                     int size = sizeof(int) + valueSpan.Length;
                                     byte[] ret = new byte[size];
                                     Unsafe.WriteUnaligned(ref ret[0], TypeCollector.GetCollectionHeader(value.Length));
                                     Unsafe.CopyBlockUnaligned(ref ret[4], ref valueSpan[0],
                                         (uint)valueSpan.Length);
                                     return ret;
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
                                    if (value == Span<T>.Empty)
                                        return new byte[2];
                                    var valueSpan = MemoryMarshal.AsBytes(value);
                                    int size = sizeof(int) + valueSpan.Length;
                                    byte[] ret = new byte[size];
                                    Unsafe.WriteUnaligned(ref ret[0], TypeCollector.GetCollectionHeader(value.Length));
                                    Unsafe.CopyBlockUnaligned(ref ret[4], ref valueSpan[0],
                                        (uint)valueSpan.Length);
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
                                 public static byte[] Serialize(this byte value)
                                 {
                                     return new byte[1] { value };
                                 }

                         {{GeneratePrivateSerializeImplMethodBody("T", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("T[]", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("T?[]", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("List<T>", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("Span<T>", "        ", "<T>", "where T : unmanaged")}}
                                 
                         {{GeneratePrivateSerializeImplMethodBody("ICollection<T>", "        ", "<T>", "where T : unmanaged")}}
                                 
                         {{GeneratePrivateSerializeImplMethodBody("T?", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("List<T?>", "        ", "<T>", "where T : unmanaged")}}

                         {{GeneratePrivateSerializeImplMethodBody("ICollection<T?>", "        ", "<T>", "where T : unmanaged")}}
                                 
                         {{GeneratePrivateSerializeImplMethodBody("string", "        ")}}

                         {{GeneratePrivateSerializeImplMethodBody("Dictionary<TKey, TValue>", "        ", "<TKey, TValue>", "where TKey : unmanaged where TValue : unmanaged")}}

                         {{sb}}    }
                         }
                         """;

            spc.AddSource("NinoSerializerExtension.g.cs", code);
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
            spc.AddSource("NinoSerializerExtension.g.cs", wrappedMessage);
        }
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