using System;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core
{
    /// <summary>
    /// Checks of values read from the data that Nino never writes otherwise: a <c>bool</c> other than 0 or 1, in every
    /// mode. Where the check goes decides what it costs the other types, so: a single bool by its own read
    /// (<see cref="Reader.ReadBool"/> and <see cref="NinoUnsafe.ReadBool"/>, which generated code uses for bool members),
    /// or out of line (<see cref="NinoLayout{T}.Read"/>, the top-level APIs through the formatter); a block of them in
    /// the data, before it is copied (<see cref="Bools(ref byte, int)"/>), behind a JIT-time test of the type written
    /// in place (<c>typeof(T) == typeof(bool)</c>), not in a helper: in a large method the inliner gives up on a helper,
    /// even one that folds away.
    /// </summary>
    internal static class NinoChecks
    {
        /// <summary>Throws if a byte of the <paramref name="count"/> at <paramref name="first"/> is neither 0 nor 1.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bools(ref byte first, int count)
        {
#if NET8_0_OR_GREATER
            // Vectorized by the runtime.
            int at = MemoryMarshal.CreateReadOnlySpan(ref first, count).IndexOfAnyExcept((byte)0, (byte)1);
            if (at >= 0)
                NinoThrow.InvalidBool(Unsafe.Add(ref first, at));
#else
            // Eight at a time: any bit other than each byte's lowest.
            const ulong high = 0xFEFE_FEFE_FEFE_FEFE;
            int i = 0;
            ulong any = 0;
            for (; i + 8 <= count; i += 8)
                any |= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, i));
            for (; i < count; i++)
                any |= Unsafe.Add(ref first, i);
            if ((any & high) != 0)
                NinoThrow.InvalidBool(2);
#endif
        }

        /// <summary>
        /// Throws if a has-value byte of the <paramref name="count"/> raw nullables at <paramref name="first"/>,
        /// <paramref name="stride"/> bytes apart, is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Flags(ref byte first, int count, int stride)
        {
            int any = Strided(ref first, count, stride);
            if ((any & 0xFE) != 0)
                NinoThrow.InvalidPresence(any);
        }

        /// <summary>
        /// Throws if a bool of the <paramref name="count"/> at <paramref name="first"/>, <paramref name="stride"/>
        /// bytes apart (the keys or values of raw pairs), is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bools(ref byte first, int count, int stride)
        {
            int any = Strided(ref first, count, stride);
            if ((any & 0xFE) != 0)
                NinoThrow.InvalidBool(any);
        }

        /// <summary>
        /// Checks <paramref name="count"/> raw values of <typeparamref name="T"/> at <paramref name="first"/>,
        /// <paramref name="stride"/> bytes apart, laid out as in memory: a <c>bool</c> other than 0 or 1, and in
        /// <see cref="NinoSettings.Untrusted"/> mode the fields of <see cref="NinoLayout{T}.Checks"/>. For the raw
        /// paths that copy values (<see cref="NinoLayout{T}.CopiesAsIs"/> false: call only then).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Values<T>(ref byte first, int count, int stride)
        {
            if (typeof(T) == typeof(bool))
            {
                if (stride == 1)
                    Bools(ref first, count);
                else
                    Bools(ref first, count, stride);
            }
            else if (NinoLayout<T>.Checks is { } checks)
            {
                NinoLayout.Check(checks, ref first, count, stride, typeof(T));
            }
        }

        /// <summary>
        /// <paramref name="value"/>, after its fields of <see cref="NinoLayout{T}.Checks"/> are checked (generated code,
        /// in <see cref="NinoSettings.Untrusted"/> mode). By value: the caller's copy never has its address taken.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Checked<T>(T value)
        {
            NinoLayout.Check(NinoLayout<T>.Checks, ref Unsafe.As<T, byte>(ref value), 1, 0, typeof(T));
            return value;
        }

        // Branch-free: the bytes are or-ed together.
        private static int Strided(ref byte first, int count, int stride)
        {
            int any = 0;
            ref byte at = ref first;
            for (int left = count; left > 0; left--)
            {
                any |= at;
                at = ref Unsafe.Add(ref at, stride);
            }

            return any;
        }
    }
}
