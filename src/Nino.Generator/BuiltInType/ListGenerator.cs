// ListGenerator.cs
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

public class ListGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoListTypeGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType) return false;

        // Accept List<T>, IList<T>, ICollection<T>, and IEnumerable<T>
        var originalDef = namedType.OriginalDefinition.ToDisplayString();
        if (originalDef != "System.Collections.Generic.List<T>" &&
            originalDef != "System.Collections.Generic.IList<T>" &&
            originalDef != "System.Collections.Generic.ICollection<T>" &&
            originalDef != "System.Collections.Generic.IEnumerable<T>")
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

        // Check if this is an interface type
        bool isInterface = typeSymbol.TypeKind == TypeKind.Interface;

        // Check if this is IEnumerable (which doesn't have Count property)
        bool isIEnumerable = namedType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>";

        var typeName = typeSymbol.GetDisplayString();

        // Check if we can use the fast unmanaged write path (only for concrete List<T> with unmanaged elements)
        bool canUseFastPath = !isInterface &&
                              elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        writer.Append("public static void Serialize(this ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Writer writer)");
        writer.AppendLine("{");

        if (canUseFastPath)
        {
            writer.AppendLine("    writer.Write(value);");
        }
        else
        {
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.AppendLine("        writer.Write(TypeCollector.NullCollection);");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            if (isIEnumerable)
            {
                // IEnumerable doesn't have Count, use write-back pattern
                writer.AppendLine("    int cnt = 0;");
                writer.AppendLine("    int oldPos = writer.Advance(4);");
                writer.AppendLine();
                writer.AppendLine("    foreach (var item in value)");
                writer.AppendLine("    {");
                writer.AppendLine("        cnt++;");
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        var pos = writer.Advance(4);"); });

                writer.Append("        ");
                writer.AppendLine(GetSerializeString(elementType, "item"));
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        writer.PutLength(pos);"); });
                writer.AppendLine("    }");
                writer.AppendLine();
                writer.AppendLine("    writer.PutBack(TypeCollector.GetCollectionHeader(cnt), oldPos);");
            }
            else
            {
                // IList, ICollection, List all have Count property
                writer.AppendLine("    int cnt = value.Count;");
                writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
                writer.AppendLine();
                writer.AppendLine("    foreach (var item in value)");
                writer.AppendLine("    {");
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        var pos = writer.Advance(4);"); });

                writer.Append("        ");
                writer.AppendLine(GetSerializeString(elementType, "item"));
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("        writer.PutLength(pos);"); });
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var elementType = namedType.TypeArguments[0];

        // Check if this is an interface type
        bool isInterface = typeSymbol.TypeKind == TypeKind.Interface;

        // For interfaces, use List<T> as the concrete type
        var typeName = typeSymbol.GetDisplayString();
        var concreteTypeName = isInterface
            ? $"System.Collections.Generic.List<{elementType.GetDisplayString()}>"
            : typeName;

        // Check if we can use the fast unmanaged read path (only for concrete List<T> with unmanaged elements)
        bool canUseFastPath = !isInterface &&
                              elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        // Out overload
        writer.Append("public static void Deserialize(out ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);

        if (canUseFastPath)
        {
            writer.AppendLine("    reader.Read(out value);");
        }
        else
        {
            writer.AppendLine();
            writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
            writer.AppendLine("    {");
            writer.AppendLine("        value = default;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();

            if (isInterface)
            {
                // For interfaces, create concrete list then assign to interface
                writer.Append("    var list = new ");
                writer.Append(concreteTypeName);
                writer.AppendLine("(length);");
                writer.AppendLine("    for (int i = 0; i < length; i++)");
                writer.AppendLine("    {");

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
                writer.AppendLine("        list.Add(item);");
                writer.AppendLine("    }");
                writer.AppendLine("    value = list;");
            }
            else
            {
                // For concrete types
                writer.Append("    value = new ");
                writer.Append(concreteTypeName);
                writer.AppendLine("(length);");
                writer.AppendLine("    for (int i = 0; i < length; i++)");
                writer.AppendLine("    {");

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
                writer.AppendLine("        value.Add(item);");
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
        writer.AppendLine();

        // Ref overload
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");

        if (isInterface)
        {
            // For interfaces, just call the out overload since we don't know the concrete type
            writer.AppendLine("    Deserializer.Deserialize(out value, ref reader);");
        }
        else
        {
            // For concrete types - lists are modifiable, optimize by preserving identity
            EofCheck(writer);

            if (canUseFastPath)
            {
                writer.AppendLine("    reader.ReadRef(ref value);");
            }
            else
            {
                writer.AppendLine();
                writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                writer.AppendLine("    {");
                writer.AppendLine("        value = null;");
                writer.AppendLine("        return;");
                writer.AppendLine("    }");
                writer.AppendLine();

                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("    Reader eleReader;"); });
                writer.AppendLine();

                writer.AppendLine("    // Initialize if null");
                writer.AppendLine("    if (value == null)");
                writer.AppendLine("    {");
                writer.Append("        value = new ");
                writer.Append(concreteTypeName);
                writer.AppendLine("(length);");
                writer.AppendLine("        for (int i = 0; i < length; i++)");
                writer.AppendLine("        {");

                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("            eleReader = reader.Slice();");
                        w.Append("            ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("            ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
                writer.AppendLine("            value.Add(item);");
                writer.AppendLine("        }");
                writer.AppendLine("    }");
                writer.AppendLine("    else");
                writer.AppendLine("    {");
                writer.AppendLine("        // Optimize: reuse existing objects to preserve identity");
                writer.AppendLine("        int existingCount = value.Count;");
                writer.AppendLine();
                writer.AppendLine("        // Phase 1: Update existing elements (preserves object identity)");
                writer.AppendLine("        int reuseCount = System.Math.Min(existingCount, length);");

                // Check if element type is a mutable reference type (not string or value type)
                bool isMutableReferenceType = !elementType.IsValueType && elementType.SpecialType != SpecialType.System_String;

                if (isMutableReferenceType)
                {
                    // For mutable reference types, we can directly mutate the object without reassignment
                    writer.AppendLine("        for (int i = 0; i < reuseCount; i++)");
                    writer.AppendLine("        {");
                    writer.AppendLine("            var temp = value[i];");

                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                        w =>
                        {
                            w.AppendLine("            eleReader = reader.Slice();");
                            w.Append("            ");
                            w.AppendLine(GetDeserializeRefString(elementType, "temp", readerName: "eleReader"));
                        },
                        w =>
                        {
                            w.Append("            ");
                            w.AppendLine(GetDeserializeRefString(elementType, "temp"));
                        });
                    writer.AppendLine("        }");
                }
                else
                {
                    // For value types and immutable types (like string), we need to assign back
                    writer.AppendLine("        for (int i = 0; i < reuseCount; i++)");
                    writer.AppendLine("        {");
                    writer.AppendLine("            var temp = value[i];");

                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                        w =>
                        {
                            w.AppendLine("            eleReader = reader.Slice();");
                            w.Append("            ");
                            w.AppendLine(GetDeserializeRefString(elementType, "temp", readerName: "eleReader"));
                        },
                        w =>
                        {
                            w.Append("            ");
                            w.AppendLine(GetDeserializeRefString(elementType, "temp"));
                        });
                    writer.AppendLine("            value[i] = temp;");
                    writer.AppendLine("        }");
                }
                writer.AppendLine();
                writer.AppendLine("        // Phase 2: Shrink if needed");
                writer.AppendLine("        if (length < existingCount)");
                writer.AppendLine("        {");
                writer.AppendLine("            value.RemoveRange(length, existingCount - length);");
                writer.AppendLine("        }");
                writer.AppendLine("        // Phase 3: Grow if needed");
                writer.AppendLine("        else if (length > existingCount)");
                writer.AppendLine("        {");
                writer.AppendLine("            for (int i = existingCount; i < length; i++)");
                writer.AppendLine("            {");

                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("                eleReader = reader.Slice();");
                        w.Append("                ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("                ");
                        w.AppendLine(GetDeserializeString(elementType, "item", isOutVariable: true));
                    });
                writer.AppendLine("                value.Add(item);");
                writer.AppendLine("            }");
                writer.AppendLine("        }");
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
    }
}
