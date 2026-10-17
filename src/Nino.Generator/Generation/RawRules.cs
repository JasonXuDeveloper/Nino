using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>Which values are written as their raw bytes, memoized for one compilation.</summary>
internal sealed class RawRules
{
    private readonly ConcurrentDictionary<ISymbol, bool> _rawStructs = new(SymbolEqualityComparer.Default);
    private readonly Func<ISymbol, bool> _computeRawStruct;
    private readonly NinoSymbols _symbols;
    private readonly MemberCache _members;
    private readonly IAssemblySymbol _assembly;
    private readonly bool _weakTolerance;

    /// <param name="weakTolerance">The assembly defines WEAK_VERSION_TOLERANCE (its own structs are never raw).</param>
    public RawRules(NinoSymbols symbols, MemberCache members, IAssemblySymbol assembly, bool weakTolerance)
    {
        _symbols = symbols;
        _members = members;
        _assembly = assembly;
        _weakTolerance = weakTolerance;
        _computeRawStruct = s => ComputeIsRawStruct((INamedTypeSymbol)s);
    }

    /// <summary>Whether values of <paramref name="type"/> are written as their raw bytes.</summary>
    public bool IsRawUnmanaged(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType)
            return false;
        if (type.IsNinoType())
            return type is INamedTypeSymbol ninoStruct && IsRawStruct(ninoStruct);
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr || type is IPointerTypeSymbol)
            return false; // pointer-sized: written as 8 bytes
        if (type is INamedTypeSymbol named)
        {
            if (named.IsTupleType)
                return false;
            var definition = named.OriginalDefinition;
            if (definition.SpecialType == SpecialType.System_Nullable_T)
                return false;
            if (TypeNames.HasMetadataName(definition, "System.Collections.Generic.KeyValuePair`2") ||
                TypeNames.IsValueTuple(definition) || TypeNames.HasMetadataName(definition, "System.DateTimeOffset"))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a <c>[NinoType]</c> struct is written as its raw memory: unmanaged, not version tolerant, and every
    /// instance field is serialized (nested <c>[NinoType]</c> structs must qualify too). Such structs are copied as
    /// one block, and arrays/lists of them with a single memory copy.
    /// </summary>
    public bool IsRawStruct(INamedTypeSymbol type)
    {
        if (!type.IsValueType || type.TypeKind != TypeKind.Struct)
            return false;
        return _rawStructs.GetOrAdd(type, _computeRawStruct);
    }

    private bool ComputeIsRawStruct(INamedTypeSymbol type)
    {
        if (!type.IsUnmanagedType)
            return false;
        // WEAK_VERSION_TOLERANCE only applies to the types of the assembly that defines it.
        if (_weakTolerance && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _assembly))
            return false;
        var attr = type.GetNinoTypeOptions();
        if (attr == null || attr.VersionTolerant || attr.Keyed)
            return false;
        var layout = NinoSymbols.Get(type, _symbols.StructLayout);
        if (layout != null && layout.ConstructorArguments.Length > 0 &&
            layout.ConstructorArguments[0].Value is int kind && kind != 0 /* Sequential */)
            return false;

        var members = new HashSet<ISymbol>(_members.Of(type.OriginalDefinition, attr),
            SymbolEqualityComparer.Default);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            var member = field.AssociatedMember() ?? field;
            if (!members.Contains(member.OriginalDefinition))
                return false;
            if (NinoSymbols.Has(member, _symbols.CustomFormatter))
                return false;
            // Every field must itself be raw: Nullable, tuples, nint or DateTimeOffset have their own formats.
            if (!IsRawUnmanaged(field.Type))
                return false;
        }

        return true;
    }
}
