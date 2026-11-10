using System;
using System.Buffers.Binary;
using System.Collections;
using System.Runtime.CompilerServices;
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
        [DataRow(16)]
        [DataRow(31)]
        [DataRow(32)]
        [DataRow(33)]
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
