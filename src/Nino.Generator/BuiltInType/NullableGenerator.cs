// NullableGenerator.cs
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

public class NullableGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> selectedTypes,
    Compilation compilation) : NinoBuiltInTypeGenerator(ninoGraph, potentialTypes, selectedTypes, compilation)
{
    protected override string OutputFileName => "NinoNullableTypeGenerator";

    public override bool Filter(ITypeSymbol typeSymbol)
    {
        return typeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    protected override void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer)
    {
        ITypeSymbol elementType = ((INamedTypeSymbol)typeSymbol).TypeArguments[0];

        writer.Append("public static void Serialize(this ");
        writer.Append(elementType.GetDisplayString());
        writer.AppendLine("? value, ref Writer writer)");
        writer.AppendLine("{");
        writer.AppendLine("    if (!value.HasValue)");
        writer.AppendLine("    {");
        writer.AppendLine("        writer.Write(false);");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();
        writer.AppendLine("    writer.Write(true);");

        switch (elementType.GetKind(NinoGraph))
        {
            case NinoTypeHelper.NinoTypeKind.Boxed:
                writer.AppendLine(
                    "    NinoSerializer.SerializeBoxed(value.Value, ref writer, value.Value?.GetType());");
                break;
            case NinoTypeHelper.NinoTypeKind.Unmanaged:
                writer.Append("    writer.UnsafeWrite<");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(">(value.Value);");
                break;
            case NinoTypeHelper.NinoTypeKind.NinoType:
                writer.Append("    NinoSerializer.Serialize<");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(">(value.Value, ref writer);");
                break;
            case NinoTypeHelper.NinoTypeKind.Other:
                writer.Append("    Serializer.Serialize((");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(")value.Value, ref writer);");
                break;
        }

        writer.AppendLine("}");
    }

    protected override void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer)
    {
        ITypeSymbol elementType = ((INamedTypeSymbol)typeSymbol).TypeArguments[0];
        writer.Append("public static void Deserialize(out ");
        writer.Append(elementType.GetDisplayString());
        writer.AppendLine("? value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);
        writer.AppendLine("    reader.UnsafeRead(out bool hasValue);");
        writer.AppendLine("    if (!hasValue)");
        writer.AppendLine("    {");
        writer.AppendLine("        value = default;");
        writer.AppendLine("        return;");
        writer.AppendLine("    }");
        writer.AppendLine();
        switch (elementType.GetKind(NinoGraph))
        {
            case NinoTypeHelper.NinoTypeKind.Boxed:
                writer.Append(elementType.GetDisplayString());
                writer.Append(" ret = NinoDeserializer.DeserializeBoxed(ref reader, null);");
                break;
            case NinoTypeHelper.NinoTypeKind.Unmanaged:
                writer.Append("    reader.UnsafeRead<");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(">(out var ret);");
                break;
            case NinoTypeHelper.NinoTypeKind.NinoType:
                writer.Append("    NinoDeserializer.Deserialize(out ");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(" ret, ref reader);");
                break;
            case NinoTypeHelper.NinoTypeKind.Other:
                writer.Append("    Deserializer.Deserialize(out ");
                writer.Append(elementType.GetDisplayString());
                writer.AppendLine(" ret, ref reader);");
                break;
        }
        
        writer.AppendLine("    value = ret;");
        writer.AppendLine("}");
        
        writer.AppendLine();
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(elementType.GetDisplayString());
        writer.AppendLine("? value, ref Reader reader) => Deserialize(out value, ref reader);");
    }
}