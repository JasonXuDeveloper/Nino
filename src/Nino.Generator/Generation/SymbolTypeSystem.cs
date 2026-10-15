using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// <see cref="ITypeSymbol"/> for <see cref="NinoFormatterBinding"/>, the registration binding shared with the runtime.
/// Nullable annotations are ignored; tuples are their <c>ValueTuple</c>.
/// </summary>
internal readonly struct SymbolTypeSystem : INinoTypeSystem<ITypeSymbol>
{
    private readonly Compilation _compilation;

    public SymbolTypeSystem(Compilation compilation) => _compilation = compilation;

    public bool IsTypeParameter(ITypeSymbol type, out int ordinal)
    {
        if (type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } p)
        {
            ordinal = p.Ordinal;
            return true;
        }

        ordinal = -1;
        return false;
    }

    public bool IsArray(ITypeSymbol type, out int key, out ITypeSymbol element)
    {
        if (type is IArrayTypeSymbol a)
        {
            key = a.IsSZArray ? 1 : -a.Rank;
            element = a.ElementType;
            return true;
        }

        key = 0;
        element = null!;
        return false;
    }

    public bool IsConstructed(ITypeSymbol type, out ITypeSymbol definition, out IReadOnlyList<ITypeSymbol> arguments)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } named && !named.IsUnboundGenericType)
        {
            if (named.IsTupleType && named.TupleUnderlyingType is { } underlying)
                named = underlying;
            definition = named.OriginalDefinition;
            arguments = named.TypeArguments;
            return !SymbolEqualityComparer.Default.Equals(named, definition);
        }

        definition = null!;
        arguments = null!;
        return false;
    }

    public bool IsInterface(ITypeSymbol type) => type.TypeKind == TypeKind.Interface;

    public bool IsPointerLike(ITypeSymbol type) => type is IPointerTypeSymbol or IFunctionPointerTypeSymbol;

    public bool Same(ITypeSymbol a, ITypeSymbol b) => SymbolEqualityComparer.Default.Equals(a, b);

    public ITypeSymbol Bound(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

    public ITypeSymbol BaseType(ITypeSymbol type) => type.BaseType!;

    public IEnumerable<ITypeSymbol> Interfaces(ITypeSymbol type) => type.AllInterfaces;

    public ITypeSymbol MakeArray(ITypeSymbol element, int key) =>
        _compilation.CreateArrayTypeSymbol(element, key == 1 ? 1 : -key);

    public ITypeSymbol Construct(ITypeSymbol definition, ITypeSymbol[] arguments) =>
        ((INamedTypeSymbol)definition).Construct(arguments);
}
