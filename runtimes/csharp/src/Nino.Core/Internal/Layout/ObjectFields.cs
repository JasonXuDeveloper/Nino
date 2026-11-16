using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The fields of an object at byte offsets: what the runtime's own collections hold, where they hold it on this
    /// runtime (<see cref="CollectionViews"/>).
    /// </summary>
    internal static class ObjectFields
    {
        /// <summary>A class whose one field is the first byte after the object header of this runtime.</summary>
        private sealed class RawObject
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public byte Data;
#pragma warning restore CS0649
        }

        /// <summary>The field of type <typeparamref name="TField"/> at <paramref name="offset"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TField At<TField>(object instance, int offset) =>
            ref Unsafe.As<byte, TField>(ref Unsafe.AddByteOffset(ref Unsafe.As<RawObject>(instance).Data,
                (IntPtr)offset));

        /// <summary>The offset of <paramref name="field"/>, a field of <paramref name="instance"/>.</summary>
        public static int OffsetOf<TField>(object instance, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<RawObject>(instance).Data, ref Unsafe.As<TField, byte>(ref field));

        /// <summary>
        /// Whether the field at <paramref name="offset"/> holds <paramref name="target"/>. Compared as addresses, so
        /// a field that holds no reference is never read as one; retried in case a GC moved the target between the
        /// two reads.
        /// </summary>
        public static bool Holds(object instance, int offset, object target)
        {
            for (int attempt = 0; attempt < 3; attempt++)
                if (At<IntPtr>(instance, offset) == Unsafe.As<object, IntPtr>(ref target))
                    return true;
            return false;
        }
    }
}
