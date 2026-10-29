using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Which classes' collections prefetch their objects (INinoPrefetchFormatter in Nino.Core; the interceptors of
/// top-level calls with an array or a list of them call NinoDirect's ...Prefetched methods), and the bytes one element
/// brings into the cache: its object, and a line of each object its reference members point to. Nino.Core prefetches
/// a collection whose elements take more than a quarter of the machine's last-level cache (NinoPrefetch.CacheBytes):
/// a shorter one is likely in the cache, where a prefetch per element costs more than it saves (1,000 shapes, a few
/// KiB, serialized 14 to 15% slower with one), and Go's prefetch of 1M records (about 100 MB) was 13 to 38% slower on
/// a Xeon whose L3 holds them.
/// </summary>
internal static class PrefetchRules
{
    /// <summary>The bytes counted for what a reference member points to: a line of another object.</summary>
    public const int PerReference = 64;

    /// <summary>The object header and method table pointer of every object (64-bit).</summary>
    private const int Header = 16;

    /// <summary>
    /// The bytes an element of <paramref name="type"/>'s collections brings into the cache, or 0 for collections that
    /// do not prefetch: only classes and interfaces (their elements are references to objects elsewhere in the heap)
    /// that are not generic (their formatter is shared generic code, where the collection loops would look it up at
    /// run time).
    /// </summary>
    public static int Bytes(INamedTypeSymbol type, FormatterKind kind, bool generic, bool fixedSize)
    {
        // A class of fixed size: its body is a few stores the JIT inlines into the collection loop with every writer
        // field in a register (FixedSizeTest's FixedClasses); left out, not measured with the runs and their calls.
        if (kind == FormatterKind.Struct || generic || fixedSize)
            return 0;
        int bytes = Header;
        for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            bytes += Fields(t, 0);
        // The smallest object is 24 bytes; no estimate above a page.
        return System.Math.Min(System.Math.Max(bytes, 24), 4096);
    }

    /// <summary>The most members whose objects are prefetched: each a load of the element's object and a prefetch.</summary>
    private const int MaxMembers = 2;

    /// <summary>
    /// The serialized members whose objects the prefetching loops prefetch once the element's own object is in the
    /// cache: up to <see cref="MaxMembers"/> fields and auto-properties of reference types, read as <c>value.Member</c>
    /// (no getter with code of its own runs, and none through an accessor).
    /// </summary>
    public static EquatableArray<string> Members(IReadOnlyList<MemberModel> models, IReadOnlyList<ISymbol> symbols)
    {
        var names = new List<string>();
        for (int i = 0; i < models.Count && names.Count < MaxMembers; i++)
        {
            if (models[i].Access != MemberAccess.Direct)
                continue;
            ITypeSymbol? type = symbols[i] switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property when IsAuto(property) => property.Type,
                _ => null,
            };
            if (type is { IsReferenceType: true })
                names.Add(models[i].SourceName);
        }

        return new EquatableArray<string>(names);
    }

    private static bool IsAuto(IPropertySymbol property) =>
        property.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    /// <summary>The bytes of <paramref name="type"/>'s own instance fields, a struct's counted in place.</summary>
    private static int Fields(INamedTypeSymbol type, int depth)
    {
        int bytes = 0;
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { IsStatic: false, IsConst: false } field)
                bytes += Size(field.Type, depth);
        }

        return bytes;
    }

    private static int Size(ITypeSymbol type, int depth)
    {
        if (!type.IsValueType)
            return 8 + PerReference;
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                return 1;
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                return 2;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                return 4;
            case SpecialType.System_Decimal:
                return 16;
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            return Size(underlying, depth);
        // Other structs: their fields, a few levels deep (8 bytes past that, or for a type parameter).
        return type is INamedTypeSymbol named && depth < 4 && named.SpecialType == SpecialType.None
            ? System.Math.Max(Fields(named, depth + 1), 1)
            : 8;
    }
}
