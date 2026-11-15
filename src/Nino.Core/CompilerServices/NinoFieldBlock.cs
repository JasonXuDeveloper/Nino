using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// Generated raw bodies of classes: whether the fields a body writes lie in the object's memory exactly as the body
    /// lays them out (the same order, no gaps, values without padding), checked once per type on an uninitialized
    /// instance; the body is then one block copy instead of a store per field (LuminPack's 52-byte bodies: two vector
    /// moves against thirteen).
    /// </summary>
    /// <remarks>
    /// A class's fields are laid out by the runtime (CoreCLR groups them by size, base class first), so the check runs
    /// where the code does; a runtime that cannot make the instance (or lays the fields out otherwise) writes field by
    /// field.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoFieldBlock
    {
        /// <summary>An instance of <typeparamref name="T"/> whose fields can be measured, or null.</summary>
        public static T Probe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
                                                       DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>()
            where T : class
        {
            try
            {
                return RuntimeHelpers.GetUninitializedObject(typeof(T)) as T;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether <paramref name="field"/> lies <paramref name="offset"/> bytes after <paramref name="first"/>, the
        /// first field of the block, and its memory is its <paramref name="bytes"/> bytes on the wire.
        /// </summary>
        public static bool IsAt<TFirst, TField>(in TFirst first, in TField field, int offset, int bytes) =>
            Unsafe.ByteOffset(ref Unsafe.As<TFirst, byte>(ref Unsafe.AsRef(in first)),
                ref Unsafe.As<TField, byte>(ref Unsafe.AsRef(in field))) == (IntPtr)offset &&
            Unsafe.SizeOf<TField>() == bytes && NinoLayout<TField>.IsCanonical && !NinoLayout<TField>.HasPadding;

        /// <summary>
        /// Copies the <paramref name="size"/> bytes from <paramref name="first"/>, the block's first field, to
        /// <paramref name="destination"/> + <paramref name="offset"/> (a constant size: unrolled into vector moves).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<TFirst>(in TFirst first, ref byte destination, int offset, int size) =>
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref destination, offset),
                ref Unsafe.As<TFirst, byte>(ref Unsafe.AsRef(in first)), (uint)size);
    }
}
