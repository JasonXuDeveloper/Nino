using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;

namespace Nino.Generator.Common;

public partial class SerializerGenerator
{
    private void GenerateTrivialCode(SourceProductionContext spc, HashSet<ITypeSymbol> generatedTypes)
    {
        var compilation = Compilation;
        var sb = new StringBuilder();
        sb.GenerateClassSerializeMethods("string");
        HashSet<string> generatedTypeNames = new();

        foreach (var ninoType in NinoTypes)
        {
            try
            {
                if (!generatedTypes.Add(ninoType.TypeSymbol))
                    continue;
                if (!generatedTypeNames.Add(ninoType.TypeSymbol.GetDisplayString()))
                    continue;


                if (!ninoType.TypeSymbol.IsSealedOrStruct())
                {
                    sb.AppendLine();
                    sb.AppendLine($$"""
                                            [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                            public static void SerializePolymorphic({{ninoType.TypeSymbol.GetTypeFullName()}} value, ref Writer writer)
                                            {
                                                    switch(value)
                                                    {
                                                        case null:
                                                        {
                                                            writer.Write(TypeCollector.Null);
                                                            return;
                                                        }
                                    """);
                    if (NinoGraph.SubTypes.TryGetValue(ninoType, out var subTypes))
                    {
                        foreach (var subType in subTypes)
                        {
                            if (!subType.TypeSymbol.IsInstanceType())
                                continue;

                            var valName = subType.TypeSymbol.GetCachedVariableName("val_");
                            sb.AppendLine($$"""
                                                                case {{subType.TypeSymbol.GetTypeFullName()}} {{valName}}:
                                                                {
                                            """);

                            sb.AppendLine(
                                $"                        writer.Write(NinoTypeConst.{subType.TypeSymbol.GetTypeFullName().GetTypeConstName()});");

                            // Optimized write path - direct write for unmanaged types
                            if (subType.TypeSymbol.IsUnmanagedType)
                            {
                                sb.AppendLine($"                        writer.Write({valName});");
                            }
                            else
                            {
                                WriteMembers(subType, valName, sb, "            ");
                            }

                            sb.AppendLine("                        return;");
                            sb.AppendLine("                    }");
                        }
                    }

                    sb.AppendLine("                    default:");
                    sb.AppendLine(
                        $"                        CachedSerializer<{ninoType.TypeSymbol.GetTypeFullName()}>.SerializePolymorphic(value, ref writer);");
                    sb.AppendLine("                        break;");
                    sb.AppendLine("                }");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }

                if (!ninoType.TypeSymbol.IsInstanceType() ||
                    !string.IsNullOrEmpty(ninoType.CustomSerializer))
                    continue;
                sb.AppendLine();
                sb.AppendLine($$"""
                                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                        public static void SerializeImpl({{ninoType.TypeSymbol.GetTypeFullName()}} value, ref Writer writer)
                                        {
                                """);

                if (!ninoType.TypeSymbol.IsValueType)
                {
                    sb.AppendLine("            if(value == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                writer.Write(TypeCollector.Null);");
                    sb.AppendLine("                return;");
                    sb.AppendLine("            }");
                    sb.AppendLine();
                }

                // Optimize polymorphic type writing - inline constant when possible
                if (ninoType.IsPolymorphic())
                {
                    sb.AppendLine(
                        $"            writer.Write(NinoTypeConst.{ninoType.TypeSymbol.GetTypeFullName().GetTypeConstName()});");
                }

                // Optimized write path - direct write for unmanaged types
                if (ninoType.TypeSymbol.IsUnmanagedType)
                {
                    sb.AppendLine("            writer.Write(value);");
                }
                else
                {
                    WriteMembers(ninoType, "value", sb);
                }

                sb.AppendLine("        }");
                sb.AppendLine();
            }
            catch (Exception e)
            {
                sb.AppendLine($"/* Error: {e.Message} for type {ninoType.TypeSymbol.GetTypeFullName()}");
                //add stacktrace
                foreach (var line in e.StackTrace.Split('\n'))
                {
                    sb.AppendLine($" * {line}");
                }

                //end error
                sb.AppendLine(" */");
            }
        }

        var curNamespace = compilation.AssemblyName!.GetNamespace();

        // Collect all custom formatters for static field generation
        var globalCustomFormatters = CollectGlobalCustomFormatters();
        var staticFormatterFields = GenerateStaticFormatterFields(globalCustomFormatters);

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using System.Buffers;
                     using System.Threading;
                     using global::Nino.Core;
                     using System.ComponentModel;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class Serializer
                         {{{staticFormatterFields}}

                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize(bool value)
                             {
                                 if (value)
                                     return new byte[1] { 1 };
                                
                                 return new byte[1] { 0 };
                             }
                             
                             [MethodImpl(MethodImplOptions.AggressiveInlining)]
                             public static byte[] Serialize(byte value)
                             {
                                 return new byte[1] { value };
                             }

                     {{GenerateWriterAccessMethodBody("string", "        ")}}

                     {{sb}}    }
                     }
                     """;

        spc.AddSource($"{curNamespace}.Serializer.g.cs", code);
    }

    private static string GenerateWriterAccessMethodBody(string typeName, string indent = "",
        string typeParam = "",
        string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Serialize{{typeParam}}({{typeName}} value, ref Writer writer) {{genericConstraint}}
                    {
                        writer.Write(value);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    private Dictionary<string, (ITypeSymbol FormatterType, ITypeSymbol ValueType)> CollectGlobalCustomFormatters()
    {
        var globalCustomFormatters = new Dictionary<string, (ITypeSymbol FormatterType, ITypeSymbol ValueType)>();

        foreach (var ninoType in NinoTypes)
        {
            foreach (var member in ninoType.Members)
            {
                if (member.HasCustomFormatter())
                {
                    var formatterType = member.CustomFormatterType();
                    if (formatterType != null)
                    {
                        var key = $"{formatterType.GetDisplayString()}_{member.Type.GetDisplayString()}";
                        globalCustomFormatters[key] = (formatterType, member.Type);
                    }
                }
            }
        }

        return globalCustomFormatters;
    }

    private string GenerateStaticFormatterFields(
        Dictionary<string, (ITypeSymbol FormatterType, ITypeSymbol ValueType)> globalCustomFormatters)
    {
        if (globalCustomFormatters.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("        // Static formatter fields for optimal performance");

        foreach (var kvp in globalCustomFormatters)
        {
            var formatterType = kvp.Value.FormatterType;
            var valueType = kvp.Value.ValueType;
            var varName = formatterType.GetCachedVariableName("formatter");
            sb.AppendLine(
                $"        private static readonly {formatterType.GetDisplayString()} {varName} = NinoFormatterInstance<{formatterType.GetDisplayString()}, {valueType.GetDisplayString()}>.Instance;");
        }

        return sb.ToString();
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

        invocation = $"Serializer.SerializeImpl({valueExpression}, ref writer)";
        return true;
    }

    private void WriteMembers(NinoType type, string valName, StringBuilder sb, string indent = "")
    {
        // First pass: collect all types that need serializers or custom formatters
        HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
        HashSet<NinoMember> membersWithCustomFormatters = new();

        foreach (var members in type.GroupByPrimitivity())
        {
            // Collect types for single-member serialization that use NinoSerializer.Serialize
            if (members.Count == 1)
            {
                var member = members[0];
                if (!(member.Type.SpecialType == SpecialType.System_String && member.IsUtf8String))
                {
                    if (member.HasCustomFormatter())
                    {
                        membersWithCustomFormatters.Add(member);
                    }
                    else
                    {
                        var kind = member.Type.GetKind(NinoGraph, GeneratedBuiltInTypes);
                        if (kind != NinoTypeHelper.NinoTypeKind.Unmanaged &&
                            kind != NinoTypeHelper.NinoTypeKind.Boxed &&
                            member.Type.SpecialType != SpecialType.System_String)
                        {
                            typesNeedingSerializers.Add(member.Type);
                        }
                    }
                }
            }
        }

        // No need to generate serializer variable declarations since CachedSerializer is static
        Dictionary<string, string> serializerVarsByType = new();
        foreach (var serializerType in typesNeedingSerializers)
        {
            var typeDisplayName = serializerType.GetDisplayString();
            // ReSharper disable once PossibleUnintendedLinearSearchInSet
            bool isBuiltIn = GeneratedBuiltInTypes.Contains(serializerType, TupleSanitizedEqualityComparer.Default);

            if (!isBuiltIn)
            {
                // Store the type name for direct static access
                serializerVarsByType[typeDisplayName] = $"CachedSerializer<{typeDisplayName}>";
            }
        }

        // Use static formatter fields instead of local variables
        Dictionary<NinoMember, string> customFormatterVarsByMember = new();
        foreach (var member in membersWithCustomFormatters)
        {
            var formatterType = member.CustomFormatterType();
            if (formatterType != null)
            {
                var varName = formatterType.GetCachedVariableName("formatter");
                customFormatterVarsByMember[member] = varName;
                // Note: Static field should be generated at class level, not as local variable
            }
        }

        // Helper to get serializer variable name for a type
        string GetSerializerVarName(ITypeSymbol serializerType)
        {
            return serializerVarsByType[serializerType.GetDisplayString()];
        }

        List<string> valNames = new();
        foreach (var members in type.GroupByPrimitivity())
        {
            valNames.Clear();
            foreach (var member in members)
            {
                var name = member.Name;
                var isPrivate = member.IsPrivate;
                var isProperty = member.IsProperty;
                var val = $"{valName}.{name}";

                if (isPrivate)
                {
                    var accessName = valName;
                    if (type.TypeSymbol.IsValueType)
                    {
                        accessName = $"ref {valName}";
                    }

                    val = isProperty
                        ? $"PrivateAccessor.__get__{name}__({accessName})"
                        : $"PrivateAccessor.__{name}__({accessName})";
                    var legacyVal = $"{valName}.__nino__generated__{name}";
                    val = $"""

                           #if NET8_0_OR_GREATER
                                                   {val}
                           #else
                                                   {legacyVal}
                           #endif

                           """;
                }

                valNames.Add(val);
            }

            if (members.Count == 1)
            {
                var member = members[0];
                var declaredType = member.Type;
                var val = valNames[0];

                if (member.HasCustomFormatter())
                {
                    // PRIORITY 1: Custom formatter (highest priority)
                    if (customFormatterVarsByMember.TryGetValue(member, out var formatterVar))
                    {
                        sb.AppendLine($"{indent}            {formatterVar}.Serialize({val}, ref writer);");
                    }
                }
                else
                {
                    var kind = declaredType.GetKind(NinoGraph, GeneratedBuiltInTypes);

                    switch (kind)
                    {
                        case NinoTypeHelper.NinoTypeKind.Unmanaged:
                            // PRIORITY 2: Unmanaged types - write directly
                            sb.AppendLine($"{indent}            writer.Write({val});");
                            break;

                        case NinoTypeHelper.NinoTypeKind.Boxed:
                            // PRIORITY 3: Object type - call boxed API in NinoSerializer directly
                            sb.AppendLine(
                                $"{indent}            NinoSerializer.SerializeBoxed({val}, ref writer, {val}?.GetType());");
                            break;

                        case NinoTypeHelper.NinoTypeKind.BuiltIn:
                            // PRIORITY 4: String types (UTF8 and UTF16 optimizations) or built-in types
                            if (declaredType.SpecialType == SpecialType.System_String)
                            {
                                if (member.IsUtf8String)
                                {
                                    sb.AppendLine($"{indent}            writer.WriteUtf8({val});");
                                }
                                else
                                {
                                    sb.AppendLine($"{indent}            writer.Write({val});");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"{indent}            Serializer.Serialize({val}, ref writer);");
                            }

                            break;

                        case NinoTypeHelper.NinoTypeKind.NinoType:
                            // PRIORITY 5: NinoType - use CachedSerializer
                            if (TryGetInlineSerializeCall(declaredType, val, out var inlineCall))
                            {
                                sb.AppendLine($"{indent}            {inlineCall};");
                            }
                            else if (!declaredType.IsSealedOrStruct())
                            {
                                sb.AppendLine(
                                    $"{indent}            Serializer.SerializePolymorphic({val}, ref writer);");
                            }
                            else
                            {
                                var serializerVar = GetSerializerVarName(declaredType);
                                sb.AppendLine($"{indent}            {serializerVar}.Serialize({val}, ref writer);");
                            }

                            break;
                    }
                }
            }
            else
            {
                // Standard path with version tolerance support
                sb.AppendLine($"{indent}#if {NinoTypeHelper.WeakVersionToleranceSymbol}");
                foreach (var val in valNames)
                {
                    sb.AppendLine($"{indent}            writer.Write({val});");
                }

                sb.AppendLine($"{indent}#else");
                sb.AppendLine($"{indent}            writer.Write(NinoTuple.Create({string.Join(", ", valNames)}));");
                sb.AppendLine($"{indent}#endif");
            }
        }
    }
}