// QueueGenerator.cs
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
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.BuiltInType;

public class QueueGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoQueueTypeGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType) return false;

        // Accept Queue<T> and ConcurrentQueue<T>
        var originalDef = namedType.OriginalDefinition.ToDisplayString();
        if (originalDef != "System.Collections.Generic.Queue<T>" &&
            originalDef != "System.Collections.Concurrent.ConcurrentQueue<T>")
            return false;

        var elementType = namedType.TypeArguments[0];

        // Element type must be valid
        if (elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Invalid)
            return false;

        return true;
    }

    protected override void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var elementType = namedType.TypeArguments[0];

        var typeName = typeSymbol.GetDisplayString();

        // Check if element is unmanaged (no WeakVersionTolerance needed)
        bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        WriteAggressiveInlining(writer);
        writer.Append("public static void Serialize(this ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Writer writer)");
        writer.AppendLine("{");

        writer.AppendLine("    if (value == null)");
        writer.AppendLine("    {");
        writer.AppendLine("        writer.Write(TypeCollector.NullCollection);");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();

        writer.AppendLine("    int cnt = value.Count;");
        writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
        writer.AppendLine();
        writer.AppendLine("    if (cnt == 0)");
        writer.AppendLine("    {");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();

        var originalDef = namedType.OriginalDefinition.ToDisplayString();
        bool isQueue = originalDef == "System.Collections.Generic.Queue<T>";

        if (isQueue)
        {
            writer.AppendLine("#if NET5_0_OR_GREATER");
            var queueViewTypeName = $"Nino.Core.Internal.QueueView<{elementType.GetDisplayString()}>";
            writer.Append("    ref var queue = ref System.Runtime.CompilerServices.Unsafe.As<");
            writer.Append(typeName);
            writer.Append(", ");
            writer.Append(queueViewTypeName);
            writer.AppendLine(">(ref value);");
            writer.AppendLine("    var array = queue._array;");
            writer.AppendLine("    if (array == null)");
            writer.AppendLine("    {");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine("    int head = queue._head;");
            writer.AppendLine("    int tail = queue._tail;");
            writer.AppendLine("    int arrayLength = array.Length;");

            if (isUnmanaged)
            {
                // For unmanaged types, use span-based writes for optimal performance
                writer.AppendLine("    // Queue wraps around, use span writes for unmanaged types");
                writer.AppendLine("    if (head < tail)");
                writer.AppendLine("    {");
                writer.AppendLine("        // Simple case: no wrap-around, single span write");
                writer.AppendLine("        writer.WriteSpanWithoutHeader(array.AsSpan(head, cnt));");
                writer.AppendLine("    }");
                writer.AppendLine("    else");
                writer.AppendLine("    {");
                writer.AppendLine("        // Wrap-around case: two span writes");
                writer.AppendLine("        writer.WriteSpanWithoutHeader(array.AsSpan(head, arrayLength - head));");
                writer.AppendLine("        writer.WriteSpanWithoutHeader(array.AsSpan(0, tail));");
                writer.AppendLine("    }");
            }
            else
            {
                // For managed types, use ref iteration to eliminate bounds checks
                writer.AppendLine("    // Queue wraps around, serialize from head to tail with ref iteration");
                writer.AppendLine("    if (head < tail)");
                writer.AppendLine("    {");
                writer.AppendLine("        // Simple case: no wrap-around");
                writer.AppendLine("        ref var cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), head);");
                writer.AppendLine("        ref var end = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), tail);");
                writer.AppendLine("        do");
                writer.AppendLine("        {");

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            var pos = writer.Advance(4);"); });

                writer.Append("            ");
                writer.AppendLine(GetSerializeString(elementType, "cur"));

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            writer.PutLength(pos);"); });

                writer.AppendLine("            cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref cur, 1);");
                writer.AppendLine("        }");
                writer.AppendLine("        while (System.Runtime.CompilerServices.Unsafe.IsAddressLessThan(ref cur, ref end));");
                writer.AppendLine("    }");
                writer.AppendLine("    else");
                writer.AppendLine("    {");
                writer.AppendLine("        // Wrap-around case: serialize from head to end, then from 0 to tail");
                writer.AppendLine("        ref var cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), head);");
                writer.AppendLine("        ref var end = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), arrayLength);");
                writer.AppendLine("        do");
                writer.AppendLine("        {");

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            var pos = writer.Advance(4);"); });

                writer.Append("            ");
                writer.AppendLine(GetSerializeString(elementType, "cur"));

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            writer.PutLength(pos);"); });

                writer.AppendLine("            cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref cur, 1);");
                writer.AppendLine("        }");
                writer.AppendLine("        while (System.Runtime.CompilerServices.Unsafe.IsAddressLessThan(ref cur, ref end));");
                writer.AppendLine();
                writer.AppendLine("        // Serialize from 0 to tail");
                writer.AppendLine("        cur = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array);");
                writer.AppendLine("        end = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), tail);");
                writer.AppendLine("        do");
                writer.AppendLine("        {");

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            var pos = writer.Advance(4);"); });

                writer.Append("            ");
                writer.AppendLine(GetSerializeString(elementType, "cur"));

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("            writer.PutLength(pos);"); });

                writer.AppendLine("            cur = ref System.Runtime.CompilerServices.Unsafe.Add(ref cur, 1);");
                writer.AppendLine("        }");
                writer.AppendLine("        while (System.Runtime.CompilerServices.Unsafe.IsAddressLessThan(ref cur, ref end));");
                writer.AppendLine("    }");
            }
            writer.AppendLine("#else");
            writer.AppendLine("    // Fallback for Unity and other non-.NET 5.0+ platforms");
            writer.AppendLine("    foreach (var item in value)");
            writer.AppendLine("    {");

            if (!isUnmanaged)
            {
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        var pos = writer.Advance(4);"); });
            }

            writer.Append("        ");
            writer.AppendLine(GetSerializeString(elementType, "item"));

            if (!isUnmanaged)
            {
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        writer.PutLength(pos);"); });
            }

            writer.AppendLine("    }");
            writer.AppendLine("#endif");
        }
        else
        {
            // Fallback to foreach for ConcurrentQueue
            writer.AppendLine("    foreach (var item in value)");
            writer.AppendLine("    {");

            if (!isUnmanaged)
            {
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        var pos = writer.Advance(4);"); });
            }

            writer.Append("        ");
            writer.AppendLine(GetSerializeString(elementType, "item"));

            if (!isUnmanaged)
            {
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        writer.PutLength(pos);"); });
            }

            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var elementType = namedType.TypeArguments[0];
        var typeName = typeSymbol.GetDisplayString();

        // Check if element is unmanaged (no WeakVersionTolerance needed)
        bool isUnmanaged = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        // Out overload
        WriteAggressiveInlining(writer);
        writer.Append("public static void Deserialize(out ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);

        writer.AppendLine();
        writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
        writer.AppendLine("    {");
        writer.AppendLine("        value = default;");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();

        if (!isUnmanaged)
        {
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();
        }

        // ConcurrentQueue doesn't support capacity parameter, only Queue does
        var originalDef = namedType.OriginalDefinition.ToDisplayString();
        bool isQueue = originalDef == "System.Collections.Generic.Queue<T>";

        if (isQueue)
        {
            writer.AppendLine("#if NET5_0_OR_GREATER");
            // For Queue<T>, directly construct internal array and use Unsafe.As for efficiency
            var elemType = elementType.GetDisplayString();
            var queueViewTypeName = $"Nino.Core.Internal.QueueView<{elemType}>";

            if (isUnmanaged)
            {
                // For unmanaged types, use efficient memcpy via GetBytes
                writer.Append("    var array = new ");
                writer.Append(GetArrayCreationString(elemType, "length"));
                writer.AppendLine(";");
                writer.Append("    reader.GetBytes(length * System.Runtime.CompilerServices.Unsafe.SizeOf<");
                writer.Append(elemType);
                writer.AppendLine(">(), out var bytes);");
                writer.AppendLine("    System.Span<byte> dst = System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan());");
                writer.AppendLine("    bytes.CopyTo(dst);");
            }
            else
            {
                // For managed types, use ref iteration for efficient element assignment
                writer.Append("    var array = new ");
                writer.Append(GetArrayCreationString(elemType, "length"));
                writer.AppendLine(";");
                writer.AppendLine("    var span = array.AsSpan();");
                writer.AppendLine("    for (int i = 0; i < length; i++)");
                writer.AppendLine("    {");

                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "span[i]", isOutVariable: false, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "span[i]", isOutVariable: false));
                    });

                writer.AppendLine("    }");
            }

            writer.AppendLine();
            writer.AppendLine("    // Use Unsafe.As to directly construct Queue with internal array");
            writer.Append("    value = new ");
            writer.Append(typeName);
            writer.AppendLine("(length);");
            writer.Append("    ref var queue = ref System.Runtime.CompilerServices.Unsafe.As<");
            writer.Append(typeName);
            writer.Append(", ");
            writer.Append(queueViewTypeName);
            writer.AppendLine(">(ref value);");
            writer.AppendLine("    queue._array = array;");
            writer.AppendLine("    queue._head = 0;");
            writer.AppendLine("    queue._tail = length;");
            writer.AppendLine("    queue._size = length;");
            writer.AppendLine("#else");
            writer.AppendLine("    // Fallback for Unity and other non-.NET 5.0+ platforms");
            writer.Append("    value = new ");
            writer.Append(typeName);
            writer.AppendLine("(length);");
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (isUnmanaged)
            {
                writer.Append("        ");
                writer.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
            }

            writer.AppendLine("        value.Enqueue(item);");
            writer.AppendLine("    }");
            writer.AppendLine("#endif");
        }
        else
        {
            // Fallback for ConcurrentQueue
            writer.Append("    value = new ");
            writer.Append(typeName);
            writer.AppendLine("();");
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (isUnmanaged)
            {
                writer.Append("        ");
                writer.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
            }

            writer.AppendLine("        value.Enqueue(item);");
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
        writer.AppendLine();

        // Ref overload - clear and repopulate
        WriteAggressiveInlining(writer);
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);

        writer.AppendLine();
        writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
        writer.AppendLine("    {");
        writer.AppendLine("        value = null;");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();

        if (!isUnmanaged)
        {
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();
        }

        // ConcurrentQueue doesn't support capacity parameter, only Queue does
        var originalDef2 = namedType.OriginalDefinition.ToDisplayString();
        bool isQueue2 = originalDef2 == "System.Collections.Generic.Queue<T>";

        if (isQueue2)
        {
            writer.AppendLine("#if NET5_0_OR_GREATER");
            // For Queue<T>, reuse existing array if possible, resize if needed
            var elemType = elementType.GetDisplayString();
            var queueViewTypeName = $"Nino.Core.Internal.QueueView<{elemType}>";

            writer.AppendLine("    // Extract existing array or create new, then resize if needed");
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(typeName);
            writer.AppendLine("(length);");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.Append("    ref var queue = ref System.Runtime.CompilerServices.Unsafe.As<");
            writer.Append(typeName);
            writer.Append(", ");
            writer.Append(queueViewTypeName);
            writer.AppendLine(">(ref value);");
            writer.AppendLine("    var array = queue._array;");
            writer.AppendLine();
            writer.AppendLine("    // Resize array if needed");
            writer.AppendLine("    if (array == null || array.Length < length)");
            writer.AppendLine("    {");
            writer.AppendLine("        System.Array.Resize(ref array, length);");
            writer.AppendLine("    }");
            writer.AppendLine();

            if (isUnmanaged)
            {
                // For unmanaged types, use efficient memcpy via GetBytes
                writer.Append("    reader.GetBytes(length * System.Runtime.CompilerServices.Unsafe.SizeOf<");
                writer.Append(elemType);
                writer.AppendLine(">(), out var bytes);");
                writer.AppendLine("    System.Span<byte> dst = System.Runtime.InteropServices.MemoryMarshal.AsBytes(array.AsSpan(0, length));");
                writer.AppendLine("    bytes.CopyTo(dst);");
            }
            else
            {
                // For managed types, use ref deserialization for efficient element assignment
                writer.AppendLine("    var span = array.AsSpan();");
                writer.AppendLine("    for (int i = 0; i < length; i++)");
                writer.AppendLine("    {");

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

                writer.AppendLine("    }");
            }

            writer.AppendLine();
            writer.AppendLine("    // Update Queue internal state with the array");
            writer.AppendLine("    queue._array = array;");
            writer.AppendLine("    queue._head = 0;");
            writer.AppendLine("    queue._tail = length;");
            writer.AppendLine("    queue._size = length;");
            writer.AppendLine("#else");
            writer.AppendLine("    // Fallback for Unity and other non-.NET 5.0+ platforms");
            writer.AppendLine("    // Initialize if null, otherwise clear");
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(typeName);
            writer.AppendLine("(length);");
            writer.AppendLine("    }");
            writer.AppendLine("    else");
            writer.AppendLine("    {");
            writer.AppendLine("        value.Clear();");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (isUnmanaged)
            {
                writer.Append("        ");
                writer.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
            }

            writer.AppendLine("        value.Enqueue(item);");
            writer.AppendLine("    }");
            writer.AppendLine("#endif");
        }
        else
        {
            // Fallback for ConcurrentQueue - clear and repopulate
            writer.AppendLine("    // Initialize if null, otherwise clear");
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(typeName);
            writer.AppendLine("();");
            writer.AppendLine("    }");
            writer.AppendLine("    else");
            writer.AppendLine("    {");
            writer.AppendLine("        value.Clear();");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (isUnmanaged)
            {
                writer.Append("        ");
                writer.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
            }
            else
            {
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
            }

            writer.AppendLine("        value.Enqueue(item);");
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
    }
}
