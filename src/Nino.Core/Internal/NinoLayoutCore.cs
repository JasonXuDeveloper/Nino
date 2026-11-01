#nullable enable
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Linked into Nino.Generator, where a Nino.Core namespace would shadow its TypeNames.Core constant.
#if NINO_GENERATOR
namespace Nino.Generator.Generation
#else
namespace Nino.Core.Internal
#endif
{
    /// <summary>How a type is laid out when it is not a struct with fields of its own.</summary>
    internal enum NinoLeaf
    {
        /// <summary>Not a leaf: a struct laid out from its fields.</summary>
        None,

        /// <summary>A type that cannot be laid out (reference, pointer-sized where not supported...).</summary>
        Unsupported,

        /// <summary>1, 2 or 4 bytes aligned to their size (<c>bool</c>, <c>char</c>, <c>int</c>, <c>float</c>...).</summary>
        Size1,
        Size2,
        Size4,

        /// <summary>8 bytes aligned to the runtime's alignment of 8-byte primitives (<c>long</c>, <c>TimeSpan</c>...).</summary>
        Size8,

        /// <summary>
        /// <c>DateTime</c>: 8 bytes aligned to the runtime's alignment of it, which is not that of <c>long</c> on 32-bit
        /// x86 CoreCLR (an auto-layout struct as a field is aligned to 4 there, a <c>long</c> to 8). Only Nino.Core's view
        /// returns it; the generator's is <see cref="Size8"/>, which its 4-byte model aligns to 4 as well.
        /// </summary>
        DateTime,

        /// <summary><c>decimal</c>: 16 bytes aligned to the runtime's alignment of <c>decimal</c>.</summary>
        Decimal,

        /// <summary><c>Guid</c>: 16 bytes aligned to 4 (an int, two shorts and eight bytes).</summary>
        Guid,

        /// <summary><c>UIntPtr</c> and pointers: the runtime's pointer size (zero-extended to 8 bytes on the wire).</summary>
        Pointer,

        /// <summary>
        /// <c>IntPtr</c>: the runtime's pointer size, sign-extended to 8 bytes on the wire. Only Nino.Core's view
        /// returns it; the generator's is <see cref="Pointer"/>.
        /// </summary>
        SignedPointer,

        /// <summary>
        /// <c>Int128</c> and <c>UInt128</c>: 16 bytes aligned to the runtime's alignment of them (16 on .NET 8 and
        /// later, 8 on .NET 7). Only Nino.Core's view returns it; the generator treats a struct with one as not portable.
        /// </summary>
        Wide,
    }

    /// <summary>
    /// What <see cref="NinoLayoutCore"/> needs to know about a type: <c>System.Type</c> at run time,
    /// <c>ITypeSymbol</c> in the generator.
    /// </summary>
    internal interface INinoLayoutView<TType>
    {
        /// <summary>How the type is laid out if it is a leaf (enums are their underlying type).</summary>
        NinoLeaf Leaf(TType type);

        /// <summary>
        /// The instance fields of a struct, in declaration order, each with its explicit offset (or -1), and its
        /// layout. False when the type is not a struct this view can describe.
        /// </summary>
        bool Struct(TType type, List<KeyValuePair<TType, int>> fields, out LayoutKind kind, out int pack,
            out int minSize);

        /// <summary>
        /// Whether a field of the type is placed as a primitive by an auto layout (a primitive, an enum or a
        /// pointer), rather than as a struct (<see cref="NinoLayoutCore"/>, <c>autoPointerSize</c>).
        /// </summary>
        bool Primitive(TType type);
    }

    /// <summary>
    /// The struct layout algorithm shared by the runtime (<c>NinoLayout</c>: the wire layout, and this runtime's memory
    /// layout to convert from) and the generator (<c>RawLayout</c>: whether a struct is laid out the same on every
    /// runtime), so both apply the same rules. This file is compiled into Nino.Core and linked into Nino.Generator.
    /// </summary>
    internal static class NinoLayoutCore
    {
        /// <summary>A leaf of a laid out type: offset and size.</summary>
        public struct Leaf
        {
            public int Offset;
            public int Size;

            /// <summary>Whether the leaf is a signed integer extended by its sign where sizes differ (<c>nint</c>).</summary>
            public bool Signed;
        }

        /// <summary>
        /// Lays out <paramref name="type"/> at <paramref name="baseOffset"/> under a runtime model (alignment of 8-byte
        /// primitives, of <c>decimal</c>, the pointer size, and the alignments of <see cref="NinoLeaf.Wide"/> and of
        /// <see cref="NinoLeaf.DateTime"/>, 0 for <paramref name="longAlign"/>), collecting its leaves in field order, and
        /// the offsets of its own fields in <paramref name="fieldOffsets"/> when given. An auto-layout struct with more
        /// than one field is laid out by the rule of 64-bit CoreCLR (<see cref="PlaceAuto"/>) when
        /// <paramref name="autoPointerSize"/> gives its pointer size, and not at all (false) when it is 0.
        /// </summary>
        public static bool Measure<TType, TView>(TView view, TType type, int longAlign, int decimalAlign,
            int pointerSize, int baseOffset, List<Leaf> leaves, out int size, out int align,
            List<int>? fieldOffsets = null, int wideAlign = 16, int dateTimeAlign = 0, int autoPointerSize = 0)
            where TView : INinoLayoutView<TType>
        {
            switch (view.Leaf(type))
            {
                case NinoLeaf.Unsupported:
                    size = align = 0;
                    return false;
                case NinoLeaf.Size1:
                    return Add(1, 1, baseOffset, leaves, out size, out align);
                case NinoLeaf.Size2:
                    return Add(2, 2, baseOffset, leaves, out size, out align);
                case NinoLeaf.Size4:
                    return Add(4, 4, baseOffset, leaves, out size, out align);
                case NinoLeaf.Size8:
                    return Add(8, longAlign, baseOffset, leaves, out size, out align);
                case NinoLeaf.DateTime:
                    return Add(8, dateTimeAlign > 0 ? dateTimeAlign : longAlign, baseOffset, leaves, out size,
                        out align);
                case NinoLeaf.Decimal:
                    return Add(16, decimalAlign, baseOffset, leaves, out size, out align);
                case NinoLeaf.Guid:
                    return Add(16, 4, baseOffset, leaves, out size, out align);
                case NinoLeaf.Pointer:
                    return Add(pointerSize, pointerSize, baseOffset, leaves, out size, out align);
                case NinoLeaf.SignedPointer:
                    return Add(pointerSize, pointerSize, baseOffset, leaves, out size, out align, true);
                case NinoLeaf.Wide:
                    return Add(16, wideAlign, baseOffset, leaves, out size, out align);
            }

            size = align = 0;
            var fields = new List<KeyValuePair<TType, int>>();
            if (!view.Struct(type, fields, out var kind, out int pack, out int minSize))
                return false;
            if (kind == LayoutKind.Auto && fields.Count > 1)
                return autoPointerSize > 0 && PlaceAuto(view, fields, longAlign, decimalAlign, pointerSize,
                    baseOffset, leaves, out size, out align, fieldOffsets, wideAlign, dateTimeAlign, autoPointerSize);
            if (pack <= 0)
                pack = int.MaxValue;

            int end = 0;
            align = 1;
            foreach (var field in fields)
            {
                int start = leaves.Count;
                if (!Measure(view, field.Key, longAlign, decimalAlign, pointerSize, 0, leaves, out int fieldSize,
                        out int fieldAlign, null, wideAlign, dateTimeAlign, autoPointerSize))
                    return false;
                fieldAlign = fieldAlign < pack ? fieldAlign : pack;
                int offset;
                if (kind == LayoutKind.Explicit)
                {
                    if (field.Value < 0)
                        return false;
                    offset = field.Value;
                }
                else
                {
                    offset = (end + fieldAlign - 1) / fieldAlign * fieldAlign;
                }

                fieldOffsets?.Add(offset);

                for (int i = start; i < leaves.Count; i++)
                {
                    var leaf = leaves[i];
                    leaf.Offset += baseOffset + offset;
                    leaves[i] = leaf;
                }

                if (offset + fieldSize > end)
                    end = offset + fieldSize;
                if (fieldAlign > align)
                    align = fieldAlign;
            }

            size = (end + align - 1) / align * align;
            if (minSize > size)
                size = minSize;
            return size > 0;
        }

        /// <summary>
        /// The fields of an auto-layout struct placed as 64-bit CoreCLR places them (its type loader, and crossgen2's
        /// MetadataFieldLayoutAlgorithm.ComputeAutoFieldLayout, the same on .NET 8 and 10): the primitive fields first,
        /// by size from 8 bytes down to 1 and in declaration order within a size, each aligned to its size; then the
        /// struct fields in declaration order, each aligned to its own alignment. The struct is aligned to
        /// <paramref name="autoPointerSize"/> when it has a primitive field and to its fields' largest alignment
        /// otherwise, and where its fields end within <paramref name="autoPointerSize"/> bytes, to the least power of
        /// two that holds them instead: <c>(int, long)</c> is the long, then the int (16 bytes), <c>(byte, short)</c>
        /// the short, then the byte (4 bytes, aligned to 4), <c>(int, int, int)</c> 16 bytes. Leaves and
        /// <paramref name="fieldOffsets"/> stay in declaration order.
        /// </summary>
        private static bool PlaceAuto<TType, TView>(TView view, List<KeyValuePair<TType, int>> fields, int longAlign,
            int decimalAlign, int pointerSize, int baseOffset, List<Leaf> leaves, out int size, out int align,
            List<int>? fieldOffsets, int wideAlign, int dateTimeAlign, int autoPointerSize)
            where TView : INinoLayoutView<TType>
        {
            size = align = 0;
            int count = fields.Count;
            var own = new List<Leaf>[count];
            var sizes = new int[count];
            var aligns = new int[count];
            var primitive = new bool[count];
            int largest = 1;
            for (int i = 0; i < count; i++)
            {
                own[i] = new List<Leaf>();
                if (!Measure(view, fields[i].Key, longAlign, decimalAlign, pointerSize, 0, own[i], out sizes[i],
                        out aligns[i], null, wideAlign, dateTimeAlign, autoPointerSize))
                    return false;
                primitive[i] = view.Primitive(fields[i].Key);
                int required = primitive[i] ? autoPointerSize : aligns[i];
                if (required > largest)
                    largest = required;
            }

            var offsets = new int[count];
            int end = 0;
            for (int primitiveSize = 8; primitiveSize >= 1; primitiveSize >>= 1)
                for (int i = 0; i < count; i++)
                    if (primitive[i] && sizes[i] == primitiveSize)
                        Place(i);
            for (int i = 0; i < count; i++)
                if (!primitive[i])
                    Place(i);

            if (end > autoPointerSize)
            {
                align = largest;
            }
            else
            {
                align = 1;
                while (align < end)
                    align *= 2;
            }

            size = (end + align - 1) / align * align;
            for (int i = 0; i < count; i++)
            {
                fieldOffsets?.Add(offsets[i]);
                foreach (var leaf in own[i])
                    leaves.Add(new Leaf
                        { Offset = baseOffset + offsets[i] + leaf.Offset, Size = leaf.Size, Signed = leaf.Signed });
            }

            return size > 0;

            void Place(int i)
            {
                offsets[i] = end = (end + aligns[i] - 1) / aligns[i] * aligns[i];
                end += sizes[i];
            }
        }

        private static bool Add(int leafSize, int leafAlign, int baseOffset, List<Leaf> leaves, out int size,
            out int align, bool signed = false)
        {
            size = leafSize;
            align = leafAlign;
            leaves.Add(new Leaf { Offset = baseOffset, Size = leafSize, Signed = signed });
            return true;
        }
    }
}
