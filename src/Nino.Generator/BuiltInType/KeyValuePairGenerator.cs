// KeyValuePairGenerator.cs
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

public class KeyValuePairGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoKeyValuePairGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol) return false;
        return typeSymbol.Name == "KeyValuePair";
    }

    protected override void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var types = namedType.TypeArguments.ToArray();
        var keyType = types[0];
        var valueType = types[1];

        // Check if we can use the fast unmanaged write
        // Both key and value must be unmanaged AND neither can be polymorphic
        bool canUseFastPath = typeSymbol.IsUnmanagedType &&
                              keyType.GetKind(NinoGraph) == NinoTypeHelper.NinoTypeKind.Unmanaged &&
                              valueType.GetKind(NinoGraph) == NinoTypeHelper.NinoTypeKind.Unmanaged;

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
            writer.Append("    ");
            writer.AppendLine(GetSerializeString(keyType, "value.Key"));
            writer.Append("    ");
            writer.AppendLine(GetSerializeString(valueType, "value.Value"));
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        var namedType = (INamedTypeSymbol)typeSymbol;
        var types = namedType.TypeArguments.ToArray();
        var keyType = types[0];
        var valueType = types[1];

        // Check if we can use the fast unmanaged read
        // Both key and value must be unmanaged AND neither can be polymorphic
        bool canUseFastPath = typeSymbol.IsUnmanagedType &&
                              typeSymbol.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T &&
                              keyType.GetKind(NinoGraph) == NinoTypeHelper.NinoTypeKind.Unmanaged &&
                              valueType.GetKind(NinoGraph) == NinoTypeHelper.NinoTypeKind.Unmanaged;
        var typeName = typeSymbol.ToDisplayString();

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
            writer.Append("    ");
            writer.AppendLine(GetDeserializeString(keyType, "k"));
            writer.Append("    ");
            writer.AppendLine(GetDeserializeString(valueType, "v"));
            writer.Append("    value = new ");
            writer.Append(typeName);
            writer.AppendLine("(k, v);");
        }

        writer.AppendLine("}");
        writer.AppendLine();

        // Ref overload - KeyValuePair is not modifiable
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");

        if (canUseFastPath)
        {
            writer.AppendLine("    => reader.Read(out value);");
        }
        else
        {
            writer.AppendLine("    => Deserialize(out value, ref reader);");
        }
    }
}
