using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Filter;
using Nino.Generator.Filter.Operation;
using Nino.Generator.Metadata;
using Nino.Generator.Template;
using Array = Nino.Generator.Filter.Array;
using Nullable = Nino.Generator.Filter.Nullable;
using String = Nino.Generator.Filter.String;

namespace Nino.Generator.Collection;

public class CollectionSerializerGenerator(
    Compilation compilation,
    List<ITypeSymbol> potentialCollectionSymbols,
    NinoGraph ninoGraph)
    : NinoCollectionGenerator(compilation, potentialCollectionSymbols, ninoGraph)
{
    protected override IFilter Selector => new Joint().With
    (
        // We want to ensure the type we are using is accessible (i.e. not private)
        new Accessible(),
        // We want to ensure all generics are fully-typed
        new Not(new RawGeneric()),
        // We now collect things we want
        new Union().With
        (
            // We accept unmanaged
            new Unmanaged(),
            // We accept NinoTyped
            new NinoTyped(),
            // We accept strings
            new String(),
            // We want key-value pairs for dictionaries
            new Joint().With
            (
                new Trivial("KeyValuePair"),
                new Not(new AnyTypeArgument(symbol => !ValidFilter(symbol)))
            ),
            // We want tuples
            new Joint().With
            (
                new Trivial("ValueTuple", "Tuple"),
                new Not(new AnyTypeArgument(symbol => !ValidFilter(symbol)))
            ),
            // We want nullables
            new Nullable(),
            // We want enumerable (which contains array, icollection, ilist, idictionary, etc)
            new Interface("IEnumerable<T>", interfaceSymbol =>
            {
                var elementType = interfaceSymbol.TypeArguments[0];
                return ValidFilter(elementType);
            })
        )
    );

    protected override string ClassName => "Serializer";

    protected override string OutputFileName =>
        $"{Compilation.AssemblyName!.GetNamespace()}.Serializer.Collection.g.cs";

    protected override void PublicMethod(StringBuilder sb, string typeFullName)
    {
        sb.GenerateClassSerializeMethods(typeFullName);
    }

    private string GetSerializeString(ITypeSymbol type, string value, string? serializerVar = null)
    {
        // unmanaged
        if (type.IsUnmanagedType &&
            (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var nt) ||
             !nt.IsPolymorphic()))
        {
            return $"writer.Write({value});";
        }

        // If serializer variable is provided, use cached serializer
        if (serializerVar != null)
        {
            return $"{serializerVar}.Serialize({value}, ref writer);";
        }

        // Fallback to static method call
        return $"NinoSerializer.Serialize({value}, ref writer);";
    }

    private void GenerateCachedSerializers(HashSet<ITypeSymbol> types, Writer sb, out Dictionary<string, string> serializerVars)
    {
        serializerVars = new Dictionary<string, string>();
        
        foreach (var type in types)
        {
            // Skip unmanaged types - they don't need cached serializers
            if (type.IsUnmanagedType &&
                (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var nt) ||
                 !nt.IsPolymorphic()))
                continue;

            var typeDisplayName = type.GetDisplayString();
            var varName = type.GetCachedVariableName("serializer");
            
            // Handle potential duplicates by adding a counter
            var originalVarName = varName;
            int counter = 1;
            while (serializerVars.Values.Contains(varName))
            {
                varName = $"{originalVarName}_{counter}";
                counter++;
            }
            
            serializerVars[typeDisplayName] = varName;
            sb.AppendLine($"    var {varName} = CachedSerializer<{typeDisplayName}>.Instance;");
        }
        
        if (serializerVars.Count > 0)
        {
            sb.AppendLine();
        }
    }

    private string? GetCachedSerializerVar(ITypeSymbol type, Dictionary<string, string> serializerVars)
    {
        return serializerVars.TryGetValue(type.GetDisplayString(), out var varName) ? varName : null;
    }

    protected override List<Transformer> Transformers =>
    [
        new
        (
            "Nullable",
            // We want nullable for ninotypes (both unmanaged and managed)
            new Joint().With
            (
                new Nullable(),
                new TypeArgument(0, ValidFilter)
            )
            , (symbol, sb) =>
            {
                ITypeSymbol elementType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                
                // Collect types that need cached serializers
                HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                typesNeedingSerializers.Add(elementType);

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(elementType.GetDisplayString());
                sb.AppendLine("? value, ref Writer writer)");
                sb.AppendLine("{");
                
                // Generate cached serializers
                GenerateCachedSerializers(typesNeedingSerializers, sb, out var serializerVars);
                
                sb.AppendLine("    if (!value.HasValue)");
                sb.AppendLine("    {");
                sb.AppendLine("        writer.Write(false);");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    writer.Write(true);");
                
                var serializerVar = GetCachedSerializerVar(elementType, serializerVars);
                sb.Append("    ");
                sb.AppendLine(GetSerializeString(elementType, "value.Value", serializerVar));
                sb.AppendLine("}");
                return true;
            }
        ),
        // KeyValuePair Ninotypes
        new
        (
            "KeyValuePair",
            // We want KeyValuePair for ninotypes (both unmanaged and managed)
            new Trivial("KeyValuePair"),
            (symbol, sb) =>
            {
                GenericTupleLikeMethods(symbol, sb,
                    ((INamedTypeSymbol)symbol).TypeArguments.ToArray(),
                    "Key", "Value");
                return true;
            }
        ),
        // Tuple Ninotypes
        new
        (
            "Tuple",
            // We only want Tuple for non-unmanaged ninotypes
            new Trivial("ValueTuple", "Tuple"),
            (symbol, sb) =>
            {
                if (symbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.TypeArguments.IsEmpty)
                    return false;
                var types = ((INamedTypeSymbol)symbol).TypeArguments.ToArray();
                GenericTupleLikeMethods(symbol, sb,
                    types,
                    types.Select((_, i) => $"Item{i + 1}").ToArray());
                return true;
            }),
        // Array Ninotypes
        new
        (
            "Array",
            new Array(symbol => ValidFilter(symbol.ElementType)),
            (symbol, sb) =>
            {
                var elementType = ((IArrayTypeSymbol)symbol).ElementType;
                bool isUnmanaged = elementType.IsUnmanagedType;

                // Collect types that need cached serializers
                HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                if (!isUnmanaged)
                {
                    typesNeedingSerializers.Add(elementType);
                }

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");

                if (isUnmanaged)
                {
                    sb.AppendLine("    writer.Write(value);");
                }
                else
                {
                    // Generate cached serializers for non-unmanaged types
                    GenerateCachedSerializers(typesNeedingSerializers, sb, out var serializerVars);
                    
                    sb.AppendLine("    if (value == null)");
                    sb.AppendLine("    {");
                    sb.AppendLine("        writer.Write(TypeCollector.NullCollection);");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    // Optimized array serialization - use span for better performance
                    sb.AppendLine("    var span = value.AsSpan();");
                    sb.AppendLine("    int cnt = span.Length;");
                    sb.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
                    sb.AppendLine();
                    // Optimized array serialization loop
                    sb.AppendLine("    for (int i = 0; i < cnt; i++)");
                    sb.AppendLine("    {");
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        var pos = writer.Advance(4);"); });
                    
                    var serializerVar = GetCachedSerializerVar(elementType, serializerVars);
                    sb.AppendLine($"        {GetSerializeString(elementType, "span[i]", serializerVar)}");
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        writer.PutLength(pos);"); });
                    sb.AppendLine("    }");
                }

                sb.AppendLine("}");
                return true;
            }),
        // non trivial IDictionary Ninotypes
        new
        (
            "NonTrivialDictionary",
            // Note that we accept non-trivial IDictionary types with unmanaged key and value types
            new Joint().With
            (
                new Interface("IDictionary<TKey, TValue>"),
                new NonTrivial("IDictionary", "IDictionary", "Dictionary")
            ),
            (symbol, sb) =>
            {
                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var idictSymbol = dictSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                  ?? dictSymbol;
                var keyType = idictSymbol.TypeArguments[0];
                var valType = idictSymbol.TypeArguments[1];
                if (!ValidFilter(keyType) || !ValidFilter(valType)) return false;

                bool isUnmanaged = keyType.IsUnmanagedType && valType.IsUnmanagedType;

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");

                ValidMethod equalityMethod = new ValidMethod((_, method) =>
                    method.MethodKind is MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator
                    && method is { Name: "op_Equality", Parameters.Length: 2 }
                    && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type,
                        dictSymbol)
                    && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type,
                        dictSymbol));

                bool shouldHaveIfDefaultCheck = !symbol.IsValueType || equalityMethod.Filter(dictSymbol);
                if (shouldHaveIfDefaultCheck)
                {
                    sb.Append("    if (value == ");
                    sb.Append(symbol.IsValueType ? "default" : "null");
                    sb.AppendLine(")");
                    sb.AppendLine("    {");
                    sb.AppendLine("        writer.Write(TypeCollector.NullCollection);");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                }

                // Generate cached serializers for non-unmanaged types
                Dictionary<string, string>? serializerVars = null;
                if (!isUnmanaged)
                {
                    HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                    if (!keyType.IsUnmanagedType)
                        typesNeedingSerializers.Add(keyType);
                    if (!valType.IsUnmanagedType)
                        typesNeedingSerializers.Add(valType);
                    
                    GenerateCachedSerializers(typesNeedingSerializers, sb, out serializerVars);
                }

                sb.AppendLine("    int cnt = value.Count;");
                sb.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
                sb.AppendLine();
                // Optimized dictionary enumeration
                sb.AppendLine("    foreach (var item in value)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.AppendLine("        writer.Write(item);");
                }
                else
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        var pos = writer.Advance(4);"); });
                    
                    var keySerializerVar = GetCachedSerializerVar(keyType, serializerVars!);
                    var valSerializerVar = GetCachedSerializerVar(valType, serializerVars!);
                    sb.AppendLine($"        {GetSerializeString(keyType, "item.Key", keySerializerVar)}");
                    sb.AppendLine($"        {GetSerializeString(valType, "item.Value", valSerializerVar)}");
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        writer.PutLength(pos);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                return true;
            }
        ),
        // trivial IDictionary Ninotypes
        new
        (
            "TrivialDictionary",
            new Joint().With
            (
                new Interface("IDictionary<TKey, TValue>"),
                new Not(new NonTrivial("IDictionary", "IDictionary", "Dictionary"))
            ),
            (symbol, sb) =>
            {
                var keyType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                var valueType = ((INamedTypeSymbol)symbol).TypeArguments[1];
                bool isUnmanaged = keyType.IsUnmanagedType && valueType.IsUnmanagedType;

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");

                sb.Append("    if (value == ");
                sb.Append(symbol.IsValueType ? "default" : "null");
                sb.AppendLine(")");
                sb.AppendLine("    {");
                sb.AppendLine("        writer.Write(TypeCollector.NullCollection);");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                // Generate cached serializers for non-unmanaged types
                Dictionary<string, string>? serializerVars = null;
                if (!isUnmanaged)
                {
                    HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                    if (!keyType.IsUnmanagedType)
                        typesNeedingSerializers.Add(keyType);
                    if (!valueType.IsUnmanagedType)
                        typesNeedingSerializers.Add(valueType);
                    
                    GenerateCachedSerializers(typesNeedingSerializers, sb, out serializerVars);
                }

                sb.AppendLine("    int cnt = value.Count;");
                sb.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
                sb.AppendLine();
                sb.AppendLine("    foreach (var item in value)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.AppendLine("        writer.Write(item);");
                }
                else
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        var pos = writer.Advance(4);"); });
                    
                    var keySerializerVar = GetCachedSerializerVar(keyType, serializerVars!);
                    var valueSerializerVar = GetCachedSerializerVar(valueType, serializerVars!);
                    sb.Append("        ");
                    sb.AppendLine(GetSerializeString(keyType, "item.Key", keySerializerVar));
                    sb.Append("        ");
                    sb.AppendLine(GetSerializeString(valueType, "item.Value", valueSerializerVar));
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        writer.PutLength(pos);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                return true;
            }),
        // non trivial IEnumerable Ninotypes
        new
        (
            "NonTrivialEnumerable",
            // Note that we accept non-trivial IEnumerable types with unmanaged element types
            new Joint().With
            (
                // Note that array is an IEnumerable, but we don't want to generate code for it
                new Interface("IEnumerable<T>"),
                new Not(new Array()),
                // We want to exclude the ones that already have a serializer
                new Not(new NinoTyped()),
                new Not(new String()),
                new NonTrivial("IEnumerable", "ICollection", "IList", "List"),
                // Ensure to have a property called Count
                new ValidProperty((_, property) =>
                    property.Name == "Count" && property.Type.SpecialType == SpecialType.System_Int32)
            ),
            (symbol, sb) =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!ValidFilter(elemType)) return false;

                bool isUnmanaged = elemType.IsUnmanagedType;

                IFilter equalityMethod = new ValidMethod((_, method) =>
                    method.MethodKind is MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator
                    && method is { Name: "op_Equality", Parameters.Length: 2 }
                    && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type,
                        namedTypeSymbol)
                    && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type,
                        namedTypeSymbol));

                bool shouldHaveIfDefaultCheck = !symbol.IsValueType || equalityMethod.Filter(namedTypeSymbol);

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");

                if (shouldHaveIfDefaultCheck)
                {
                    sb.Append("    if (value == ");
                    sb.Append(symbol.IsValueType ? "default" : "null");
                    sb.AppendLine(")");
                    sb.AppendLine("    {");
                    sb.AppendLine("        writer.Write(TypeCollector.NullCollection);");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                }

                // Generate cached serializers for non-unmanaged types
                Dictionary<string, string>? serializerVars = null;
                if (!isUnmanaged)
                {
                    HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                    if (!elemType.IsUnmanagedType)
                        typesNeedingSerializers.Add(elemType);
                    
                    GenerateCachedSerializers(typesNeedingSerializers, sb, out serializerVars);
                }

                sb.AppendLine("    int cnt = value.Count;");
                sb.AppendLine("    writer.Write(TypeCollector.GetCollectionHeader(cnt));");
                sb.AppendLine();
                // Optimized enumerable serialization
                sb.AppendLine("    foreach (var item in value)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.AppendLine("        writer.Write(item);");
                }
                else
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        var pos = writer.Advance(4);"); });
                    
                    var elemSerializerVar = GetCachedSerializerVar(elemType, serializerVars!);
                    sb.AppendLine($"        {GetSerializeString(elemType, "item", elemSerializerVar)}");
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        writer.PutLength(pos);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                return true;
            }
        ),
        // trivial unmanaged IList Ninotypes
        new
        (
            "TrivialUnmanagedIList",
            new Joint().With
            (
                new Interface("IList<T>"),
                new TypeArgument(0, symbol => symbol.IsUnmanagedType),
                new Not(new Array())
            ),
            (symbol, sb) =>
            {
                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");
                sb.AppendLine("        writer.Write(value);");
                sb.AppendLine("}");
                return true;
            }
        ),
        // trivial IEnumerable Ninotypes
        new
        (
            "TrivialEnumerable",
            new Joint().With
            (
                // Note that array is an IEnumerable, but we don't want to generate code for it
                new Interface("IEnumerable<T>"),
                new Not(new Array()),
                new Not(new NonTrivial("IEnumerable", "IEnumerable", "ICollection", "IList", "List"))
            ),
            (symbol, sb) =>
            {
                var elementType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                bool isUnmanaged = elementType.IsUnmanagedType;

                sb.AppendLine(Inline);
                sb.Append("public static void Serialize(this ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Writer writer)");
                sb.AppendLine("{");
                sb.Append("    if (value == ");
                sb.Append(symbol.IsValueType ? "default" : "null");
                sb.AppendLine(")");
                sb.AppendLine("    {");
                sb.AppendLine("        writer.Write(TypeCollector.NullCollection);");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                // Generate cached serializers for non-unmanaged types
                Dictionary<string, string>? serializerVars = null;
                if (!isUnmanaged)
                {
                    HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
                    if (!elementType.IsUnmanagedType)
                        typesNeedingSerializers.Add(elementType);
                    
                    GenerateCachedSerializers(typesNeedingSerializers, sb, out serializerVars);
                }

                sb.AppendLine("    int cnt = 0;");
                sb.AppendLine("    int oldPos = writer.Advance(4);");
                sb.AppendLine();
                sb.AppendLine("    foreach (var item in value)");
                sb.AppendLine("    {");
                sb.AppendLine("        cnt++;");

                if (isUnmanaged)
                {
                    sb.AppendLine("        writer.Write(item);");
                }
                else
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        var pos = writer.Advance(4);"); });
                    
                    var elemSerializerVar = GetCachedSerializerVar(elementType, serializerVars!);
                    sb.Append("        ");
                    sb.AppendLine(GetSerializeString(elementType, "item", elemSerializerVar));
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("        writer.PutLength(pos);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    writer.PutBack(TypeCollector.GetCollectionHeader(cnt), oldPos);");
                sb.AppendLine("}");
                return true;
            }
        )
    ];

    private void GenericTupleLikeMethods(ITypeSymbol type, Writer writer, ITypeSymbol[] types, params string[] fields)
    {
        bool isUnmanaged = type.IsUnmanagedType;
        writer.AppendLine(Inline);
        writer.Append("public static void Serialize(this ");
        writer.Append(type.GetDisplayString());
        writer.AppendLine(" value, ref Writer writer)");
        writer.AppendLine("{");
        if (isUnmanaged)
        {
            writer.AppendLine("    writer.Write(value);");
        }
        else
        {
            // Generate cached serializers for non-unmanaged field types
            HashSet<ITypeSymbol> typesNeedingSerializers = new(SymbolEqualityComparer.Default);
            foreach (var fieldType in types)
            {
                if (!fieldType.IsUnmanagedType)
                    typesNeedingSerializers.Add(fieldType);
            }
            
            Dictionary<string, string>? serializerVars = null;
            if (typesNeedingSerializers.Count > 0)
            {
                GenerateCachedSerializers(typesNeedingSerializers, writer, out serializerVars);
            }

            for (int i = 0; i < fields.Length; i++)
            {
                writer.Append("    ");
                var fieldSerializerVar = serializerVars != null ? GetCachedSerializerVar(types[i], serializerVars) : null;
                writer.AppendLine(GetSerializeString(types[i], $"value.{fields[i]}", fieldSerializerVar));
            }
        }

        writer.AppendLine("}");
    }
}