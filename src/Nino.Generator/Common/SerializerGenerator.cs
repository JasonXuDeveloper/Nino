using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Filter;
using Nino.Generator.Filter.Operation;
using Nino.Generator.Metadata;
using Nino.Generator.Template;
using String = Nino.Generator.Filter.String;

namespace Nino.Generator.Common;

public class SerializerGenerator : NinoCommonGenerator
{
    private readonly IFilter _unmanagedFilter;

    public SerializerGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
        : base(compilation, ninoGraph, ninoTypes)
    {
        _unmanagedFilter = new Union().With
        (
            new Joint().With
            (
                new Not(new String()),
                new Unmanaged()
            ),
            new Interface("IEnumerable<T>", interfaceSymbol =>
            {
                var elementType = interfaceSymbol.TypeArguments[0];
                return elementType.IsUnmanagedType;
            })
        );
    }

    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;

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

        foreach (var ninoType in NinoTypes)
        {
            try
            {
                var classType = ninoType.TypeSymbol;
                string typeFullName = classType.GetTypeFullName();
                bool isPolymorphicType = ninoType.IsPolymorphic();

                // check if struct is unmanaged
                if (ninoType.TypeSymbol.IsUnmanagedType && !isPolymorphicType)
                {
                    continue;
                }

                sb.GenerateClassSerializeMethods(typeFullName);
                sb.AppendLine();

                HashSet<string> visited = new HashSet<string>();

                sb.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(
                    $"        public static void Serialize(this {typeFullName} value, ref Writer writer)");
                sb.AppendLine("        {");

                void WriteMembers(NinoType type, string valName)
                {
                    List<string> valNames = new();
                    foreach (var members in type.GroupByPrimitivity())
                    {
                        valNames.Clear();
                        foreach (var member in members)
                        {
                            var name = member.Name;
                            var isPrivate = member.IsPrivate;
                            var isProperty = member.IsProperty;
                            var val = $"{valName}.{name}";

                            if (isPrivate)
                            {
                                var accessName = valName;
                                if (type.TypeSymbol.IsValueType)
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

                            valNames.Add(val);
                        }

                        if (members.Count == 1)
                        {
                            var member = members[0];
                            var declaredType = member.Type;
                            var val = valNames[0];

                            //check if the typesymbol declaredType is string
                            if (declaredType.SpecialType == SpecialType.System_String)
                            {
                                //check if this member is annotated with [NinoUtf8]
                                var isUtf8 = member.IsUtf8String;

                                sb.AppendLine(
                                    isUtf8
                                        ? $"                    writer.WriteUtf8({val});"
                                        : $"                    writer.Write({val});");
                            }
                            else if (_unmanagedFilter.Filter(declaredType))
                            {
                                sb.AppendLine(
                                    $"                    writer.Write({val});");
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(type.CustomSerializer))
                                {
                                    sb.AppendLine(
                                        $"                    {type.CustomSerializer}.Serialize({val}, ref writer);");
                                    continue;
                                }

                                sb.AppendLine(
                                    $"                    Serialize({val}, ref writer);");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"#if {NinoTypeHelper.WeakVersionToleranceSymbol}");
                            foreach (var val in valNames)
                            {
                                sb.AppendLine($"                    writer.Write({val});");
                            }

                            sb.AppendLine("#else");
                            sb.AppendLine(
                                $"                    writer.Write(NinoTuple.Create({string.Join(", ", valNames)}));");
                            sb.AppendLine("#endif");
                        }
                    }
                }

                if (isPolymorphicType && ninoType.TypeSymbol.IsReferenceType)
                {
                    sb.AppendLine("            switch (value)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                case null:");
                    sb.AppendLine("                    writer.Write(TypeCollector.Null);");
                    sb.AppendLine("                    return;");

                    visited.Add("null");
                }

                if (NinoGraph.SubTypes.TryGetValue(ninoType, out var lst))
                {
                    //sort lst by how deep the inheritance is (i.e. how many levels of inheritance), the deepest first
                    lst.Sort((a, b) =>
                    {
                        int aCount = NinoGraph.BaseTypes[a].Count;
                        int bCount = NinoGraph.BaseTypes[b].Count;
                        return bCount.CompareTo(aCount);
                    });

                    foreach (var subType in lst)
                    {
                        if (subType.TypeSymbol.IsInstanceType())
                        {
                            if (!visited.Add(subType.TypeSymbol.ToDisplayString()))
                            {
                                continue;
                            }

                            string valName = subType.TypeSymbol.GetTypeInstanceName();
                            sb.AppendLine($"                case {subType.TypeSymbol.ToDisplayString()} {valName}:");
                            if (!string.IsNullOrEmpty(subType.CustomSerializer))
                            {
                                sb.AppendLine(
                                    $"                    {subType.CustomSerializer}.Serialize({valName}, ref writer);");
                            }
                            else
                            {
                                sb.AppendLine(
                                    $"                    writer.Write(NinoTypeConst.{subType.TypeSymbol.GetTypeFullName().GetTypeConstName()});");
                                if (subType.TypeSymbol.IsUnmanagedType)
                                {
                                    sb.AppendLine(
                                        $"                    writer.Write({valName});");
                                }
                                else
                                {
                                    WriteMembers(subType, valName);
                                }
                            }

                            sb.AppendLine("                    return;");
                        }
                    }
                }

                if (ninoType.TypeSymbol.IsInstanceType())
                {
                    if (ninoType.TypeSymbol.IsReferenceType)
                    {
                        sb.AppendLine("                default:");
                    }


                    if (!string.IsNullOrEmpty(ninoType.CustomSerializer))
                    {
                        sb.AppendLine(
                            $"                    {ninoType.CustomSerializer}.Serialize(value, ref writer);");
                    }
                    else
                    {
                        if (isPolymorphicType)
                        {
                            sb.AppendLine(
                                $"                    writer.Write(NinoTypeConst.{ninoType.TypeSymbol.GetTypeFullName().GetTypeConstName()});");
                        }


                        if (ninoType.TypeSymbol.IsUnmanagedType)
                        {
                            sb.AppendLine("                    writer.Write(value);");
                        }
                        else
                        {
                            WriteMembers(ninoType, "value");
                        }
                    }

                    if (isPolymorphicType && ninoType.TypeSymbol.IsReferenceType)
                    {
                        sb.AppendLine("                    return;");
                    }

                    visited.Add("default");
                }

                if (isPolymorphicType && ninoType.TypeSymbol.IsReferenceType)
                {
                    if (!visited.Contains("default"))
                    {
                        sb.AppendLine("                default:");
                        sb.AppendLine(
                            "                    throw new InvalidOperationException($\"Invalid type: {value.GetType().FullName}\");");
                    }

                    sb.AppendLine("            }");
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }
            catch (Exception e)
            {
                sb.AppendLine($"/* Error: {e.Message} for type {ninoType.TypeSymbol.GetTypeFullName()}");
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
                                 ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                                     MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
                     #if NET8_0_OR_GREATER
                                         ref value
                     #else
                                         value
                     #endif
                                     ), 1));
                                 src.CopyTo(ret);
                                 return ret;
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static void Serialize<T>(this T value, IBufferWriter<byte> bufferWriter) where T : unmanaged
                             {
                                 int size = Unsafe.SizeOf<T>();
                                 ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                                     MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
                     #if NET8_0_OR_GREATER
                                         ref value
                     #else
                                         value
                     #endif
                                     ), 1));
                                 src.CopyTo(bufferWriter.GetSpan(size));
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