using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>Names of types as generated code and the runtime spell them, and the ids hashed from them.</summary>
internal static class TypeNames
{
    // Built by hand: SymbolDisplay is by far the most expensive part of a model build (it pools and trims display
    // part arrays per call), and every transform re-runs on each edit.

    /// <summary>Prefix of every Nino.Core type in generated code.</summary>
    public const string Core = "global::Nino.Core.";

    /// <summary>Namespace of everything the generator emits.</summary>
    public const string GenNamespace = "NinoGen";

    /// <summary>Prefix of every generated type in generated code.</summary>
    public const string Gen = "global::" + GenNamespace + ".";

    /// <summary>Prefix of <c>System.Runtime.CompilerServices</c> types in generated code.</summary>
    public const string CompilerServices = "global::System.Runtime.CompilerServices.";

    /// <summary>Hides generated helpers from IntelliSense.</summary>
    public const string HiddenAttribute =
        "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]";

    /// <summary>
    /// The aggressive inlining attribute, spelled out (formatter files use the shorter alias from
    /// <c>FormatterNames.Inline</c> instead).
    /// </summary>
    public const string InlineAttribute =
        "[" + CompilerServices + "MethodImpl(" + CompilerServices + "MethodImplOptions.AggressiveInlining)]";

    /// <summary>Fully qualified C# name, e.g. <c>global::Ns.Outer.Box&lt;int&gt;</c>.</summary>
    public static string Global(ITypeSymbol type)
    {
        var sb = new StringBuilder(64);
        AppendGlobal(sb, type);
        return sb.ToString();
    }

    /// <summary>Dotted namespace name without <c>global::</c>; empty for the global namespace.</summary>
    public static string Namespace(INamespaceSymbol? ns)
    {
        if (ns == null || ns.IsGlobalNamespace)
            return "";
        var parent = Namespace(ns.ContainingNamespace);
        return parent.Length == 0 ? ns.Name : parent + "." + ns.Name;
    }

    /// <summary>
    /// Whether the metadata name of <paramref name="definition"/> (e.g. <c>System.Collections.Generic.List`1</c>,
    /// nested types joined by <c>+</c>) is <paramref name="name"/>, compared without building it. For BCL types,
    /// which may come from any of several referenced assemblies, so are matched by name rather than by symbol.
    /// </summary>
    public static bool HasMetadataName(INamedTypeSymbol definition, string name) =>
        MatchMetadataName(definition, name, name.Length) == 0;

    /// <summary>
    /// Whether generated code in <paramref name="compilation"/> can name the type <paramref name="metadataName"/>:
    /// exactly one definition it can access. Libraries carry internal polyfills of BCL attributes (ZLinq's
    /// netstandard build has <c>ModuleInitializerAttribute</c>), which exist but cannot be used; a second
    /// accessible one (a polyfill seen through InternalsVisibleTo) makes the name ambiguous.
    /// </summary>
    public static bool CanName(Compilation compilation, string metadataName) =>
        compilation.GetTypesByMetadataName(metadataName)
            .Count(t => compilation.IsSymbolAccessibleWithin(t, compilation.Assembly)) == 1;

    /// <summary>Whether <paramref name="definition"/> is a generic <c>System.ValueTuple`N</c>.</summary>
    public static bool IsValueTuple(INamedTypeSymbol definition) =>
        definition is { Name: "ValueTuple", Arity: > 0, ContainingType: null } &&
        definition.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true };

    // Matches the symbol's name (and its containers') against name[..end]; returns where the match starts, or -1.
    private static int MatchMetadataName(ISymbol symbol, string name, int end)
    {
        string own = symbol.MetadataName;
        int start = end - own.Length;
        if (start < 0 || string.CompareOrdinal(name, start, own, 0, own.Length) != 0)
            return -1;
        ISymbol? container = symbol.ContainingType;
        char separator = '+';
        if (container == null)
        {
            if (symbol.ContainingNamespace is not { IsGlobalNamespace: false } ns)
                return start;
            container = ns;
            separator = '.';
        }

        return start > 0 && name[start - 1] == separator ? MatchMetadataName(container, name, start - 1) : -1;
    }

    /// <summary>
    /// Open generic name for <c>typeof</c>, e.g. <c>global::Ns.Outer&lt;&gt;.Box&lt;,&gt;</c>.
    /// </summary>
    public static string OpenName(INamedTypeSymbol type)
    {
        string own = Identifier(type.Name) + (type.Arity > 0 ? "<" + new string(',', type.Arity - 1) + ">" : "");
        if (type.ContainingType != null)
            return OpenName(type.ContainingType) + "." + own;
        var sb = new StringBuilder("global::");
        AppendNamespace(sb, type.ContainingNamespace);
        return sb.Append(own).ToString();
    }

    /// <summary>The identifier as C# source: keywords are escaped with <c>@</c>.</summary>
    public static string Identifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;

    /// <summary>Appends <c>Ns.Sub.</c> (escaped, with a trailing dot); nothing for the global namespace.</summary>
    private static void AppendNamespace(StringBuilder sb, INamespaceSymbol? ns)
    {
        if (ns == null || ns.IsGlobalNamespace)
            return;
        AppendNamespace(sb, ns.ContainingNamespace);
        sb.Append(Identifier(ns.Name)).Append('.');
    }

    private static void AppendGlobal(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
            {
                // C# writes rank specifiers outermost first: int[][,] is an array of int[,].
                ITypeSymbol element = array;
                var ranks = new List<int>();
                while (element is IArrayTypeSymbol a)
                {
                    ranks.Add(a.Rank);
                    element = a.ElementType;
                }

                AppendGlobal(sb, element);
                foreach (var rank in ranks)
                {
                    sb.Append('[');
                    sb.Append(',', rank - 1);
                    sb.Append(']');
                }

                return;
            }
            case IPointerTypeSymbol pointer:
                AppendGlobal(sb, pointer.PointedAtType);
                sb.Append('*');
                return;
            case ITypeParameterSymbol parameter:
                sb.Append(Identifier(parameter.Name));
                return;
            case INamedTypeSymbol named when named.TypeKind != TypeKind.Error:
            {
                var keyword = Keyword(named.SpecialType);
                if (keyword != null)
                {
                    sb.Append(keyword);
                    return;
                }

                if (named.IsTupleType && named.TupleUnderlyingType != null)
                    named = named.TupleUnderlyingType;
                AppendNamed(sb, named);
                return;
            }
            default:
                sb.Append(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                return;
        }
    }

    private static void AppendNamed(StringBuilder sb, INamedTypeSymbol named)
    {
        if (named.ContainingType != null)
        {
            AppendNamed(sb, named.ContainingType);
            sb.Append('.');
        }
        else
        {
            sb.Append("global::");
            AppendNamespace(sb, named.ContainingNamespace);
        }

        sb.Append(Identifier(named.Name));
        if (named.TypeArguments.Length == 0)
            return;
        sb.Append('<');
        for (int i = 0; i < named.TypeArguments.Length; i++)
        {
            if (i != 0)
                sb.Append(", ");
            AppendGlobal(sb, named.TypeArguments[i]);
        }

        sb.Append('>');
    }

    private static string? Keyword(SpecialType type) => type switch
    {
        SpecialType.System_Object => "object",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_Char => "char",
        SpecialType.System_SByte => "sbyte",
        SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Decimal => "decimal",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_String => "string",
        SpecialType.System_Void => "void",
        _ => null,
    };

    /// <summary>All type arguments of a named type, outermost containing type first.</summary>
    public static IReadOnlyList<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
    {
        if (type.ContainingType == null)
            return type.TypeArguments;
        var result = new List<ITypeSymbol>(AllTypeArguments(type.ContainingType));
        result.AddRange(type.TypeArguments);
        return result;
    }

    /// <summary>
    /// Whether <paramref name="type"/> names a type parameter anywhere: in its type arguments, its element type, or
    /// those of its containing types (<c>Outer&lt;T&gt;.Inner</c>).
    /// </summary>
    public static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => AllTypeArguments(n).Any(ContainsTypeParameter),
        _ => false,
    };

    /// <summary>A type or formatter as C# source without <c>global::</c>, as published layouts show it.</summary>
    public static string Plain(string name) => name.Replace("global::", "");

    /// <summary>The contents of a C# string literal for <paramref name="s"/>.</summary>
    public static string EscapeString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>All type parameters of a type definition, outermost containing type first.</summary>
    public static List<ITypeParameterSymbol> AllTypeParameters(INamedTypeSymbol type)
    {
        var result = new List<ITypeParameterSymbol>();
        if (type.ContainingType != null)
            result.AddRange(AllTypeParameters(type.ContainingType));
        result.AddRange(type.TypeParameters);
        return result;
    }

    /// <summary>
    /// Name of the generated formatter for a type definition: namespace and containing types joined by '_'
    /// (literal underscores doubled, so names cannot collide), arity suffixes, then "Formatter".
    /// </summary>
    public static string FormatterName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = type; t != null; t = t.ContainingType)
            parts.Add(t.Name.Replace("_", "__") + (t.Arity > 0 ? "_" + t.Arity : ""));
        parts.Reverse();
        var ns = type.ContainingNamespace;
        var nsName = Namespace(ns);
        var sb = new StringBuilder();
        if (nsName.Length != 0)
        {
            sb.Append(nsName.Replace("_", "__").Replace('.', '_'));
            sb.Append('_');
        }

        sb.Append(string.Join("_", parts));
        sb.Append("Formatter");
        return sb.ToString();
    }

    /// <summary>
    /// Name hashed into the type id; must match <c>Nino.Core.NinoTypeId.GetName</c> at runtime.
    /// </summary>
    public static string RuntimeName(ITypeSymbol type)
    {
        var sb = new StringBuilder();
        AppendRuntimeName(sb, type);
        return sb.ToString();
    }

    private static void AppendRuntimeName(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                AppendRuntimeName(sb, array.ElementType);
                sb.Append('[');
                sb.Append(',', array.Rank - 1);
                sb.Append(']');
                return;
            case ITypeParameterSymbol parameter:
                sb.Append(parameter.Name);
                return;
            case INamedTypeSymbol named:
                if (named.IsTupleType && named.TupleUnderlyingType != null)
                    named = named.TupleUnderlyingType;
                AppendDefinitionName(sb, named);
                var args = AllTypeArguments(named);
                if (args.Count == 0)
                    return;
                sb.Append('<');
                for (int i = 0; i < args.Count; i++)
                {
                    if (i != 0)
                        sb.Append(',');
                    AppendRuntimeName(sb, args[i]);
                }

                sb.Append('>');
                return;
            default:
                sb.Append(type.Name);
                return;
        }
    }

    private static void AppendDefinitionName(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType != null)
        {
            AppendDefinitionName(sb, type.ContainingType);
            sb.Append('+');
        }
        else if (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            sb.Append(Namespace(ns));
            sb.Append('.');
        }

        sb.Append(type.MetadataName.Split('`')[0]);
    }

    /// <summary>The id hashed from a name; shares its implementation with <c>NinoTypeId.Compute</c>.</summary>
    public static int ComputeId(string name) => NinoHash.Compute(name);

    /// <summary>Constraint clauses for the given type parameters.</summary>
    public static string Constraints(IEnumerable<ITypeParameterSymbol> parameters)
    {
        var sb = new StringBuilder();
        foreach (var p in parameters)
        {
            var items = new List<string>();
            if (p.HasUnmanagedTypeConstraint)
                items.Add("unmanaged");
            else if (p.HasValueTypeConstraint)
                items.Add("struct");
            else if (p.HasReferenceTypeConstraint)
                items.Add("class");
            else if (p.HasNotNullConstraint)
                items.Add("notnull");
            items.AddRange(p.ConstraintTypes.Select(Global));
            if (p.HasConstructorConstraint && !p.HasValueTypeConstraint && !p.HasUnmanagedTypeConstraint)
                items.Add("new()");
            if (items.Count != 0)
                sb.Append(" where ").Append(p.Name).Append(" : ").Append(string.Join(", ", items));
        }

        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="formatter"/> with the generated formatters in <paramref name="names"/> replaced by their nested
    /// twin <paramref name="suffix"/> (<c>.__Top</c>, <c>.__Shallow</c>): the formatter itself, or the element
    /// formatters it is composed of (whole type arguments of built-in collection, nullable and tuple formatters;
    /// generated formatters take no formatter type arguments, and a generic one is never replaced).
    /// </summary>
    internal static string WithTwin(string formatter, HashSet<string> names, string suffix)
    {
        if (names.Count == 0)
            return formatter;
        if (names.Contains(formatter))
            return formatter + suffix;
        var result = new StringBuilder(formatter.Length);
        int i = 0;
        while (true)
        {
            int start = formatter.IndexOf(Gen, i, System.StringComparison.Ordinal);
            if (start < 0)
                break;
            int end = start + Gen.Length;
            while (end < formatter.Length && (char.IsLetterOrDigit(formatter[end]) || formatter[end] == '_'))
                end++;
            result.Append(formatter, i, end - i);
            // A whole type argument: followed by ',' or '>' (not a generic formatter or a nested name).
            if (end < formatter.Length && formatter[end] is ',' or '>' &&
                names.Contains(formatter.Substring(start, end - start)))
                result.Append(suffix);
            i = end;
        }

        return result.Append(formatter, i, formatter.Length - i).ToString();
    }

    /// <summary>
    /// Nino.Core formatters that read and write without a loop over elements in the generated body, and never pass
    /// the reader's or writer's address to a call that is not inlined: strings, raw arrays and lists, and
    /// dictionaries of raw keys and raw or UTF-16 string values.
    /// </summary>
    internal static bool IsFlat(string formatter)
    {
        if (formatter == Core + "StringFormatter" || formatter == Core + "Utf8StringFormatter" ||
            formatter.StartsWith(Core + "UnmanagedArrayFormatter<", System.StringComparison.Ordinal) ||
            formatter.StartsWith(Core + "UnmanagedListFormatter<", System.StringComparison.Ordinal))
            return true;
        if (!formatter.StartsWith(Core + "DictionaryFormatter<", System.StringComparison.Ordinal))
            return false;
        // DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>: the last two arguments.
        string args = formatter.Substring(Core.Length + "DictionaryFormatter<".Length);
        int value = args.LastIndexOf(", " + Core, System.StringComparison.Ordinal);
        if (value < 0)
            return false;
        int key = args.LastIndexOf(", " + Core, value - 1, System.StringComparison.Ordinal);
        if (key < 0)
            return false;
        string keyFormatter = args.Substring(key + 2, value - key - 2);
        string valueFormatter = args.Substring(value + 2, args.Length - value - 3);
        return keyFormatter.StartsWith(Core + "UnmanagedFormatter<", System.StringComparison.Ordinal) &&
               (valueFormatter == Core + "StringFormatter" ||
                valueFormatter.StartsWith(Core + "UnmanagedFormatter<", System.StringComparison.Ordinal));
    }
}
