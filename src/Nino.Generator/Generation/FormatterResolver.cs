using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Chooses the formatter type (as C# source) for a member type. Mirrors
/// <c>Nino.Core.NinoFormatterResolver</c>, so generated code and runtime-resolved formatters write the same bytes.
/// </summary>
internal sealed class FormatterResolver
{
    private const string Core = "global::Nino.Core.";
    private readonly IAssemblySymbol _assembly;
    // The assembly compiled with WEAK_VERSION_TOLERANCE, if any: its types are all version tolerant.
    private readonly IAssemblySymbol? _weakAssembly;

    public FormatterResolver(IAssemblySymbol assembly, bool weakTolerance)
    {
        _assembly = assembly;
        _weakAssembly = weakTolerance ? assembly : null;
    }

    public static string Cached(ITypeSymbol type) => $"{Core}CachedFormatter<{TypeNames.Global(type)}>";

    /// <summary>Whether this compilation generates a formatter for <paramref name="type"/>.</summary>
    public bool IsGeneratedHere(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, _assembly) &&
               ModelBuilder.IsEligible(definition) && definition.IsNinoType();
    }

    public static string GeneratedFormatter(INamedTypeSymbol type)
    {
        var name = "global::NinoGen." + TypeNames.FormatterName(type.OriginalDefinition);
        var args = TypeNames.AllTypeArguments(type);
        return args.Count == 0 ? name : $"{name}<{string.Join(", ", args.Select(TypeNames.Global))}>";
    }

    /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
    public static bool IsRawUnmanaged(ITypeSymbol type, IAssemblySymbol? weakAssembly)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType)
            return false;
        if (type.IsNinoType())
            return type is INamedTypeSymbol ninoStruct && ModelBuilder.IsRawStruct(ninoStruct, weakAssembly);
        if (type is INamedTypeSymbol named)
        {
            if (named.IsTupleType)
                return false;
            var definition = named.OriginalDefinition;
            if (definition.SpecialType == SpecialType.System_Nullable_T)
                return false;
            var name = MetadataName(definition);
            if (name == "System.Collections.Generic.KeyValuePair`2" || name.StartsWith("System.ValueTuple`"))
                return false;
        }

        return true;
    }

    /// <summary>Size expression of a raw unmanaged type.</summary>
    public static string RawSize(ITypeSymbol type)
    {
        var t = type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
            ? underlying
            : type;
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                return "1";
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                return "2";
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                return "4";
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                return "8";
            case SpecialType.System_Decimal:
                return "16";
            default:
                return $"{Core}NinoUnsafe.SizeOf<{TypeNames.Global(type)}>()";
        }
    }

    public static string MetadataName(INamedTypeSymbol definition)
    {
        var ns = definition.ContainingNamespace;
        var nsName = TypeNames.Namespace(ns);
        var prefix = nsName.Length == 0 ? "" : nsName + ".";
        return definition.ContainingType != null
            ? MetadataName(definition.ContainingType) + "+" + definition.MetadataName
            : prefix + definition.MetadataName;
    }

    /// <summary>
    /// Returns the formatter for <paramref name="type"/>, or null if Nino cannot serialize it at all.
    /// Types it does not know are resolved at runtime through <c>CachedFormatter&lt;T&gt;</c>.
    /// </summary>
    public string? Resolve(ITypeSymbol type, bool utf8)
    {
        try
        {
            return ResolveCore(type, utf8);
        }
        catch (UnsupportedTypeException)
        {
            return null;
        }
    }

    private sealed class UnsupportedTypeException : System.Exception
    {
    }

    private string? ResolveCore(ITypeSymbol type, bool utf8)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        switch (type)
        {
            case ITypeParameterSymbol:
                return Cached(type);
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return null;
            case IArrayTypeSymbol array:
                return ResolveArray(array, utf8);
            case INamedTypeSymbol named:
                return ResolveNamed(named, utf8);
            default:
                return null;
        }
    }

    // An element type Nino cannot serialize makes the whole member unserializable.
    private string Element(ITypeSymbol type, bool utf8) => ResolveCore(type, utf8) ?? throw new UnsupportedTypeException();

    private string? ResolveArray(IArrayTypeSymbol array, bool utf8)
    {
        var element = array.ElementType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var e = TypeNames.Global(element);
        if (array.IsSZArray)
        {
            if (IsRawUnmanaged(element, _weakAssembly))
                return $"{Core}UnmanagedArrayFormatter<{e}>";
            if (element is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
                IsRawUnmanaged(nullable.TypeArguments[0], _weakAssembly))
                return $"{Core}NullableArrayFormatter<{TypeNames.Global(nullable.TypeArguments[0])}>";
            return $"{Core}ArrayFormatter<{e}, {Element(element, utf8)}>";
        }

        return array.Rank switch
        {
            2 => $"{Core}Array2DFormatter<{e}, {Element(element, utf8)}>",
            3 => $"{Core}Array3DFormatter<{e}, {Element(element, utf8)}>",
            4 => $"{Core}Array4DFormatter<{e}, {Element(element, utf8)}>",
            _ => null,
        };
    }

    private string? ResolveNamed(INamedTypeSymbol type, bool utf8)
    {
        if (type.IsRefLikeType || type.TypeKind == TypeKind.Delegate)
            return null;

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return utf8 ? $"{Core}Utf8StringFormatter" : $"{Core}StringFormatter";
            case SpecialType.System_Object:
                return $"{Core}PolymorphicFormatter<object>";
        }

        var t = TypeNames.Global(type);
        if (IsRawUnmanaged(type, _weakAssembly))
            return $"{Core}UnmanagedFormatter<{t}>";

        if (type.IsTupleType && type.TupleUnderlyingType is { } underlying)
            type = underlying;

        var args = type.TypeArguments.Select(a => a.WithNullableAnnotation(NullableAnnotation.NotAnnotated))
            .ToArray();
        var a = args.Select(TypeNames.Global).ToArray();

        if (type.IsGenericType)
        {
            var definition = MetadataName(type.OriginalDefinition);
            switch (definition)
            {
                case "System.Nullable`1":
                    return $"{Core}NullableFormatter<{a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.List`1":
                    return List(t, args[0], utf8);
                case "System.Collections.Generic.Dictionary`2":
                    return $"{Core}DictionaryFormatter<{t}, {a[0]}, {a[1]}, {Element(args[0], utf8)}, {Element(args[1], utf8)}>";
                case "System.Collections.Generic.HashSet`1":
                    return $"{Core}HashSetFormatter<{t}, {a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.Queue`1":
                    return $"{Core}QueueFormatter<{a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.Stack`1":
                    return $"{Core}StackFormatter<{a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.KeyValuePair`2":
                    return $"{Core}KeyValuePairFormatter<{a[0]}, {a[1]}, {Element(args[0], utf8)}, {Element(args[1], utf8)}>";
                case "System.ArraySegment`1":
                    return $"{Core}ArraySegmentFormatter<{a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.PriorityQueue`2":
                    return $"global::NinoGen.NinoPriorityQueueFormatter<{a[0]}, {a[1]}, {Element(args[0], utf8)}, {Element(args[1], utf8)}>";
                case "System.Collections.Generic.SortedSet`1":
                case "System.Collections.Generic.LinkedList`1":
                    return $"{Core}CollectionFormatter<{t}, {a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Concurrent.ConcurrentQueue`1":
                case "System.Collections.Concurrent.ConcurrentStack`1":
                case "System.Collections.Concurrent.ConcurrentBag`1":
                    return $"{Core}ProducerConsumerFormatter<{t}, {a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Concurrent.ConcurrentDictionary`2":
                case "System.Collections.Generic.SortedDictionary`2":
                case "System.Collections.Generic.SortedList`2":
                    return $"{Core}DictionaryLikeFormatter<{t}, {a[0]}, {a[1]}, {Element(args[0], utf8)}, {Element(args[1], utf8)}>";
                case "System.Collections.Generic.IEnumerable`1":
                case "System.Collections.Generic.ICollection`1":
                case "System.Collections.Generic.IList`1":
                case "System.Collections.Generic.IReadOnlyCollection`1":
                case "System.Collections.Generic.IReadOnlyList`1":
                    return $"{Core}InterfaceCollectionFormatter<{t}, global::System.Collections.Generic.List<{a[0]}>, {a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.ISet`1":
                case "System.Collections.Generic.IReadOnlySet`1":
                    return $"{Core}InterfaceCollectionFormatter<{t}, global::System.Collections.Generic.HashSet<{a[0]}>, {a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Generic.IDictionary`2":
                case "System.Collections.Generic.IReadOnlyDictionary`2":
                case "System.Collections.ObjectModel.ReadOnlyDictionary`2":
                    return $"{Core}InterfaceDictionaryFormatter<{t}, {a[0]}, {a[1]}, {Element(args[0], utf8)}, {Element(args[1], utf8)}>";
                case "System.Collections.Immutable.ImmutableArray`1":
                    return $"global::NinoGen.NinoImmutableArrayFormatter<{a[0]}, {Element(args[0], utf8)}>";
                case "System.Collections.Immutable.ImmutableList`1":
                    return $"global::NinoGen.NinoImmutableListFormatter<{a[0]}, {Element(args[0], utf8)}>";
            }

            if (definition.StartsWith("System.ValueTuple`") || definition.StartsWith("System.Tuple`"))
            {
                if (args.Length > 7)
                    return null;
                var formatter = definition.StartsWith("System.ValueTuple`") ? "ValueTupleFormatter" : "TupleFormatter";
                return $"{Core}{formatter}<{string.Join(", ", a)}, {string.Join(", ", args.Select(x => Element(x, utf8)))}>";
            }
        }

        // Checked after the built-in generics: walking the base types and interfaces of BCL instantiations
        // (List<T>, Dictionary<K, V>...) is the most expensive part of a model build.
        if (type.IsNinoType())
            return IsGeneratedHere(type) ? GeneratedFormatter(type) : Cached(type);

        return ResolveUserCollection(type, t, utf8);
    }

    /// <summary>
    /// The collection formatter for a user collection: a class derived from <c>List&lt;T&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c> or <c>Dictionary&lt;TKey, TValue&gt;</c>, or implementing the collection interfaces,
    /// with a public parameterless constructor. Also used for the elements of <c>[NinoType]</c> collections.
    /// </summary>
    public string? ResolveUserCollection(INamedTypeSymbol type) => ResolveUserCollection(type, TypeNames.Global(type), false);

    private string? ResolveUserCollection(INamedTypeSymbol type, string t, bool utf8)
    {
        // User collections derived from List<T>, HashSet<T> or Dictionary<TKey, TValue>, or implementing the
        // collection interfaces, with a public parameterless constructor.
        if (type.TypeKind == TypeKind.Class && !type.IsAbstract &&
            type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                if (!b.IsGenericType)
                    continue;
                var ba = b.TypeArguments;
                switch (MetadataName(b.OriginalDefinition))
                {
                    case "System.Collections.Generic.List`1":
                        return List(t, ba[0], utf8);
                    case "System.Collections.Generic.HashSet`1":
                        return $"{Core}HashSetFormatter<{t}, {TypeNames.Global(ba[0])}, {Element(ba[0], utf8)}>";
                    case "System.Collections.Generic.Dictionary`2":
                        return $"{Core}DictionaryFormatter<{t}, {TypeNames.Global(ba[0])}, {TypeNames.Global(ba[1])}, {Element(ba[0], utf8)}, {Element(ba[1], utf8)}>";
                }
            }

            var dictionary = type.AllInterfaces.FirstOrDefault(i =>
                MetadataName(i.OriginalDefinition) == "System.Collections.Generic.IDictionary`2");
            if (dictionary != null)
            {
                var da = dictionary.TypeArguments;
                return $"{Core}DictionaryLikeFormatter<{t}, {TypeNames.Global(da[0])}, {TypeNames.Global(da[1])}, {Element(da[0], utf8)}, {Element(da[1], utf8)}>";
            }

            var producer = type.AllInterfaces.FirstOrDefault(i =>
                MetadataName(i.OriginalDefinition) == "System.Collections.Concurrent.IProducerConsumerCollection`1");
            if (producer != null)
                return $"{Core}ProducerConsumerFormatter<{t}, {TypeNames.Global(producer.TypeArguments[0])}, {Element(producer.TypeArguments[0], utf8)}>";

            var collection = type.AllInterfaces.FirstOrDefault(i =>
                MetadataName(i.OriginalDefinition) == "System.Collections.Generic.ICollection`1");
            if (collection != null)
                return $"{Core}CollectionFormatter<{t}, {TypeNames.Global(collection.TypeArguments[0])}, {Element(collection.TypeArguments[0], utf8)}>";
        }

        return null;
    }

    private string List(string listType, ITypeSymbol element, bool utf8)
    {
        element = element.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var e = TypeNames.Global(element);
        if (IsRawUnmanaged(element, _weakAssembly))
            return $"{Core}UnmanagedListFormatter<{listType}, {e}>";
        if (element is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
            IsRawUnmanaged(nullable.TypeArguments[0], _weakAssembly))
            return $"{Core}NullableListFormatter<{listType}, {TypeNames.Global(nullable.TypeArguments[0])}>";
        return $"{Core}ListFormatter<{listType}, {e}, {Element(element, utf8)}>";
    }
}
