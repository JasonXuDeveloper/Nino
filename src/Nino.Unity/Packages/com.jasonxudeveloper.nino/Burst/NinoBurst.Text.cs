// NinoKernels.Narrow, Widen, NarrowAscii and WidenAscii: Latin-1 strings narrowed to and widened from one byte a
// char, and the leading ASCII text of [NinoUtf8] strings, 16 chars at a time. Registration is in NinoBurst.cs.
#if NINO_BURST
using System.Runtime.InteropServices;
using AOT;
using Nino.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NarrowFunction(ushort* source, byte* destination, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void WidenFunction(byte* source, ushort* destination, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WidenAsciiFunction(byte* source, ushort* destination, int length);

        /// <summary>
        /// <see cref="NinoKernels.Narrow"/>, 16 chars at a time (the last 16 again, overlapping): 1 when all are Latin-1,
        /// 0 at the first 16 with a char above U+00FF. No 32-byte path: on the CI players' CPUs it was no faster
        /// (strings of 1000 and 100000 chars, within a few percent either way).
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(NarrowFunction))]
        public static int Narrow(ushort* source, byte* destination, int length)
        {
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                int i = 0;
                while (true)
                {
                    v128 a = Load((byte*)(source + i)), b = Load((byte*)(source + i + 8));
                    if (AboveLatin1(a, b))
                        return 0;
                    Store(destination + i, Pack(a, b));
                    if (i + 16 == length)
                        return 1;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (int i = 0; i < length; i++)
            {
                if (source[i] > 0xFF)
                    return 0;
                destination[i] = (byte)source[i];
            }

            return 1;
        }

        /// <summary><see cref="NinoKernels.Widen"/>, 16 bytes at a time (the last 16 again, overlapping).</summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(WidenFunction))]
        public static void Widen(byte* source, ushort* destination, int length)
        {
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                int i = 0;
                while (true)
                {
                    v128 v = Load(source + i);
                    Store((byte*)(destination + i), Low(v));
                    Store((byte*)(destination + i + 8), High(v));
                    if (i + 16 == length)
                        return;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (int i = 0; i < length; i++)
                destination[i] = source[i];
        }

        /// <summary>
        /// <see cref="NinoKernels.NarrowAscii"/>, 16 chars at a time (the last 16 again, overlapping): the count of
        /// leading ASCII chars, each written as its byte; from the first 16 with another char, one at a time.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(NarrowFunction))]
        public static int NarrowAscii(ushort* source, byte* destination, int length)
        {
            int i = 0;
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                while (true)
                {
                    v128 a = Load((byte*)(source + i)), b = Load((byte*)(source + i + 8));
                    if (AboveAscii(a, b))
                        break;
                    Store(destination + i, Pack(a, b));
                    if (i + 16 == length)
                        return length;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (; i < length && source[i] < 0x80; i++)
                destination[i] = (byte)source[i];
            return i;
        }

        /// <summary>
        /// <see cref="NinoKernels.WidenAscii"/>, 16 bytes at a time (the last 16 again, overlapping): the count of
        /// leading ASCII bytes, each written as its char; from the first 16 with another byte, one at a time.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(WidenAsciiFunction))]
        public static int WidenAscii(byte* source, ushort* destination, int length)
        {
            int i = 0;
            if ((Arm.Neon.IsNeonSupported || X86.Sse2.IsSse2Supported) && length >= 16)
            {
                while (true)
                {
                    v128 v = Load(source + i);
                    if (AnyAboveAscii(v))
                        break;
                    Store((byte*)(destination + i), Low(v));
                    Store((byte*)(destination + i + 8), High(v));
                    if (i + 16 == length)
                        return length;
                    i = i + 32 <= length ? i + 16 : length - 16;
                }
            }

            for (; i < length && source[i] < 0x80; i++)
                destination[i] = source[i];
            return i;
        }
    }
}
#endif
