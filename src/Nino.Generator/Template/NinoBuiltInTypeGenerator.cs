// NinoBuiltInTypeGenerator.cs
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

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Template;

public abstract class NinoBuiltInTypeGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> generatedTypes,
    Compilation compilation,
    bool isUnityAssembly = false) : NinoGenerator(compilation, isUnityAssembly)
{
    protected NinoGraph NinoGraph { get; } = ninoGraph;
    protected HashSet<ITypeSymbol> GeneratedTypes { get; } = generatedTypes;
    protected abstract string OutputFileName { get; }
    public abstract bool Filter(ITypeSymbol typeSymbol);
    protected abstract void GenerateSerializer(ITypeSymbol typeSymbol, Writer writer);
    protected abstract void GenerateDeserializer(ITypeSymbol typeSymbol, Writer writer);
    private readonly HashSet<ITypeSymbol> _registeredTypes = new(TupleSanitizedEqualityComparer.Default);

    protected void AddRegisteredType(ITypeSymbol typeSymbol)
    {
        _registeredTypes.Add(typeSymbol);
    }


    public class Writer(string indent)
    {
        private readonly StringBuilder _sb = new(16_000);

        public void Append(string str)
        {
            _sb.Append(str);
        }

        public void AppendLine()
        {
            _sb.AppendLine();
            _sb.Append(indent);
        }

        public void AppendLine(string str)
        {
            _sb.AppendLine(str);
            _sb.Append(indent);
        }

        public void Clear()
        {
            _sb.Clear();
        }

        public void CopyTo(StringBuilder sb)
        {
            sb.Append(indent);
            sb.Append(_sb);
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }

    protected static readonly Action<string, Writer, Action<Writer>> IfDirective = (str, writer, action) =>
    {
        writer.Append("#if ");
        writer.AppendLine(str);
        action(writer);
        writer.AppendLine("#endif");
    };

    protected static readonly Action<string, Writer, Action<Writer>, Action<Writer>> IfElseDirective =
        (str, writer, actionIf, actionElse) =>
        {
            writer.Append("#if ");
            writer.AppendLine(str);
            actionIf(writer);
            writer.AppendLine("#else");
            actionElse(writer);
            writer.AppendLine("#endif");
        };


    protected static readonly Action<Writer> EofCheck = writer =>
    {
        IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, writer, w =>
        {
            w.AppendLine("    if (reader.Eof)");
            w.AppendLine("    {");
            w.AppendLine("        value = default;");
            w.AppendLine("        return;");
            w.AppendLine("    }");
        });
    };

    protected static readonly Action<Writer> WriteAggressiveInlining = writer =>
    {
        writer.AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
    };

    protected string GetSerializeString(ITypeSymbol type, string valueName)
    {
        switch (type.GetKind(NinoGraph, GeneratedTypes))
        {
            case NinoTypeHelper.NinoTypeKind.Boxed:
                return $"NinoSerializer.SerializeBoxed({valueName}, ref writer, {valueName}?.GetType());";
            case NinoTypeHelper.NinoTypeKind.Unmanaged:
                return $"writer.UnsafeWrite<{type.GetDisplayString()}>({valueName});";
            case NinoTypeHelper.NinoTypeKind.NinoType:
                if (TryGetInlineSerializeCall(type, valueName, out var inlineSerialize))
                    return inlineSerialize;
                return $"NinoSerializer.Serialize<{type.GetDisplayString()}>({valueName}, ref writer);";
            case NinoTypeHelper.NinoTypeKind.BuiltIn:
                return $"Serializer.Serialize({valueName}, ref writer);";
            default:
                throw new InvalidOperationException(
                    $"Type {type.GetDisplayString()} is not supported for serialization.");
        }
    }

    protected string GetDeserializeString(ITypeSymbol type, string varName, bool isOutVariable = true,
        string readerName = "reader")
    {
        switch (type.GetKind(NinoGraph, GeneratedTypes))
        {
            case NinoTypeHelper.NinoTypeKind.Boxed:
                var boxedDecl = isOutVariable ? $"{type.GetDisplayString()} {varName} = " : $"{varName} = ";
                return $"{boxedDecl}NinoDeserializer.DeserializeBoxed(ref {readerName}, null);";
            case NinoTypeHelper.NinoTypeKind.Unmanaged:
                if (isOutVariable)
                    return $"{readerName}.UnsafeRead<{type.GetDisplayString()}>(out var {varName});";
                return $"{readerName}.UnsafeRead<{type.GetDisplayString()}>(out {varName});";
            case NinoTypeHelper.NinoTypeKind.NinoType:
                if (TryGetInlineDeserializeCall(type, byRef: false, isOutVariable, varName, readerName,
                        out var inlineDeserialize))
                    return inlineDeserialize;
                if (!type.IsSealedOrStruct())
                {
                    if (isOutVariable)
                        return
                            $"Deserializer.DeserializePolymorphic(out {type.GetDisplayString()} {varName}, ref {readerName});";
                    return $"Deserializer.DeserializePolymorphic(out {varName}, ref {readerName});";
                }

                if (isOutVariable)
                    return $"NinoDeserializer.Deserialize(out {type.GetDisplayString()} {varName}, ref {readerName});";
                return $"NinoDeserializer.Deserialize(out {varName}, ref {readerName});";
            case NinoTypeHelper.NinoTypeKind.BuiltIn:
                if (isOutVariable)
                    return $"Deserializer.Deserialize(out {type.GetDisplayString()} {varName}, ref {readerName});";
                return $"Deserializer.Deserialize(out {varName}, ref {readerName});";
            default:
                throw new InvalidOperationException(
                    $"Type {type.GetDisplayString()} is not supported for deserialization.");
        }
    }

    protected string GetDeserializeRefString(ITypeSymbol type, string varName, string readerName = "reader")
    {
        switch (type.GetKind(NinoGraph, GeneratedTypes))
        {
            case NinoTypeHelper.NinoTypeKind.Boxed:
                return $"NinoDeserializer.DeserializeRefBoxed(ref {varName}, ref {readerName}, null);";
            case NinoTypeHelper.NinoTypeKind.Unmanaged:
                return $"{readerName}.UnsafeRead(out {varName});";
            case NinoTypeHelper.NinoTypeKind.NinoType:
                if (TryGetInlineDeserializeCall(type, byRef: true, isOutVariable: false, varName, readerName,
                        out var inlineRefDeserialize))
                    return inlineRefDeserialize;
                if (!type.IsSealedOrStruct())
                {
                    return $"Deserializer.DeserializeRefPolymorphic(ref {varName}, ref {readerName});";
                }

                return $"NinoDeserializer.DeserializeRef(ref {varName}, ref {readerName});";
            case NinoTypeHelper.NinoTypeKind.BuiltIn:
                return $"Deserializer.DeserializeRef(ref {varName}, ref {readerName});";
            default:
                throw new InvalidOperationException(
                    $"Type {type.GetDisplayString()} is not supported for ref deserialization.");
        }
    }

    /// <summary>
    /// Generates correct array creation syntax for both jagged and multi-dimensional arrays.
    /// Handles complex cases like int[][], int[,][], int[][,], etc.
    /// </summary>
    /// <param name="elementTypeString">The element type as a string (e.g., "int[]", "byte[,]")</param>
    /// <param name="dimensionString">The dimension specifier (e.g., "length", "len0, len1")</param>
    /// <returns>Correct array creation string (e.g., "int[length][]", "int[len0, len1][]")</returns>
    protected static string GetArrayCreationString(string elementTypeString, string dimensionString)
    {
        // Find the first '[' that's not inside angle brackets (generics)
        // Insert [dimensionString] before it
        // Examples:
        //   "int[]" + "length" -> "int[length][]"
        //   "byte[,]" + "length" -> "byte[length][,]"
        //   "int[][,]" + "length" -> "int[length][][,]"
        //   "int" + "len0, len1" -> "int[len0, len1]"
        //   "Dictionary<string, int[]>" + "length" -> "Dictionary<string, int[]>[length]"

        int angleDepth = 0;
        int firstBracket = -1;

        for (int i = 0; i < elementTypeString.Length; i++)
        {
            if (elementTypeString[i] == '<') angleDepth++;
            else if (elementTypeString[i] == '>') angleDepth--;
            else if (elementTypeString[i] == '[' && angleDepth == 0)
            {
                firstBracket = i;
                break;
            }
        }

        // If no brackets found, append dimension at the end
        if (firstBracket < 0)
            return $"{elementTypeString}[{dimensionString}]";

        // Insert dimension before the first bracket
        return elementTypeString.Insert(firstBracket, $"[{dimensionString}]");
    }

    private bool TryGetInlineSerializeCall(ITypeSymbol type, string valueExpression, out string invocation)
    {
        invocation = null!;

        if (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var ninoType))
            return false;

        if (!string.IsNullOrEmpty(ninoType.CustomSerializer))
            return false;

        if (!ninoType.TypeSymbol.IsSealedOrStruct())
            return false;

        invocation = $"Serializer.SerializeImpl({valueExpression}, ref writer);";
        return true;
    }

    private bool TryGetInlineDeserializeCall(ITypeSymbol type, bool byRef, bool isOutVariable, string varName,
        string readerName, out string invocation)
    {
        invocation = null!;

        if (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var ninoType))
            return false;

        if (!string.IsNullOrEmpty(ninoType.CustomDeserializer))
            return false;

        if (!ninoType.TypeSymbol.IsSealedOrStruct())
            return false;

        if (byRef)
        {
            invocation = $"Deserializer.DeserializeImplRef(ref {varName}, ref {readerName});";
            return true;
        }

        invocation = isOutVariable
            ? $"Deserializer.DeserializeImpl(out {type.GetDisplayString()} {varName}, ref {readerName});"
            : $"Deserializer.DeserializeImpl(out {varName}, ref {readerName});";
        return true;
    }

    /// <summary>
    /// Generates code for this built-in type generator and returns the results.
    /// Used by NinoBuiltInTypesGenerator to consolidate multiple generators into single output files.
    /// </summary>
    public (string serializerCode, string deserializerCode, HashSet<ITypeSymbol> registeredTypes) GenerateCode(
        HashSet<ITypeSymbol> typesToProcess)
    {
        _registeredTypes.Clear();
        Writer serializerWriter = new("        ");
        serializerWriter.AppendLine();
        Writer deserializerWriter = new("        ");
        deserializerWriter.AppendLine();

        foreach (var typeSymbol in typesToProcess)
        {
            if (!Filter(typeSymbol)) continue;
            GenerateSerializer(typeSymbol, serializerWriter);
            GenerateDeserializer(typeSymbol, deserializerWriter);
            serializerWriter.AppendLine();
            deserializerWriter.AppendLine();
            _registeredTypes.Add(typeSymbol);
        }

        return (serializerWriter.ToString(), deserializerWriter.ToString(),
            new HashSet<ITypeSymbol>(_registeredTypes, TupleSanitizedEqualityComparer.Default));
    }

    protected override void Generate(SourceProductionContext spc)
    {
        var (serializerCode, deserializerCode, registeredTypes) = GenerateCode(potentialTypes);

        if (registeredTypes.Count == 0)
        {
            return; // No types to generate
        }

        _registeredTypes.Clear();
        foreach (var type in registeredTypes)
        {
            _registeredTypes.Add(type);
        }

        StringBuilder registrationCode = new();
        foreach (var registeredType in _registeredTypes)
        {
            var typeName = registeredType.GetDisplayString();
            registrationCode.AppendLine(
                $"                NinoTypeMetadata.RegisterSerializer<{typeName}>(Serializer.Serialize, false);");
            registrationCode.AppendLine(
                $"                NinoTypeMetadata.RegisterDeserializer<{typeName}>(-1, Deserializer.Deserialize, Deserializer.DeserializeRef, Deserializer.Deserialize, Deserializer.DeserializeRef, false);");
        }

        var curNamespace = Compilation.AssemblyName!.GetNamespace();

        // generate code
        var code = $$"""
                     // <auto-generated/>
                     #nullable disable
                     #pragma warning disable CS8669

                     using System;
                     using global::Nino.Core;
                     using System.Buffers;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class Serializer
                         {
                     {{serializerCode}}    }
                     }
                     """;

        spc.AddSource($"{OutputFileName}.Serializer.g.cs", code);

        code = $$"""
                 // <auto-generated/>
                 #nullable disable
                 #pragma warning disable CS8669

                 using System;
                 using global::Nino.Core;
                 using System.Buffers;
                 using System.Collections.Generic;
                 using System.Collections.Concurrent;
                 using System.Runtime.InteropServices;
                 using System.Runtime.CompilerServices;

                 namespace {{curNamespace}}
                 {
                     public static partial class Deserializer
                     {
                 {{deserializerCode}}    }
                 }
                 """;
        spc.AddSource($"{OutputFileName}.Deserializer.g.cs", code);

        // Generate registration code
        if (registrationCode.Length > 0)
        {
            // Unity initialization code (conditional)
            var unityInitCode = IsUnityAssembly ? """

                     #if UNITY_2020_2_OR_NEWER
                     #if UNITY_EDITOR
                             [UnityEditor.InitializeOnLoadMethod]
                             private static void InitEditor() => Init();
                     #endif

                             [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
                             private static void InitRuntime() => Init();
                     #endif
                     """ : string.Empty;

            code = $$"""
                     // <auto-generated/>
                     #nullable disable
                     #pragma warning disable CS8669

                     using System;
                     using global::Nino.Core;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         internal static class {{OutputFileName}}Registration
                         {
                             private static bool _initialized;
                             private static object _lock = new object();


                             #if NET5_0_OR_GREATER
                             [ModuleInitializer]
                             #endif
                             public static void Init()
                             {
                                 lock (_lock)
                                 {
                                     if (_initialized)
                                         return;

                     {{registrationCode}}
                                     _initialized = true;
                                 }
                             }
                     {{unityInitCode}}
                         }
                     }
                     """;
            spc.AddSource($"{OutputFileName}.Registration.g.cs", code);
        }
    }
}