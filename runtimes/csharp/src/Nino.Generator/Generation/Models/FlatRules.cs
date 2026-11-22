using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Whether a class's or struct's body is flat (#355, the generator seam), and its items (<see cref="FlatModel"/>):
/// decided from the body the generator would write, never from a list of member types. A body is flat where it is a
/// sequence of raw members, bools, raw <c>T?</c>s, strings and members whose type is itself flat, and nothing else runs
/// around it: a positional, version-tolerant (its members in a region) or keyed (each a key, a length and its value)
/// class or struct (a generic one whose members name no type parameter), neither tracked, pooled, made by a factory or
/// a constructor with arguments, a collection, nor calling callbacks; every member a field or property generated code
/// reads and assigns directly (a field by reference, NinoItemAt; a property through its accessors, NinoPropertyAt),
/// raw, a raw <c>T?</c> or a string written by the built-in formatters, or of a flat non-generic type generated here
/// written by its own formatter (a custom member formatter makes a member none of them); a positional class with at
/// least one string, <c>T?</c> or nested body (one of raw members alone is one block already,
/// <see cref="TypeModel.RawBody"/>), a struct holding a reference.
/// </summary>
internal static class FlatRules
{
    /// <summary>
    /// The most levels of inlining a flat body's chain takes (<see cref="FlatModel.Levels"/>): each item is a level in
    /// the kernels (a list nested in type arguments), as is each wrapper (a version-tolerant or keyed body's region and
    /// members, a nested body), and the JIT inlines at most 20 levels deep, the kernels' own frames included.
    /// </summary>
    public const int MaxItems = 12;

    /// <summary>
    /// The flat layout of <paramref name="type"/>, or null when its body is not flat. <paramref name="symbols"/> are the
    /// members' symbols in the order of <paramref name="members"/>; <paramref name="facts"/> tells the flat bodies of
    /// member types (<see cref="TypeFacts.Flat"/>).
    /// </summary>
    public static FlatModel? Build(INamedTypeSymbol type, FormatterKind kind, bool rawStruct, NinoFormat format, bool generic,
        bool trackReferences, bool collection, bool pooled, bool callbacks, ConstructionModel construction,
        IReadOnlyList<MemberModel> members, IReadOnlyList<ISymbol> symbols, TypeFacts facts)
    {
        if (!TypeFacts.EnterFlat(type))
            return null; // Reached again while its own body is decided: it holds itself, never flat.
        try
        {
            return facts.Flattened(type, Decide(kind, rawStruct, format, generic, trackReferences, collection, pooled,
                callbacks, construction, members, symbols, facts));
        }
        finally
        {
            TypeFacts.ExitFlat(type);
        }
    }

    private static FlatModel? Decide(FormatterKind kind, bool rawStruct, NinoFormat format, bool generic,
        bool trackReferences, bool collection, bool pooled, bool callbacks, ConstructionModel construction,
        IReadOnlyList<MemberModel> members, IReadOnlyList<ISymbol> symbols, TypeFacts facts)
    {
        // A positional body of raw members alone is one block already (TypeModel.RawBody): a string, a T? or a nested
        // body makes it flat; a version-tolerant or keyed one is never raw, its region makes it a chain.
        // A struct (not raw: a raw one is a block) composes in place, its runs only, as a pair does: with a reference
        // (a string, or one inside a nested body), so that collections move it through the run kernels (NinoCursor.Own);
        // never in blocks (no startup check).
        bool valueType = kind == FormatterKind.Struct;
        bool positional = format == NinoFormat.Positional;
        // A generic type: its members' types name no type parameter (each a constant item, whatever the arguments);
        // its hooks hold only where every argument is a value type (FlatEmitter), where its code is exact.
        if (!(valueType ? !rawStruct : kind is FormatterKind.Class or FormatterKind.SealedClass) ||
            generic && members.Any(static m => m.Formatter.Open) ||
            trackReferences || collection || pooled || callbacks ||
            construction.Recreate || construction.Factory.Length != 0 ||
            !valueType && !construction.Creator.StartsWith("new ", System.StringComparison.Ordinal) ||
            members.Count == 0 || members.Count != symbols.Count)
            return null;

        var nested = new FlatModel?[members.Count];
        bool holdsReference = false, needsChain = !positional;
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member.Access != MemberAccess.Direct || member.ConstructionOnly || member.Unassignable)
                return null;
            if (member.IsString)
            {
                holdsReference = needsChain = true;
                continue;
            }

            if (IsSlot(member))
            {
                needsChain = true;
                continue;
            }

            if (member.Raw != null)
                continue;
            if ((nested[i] = Nested(member, symbols[i], facts)) is not { } inner)
                return null;
            // A class is a reference; a flat struct holds one.
            holdsReference = needsChain = true;
        }

        if (valueType ? !holdsReference : !needsChain)
            return null;

        var offsets = PredictedOffsets(members, symbols);
        var items = new List<FlatItem>(members.Count);
        int depth = 0, levels = 0, wrappers = positional ? 0 : format == NinoFormat.Keyed ? 2 : 1;
        for (int i = 0; i < members.Count;)
        {
            var member = members[i];
            bool property = member.IsProperty;
            // The levels of this item's node: the nodes before it, its own, and its bottom item's wrapper.
            int level = wrappers + items.Count + 1 + (positional ? 0 : 1);
            if (nested[i] is { } inner)
            {
                // Its item (a level), its element item for a class (another), then its chain.
                depth = System.Math.Max(depth, inner.RunDepth);
                levels = System.Math.Max(levels, level + 1 + (inner.ValueType ? 0 : 1) + inner.Levels);
                // The chain of fields (no startup check), as one item: NinoFlat's Sealed or Typed, NinoFlatValues' Item.
                string body = inner.ValueType ? "__Flat.Item"
                    : (inner.Checks ? "__Fields." : "__Flat.") + (inner.Sealed ? "Sealed" : "Typed");
                items.Add(new FlatItem(FlatItemKind.Nested, i, 1, property, member.Formatter.Name + "." + body, !inner.ValueType));
                i++;
                continue;
            }

            levels = System.Math.Max(levels, level);
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

            // A struct, or a value Untrusted mode checks (DateTime, decimal): copied as its bytes only where proven
            // (a property's and a struct's never).
            if (!raw.IsConstant || raw.Read != RawRead.Plain)
            {
                items.Add(new FlatItem(FlatItemKind.Struct, i++, 1, property));
                continue;
            }

            // A property, a struct's member, or a member of a region (whose every member is its own item): its value
            // alone.
            if (property || valueType || !positional)
            {
                items.Add(new FlatItem(FlatItemKind.Value, i++, 1, property));
                continue;
            }

            // Consecutive primitives laid out in the object as on the wire: one block.
            int end = i + 1;
            while (end < members.Count && nested[end] == null && Continues(members, offsets, end))
                end++;
            items.Add(new FlatItem(end - i > 1 ? FlatItemKind.Block : FlatItemKind.Value, i, end - i));
            i = end;
        }

        return levels <= MaxItems
            ? new FlatModel(new EquatableArray<FlatItem>(items.ToArray()), valueType, generic, format, depth, levels,
                kind == FormatterKind.SealedClass)
            : null;
    }

    /// <summary>
    /// The flat body of <paramref name="member"/>'s type, where the chain nests it: a non-generic <c>[NinoType]</c>
    /// generated here, written by its own generated formatter (no custom one), whose body is flat; else null.
    /// </summary>
    private static FlatModel? Nested(MemberModel member, ISymbol symbol, TypeFacts facts)
    {
        if (TypeOf(symbol) is not INamedTypeSymbol { IsGenericType: false } type || !member.Formatter.Generated ||
            member.Formatter.Args.Length != 0 ||
            member.Formatter.Name != TypeNames.Gen + TypeNames.FormatterName(type.OriginalDefinition))
            return null;
        return facts.Flat(type) is { Generic: false } inner ? inner : null;
    }

    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="member"/> is a raw <c>T?</c>: written by <c>UnmanagedNullableFormatter</c>, a slot's
    /// single value (a presence byte, then the value).
    /// </summary>
    private static bool IsSlot(MemberModel member) =>
        member.Formatter.Name == TypeNames.Core + "UnmanagedNullableFormatter" && !member.Formatter.Open &&
        SlotBytes(member) > 0;

    /// <summary>
    /// The most bytes a raw <c>T?</c> member's single value takes (its item's size, a literal of the generated
    /// accessor): its presence byte and the canonical bytes of <c>T</c>, as its encoding publishes them
    /// (<c>nullable(raw(N)...)</c>, WireEncoding); 0 where the encoding names none.
    /// </summary>
    public static int SlotBytes(MemberModel member)
    {
        const string prefix = "nullable(raw(";
        string encoding = member.Encoding;
        if (!encoding.StartsWith(prefix, System.StringComparison.Ordinal))
            return 0;
        int end = encoding.IndexOf(')', prefix.Length);
        return end > prefix.Length && int.TryParse(encoding.Substring(prefix.Length, end - prefix.Length),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int bytes)
            ? 1 + bytes
            : 0;
    }

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
