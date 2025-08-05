using System;
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

public class CollectionDeserializerGenerator(
    Compilation compilation,
    List<ITypeSymbol> potentialCollectionSymbols,
    NinoGraph ninoGraph)
    : NinoCollectionGenerator(compilation, potentialCollectionSymbols, ninoGraph)
{
    protected override IFilter Selector =>
        new Joint().With
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
                // We want arrays
                new Array(arraySymbol => ValidFilter(arraySymbol.ElementType)),
                // We want dictionaries with valid indexers
                new Joint().With
                (
                    new Interface("IDictionary<TKey, TValue>", interfaceSymbol =>
                    {
                        var keyType = interfaceSymbol.TypeArguments[0];
                        var valueType = interfaceSymbol.TypeArguments[1];
                        return ValidFilter(keyType) && ValidFilter(valueType);
                    })
                ),
                // We want collections/lists with valid constructors
                new Joint().With
                (
                    // We want enumerable (which contains array, icollection, ilist, idictionary, etc)
                    new Interface("IEnumerable<T>", interfaceSymbol =>
                    {
                        var elementType = interfaceSymbol.TypeArguments[0];
                        return ValidFilter(elementType);
                    }),
                    new ValidMethod((symbol, method) =>
                    {
                        if (symbol.TypeKind == TypeKind.Interface) return true;
                        if (method.MethodKind == MethodKind.Constructor)
                        {
                            if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                            var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                                  i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                                              ?? namedTypeSymbol;
                            var elemType = ienumSymbol.TypeArguments[0];
                            // make array type from element type
                            var arrayType = Compilation.CreateArrayTypeSymbol(elemType);

                            return method.Parameters.Length == 0
                                   || (method.Parameters.Length == 1
                                       && method.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                                   || (method.Parameters.Length == 1
                                       && Compilation.HasImplicitConversion(arrayType, method.Parameters[0].Type));
                        }

                        return false;
                    })
                )
            )
        );

    protected override string ClassName => "Deserializer";

    protected override string OutputFileName =>
        $"{Compilation.AssemblyName!.GetNamespace()}.Deserializer.Collection.g.cs";

    protected override void PublicMethod(StringBuilder sb, string typeFullName)
    {
        sb.GenerateClassDeserializeMethods(typeFullName);
    }

    private static readonly Action<Writer> EofCheck = writer =>
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

    private string GetDeserializeString(ITypeSymbol type, bool assigned, string value, string reader = "reader",
        string? deserializerVar = null)
    {
        var typeFullName = assigned ? "" : $" {type.GetDisplayString()}";

        // unmanaged
        if (type.IsUnmanagedType &&
            (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var nt) ||
             !nt.IsPolymorphic()))
        {
            return $"{reader}.Read(out{typeFullName} {value});";
        }

        // If deserializer variable is provided, use cached deserializer
        if (deserializerVar != null)
        {
            return $"{deserializerVar}.Deserialize(out{typeFullName} {value}, ref {reader});";
        }

        // Fallback to static method call
        return $"NinoDeserializer.Deserialize(out{typeFullName} {value}, ref {reader});";
    }

    private void GenerateCachedDeserializers(HashSet<ITypeSymbol> types, Writer sb,
        out Dictionary<string, string> deserializerVars)
    {
        deserializerVars = new Dictionary<string, string>();

        foreach (var type in types)
        {
            // Skip unmanaged types - they don't need cached deserializers
            if (type.IsUnmanagedType &&
                (!NinoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var nt) ||
                 !nt.IsPolymorphic()))
                continue;

            var typeDisplayName = type.GetDisplayString();
            var typeName = typeDisplayName
                .Replace("global::", "")
                .ToLower()
                .Replace("[]", "_array") // Handle arrays specifically before general replacement
                .Replace("<", "_of_") // Handle generics more clearly
                .Replace(">", "_")
                .Replace(", ", "_and_") // Handle multiple generic parameters
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .Aggregate("", (current, c) => current + c)
                .Replace("___", "_") // Clean up multiple underscores
                .Replace("__", "_"); // Clean up double underscores
            var varName = $"deserializer_{typeName}";

            // Handle potential duplicates by adding a counter
            var originalVarName = varName;
            int counter = 1;
            while (deserializerVars.Values.Contains(varName))
            {
                varName = $"{originalVarName}_{counter}";
                counter++;
            }

            deserializerVars[typeDisplayName] = varName;
            sb.AppendLine($"    var {varName} = CachedDeserializer<{typeDisplayName}>.Instance;");
        }

        if (deserializerVars.Count > 0)
        {
            sb.AppendLine();
        }
    }

    private string? GetCachedDeserializerVar(ITypeSymbol type, Dictionary<string, string> deserializerVars)
    {
        return deserializerVars.TryGetValue(type.GetDisplayString(), out var varName) ? varName : null;
    }

    private static readonly Joint HasAddAndClear = new Joint().With(
        new ValidMethod((s, method) =>
        {
            if (s.TypeKind == TypeKind.Interface) return false;
            if (s is not INamedTypeSymbol ns) return false;
            var es = ns.AllInterfaces.FirstOrDefault(i =>
                         i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                     ?? ns;
            var elementType = es.TypeArguments[0];

            return method.Name == "Add"
                   && method.Parameters.Length == 1
                   && method.Parameters[0].Type.Equals(elementType, SymbolEqualityComparer.Default);
        }),
        new ValidMethod((s, method) =>
        {
            if (s.TypeKind == TypeKind.Interface) return false;
            if (s is not INamedTypeSymbol) return false;

            return method.Name == "Clear"
                   && method.Parameters.Length == 0;
        }));

    protected override List<Transformer> Transformers =>
    [
        new
        (
            "Nullable",
            // We want nullable for non-unmanaged ninotypes
            new Joint().With
            (
                new Nullable(),
                new TypeArgument(0, ValidFilter)
            )
            , (symbol, sb) =>
            {
                ITypeSymbol elementType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                var elementTypeFullName = elementType.GetDisplayString();

                // Collect types that need cached deserializers
                HashSet<ITypeSymbol> typesNeedingDeserializers = new(SymbolEqualityComparer.Default);
                typesNeedingDeserializers.Add(elementType);

                // Out overload
                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(elementTypeFullName);
                sb.AppendLine("? value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);

                // Generate cached deserializers
                GenerateCachedDeserializers(typesNeedingDeserializers, sb, out var deserializerVars);

                sb.AppendLine("    reader.Read(out bool hasValue);");
                sb.AppendLine("    if (!hasValue)");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                var deserializerVar = GetCachedDeserializerVar(elementType, deserializerVars);
                sb.Append("    ");
                sb.AppendLine(GetDeserializeString(elementType, false, "ret", "reader", deserializerVar));
                sb.AppendLine("    value = ret;");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - nullable is not modifiable, so just call out overload
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(elementTypeFullName);
                sb.AppendLine("? value, ref Reader reader) => Deserialize(out value, ref reader);");

                return true;
            }
        ),
        // KeyValuePair Ninotypes
        new
        (
            "KeyValuePair",
            // We only want KeyValuePair for non-unmanaged ninotypes
            new Joint().With(
                new Trivial("KeyValuePair")
            ),
            (symbol, sb) =>
            {
                GenericTupleLikeMethods(symbol, sb,
                    ((INamedTypeSymbol)symbol).TypeArguments.ToArray(),
                    "K", "V");
                return true;
            }),
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
                GenericTupleLikeMethods(symbol, sb,
                    ((INamedTypeSymbol)symbol).TypeArguments.ToArray(),
                    ((INamedTypeSymbol)symbol)
                    .TypeArguments.Select((_, i) => $"Item{i + 1}").ToArray());
                return true;
            }),
        // Array Ninotypes
        new
        (
            "Array",
            new Array(_ => true),
            (symbol, sb) =>
            {
                var elementType = ((IArrayTypeSymbol)symbol).ElementType;
                var elemType = elementType.GetDisplayString();
                var creationDecl = elemType.EndsWith("[]")
                    ? elemType.Insert(elemType.IndexOf("[]", StringComparison.Ordinal), "[length]")
                    : $"{elemType}[length]";
                var typeName = symbol.GetDisplayString();
                bool isUnmanaged = elementType.IsUnmanagedType &&
                                   elementType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

                // Collect types that need cached deserializers
                HashSet<ITypeSymbol> typesNeedingDeserializers = new(SymbolEqualityComparer.Default);
                if (!isUnmanaged)
                {
                    typesNeedingDeserializers.Add(elementType);
                }

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(typeName);
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);

                if (isUnmanaged)
                {
                    sb.AppendLine("    reader.Read(out value);");
                    sb.AppendLine("}");
                }
                else
                {
                    // Generate cached deserializers for non-unmanaged types
                    GenerateCachedDeserializers(typesNeedingDeserializers, sb, out var deserializerVars);

                    sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                    sb.AppendLine("    {");
                    sb.AppendLine("        value = null;");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    // Optimized array deserialization - reduce reader slicing overhead
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                    sb.Append("    value = new ");
                    sb.Append(creationDecl);
                    sb.AppendLine(";");
                    sb.AppendLine("    var span = value.AsSpan();");
                    sb.AppendLine("    for (int i = 0; i < length; i++)");
                    sb.AppendLine("    {");

                    var deserializerVar = GetCachedDeserializerVar(elementType, deserializerVars);
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.AppendLine(
                                $"        {GetDeserializeString(elementType, true, "span[i]", "eleReader", deserializerVar)}");
                        },
                        w =>
                        {
                            w.AppendLine(
                                $"        {GetDeserializeString(elementType, true, "span[i]", "reader", deserializerVar)}");
                        });
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }

                sb.AppendLine();

                // Ref overload - arrays are modifiable, resize and fill
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(typeName);
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                if (isUnmanaged)
                {
                    sb.AppendLine("    reader.ReadRef(ref value);");
                    sb.AppendLine("}");
                }
                else
                {
                    // Generate cached deserializers for ref overload too
                    GenerateCachedDeserializers(typesNeedingDeserializers, sb, out var refDeserializerVars);

                    sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                    sb.AppendLine("    {");
                    sb.AppendLine("        value = null;");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();

                    if (!isUnmanaged)
                    {
                        IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w => { w.AppendLine("    Reader eleReader;"); });
                        sb.AppendLine();
                    }

                    sb.AppendLine("    // Resize array if needed");
                    sb.AppendLine("    if (value == null || value.Length != length)");
                    sb.AppendLine("    {");
                    sb.Append("        value = new ");
                    sb.Append(creationDecl);
                    sb.AppendLine(";");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    sb.AppendLine("    var span = value.AsSpan();");
                    sb.AppendLine("    for (int i = 0; i < length; i++)");
                    sb.AppendLine("    {");

                    var refDeserializerVar = GetCachedDeserializerVar(elementType, refDeserializerVars);
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("    ");
                            w.Append("    ");
                            w.AppendLine(GetDeserializeString(elementType, true, "span[i]", "eleReader",
                                refDeserializerVar));
                        },
                        w =>
                        {
                            w.Append("    ");
                            w.Append("    ");
                            w.AppendLine(GetDeserializeString(elementType, true, "span[i]", "reader",
                                refDeserializerVar));
                        });
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }

                return true;
            }
        ),
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
                if (symbol.TypeKind == TypeKind.Interface) return false;
                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var idictSymbol = dictSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                  ?? dictSymbol;
                var keyType = idictSymbol.TypeArguments[0];
                var valType = idictSymbol.TypeArguments[1];
                if (!ValidFilter(keyType) || !ValidFilter(valType)) return false;

                var validIndexer = new ValidIndexer((_, indexer) =>
                {
                    if (symbol is not INamedTypeSymbol) return false;
                    var keySymbol = idictSymbol.TypeArguments[0];
                    var valueSymbol = idictSymbol.TypeArguments[1];

                    return indexer.DeclaredAccessibility == Accessibility.Public &&
                           indexer.Parameters.Length == 1
                           && indexer.Parameters[0].Type
                               .Equals(keySymbol, SymbolEqualityComparer.Default)
                           && indexer.Type.Equals(valueSymbol, SymbolEqualityComparer.Default)
                           && indexer.SetMethod != null;
                });
                if (!validIndexer.Filter(symbol)) return false;

                var dictType = symbol.GetDisplayString();
                bool isUnmanaged =
                    keyType.IsUnmanagedType && keyType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                                            && valType.IsUnmanagedType && valType.OriginalDefinition.SpecialType !=
                                            SpecialType.System_Nullable_T;

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(dictSymbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                // Optimized dictionary deserialization - reduce reader overhead
                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.Append("    value = new ");
                sb.Append(dictType);
                sb.Append(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");
                sb.AppendLine(";");
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        reader.Read(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.AppendLine($"        {GetDeserializeString(keyType, false, "key", "eleReader")}");
                            w.AppendLine($"        {GetDeserializeString(valType, false, "val", "eleReader")}");
                        },
                        w =>
                        {
                            w.AppendLine($"        {GetDeserializeString(keyType, false, "key")}");
                            w.AppendLine($"        {GetDeserializeString(valType, false, "val")}");
                        });
                    sb.AppendLine("        value[key] = val;");
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - dictionaries are modifiable, clear and repopulate
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(dictSymbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = null;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.AppendLine("    // Initialize if null, otherwise clear");
                sb.AppendLine("    if (value == null)");
                sb.AppendLine("    {");
                sb.Append("        value = new ");
                sb.Append(dictType);
                sb.Append(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");
                sb.AppendLine(";");
                sb.AppendLine("    }");
                sb.AppendLine("    else");
                sb.AppendLine("    {");
                sb.AppendLine("        value.Clear();");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        reader.Read(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key", "eleReader"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val"));
                        });
                    sb.AppendLine("        value[key] = val;");
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
                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var keyType = dictSymbol.TypeArguments[0];
                var valType = dictSymbol.TypeArguments[1];
                var dictType =
                    $"System.Collections.Generic.Dictionary<{keyType.GetDisplayString()}, {valType.GetDisplayString()}>";
                bool isUnmanaged =
                    keyType.IsUnmanagedType && keyType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                                            && valType.IsUnmanagedType && valType.OriginalDefinition.SpecialType !=
                                            SpecialType.System_Nullable_T;

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(dictSymbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                if (isUnmanaged)
                {
                    sb.AppendLine("    reader.Read(out value);");
                    sb.AppendLine("}");
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                    sb.AppendLine("    {");
                    sb.AppendLine("        value = default;");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                    sb.Append("    value = new ");
                    sb.Append(dictType);
                    sb.Append(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");

                    sb.AppendLine(";");
                    sb.AppendLine("    for (int i = 0; i < length; i++)");
                    sb.AppendLine("    {");
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key", "eleReader"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val"));
                        });
                    sb.AppendLine("        value[key] = val;");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }

                sb.AppendLine();

                // Ref overload for trivial dictionaries - proper implementation
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(dictSymbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                if (isUnmanaged)
                {
                    sb.AppendLine("    reader.ReadRef(ref value);");
                    sb.AppendLine("}");
                    return true;
                }

                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = null;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.AppendLine("    // Initialize if null, otherwise clear");
                sb.AppendLine("    if (value == null)");
                sb.AppendLine("    {");
                sb.Append("        value = new ");
                sb.Append(dictType);
                sb.Append(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "(length)" : "()");
                sb.AppendLine(";");
                sb.AppendLine("    }");
                sb.AppendLine("    else");
                sb.AppendLine("    {");
                sb.AppendLine("        value.Clear();");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        reader.Read(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key", "eleReader"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(keyType, false, "key"));
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(valType, false, "val"));
                        });
                    sb.AppendLine("        value[key] = val;");
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");

                return true;
            }
        ),
        // non trivial IEnumerable Ninotypes
        new
        (
            "NonTrivialEnumerableUsingAdd",
            // Note that we accept non-trivial Enumerable types with unmanaged element types
            new Joint().With
            (
                // Note that array is an IEnumerable, but we don't want to generate code for it
                new Interface("IEnumerable<T>"),
                new Not(new Array()),
                // We don't want a dictionary fallback to this case
                new Not(new Interface("IDictionary<TKey, TValue>")),
                // We want to exclude the ones that already have a serializer
                new Not(new NinoTyped()),
                new Not(new String()),
                new NonTrivial("IEnumerable", "ICollection", "IList", "List"),
                // We want to be able to Add
                new ValidMethod((symbol, method) =>
                {
                    if (symbol.TypeKind == TypeKind.Interface) return false;
                    if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                    var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                          i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                                      ?? namedTypeSymbol;
                    var elementType = ienumSymbol.TypeArguments[0];

                    return method.Name == "Add"
                           && method.Parameters.Length == 1
                           && method.Parameters[0].Type.Equals(elementType, SymbolEqualityComparer.Default);
                })
            ),
            (symbol, sb) =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!ValidFilter(elemType)) return false;

                var collType = symbol.GetDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                bool constructorWithNumArg = ienumSymbol.Constructors.Any(c =>
                    c.Parameters.Length == 1 && c.Parameters[0].Type.GetDisplayString() == "System.Int32");

                bool hasEnsureCapacity = namedTypeSymbol.GetMembers("EnsureCapacity").OfType<IMethodSymbol>()
                    .Any(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

                var creationDecl = constructorWithNumArg
                    ? $"new {collType}(length)"
                    : $"new {collType}()";

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(namedTypeSymbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.Append("    value = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");

                if (hasEnsureCapacity)
                {
                    sb.AppendLine("    value.EnsureCapacity(length);");
                }

                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        ");
                    sb.AppendLine(GetDeserializeString(elemType, false, "item"));
                    sb.AppendLine("        value.Add(item);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, false, "item", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, false, "item"));
                        });
                    sb.AppendLine("        value.Add(item);");
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - check if enumerable has Clear method
                if (HasAddAndClear.Filter(symbol))
                {
                    // Modifiable - has both Add and Clear methods
                    sb.AppendLine(Inline);
                    sb.Append("public static void DeserializeRef(ref ");
                    sb.Append(namedTypeSymbol.GetDisplayString());
                    sb.AppendLine(" value, ref Reader reader)");
                    sb.AppendLine("{");
                    EofCheck(sb);
                    sb.AppendLine();
                    sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                    sb.AppendLine("    {");
                    sb.AppendLine("        value = null;");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();

                    if (!isUnmanaged)
                    {
                        IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w => { w.AppendLine("    Reader eleReader;"); });
                        sb.AppendLine();
                    }

                    sb.AppendLine("    // Initialize if null");
                    sb.AppendLine("    if (value == null)");
                    sb.AppendLine("    {");
                    sb.Append("        value = ");
                    sb.Append(creationDecl);
                    sb.AppendLine(";");
                    sb.AppendLine("    }");
                    sb.AppendLine("    else");
                    sb.AppendLine("    {");
                    sb.AppendLine("        value.Clear();");
                    sb.AppendLine("    }");
                    if (hasEnsureCapacity)
                    {
                        sb.AppendLine("    value.EnsureCapacity(length);");
                    }

                    sb.AppendLine();
                    sb.AppendLine("    for (int i = 0; i < length; i++)");
                    sb.AppendLine("    {");

                    if (isUnmanaged)
                    {
                        sb.Append("        ");
                        sb.AppendLine(GetDeserializeString(elemType, false, "item"));
                        sb.AppendLine("        value.Add(item);");
                    }
                    else
                    {
                        IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w =>
                            {
                                w.AppendLine("        eleReader = reader.Slice();");
                                w.Append("        ");
                                w.AppendLine(GetDeserializeString(elemType, false, "item", "eleReader"));
                            },
                            w =>
                            {
                                w.Append("        ");
                                w.AppendLine(GetDeserializeString(elemType, false, "item"));
                            });
                        sb.AppendLine("        value.Add(item);");
                    }

                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }
                else
                {
                    // Not modifiable - fallback to out overload
                    sb.AppendLine(Inline);
                    sb.Append("public static void DeserializeRef(ref ");
                    sb.Append(namedTypeSymbol.GetDisplayString());
                    sb.AppendLine(" value, ref Reader reader) => Deserialize(out value, ref reader);");
                }

                return true;
            }
        ),
        // stack Ninotypes
        new
        (
            "Stack",
            new Interface("Stack<T>"),
            (symbol, sb) =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.GetDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!ValidFilter(elemType)) return false;
                var typeDecl = symbol.GetDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                var creationDecl = $"new {typeDecl}(arr)";
                var arrCreationDecl = $"new {elemType.GetDisplayString()}[length]";

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(namedTypeSymbol.ToDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.Append("    var arr = ");
                sb.Append(arrCreationDecl);
                sb.AppendLine(";");
                sb.AppendLine("    var span = arr.AsSpan();");
                sb.AppendLine("    for (int i = length - 1; i >= 0; i--)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.AppendLine("        reader.Read(out span[i]);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, true, "span[i]", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, true, "span[i]"));
                        });
                }

                sb.AppendLine("    }");
                sb.AppendLine();
                sb.Append("    value = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - Stack is not modifiable in-place, call out overload
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(namedTypeSymbol.ToDisplayString());
                sb.AppendLine(" value, ref Reader reader) => Deserialize(out value, ref reader);");

                return true;
            }
        ),
        // non trivial IEnumerable Ninotypes
        new
        (
            "NonTrivialEnumerableUsingCtorWithArr",
            // Note that we accept non-trivial IEnumerable types with unmanaged element types
            new Joint().With
            (
                // Note that array is an IEnumerable, but we don't want to generate code for it
                new Interface("IEnumerable<T>"),
                new Not(new Array()),
                // We don't want a dictionary fallback to this case
                new Not(new Interface("IDictionary<TKey, TValue>")),
                // We want to exclude the ones that already have a serializer
                new Not(new NinoTyped()),
                new Not(new String()),
                new NonTrivial("IEnumerable", "ICollection", "IList", "List"),
                // We want to be able to use a constructor with IEnumerable
                new ValidMethod((symbol, method) =>
                {
                    if (symbol.TypeKind == TypeKind.Interface) return false;
                    if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                    var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                          i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                      ?? namedTypeSymbol;
                    var elementType = ienumSymbol.TypeArguments[0];
                    var arrayType = Compilation.CreateArrayTypeSymbol(elementType);

                    return method.MethodKind == MethodKind.Constructor
                           && method.Parameters.Length == 1
                           && Compilation.HasImplicitConversion(arrayType, method.Parameters[0].Type);
                })
            ),
            (symbol, sb) =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!ValidFilter(elemType)) return false;
                var typeDecl = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType &&
                                   elemType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

                var creationDecl = $"new {typeDecl}(arr)";
                var arrCreationDecl = $"new {elemType.ToDisplayString()}[]";
                //replace first `[` to `[length`
                arrCreationDecl = arrCreationDecl.Insert(arrCreationDecl.IndexOf('[') + 1, "length");

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(namedTypeSymbol.ToDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.Append("    var arr = ");
                sb.Append(arrCreationDecl);
                sb.AppendLine(";");
                sb.AppendLine("    var span = arr.AsSpan();");
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.AppendLine("        reader.Read(out span[i]);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, true, "span[i]", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, true, "span[i]"));
                        });
                }

                sb.AppendLine("    }");
                sb.AppendLine();
                sb.Append("    value = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - non-trivial enumerable using ctor with array is not modifiable, call out overload
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(namedTypeSymbol.ToDisplayString());
                sb.AppendLine(" value, ref Reader reader) => Deserialize(out value, ref reader);");

                return true;
            }
        ),
        // trivial unmanaged List Ninotypes
        new
        (
            "TrivialUnmanagedList",
            new Joint().With
            (
                new Interface("List<T>"),
                new TypeArgument(0, symbol => symbol.IsUnmanagedType),
                new Not(new NonTrivial("IList", "List", "List")),
                new Not(new Array())
            ),
            (symbol, sb) =>
            {
                // must be a List<T> with unmanaged T
                INamedTypeSymbol listSymbol = (INamedTypeSymbol)symbol;
                var elemType = listSymbol.TypeArguments[0];
                if (!elemType.IsUnmanagedType ||
                    elemType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return false;
                // list symbol must be a List<T>
                if (listSymbol.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
                    return false;

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine("    reader.Read(out value);");
                sb.AppendLine("}");
                sb.AppendLine();

                // Ref overload - trivial unmanaged IList, use direct ReadRef
                sb.AppendLine(Inline);
                sb.Append("public static void DeserializeRef(ref ");
                sb.Append(symbol.GetDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine("    reader.ReadRef(ref value);");
                sb.AppendLine("}");

                return true;
            }
        ),
        // trivial IEnumerable Ninotypes
        new
        (
            "TrivialEnumerableUsingAdd",
            // Note that we accept non-trivial IEnumerable types with unmanaged element types
            new Joint().With
            (
                // Note that array is an IEnumerable, but we don't want to generate code for it
                new Interface("IEnumerable<T>"),
                new Not(new Array()),
                // We want to exclude the ones that already have a serializer
                new Not(new NinoTyped()),
                new Not(new String()),
                new Not(new NonTrivial("IEnumerable", "IEnumerable", "ICollection", "IList", "List"))
            ),
            (symbol, sb) =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                var typeDecl = namedTypeSymbol.TypeKind == TypeKind.Interface
                    ? $"System.Collections.Generic.List<{elemType.ToDisplayString()}>"
                    : namedTypeSymbol.ToDisplayString();

                bool isUnmanaged = elemType.IsUnmanagedType &&
                                   elemType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

                var hasPublicLengthCtor = namedTypeSymbol.Constructors
                    .Any(c => c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

                var creationDecl = "new " + typeDecl + (hasPublicLengthCtor ? "(length)" : "()");

                // First method: Deserialize(out T value, ref Reader reader)
                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(namedTypeSymbol.ToDisplayString());
                sb.AppendLine(" value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine();
                sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();

                if (!isUnmanaged)
                {
                    IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w => { w.AppendLine("    Reader eleReader;"); });
                    sb.AppendLine();
                }

                sb.Append("    var lst = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        reader.Read(out ");
                    sb.Append(elemType.ToDisplayString());
                    sb.AppendLine(" item);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, false, "item", "eleReader"));
                        },
                        w =>
                        {
                            w.Append("        ");
                            w.AppendLine(GetDeserializeString(elemType, false, "item"));
                        });
                }

                sb.AppendLine("        lst.Add(item);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    value = lst;");
                sb.AppendLine("}");

                // Second method: Deserialize(T value, ref Reader reader) - only if hasAddAndClear
                if (HasAddAndClear.Filter(symbol))
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine(Inline);
                    sb.Append("public static void Deserialize(");
                    sb.Append(namedTypeSymbol.ToDisplayString());
                    sb.AppendLine(" value, ref Reader reader)");
                    sb.AppendLine("{");
                    EofCheck(sb);
                    sb.AppendLine();
                    sb.AppendLine("    if (!reader.ReadCollectionHeader(out var length))");
                    sb.AppendLine("    {");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                    sb.AppendLine();

                    if (!isUnmanaged)
                    {
                        IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w => { w.AppendLine("    Reader eleReader;"); });
                        sb.AppendLine();
                    }

                    sb.AppendLine("    value.Clear();");
                    sb.AppendLine("    for (int i = 0; i < length; i++)");
                    sb.AppendLine("    {");

                    if (isUnmanaged)
                    {
                        sb.Append("        reader.Read(out ");
                        sb.Append(elemType.ToDisplayString());
                        sb.AppendLine(" item);");
                    }
                    else
                    {
                        IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w =>
                            {
                                w.AppendLine("        eleReader = reader.Slice();");
                                w.Append("        ");
                                w.AppendLine(GetDeserializeString(elemType, false, "item", "eleReader"));
                            },
                            w =>
                            {
                                w.Append("        ");
                                w.AppendLine(GetDeserializeString(elemType, false, "item"));
                            });
                    }

                    sb.AppendLine("        value.Add(item);");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }

                sb.AppendLine();

                // Ref overload - check if it's modifiable (has Add and Clear) or not
                if (HasAddAndClear.Filter(symbol))
                {
                    // Modifiable - can use existing method that clears and adds
                    sb.AppendLine(Inline);
                    sb.Append("public static void DeserializeRef(ref ");
                    sb.Append(namedTypeSymbol.ToDisplayString());
                    sb.AppendLine(" value, ref Reader reader)");
                    sb.AppendLine("{");
                    EofCheck(sb);
                    sb.AppendLine();
                    sb.AppendLine("    // Initialize if null");
                    sb.AppendLine("    if (value == null)");
                    sb.AppendLine("    {");
                    sb.Append("        value = new ");
                    sb.Append(typeDecl);
                    sb.AppendLine("();");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    sb.AppendLine("    // Use the existing method that clears and populates");
                    sb.Append("    Deserialize(value, ref reader);");
                    sb.AppendLine("}");
                }
                else
                {
                    // Not modifiable - call out overload
                    sb.AppendLine(Inline);
                    sb.Append("public static void DeserializeRef(ref ");
                    sb.Append(namedTypeSymbol.ToDisplayString());
                    sb.AppendLine(" value, ref Reader reader) => Deserialize(out value, ref reader);");
                }

                return true;
            }
        )
    ];

    private void GenericTupleLikeMethods(ITypeSymbol type, Writer writer, ITypeSymbol[] types, params string[] fields)
    {
        bool isValueTuple = type.Name == "ValueTuple";
        var typeName = type.ToDisplayString();
        bool isUnmanaged = type.IsUnmanagedType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;

        // Out overload
        writer.AppendLine(Inline);
        writer.Append("public static void Deserialize(out ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        writer.AppendLine("{");
        EofCheck(writer);
        if (isUnmanaged)
        {
            writer.AppendLine("    reader.Read(out value);");
            writer.AppendLine("}");
        }
        else
        {
            for (int i = 0; i < fields.Length; i++)
            {
                writer.Append("    ");
                writer.AppendLine(GetDeserializeString(types[i], false, fields[i].ToLower()));
            }

            writer.Append("    value = ");
            if (!isValueTuple)
            {
                writer.Append("new ");
                writer.Append(typeName);
            }

            writer.Append("(");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i != 0)
                {
                    writer.Append(", ");
                }

                writer.Append(fields[i].ToLower());
            }

            writer.AppendLine(");");
            writer.AppendLine("}");
        }

        writer.AppendLine();

        // Ref overload - tuples are not modifiable
        writer.AppendLine(Inline);
        writer.Append("public static void DeserializeRef(ref ");
        writer.Append(typeName);
        writer.AppendLine(" value, ref Reader reader)");
        if (isUnmanaged)
        {
            writer.AppendLine("    => reader.Read(out value);");
        }
        else
        {
            writer.AppendLine("    => Deserialize(out value, ref reader);");
        }
    }
}