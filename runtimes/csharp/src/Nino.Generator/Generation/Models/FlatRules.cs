using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Whether a class's body is flat (#355, the generator seam), and its items (<see cref="FlatModel"/>): decided from
/// the body the generator would write, never from a list of member types. A body is flat where it is a sequence of
/// raw members, bools, raw <c>T?</c>s and strings and nothing else runs around it: a positional class, neither generic,
/// tracked, pooled, made by a factory or a constructor with arguments, a collection, nor calling callbacks; every member
/// a field or property generated code reads and assigns directly (a field by reference, NinoItemAt; a property through
/// its accessors, NinoPropertyAt), raw, a raw <c>T?</c> or a string written by the built-in formatters (a custom member
/// formatter makes a member none of them); at least one string or <c>T?</c> (a body of raw members alone is one block
/// already, <see cref="TypeModel.RawBody"/>).
/// </summary>
internal static class FlatRules
{
    /// <summary>
    /// The most items a flat body has: each is a level of inlining in the kernels (a list nested in type arguments), and
    /// the JIT inlines at most 20 levels deep, the kernels' own frames included.
    /// </summary>
    public const int MaxItems = 12;

    /// <summary>
    /// The flat layout of <paramref name="type"/>, or null when its body is not flat. <paramref name="symbols"/> are the
    /// members' symbols in the order of <paramref name="members"/>.
    /// </summary>
    public static FlatModel? Build(INamedTypeSymbol type, FormatterKind kind, NinoFormat format, bool generic,
        bool trackReferences, bool collection, bool pooled, bool callbacks, ConstructionModel construction,
        IReadOnlyList<MemberModel> members, IReadOnlyList<ISymbol> symbols)
    {
        // A body of raw members alone is one block already (TypeModel.RawBody): a string or a T? makes it flat.
        if (kind is not (FormatterKind.Class or FormatterKind.SealedClass) || generic || trackReferences ||
            collection || pooled || callbacks || format != NinoFormat.Positional || construction.Recreate ||
            construction.Factory.Length != 0 || !construction.Creator.StartsWith("new ", System.StringComparison.Ordinal) ||
            members.Count == 0 || members.Count != symbols.Count || !members.Any(static m => m.IsString || IsSlot(m)))
            return null;
        foreach (var member in members)
            if (member.Access != MemberAccess.Direct || member.ConstructionOnly || member.Unassignable ||
                member.Raw == null && !member.IsString && !IsSlot(member))
                return null;

        var offsets = PredictedOffsets(members, symbols);
        var items = new List<FlatItem>(members.Count);
        for (int i = 0; i < members.Count;)
        {
            var member = members[i];
            bool property = member.IsProperty;
            if (member.IsString)
            {
                bool utf8 = (member.Traits & FormatterTraits.Utf8String) != 0;
                items.Add(new FlatItem(utf8 ? FlatItemKind.Utf8 : FlatItemKind.String, i++, 1, property));
                continue;
            }

            if (IsSlot(member))
            {
                items.Add(new FlatItem(FlatItemKind.Slot, i++, 1, property));
                continue;
            }

            var raw = member.Raw!;
            if (raw.Read == RawRead.Bool)
            {
                items.Add(new FlatItem(FlatItemKind.Bool, i++, 1, property));
                continue;
            }

            // A struct, or a value Untrusted mode checks (DateTime, decimal): copied as its bytes only where proven.
            if (!raw.IsConstant || raw.Read != RawRead.Plain)
            {
                items.Add(new FlatItem(FlatItemKind.Struct, i++, 1, property));
                continue;
            }

            // A property: its value alone, through its accessors.
            if (property)
            {
                items.Add(new FlatItem(FlatItemKind.Value, i++, 1, true));
                continue;
            }

            // Consecutive primitives laid out in the object as on the wire: one block.
            int end = i + 1;
            while (end < members.Count && Continues(members, offsets, end))
                end++;
            items.Add(new FlatItem(end - i > 1 ? FlatItemKind.Block : FlatItemKind.Value, i, end - i));
            i = end;
        }

        return items.Count <= MaxItems ? new FlatModel(new EquatableArray<FlatItem>(items.ToArray())) : null;
    }

    /// <summary>
    /// Whether <paramref name="member"/> is a raw <c>T?</c>: written by <c>UnmanagedNullableFormatter</c>, a slot's
    /// single value (a presence byte, then the value).
    /// </summary>
    private static bool IsSlot(MemberModel member) =>
        member.Formatter.Name == TypeNames.Core + "UnmanagedNullableFormatter" && !member.Formatter.Open;

    /// <summary>
    /// Whether member <paramref name="i"/> extends the block of the member before it: both primitives (not bools,
    /// whose bytes a read checks), declared by the same class, and predicted next to each other in its memory.
    /// </summary>
    private static bool Continues(IReadOnlyList<MemberModel> members, int[] offsets, int i)
    {
        var previous = members[i - 1];
        var member = members[i];
        return !member.IsProperty && member.Raw is { IsConstant: true, Read: RawRead.Plain } && previous.Raw is { IsConstant: true, Read: RawRead.Plain } &&
               offsets[i] >= 0 && offsets[i - 1] >= 0 && member.DeclaringType == previous.DeclaringType &&
               offsets[i] == offsets[i - 1] + previous.Raw.Bytes;
    }

    /// <summary>
    /// Where 64-bit CoreCLR places each primitive member among the instance fields of the class declaring it
    /// (<c>NinoLayoutCore.PlacePrimitives</c>: its references first, then its primitives by size), relative to the
    /// first primitive; -1 for the others. A prediction only: the generated startup check proves it on the runtime.
    /// </summary>
    private static int[] PredictedOffsets(IReadOnlyList<MemberModel> members, IReadOnlyList<ISymbol> symbols)
    {
        var offsets = new int[members.Count];
        var placed = new Dictionary<IFieldSymbol, int>(SymbolEqualityComparer.Default);
        var done = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        for (int i = 0; i < members.Count; i++)
        {
            offsets[i] = -1;
            if (members[i].Raw is not { IsConstant: true } || symbols[i] is not IFieldSymbol field)
                continue;
            if (done.Add(field.ContainingType))
                Place(field.ContainingType, placed);
            if (placed.TryGetValue(field, out int offset))
                offsets[i] = offset;
        }

        return offsets;
    }

    /// <summary>The predicted offsets of the primitive instance fields <paramref name="type"/> declares.</summary>
    private static void Place(INamedTypeSymbol type, Dictionary<IFieldSymbol, int> placed)
    {
        var fields = new List<IFieldSymbol>();
        var sizes = new List<int>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field)
                continue;
            if (field.Type.TypeKind == TypeKind.TypeParameter)
                return; // A field of unknown kind: no prediction.
            int size = PrimitiveSize(field.Type);
            if (size <= 0)
                continue; // A reference, placed before the primitives, or a struct, placed after them.
            fields.Add(field);
            sizes.Add(size);
        }

        var sizeArray = sizes.ToArray();
        var offsets = new int[sizeArray.Length];
        NinoLayoutCore.PlacePrimitives(sizeArray, sizeArray, Enumerable.Repeat(true, sizeArray.Length).ToArray(),
            offsets);
        for (int i = 0; i < fields.Count; i++)
            placed[fields[i]] = offsets[i];
    }

    /// <summary>The size of a primitive or enum field, 0 for a reference, -1 for anything else.</summary>
    private static int PrimitiveSize(ITypeSymbol type)
    {
        if (type.IsReferenceType)
            return 0;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            type = underlying;
        return type.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
            SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
            _ => -1,
        };
    }
}
