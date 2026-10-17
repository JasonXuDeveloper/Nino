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

        /// <summary>8 bytes aligned to the runtime's alignment of 8-byte primitives (<c>long</c>, <c>DateTime</c>...).</summary>
        Size8,

        /// <summary><c>decimal</c>: 16 bytes aligned to the runtime's alignment of <c>decimal</c>.</summary>
        Decimal,

        /// <summary><c>Guid</c>: 16 bytes aligned to 4 (an int, two shorts and eight bytes).</summary>
        Guid,

        /// <summary><c>IntPtr</c>, <c>UIntPtr</c> and pointers: the runtime's pointer size.</summary>
        Pointer,
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
        }

        /// <summary>
        /// Lays out <paramref name="type"/> at <paramref name="baseOffset"/> under a runtime model (alignment of 8-byte
        /// primitives, of <c>decimal</c>, and the pointer size), collecting its leaves in field order, and the offsets
        /// of its own fields in <paramref name="fieldOffsets"/> when given.
        /// </summary>
        public static bool Measure<TType, TView>(TView view, TType type, int longAlign, int decimalAlign,
            int pointerSize, int baseOffset, List<Leaf> leaves, out int size, out int align,
            List<int>? fieldOffsets = null)
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
                case NinoLeaf.Decimal:
                    return Add(16, decimalAlign, baseOffset, leaves, out size, out align);
                case NinoLeaf.Guid:
                    return Add(16, 4, baseOffset, leaves, out size, out align);
                case NinoLeaf.Pointer:
                    return Add(pointerSize, pointerSize, baseOffset, leaves, out size, out align);
            }

            size = align = 0;
            var fields = new List<KeyValuePair<TType, int>>();
            if (!view.Struct(type, fields, out var kind, out int pack, out int minSize))
                return false;
            if (kind == LayoutKind.Auto && fields.Count > 1)
                return false;
            if (pack <= 0)
                pack = int.MaxValue;

            int end = 0;
            align = 1;
            foreach (var field in fields)
            {
                int start = leaves.Count;
                if (!Measure(view, field.Key, longAlign, decimalAlign, pointerSize, 0, leaves, out int fieldSize,
                        out int fieldAlign))
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

        private static bool Add(int leafSize, int leafAlign, int baseOffset, List<Leaf> leaves, out int size,
            out int align)
        {
            size = leafSize;
            align = leafAlign;
            leaves.Add(new Leaf { Offset = baseOffset, Size = leafSize });
            return true;
        }
    }
}
