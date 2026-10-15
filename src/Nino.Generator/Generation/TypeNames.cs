using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

internal static class TypeNames
{
    // Built by hand: SymbolDisplay is by far the most expensive part of a model build (it pools and trims display
    // part arrays per call), and every transform re-runs on each edit.

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

    /// <summary>Metadata name, e.g. <c>System.Collections.Generic.List`1</c>; nested types joined by <c>+</c>.</summary>
    public static string MetadataName(INamedTypeSymbol definition)
    {
        var ns = definition.ContainingNamespace;
        var nsName = Namespace(ns);
        var prefix = nsName.Length == 0 ? "" : nsName + ".";
        return definition.ContainingType != null
            ? MetadataName(definition.ContainingType) + "+" + definition.MetadataName
            : prefix + definition.MetadataName;
    }

    private static string Escape(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;

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
                sb.Append(Escape(parameter.Name));
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
            var ns = Namespace(named.ContainingNamespace);
            if (ns.Length != 0)
            {
                foreach (var part in ns.Split('.'))
                {
                    sb.Append(Escape(part));
                    sb.Append('.');
                }
            }
        }

        sb.Append(Escape(named.Name));
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
        var result = new List<ITypeSymbol>();
        if (type.ContainingType != null)
            result.AddRange(AllTypeArguments(type.ContainingType));
        result.AddRange(type.TypeArguments);
        return result;
    }

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

    /// <summary>FNV-1a over UTF-16 code units (low byte, then high byte); must match <c>NinoTypeId.Compute</c>.</summary>
    public static int ComputeId(string name)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash ^= (byte)c;
                hash *= 16777619;
                hash ^= (byte)(c >> 8);
                hash *= 16777619;
            }

            return hash == 0 ? 1 : (int)hash;
        }
    }

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
}
