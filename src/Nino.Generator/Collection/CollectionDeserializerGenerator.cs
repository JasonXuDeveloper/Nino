using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Filter;
using Nino.Generator.Filter.Operation;
using Nino.Generator.Template;
using Array = Nino.Generator.Filter.Array;
using Nullable = Nino.Generator.Filter.Nullable;
using String = Nino.Generator.Filter.String;

namespace Nino.Generator.Collection;

public class CollectionDeserializerGenerator(
    Compilation compilation,
    List<ITypeSymbol> potentialCollectionSymbols)
    : NinoCollectionGenerator(compilation, potentialCollectionSymbols)
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
                    }),
                    new ValidIndexer((symbol, indexer) =>
                    {
                        if (symbol.TypeKind == TypeKind.Interface) return true;
                        if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                        var idictSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                          ?? namedTypeSymbol;
                        var keySymbol = idictSymbol.TypeArguments[0];
                        var valueSymbol = idictSymbol.TypeArguments[1];

                        return indexer.DeclaredAccessibility == Accessibility.Public &&
                               indexer.Parameters.Length == 1
                               && indexer.Parameters[0].Type
                                   .Equals(keySymbol, SymbolEqualityComparer.Default)
                               && indexer.Type.Equals(valueSymbol, SymbolEqualityComparer.Default);
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
    protected override string OutputFileName => "NinoDeserializer.Collection.g.cs";

    protected override Action<StringBuilder, string> PublicMethod => (sb, typeFullName) =>
    {
        sb.GenerateClassDeserializeMethods(typeFullName);
    };

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
                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(elementTypeFullName);
                sb.AppendLine("? value, ref Reader reader)");
                sb.AppendLine("{");
                EofCheck(sb);
                sb.AppendLine("    reader.Read(out bool hasValue);");
                sb.AppendLine("    if (!hasValue)");
                sb.AppendLine("    {");
                sb.AppendLine("        value = default;");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.Append("    Deserialize(out ");
                sb.Append(elementTypeFullName);
                sb.AppendLine(" ret, ref reader);");
                sb.AppendLine("    value = ret;");
                sb.AppendLine("}");
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
                    ((INamedTypeSymbol)symbol).TypeArguments
                    .Select(typeSymbol =>
                        typeSymbol.GetDisplayString()).ToArray(),
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
                    ((INamedTypeSymbol)symbol).TypeArguments
                    .Select(typeSymbol =>
                        typeSymbol.GetDisplayString()).ToArray(),
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
                var elemType = ((IArrayTypeSymbol)symbol).ElementType.GetDisplayString();
                var creationDecl = elemType.EndsWith("[]")
                    ? elemType.Insert(elemType.IndexOf("[]", StringComparison.Ordinal), "[length]")
                    : $"{elemType}[length]";
                var typeName = symbol.GetDisplayString();
                bool isUnmanaged = ((IArrayTypeSymbol)symbol).ElementType.IsUnmanagedType;
                if (isUnmanaged)
                {
                    sb.AppendLine(Inline);
                    sb.Append("public static void Deserialize(out ");
                    sb.Append(typeName);
                    sb.AppendLine(" value, ref Reader reader)");
                    sb.AppendLine("{");
                    EofCheck(sb);
                    sb.AppendLine("    reader.Read(out value);");
                    sb.AppendLine("}");
                    return true;
                }

                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(out ");
                sb.Append(typeName);
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
                IfDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                    w => { w.AppendLine("    Reader eleReader;"); });
                sb.AppendLine();
                sb.Append("    value = new ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
                sb.AppendLine("    var span = value.AsSpan();");
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");
                IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                    w =>
                    {
                        w.AppendLine("        eleReader = reader.Slice();");
                        w.AppendLine("        Deserialize(out span[i], ref eleReader);");
                    },
                    w => { w.AppendLine("        Deserialize(out span[i], ref reader);"); });
                sb.AppendLine("    }");
                sb.AppendLine("}");

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

                var dictType = symbol.GetDisplayString();
                bool isUnmanaged = keyType.IsUnmanagedType && valType.IsUnmanagedType;

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
                    sb.Append("        Deserialize(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp, ref reader);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref eleReader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref eleReader);");
                        },
                        w =>
                        {
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref reader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref reader);");
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
                bool isUnmanaged = keyType.IsUnmanagedType && valType.IsUnmanagedType;

                // First method: Deserialize(TDict value, ref Reader reader)
                sb.AppendLine(Inline);
                sb.Append("public static void Deserialize(");
                sb.Append(dictSymbol.GetDisplayString());
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
                    sb.Append("        Deserialize(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp, ref reader);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref eleReader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref eleReader);");
                        },
                        w =>
                        {
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref reader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref reader);");
                        });
                    sb.AppendLine("        value[key] = val;");
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();

                // Second method: Deserialize(out TDict value, ref Reader reader)
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
                    sb.Append("        Deserialize(out KeyValuePair<");
                    sb.Append(keyType.GetDisplayString());
                    sb.Append(", ");
                    sb.Append(valType.GetDisplayString());
                    sb.AppendLine("> kvp, ref reader);");
                    sb.AppendLine("        value[kvp.Key] = kvp.Value;");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref eleReader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref eleReader);");
                        },
                        w =>
                        {
                            w.Append("        Deserialize(out ");
                            w.Append(keyType.GetDisplayString());
                            w.AppendLine(" key, ref reader);");
                            w.Append("        Deserialize(out ");
                            w.Append(valType.GetDisplayString());
                            w.AppendLine(" val, ref reader);");
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
                sb.AppendLine("    for (int i = 0; i < length; i++)");
                sb.AppendLine("    {");

                if (isUnmanaged)
                {
                    sb.Append("        Deserialize(out ");
                    sb.Append(elemType.GetDisplayString());
                    sb.AppendLine(" item, ref reader);");
                    sb.AppendLine("        value.Add(item);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        Deserialize(out ");
                            w.Append(elemType.GetDisplayString());
                            w.AppendLine(" item, ref eleReader);");
                        },
                        w =>
                        {
                            w.Append("        Deserialize(out ");
                            w.Append(elemType.GetDisplayString());
                            w.AppendLine(" item, ref reader);");
                        });
                    sb.AppendLine("        value.Add(item);");
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
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
                    sb.AppendLine("        Deserialize(out span[i], ref reader);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.AppendLine("        Deserialize(out span[i], ref eleReader);");
                        },
                        w => { w.AppendLine("        Deserialize(out span[i], ref reader);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine();
                sb.Append("    value = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
                sb.AppendLine("}");
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
                bool isUnmanaged = elemType.IsUnmanagedType;

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
                    sb.AppendLine("        Deserialize(out span[i], ref reader);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.AppendLine("        Deserialize(out span[i], ref eleReader);");
                        },
                        w => { w.AppendLine("        Deserialize(out span[i], ref reader);"); });
                }

                sb.AppendLine("    }");
                sb.AppendLine();
                sb.Append("    value = ");
                sb.Append(creationDecl);
                sb.AppendLine(";");
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
                var typeDecl = $"System.Collections.Generic.List<{elemType.ToDisplayString()}>";
                bool isUnmanaged = elemType.IsUnmanagedType;
                var creationDecl = "new " + typeDecl + "(length)";

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
                    sb.Append("        Deserialize(out ");
                    sb.Append(elemType.ToDisplayString());
                    sb.AppendLine(" item, ref reader);");
                }
                else
                {
                    IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                        w =>
                        {
                            w.AppendLine("        eleReader = reader.Slice();");
                            w.Append("        Deserialize(out ");
                            w.Append(elemType.ToDisplayString());
                            w.AppendLine(" item, ref eleReader);");
                        },
                        w =>
                        {
                            w.Append("        Deserialize(out ");
                            w.Append(elemType.ToDisplayString());
                            w.AppendLine(" item, ref reader);");
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
                        sb.Append("        Deserialize(out ");
                        sb.Append(elemType.ToDisplayString());
                        sb.AppendLine(" item, ref reader);");
                    }
                    else
                    {
                        IfElseDirective(NinoTypeHelper.WeakVersionToleranceSymbol, sb,
                            w =>
                            {
                                w.AppendLine("        eleReader = reader.Slice();");
                                w.Append("        Deserialize(out ");
                                w.Append(elemType.ToDisplayString());
                                w.AppendLine(" item, ref eleReader);");
                            },
                            w =>
                            {
                                w.Append("        Deserialize(out ");
                                w.Append(elemType.ToDisplayString());
                                w.AppendLine(" item, ref reader);");
                            });
                    }

                    sb.AppendLine("        value.Add(item);");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }

                return true;
            }
        )
    ];

    private void GenericTupleLikeMethods(ITypeSymbol type, Writer writer, string[] types, params string[] fields)
    {
        bool isValueTuple = type.Name == "ValueTuple";
        var typeName = type.ToDisplayString();
        bool isUnmanaged = type.IsUnmanagedType;

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
                writer.Append("    Deserialize(out ");
                writer.Append(types[i]);
                writer.Append(" ");
                writer.Append(fields[i].ToLower());
                writer.AppendLine(", ref reader);");
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
    }
}