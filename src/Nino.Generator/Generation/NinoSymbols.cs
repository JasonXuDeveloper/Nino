using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// The attribute types Nino reads, resolved once per compilation (through <see cref="TypeFacts"/>), so attributes are
/// compared by symbol: a same-named attribute from another namespace never matches. A type missing from the
/// referenced Nino.Core is null and matches nothing.
/// </summary>
internal sealed class NinoSymbols
{
    public readonly INamedTypeSymbol? NinoType, Member, Ignore, CustomFormatter, Formatter, Utf8, FormerName,
        ExplicitOrder, Constructor, RefDeserialization, Pooled, StructLayout;

    public NinoSymbols(Compilation compilation)
    {
        INamedTypeSymbol? Core(string name) => compilation.GetTypeByMetadataName("Nino.Core." + name);
        NinoType = Core("NinoTypeAttribute");
        Member = Core("NinoMemberAttribute");
        Ignore = Core("NinoIgnoreAttribute");
        CustomFormatter = Core("NinoCustomFormatterAttribute");
        Formatter = Core("NinoFormatter`1");
        Utf8 = Core("NinoUtf8Attribute");
        FormerName = Core("NinoFormerNameAttribute");
        ExplicitOrder = Core("NinoExplicitOrderAttribute");
        Constructor = Core("NinoConstructorAttribute");
        RefDeserialization = Core("NinoRefDeserializationAttribute");
        Pooled = Core("NinoPooledAttribute");
        StructLayout = compilation.GetTypeByMetadataName("System.Runtime.InteropServices.StructLayoutAttribute");
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
