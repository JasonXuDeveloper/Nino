using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// What generated code knows about a formatter beyond its name: which of Nino.Core's formatters it is, and how it
/// reads and writes. Decided once, from the symbols (<see cref="Of(string, IReadOnlyList{FormatterTraits})"/>), so
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
    /// address to a call that is not inlined: strings, raw arrays and lists, and dictionaries of raw keys and raw or
    /// UTF-16 string values.
    /// </summary>
    Flat = 1 << 6,

    /// <summary>The generated formatter of the type itself.</summary>
    Generated = 1 << 7,
}

internal static class FormatterTraitsOf
{
    /// <summary>
    /// The traits of the Nino.Core formatter named <paramref name="name"/> (its metadata name without arity), whose
    /// type arguments have the traits <paramref name="arguments"/> (<see cref="FormatterTraits.None"/> for those that
    /// are no formatters).
    /// </summary>
    public static FormatterTraits Of(string name, IReadOnlyList<FormatterTraits>? arguments = null) =>
        FormatterTraits.Core | name switch
        {
            "StringFormatter" => FormatterTraits.String | FormatterTraits.Flat,
            "Utf8StringFormatter" => FormatterTraits.Utf8String | FormatterTraits.Flat,
            "UnmanagedFormatter" => FormatterTraits.Raw,
            "ObjectFormatter" => FormatterTraits.Object,
            "CachedFormatter" => FormatterTraits.Cached,
            "UnmanagedArrayFormatter" or "UnmanagedListFormatter" => FormatterTraits.Flat,
            // DictionaryFormatter<[TDictionary, ]TKey, TValue, TKeyFormatter, TValueFormatter>
            "DictionaryFormatter" when arguments is { Count: >= 4 } &&
                                       (arguments[arguments.Count - 2] & FormatterTraits.Raw) != 0 &&
                                       (arguments[arguments.Count - 1] &
                                        (FormatterTraits.Raw | FormatterTraits.String)) != 0 => FormatterTraits.Flat,
            _ => FormatterTraits.None,
        };

    /// <summary>The traits of a formatter named by its symbol (a <c>[NinoCustomFormatter]</c>).</summary>
    public static FormatterTraits Of(INamedTypeSymbol formatter) =>
        IsCore(formatter)
            ? Of(formatter.Name, formatter.TypeArguments
                .Select(static a => a is INamedTypeSymbol named ? Of(named) : FormatterTraits.None).ToArray())
            : FormatterTraits.None;

    /// <summary>Whether <paramref name="formatter"/> is declared by Nino.Core.</summary>
    public static bool IsCore(INamedTypeSymbol formatter) =>
        formatter.ContainingAssembly?.Name == NinoNames.CoreAssembly;
}
