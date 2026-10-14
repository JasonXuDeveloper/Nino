using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

internal static class TypeNames
{
    private static readonly SymbolDisplayFormat GlobalFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                                  SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Fully qualified C# name, e.g. <c>global::Ns.Outer.Box&lt;int&gt;</c>.</summary>
    // Display strings are requested many times per type (member types, formatter names, registrations); symbols
    // are immutable, so cache by instance. Weak keys never keep an old compilation alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ITypeSymbol, string> GlobalNames = new();

    public static string Global(ITypeSymbol type)
    {
        return GlobalNames.GetValue(type, static t =>
            t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(GlobalFormat));
    }

    /// <summary>All type arguments of a named type, outermost containing type first.</summary>
    public static List<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
    {
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
        var nsName = ns == null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();
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
            sb.Append(ns.ToDisplayString());
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
