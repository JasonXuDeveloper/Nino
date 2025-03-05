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

public class CollectionSerializerGenerator(
    Compilation compilation,
    List<ITypeSymbol> potentialCollectionSymbols)
    : NinoCollectionGenerator(compilation, potentialCollectionSymbols)
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
            // We want dictionaries
            new Interface("IDictionary"),
            // We want arrays
            new Array(),
            // We want collections
            new Interface("ICollection"),
            // We want lists
            new Interface("IList"),
            // We want span
            new Span()
        )
    );

    protected override string ClassName => "Serializer";
    protected override string OutputFileName => "NinoSerializer.Collection.g.cs";

    protected override Action<StringBuilder, string> PublicMethod => (sb, typeFullName) =>
    {
        sb.GenerateClassSerializeMethods(typeFullName);
    };

    protected override List<Transformer> Transformers => new List<Transformer>
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
                         public static void Serialize(this {{elementType.ToDisplayString()}}? value, ref Writer writer)
                         {
                             if (!value.HasValue)
                             {
                                 writer.Write(false);
                                 return;
                             }
                             
                             writer.Write(true);
                             Serialize(value.Value, ref writer);
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
                GenericTupleLikeMethods(symbol, "Key", "Value")
        ),
        // Tuple Ninotypes
        new
        (
            "Tuple",
            // We only want Tuple for non-unmanaged ninotypes
            new Trivial("ValueTuple", "Tuple"),
            symbol => symbol.IsUnmanagedType
                ? ""
                : GenericTupleLikeMethods(symbol, ((INamedTypeSymbol)symbol)
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
            symbol => $$"""
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                        {
                            if (value == null)
                            {
                                writer.Write(TypeCollector.NullCollection);
                                return;
                            }
                        
                            var span = value.AsSpan();
                            int cnt = span.Length;
                            int pos;
                            writer.Write(TypeCollector.GetCollectionHeader(cnt));
                            
                            for (int i = 0; i < cnt; i++)
                            {
                                pos = writer.Advance(4);
                                Serialize(span[i], ref writer);
                                writer.PutLength(pos);
                            }
                        }
                        """
        ),
        // Span Ninotypes
        new
        (
            "Span",
            new Joint().With
            (
                new Span(),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType)
            ),
            symbol => $$"""
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                        {
                            if (value.IsEmpty)
                            {
                                writer.Write(TypeCollector.NullCollection);
                                return;
                            }
                        
                            int cnt = value.Length;
                            int pos;
                            writer.Write(TypeCollector.GetCollectionHeader(cnt));
                            
                            for (int i = 0; i < cnt; i++)
                            {
                                pos = writer.Advance(4);
                                Serialize(value[i], ref writer);
                                writer.PutLength(pos);
                            }
                        }
                        """
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
                INamedTypeSymbol dictSymbol = (INamedTypeSymbol)symbol;
                var idictSymbol = dictSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "IDictionary")
                                  ?? dictSymbol;
                var keyType = idictSymbol.TypeArguments[0];
                var valType = idictSymbol.TypeArguments[1];
                if (!Selector.Filter(keyType) || !Selector.Filter(valType)) return "";

                bool isUnmanaged = keyType.IsUnmanagedType && valType.IsUnmanagedType;

                string nonTrivialUnmanagedCase = """
                                                     Serialize(item, ref writer);
                                                 """;

                string fallbackCase = """
                                          int pos = writer.Advance(4);
                                          Serialize(item.Key, ref writer);
                                          Serialize(item.Value, ref writer);
                                          writer.PutLength(pos);
                                      """;

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                         {
                             if (value == {{(symbol.IsValueType ? "default" : "null")}})
                             {
                                 writer.Write(TypeCollector.NullCollection);
                                 return;
                             }
                         
                             int cnt = value.Count;
                             writer.Write(TypeCollector.GetCollectionHeader(cnt));
                         
                             foreach (var item in value)
                             {
                                 {{(isUnmanaged ? nonTrivialUnmanagedCase : fallbackCase)}}
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
            symbol => $$"""
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                        {
                            if (value == {{(symbol.IsValueType ? "default" : "null")}})
                            {
                                writer.Write(TypeCollector.NullCollection);
                                return;
                            }
                        
                            int cnt = value.Count;
                            int pos;
                            writer.Write(TypeCollector.GetCollectionHeader(cnt));
                        
                            foreach (var item in value)
                            {
                                pos = writer.Advance(4);
                                Serialize(item.Key, ref writer);
                                Serialize(item.Value, ref writer);
                                writer.PutLength(pos);
                            }
                        }
                        """),
        // non trivial ICollection Ninotypes
        new
        (
            "NonTrivialCollection",
            // Note that we accept non-trivial ICollection types with unmanaged element types
            new Joint().With
            (
                // Note that array is an ICollection, but we don't want to generate code for it
                new Interface("ICollection"),
                new Not(new Array()),
                new NonTrivial("ICollection", "ICollection", "List")
            ),
            symbol =>
            {
                INamedTypeSymbol collSymbol = (INamedTypeSymbol)symbol;
                var iCollSymbol = collSymbol.AllInterfaces.FirstOrDefault(i => i.Name == "ICollection")
                                  ?? collSymbol;
                var elemType = iCollSymbol.TypeArguments[0];
                if (!Selector.Filter(elemType)) return "";

                bool isUnmanaged = elemType.IsUnmanagedType;

                string nonTrivialUnmanagedCase = """
                                                     Serialize(item, ref writer);
                                                 """;

                string fallbackCase = """
                                            int pos = writer.Advance(4);
                                            Serialize(item, ref writer);
                                            writer.PutLength(pos);
                                      """;

                return $$"""
                         [MethodImpl(MethodImplOptions.AggressiveInlining)]
                         public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                         {
                             if (value == {{(symbol.IsValueType ? "default" : "null")}})
                             {
                                 writer.Write(TypeCollector.NullCollection);
                                 return;
                             }
                         
                             int cnt = value.Count;
                             writer.Write(TypeCollector.GetCollectionHeader(cnt));
                         
                             foreach (var item in value)
                             {
                                 {{(isUnmanaged ? nonTrivialUnmanagedCase : fallbackCase)}}
                             }
                         }
                         """;
            }
        ),
        // trivial ICollection Ninotypes
        new
        (
            "TrivialCollection",
            new Joint().With
            (
                // Note that array is an ICollection, but we don't want to generate code for it
                new Interface("ICollection"),
                new Not(new Array()),
                new Not(new NonTrivial("ICollection", "ICollection", "List")),
                new AnyTypeArgument(symbol => !symbol.IsUnmanagedType)
            ),
            symbol => $$"""
                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public static void Serialize(this {{symbol.ToDisplayString()}} value, ref Writer writer)
                        {
                            if (value == {{(symbol.IsValueType ? "default" : "null")}})
                            {
                                writer.Write(TypeCollector.NullCollection);
                                return;
                            }
                        
                            int cnt = value.Count;
                            int pos;
                            writer.Write(TypeCollector.GetCollectionHeader(cnt));
                        
                            foreach (var item in value)
                            {
                                pos = writer.Advance(4);
                                Serialize(item, ref writer);
                                writer.PutLength(pos);
                            }
                        }
                        """),
    };

    private string GenericTupleLikeMethods(ITypeSymbol type, params string[] fields)
    {
        string[] serializeValues = fields.Select(field => $"Serialize(value.{field}, ref writer);").ToArray();

        return $$"""
                 [MethodImpl(MethodImplOptions.AggressiveInlining)]
                 public static void Serialize(this {{type.ToDisplayString()}} value, ref Writer writer)
                 {
                     {{string.Join("\n    ", serializeValues)}};
                 }
                 """;
    }
}