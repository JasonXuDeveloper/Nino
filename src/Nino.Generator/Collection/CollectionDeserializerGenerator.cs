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

public class CollectionDeserializerGenerator : NinoCollectionGenerator
{
    public CollectionDeserializerGenerator(
        Compilation compilation,
        List<ITypeSymbol> potentialCollectionSymbols) : base(compilation, potentialCollectionSymbols)
    {
    }

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
                    new Not(new AnyTypeArgument(symbol => !Selector.Filter(symbol)))
                ),
                // We want tuples
                new Joint().With
                (
                    new Trivial("ValueTuple", "Tuple"),
                    new Not(new AnyTypeArgument(symbol => !Selector.Filter(symbol)))
                ),
                // We want nullables
                new Nullable(),
                // We want arrays
                new Array(arraySymbol => Selector.Filter(arraySymbol.ElementType)),
                // We want dictionaries with valid indexers
                new Joint().With
                (
                    new Interface("IDictionary<TKey, TValue>", interfaceSymbol =>
                    {
                        var keyType = interfaceSymbol.TypeArguments[0];
                        var valueType = interfaceSymbol.TypeArguments[1];
                        return Selector.Filter(keyType) && Selector.Filter(valueType);
                    }),
                    new ValidIndexer((symbol, indexer) =>
                    {
                        if (symbol.TypeKind == TypeKind.Interface) return true;
                        if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                        var idictSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                          ?? namedTypeSymbol;
                        var keySymbol = idictSymbol.TypeArguments[0];
                        var valueSymbol = idictSymbol.TypeArguments[1];

                        return indexer.Parameters.Length == 1
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
                        return Selector.Filter(elementType);
                    }),
                    new ValidMethod((symbol, method) =>
                    {
                        if (symbol.TypeKind == TypeKind.Interface) return true;
                        if (method.MethodKind == MethodKind.Constructor)
                        {
                            if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                            var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                                  i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
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

    protected override List<Transformer> Transformers => new()
    {
        // Nullable Ninotypes
        new
        (
            "Nullable",
            // We want nullable for non-unmanaged ninotypes
            new Joint().With
            (
                new Nullable(),
                new TypeArgument(0, symbol => !symbol.IsUnmanagedType && Selector.Filter(symbol))
            )
            , symbol =>
            {
                ITypeSymbol elementType = ((INamedTypeSymbol)symbol).TypeArguments[0];
                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{elementType.ToDisplayString()}}? value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             reader.Read(out bool hasValue);
                             if (!hasValue)
                             {
                                 value = default;
                                 return;
                             }
                             
                             Deserialize(out {{elementType.ToDisplayString()}} ret, ref reader);
                             value = ret;
                         }
                         """;
            }
        ),
        // KeyValuePair Ninotypes
        new
        (
            "KeyValuePair",
            // We only want KeyValuePair for non-unmanaged ninotypes
            new Joint().With(
                new Trivial("KeyValuePair"),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType)
            ),
            symbol =>
                GenericTupleLikeMethods(symbol,
                    ((INamedTypeSymbol)symbol).TypeArguments
                    .Select(typeSymbol =>
                        typeSymbol.ToDisplayString()).ToArray(),
                    "K", "V")
        ),
        // Tuple Ninotypes
        new
        (
            "Tuple",
            // We only want Tuple for non-unmanaged ninotypes
            new Trivial("ValueTuple", "Tuple"),
            symbol => symbol.IsUnmanagedType
                ? ""
                : GenericTupleLikeMethods(symbol,
                    ((INamedTypeSymbol)symbol).TypeArguments
                    .Select(typeSymbol =>
                        typeSymbol.ToDisplayString()).ToArray(),
                    ((INamedTypeSymbol)symbol)
                    .TypeArguments.Select((_, i) => $"Item{i + 1}").ToArray())
        ),
        // Array Ninotypes
        new
        (
            "Array",
            new Array(arraySymbol =>
            {
                var elementType = arraySymbol.ElementType;
                return !elementType.IsUnmanagedType;
            }),
            symbol =>
            {
                var elemType = ((IArrayTypeSymbol)symbol).ElementType.ToDisplayString();
                var creationDecl = elemType.EndsWith("[]")
                    ? elemType.Insert(elemType.IndexOf("[]", StringComparison.Ordinal), "[length]")
                    : $"{elemType}[length]";
                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{symbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = null;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = null;
                                 return;
                             }
                             
                             #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             Reader eleReader;
                             #endif
                             
                             value = new {{creationDecl}};
                             var span = value.AsSpan();
                             for (int i = 0; i < length; i++)
                             {
                             #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                 eleReader = reader.Slice();
                                 Deserialize(out span[i], ref eleReader);
                             #else
                                 Deserialize(out span[i], ref reader);
                             #endif
                             }
                         }
                         """;
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
            symbol =>
            {
                if (symbol.TypeKind == TypeKind.Interface) return "";

                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var idictSymbol = dictSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                  ?? dictSymbol;
                var keyType = idictSymbol.TypeArguments[0];
                var valType = idictSymbol.TypeArguments[1];
                if (!Selector.Filter(keyType) || !Selector.Filter(valType)) return "";

                var dictType = symbol.ToDisplayString();
                bool isUnmanaged = keyType.IsUnmanagedType && valType.IsUnmanagedType;

                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var deserializeStr = isUnmanaged
                    ? $"""
                               Deserialize(out KeyValuePair<{keyType.ToDisplayString()}, {valType.ToDisplayString()}> kvp, ref reader);
                               value[kvp.Key] = kvp.Value;
                       """
                    : $"""
                       #if {NinoTypeHelper.WeakVersionToleranceSymbol}
                               {slice}
                               Deserialize(out {keyType.ToDisplayString()} key, ref eleReader);
                               Deserialize(out {valType.ToDisplayString()} val, ref eleReader);
                       #else
                               Deserialize(out {keyType.ToDisplayString()} key, ref reader);
                               Deserialize(out {valType.ToDisplayString()} val, ref reader);
                       #endif
                               value[key] = val;
                       """;

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{dictSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             {{eleReaderDecl}}
                         #endif
                             
                             value = new {{dictType}}({{(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "length" : "")}});
                             for (int i = 0; i < length; i++)
                             {
                         {{deserializeStr}}
                             }
                         }
                         """;
            }
        ),
        // trivial IDictionary Ninotypes
        new
        (
            "TrivialDictionary",
            new Joint().With
            (
                new Interface("IDictionary<TKey, TValue>"),
                new Not(new NonTrivial("IDictionary", "IDictionary", "Dictionary")),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType)
            ),
            symbol =>
            {
                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var keyType = dictSymbol.TypeArguments[0];
                var valType = dictSymbol.TypeArguments[1];
                var dictType =
                    $"System.Collections.Generic.Dictionary<{keyType.ToDisplayString()}, {valType.ToDisplayString()}>";

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{dictSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             Reader eleReader;
                         #endif
                             
                             value = new {{dictType}}({{(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "length" : "")}});
                             for (int i = 0; i < length; i++)
                             {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                 eleReader = reader.Slice();
                                 Deserialize(out {{keyType.ToDisplayString()}} key, ref eleReader);
                                 Deserialize(out {{valType.ToDisplayString()}} val, ref eleReader);
                         #else
                                Deserialize(out {{keyType.ToDisplayString()}} key, ref reader);
                                Deserialize(out {{valType.ToDisplayString()}} val, ref reader);
                         #endif
                                value[key] = val;
                             }
                         }
                         """;
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
                                          i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                      ?? namedTypeSymbol;
                    var elementType = ienumSymbol.TypeArguments[0];

                    return method.Name == "Add"
                           && method.Parameters.Length == 1
                           && method.Parameters[0].Type.Equals(elementType, SymbolEqualityComparer.Default);
                })
            ),
            symbol =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";

                var collType = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                bool constructorWithNumArg = ienumSymbol.Constructors.Any(c =>
                    c.Parameters.Length == 1 && c.Parameters[0].Type.ToDisplayString() == "System.Int32");

                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var creationDecl = constructorWithNumArg
                    ? $"new {collType}(length)"
                    : $"new {collType}()";
                
                var deserializeStr = isUnmanaged
                    ? $"""
                               Deserialize(out {elemType.ToDisplayString()} item, ref reader);
                               value.Add(item);
                       """
                    : $"""
                       #if {NinoTypeHelper.WeakVersionToleranceSymbol}
                               {slice}
                               Deserialize(out {elemType.ToDisplayString()} item, ref eleReader);
                       #else
                               Deserialize(out {elemType.ToDisplayString()} item, ref reader);
                       #endif
                               value.Add(item);
                       """;

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{namedTypeSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             {{eleReaderDecl}}
                         #endif
                             
                             value = {{creationDecl}};
                             for (int i = 0; i < length; i++)
                             {
                         {{deserializeStr}}
                             }
                         }
                         """;
            }
        ),
        // stack Ninotypes
        new
        (
            "Stack",
            new Interface("Stack<T>"),
            symbol =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";
                var typeDecl = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var creationDecl = $"new {typeDecl}(arr)";
                var arrCreationDecl = $"new {elemType.ToDisplayString()}[length]";
                
                var deserializeStr = isUnmanaged
                    ? $"""
                               Deserialize(out span[i], ref reader);
                       """
                    : $"""
                       #if {NinoTypeHelper.WeakVersionToleranceSymbol}
                               {slice}
                               Deserialize(out span[i], ref eleReader);
                       #else
                               Deserialize(out span[i], ref reader);
                       #endif
                       """;
                
                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{namedTypeSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             {{eleReaderDecl}}
                         #endif
                             var arr = {{arrCreationDecl}};
                             var span = arr.AsSpan();
                             for (int i = length - 1; i >= 0; i--)
                             {
                         {{deserializeStr}}
                             }
                         
                             value = {{creationDecl}};
                         }
                         """;
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
            symbol =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";
                var typeDecl = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var creationDecl = $"new {typeDecl}(arr)";
                var arrCreationDecl = $"new {elemType.ToDisplayString()}[]";
                //replace first `[` to `[length`
                arrCreationDecl = arrCreationDecl.Insert(arrCreationDecl.IndexOf('[') + 1, "length");
                var deserializeStr = isUnmanaged
                    ? $"""
                               Deserialize(out span[i], ref reader);
                       """
                    : $"""
                       #if {NinoTypeHelper.WeakVersionToleranceSymbol}
                               {slice}
                               Deserialize(out span[i], ref eleReader);
                       #else
                               Deserialize(out span[i], ref reader);
                       #endif
                       """;

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{namedTypeSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             {{eleReaderDecl}}
                         #endif
                         
                             var arr = {{arrCreationDecl}};
                             var span = arr.AsSpan();
                             for (int i = 0; i < length; i++)
                             {
                         {{deserializeStr}}
                             }
                         
                             value = {{creationDecl}};
                         }
                         """;
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
                new Not(new NonTrivial("IEnumerable", "ICollection", "IList", "List")),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType)
            ),
            symbol =>
            {
                INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
                var ienumSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i =>
                                      i.OriginalDefinition.ToDisplayString().EndsWith("IEnumerable<T>"))
                                  ?? namedTypeSymbol;
                var elemType = ienumSymbol.TypeArguments[0];
                var typeDecl = $"System.Collections.Generic.List<{elemType.ToDisplayString()}>";

                var creationDecl = $"new {typeDecl}(length)";
                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{namedTypeSymbol.ToDisplayString()}} value, ref Reader reader)
                         {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                              if (reader.Eof)
                              {
                                 value = default;
                                 return;
                              }
                         #endif
                             
                             if (!reader.ReadCollectionHeader(out var length))
                             {
                                 value = default;
                                 return;
                             }

                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                             Reader eleReader;
                         #endif
                             
                             value = {{creationDecl}};
                             for (int i = 0; i < length; i++)
                             {
                         #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                                 eleReader = reader.Slice();
                                 Deserialize(out {{elemType.ToDisplayString()}} item, ref eleReader);
                         #else
                                 Deserialize(out {{elemType.ToDisplayString()}} item, ref reader);
                         #endif
                                 value.Add(item);
                             }
                         }
                         """;
            }
        ),
    };

    private string GenericTupleLikeMethods(ITypeSymbol type, string[] types, params string[] fields)
    {
        string[] deserializeValues =
            fields.Select((field, i) =>
                $"Deserialize(out {types[i]} {field.ToLower()}, ref reader);").ToArray();
        bool isValueTuple = type.Name == "ValueTuple";

        return $$"""
                 [MethodImpl(MethodImplOptions.AggressiveInlining)]
                 public static void Deserialize(out {{type.ToDisplayString()}} value, ref Reader reader)
                 {
                 #if {{NinoTypeHelper.WeakVersionToleranceSymbol}}
                      if (reader.Eof)
                      {
                         value = default;
                         return;
                      }
                 #endif
                 
                     {{string.Join("\n    ", deserializeValues)}};
                     value = {{(isValueTuple ? "" : $"new {type.ToDisplayString()}")}}({{
                         string.Join(", ",
                             fields.Select(field => field.ToLower()))
                     }});
                 }
                 """;
    }
}