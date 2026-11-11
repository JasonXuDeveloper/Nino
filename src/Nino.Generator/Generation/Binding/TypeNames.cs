using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>Names of types as generated code and the runtime spell them, and the ids hashed from them.</summary>
internal static class TypeNames
{
    // Built by hand: SymbolDisplay is by far the most expensive part of a model build (it pools and trims display
    // part arrays per call), and every transform re-runs on each edit.

    /// <summary>Prefix of every Nino.Core type in generated code.</summary>
    public const string Core = "global::Nino.Core.";

    /// <summary>
    /// Prefix of the Nino.Core types only generated code uses (<c>Nino.Core.CompilerServices</c>: <c>NinoObject</c>,
    /// <c>NinoUnsafe</c>, the body formatter interfaces...).
    /// </summary>
    public const string CoreCompilerServices = Core + "CompilerServices.";

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

    /// <summary>The no-inlining attribute, spelled out (see <see cref="InlineAttribute"/>).</summary>
    public const string NoInlineAttribute =
        "[" + CompilerServices + "MethodImpl(" + CompilerServices + "MethodImplOptions.NoInlining)]";

    /// <summary>Fully qualified C# name, e.g. <c>global::Ns.Outer.Box&lt;int&gt;</c>.</summary>
    public static string Global(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol parameter)
            return Identifier(parameter.Name);
        if (type is INamedTypeSymbol { TypeKind: not TypeKind.Error } named && Keyword(named.SpecialType) is { } keyword)
            return keyword;

        var sb = TextScratch.Rent(64);
        try
        {
            AppendGlobal(sb, type);
            return sb.ToString();
        }
        finally
        {
            TextScratch.Return(sb);
        }
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
        var sb = TextScratch.Rent(64);
        try
        {
            sb.Append("global::");
            AppendNamespace(sb, type.ContainingNamespace);
            AppendOpenName(sb, type);
            return sb.ToString();
        }
        finally
        {
            TextScratch.Return(sb);
        }
    }

    private static void AppendOpenName(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containing)
        {
            AppendOpenName(sb, containing);
            sb.Append('.');
        }

        sb.Append(Identifier(type.Name));
        if (type.Arity > 0)
            sb.Append('<').Append(',', type.Arity - 1).Append('>');
    }

    /// <summary>The identifier as C# source: keywords are escaped with <c>@</c>.</summary>
    public static string Identifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;

    /// <summary>Appends <c>Ns.Sub.</c> (escaped, with a trailing dot); nothing for the global namespace.</summary>
    private static void AppendNamespace(StringBuilder sb, INamespaceSymbol? ns, bool escape = true)
    {
        if (ns == null || ns.IsGlobalNamespace)
            return;
        AppendNamespace(sb, ns.ContainingNamespace, escape);
        sb.Append(escape ? Identifier(ns.Name) : ns.Name).Append('.');
    }

    internal static void AppendGlobal(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
            {
                // C# writes rank specifiers outermost first: int[][,] is an array of int[,].
                ITypeSymbol element = array;
                while (element is IArrayTypeSymbol a)
                    element = a.ElementType;

                AppendGlobal(sb, element);
                for (ITypeSymbol current = array; current is IArrayTypeSymbol ranked; current = ranked.ElementType)
                {
                    sb.Append('[');
                    sb.Append(',', ranked.Rank - 1);
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
            return type.Arity == 0 ? Array.Empty<ITypeSymbol>() : type.TypeArguments;
        var result = new List<ITypeSymbol>();
        AppendTypeArguments(type, result);
        return result;
    }

    private static void AppendTypeArguments(INamedTypeSymbol type, List<ITypeSymbol> result)
    {
        if (type.ContainingType is { } containing)
            AppendTypeArguments(containing, result);
        if (type.Arity == 0)
            return;
        foreach (var argument in type.TypeArguments)
            result.Add(argument);
    }

    /// <summary>
    /// Whether <paramref name="type"/> names a type parameter anywhere: in its type arguments, its element type, or
    /// those of its containing types (<c>Outer&lt;T&gt;.Inner</c>).
    /// </summary>
    public static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
            return true;
        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);
        for (var named = type as INamedTypeSymbol; named != null; named = named.ContainingType)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (ContainsTypeParameter(argument))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="type"/> is or contains the type parameter <paramref name="p"/>.</summary>
    public static bool Mentions(ITypeSymbol type, ITypeParameterSymbol p)
    {
        while (type is IArrayTypeSymbol array)
            type = array.ElementType;
        if (type is ITypeParameterSymbol parameter)
            return SymbolEqualityComparer.Default.Equals(parameter, p);

        for (var named = type as INamedTypeSymbol; named != null; named = named.ContainingType)
        {
            if (named.Arity == 0)
                continue;
            foreach (var argument in named.TypeArguments)
                if (Mentions(argument, p))
                    return true;
        }

        return false;
    }

    private const string GlobalQualifier = "global::";

    /// <summary>A type or formatter as C# source without <c>global::</c>, as published layouts show it.</summary>
    public static string Plain(string name) => name.Replace(GlobalQualifier, "");

    /// <summary>Appends the plain type name without creating a replacement string.</summary>
    public static void AppendPlain(StringBuilder builder, string name)
    {
        int start = 0;
        int at;
        while ((at = name.IndexOf(GlobalQualifier, start, StringComparison.Ordinal)) >= 0)
        {
            builder.Append(name, start, at - start);
            start = at + GlobalQualifier.Length;
        }
        builder.Append(name, start, name.Length - start);
    }

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
    /// The formatter type parameter a generated formatter of a generic type declares for its type parameter
    /// <paramref name="name"/>: the formatter of <c>T</c>, which closed instances name (<c>Box&lt;int&gt;</c>'s
    /// formatter takes <c>UnmanagedFormatter&lt;int&gt;</c>), so a member of type <c>T</c> is a direct call on every
    /// runtime instead of a virtual call through <c>CachedFormatter&lt;T&gt;</c> (IL2CPP, Mono and NativeAOT do not
    /// devirtualize that one).
    /// </summary>
    public static string FormatterParameter(string name) => "__F_" + name;

    /// <summary>
    /// Whether <paramref name="p"/> has a <see cref="FormatterParameter"/>: a parameter of a type (not of a method),
    /// which every generated formatter it is in scope in declares.
    /// </summary>
    public static bool HasFormatterParameter(ITypeParameterSymbol p) => p.TypeParameterKind == TypeParameterKind.Type;

    /// <summary>
    /// Name of the generated formatter for a type definition: namespace and containing types joined by '_'
    /// (literal underscores doubled, so names cannot collide), arity suffixes, then "Formatter".
    /// </summary>
    public static string FormatterName(INamedTypeSymbol type)
    {
        var sb = TextScratch.Rent(64);
        try
        {
            AppendFormatterStem(sb, type);
            return sb.Append("Formatter").ToString();
        }
        finally
        {
            TextScratch.Return(sb);
        }
    }

    /// <summary>
    /// Name of the nesting formatter generated for <paramref name="type"/> (a closed class that holds itself):
    /// its definition's <see cref="FormatterName"/> stem, the hash <paramref name="id"/> of the closed type (and its
    /// encoding), then "NestingFormatter". A user type's name has its underscores doubled, so none ends this way.
    /// </summary>
    public static string NestingFormatterName(INamedTypeSymbol type, int id) =>
        Stem(type.OriginalDefinition).Append('_').Append(id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
            .Append(NestingFormatterSuffix).ToString();

    internal const string NestingFormatterSuffix = "NestingFormatter";

    private static StringBuilder Stem(INamedTypeSymbol type)
    {
        var sb = new StringBuilder();
        AppendFormatterStem(sb, type);
        return sb;
    }

    private static void AppendFormatterStem(StringBuilder sb, INamedTypeSymbol type)
    {
        AppendNamespaceStem(sb, type.ContainingNamespace);
        AppendStem(sb, type);
    }

    private static void AppendStem(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containing)
        {
            AppendStem(sb, containing);
            sb.Append('_');
        }

        AppendStemPart(sb, type.Name);
        if (type.Arity > 0)
            sb.Append('_').Append(type.Arity);
    }

    private static void AppendNamespaceStem(StringBuilder sb, INamespaceSymbol? ns)
    {
        if (ns == null || ns.IsGlobalNamespace)
            return;
        AppendNamespaceStem(sb, ns.ContainingNamespace);
        AppendStemPart(sb, ns.Name);
        sb.Append('_');
    }

    private static void AppendStemPart(StringBuilder sb, string name)
    {
        int start = 0;
        for (int at = name.IndexOf('_'); at >= 0; at = name.IndexOf('_', start))
        {
            sb.Append(name, start, at - start).Append("__");
            start = at + 1;
        }

        sb.Append(name, start, name.Length - start);
    }

    /// <summary>
    /// Name hashed into the type id; must match <c>Nino.Core.NinoTypeId.GetRuntimeName</c> at runtime.
    /// </summary>
    public static string RuntimeName(ITypeSymbol type)
    {
        var sb = TextScratch.Rent(64);
        try
        {
            AppendRuntimeName(sb, type);
            return sb.ToString();
        }
        finally
        {
            TextScratch.Return(sb);
        }
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
                if (named.IsGenericType && AppendRuntimeArguments(sb, named))
                    sb.Append('>');
                return;
            default:
                sb.Append(type.Name);
                return;
        }
    }

    // The runtime puts every containing type's arguments in one list, outermost first.
    private static bool AppendRuntimeArguments(StringBuilder sb, INamedTypeSymbol type)
    {
        bool written = type.ContainingType is { } containing && AppendRuntimeArguments(sb, containing);
        foreach (var argument in type.TypeArguments)
        {
            sb.Append(written ? ',' : '<');
            AppendRuntimeName(sb, argument);
            written = true;
        }

        return written;
    }

    private static void AppendDefinitionName(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType != null)
        {
            AppendDefinitionName(sb, type.ContainingType);
            sb.Append('+');
        }
        else if (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
            AppendNamespace(sb, ns, escape: false);

        string name = type.MetadataName;
        int arity = name.IndexOf('`');
        if (arity < 0)
            sb.Append(name);
        else
            sb.Append(name, 0, arity);
    }

    /// <summary>The id hashed from a name; shares its implementation with <c>NinoTypeId.Compute</c>.</summary>
    public static int ComputeId(string name) => NinoHash.Compute(name);

    /// <summary>Constraint clauses for the given type parameters.</summary>
    public static string Constraints(IEnumerable<ITypeParameterSymbol> parameters)
    {
        StringBuilder? sb = null;
        foreach (var p in parameters)
        {
            string? primary = p.HasUnmanagedTypeConstraint ? "unmanaged"
                : p.HasValueTypeConstraint ? "struct"
                : p.HasReferenceTypeConstraint ? "class"
                : p.HasNotNullConstraint ? "notnull" : null;
            var types = p.ConstraintTypes;
            bool constructor = p.HasConstructorConstraint && !p.HasValueTypeConstraint && !p.HasUnmanagedTypeConstraint;
            if (primary == null && types.Length == 0 && !constructor)
                continue;

            sb ??= new StringBuilder();
            sb.Append(" where ").Append(p.Name).Append(" : ").Append(primary);
            bool needsSeparator = primary != null;
            foreach (var type in types)
            {
                if (needsSeparator)
                    sb.Append(", ");
                AppendGlobal(sb, type);
                needsSeparator = true;
            }

            if (constructor)
                sb.Append(needsSeparator ? ", new()" : "new()");
        }

        return sb == null ? "" : sb.ToString();
    }

    /// <summary>The name of a formatter's <c>__Top</c> twin, a struct nested in it (<c>TypeModel.HasTop</c>).</summary>
    public const string TopTwinName = "__Top";

    /// <summary>The name of a formatter's <c>__Shallow</c> twin, a struct nested in it (<c>DepthModel.Shallow</c>).</summary>
    public const string ShallowTwinName = "__Shallow";

    /// <summary>The suffix that names a formatter's <see cref="TopTwinName"/>.</summary>
    public const string Top = "." + TopTwinName;

    /// <summary>The suffix that names a formatter's <see cref="ShallowTwinName"/>.</summary>
    public const string Shallow = "." + ShallowTwinName;
}
