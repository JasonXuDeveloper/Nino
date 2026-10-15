using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// The Nino types the generator and analyzer compare against (mostly attributes), resolved once per compilation (through <see cref="TypeFacts"/>), so attributes are
/// compared by symbol: a same-named attribute from another namespace never matches. A type missing from the
/// referenced Nino.Core is null and matches nothing.
/// </summary>
internal sealed class NinoSymbols
{
    public readonly INamedTypeSymbol? NinoType, Member, Ignore, CustomFormatter, Utf8, FormerName,
        ExplicitOrder, Constructor, Factory, Created, Raw, Pooled, StructLayout, IFormatter, FormatterAttribute,
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
        Factory = Core("NinoFactoryAttribute");
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

    /// <summary>The string arguments of every <paramref name="attribute"/> applied to <paramref name="symbol"/>.</summary>
    public static List<string> StringArguments(ISymbol symbol, INamedTypeSymbol? attribute)
    {
        var values = new List<string>();
        if (attribute == null)
            return values;
        foreach (var data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute) &&
                data.ConstructorArguments.FirstOrDefault().Value is string value)
                values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// The C# 11 <c>required</c> members the type and its bases declare, which <c>new T()</c> must set in its object
    /// initializer unless the constructor is <c>[SetsRequiredMembers]</c>. <c>IsRequired</c> is read by reflection:
    /// Roslyn 4.3, which the generator is built against, predates it (and the language feature).
    /// </summary>
    public static List<ISymbol> RequiredMembers(INamedTypeSymbol type)
    {
        var result = new List<ISymbol>();
        if (PropertyIsRequired == null && FieldIsRequired == null)
            return result;
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (IsRequired(member))
                    result.Add(member);
            }
        }

        return result;
    }

    /// <inheritdoc cref="RequiredMembers"/>
    public static bool HasRequiredMembers(INamedTypeSymbol type) => RequiredMembers(type).Count != 0;

    /// <summary>Whether the constructor is <c>[SetsRequiredMembers]</c>: <c>new T()</c> needs no initializer.</summary>
    public static bool SetsRequiredMembers(IMethodSymbol constructor) => constructor.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

    private static bool IsRequired(ISymbol member)
    {
        var isRequired = member switch
        {
            IPropertySymbol => PropertyIsRequired,
            IFieldSymbol => FieldIsRequired,
            _ => null,
        };
        return isRequired != null && !member.IsStatic && isRequired.GetValue(member) is true;
    }

    // Interface properties of the Roslyn that runs the generator (4.4+); null on 4.3, which has no required members.
    private static readonly PropertyInfo? PropertyIsRequired = typeof(IPropertySymbol).GetProperty("IsRequired");

    private static readonly PropertyInfo? FieldIsRequired = typeof(IFieldSymbol).GetProperty("IsRequired");
}
