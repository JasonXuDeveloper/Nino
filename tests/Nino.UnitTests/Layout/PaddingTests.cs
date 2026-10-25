using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    public struct PaddedPlain
    {
        public short A;
        public long B;
    }

    [NinoType]
    public class PaddedHolder
    {
        public SubsetPadded Nino;
        public PaddedPlain Plain;
        public int Tail;
        public SubsetPadded[] Array;
        public List<PaddedPlain> List;
        public Dictionary<int, SubsetPadded> Map;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct ExplicitGap
    {
        [FieldOffset(0)] public int A;
        [FieldOffset(8)] public int B;
    }

    [TestClass]
    public class PaddingTests
    {
        // Builds a value whose padding holds garbage, as uninitialized memory would.
        private static T Dirty<T>(T clean) where T : unmanaged
        {
            T value = default;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<T, byte>(ref value), 0xAB, (uint)Unsafe.SizeOf<T>());
            // Assigning the fields one by one keeps the garbage in the padding.
            ref byte dst = ref Unsafe.As<T, byte>(ref value);
            ref byte src = ref Unsafe.As<T, byte>(ref clean);
            var mask = Mask<T>();
            for (int i = 0; i < mask.Length; i++)
                if (mask[i])
                    Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
            return value;
        }

        private static bool[] Mask<T>()
        {
            if (typeof(T) == typeof(SubsetPadded))
                return new[] { true, false, false, false, true, true, true, true };
            if (typeof(T) == typeof(PaddedPlain))
                return new[] { true, true, false, false, false, false, false, false, true, true, true, true, true, true, true, true };
            return new[] { true, true, true, true, false, false, false, false, true, true, true, true };
        }

        [TestMethod]
        public void PaddingIsCleared()
        {
            var nino = Dirty(new SubsetPadded { A = 1, B = 2 });
            var plain = Dirty(new PaddedPlain { A = 3, B = 4 });
            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }, NinoSerializer.Serialize(nino));
            CollectionAssert.AreEqual(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 },
                NinoSerializer.Serialize(plain));
            CollectionAssert.AreEqual(new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0 },
                NinoSerializer.Serialize(Dirty(new ExplicitGap { A = 5, B = 6 })));

            var cleanHolder = new PaddedHolder
            {
                Nino = new SubsetPadded { A = 1, B = 2 }, Plain = new PaddedPlain { A = 3, B = 4 }, Tail = 5,
                Array = new[] { new SubsetPadded { A = 7, B = 8 }, new SubsetPadded { A = 9, B = 10 } },
                List = new List<PaddedPlain> { new PaddedPlain { A = 11, B = 12 } },
                Map = new Dictionary<int, SubsetPadded> { { 13, new SubsetPadded { A = 14, B = 15 } } },
            };
            var dirtyHolder = new PaddedHolder
            {
                Nino = nino, Plain = plain, Tail = 5,
                Array = new[] { Dirty(cleanHolder.Array[0]), Dirty(cleanHolder.Array[1]) },
                List = new List<PaddedPlain> { Dirty(cleanHolder.List[0]) },
                Map = new Dictionary<int, SubsetPadded> { { 13, Dirty(cleanHolder.Map[13]) } },
            };
            var bytes = NinoSerializer.Serialize(dirtyHolder);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder), bytes);
            CollectionAssert.DoesNotContain(bytes, (byte)0xAB);

            CollectionAssert.AreEqual(NinoSerializer.Serialize(cleanHolder.Array), NinoSerializer.Serialize(dirtyHolder.Array));
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(dirtyHolder.Array), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(dirtyHolder.List), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize((SubsetPadded?)nino), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(new HashSet<PaddedPlain> { plain }), (byte)0xAB);
        }

        [TestMethod]
        public void MaskedCopyMatchesTheMaskAtEveryLengthAndAlignment()
        {
            var random = new System.Random(7);
            foreach (int size in new[] { 2, 3, 12, 16, 24, 40, 64, 72 })
            {
                var pattern = new byte[size];
                for (int i = 0; i < size; i++)
                    pattern[i] = (byte)(i % 3 == 1 ? 0 : 0xFF);
                var mask = NinoMask.Create(pattern);
                Assert.IsNotNull(mask);
                foreach (int count in new[] { 0, 1, 2, 3, 5, 8, 13, 33, 100 })
                {
                    for (int offset = 0; offset < 8; offset++)
                    {
                        var src = new byte[count * size];
                        random.NextBytes(src);
                        var dst = new byte[offset + src.Length + 8];
                        random.NextBytes(dst);
                        var expected = (byte[])dst.Clone();
                        for (int i = 0; i < src.Length; i++)
                            expected[offset + i] = (byte)(src[i] & pattern[i % size]);
                        if (src.Length != 0)
                            mask.Copy(ref dst[offset], ref src[0], count);
                        CollectionAssert.AreEqual(expected, dst, $"size {size}, count {count}, offset {offset}");

                        // In place: only the padding changes.
                        var inPlace = (byte[])src.Clone();
                        if (inPlace.Length != 0)
                            mask.Clear(ref inPlace[0], count);
                        for (int i = 0; i < src.Length; i++)
                            Assert.AreEqual((byte)(src[i] & pattern[i % size]), inPlace[i]);
                    }
                }
            }
        }

#if !NINO_CORE_NETSTANDARD
        // The copies AMD CPUs take where the destination is a little past the source modulo 4 KB (4K aliasing): from
        // the end down. Taken on AMD only, so tested here directly, at every length and alignment.
        [TestMethod]
        public void DownwardCopiesMatchTheForwardOnes()
        {
            var random = new System.Random(11);
            foreach (int size in new[] { 4, 8, 16, 32, 64 })
            {
                var pattern = new byte[size];
                for (int i = 0; i < size; i++)
                    pattern[i] = (byte)(i % 4 == 3 ? 0 : 0xFF);
                var mask = NinoMask.Create(pattern);
                foreach (int count in new[] { 1, 2, 3, 7, 16, 33, 100, 1000 })
                {
                    for (int offset = 0; offset < 70; offset += 3)
                    {
                        var src = new byte[count * size + 70];
                        random.NextBytes(src);
                        var dst = new byte[offset + count * size + 8];
                        random.NextBytes(dst);
                        var expected = (byte[])dst.Clone();
                        int from = offset % 64;
                        for (int i = 0; i < count * size; i++)
                            expected[offset + i] = (byte)(src[from + i] & pattern[i % size]);
                        if (mask.CopyDown(ref dst[offset], ref src[from], count))
                            CollectionAssert.AreEqual(expected, dst, $"masked: size {size}, count {count}, offset {offset}");

                        uint bytes = (uint)(count * size);
                        if (bytes < 64)
                            continue;
                        dst = (byte[])expected.Clone();
                        random.NextBytes(dst);
                        var plain = (byte[])dst.Clone();
                        System.Array.Copy(src, from, plain, offset, bytes);
                        if (System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated)
                        {
                            var copy = (byte[])dst.Clone();
                            Nino.Core.CompilerServices.NinoUnsafe.CopyVectorsDown<System.Runtime.Intrinsics.Vector512<byte>>(ref copy[offset],
                                ref src[from], bytes);
                            CollectionAssert.AreEqual(plain, copy, $"512: {bytes} bytes, offset {offset}");
                        }

                        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
                        {
                            Nino.Core.CompilerServices.NinoUnsafe.CopyVectorsDown<System.Runtime.Intrinsics.Vector256<byte>>(ref dst[offset],
                                ref src[from], bytes);
                            CollectionAssert.AreEqual(plain, dst, $"256: {bytes} bytes, offset {offset}");
                        }
                    }
                }
            }
        }
#endif
    }
}
