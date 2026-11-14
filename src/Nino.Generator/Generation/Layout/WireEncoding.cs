using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// How a value is written, as the published layout shows it (<c>NinoTypeLayoutAttribute.Members</c>): what the bytes
/// are rather than which formatter writes them, so a tool (a schema check, the TypeScript generator) can read and
/// write them without knowing Nino.Core's formatters.
/// </summary>
internal static class WireEncoding
{
    public const string String = "string";
    public const string Utf8 = "utf8";
    public const string Object = "object";

    /// <summary>A <c>[NinoType]</c> or a type resolved at run time (a type parameter): its own layout describes it.</summary>
    public static string Type(ITypeSymbol type) => $"type({TypeNames.Plain(TypeNames.Global(type))})";

    /// <summary>A formatter whose bytes are not described: another library's or a custom one.</summary>
    public static string Formatter(string code) => $"formatter({TypeNames.Plain(code)})";

    /// <summary>
    /// The encoding of a Nino.Core formatter bound to <paramref name="types"/> (bound type arguments) and
    /// <paramref name="elements"/> (an owned array of element encodings in argument order), or null if the
    /// formatter has no built-in encoding. The value-tuple case may flatten the final element in place.
    /// </summary>
    public static string? OfCore(string formatter, ITypeSymbol[] types, string[] elements, Compilation compilation)
    {
        var e = elements;
        return formatter switch
        {
            "ArrayFormatter" or "ArraySegmentFormatter" or "MemoryFormatter" or "ReadOnlyMemoryFormatter"
                or "ReadOnlySequenceFormatter" or "ListFormatter" or "CollectionFormatter" or "HashSetFormatter"
                or "QueueFormatter" or "StackFormatter" or "ProducerConsumerFormatter"
                or "ConcurrentStackFormatter" or "BlockingCollectionFormatter"
                or "InterfaceListFormatter" or "InterfaceSetFormatter" => $"seq({e[0]})",
            "UnmanagedArrayFormatter" or "UnmanagedListFormatter" => $"seq({RawLayout.Describe(types[types.Length - 1])})",
            // NinoNullable<T> is written as T?: the same encodings.
            "NullableArrayFormatter" or "NullableListFormatter" or "NinoNullableArrayFormatter"
                or "NinoNullableListFormatter" => $"seq({RawLayout.Describe(compilation
                .GetSpecialType(SpecialType.System_Nullable_T).Construct(types[types.Length - 1]))})",
            "NinoNullableFormatter" or "RawNullableFormatter" =>
                $"nullable({RawLayout.Describe(types[types.Length - 1])})",
            "DictionaryFormatter" or "DictionaryLikeFormatter" or "InterfaceDictionaryFormatter" =>
                $"map({e[0]},{e[1]})",
            "KeyValuePairFormatter" => $"pair({e[0]},{e[1]})",
            "PriorityQueueFormatter" => $"seq(pair({e[0]},{e[1]}))",
            "GroupingFormatter" => $"group({e[0]},{e[1]})",
            "LookupFormatter" => $"seq(group({e[0]},{e[1]}))",
            "NullableFormatter" or "LazyFormatter" => $"nullable({e[0]})",
            "ValueTupleFormatter" => $"tuple({string.Join(",", e.Length == 8 ? Flat(e) : e)})",
            "TupleFormatter" => $"nullable(tuple({string.Join(",", e)}))",
            "Array2DFormatter" => $"grid(2,{e[0]})",
            "Array3DFormatter" => $"grid(3,{e[0]})",
            "Array4DFormatter" => $"grid(4,{e[0]})",
            "IntPtrFormatter" or "UIntPtrFormatter" => "raw(8)",
            "UriFormatter" or "CultureInfoFormatter" or "StringBuilderFormatter" => String,
            "DateTimeOffsetFormatter" => "datetimeoffset",
            "BigIntegerFormatter" => "bigint",
            "VersionFormatter" => "version",
            "BitArrayFormatter" => "bits",
            "TimeZoneInfoFormatter" => "timezone",
            _ => null,
        };
    }

    /// <summary>
    /// The encoding of a raw <c>T?</c> or a <c>NinoNullable&lt;T&gt;</c> as the element of a sequence: a slot of a
    /// fixed size (the layout of <c>Nullable&lt;T&gt;</c>), where a single value is <c>nullable(...)</c>. Null for
    /// every other formatter.
    /// </summary>
    public static string? SlotOf(string formatter, ITypeSymbol[] types, Compilation compilation) =>
        formatter is "RawNullableFormatter" or "NinoNullableFormatter"
            ? RawLayout.Describe(compilation.GetSpecialType(SpecialType.System_Nullable_T)
                .Construct(types[types.Length - 1]))
            : null;

    /// <summary>
    /// Which element formatter of a Nino.Core formatter writes the elements of a sequence (0 or 1, counted among its
    /// element formatters), which are slots where they have one (<see cref="SlotOf"/>); -1 for a formatter that
    /// writes single values only (pairs, maps, tuples, nullables).
    /// </summary>
    public static int SequenceElement(string formatter) => formatter switch
    {
        "ArrayFormatter" or "ArraySegmentFormatter" or "MemoryFormatter" or "ReadOnlyMemoryFormatter"
            or "ReadOnlySequenceFormatter" or "ListFormatter" or "CollectionFormatter" or "HashSetFormatter"
            or "QueueFormatter" or "StackFormatter" or "ProducerConsumerFormatter"
            or "ConcurrentStackFormatter" or "BlockingCollectionFormatter"
            or "InterfaceListFormatter" or "InterfaceSetFormatter"
            or "Array2DFormatter" or "Array3DFormatter" or "Array4DFormatter" => 0,
        "GroupingFormatter" or "LookupFormatter" => 1,
        _ => -1,
    };

    /// <summary>
    /// The items of a value tuple of eight or more: the seven, then those of its rest (a value tuple), as one tuple of
    /// every item, which is how they are written.
    /// </summary>
    private static string[] Flat(string[] items)
    {
        string? rest = items[7];
        if (rest == null || !rest.StartsWith("tuple(", System.StringComparison.Ordinal))
            return items;
        items[7] = rest.Substring(6, rest.Length - 7);
        return items;
    }
}
