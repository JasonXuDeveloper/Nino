using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// The Nino types the generator and analyzer compare against (mostly attributes), resolved once per compilation (through <see cref="TypeFacts"/>), so attributes are
/// compared by symbol: a same-named attribute from another namespace never matches. A type missing from the
/// referenced Nino.Core is null and matches nothing.
/// </summary>
internal sealed class NinoSymbols
{
    public readonly INamedTypeSymbol? NinoType, Member, Ignore, CustomFormatter, Utf8, FormerName,
        ExplicitOrder, Constructor, RefDeserialization, Created, Raw, Pooled, StructLayout, IFormatter, FormatterAttribute,
        Serializer, Deserializer;

    public NinoSymbols(Compilation compilation)
    {
        INamedTypeSymbol? Core(string name) => compilation.GetTypeByMetadataName(NinoNames.Core(name));
        NinoType = Core(NinoNames.NinoTypeAttribute);
        Member = Core("NinoMemberAttribute");
        Ignore = Core("NinoIgnoreAttribute");
        CustomFormatter = Core("NinoCustomFormatterAttribute");
        IFormatter = Core("INinoFormatter`1");
        FormatterAttribute = Core("NinoFormatterAttribute");
        Utf8 = Core("NinoUtf8Attribute");
        FormerName = Core("NinoFormerNameAttribute");
        ExplicitOrder = Core("NinoExplicitOrderAttribute");
        Constructor = Core("NinoConstructorAttribute");
        RefDeserialization = Core("NinoRefDeserializationAttribute");
        Created = Core("NinoCreatedAttribute");
        Raw = Core("NinoRawAttribute");
        Pooled = Core("NinoPooledAttribute");
        Serializer = Core(NinoNames.Serializer);
        Deserializer = Core(NinoNames.Deserializer);
        StructLayout = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.StructLayoutAttribute");
    }

    /// <summary>
    /// Whether <paramref name="formatter"/> can be the <c>[NinoCustomFormatter]</c> of a <paramref name="memberType"/>
    /// member: a struct implementing <c>INinoFormatter&lt;T&gt;</c> where <c>T</c> is exactly the member type, called
    /// directly like the built-in formatters. The analyzer reports NINO008 for anything else and the generator skips
    /// the member.
    /// </summary>
    public bool IsCustomFormatterOf(INamedTypeSymbol formatter, ITypeSymbol memberType)
    {
        if (IFormatter == null || !formatter.IsValueType || formatter.IsRefLikeType ||
            formatter.IsUnboundGenericType || formatter.TypeKind == TypeKind.Error)
            return false;
        foreach (var i in formatter.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, IFormatter) &&
                SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], memberType))
                return true;
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
