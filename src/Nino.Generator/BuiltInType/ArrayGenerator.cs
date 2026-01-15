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
            // Multi-dimensional array - choose implementation based on element type
            bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.AppendLine("        writer.Write(TypeCollector.NullCollection);");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            if (isUnmanaged)
            {
                // Unmanaged types use optimized Span-based implementation
                // Get dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    var len{i} = value.GetLength({i});");
                }

                // Calculate total length
                writer.Append("    var totalElements = ");
                for (int i = 0; i < rank; i++)
                {
                    if (i > 0) writer.Append(" * ");
                    writer.Append($"len{i}");
                }
                writer.AppendLine(";");

                // Write collection header (includes empty array detection)
                writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(totalElements));");
                writer.AppendLine();

                // Write dimension info
                writer.AppendLine($"    writer.Write({rank});"); // Write rank
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    writer.Write(len{i});");
                }
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Get array data reference and create Span
                writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
                writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
                writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalElements);");
                writer.AppendLine();

                writer.AppendLine("    writer.WriteSpanWithoutHeader(span);");
            }
            else
            {
                // Non-unmanaged types use nested loop implementation
                // Calculate dimensions and total element count
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    int len{i} = value.GetLength({i});");
                }
                writer.Append("    int totalElements = ");
                for (int i = 0; i < rank; i++)
                {
                    if (i > 0) writer.Append(" * ");
                    writer.Append($"len{i}");
                }
                writer.AppendLine(";");
                writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(totalElements));");
                writer.AppendLine();

                // Write rank
                writer.AppendLine($"    writer.Write({rank});");
                writer.AppendLine();

                // Write dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    writer.Write(len{i});");
                }
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Generate nested loops for space-locality-aware serialization
                writer.AppendLine("    // Serialize in row-major order for space locality");
                for (int i = 0; i < rank; i++)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}for (int i{i} = 0; i{i} < len{i}; i{i}++)");
                    writer.AppendLine($"{indent}{{");
                }

                var innerIndent = new string(' ', 4 * (rank + 1));
                var indices = string.Join(", ", Enumerable.Range(0, rank).Select(i => $"i{i}"));

                // Only use WEAK_VERSION_TOLERANCE for non-unmanaged types
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine($"{innerIndent}var pos = writer.Advance(4);"); });

                writer.Append(innerIndent);
                writer.AppendLine(GetSerializeString(elementType, $"value[{indices}]"));

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine($"{innerIndent}writer.PutLength(pos);"); });

                for (int i = rank - 1; i >= 0; i--)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}}}");
                }
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
            // Multi-dimensional array - choose implementation based on element type
            bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

            if (isUnmanaged)
            {
                // Unmanaged types use optimized Span-based implementation
                writer.AppendLine("    if (!reader.ReadCollectionHeader(out var totalElements))");
                writer.AppendLine("    {");
                writer.AppendLine("        value = null;");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read rank
                writer.AppendLine("    int readRank;");
                writer.AppendLine("    reader.Read(out readRank);");
                writer.AppendLine($"    if (readRank != {rank})");
                writer.AppendLine("    {");
                writer.AppendLine($"        throw new System.InvalidOperationException($\"Array rank mismatch. Expected {rank}, got {{readRank}}\");");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    int len{i};");
                    writer.AppendLine($"    reader.Read(out len{i});");
                }
                writer.AppendLine();

                // Create array
                writer.Append("    value = new ");
                writer.Append(GetArrayCreationString(elementTypeName, string.Join(", ", Enumerable.Range(0, rank).Select(i => $"len{i}"))));
                writer.AppendLine(";");
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Get array data reference and create Span
                writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
                writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
                writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalElements);");
                writer.AppendLine();

                writer.AppendLine("    // Fast path for unmanaged types");
                writer.AppendLine("    reader.ReadSpan(span);");
            }
            else
            {
                // Non-unmanaged types use nested loop implementation
                // Read collection header first (handles null case)
                writer.AppendLine("    if (!reader.ReadCollectionHeader(out var totalElements))");
                writer.AppendLine("    {");
                writer.AppendLine("        value = null;");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read rank
                writer.AppendLine("    int readRank;");
                writer.AppendLine("    reader.Read(out readRank);");
                writer.AppendLine($"    if (readRank != {rank})");
                writer.AppendLine("    {");
                writer.AppendLine($"        throw new System.InvalidOperationException($\"Array rank mismatch. Expected {rank}, got {{readRank}}\");");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    int len{i};");
                    writer.AppendLine($"    reader.Read(out len{i});");
                }
                writer.AppendLine();

                // Only use WEAK_VERSION_TOLERANCE reader for non-unmanaged types
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("    Reader eleReader;"); });
                writer.AppendLine();

                // Create array with proper dimensions using direct syntax
                var lengths = string.Join(", ", Enumerable.Range(0, rank).Select(i => $"len{i}"));
                writer.Append("    value = new ");
                writer.Append(GetArrayCreationString(elementTypeName, lengths));
                writer.AppendLine(";");
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Generate nested loops for space-locality-aware deserialization
                writer.AppendLine("    // Deserialize in row-major order for space locality");
                for (int i = 0; i < rank; i++)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}for (int i{i} = 0; i{i} < len{i}; i{i}++)");
                    writer.AppendLine($"{indent}{{");
                }

                var innerIndent = new string(' ', 4 * (rank + 1));
                var indices = string.Join(", ", Enumerable.Range(0, rank).Select(i => $"i{i}"));

                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine($"{innerIndent}eleReader = reader.Slice();");
                        w.Append($"{innerIndent}");
                        w.AppendLine(GetDeserializeString(elementType, $"value[{indices}]", isOutVariable: false,
                            readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append($"{innerIndent}");
                        w.AppendLine(GetDeserializeString(elementType, $"value[{indices}]", isOutVariable: false));
                    });

                for (int i = rank - 1; i >= 0; i--)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}}}");
                }
            }
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
            // Multi-dimensional array - choose implementation based on element type
            bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

            if (isUnmanaged)
            {
                // Unmanaged types use optimized Span-based implementation
                writer.AppendLine("    if (!reader.ReadCollectionHeader(out var totalElements))");
                writer.AppendLine("    {");
                writer.AppendLine("        value = null;");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read rank
                writer.AppendLine("    int readRank;");
                writer.AppendLine("    reader.Read(out readRank);");
                writer.AppendLine($"    if (readRank != {rank})");
                writer.AppendLine("    {");
                writer.AppendLine($"        throw new System.InvalidOperationException($\"Array rank mismatch. Expected {rank}, got {{readRank}}\");");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    int len{i};");
                    writer.AppendLine($"    reader.Read(out len{i});");
                }
                writer.AppendLine();

                // Check if existing array can be reused
                writer.AppendLine("    if (value is not null");
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"        && value.GetLength({i}) == len{i}");
                }
                writer.AppendLine($"        && value.Length == totalElements)");
                writer.AppendLine("    {");
                writer.AppendLine("        // allow overwrite");
                writer.AppendLine("    }");
                writer.AppendLine("    else");
                writer.AppendLine("    {");
                writer.Append("        value = new ");
                writer.Append(GetArrayCreationString(elementTypeName, string.Join(", ", Enumerable.Range(0, rank).Select(i => $"len{i}"))));
                writer.AppendLine(";");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Get array data reference and create Span
                writer.AppendLine($"    ref var src = ref NinoMarshal.DangerousGetArrayDataReference<{elementTypeName}>(value);");
                writer.AppendLine($"    ref var first = ref System.Runtime.CompilerServices.Unsafe.As<byte, {elementTypeName}>(ref src);");
                writer.AppendLine($"    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, totalElements);");
                writer.AppendLine();

                writer.AppendLine("    // Fast path for unmanaged types");
                writer.AppendLine("    reader.ReadSpan(span);");
            }
            else
            {
                // Non-unmanaged types use nested loop implementation
                // Read collection header first (handles null case)
                writer.AppendLine("    if (!reader.ReadCollectionHeader(out var totalElements))");
                writer.AppendLine("    {");
                writer.AppendLine("        value = null;");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read rank
                writer.AppendLine("    int readRank;");
                writer.AppendLine("    reader.Read(out readRank);");
                writer.AppendLine($"    if (readRank != {rank})");
                writer.AppendLine("    {");
                writer.AppendLine($"        throw new System.InvalidOperationException($\"Array rank mismatch. Expected {rank}, got {{readRank}}\");");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Read dimensions
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"    int len{i};");
                    writer.AppendLine($"    reader.Read(out len{i});");
                }
                writer.AppendLine();

                // Only use WEAK_VERSION_TOLERANCE reader for non-unmanaged types
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("    Reader eleReader;"); });
                writer.AppendLine();

                // Check if we can reuse existing array
                writer.AppendLine("    bool canReuse = value != null");
                for (int i = 0; i < rank; i++)
                {
                    writer.AppendLine($"        && value.GetLength({i}) == len{i}");
                }
                writer.AppendLine("        ;");
                writer.AppendLine();

                writer.AppendLine("    if (!canReuse)");
                writer.AppendLine("    {");
                var lengths = string.Join(", ", Enumerable.Range(0, rank).Select(i => $"len{i}"));
                writer.Append("        value = new ");
                writer.Append(GetArrayCreationString(elementTypeName, lengths));
                writer.AppendLine(";");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Early exit for empty arrays
                writer.AppendLine("    if (totalElements == 0)");
                writer.AppendLine("    {");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                // Generate nested loops for space-locality-aware deserialization
                writer.AppendLine("    // Deserialize in row-major order for space locality");
                for (int i = 0; i < rank; i++)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}for (int i{i} = 0; i{i} < len{i}; i{i}++)");
                    writer.AppendLine($"{indent}{{");
                }

                var innerIndent = new string(' ', 4 * (rank + 1));
                var indices = string.Join(", ", Enumerable.Range(0, rank).Select(i => $"i{i}"));

                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine($"{innerIndent}eleReader = reader.Slice();");
                        w.Append($"{innerIndent}");
                        w.AppendLine(GetDeserializeRefString(elementType, $"value[{indices}]", readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append($"{innerIndent}");
                        w.AppendLine(GetDeserializeRefString(elementType, $"value[{indices}]"));
                    });

                for (int i = rank - 1; i >= 0; i--)
                {
                    var indent = new string(' ', 4 * (i + 1));
                    writer.AppendLine($"{indent}}}");
                }
            }
        }

        writer.AppendLine("}");
    }
}
