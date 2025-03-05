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
                new Array(),
                // We want dictionaries with valid indexers
                new Joint().With
                (
                    new Interface("IDictionary"),
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
                    new Union().With
                    (
                        new Interface("ICollection"),
                        new Interface("IList")
                    ),
                    new ValidMethod((symbol, method) =>
                    {
                        if (symbol.TypeKind == TypeKind.Interface) return true;
                        if (method.MethodKind == MethodKind.Constructor)
                        {
                            return method.Parameters.Length == 0
                                   || (method.Parameters.Length == 1
                                       && method.Parameters[0].Type.SpecialType == SpecialType.System_Int32);
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
                new TypeArgument(0, symbol => !symbol.IsUnmanagedType)
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
                             
                             Reader eleReader;
                             
                             value = new {{creationDecl}};
                             var span = value.AsSpan();
                             for (int i = 0; i < length; i++)
                             {
                                 eleReader = reader.Slice();
                                 Deserialize(out span[i], ref eleReader);
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
                new Interface("IDictionary"),
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

                var reader = isUnmanaged ? "reader" : "eleReader";
                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var deserializeStr = isUnmanaged
                    ? $"""
                               Deserialize(out KeyValuePair<{keyType.ToDisplayString()}, {valType.ToDisplayString()}> kvp, ref {reader});
                               value[kvp.Key] = kvp.Value;
                       """
                    : $"""
                               Deserialize(out {keyType.ToDisplayString()} key, ref {reader});
                               Deserialize(out {valType.ToDisplayString()} val, ref {reader});
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
                         
                             {{eleReaderDecl}}
                             
                             value = new {{dictType}}({{(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "length" : "")}});
                             for (int i = 0; i < length; i++)
                             {
                                 {{slice}}
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
                new Interface("IDictionary"),
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
                         
                             Reader eleReader;
                             
                             value = new {{dictType}}({{(dictType.StartsWith("System.Collections.Generic.Dictionary") ? "length" : "")}});
                             for (int i = 0; i < length; i++)
                             {
                                 eleReader = reader.Slice();
                                 Deserialize(out {{keyType.ToDisplayString()}} key, ref eleReader);
                                 Deserialize(out {{valType.ToDisplayString()}} val, ref eleReader);
                                 value[key] = val;
                             }
                         }
                         """;
            }
        ),
        // non trivial ICollection Ninotypes
        new
        (
            "NonTrivialCollectionUsingAdd",
            // Note that we accept non-trivial ICollection types with unmanaged element types
            new Joint().With
            (
                // Note that array is an ICollection, but we don't want to generate code for it
                new Interface("ICollection"),
                new Not(new Array()),
                new NonTrivial("ICollection", "ICollection", "List"),
                // We want to be able to Add
                new ValidMethod((symbol, method) =>
                {
                    if (symbol.TypeKind == TypeKind.Interface) return false;
                    if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                    var iCollSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICollection")
                                      ?? namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IList")
                                      ?? namedTypeSymbol;
                    var elementType = iCollSymbol.TypeArguments[0];

                    return method.Name == "Add"
                           && method.Parameters.Length == 1
                           && method.Parameters[0].Type.Equals(elementType, SymbolEqualityComparer.Default);
                })
            ),
            symbol =>
            {
                INamedTypeSymbol collSymbol = (INamedTypeSymbol)symbol;
                var iCollSymbol = collSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICollection")
                                  ?? collSymbol;
                var elemType = iCollSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";

                var collType = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                bool constructorWithNumArg = collSymbol.Constructors.Any(c =>
                    c.Parameters.Length == 1 && c.Parameters[0].Type.ToDisplayString() == "System.Int32");

                var reader = isUnmanaged ? "reader" : "eleReader";
                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var creationDecl = constructorWithNumArg
                    ? $"new {collType}(length)"
                    : $"new {collType}()";

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{collSymbol.ToDisplayString()}} value, ref Reader reader)
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
                         
                             {{eleReaderDecl}}
                             
                             value = {{creationDecl}};
                             for (int i = 0; i < length; i++)
                             {
                                 {{slice}}
                                 Deserialize(out {{elemType.ToDisplayString()}} item, ref {{reader}});
                                 value.Add(item);
                             }
                         }
                         """;
            }
        ),
        // non trivial ICollection Ninotypes
        new
        (
            "NonTrivialCollectionUsingCtorWithEnumerable",
            // Note that we accept non-trivial ICollection types with unmanaged element types
            new Joint().With
            (
                // Note that array is an ICollection, but we don't want to generate code for it
                new Interface("ICollection"),
                new Not(new Array()),
                new NonTrivial("ICollection", "ICollection", "List"),
                // We want to be able to use a constructor with IEnumerable
                new ValidMethod((symbol, method) =>
                {
                    if (symbol.TypeKind == TypeKind.Interface) return false;
                    if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                    var iCollSymbol = namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICollection")
                                      ?? namedTypeSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IList")
                                      ?? namedTypeSymbol;
                    var elementType = iCollSymbol.TypeArguments[0];

                    return method.MethodKind == MethodKind.Constructor
                           && method.Parameters.Length == 1
                           && method.Parameters[0].Type.SpecialType == SpecialType.System_Collections_IEnumerable
                           && method.Parameters[0].Type is INamedTypeSymbol ienumerable && ienumerable.IsGenericType
                           && ienumerable.TypeArguments.Length > 0 && ienumerable.TypeArguments[0]
                               .Equals(elementType, SymbolEqualityComparer.Default);
                })
            ),
            symbol =>
            {
                INamedTypeSymbol collSymbol = (INamedTypeSymbol)symbol;
                var iCollSymbol = collSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICollection")
                                  ?? collSymbol;
                var elemType = iCollSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";
                var collType = symbol.ToDisplayString();
                bool isUnmanaged = elemType.IsUnmanagedType;

                var reader = isUnmanaged ? "reader" : "eleReader";
                var slice = isUnmanaged ? "" : "eleReader = reader.Slice();";
                var eleReaderDecl = isUnmanaged ? "" : "Reader eleReader;";
                var creationDecl = $"new {collType}(arr)";
                var arrCreationDecl = $"new {elemType.ToDisplayString()}[length]";

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{collSymbol.ToDisplayString()}} value, ref Reader reader)
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
                         
                             {{eleReaderDecl}}
                             var arr = {{arrCreationDecl}};
                             var span = arr.AsSpan();
                             for (int i = 0; i < length; i++)
                             {
                                 {{slice}}
                                 Deserialize(out span[i], ref {{reader}});
                             }
                         
                             value = {{creationDecl}};
                         }
                         """;
            }
        ),
        // trivial ICollection Ninotypes
        new
        (
            "TrivialCollectionUsingAdd",
            // Note that we accept non-trivial ICollection types with unmanaged element types
            new Joint().With
            (
                // Note that array is an ICollection, but we don't want to generate code for it
                new Interface("ICollection"),
                new Not(new Array()),
                new Not(new NonTrivial("ICollection", "ICollection", "List")),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType),
                // We want to be able to Add
                new ValidMethod((symbol, method) =>
                {
                    if (symbol.TypeKind == TypeKind.Interface) return true;
                    if (symbol is not INamedTypeSymbol namedTypeSymbol) return false;
                    var elementType = namedTypeSymbol.TypeArguments[0];

                    return method.Name == "Add"
                           && method.Parameters.Length == 1
                           && method.Parameters[0].Type.Equals(elementType, SymbolEqualityComparer.Default);
                })
            ),
            symbol =>
            {
                INamedTypeSymbol collSymbol = (INamedTypeSymbol)symbol;
                var elemType = collSymbol.TypeArguments[0];
                var collType = $"System.Collections.Generic.List<{elemType.ToDisplayString()}>";

                bool constructorWithNumArg = collSymbol.Constructors.Any(c =>
                    c.Parameters.Length == 1 && c.Parameters[0].Type.ToDisplayString() == "System.Int32");

                var creationDecl = constructorWithNumArg
                    ? $"new {collType}(length)"
                    : $"new {collType}()";

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Deserialize(out {{collSymbol.ToDisplayString()}} value, ref Reader reader)
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
                         
                             Reader eleReader;
                             
                             value = {{creationDecl}};
                             for (int i = 0; i < length; i++)
                             {
                                 eleReader = reader.Slice();
                                 Deserialize(out {{elemType.ToDisplayString()}} item, ref eleReader);
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