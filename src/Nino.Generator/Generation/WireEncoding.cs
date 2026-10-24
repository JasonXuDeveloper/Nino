using System.Linq;
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
    /// <paramref name="elements"/> (the encodings of its element formatters, null for type arguments), or null if it
    /// has none of its own.
    /// </summary>
    public static string? OfCore(string formatter, ITypeSymbol[] types, string?[] elements, Compilation compilation)
    {
        var e = elements.Where(static x => x != null).ToArray();
        return formatter switch
        {
            "ArrayFormatter" or "ArraySegmentFormatter" or "MemoryFormatter" or "ReadOnlyMemoryFormatter"
                or "ReadOnlySequenceFormatter" or "ListFormatter" or "CollectionFormatter" or "HashSetFormatter"
                or "QueueFormatter" or "StackFormatter" or "ProducerConsumerFormatter" or "BlockingCollectionFormatter"
                or "InterfaceListFormatter" or "InterfaceSetFormatter" => $"seq({e[0]})",
            "UnmanagedArrayFormatter" or "UnmanagedListFormatter" => $"seq({RawLayout.Describe(types[types.Length - 1])})",
            "NullableArrayFormatter" or "NullableListFormatter" => $"seq({RawLayout.Describe(compilation
                .GetSpecialType(SpecialType.System_Nullable_T).Construct(types[types.Length - 1]))})",
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
    /// The items of a value tuple of eight or more: the seven, then those of its rest (a value tuple), as one tuple of
    /// every item, which is how they are written.
    /// </summary>
    private static string?[] Flat(string?[] items)
    {
        string? rest = items[7];
        if (rest == null || !rest.StartsWith("tuple(", System.StringComparison.Ordinal))
            return items;
        var flat = (string?[])items.Clone();
        flat[7] = rest.Substring(6, rest.Length - 7);
        return flat;
    }
}
