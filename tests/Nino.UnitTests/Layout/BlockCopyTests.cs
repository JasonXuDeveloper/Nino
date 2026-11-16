#if NET8_0_OR_GREATER
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core.CompilerServices;

namespace Nino.UnitTests
{
    /// <summary>
    /// NinoUnsafe's block copies at every distance between source and destination modulo 4 KB, where AMD processors
    /// copy downward (4K aliasing), and from sources at several alignments: the bytes copied, and nothing around them.
    /// </summary>
    [TestClass]
    public unsafe class BlockCopyTests
    {
        private static readonly uint[] Sizes = { 65, 129, 200, 1000, 8000, 24000, 32768 };
        private static readonly int[] Gaps = { 0, 4, 36, 64, 132, 1028, 2047, 2048, 4036 };
        private static readonly int[] Shifts = { 0, 8, 40 };

        private delegate void CopyFn(ref byte destination, ref byte source, uint size);

        [TestMethod]
        public void CopyWritesTheBlockAtEveryGap() => Check((ref byte d, ref byte s, uint n) => NinoUnsafe.Copy(ref d, ref s, n));

        [TestMethod]
        public void DownwardCopyWritesTheBlockAtEveryGap()
        {
            if (!Vector512.IsHardwareAccelerated)
                Assert.Inconclusive("64-byte vectors are not accelerated here.");
            Check((ref byte d, ref byte s, uint n) =>
            {
                if (n > 128)
                    NinoUnsafe.CopyVectorsDown(ref d, ref s, n);
                else
                    NinoUnsafe.Copy(ref d, ref s, n);
            });
        }

        private static void Check(CopyFn copy)
        {
            const int region = 1 << 17;
            byte* memory = (byte*)NativeMemory.AlignedAlloc(region * 2, 4096);
            try
            {
                var random = new Random(7);
                foreach (uint size in Sizes)
                foreach (int gap in Gaps)
                foreach (int shift in Shifts)
                {
                    byte* source = memory + shift;
                    byte* destination = memory + region + shift + gap;
                    new Span<byte>(memory, region * 2).Fill(0xA5);
                    random.NextBytes(new Span<byte>(source, (int)size));
                    copy(ref *destination, ref *source, size);
                    string at = $"size {size}, gap {gap}, shift {shift}";
                    Assert.IsTrue(new Span<byte>(destination, (int)size).SequenceEqual(new Span<byte>(source, (int)size)), at);
                    Assert.AreEqual(0xA5, destination[-1], at);
                    Assert.AreEqual(0xA5, destination[size], at);
                }
            }
            finally
            {
                NativeMemory.AlignedFree(memory);
            }
        }
    }
}
#endif
