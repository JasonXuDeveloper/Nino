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

        // Check if we can use the fast unmanaged write path
        // Element must be unmanaged AND cannot be polymorphic
        bool canUseFastPath = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        writer.Append("public static void Serialize(this ");
        writer.Append(typeSymbol.GetDisplayString());
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
            // Optimized array serialization - use span for better performance
            writer.AppendLine("    var span = value.AsSpan();");
            writer.AppendLine("    int cnt = span.Length;");
            writer.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
            writer.AppendLine();
            // Optimized array serialization loop
            writer.AppendLine("    for (int i = 0; i < cnt; i++)");
            writer.AppendLine("    {");
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        var pos = writer.Advance(4);"); });

            writer.Append("        ");
            writer.AppendLine(GetSerializeString(elementType, "span[i]"));
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        writer.PutLength(pos);"); });
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var arraySymbol = (IArrayTypeSymbol)typeSymbol;
        var elementType = arraySymbol.ElementType;
        var elemType = elementType.GetDisplayString();
        var creationDecl = elemType.EndsWith("[]")
            ? elemType.Insert(elemType.IndexOf("[]", System.StringComparison.Ordinal), "[length]")
            : $"{elemType}[length]";
        var typeName = typeSymbol.GetDisplayString();

        // Check if we can use the fast unmanaged read path
        // Element must be unmanaged AND cannot be polymorphic
        bool canUseFastPath = elementType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

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
            writer.Append(creationDecl);
            writer.AppendLine(";");
            writer.AppendLine("    var span = value.AsSpan();");
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

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
            writer.AppendLine("    }");
        }

        writer.AppendLine("}");

        writer.AppendLine();

        // Ref overload - arrays are modifiable, resize and fill
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);
        if (canUseFastPath)
        {
            writer.AppendLine("    reader.ReadRef(ref value);");
        }
        else
        {
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
            writer.Append(creationDecl);
            writer.AppendLine(";");
            writer.AppendLine("    }");
            writer.AppendLine("    else if (value.Length != length)");
            writer.AppendLine("    {");
            writer.AppendLine("        Array.Resize(ref value, length);");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    var span = value.AsSpan();");
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

        writer.AppendLine("}");
    }
}