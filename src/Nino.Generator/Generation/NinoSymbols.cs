using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// The Nino types the generator and analyzer compare against (mostly attributes), resolved once per compilation (through <see cref="TypeFacts"/>), so attributes are
/// compared by symbol: a same-named attribute from another namespace never matches. A type missing from the
/// referenced Nino.Core is null and matches nothing.
/// </summary>
internal sealed class NinoSymbols
{
    public readonly INamedTypeSymbol? NinoType, Member, Ignore, CustomFormatter, Formatter, Utf8, FormerName,
        ExplicitOrder, Constructor, RefDeserialization, Pooled, StructLayout, IFormatter, FormatterAttribute,
        Serializer, Deserializer;

    public NinoSymbols(Compilation compilation)
    {
        INamedTypeSymbol? Core(string name) => compilation.GetTypeByMetadataName(NinoNames.Core(name));
        NinoType = Core(NinoNames.NinoTypeAttribute);
        Member = Core("NinoMemberAttribute");
        Ignore = Core("NinoIgnoreAttribute");
        CustomFormatter = Core("NinoCustomFormatterAttribute");
        Formatter = Core("NinoFormatter`1");
        IFormatter = Core("INinoFormatter`1");
        FormatterAttribute = Core("NinoFormatterAttribute");
        Utf8 = Core("NinoUtf8Attribute");
        FormerName = Core("NinoFormerNameAttribute");
        ExplicitOrder = Core("NinoExplicitOrderAttribute");
        Constructor = Core("NinoConstructorAttribute");
        RefDeserialization = Core("NinoRefDeserializationAttribute");
        Pooled = Core("NinoPooledAttribute");
        Serializer = Core(NinoNames.Serializer);
        Deserializer = Core(NinoNames.Deserializer);
        StructLayout = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.StructLayoutAttribute");
    }

    /// <summary>
    /// Whether <paramref name="formatter"/> can be the <c>[NinoCustomFormatter]</c> of a <paramref name="memberType"/>
    /// member: a struct implementing <c>INinoFormatter&lt;T&gt;</c> (called directly, like built-in formatters), or a
    /// concrete class deriving from <c>NinoFormatter&lt;T&gt;</c> with a public parameterless constructor (one shared
    /// instance). <c>T</c> is exactly the member type. The analyzer reports NINO008 for anything else and the
    /// generator skips the member.
    /// </summary>
    public bool IsCustomFormatterOf(INamedTypeSymbol formatter, ITypeSymbol memberType)
    {
        if (formatter.IsUnboundGenericType || formatter.TypeKind == TypeKind.Error)
            return false;
        if (formatter.IsValueType)
        {
            if (IFormatter == null || formatter.IsRefLikeType)
                return false;
            foreach (var i in formatter.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, IFormatter) &&
                    SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], memberType))
                    return true;
            }

            return false;
        }

        if (Formatter == null || formatter.IsAbstract || formatter.IsStatic)
            return false;
        bool constructible = false;
        foreach (var ctor in formatter.InstanceConstructors)
        {
            if (ctor.Parameters.Length == 0 && ctor.DeclaredAccessibility == Accessibility.Public)
                constructible = true;
        }

        if (!constructible)
            return false;
        for (var baseType = formatter.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, Formatter))
                return SymbolEqualityComparer.Default.Equals(baseType.TypeArguments[0], memberType);
        }

        return false;
    }

    /// <summary>The <paramref name="attribute"/> applied to <paramref name="symbol"/>, or null.</summary>
    public static AttributeData? Get(ISymbol symbol, INamedTypeSymbol? attribute)
    {
        if (attribute == null)
            return null;
        foreach (var data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return data;
        }

        return null;
    }

    public static bool Has(ISymbol symbol, INamedTypeSymbol? attribute) => Get(symbol, attribute) != null;
}
