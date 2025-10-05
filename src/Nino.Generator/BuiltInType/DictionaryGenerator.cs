// DictionaryGenerator.cs
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

public class DictionaryGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoDictionaryTypeGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType) return false;

        // Accept Dictionary<,>, ConcurrentDictionary<,>, and IDictionary<,>
        var originalDef = namedType.OriginalDefinition.ToDisplayString();
        if (originalDef != "System.Collections.Generic.Dictionary<TKey, TValue>" &&
            originalDef != "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" &&
            originalDef != "System.Collections.Generic.IDictionary<TKey, TValue>")
            return false;

        var keyType = namedType.TypeArguments[0];
        var valueType = namedType.TypeArguments[1];

        // Both key and value types must be valid
        if (keyType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Invalid ||
            valueType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Invalid)
            return false;

        return true;
    }

    protected override void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var keyType = namedType.TypeArguments[0];
        var valueType = namedType.TypeArguments[1];

        var typeName = typeSymbol.GetDisplayString();

        // Check if KVP is unmanaged (for fast path, no WeakVersionTolerance needed)
        bool kvpIsUnmanaged = keyType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged &&
                              valueType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

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
        writer.AppendLine("    foreach (var item in value)");
        writer.AppendLine("    {");

        if (kvpIsUnmanaged)
        {
            // For unmanaged KVP, use UnsafeWrite directly (no WeakVersionTolerance needed)
            writer.AppendLine("        writer.UnsafeWrite(item);");
        }
        else
        {
            // For managed KVP, serialize Key and Value separately with WeakVersionTolerance
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        var pos = writer.Advance(4);"); });

            writer.Append("        ");
            writer.AppendLine(GetSerializeString(keyType, "item.Key"));
            writer.Append("        ");
            writer.AppendLine(GetSerializeString(valueType, "item.Value"));

            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("        writer.PutLength(pos);"); });
        }

        writer.AppendLine("    }");

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var keyType = namedType.TypeArguments[0];
        var valueType = namedType.TypeArguments[1];

        // Check if this is IDictionary interface
        bool isIDictionary = namedType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>";

        // For IDictionary, use Dictionary as the concrete type
        var typeName = typeSymbol.GetDisplayString();
        var concreteTypeName = isIDictionary
            ? $"System.Collections.Generic.Dictionary<{keyType.GetDisplayString()}, {valueType.GetDisplayString()}>"
            : typeName;

        // Check if KVP is unmanaged (for fast path, no WeakVersionTolerance needed)
        var kvpTypeName = $"System.Collections.Generic.KeyValuePair<{keyType.GetDisplayString()}, {valueType.GetDisplayString()}>";
        bool kvpIsUnmanaged = keyType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged &&
                              valueType.GetKind(NinoGraph, GeneratedTypes) == NinoTypeHelper.NinoTypeKind.Unmanaged;

        // Out overload
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

        if (!kvpIsUnmanaged)
        {
            IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w => { w.AppendLine("    Reader eleReader;"); });
            writer.AppendLine();
        }

        writer.Append("    value = new ");
        writer.Append(concreteTypeName);
        writer.Append(concreteTypeName.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");
        writer.AppendLine(";");
        writer.AppendLine("    for (int i = 0; i < length; i++)");
        writer.AppendLine("    {");

        if (kvpIsUnmanaged)
        {
            // For unmanaged KVP, use UnsafeRead directly (no WeakVersionTolerance needed)
            writer.Append("        reader.UnsafeRead(out ");
            writer.Append(kvpTypeName);
            writer.AppendLine(" kvp);");
            writer.AppendLine("        value[kvp.Key] = kvp.Value;");
        }
        else
        {
            // For managed KVP, deserialize Key and Value separately with WeakVersionTolerance
            IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                w =>
                {
                    w.AppendLine("        eleReader = reader.Slice();");
                    w.Append("        ");
                    w.AppendLine(GetDeserializeString(keyType, "key", isOutVariable: true, readerName: "eleReader"));
                    w.Append("        ");
                    w.AppendLine(GetDeserializeString(valueType, "val", isOutVariable: true, readerName: "eleReader"));
                },
                w =>
                {
                    w.Append("        ");
                    w.AppendLine(GetDeserializeString(keyType, "key", isOutVariable: true));
                    w.Append("        ");
                    w.AppendLine(GetDeserializeString(valueType, "val", isOutVariable: true));
                });
            writer.AppendLine("        value[key] = val;");
        }

        writer.AppendLine("    }");

        writer.AppendLine("}");
        writer.AppendLine();

        // Ref overload
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");

        if (isIDictionary)
        {
            // For interfaces, just call the out overload since we don't know the concrete type
            writer.AppendLine("    Deserializer.Deserialize(out value, ref reader);");
        }
        else
        {
            // For concrete types - dictionaries are modifiable, clear and repopulate
            EofCheck(writer);

            writer.AppendLine();
            writer.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
            writer.AppendLine("    {");
            writer.AppendLine("        value = null;");
            writer.AppendLine("        return;");
            writer.AppendLine("    }");
            writer.AppendLine();

            if (!kvpIsUnmanaged)
            {
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w => { w.AppendLine("    Reader eleReader;"); });
                writer.AppendLine();
            }

            writer.AppendLine("    // Initialize if null, otherwise clear");
            writer.AppendLine("    if (value == null)");
            writer.AppendLine("    {");
            writer.Append("        value = new ");
            writer.Append(concreteTypeName);
            writer.Append(concreteTypeName.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");
            writer.AppendLine(";");
            writer.AppendLine("    }");
            writer.AppendLine("    else");
            writer.AppendLine("    {");
            writer.AppendLine("        value.Clear();");
            writer.AppendLine("    }");
            writer.AppendLine();
            writer.AppendLine("    for (int i = 0; i < length; i++)");
            writer.AppendLine("    {");

            if (kvpIsUnmanaged)
            {
                // For unmanaged KVP, use UnsafeRead directly (no WeakVersionTolerance needed)
                writer.Append("        reader.UnsafeRead(out ");
                writer.Append(kvpTypeName);
                writer.AppendLine(" kvp);");
                writer.AppendLine("        value[kvp.Key] = kvp.Value;");
            }
            else
            {
                // For managed KVP, deserialize Key and Value separately with WeakVersionTolerance
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(keyType, "key", isOutVariable: true, readerName: "eleReader"));
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(valueType, "val", isOutVariable: true, readerName: "eleReader"));
                    },
                    w =>
                    {
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(keyType, "key", isOutVariable: true));
                        w.Append("        ");
                        w.AppendLine(GetDeserializeString(valueType, "val", isOutVariable: true));
                    });
                writer.AppendLine("        value[key] = val;");
            }

            writer.AppendLine("    }");
        }

        writer.AppendLine("}");
    }
}
