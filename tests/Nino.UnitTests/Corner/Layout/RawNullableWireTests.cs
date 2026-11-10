using System;
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// A <c>T?</c> sequence of a raw [NinoType] struct is written at the fixed stride of its layout (a has-value byte,
    /// padding, then the value, zero when null) whoever binds the formatter: the generated code of a member, or the
    /// runtime resolver for a type no generated code names (<c>Point?[]</c> here, through its Type).
    /// Compiled on its own: this assembly's structs are version tolerant, so not raw.
    /// </summary>
    [TestClass]
    public class RawNullableWireTests
    {
        private const string Source = @"
using System.Collections.Generic;
using Nino.Core;

namespace RawWire
{
    [NinoType]
    public struct Point { public int X; public int Y; }

    [NinoType]
    public sealed class Holder { public List<Point?> List; }

    [NinoType]
    public sealed class PlainHolder { public List<Point> List; }

    public static class Use
    {
        public static byte[] Generated() =>
            NinoSerializer.Serialize(new Holder { List = new List<Point?> { new Point { X = 3, Y = 4 }, null } });

        public static Point?[] Values() => new Point?[] { new Point { X = 3, Y = 4 }, null };

        public static byte[] GeneratedPlain() =>
            NinoSerializer.Serialize(new PlainHolder { List = new List<Point> { new Point { X = 3, Y = 4 } } });

        public static Point[] PlainValues() => new Point[] { new Point { X = 3, Y = 4 } };
    }
}";

        // Length 2, then { has-value, 3 bytes of padding, X, Y } per element; the null one all zero.
        private static readonly byte[] Expected = Rt.FromHex(
            "02000000" + "01000000" + "03000000" + "04000000" + "00000000" + "00000000" + "00000000");

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(7)]
        [DataRow(8)]
        [DataRow(9)]
        [DataRow(15)]
        [DataRow(16)]
        [DataRow(17)]
        [DataRow(31)]
        [DataRow(32)]
        [DataRow(33)]
        [DataRow(63)]
        [DataRow(64)]
        [DataRow(65)]
        [DataRow(129)]
        public void NullableBoolWritesZeroForNullSlots(int count)
        {
            var values = new bool?[count];
            var expected = new byte[sizeof(int) + count * 2];
            BinaryPrimitives.WriteInt32LittleEndian(expected, count);
            for (int i = 0; i < count; i++)
            {
                if (i % 3 == 0)
                {
                    Unsafe.Add(ref Unsafe.As<bool?, byte>(ref values[i]), NullableLayout<bool>.ValueOffset) = 0x7F;
                    continue;
                }

                values[i] = i % 3 == 1;
                expected[sizeof(int) + i * 2] = 1;
                expected[sizeof(int) + i * 2 + 1] = values[i].GetValueOrDefault() ? (byte)1 : (byte)0;
            }

            using var buffer = new NinoArrayBufferWriter();
            buffer.GetSpan(1)[0] = 0xA5;
            buffer.Advance(1);
            NinoSerializer.Serialize(values, buffer);
            Assert.AreEqual((byte)0xA5, buffer.WrittenSpan[0]);
            CollectionAssert.AreEqual(expected, buffer.WrittenSpan.Slice(1).ToArray());
        }

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        private delegate void BoolSlotsKernel(ref ushort source, ref ushort target, int count);

        [TestMethod]
        [DataRow(64)]
        [DataRow(128)]
        [DataRow(256)]
        [DataRow(512)]
        public void NullableBoolVectorWidthsPreserveUnalignedTails(int width)
        {
            var vector = width switch
            {
                64 => typeof(Vector64<ushort>),
                128 => typeof(Vector128<ushort>),
                256 => typeof(Vector256<ushort>),
                _ => typeof(Vector512<ushort>),
            };
            var owner = typeof(NinoNullables<bool>);
            var slots = owner.GetNestedType("BoolSlots" + width, BindingFlags.NonPublic);
            if (slots.ContainsGenericParameters)
                slots = slots.MakeGenericType(typeof(bool));
            var write = owner.GetMethod("WriteBoolSlots", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(vector, slots).CreateDelegate<BoolSlotsKernel>();
            var read = owner.GetMethod("ReadBoolSlots", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(vector, slots).CreateDelegate<BoolSlotsKernel>();
            int lanes = width / 16;
            for (int count = lanes; count <= lanes * 3 + 1; count++)
            foreach (int offset in new[] { 0, 1, 7 })
            {
                var source = new ushort[count];
                var expected = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    source[i] = (i % 3) switch { 0 => 0x7F00, 1 => 0x0101, _ => 0x0001 };
                    expected[i] = i % 3 == 0 ? (ushort)0 : source[i];
                }
                foreach (var kernel in new[] { write, read })
                {
                    var target = new byte[offset + count * 2 + 16];
                    Array.Fill(target, (byte)0xA5);
                    kernel(ref source[0], ref Unsafe.As<byte, ushort>(ref target[offset]), count);
                    for (int i = 0; i < count; i++)
                        Assert.AreEqual(expected[i], Unsafe.ReadUnaligned<ushort>(ref target[offset + i * 2]));
                    for (int i = 0; i < offset; i++)
                        Assert.AreEqual((byte)0xA5, target[i]);
                    for (int i = offset + count * 2; i < target.Length; i++)
                        Assert.AreEqual((byte)0xA5, target[i]);
                }

                if (!BitConverter.IsLittleEndian || NullableLayout<bool>.HasValueOffset != 0 ||
                    NullableLayout<bool>.ValueOffset != 1 || Unsafe.SizeOf<bool?>() != 2 ||
                    count != lanes + 1 && count != lanes * 2 + 1)
                    continue;
                for (int invalid = 0; invalid < count; invalid++)
                {
                    var target = new byte[offset + count * 2 + 16];
                    Array.Fill(target, (byte)0xA5);
                    for (int i = 0; i < count; i++)
                        Unsafe.WriteUnaligned(ref target[offset + i * 2], (ushort)0x0101);
                    ushort old = source[invalid];
                    source[invalid] = 0x0201;
                    try
                    {
                        Assert.ThrowsExactly<NinoDataException>(() =>
                            read(ref source[0], ref Unsafe.As<byte, ushort>(ref target[offset]), count));
                        for (int i = 0; i < count; i++)
                            Assert.AreEqual(i < invalid ? expected[i] : (ushort)0x0101,
                                Unsafe.ReadUnaligned<ushort>(ref target[offset + i * 2]));
                    }
                    finally
                    {
                        source[invalid] = old;
                    }
                }
            }
        }
#endif

        [TestMethod]
        [DataRow(4, false)]
        [DataRow(7, false)]
        [DataRow(8, false)]
        [DataRow(16, false)]
        [DataRow(32, false)]
        [DataRow(33, false)]
        [DataRow(4, true)]
        [DataRow(7, true)]
        [DataRow(8, true)]
        [DataRow(16, true)]
        [DataRow(32, true)]
        [DataRow(33, true)]
        public void NullableBoolOverlappingReadsKeepScalarOrder(int count, bool destinationAfterSource)
        {
            if (!BitConverter.IsLittleEndian || NullableLayout<bool>.HasValueOffset != 0 ||
                NullableLayout<bool>.ValueOffset != 1 || Unsafe.SizeOf<bool?>() != 2)
                Assert.Inconclusive("The nullable bool memory layout differs from the wire layout.");
            var storage = new bool?[count + 1];
            for (int i = 0; i < storage.Length; i++)
                storage[i] = i % 3 == 0 ? true : i % 3 == 1 ? false : null;
            int source = destinationAfterSource ? 0 : 1;
            int target = destinationAfterSource ? 1 : 0;
            var expected = new bool?[count];
            if (destinationAfterSource)
                Array.Fill(expected, storage[source]);
            else
                storage.AsSpan(source, count).CopyTo(expected);

            var data = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<bool?, byte>(ref storage[source]), count * 2);
            var reader = new Reader(data);
            NinoNullables<bool>.ReadPrechecked(storage.AsSpan(target, count), ref reader);
            CollectionAssert.AreEqual(expected, storage.AsSpan(target, count).ToArray());
            Assert.AreEqual(0, reader.Remaining);
        }

        [TestMethod]
        public void GeneratedAndRuntimeFormattersWriteTheSameBytes()
        {
            var compiled = CornerCompiler.Compile(Source);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            var use = compiled.Assembly.GetType("RawWire.Use");
            var arrayType = typeof(Nullable<>).MakeGenericType(compiled.Assembly.GetType("RawWire.Point")).MakeArrayType();

            // The generated member: the holder's null flag, then the list.
            var generated = (byte[])use.GetMethod("Generated").Invoke(null, null);
            CollectionAssert.AreEqual(Expected, generated.AsSpan(1).ToArray());

            // The runtime resolver, which no generated code names Point?[] for.
            var values = use.GetMethod("Values").Invoke(null, null);
            var boxed = NinoSerializer.SerializeBoxed(values, arrayType);
            CollectionAssert.AreEqual(Expected, boxed);
            CollectionAssert.AreEqual((ICollection)values, (ICollection)NinoDeserializer.DeserializeBoxed(Expected, arrayType));

            // Not nullable: one raw block either way (unchanged).
            var plainType = compiled.Assembly.GetType("RawWire.Point").MakeArrayType();
            var plain = (byte[])use.GetMethod("GeneratedPlain").Invoke(null, null);
            var plainValues = use.GetMethod("PlainValues").Invoke(null, null);
            CollectionAssert.AreEqual(Rt.FromHex("01000000" + "03000000" + "04000000"), plain.AsSpan(1).ToArray());
            CollectionAssert.AreEqual(plain.AsSpan(1).ToArray(), NinoSerializer.SerializeBoxed(plainValues, plainType));
        }
    }
}
