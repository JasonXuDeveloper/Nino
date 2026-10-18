using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Nino.Core
{
    /// <summary>Strings made from UTF-16 chars in the data.</summary>
    internal static class NinoStrings
    {
        /// <summary>A new string of the <paramref name="length"/> UTF-16 chars at <paramref name="chars"/> (length &gt; 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe string New(ref byte chars, int length)
        {
#if NET8_0_OR_GREATER
            // The string constructor allocates and then calls Memmove, which is most of the time of a short string.
            // Allocated by the runtime's own FastAllocateString and filled with an inline copy: a string of 3 to 12
            // chars 25% faster (12 to 14 ns against 16 to 19).
            if (Allocator.Kind != 0)
            {
                string value = Allocator.Kind == 2 ? Allocator.Alloc(null, length) : Allocator.AllocInt(null, length);
                ref byte destination = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(value.AsSpan()));
                uint size = (uint)length * sizeof(char);
                if (size <= 32)
                    NinoUnsafe.Copy(ref destination, ref chars, size);
                else if (size <= 64)
                {
                    // 32-byte vectors are emulated where they are not accelerated (Arm64): two 16-byte ones each.
                    if (Vector256.IsHardwareAccelerated)
                    {
                        var head = Vector256.LoadUnsafe(ref chars);
                        var tail = Vector256.LoadUnsafe(ref chars, size - 32);
                        head.StoreUnsafe(ref destination);
                        tail.StoreUnsafe(ref destination, size - 32);
                    }
                    else
                    {
                        var head = Vector128.LoadUnsafe(ref chars);
                        var head2 = Vector128.LoadUnsafe(ref chars, 16);
                        var tail = Vector128.LoadUnsafe(ref chars, size - 32);
                        var tail2 = Vector128.LoadUnsafe(ref chars, size - 16);
                        head.StoreUnsafe(ref destination);
                        head2.StoreUnsafe(ref destination, 16);
                        tail.StoreUnsafe(ref destination, size - 32);
                        tail2.StoreUnsafe(ref destination, size - 16);
                    }
                }
                else
                    Unsafe.CopyBlockUnaligned(ref destination, ref chars, size);
                return value;
            }

            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref chars), length));
#elif NETSTANDARD2_0
            fixed (byte* p = &chars)
            {
                return new string((char*)p, 0, length);
            }
#else
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref chars), length));
#endif
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// <c>string.FastAllocateString</c>, which takes an <c>nint</c> on .NET 10 and an <c>int</c> on .NET 8 (this
        /// build runs on both). Each is tried once; the string constructor is used when neither is found.
        /// </summary>
        private static class Allocator
        {
            /// <summary>2: the nint overload, 1: the int one, 0: none. Read-only after startup, a constant to Tier1.</summary>
            public static readonly int Kind = Probe();

            [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FastAllocateString")]
            public static extern string Alloc(string type, nint length);

            [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FastAllocateString")]
            public static extern string AllocInt(string type, int length);

            private static int Probe()
            {
                try
                {
                    if (Alloc(null, 1)?.Length == 1)
                        return 2;
                }
                catch (MissingMethodException)
                {
                }

                try
                {
                    if (AllocInt(null, 1)?.Length == 1)
                        return 1;
                }
                catch (MissingMethodException)
                {
                }

                return 0;
            }
        }
#endif
    }
}
