using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Decides whether a struct's raw memory is the same on every runtime Nino targets. Layouts differ in two ways:
/// x86 .NET/Mono align 8-byte primitives to 4 (64-bit, ARM and wasm align them to 8), and Mono/IL2CPP's
/// <c>decimal</c> is four ints (alignment 4) where CoreCLR's has a <c>ulong</c> (alignment 8). A struct is portable
/// when its field offsets and size come out the same under both models, and it has no pointer-sized fields.
/// </summary>
internal static class RawLayout
{
    public static bool IsPortable(ITypeSymbol type)
    {
        var wide = new List<int>();
        var narrow = new List<int>();
        return Measure(type, 8, 0, wide, out int wideSize, out _) &&
               Measure(type, 4, 0, narrow, out int narrowSize, out _) &&
               wideSize == narrowSize && wide.SequenceEqual(narrow);
    }

    // Lays out the type at baseOffset with 8-byte alignments capped to `cap`, collecting primitive offsets.
    private static bool Measure(ITypeSymbol type, int cap, int baseOffset, List<int> offsets, out int size,
        out int align)
    {
        size = align = 0;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            type = underlying;
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                return Primitive(1, 1, baseOffset, offsets, out size, out align);
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                return Primitive(2, 2, baseOffset, offsets, out size, out align);
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                return Primitive(4, 4, baseOffset, offsets, out size, out align);
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
            case SpecialType.System_DateTime:
            case SpecialType.System_Decimal when cap == 8:
                return Primitive(type.SpecialType == SpecialType.System_Decimal ? 16 : 8, cap, baseOffset, offsets,
                    out size, out align);
            case SpecialType.System_Decimal:
                return Primitive(16, 4, baseOffset, offsets, out size, out align);
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return false;
        }

        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named)
            return false;
        var definition = named.OriginalDefinition;
        if (TypeNames.HasMetadataName(definition, "System.TimeSpan"))
            return Primitive(8, cap, baseOffset, offsets, out size, out align);
        if (TypeNames.HasMetadataName(definition, "System.Guid"))
            return Primitive(16, 4, baseOffset, offsets, out size, out align);

        int end = 0;
        align = 1;
        foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            int start = offsets.Count;
            if (!Measure(field.Type, cap, 0, offsets, out int fieldSize, out int fieldAlign))
                return false;
            int offset = (end + fieldAlign - 1) / fieldAlign * fieldAlign;
            for (int i = start; i < offsets.Count; i++)
                offsets[i] += baseOffset + offset;
            end = offset + fieldSize;
            align = System.Math.Max(align, fieldAlign);
        }

        size = (end + align - 1) / align * align;
        return size > 0;
    }

    private static bool Primitive(int primitiveSize, int primitiveAlign, int baseOffset, List<int> offsets,
        out int size, out int align)
    {
        size = primitiveSize;
        align = primitiveAlign;
        offsets.Add(baseOffset);
        return true;
    }
}
