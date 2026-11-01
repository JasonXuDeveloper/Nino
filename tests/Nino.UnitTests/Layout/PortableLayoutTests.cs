using System.Collections.Generic;
using System.Linq;
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

    public struct WideAfterByteOrLong
    {
#pragma warning disable CS0649
        public byte A;
#if NET8_0_OR_GREATER
        public System.Int128 B;
#else
        public long B;
#endif
#pragma warning restore CS0649
    }

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

        private struct HoldsDateTime
        {
#pragma warning disable CS0649
            public int A;
            public System.DateTime B;
#pragma warning restore CS0649
        }

        private struct HoldsDateTimes
        {
#pragma warning disable CS0649
            public byte A;
            public System.DateTime B;
            public int C;
#pragma warning restore CS0649
        }

        // How .NET on 32-bit x86 lays out HoldsDateTimes: DateTime aligned to 4, long to 8.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct HoldsDateTimesX86
        {
            public byte A;
            public System.DateTime B;
            public int C;
        }

        private struct HoldsTimeSpan
        {
#pragma warning disable CS0649
            public int A;
            public System.TimeSpan B;
#pragma warning restore CS0649
        }

        [TestMethod]
        public void X86DateTimeAlignmentIsConvertedToTheCanonicalLayout()
        {
            // .NET on 32-bit x86 aligns long, double and TimeSpan fields to 8 but DateTime (an auto-layout struct) to
            // 4: { int; DateTime } is 12 bytes there, 16 on 64-bit. With one alignment for both, the model said 16,
            // missed the runtime's 12, and the value was copied as its 12 bytes of memory.
            var info = NinoLayout.Compute(typeof(HoldsDateTime), 12, 8, 8, 0, 4);
            Assert.IsFalse(info.Canonical);
            Assert.AreEqual(16, info.Size);
            CollectionAssert.AreEqual(new[] { 0, 0, 4, 4, 8, 8 }, info.Map);

            var x86 = NinoLayout.Compute(typeof(HoldsDateTimes), Unsafe.SizeOf<HoldsDateTimesX86>(), 8, 8, 0, 4);
            Assert.IsFalse(x86.Canonical);
            Assert.AreEqual(Unsafe.SizeOf<HoldsDateTimes>(), x86.Size);
            var memory = new HoldsDateTimesX86 { A = 1, B = new System.DateTime(0x0102030405060708L), C = -3 };
            var wire = new byte[x86.Size];
            NinoLayout.Convert(x86, ref Unsafe.As<HoldsDateTimesX86, byte>(ref memory), ref wire[0], true);
            CollectionAssert.AreEqual(
                NinoSerializer.Serialize(new HoldsDateTimes { A = 1, B = memory.B, C = -3 }), wire);
            var back = default(HoldsDateTimesX86);
            NinoLayout.Convert(x86, ref Unsafe.As<HoldsDateTimesX86, byte>(ref back), ref wire[0], false);
            Assert.AreEqual(memory.B, back.B);
            Assert.AreEqual(-3, back.C);

            // TimeSpan follows long (8 there): nothing to convert.
            Assert.IsTrue(NinoLayout.Compute(typeof(HoldsTimeSpan), 16, 8, 8, 0, 4).Canonical);
            // This runtime's probed alignments describe its own layout: 16 canonical bytes, converted or not.
            Assert.AreEqual(16, NinoLayout.Get(typeof(HoldsDateTime), Unsafe.SizeOf<HoldsDateTime>()).Size);
        }

        private struct Mixed
        {
#pragma warning disable CS0649
            public bool A;
            public HoldsDateTimes B;
            public bool C;
            public decimal D;
            public short E;
            public System.Guid F;
            public byte G;
            public double H;
#pragma warning restore CS0649
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Overlapping
        {
#pragma warning disable CS0649
            [FieldOffset(0)] public long A;
            [FieldOffset(4)] public int B;
            [FieldOffset(9)] public bool C;
#pragma warning restore CS0649
        }

        private struct HoldsNullable
        {
#pragma warning disable CS0649
            public byte A;
            public int? B;
#pragma warning restore CS0649
        }

        [TestMethod]
        public void MeasuredOffsetsMatchTheModelWhereItHolds()
        {
            // Where the model is not this runtime's (.NET Framework lays out a struct holding a DateTime as
            // auto-layout), the offsets are read from an instance instead: here they must agree with the model.
            AssertSameLayout<Natural>();
            AssertSameLayout<HoldsDateTime>();
            AssertSameLayout<HoldsDateTimes>();
            AssertSameLayout<HoldsDateTimesX86>();
            AssertSameLayout<Mixed>();
            AssertSameLayout<Overlapping>();
            AssertSameLayout<WideAfterByteOrLong>();
            // A Nullable field is boxed as its value, so its offset cannot be read: copied as memory, as before.
            var nullable = NinoLayout.Compute(typeof(HoldsNullable), Unsafe.SizeOf<HoldsNullable>(), 8, 8, 0, 0, true);
            Assert.IsTrue(nullable.Canonical);
            Assert.IsNull(nullable.Map);
            Assert.IsNull(nullable.Mask);
        }

        private static void AssertSameLayout<T>() where T : struct
        {
            var model = NinoLayout.Compute(typeof(T), Unsafe.SizeOf<T>(), 8, 8);
            var measured = NinoLayout.Compute(typeof(T), Unsafe.SizeOf<T>(), 8, 8, 0, 0, true);
            Assert.AreEqual(model.Canonical, measured.Canonical, typeof(T).Name);
            Assert.AreEqual(model.Size, measured.Size, typeof(T).Name);
            CollectionAssert.AreEqual(model.Mask, measured.Mask, typeof(T).Name);
            CollectionAssert.AreEqual(model.Map, measured.Map, typeof(T).Name);
        }

        [TestMethod]
        public void DateTimeAfterAnIntIsWrittenInTheCanonicalLayout()
        {
            var value = new HoldsDateTime { A = 1, B = new System.DateTime(0x0102030405060708L) };
            var expected = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1 };
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(value));
            Assert.AreEqual(value.B, NinoDeserializer.Deserialize<HoldsDateTime>(expected).B);
            var array = NinoSerializer.Serialize(new[] { value, value });
            CollectionAssert.AreEqual(expected.Concat(expected).ToArray(), array.Skip(4).ToArray());
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
