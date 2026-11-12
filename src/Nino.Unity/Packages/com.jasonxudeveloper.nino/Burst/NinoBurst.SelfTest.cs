// The self-test Register runs once, before Nino.Core gets any kernel: each kernel is called through the function
// pointer Nino.Core would call, on inputs that take every branch (the lengths around 16 and 32, the AVX2 permute from
// 256 values and its three store kinds, the AVX2 and 16-byte tails of Interleave32 and NormalizeBoolSlots, a char or
// byte that stops each text kernel at the start, inside and at the end of a vector), and compared byte for byte with
// Nino.Core's scalar reference, guard bytes included. A kernel that throws or differs is left out of the table (Nino
// keeps its own path for that work) with a warning naming it and the Burst version. Burst versions compile the same
// C# differently (NinoBurst.Vectors.cs, the header): this is what keeps a new one from breaking serialization.
//
// In the editor (Burst's JIT) a kernel that throws reaches the catch below as a managed exception. In players Burst
// turns a throw into an abort of the process, which no catch sees: there the test catches wrong bytes only, and the
// kernels' form and the CI runs on each Burst major are what keep a throw out.
#if NINO_BURST
using System;
using Nino.Core;
using UnityEngine;

namespace Nino.Burst
{
    public static unsafe partial class NinoBurst
    {
        /// <summary>The Burst major the package was compiled against (asmdef versionDefines), for logs.</summary>
#if NINO_BURST_2
        internal const string BurstVersion = "2.x";
#else
        internal const string BurstVersion = "1.8.x";
#endif

        private static readonly int[] TestLengths = { 0, 1, 15, 16, 17, 31, 32, 33, 100 };

        /// <summary>
        /// Clears every kernel of <paramref name="kernels"/> that throws or writes other bytes than Nino.Core's scalar
        /// reference, logging each (a null one is skipped); false when none is left.
        /// </summary>
        internal static bool SelfTest(NinoKernels kernels, string table)
        {
            if (kernels.Permute != null && !Passes(kernels, PermuteWorks, "Permute", table))
                kernels.Permute = null;
            if (kernels.Interleave32 != null && !Passes(kernels, Interleave32Works, "Interleave32", table))
                kernels.Interleave32 = null;
            if (kernels.NormalizeBoolSlots != null && !Passes(kernels, NormalizeBoolSlotsWorks, "NormalizeBoolSlots", table))
                kernels.NormalizeBoolSlots = null;
            if (kernels.Narrow != null && !Passes(kernels, NarrowWorks, "Narrow", table))
                kernels.Narrow = null;
            if (kernels.Widen != null && !Passes(kernels, WidenWorks, "Widen", table))
                kernels.Widen = null;
            if (kernels.NarrowAscii != null && !Passes(kernels, NarrowAsciiWorks, "NarrowAscii", table))
                kernels.NarrowAscii = null;
            if (kernels.WidenAscii != null && !Passes(kernels, WidenAsciiWorks, "WidenAscii", table))
                kernels.WidenAscii = null;
            return kernels.Permute != null || kernels.Interleave32 != null || kernels.NormalizeBoolSlots != null ||
                   kernels.Narrow != null || kernels.Widen != null || kernels.NarrowAscii != null ||
                   kernels.WidenAscii != null;
        }

        private static bool Passes(NinoKernels kernels, Func<NinoKernels, bool> test, string name, string table)
        {
            string failure;
            try
            {
                failure = test(kernels) ? null : "wrote other bytes than Nino's scalar code";
            }
            catch (Exception e)
            {
                failure = "threw " + e.GetType().Name + (string.IsNullOrEmpty(e.Message) ? "" : ": " + e.Message);
            }

            if (failure == null)
                return true;
            Warn($"the Burst kernel {name}{table} {failure} in its self-test; Nino uses its scalar path for this work");
            return false;
        }

        // Every kernel that is left out says so, with what a report needs: the Burst and Unity versions and the CPU.
        // (In an IL2CPP player a kernel that throws ends the process before this runs: there, CI catches it.)
        internal static void Warn(string what) =>
            Debug.LogWarning($"Nino: {what} (Burst {BurstVersion}, Unity {Application.unityVersion}, " +
                             $"{SystemInfo.processorType}). Please report it at https://github.com/JasonXuDeveloper/Nino/issues.");

        // A deterministic byte sequence (xorshift), so a failure repeats.
        private static void Fill(byte[] bytes, uint seed)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                bytes[i] = (byte)seed;
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static bool Same(char[] a, char[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static bool PermuteWorks(NinoKernels kernels)
        {
            // (source, destination) strides: one load and one store (16 bytes or less), several (up to 64), and for
            // the AVX2 path (256 values or more, both up to 16) each store kind: whole lanes (16), whole dwords (12),
            // two 16-byte stores (5).
            int[] strides = { 8, 8, 16, 16, 12, 12, 5, 5, 3, 7, 13, 9, 24, 20, 64, 64, 33, 1 };
            int[] counts = { 0, 1, 2, 5, 33, 300 };
            uint seed = 1;
            for (int s = 0; s < strides.Length; s += 2)
            {
                int from = strides[s], to = strides[s + 1];
                var table = new byte[to];
                Fill(table, seed++);
                for (int j = 0; j < to; j++)
                    table[j] = j % 5 == 3 ? (byte)0xFF : (byte)(table[j] % from);
                foreach (int count in counts)
                {
                    var source = new byte[from * count + 1];
                    Fill(source, seed++);
                    // Guard bytes after the values: nothing may be written past them.
                    var actual = new byte[to * count + 64];
                    Fill(actual, seed++);
                    var expected = (byte[])actual.Clone();
                    fixed (byte* src = source, a = actual, e = expected, t = table)
                    {
                        kernels.Permute(src, from, a, to, t, count);
                        NinoKernels.PermuteScalar(src, from, e, to, t, count);
                    }

                    if (!Same(expected, actual))
                        return false;
                }
            }

            return true;
        }

        private static bool Interleave32Works(NinoKernels kernels)
        {
            // Multiples of four (Nino.Core passes no others): the AVX2 path's groups of eight and its four-pair tail.
            foreach (int count in new[] { 0, 4, 8, 12, 16, 36 })
            {
                var first = new int[count];
                var second = new int[count];
                for (int i = 0; i < count; i++)
                {
                    first[i] = i * 0x01020304 + 7;
                    second[i] = -13 * i - 1;
                }

                var actual = new byte[count * 8 + 2];
                Fill(actual, (uint)count + 3);
                var expected = (byte[])actual.Clone();
                fixed (int* a = first, b = second)
                fixed (byte* x = actual, y = expected)
                {
                    // At an odd offset: the destination may be unaligned.
                    kernels.Interleave32(a, b, x + 1, count);
                    NinoKernels.Interleave32Scalar(a, b, y + 1, count);
                }

                if (!Same(expected, actual))
                    return false;
            }

            return true;
        }

        private static bool NormalizeBoolSlotsWorks(NinoKernels kernels)
        {
            foreach (int count in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 100 })
            foreach (int invalid in new[] { -1, 0, count / 2, count - 1 })
            {
                var source = new byte[count * 2];
                for (int i = 0; i < count; i++)
                {
                    // Null slots with a dirty value byte, and present ones with 0 or 1.
                    source[i * 2] = i % 3 == 0 ? (byte)0 : (byte)1;
                    source[i * 2 + 1] = i % 3 == 0 ? (byte)0x7F : (byte)(i % 2);
                }

                if (invalid >= 0 && count > 0)
                {
                    source[invalid * 2] = 1;
                    source[invalid * 2 + 1] = 2;
                }

                var actual = new byte[count * 2 + 16];
                for (int i = 0; i < actual.Length; i++)
                    actual[i] = 0xA5;
                var expected = (byte[])actual.Clone();
                fixed (byte* s = source, a = actual, e = expected)
                {
                    int expectedDone = NinoKernels.NormalizeBoolSlotsScalar(s, e, count);
                    int done = kernels.NormalizeBoolSlots(s, a, count);
                    if (done < 0 || done > expectedDone)
                        return false;
                    // Bytes after the completed prefix stay untouched.
                    for (int i = done * 2; i < actual.Length; i++)
                        if (actual[i] != 0xA5)
                            return false;
                    if (done + NinoKernels.NormalizeBoolSlotsScalar(s + done * 2, a + done * 2, count - done) !=
                        expectedDone)
                        return false;
                }

                if (!Same(expected, actual))
                    return false;
            }

            return true;
        }

        // Each length with the stopping char nowhere, first, in the middle, at a vector's start and last.
        private static int[] StopsFor(int length) => new[] { -1, 0, length / 2, 16, length - 1 };

        private static bool NarrowWorks(NinoKernels kernels)
        {
            foreach (int length in TestLengths)
            foreach (int stop in StopsFor(length))
            {
                if (stop >= length)
                    continue;
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                    chars[i] = (char)((i * 37 + 11) & 0xFF);
                if (stop >= 0)
                    chars[stop] = (char)(0x100 + stop);
                var actual = new byte[length + 16];
                var expected = new byte[length + 16];
                bool narrowed, latin1;
                fixed (char* c = chars)
                fixed (byte* a = actual, e = expected)
                {
                    narrowed = kernels.Narrow((ushort*)c, a, length) != 0;
                    latin1 = NinoKernels.NarrowScalar(c, e, length);
                }

                // Not Latin-1: any part may have been written, but nothing past the chars.
                if (narrowed != latin1 || (latin1 ? !Same(expected, actual) : !Untouched(actual, length)))
                    return false;
            }

            return true;
        }

        private static bool WidenWorks(NinoKernels kernels)
        {
            foreach (int length in TestLengths)
            {
                var bytes = new byte[length];
                Fill(bytes, (uint)length + 5);
                var actual = new char[length + 8];
                var expected = new char[length + 8];
                fixed (byte* b = bytes)
                fixed (char* a = actual, e = expected)
                {
                    kernels.Widen(b, (ushort*)a, length);
                    NinoKernels.WidenScalar(b, e, length);
                }

                if (!Same(expected, actual))
                    return false;
            }

            return true;
        }

        private static bool NarrowAsciiWorks(NinoKernels kernels)
        {
            foreach (int length in TestLengths)
            foreach (int stop in StopsFor(length))
            foreach (char other in new[] { '\u0080', 'Ā' })
            {
                if (stop >= length)
                    continue;
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                    chars[i] = (char)((i * 37 + 11) & 0x7F);
                if (stop >= 0)
                    chars[stop] = other;
                var actual = new byte[length + 16];
                var expected = new byte[length + 16];
                int done, ascii;
                fixed (char* c = chars)
                fixed (byte* a = actual, e = expected)
                {
                    done = kernels.NarrowAscii((ushort*)c, a, length);
                    ascii = NinoKernels.NarrowAsciiScalar(c, e, length);
                }

                // Bytes past the ASCII ones may be written too, up to the length.
                if (done != ascii || !Untouched(actual, length))
                    return false;
                for (int i = 0; i < ascii; i++)
                    if (actual[i] != expected[i])
                        return false;
            }

            return true;
        }

        private static bool WidenAsciiWorks(NinoKernels kernels)
        {
            foreach (int length in TestLengths)
            foreach (int stop in StopsFor(length))
            foreach (byte other in new byte[] { 0x80, 0xFF })
            {
                if (stop >= length)
                    continue;
                var bytes = new byte[length];
                for (int i = 0; i < length; i++)
                    bytes[i] = (byte)((i * 37 + 11) & 0x7F);
                if (stop >= 0)
                    bytes[stop] = other;
                var actual = new char[length + 8];
                var expected = new char[length + 8];
                int done, ascii;
                fixed (byte* b = bytes)
                fixed (char* a = actual, e = expected)
                {
                    done = kernels.WidenAscii(b, (ushort*)a, length);
                    ascii = NinoKernels.WidenAsciiScalar(b, e, length);
                }

                if (done != ascii)
                    return false;
                for (int i = 0; i < ascii; i++)
                    if (actual[i] != expected[i])
                        return false;
                for (int i = length; i < actual.Length; i++)
                    if (actual[i] != 0)
                        return false;
            }

            return true;
        }

        // Nothing written from index `from` on (the arrays start zeroed).
        private static bool Untouched(byte[] bytes, int from)
        {
            for (int i = from; i < bytes.Length; i++)
                if (bytes[i] != 0)
                    return false;
            return true;
        }
    }
}
#endif
