using System;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The top-level reads of whole payloads in <see cref="NinoSettings.Untrusted"/> mode: each public API that takes
    /// the data as a span starts with a test of the setting (a constant once compiled, so trusted reads keep their own
    /// path and code) and comes here when it is on. One path for all: a reader over the data, the value read through
    /// its formatter (which checks what the raw paths would copy), then every byte must have been read.
    /// </summary>
    internal static class NinoUntrusted
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Read<T>(ReadOnlySpan<byte> data)
        {
            T value = default;
            Read(data, ref value);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Read<T>(ReadOnlySpan<byte> data, ref T value)
        {
            var reader = new Reader(data);
            NinoTopFormatter<T>.Formatter.Deserialize(ref value, ref reader);
            End(ref reader);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Read<T, TFormatter>(ReadOnlySpan<byte> data) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            Read<T, TFormatter>(data, ref value);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Read<T, TFormatter>(ReadOnlySpan<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            var reader = new Reader(data);
            default(TFormatter).Deserialize(ref value, ref reader);
            End(ref reader);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadBoxed(ReadOnlySpan<byte> data, ref object value, Type type)
        {
            var reader = new Reader(data);
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
            End(ref reader);
        }

        /// <summary>Bytes the value did not take (with the bodies deferred after it) are invalid data.</summary>
        private static void End(ref Reader reader)
        {
            int left = reader.Remaining;
            reader.ReleaseReferences();
            if (left != 0)
                NinoThrow.TrailingBytes(left);
        }
    }
}
