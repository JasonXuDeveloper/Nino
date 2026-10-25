using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
#if NET8_0_OR_GREATER
    public struct WideAfterByte
    {
        public byte A;
        public System.Int128 B;
    }
#endif

    [TestClass]
    public class PortableLayoutTests
    {
        [TestMethod]
        public void LayoutDependentStructsUseTheCanonicalLayout()
        {
            // The 64-bit layout, padding cleared: the bytes every runtime writes and reads.
            var bytes = NinoSerializer.Serialize(new SubsetUnportableStruct { X = 1, Y = 2 });
            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0 }, bytes);
            Assert.AreEqual(2, NinoDeserializer.Deserialize<SubsetUnportableStruct>(bytes).Y);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct X86Memory
        {
            public int X;
            public long Y;
            public decimal D;
        }

        private struct Natural
        {
#pragma warning disable CS0649
            public int X;
            public long Y;
            public decimal D;
#pragma warning restore CS0649
        }

        [TestMethod]
        public void X86LayoutIsConvertedToTheCanonicalLayout()
        {
            // How x86 (8-byte primitives and decimal aligned to 4) lays out Natural: exactly X86Memory.
            var info = NinoLayout.Compute(typeof(Natural), Unsafe.SizeOf<X86Memory>(), 4, 4);
            Assert.IsFalse(info.Canonical);
            Assert.AreEqual(Unsafe.SizeOf<Natural>(), info.Size);

            var memory = new X86Memory { X = 1, Y = 2, D = 3.5m };
            var wire = new byte[info.Size];
            NinoLayout.Convert(info, ref Unsafe.As<X86Memory, byte>(ref memory), ref wire[0], true);
            var natural = new Natural { X = 1, Y = 2, D = 3.5m };
            CollectionAssert.AreEqual(NinoSerializer.Serialize(natural), wire);

            var back = default(X86Memory);
            NinoLayout.Convert(info, ref Unsafe.As<X86Memory, byte>(ref back), ref wire[0], false);
            // Compared as bytes: Equals would read the decimal at offset 12, which faults on Arm64 (SIGBUS).
            CollectionAssert.AreEqual(Bytes(ref memory), Bytes(ref back));

            Assert.IsTrue(NinoLayout.Compute(typeof(Natural), Unsafe.SizeOf<Natural>(), 8, 8).Canonical);
        }

        private static byte[] Bytes(ref X86Memory value) =>
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)).ToArray();

        [TestMethod]
        public void NullableSequencesUseTheCanonicalLayout()
        {
            var values = new int?[] { 1, null, 3 };
            // Garbage in the padding of the in-memory Nullable<int> must not reach the output.
            for (int i = 0; i < values.Length; i++)
                for (int b = 1; b < 4; b++)
                    Unsafe.Add(ref Unsafe.As<int?, byte>(ref values[i]), b) = 0xAB;
            var bytes = NinoSerializer.Serialize(values);
            CollectionAssert.DoesNotContain(bytes, (byte)0xAB);
            CollectionAssert.AreEqual(new int?[] { 1, null, 3 }, NinoDeserializer.Deserialize<int?[]>(bytes));

            var longs = new List<long?> { long.MinValue, null };
            Assert.AreEqual(4 + 2 * 16, NinoSerializer.Serialize(longs).Length);
            CollectionAssert.AreEqual(longs, NinoDeserializer.Deserialize<List<long?>>(NinoSerializer.Serialize(longs)));
            var decimals = new decimal?[] { 1.5m, null, -7m };
            Assert.AreEqual(4 + 3 * 24, NinoSerializer.Serialize(decimals).Length);
            CollectionAssert.AreEqual(decimals, NinoDeserializer.Deserialize<decimal?[]>(NinoSerializer.Serialize(decimals)));
            var bytesOnly = new byte?[] { 1, null };
            CollectionAssert.AreEqual(new byte[] { 2, 0, 0, 0, 1, 1, 0, 0 }, NinoSerializer.Serialize(bytesOnly));
        }

        [TestMethod]
        public void PointerSizedValuesUseEightBytes()
        {
            var bytes = NinoSerializer.Serialize(new System.IntPtr(-5));
            Assert.AreEqual(8, bytes.Length);
            Assert.AreEqual(new System.IntPtr(-5), NinoDeserializer.Deserialize<System.IntPtr>(bytes));
            var array = new[] { new System.UIntPtr(7), new System.UIntPtr(9) };
            Assert.AreEqual(4 + 16, NinoSerializer.Serialize(array).Length);
            CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<System.UIntPtr[]>(NinoSerializer.Serialize(array)));
        }

        [TestMethod]
        public void DateTimeOffsetIsTicksAndOffset()
        {
            var value = new System.DateTimeOffset(2024, 5, 6, 7, 8, 9, System.TimeSpan.FromMinutes(-150));
            var bytes = NinoSerializer.Serialize(value);
            Assert.AreEqual(10, bytes.Length);
            var back = NinoDeserializer.Deserialize<System.DateTimeOffset>(bytes);
            Assert.AreEqual(value, back);
            Assert.AreEqual(value.Offset, back.Offset);
            var list = new List<System.DateTimeOffset> { value, System.DateTimeOffset.MinValue };
            CollectionAssert.AreEqual(list, NinoDeserializer.Deserialize<List<System.DateTimeOffset>>(NinoSerializer.Serialize(list)));
        }

#if NET8_0_OR_GREATER
        [TestMethod]
        public void Int128IsAlignedToSixteenBytes()
        {
            var info = NinoLayout.Compute(typeof(WideAfterByte), Unsafe.SizeOf<WideAfterByte>(), 8, 8);
            Assert.AreEqual(32, info.Size);
            Assert.AreEqual(16, info.Align);
            // CoreCLR on 64-bit aligns it to 16, as the canonical layout does; Arm32 aligns it to 8 and converts.
            Assert.IsTrue(NinoLayout.WideAlign == 16 || NinoLayout.WideAlign == 8);
            Assert.AreEqual(NinoLayout.WideAlign == 16, info.Canonical);

            // A runtime that aligns it to 8 (Arm32, .NET 7) has it at 8, and converts to the same 32 bytes.
            var narrow = NinoLayout.Compute(typeof(WideAfterByte), 24, 8, 8, 8);
            Assert.IsFalse(narrow.Canonical);
            Assert.AreEqual(32, narrow.Size);
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 8, 16, 16 }, narrow.Map);
        }

        [TestMethod]
        public void Int128PaddingIsCleared()
        {
            var value = new WideAfterByte { A = 1, B = -2 };
            // The padding before B: 15 bytes where Int128 is aligned to 16, 7 where it is aligned to 8 (Arm32).
            int padEnd = (int)Unsafe.ByteOffset(ref Unsafe.As<WideAfterByte, byte>(ref value),
                ref Unsafe.As<System.Int128, byte>(ref value.B));
            for (int i = 1; i < padEnd; i++)
                Unsafe.Add(ref Unsafe.As<WideAfterByte, byte>(ref value), i) = 0xAB;
            var bytes = NinoSerializer.Serialize(value);
            var expected = new byte[32];
            expected[0] = 1;
            expected[16] = 0xFE;
            for (int i = 17; i < 32; i++)
                expected[i] = 0xFF;
            CollectionAssert.AreEqual(expected, bytes);
            var back = NinoDeserializer.Deserialize<WideAfterByte>(bytes);
            Assert.AreEqual((byte)1, back.A);
            Assert.AreEqual((System.Int128)(-2), back.B);

            var array = NinoSerializer.Serialize(new[] { value, value });
            CollectionAssert.DoesNotContain(array, (byte)0xAB);
            Assert.AreEqual(4 + 64, array.Length);
        }
#endif
    }
}
