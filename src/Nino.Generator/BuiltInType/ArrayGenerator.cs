// ArrayGenerator.cs
//
//  Author:
//        JasonXuDeveloper <jason@xgamedev.net>
//
//  Copyright (c) 2025 JEngine
//
//  Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in
//  all copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//  THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.BuiltInType;

public class ArrayGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoArrayTypeGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not IArrayTypeSymbol arraySymbol) return false;
        var elementType = arraySymbol.ElementType;
        return elementType.GetKind(NinoGraph, GeneratedTypes) != NinoTypeHelper.NinoTypeKind.Invalid;
    }

    protected override void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var arraySymbol = (IArrayTypeSymbol)typeSymbol;
        var elementType = arraySymbol.ElementType;
        var elementTypeName = elementType.GetDisplayString();
        var rank = arraySymbol.Rank;
        var typeName = typeSymbol.GetDisplayString();

        // Check if we can use the fast unmanaged write path
        bool canUseFastPath = rank == 1 && elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        // Check if we can use monomorphic fast path (sealed/struct NinoType)
        bool canUseMonomorphicPath = rank == 1 &&
                                      elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.NinoType &&
                                      elementType.IsSealedOrStruct();

        WriteAggressiveInlining(writer);
        writer.Append("public static void Serialize(this ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Writer writer)");
        writer.AppendLine("{");

        if (canUseFastPath)
        {
            writer.AppendLine("    writer.Write(value);");
        }
        else if (rank == 1)
        {
            // 1D array (jagged or managed element type)
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.AppendLine("        writer.Write(TypeCollector.NullCollection);");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    int cnt = value.Length;");
            writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
            writer.AppendLine();

            // Early exit for empty arrays
            writer.AppendLine("    if (cnt == 0)");
            writer.AppendLine("    {");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            // Monomorphic fast path: cache the serializer delegate once
            if (canUseMonomorphicPath)
            {
                writer.AppendLine("    // Monomorphic fast path: element type is sealed/struct, cache serializer");
                writer.AppendLine($"    var serializer = CachedSerializer<{elementTypeName}>.SerializePolymorphic;");
                writer.AppendLine();
            }

            // Both value and reference types benefit from ref iteration - eliminates bounds checks
            writer.AppendLine("#if NET5_0_OR_GREATER");
            writer.AppendLine("    ref var cur = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(value);");
            writer.AppendLine("#else");
            writer.AppendLine("    ref var cur = ref value[0];");
            writer.AppendLine("#endif");
            writer.AppendLine("    ref var end = ref System.Runtime.CompilerServices.Unsafe.Add(ref cur, cnt);");
            writer.AppendLine();
            writer.AppendLine("    do");
            writer.AppendLine("    {");
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        var pos = writer.Advance(4);"); });

            if (canUseMonomorphicPath)
            {
                // Use cached serializer directly
                writer.AppendLine("        serializer(cur, ref writer);");
            }
            else
            {
                writer.Append("        ");
                writer.AppendLine(GetSerializeString(elementType, "cur"));
            }
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        writer.PutLength(pos);"); });
            writer.AppendLine("        cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref cur, 1);");
            writer.AppendLine("    }");
            writer.AppendLine("    while (System.Runtime.CompilerServices.Unsafe.IsAddressLessThan(ref cur, ref end));");
        }
        else
        {
            // Multi-dimensional array - 按照 MultDimensionalArrayParser 模板
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.AppendLine("        writer.Write(TypeCollector.Null);");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            // 获取维度
            for (int i = 0; i < rank; i++)
            {
                writer.AppendLine($"    var dim{i} = value.GetLength({i});");
            }

            // 写入维度元组
            writer.Append("    writer.Write(NinoTuple.Create(");
            for (int i = 0; i < rank; i++)
            {
                if (i > 0) writer.Append(", ");
                writer.Append($"dim{i}");
            }
            writer.AppendLine("));");
            writer.AppendLine();

            // 计算总长度
            writer.Append("    var totalLength = ");
            for (int i = 0; i < rank; i++)
            {
                if (i > 0) writer.Append(" * ");
                writer.Append($"dim{i}");
            }
            writer.AppendLine(";");
            writer.AppendLine();

            // 获取数组数据引用并创建 Span
            writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
            writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
            writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalLength);");
            writer.AppendLine();

            // 根据类型选择序列化方式
            bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;
            if (isUnmanaged)
            {
                writer.AppendLine("    writer.WriteSpanWithoutHeader(span);");
            }
            else
            {
                writer.AppendLine("    foreach (ref var v in span)");
                writer.AppendLine("    {");
                writer.AppendLine($"        {GetSerializeString(elementType, "v")}");
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var arraySymbol = (IArrayTypeSymbol)typeSymbol;
        var elementType = arraySymbol.ElementType;
        var elementTypeName = elementType.GetDisplayString();
        var rank = arraySymbol.Rank;
        var typeName = typeSymbol.GetDisplayString();

        // Check if we can use the fast unmanaged read path
        bool canUseFastPath = rank == 1 && elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        // Check if we can use monomorphic fast path (sealed/struct NinoType)
        bool canUseMonomorphicPath = rank == 1 &&
                                      elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.NinoType &&
                                      elementType.IsSealedOrStruct();

        // Out overload
        WriteAggressiveInlining(writer);
        writer.Append("public static void Deserialize(out ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);

        if (canUseFastPath)
        {
            writer.AppendLine("    reader.Read(out value);");
        }
        else if (rank == 1)
        {
            // 1D array
            writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
            writer.AppendLine("    {");
            writer.AppendLine("        value = null;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();
            // Optimized array deserialization - reduce reader slicing overhead
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();
            writer.Append("    value = new ");
            writer.Append(GetArrayCreationString(elementTypeName, "length"));
            writer.AppendLine(";");
            writer.AppendLine("    var span = value.AsSpan();");

            // Monomorphic fast path: cache the deserializer delegate once
            if (canUseMonomorphicPath)
            {
                writer.AppendLine("    // Monomorphic fast path: element type is sealed/struct, cache deserializer");
                writer.AppendLine($"    var deserializer = CachedDeserializer<{elementTypeName}>.Deserialize;");
            }

            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (canUseMonomorphicPath)
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.AppendLine("        deserializer(out span[i], ref eleReader);");
                    },
                    w =>
                    {
                        w.AppendLine("        deserializer(out span[i], ref reader);");
                    });
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "span[i]", isOutVariable: false,
                            readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "span[i]", isOutVariable: false));
                    });
            }
            writer.AppendLine("    }");
        }
        else
        {
            // Multi-dimensional array - 按照 MultDimensionalArrayParser 模板
            writer.AppendLine("    reader.Peak(out int typeId);");
            writer.AppendLine("    if (typeId == TypeCollector.Null)");
            writer.AppendLine("    {");
            writer.AppendLine("        value = null;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            // 读取维度
            for (int i = 0; i < rank; i++)
            {
                writer.AppendLine($"    reader.UnsafeRead(out int dim{i});");
            }
            writer.AppendLine();

            // 计算总长度
            writer.Append("    var totalLength = ");
            for (int i = 0; i < rank; i++)
            {
                if (i > 0) writer.Append(" * ");
                writer.Append($"dim{i}");
            }
            writer.AppendLine(";");
            writer.AppendLine();

            // 创建数组
            writer.Append("    value = new ");
            writer.Append(GetArrayCreationString(elementTypeName, string.Join(", ", Enumerable.Range(0, rank).Select(i => $"dim{i}"))));
            writer.AppendLine(";");
            writer.AppendLine();

            // 获取数组数据引用并创建 Span
            writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
            writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
            writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalLength);");
            writer.AppendLine();

            // 反序列化每个元素 - 统一使用 foreach，不区分类型
            writer.AppendLine("    foreach (ref var v in span)");
            writer.AppendLine("    {");
            writer.AppendLine($"        {GetDeserializeRefString(elementType, "v")}");
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");

        writer.AppendLine();

        // Ref overload - arrays are modifiable, resize and fill
        WriteAggressiveInlining(writer);
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);
        if (canUseFastPath)
        {
            writer.AppendLine("    reader.ReadRef(ref value);");
        }
        else if (rank == 1)
        {
            // 1D array
            writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
            writer.AppendLine("    {");
            writer.AppendLine("        value = null;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();

            writer.AppendLine("    // Use Array.Resize for non-null arrays, otherwise create new");
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(GetArrayCreationString(elementTypeName, "length"));
            writer.AppendLine(";");
            writer.AppendLine("    }");
            writer.AppendLine("    else if (value.Length != length)");
            writer.AppendLine("    {");
            writer.AppendLine("        Array.Resize(ref value, length);");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    var span = value.AsSpan();");

            // Monomorphic fast path: cache the deserializer delegate once
            if (canUseMonomorphicPath)
            {
                writer.AppendLine("    // Monomorphic fast path: element type is sealed/struct, cache deserializer");
                writer.AppendLine($"    var deserializerRef = CachedDeserializer<{elementTypeName}>.DeserializeRef;");
            }

            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (canUseMonomorphicPath)
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.AppendLine("        deserializerRef(ref span[i], ref eleReader);");
                    },
                    w =>
                    {
                        w.AppendLine("        deserializerRef(ref span[i], ref reader);");
                    });
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeRefString(elementType, "span[i]", readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeRefString(elementType, "span[i]"));
                    });
            }
            writer.AppendLine("    }");
        }
        else
        {
            // Multi-dimensional array - 按照 MultDimensionalArrayParser 模板
            writer.AppendLine("    reader.Peak(out int typeId);");
            writer.AppendLine("    if (typeId == TypeCollector.Null)");
            writer.AppendLine("    {");
            writer.AppendLine("        value = null;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            // 读取维度
            for (int i = 0; i < rank; i++)
            {
                writer.AppendLine($"    reader.UnsafeRead(out int dim{i});");
            }
            writer.AppendLine();

            // 计算总长度
            writer.Append("    var totalLength = ");
            for (int i = 0; i < rank; i++)
            {
                if (i > 0) writer.Append(" * ");
                writer.Append($"dim{i}");
            }
            writer.AppendLine(";");
            writer.AppendLine();

            // 检查是否可以重用现有数组
            writer.AppendLine("    if (value is not null");
            for (int i = 0; i < rank; i++)
            {
                writer.AppendLine($"        && value.GetLength({i}) == dim{i}");
            }
            writer.AppendLine($"        && value.Length == totalLength)");
            writer.AppendLine("    {");
            writer.AppendLine("        // allow overwrite");
            writer.AppendLine("    }");
            writer.AppendLine("    else");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(GetArrayCreationString(elementTypeName, string.Join(", ", Enumerable.Range(0, rank).Select(i => $"dim{i}"))));
            writer.AppendLine(";");
            writer.AppendLine("    }");
            writer.AppendLine();

            // 获取数组数据引用并创建 Span
            writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
            writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
            writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalLength);");
            writer.AppendLine();
            
            writer.AppendLine("    foreach (ref var v in span)");
            writer.AppendLine("    {");
            writer.AppendLine($"        {GetDeserializeRefString(elementType, "v")}");
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
    }
}