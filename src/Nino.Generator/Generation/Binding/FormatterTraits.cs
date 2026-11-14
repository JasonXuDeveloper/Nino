using System;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// What generated code knows about a formatter beyond its name: which of Nino.Core's formatters it is, and how it
/// reads and writes. Decided once, from the symbols (<see cref="FormatterTraitsOf"/>), so
/// emitters test flags instead of formatter names.
/// </summary>
[Flags]
internal enum FormatterTraits
{
    None = 0,

    /// <summary>One of Nino.Core's formatters.</summary>
    Core = 1,

    /// <summary><c>StringFormatter</c> (UTF-16): generated code calls the reader and writer directly.</summary>
    String = 1 << 1,

    /// <summary><c>Utf8StringFormatter</c>: generated code calls the reader and writer directly.</summary>
    Utf8String = 1 << 2,

    /// <summary><c>UnmanagedFormatter&lt;T&gt;</c>: the value's raw memory.</summary>
    Raw = 1 << 3,

    /// <summary><c>ObjectFormatter</c>.</summary>
    Object = 1 << 4,

    /// <summary><c>CachedFormatter&lt;T&gt;</c>: the formatter is resolved at runtime.</summary>
    Cached = 1 << 5,

    /// <summary>
    /// Reads and writes without a loop over elements in the generated body, and never passes the reader's or writer's
    /// address to a call that is not inlined: strings, nullable raw values, raw arrays and lists, and dictionaries of
    /// raw keys and raw or UTF-16 string values.
    /// </summary>
    Flat = 1 << 6,

    /// <summary>The generated formatter of the type itself.</summary>
    Generated = 1 << 7,

    /// <summary>Reads without creating reference or deferral state in its reader.</summary>
    PreservesReadContext = 1 << 8,
}

internal static class FormatterTraitsOf
{
    /// <summary>
    /// The traits of a Nino.Core formatter. Only the final two argument traits affect this classification.
    /// </summary>
    public static FormatterTraits Of(string name, int count = 0, FormatterTraits last = FormatterTraits.None,
        FormatterTraits previous = FormatterTraits.None) =>
        FormatterTraits.Core | name switch
        {
            "StringFormatter" => FormatterTraits.String | FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            "Utf8StringFormatter" => FormatterTraits.Utf8String | FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            "UnmanagedFormatter" => FormatterTraits.Raw,
            "DateTimeOffsetFormatter" or "BigIntegerFormatter" or "UriFormatter" or "VersionFormatter" or
                "StringBuilderFormatter" or "CultureInfoFormatter" or "TimeZoneInfoFormatter" or "BitArrayFormatter" =>
                FormatterTraits.PreservesReadContext,
            "ObjectFormatter" => FormatterTraits.Object,
            "CachedFormatter" => FormatterTraits.Cached,
            "UnmanagedArrayFormatter" or "UnmanagedListFormatter" => FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            // A raw value's presence byte and bytes in one reservation (Writer.WriteNullableRaw, Reader.ReadNullableRaw),
            // inlined as a raw member is: the body can keep a local writer (TypeModel.LocalWriter).
            "NullableFormatter" when count == 2 && (last & FormatterTraits.Raw) != 0 =>
                FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            "NullableFormatter" when count == 2 && (last & FormatterTraits.PreservesReadContext) != 0 =>
                FormatterTraits.PreservesReadContext,
            "NinoNullableFormatter" or "RawNullableFormatter" =>
                FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            "NullableArrayFormatter" or "NullableListFormatter" or "NinoNullableArrayFormatter"
                or "NinoNullableListFormatter" => FormatterTraits.PreservesReadContext,
            "ArrayFormatter" or "MemoryFormatter" or "ReadOnlyMemoryFormatter" or "ArraySegmentFormatter" or
                "ReadOnlySequenceFormatter" when count == 2 &&
                                   (last & (FormatterTraits.Raw | FormatterTraits.PreservesReadContext)) != 0 =>
                FormatterTraits.PreservesReadContext,
            "ListFormatter" when count == 3 &&
                                   (last & (FormatterTraits.Raw | FormatterTraits.PreservesReadContext)) != 0 =>
                FormatterTraits.PreservesReadContext,
            "KeyValuePairFormatter" when count == 4 &&
                                         (previous & (FormatterTraits.Raw | FormatterTraits.PreservesReadContext)) != 0 &&
                                         (last & (FormatterTraits.Raw | FormatterTraits.PreservesReadContext)) != 0 =>
                FormatterTraits.PreservesReadContext,
            // DictionaryFormatter<[TDictionary, ]TKey, TValue, TKeyFormatter, TValueFormatter>
            "DictionaryFormatter" when count >= 4 &&
                                       (previous & FormatterTraits.Raw) != 0 &&
                                       (last &
                                        (FormatterTraits.Raw | FormatterTraits.String)) != 0 =>
                FormatterTraits.Flat | FormatterTraits.PreservesReadContext,
            _ => FormatterTraits.None,
        };

    /// <summary>The traits of a formatter named by its symbol (a <c>[NinoMemberFormatter]</c>).</summary>
    public static FormatterTraits Of(INamedTypeSymbol formatter)
    {
        if (!IsCore(formatter))
            return FormatterTraits.None;

        var args = formatter.TypeArguments;
        int count = args.Length;
        return Of(formatter.Name, count,
            count > 0 && args[count - 1] is INamedTypeSymbol last ? Of(last) : FormatterTraits.None,
            count > 1 && args[count - 2] is INamedTypeSymbol previous ? Of(previous) : FormatterTraits.None);
    }

    /// <summary>Whether <paramref name="formatter"/> is declared by Nino.Core.</summary>
    public static bool IsCore(INamedTypeSymbol formatter) =>
        formatter.ContainingAssembly?.Name == NinoNames.CoreAssembly;
}
