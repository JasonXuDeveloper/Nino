using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// The Nino types the generator and analyzer compare against (mostly attributes), resolved once per compilation (through <see cref="TypeFacts"/>), so attributes are
/// compared by symbol: a same-named attribute from another namespace never matches. A type missing from the
/// referenced Nino.Core is null and matches nothing.
/// </summary>
internal sealed class NinoSymbols
{
    public readonly INamedTypeSymbol? Member, Ignore, CustomFormatter, Utf8, FormerName,
        Constructor, Factory, Created, Raw, Pooled, IFormatter, FormatterAttribute,
        Serializer, Deserializer, Writer, Reader, Direct, Utf8Formatter, Layouts;

    /// <summary>
    /// The serialization callback attributes, indexed by <see cref="CallbackKind"/> (an entry is null when the
    /// referenced Nino.Core has no such attribute).
    /// </summary>
    public readonly INamedTypeSymbol?[] Callbacks;

    /// <summary>
    /// Whether the referenced Nino.Core's <c>NinoTypeLayoutAttribute</c> has a settable <c>bool Deferrable</c>, which
    /// the published layouts set (<c>LayoutEmitter</c>); older versions have none.
    /// </summary>
    public readonly bool LayoutDeferrable;

    public NinoSymbols(Compilation compilation)
    {
        INamedTypeSymbol? Core(string name) => compilation.GetTypeByMetadataName(NinoNames.Core(name));
        Member = Core(NinoNames.NinoMember + "Attribute");
        Ignore = Core(NinoNames.NinoIgnore + "Attribute");
        CustomFormatter = Core("NinoMemberFormatterAttribute");
        IFormatter = Core("INinoFormatter`1");
        FormatterAttribute = Core("NinoFormatterAttribute");
        Utf8 = Core("NinoUtf8Attribute");
        Utf8Formatter = Core("Utf8StringFormatter");
        FormerName = Core("NinoFormerNameAttribute");
        Constructor = Core("NinoConstructorAttribute");
        Factory = Core("NinoFactoryAttribute");
        Created = Core("NinoCreatedAttribute");
        Raw = Core("NinoRawAttribute");
        Pooled = Core("NinoPooledAttribute");
        Serializer = Core(NinoNames.Serializer);
        Deserializer = Core(NinoNames.Deserializer);
        Writer = Core("Writer");
        Reader = Core("Reader");
        Direct = Core("CompilerServices.NinoDirect");
        Layouts = Core(NinoNames.NinoLayoutsAttribute);
        Callbacks = Generation.Callbacks.AttributeNames.Select(name => Core(name + "Attribute")).ToArray();
        LayoutDeferrable = Core("NinoTypeLayoutAttribute") is { } layout && layout.GetMembers("Deferrable")
            .OfType<IPropertySymbol>().Any(static p => p.Type.SpecialType == SpecialType.System_Boolean &&
                                                       p.SetMethod is { DeclaredAccessibility: Accessibility.Public });
    }

    /// <summary>Whether <paramref name="type"/> is <c>INinoFormatter&lt;T&gt;</c> or implements it.</summary>
    public bool IsFormatter(ITypeSymbol type)
    {
        if (IFormatter == null)
            return false;
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, IFormatter))
            return true;
        return ImplementsFormatter(type, null);
    }

    /// <summary>
    /// Whether <paramref name="formatter"/> can be the <c>[NinoMemberFormatter]</c> of a <paramref name="memberType"/>
    /// member: a struct implementing <c>INinoFormatter&lt;T&gt;</c> where <c>T</c> is exactly the member type, called
    /// directly like the built-in formatters. The analyzer reports NINO008 for anything else and the generator skips
    /// the member.
    /// </summary>
    public bool IsCustomFormatterOf(INamedTypeSymbol formatter, ITypeSymbol memberType)
    {
        if (IFormatter == null || !formatter.IsValueType || formatter.IsRefLikeType ||
            formatter.IsUnboundGenericType || formatter.TypeKind == TypeKind.Error)
            return false;
        return ImplementsFormatter(formatter, memberType);
    }

    private bool ImplementsFormatter(ITypeSymbol type, ITypeSymbol? memberType)
    {
        foreach (var i in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, IFormatter) &&
                (memberType == null || SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], memberType)))
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

    /// <summary>
    /// The type a top-level <c>NinoSerializer</c> / <c>NinoDeserializer</c> <paramref name="call"/> to
    /// <paramref name="method"/> needs a formatter for: its type argument, or the type of its <c>typeof</c> argument;
    /// null when it is not such a call or names neither. The generator registers and intercepts it, the analyzer
    /// checks it (NINO029).
    /// </summary>
    public ITypeSymbol? CallType(IMethodSymbol method, InvocationExpressionSyntax call, SemanticModel model,
        CancellationToken ct)
    {
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, Serializer) &&
            !SymbolEqualityComparer.Default.Equals(method.ContainingType, Deserializer))
            return null;
        return method.IsGenericMethod
            ? method.TypeArguments[0]
            : TypeOfArgument(call.ArgumentList) is { } typeOf
                ? model.GetTypeInfo(typeOf.Type, ct).Type
                : null;
    }

    /// <summary>The first <c>typeof</c> argument of a call, or null.</summary>
    public static TypeOfExpressionSyntax? TypeOfArgument(ArgumentListSyntax arguments)
    {
        foreach (var argument in arguments.Arguments)
        {
            if (argument.Expression is TypeOfExpressionSyntax typeOf)
                return typeOf;
        }

        return null;
    }

    public static bool Has(ISymbol symbol, INamedTypeSymbol? attribute) => Get(symbol, attribute) != null;

    /// <summary>
    /// The <c>[NinoMemberFormatter]</c> of <paramref name="member"/>, or null. One of <c>Utf8StringFormatter</c> on a
    /// string member is <c>[NinoUtf8]</c> (<see cref="WritesUtf8"/>), not a formatter of the user's: the same bytes,
    /// the same layout (<c>utf8</c>) and the same code.
    /// </summary>
    public AttributeData? CustomFormatterOf(ISymbol member) =>
        Get(member, CustomFormatter) is { } attribute && !IsUtf8Formatter(member, attribute) ? attribute : null;

    /// <summary>Whether <paramref name="member"/> has a <c>[NinoMemberFormatter]</c> (<see cref="CustomFormatterOf"/>).</summary>
    public bool HasCustomFormatter(ISymbol member) => CustomFormatterOf(member) != null;

    /// <summary>
    /// Whether the strings of <paramref name="member"/> are written as UTF-8: <c>[NinoUtf8]</c>, or a
    /// <c>[NinoMemberFormatter(typeof(Utf8StringFormatter))]</c> on a string member.
    /// </summary>
    public bool WritesUtf8(ISymbol member) =>
        Has(member, Utf8) || Get(member, CustomFormatter) is { } attribute && IsUtf8Formatter(member, attribute);

    /// <summary>A <c>[NinoMemberFormatter]</c> of exactly <c>Utf8StringFormatter</c> on a string member.</summary>
    public bool IsUtf8Formatter(ISymbol member, AttributeData attribute) =>
        Utf8Formatter != null && attribute.ConstructorArguments is { Length: > 0 } args &&
        SymbolEqualityComparer.Default.Equals(args[0].Value as INamedTypeSymbol, Utf8Formatter) &&
        MemberCollector.MemberType(member) is { SpecialType: SpecialType.System_String };

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
    /// The member names a <c>[NinoConstructor]</c> lists (a missing name reads as ""), or null when it lists none:
    /// its parameter names are then the member names.
    /// </summary>
    public static List<string>? ConstructorNames(AttributeData constructor) =>
        constructor.ConstructorArguments is { Length: > 0 } a && a[0].Kind == TypedConstantKind.Array &&
        a[0].Values.Length > 0
            ? a[0].Values.Select(v => v.Value as string ?? "").ToList()
            : null;

    /// <summary>Whether <paramref name="member"/> overrides <paramref name="target"/> at any depth.</summary>
    public static bool Overrides(ISymbol member, ISymbol target)
    {
        for (var property = (member as IPropertySymbol)?.OverriddenProperty; property != null;
             property = property.OverriddenProperty)
        {
            if (SymbolEqualityComparer.Default.Equals(property, target))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="argument"/> meets the constraints C# itself checks on <paramref name="parameter"/>:
    /// <c>class</c>, <c>struct</c> (not <c>Nullable&lt;T&gt;</c>), <c>unmanaged</c> and <c>new()</c> (a struct, or a
    /// concrete class with a public parameterless constructor). Constraint types are the caller's: how they are
    /// matched differs (a closed argument, or one unified with a formatter's parameters).
    /// </summary>
    public static bool MeetsCSharpConstraints(ITypeParameterSymbol parameter, ITypeSymbol argument) =>
        !(parameter.HasReferenceTypeConstraint && !argument.IsReferenceType ||
          parameter.HasValueTypeConstraint &&
          (!argument.IsValueType || argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) ||
          parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType ||
          parameter.HasConstructorConstraint && !HasPublicDefaultConstructor(argument));

    private static bool HasPublicDefaultConstructor(ITypeSymbol type) => type switch
    {
        { IsValueType: true } => true,
        INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named => named.InstanceConstructors.Any(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public),
        _ => false,
    };

    /// <summary>
    /// Whether generated code can call <paramref name="method"/> as <c>M()</c>: every parameter is optional (or
    /// <c>params</c>), as in ET's <c>static T Create(bool isFromPool = false)</c> or <c>MultiMap(int maxPoolCount = 0)</c>.
    /// </summary>
    public static bool CallableWithoutArguments(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (!parameter.IsOptional && !parameter.IsParams)
                return false;
        }

        return true;
    }

    /// <summary>
    /// What is wrong with <paramref name="method"/> as a <c>[NinoFactory]</c> (NINO017), empty if nothing: generated
    /// code calls it as <c>T.Factory()</c>, so it must be a public static non-generic method of the type, callable
    /// without arguments and returning the type.
    /// </summary>
    public static List<string> FactoryErrors(IMethodSymbol method)
    {
        var errors = new List<string>();
        if (method.DeclaredAccessibility != Accessibility.Public)
            errors.Add("must be public");
        if (!method.IsStatic)
            errors.Add("must be static");
        if (method.IsGenericMethod)
            errors.Add("must not be generic");
        if (!CallableWithoutArguments(method))
            errors.Add("must be callable without arguments (parameterless, or every parameter optional)");
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, method.ContainingType))
            errors.Add($"must return {method.ContainingType.ToDisplayString()}");
        return errors;
    }

    /// <summary>The valid <c>[NinoFactory]</c> of <paramref name="type"/> (see <see cref="FactoryErrors"/>), or null.</summary>
    public IMethodSymbol? FindFactory(INamedTypeSymbol type)
    {
        if (Factory == null)
            return null;
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method && Has(method, Factory) &&
                FactoryErrors(method).Count == 0)
                return method;
        }

        return null;
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
        a.AttributeClass is { } type &&
        TypeNames.HasMetadataName(type, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"));

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
