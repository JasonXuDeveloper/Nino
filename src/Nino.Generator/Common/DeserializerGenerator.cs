using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.Common;

public partial class DeserializerGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
    : NinoCommonGenerator(compilation, ninoGraph, ninoTypes)
{
    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;
        HashSet<ITypeSymbol> generatedTypes = new(SymbolEqualityComparer.Default);
        GenerateTrivialCode(spc, generatedTypes);

        StringBuilder sb = new();
        sb.AppendLine("""
                              private delegate object DeserializeDelegate(ref Reader reader);
                              private static Dictionary<IntPtr, DeserializeDelegate> _deserializers = new()
                              {
                      """);
        foreach (var type in generatedTypes)
        {
            if (type.IsUnmanagedType && !type.IsPolyMorphicType())
                continue;
            sb.AppendLine($$"""
                                        {
                                            typeof({{type.ToDisplayString()}}).TypeHandle.Value, 
                                            (ref Reader reader) => 
                                                {
                                                    Deserialize(out {{type.ToDisplayString()}} value, ref reader);
                                                    return value;
                                                }
                                        },
                            """);
        }

        sb.AppendLine("        };");

        sb.AppendLine($$"""

                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static void Deserialize<T>(ReadOnlySpan<byte> data, out T value)
                                {
                                    var reader = new Reader(data);
                                    Deserialize(out value, ref reader);
                                }
                                
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static void Deserialize<T>(out T value, ref Reader reader)
                                {
                                #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                     if (reader.Eof)
                                     {
                                        value = default;
                                        return;
                                     }
                                #endif

                                    if(!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                                    {
                                        reader.Read(out value);
                                        return;
                                    }

                                    if (!_deserializers.TryGetValue(typeof(T).TypeHandle.Value, out var deserializer))
                                    {
                                        throw new Exception($"Deserializer not found for type {typeof(T).FullName}");
                                    }

                                    value = (T)deserializer.Invoke(ref reader);
                                }
                                
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static T Deserialize<T>(ReadOnlySpan<byte> data)
                                {
                                    var reader = new Reader(data);
                                    return Deserialize<T>(ref reader);
                                }
                                
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static T Deserialize<T>(ref Reader reader)
                                {
                                    Deserialize(out T value, ref reader);
                                    return value;
                                }
                                
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static object Deserialize(ReadOnlySpan<byte> data, Type type)
                                {
                                    var reader = new Reader(data);
                                    return Deserialize(ref reader, type);
                                }
                                
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static object Deserialize(ref Reader reader, Type type)
                                {
                                    if (!_deserializers.TryGetValue(type.TypeHandle.Value, out var deserializer))
                                    {
                                        throw new Exception($"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
                                    }

                                    return deserializer.Invoke(ref reader);
                                }
                        """);

        var curNamespace = compilation.AssemblyName!.GetNamespace();
        // generate code
        var genericCode = $$"""
                            // <auto-generated/>
                            using System;
                            using global::Nino.Core;
                            using System.Buffers;
                            using System.ComponentModel;
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

        spc.AddSource("NinoDeserializer.Generic.g.cs", genericCode);
    }
}