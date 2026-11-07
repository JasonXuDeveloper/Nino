using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

namespace Nino.UnitTests
{
    /// <summary>A plain (raw) struct around a BCL struct, after a byte and before an int.</summary>
    public struct Around<T>
    {
        public byte A;
        public T B;
        public int C;
    }

    public enum SmallEnum : byte
    {
        One = 1,
    }

    /// <summary>A tuple and a DateTimeOffset in a raw struct: their dates and offset are checked in Untrusted mode.</summary>
    public struct HoldsBclValues
    {
        public int A;
        public (byte Tag, DateTime When) T;
        public DateTimeOffset O;
    }

    /// <summary>
    /// BCL structs inside raw structs are written in the canonical layout (64-bit CoreCLR's, NinoLayout), auto-layout
    /// ones (ValueTuple, DateTimeOffset) included: the model of CoreCLR's auto layout must be this runtime's own layout
    /// (so these are copied as they are here), and the bytes must be the canonical ones.
    /// </summary>
    [TestClass]
    public class BclStructLayoutTests
    {
        /// <summary>Instantiations with mixed field sizes, nesting, and every kind of BCL struct inside a tuple.</summary>
        internal static readonly Type[] Inside =
        {
            typeof((int, long)), typeof((long, int)), typeof((byte, short)), typeof((byte, long, int)),
            typeof((byte, int, short)), typeof((short, byte, int)), typeof((int, int)), typeof((int, int, int)),
            typeof((byte, byte, byte)), typeof((bool, char, byte)), typeof((double, float)), typeof((float, double)),
            typeof((SmallEnum, long)), typeof((int, IntPtr)), typeof((byte, UIntPtr)), typeof(ValueTuple<long>),
            typeof(ValueTuple<byte>), typeof((int, DateTime)), typeof((byte, DateTime, int)), typeof((int, TimeSpan)),
            typeof((byte, decimal)), typeof((int, decimal)), typeof((byte, Guid)), typeof((byte, int?)),
            typeof((long?, byte)), typeof((byte, (int, long))), typeof(((byte, short), long)),
            typeof((int, (byte, short), long)), typeof((byte, (byte, short))), typeof((int, KeyValuePair<int, long>)),
            typeof((byte, DateTimeOffset)), typeof((byte, short, int, long, byte, short, int)),
            typeof((byte, long, byte, long, byte, long, byte, int)), typeof((int, (byte, long), (short, (byte, int)))),
            typeof(KeyValuePair<int, long>), typeof(KeyValuePair<byte, long>), typeof(KeyValuePair<long, byte>),
            typeof(KeyValuePair<int, DateTime>), typeof(KeyValuePair<byte, (int, long)>), typeof(DateTimeOffset),
            typeof(DateTime), typeof(TimeSpan), typeof(decimal), typeof(Guid), typeof(int?), typeof(long?),
            typeof((int, long)?), typeof(DateTimeOffset?), typeof(IntPtr), typeof(System.Numerics.Complex),
#if NET8_0_OR_GREATER
            typeof(Int128), typeof((byte, Int128)), typeof(Half), typeof((byte, Half)), typeof(DateOnly),
            typeof(TimeOnly), typeof((byte, DateOnly, TimeOnly)), typeof(Range), typeof(Index),
            typeof(System.Numerics.Vector3), typeof(System.Numerics.Quaternion), typeof(System.Numerics.Matrix3x2),
            typeof(System.Numerics.Matrix4x4), typeof(System.Numerics.Plane), typeof(System.Text.Rune),
            // Aligned to their size by 64-bit CoreCLR on x64 and Arm64 (Vector256 and Vector512: x64 only, below).
            typeof(System.Runtime.Intrinsics.Vector64<int>), typeof(System.Runtime.Intrinsics.Vector128<int>),
            typeof((byte, System.Runtime.Intrinsics.Vector128<long>)),
#endif
        };

        private static NinoLayout.Info Layout(Type type) =>
            (NinoLayout.Info)typeof(BclStructLayoutTests).GetMethod(nameof(LayoutOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, null)!;

        private static NinoLayout.Info LayoutOf<T>() => NinoLayout.Get(typeof(T), Unsafe.SizeOf<T>());

        [TestMethod]
        public void CoreClrKeepsTheCanonicalLayout()
        {
            // 64-bit CoreCLR lays these out canonically: copied as they are (the speed of x64 and Arm64), and the
            // model of its auto layout (NinoLayoutCore.PlaceAuto) agrees with what it measured, field by field.
            Assert.AreEqual(8, IntPtr.Size);
            var differ = new List<string>();
            foreach (var type in Inside)
            {
                var around = typeof(Around<>).MakeGenericType(type);
                foreach (var t in Nullable.GetUnderlyingType(type) == null ? new[] { type, around } : new[] { around })
                {
                    var info = Layout(t);
                    // .NET 8 has a short for DateTimeOffset's minutes, .NET 9+ the canonical int.
                    bool dateTimeOffset = t.ToString().Contains("DateTimeOffset");
                    bool expected = !dateTimeOffset || ValueViews.DateTimeOffsetMinutesSize == 4;
                    if (info.Size == 0 || info.Canonical != expected)
                        differ.Add($"{t}: size {info.Size}, canonical {info.Canonical}");
                }
            }

            Assert.AreEqual(0, differ.Count, string.Join("\n", differ));
        }

        [TestMethod]
        public void TuplesUseCoreClrAutoLayout()
        {
            // (int, long): the long first, then the int, 16 bytes; after a byte at 8; the int C after it at 24.
            var value = new Around<(int, long)> { A = 1, B = (0x22222222, 0x3333333333333333), C = 0x44444444 };
            CollectionAssert.AreEqual(Rt.FromHex(
                "01000000 00000000 33333333 33333333 22222222 00000000 44444444 00000000"),
                NinoSerializer.Serialize(value));
            // (byte, short): the short, then the byte, 4 bytes aligned to 4.
            var small = new Around<(byte, short)> { A = 1, B = (2, 0x0303), C = 4 };
            CollectionAssert.AreEqual(Rt.FromHex("01000000 03030200 04000000"), NinoSerializer.Serialize(small));
            var back = NinoDeserializer.Deserialize<Around<(byte, short)>>(NinoSerializer.Serialize(small));
            Assert.AreEqual(small.B, back.B);
        }

        [TestMethod]
        public void DateTimeOffsetUsesItsCanonicalLayout()
        {
            // Its offset in minutes as an int (sign-extended, whatever this runtime keeps), padding, then the UTC date.
            var o = new DateTimeOffset(0x0102030405060708L, TimeSpan.FromMinutes(-90));
            var value = new Around<DateTimeOffset> { A = 1, B = o, C = 2 };
            var utc = BitConverter.GetBytes(o.UtcTicks);
            var expected = Rt.FromHex("01000000 00000000 A6FFFFFF 00000000").Concat(utc).Concat(Rt.FromHex("02000000 00000000"))
                .ToArray();
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(expected, bytes);
            var back = NinoDeserializer.Deserialize<Around<DateTimeOffset>>(bytes);
            Assert.AreEqual(o, back.B);
            Assert.AreEqual(o.Offset, back.B.Offset);
            var array = NinoDeserializer.Deserialize<Around<DateTimeOffset>[]>(NinoSerializer.Serialize(new[] { value, value }));
            Assert.AreEqual(o.Offset, array[1].B.Offset);
        }

        [TestMethod]
        public void ValuesRoundTrip()
        {
            var value = new Around<(int, (byte, long), (short, (byte, int)))>
                { A = 3, B = (-1, (2, -3), (-4, (5, -6))), C = 7 };
            Assert.AreEqual(value.B, NinoDeserializer.Deserialize<Around<(int, (byte, long), (short, (byte, int)))>>(
                NinoSerializer.Serialize(value)).B);
            var nullable = new Around<(long?, byte)> { A = 1, B = (-9, 2), C = 3 };
            Assert.AreEqual(nullable.B, NinoDeserializer.Deserialize<Around<(long?, byte)>>(
                NinoSerializer.Serialize(nullable)).B);
            var pair = new Around<KeyValuePair<byte, (int, long)>>
                { B = new KeyValuePair<byte, (int, long)>(4, (5, 6)) };
            Assert.AreEqual(pair.B, NinoDeserializer.Deserialize<Around<KeyValuePair<byte, (int, long)>>>(
                NinoSerializer.Serialize(pair)).B);
        }

        [TestMethod]
        public void AutoLayoutRule()
        {
            // The model alone, against what CoreCLR's type loader does (and crossgen2 computes) on 64-bit.
            Assert.AreEqual(16, Layout(typeof((int, int, int))).Size);
            Assert.AreEqual(8, Layout(typeof((int, int))).Align);
            Assert.AreEqual(4, Layout(typeof((byte, short))).Align);
            Assert.AreEqual(16, Layout(typeof((byte, int?))).Size);
            Assert.AreEqual(24, Layout(typeof((byte, Guid))).Size);
            Assert.AreEqual(16, Layout(typeof(DateTimeOffset)).Size);
        }

#if NET8_0_OR_GREATER
        [TestMethod]
        public void BclValuesInRawStructsAreChecked() =>
            SettingsTests.RunProbe(nameof(SettingsProbes.UntrustedBclLayouts));
#endif

#if NET8_0_OR_GREATER
        [TestMethod]
        public void IntrinsicVectorsAreAlignedAsOnX64()
        {
            // Vector256 aligned to 32 (x64 CoreCLR; Arm64 aligns it to 16 and converts): A at 0, the vector at 32, C at 64.
            var value = new Around<System.Runtime.Intrinsics.Vector256<long>>
                { A = 1, B = System.Runtime.Intrinsics.Vector256.Create(1L, -2, 3, -4), C = -1 };
            var bytes = NinoSerializer.Serialize(value);
            Assert.AreEqual(96, bytes.Length);
            Assert.AreEqual(1L, BitConverter.ToInt64(bytes, 32));
            Assert.AreEqual(-4L, BitConverter.ToInt64(bytes, 56));
            Assert.AreEqual(-1, BitConverter.ToInt32(bytes, 64));
            Assert.AreEqual(value.B, NinoDeserializer.Deserialize<Around<System.Runtime.Intrinsics.Vector256<long>>>(bytes).B);
            var wide = new Around<System.Runtime.Intrinsics.Vector512<long>>
                { A = 1, B = System.Runtime.Intrinsics.Vector512.Create(7L), C = -1 };
            bytes = NinoSerializer.Serialize(wide);
            Assert.AreEqual(192, bytes.Length);
            Assert.AreEqual(7L, BitConverter.ToInt64(bytes, 64));
            Assert.AreEqual(-1, BitConverter.ToInt32(bytes, 128));
            Assert.AreEqual(wide.B, NinoDeserializer.Deserialize<Around<System.Runtime.Intrinsics.Vector512<long>>>(bytes).B);
            bool x64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                       System.Runtime.InteropServices.Architecture.X64;
            Assert.AreEqual(x64, Layout(typeof(Around<System.Runtime.Intrinsics.Vector256<long>>)).Canonical);
        }
#endif
    }
}

#if NET8_0_OR_GREATER
namespace Nino.UnitTests
{
    public static partial class SettingsProbes
    {
        public static string UntrustedBclLayouts(byte[] _)
        {
            NinoSettings.Untrusted = true;
            var value = new HoldsBclValues
            {
                A = 1, T = (2, new DateTime(0x0123456789ABCDEF, DateTimeKind.Utc)),
                O = new DateTimeOffset(637_000_000_000_000_000, TimeSpan.FromMinutes(-150)),
            };
            // A at 0; the tuple at 8 (its byte, then its date at 16); the DateTimeOffset at 24 (minutes, then UTC at 32).
            var data = NinoSerializer.Serialize(value);
            if (data.Length != 40)
                return "size " + data.Length;
            var back = NinoDeserializer.Deserialize<HoldsBclValues>(data);
            if (back.T != value.T || back.O != value.O || back.O.Offset != value.O.Offset)
                return "value";
            var array = NinoSerializer.Serialize(new[] { value, value, value });
            if (NinoDeserializer.Deserialize<HoldsBclValues[]>(array)[2].O.Offset != value.O.Offset)
                return "array";
            var bad = new List<(string, byte[])>
            {
                ("ticks past the maximum in a tuple", With(data, 16, BitConverter.GetBytes(BadTicks))),
                ("an offset of 15 hours", With(data, 24, BitConverter.GetBytes(15 * 60))),
                ("an offset past a short", With(data, 24, BitConverter.GetBytes(0x10000))),
                ("a UTC date past the maximum", With(data, 32, BitConverter.GetBytes(DateTime.MaxValue.Ticks + 1))),
                ("a local date past the maximum", With(With(data, 32, BitConverter.GetBytes(DateTime.MaxValue.Ticks)),
                    24, BitConverter.GetBytes(60))),
            };
            foreach (var (what, bytes) in bad)
            {
                if (Fails<HoldsBclValues>(bytes, what) is { } single)
                    return single;
                var many = (byte[])array.Clone();
                Array.Copy(bytes, 0, many, 4 + 40, 40);
                if (Fails<HoldsBclValues[]>(many, what + " (array)") is { } run)
                    return run;
            }

            return "ok";
        }
    }
}
#endif
