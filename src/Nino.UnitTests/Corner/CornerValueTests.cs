using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Corner;

#nullable disable
namespace Nino.UnitTests.Corner
{
    public enum EByte : byte { Min = byte.MinValue, Max = byte.MaxValue }
    public enum ESByte : sbyte { Min = sbyte.MinValue, Max = sbyte.MaxValue }
    public enum EShort : short { Min = short.MinValue, Max = short.MaxValue }
    public enum EUShort : ushort { Min = ushort.MinValue, Max = ushort.MaxValue }
    public enum EInt { Min = int.MinValue, Max = int.MaxValue }
    public enum EUInt : uint { Min = uint.MinValue, Max = uint.MaxValue }
    public enum ELong : long { Min = long.MinValue, Max = long.MaxValue }
    public enum EULong : ulong { Min = ulong.MinValue, Max = ulong.MaxValue }

    [Flags]
    public enum EFlags : ulong
    {
        None = 0,
        A = 1,
        B = 1UL << 31,
        C = 1UL << 63,
        All = A | B | C,
    }

    [NinoType]
    public class EnumHolder
    {
        public EByte B;
        public ESByte SB;
        public EShort S;
        public EUShort US;
        public EInt I;
        public EUInt UI;
        public ELong L;
        public EULong UL;
        public EFlags F;
        public EFlags? NF;
        public EFlags[] Fs;
        public List<ESByte> SBs;
        public Dictionary<EUShort, ELong> Map;
    }

    [NinoType]
    public class ScalarHolder
    {
        public bool T, F;
        public char C0, CMax, CSurrogate;
        public decimal D;
        public DateTime When;
        public DateTimeOffset Offset;
        public Guid Id;
        public TimeSpan Span;
        public nint N;
        public nuint UN;
        public nint[] Ns;
        public double NaN, NegZero;
        public float FInf;
#if NET5_0_OR_GREATER
        public Half H;
        public Half[] Hs;
#endif
#if NET7_0_OR_GREATER
        public Int128 Big;
        public UInt128 UBig;
        public DateOnly Date;
        public TimeOnly Time;
#endif
    }

    // Written member by member in this (version tolerant) assembly: the same values must round-trip.
    [NinoType]
    public struct MemberwisePadded
    {
        public byte A;
        public long B;
        public short C;
    }

    [NinoType]
    public class StringHolder
    {
        public string Utf16;
        [NinoUtf8] public string Utf8;
        [NinoUtf8] public string Utf8Prop { get; set; }
        public List<string> List;
        public string[] Array;
    }

    [TestClass]
    public class CornerValueTests
    {
        // A value whose padding holds garbage: fill with 0xAB, then assign the fields (which leaves the padding).
        private static T Dirty<T>(Func<T, T> assign) where T : unmanaged
        {
            T value = default;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<T, byte>(ref value), 0xAB, (uint)Unsafe.SizeOf<T>());
            return assign(value);
        }

        [TestMethod]
        public void RawStructPaddingIsZeroedAndBytesAreCanonical()
        {
            var clean = new RawPadded { A = 1, B = 2, C = 3, D = 4 };
            var dirty = Dirty<RawPadded>(v =>
            {
                v.A = 1;
                v.B = 2;
                v.C = 3;
                v.D = 4;
                return v;
            });
            var bytes = NinoSerializer.Serialize(dirty);
            // byte A @0, int B @4, short C @8, long D @16: 24 bytes, padding zero.
            CollectionAssert.AreEqual(new byte[]
            {
                1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0,
            }, bytes);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(clean), bytes);
            Assert.AreEqual(clean, NinoDeserializer.Deserialize<RawPadded>(bytes));

            var nested = Dirty<RawNested>(v =>
            {
                v.Inner.A = 5;
                v.Inner.B = 0;
                v.Inner.C = 0;
                v.Inner.D = 6;
                v.Tail = 7;
                v.Second.A = 0;
                v.Second.B = 8;
                v.Second.C = 0;
                v.Second.D = 0;
                return v;
            });
            var nestedBytes = NinoSerializer.Serialize(nested);
            CollectionAssert.DoesNotContain(nestedBytes, (byte)0xAB);
            Assert.AreEqual(Unsafe.SizeOf<RawNested>(), nestedBytes.Length);
            var nestedBack = NinoDeserializer.Deserialize<RawNested>(nestedBytes);
            Assert.AreEqual(5, nestedBack.Inner.A);
            Assert.AreEqual(7, nestedBack.Tail);
            Assert.AreEqual(8, nestedBack.Second.B);

            // Arrays, lists, nullable and members holding them.
            var array = new[] { dirty, dirty };
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(array), (byte)0xAB);
            CollectionAssert.AreEqual(array, Rt.RoundTrip(array));
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(new List<RawNested> { nested }), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize((RawNested?)nested), (byte)0xAB);
            CollectionAssert.DoesNotContain(NinoSerializer.Serialize(new Box<RawNested> { Item = nested }), (byte)0xAB);
        }

        [TestMethod]
        public void PackedExplicitAndUnionLayouts()
        {
            var packed = Dirty<RawPacked>(v =>
            {
                v.A = 1;
                v.B = long.MinValue;
                v.C = -1;
                return v;
            });
            var packedBytes = NinoSerializer.Serialize(packed);
            Assert.AreEqual(11, packedBytes.Length);
            Assert.AreEqual(packed, NinoDeserializer.Deserialize<RawPacked>(packedBytes));

            var exp = Dirty<RawExplicit>(v =>
            {
                v.A = 1;
                v.B = 2;
                return v;
            });
            var expBytes = NinoSerializer.Serialize(exp);
            CollectionAssert.DoesNotContain(expBytes, (byte)0xAB);
            var expBack = NinoDeserializer.Deserialize<RawExplicit>(expBytes);
            Assert.AreEqual(1, expBack.A);
            Assert.AreEqual(2, expBack.B);
            CollectionAssert.AreEqual(expBytes, NinoSerializer.Serialize(new RawExplicit { A = 1, B = 2 }));

            var union = new RawUnion { Whole = 0x1122334455667788 };
            var unionBack = Rt.RoundTrip(union);
            Assert.AreEqual(union.Whole, unionBack.Whole);
            Assert.AreEqual(union.Low, unionBack.Low);
            Assert.AreEqual(union.High, unionBack.High);
            CollectionAssert.AreEqual(new[] { union }, Rt.RoundTrip(new[] { union }));

            var memberwise = new MemberwisePadded { A = 255, B = -1, C = short.MinValue };
            Assert.AreEqual(memberwise, Rt.RoundTrip(memberwise));
            CollectionAssert.AreEqual(new[] { memberwise, default }, Rt.RoundTrip(new[] { memberwise, default }));
        }

        [TestMethod]
        public void EnumsOfEveryUnderlyingType()
        {
            var value = new EnumHolder
            {
                B = EByte.Max, SB = ESByte.Min, S = EShort.Min, US = EUShort.Max, I = EInt.Min, UI = EUInt.Max,
                L = ELong.Min, UL = EULong.Max, F = EFlags.All, NF = EFlags.B | EFlags.C,
                Fs = new[] { EFlags.None, EFlags.C, (EFlags)12345 }, SBs = new List<ESByte> { (ESByte)(-5), ESByte.Max },
                Map = new Dictionary<EUShort, ELong> { [EUShort.Min] = ELong.Max, [(EUShort)7] = (ELong)(-7) },
            };
            var back = Rt.RoundTrip(value);
            Assert.AreEqual(EByte.Max, back.B);
            Assert.AreEqual(ESByte.Min, back.SB);
            Assert.AreEqual(EShort.Min, back.S);
            Assert.AreEqual(EUShort.Max, back.US);
            Assert.AreEqual(EInt.Min, back.I);
            Assert.AreEqual(EUInt.Max, back.UI);
            Assert.AreEqual(ELong.Min, back.L);
            Assert.AreEqual(EULong.Max, back.UL);
            Assert.AreEqual(EFlags.All, back.F);
            Assert.AreEqual(EFlags.B | EFlags.C, back.NF);
            CollectionAssert.AreEqual(value.Fs, back.Fs);
            CollectionAssert.AreEqual(value.SBs, back.SBs);
            CollectionAssert.AreEquivalent(value.Map.ToList(), back.Map.ToList());
            Assert.IsNull(Rt.RoundTrip(new EnumHolder()).NF);

            // Enums are written at their underlying size.
            Assert.AreEqual(1, NinoSerializer.Serialize(ESByte.Min).Length);
            Assert.AreEqual(2, NinoSerializer.Serialize(EUShort.Max).Length);
            Assert.AreEqual(8, NinoSerializer.Serialize(EFlags.C).Length);
            Assert.AreEqual((EFlags)ulong.MaxValue, Rt.RoundTrip((EFlags)ulong.MaxValue));
        }

        [TestMethod]
        public void BoolCharDecimalDateAndOtherBclValues()
        {
            var negZeroDecimal = new decimal(0, 0, 0, true, 0);
            var value = new ScalarHolder
            {
                T = true, F = false, C0 = '\0', CMax = '￿', CSurrogate = '\ud800',
                D = negZeroDecimal,
                When = new DateTime(2024, 2, 29, 23, 59, 59, DateTimeKind.Local).AddTicks(1234567),
                Offset = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(5 * 60 + 45)),
                Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
                Span = TimeSpan.MinValue, N = unchecked((nint)long.MinValue), UN = unchecked((nuint)ulong.MaxValue), Ns = new nint[] { -1, 0, unchecked((nint)long.MaxValue) },
                NaN = double.NaN, NegZero = -0.0, FInf = float.NegativeInfinity,
#if NET5_0_OR_GREATER
                H = Half.MaxValue, Hs = new[] { Half.NaN, Half.Epsilon, (Half)(-0.0f) },
#endif
#if NET7_0_OR_GREATER
                Big = Int128.MinValue, UBig = UInt128.MaxValue, Date = DateOnly.MaxValue, Time = TimeOnly.MaxValue,
#endif
            };
            var back = Rt.RoundTrip(value);
            Assert.IsTrue(back.T);
            Assert.IsFalse(back.F);
            Assert.AreEqual('\0', back.C0);
            Assert.AreEqual('￿', back.CMax);
            Assert.AreEqual('\ud800', back.CSurrogate);
            CollectionAssert.AreEqual(decimal.GetBits(negZeroDecimal), decimal.GetBits(back.D));
            Assert.AreEqual(value.When.Ticks, back.When.Ticks);
            Assert.AreEqual(DateTimeKind.Local, back.When.Kind);
            Assert.AreEqual(value.Offset, back.Offset);
            Assert.AreEqual(value.Offset.Offset, back.Offset.Offset);
            Assert.AreEqual(value.Id, back.Id);
            Assert.AreEqual(TimeSpan.MinValue, back.Span);
            Assert.AreEqual(unchecked((nint)long.MinValue), back.N);
            Assert.AreEqual(unchecked((nuint)ulong.MaxValue), back.UN);
            CollectionAssert.AreEqual(value.Ns, back.Ns);
            Assert.IsTrue(double.IsNaN(back.NaN));
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(back.NegZero));
            Assert.AreEqual(float.NegativeInfinity, back.FInf);
#if NET5_0_OR_GREATER
            Assert.AreEqual(Half.MaxValue, back.H);
            Assert.IsTrue(Half.IsNaN(back.Hs[0]));
            Assert.AreEqual(Half.Epsilon, back.Hs[1]);
            Assert.IsTrue(Half.IsNegative(back.Hs[2]));
            Assert.AreEqual(2, NinoSerializer.Serialize(Half.One).Length);
#endif
#if NET7_0_OR_GREATER
            Assert.AreEqual(Int128.MinValue, back.Big);
            Assert.AreEqual(UInt128.MaxValue, back.UBig);
            Assert.AreEqual(DateOnly.MaxValue, back.Date);
            Assert.AreEqual(TimeOnly.MaxValue, back.Time);
#endif
        }

        [TestMethod]
        public void ExtremeDecimalsDatesAndOffsets()
        {
            foreach (var d in new[]
                     {
                         decimal.MaxValue, decimal.MinValue, decimal.One, 0.0000000000000000000000000001m,
                         1.0000000000000000000000000000m, -79228162514264337593543950335m / 3,
                     })
            {
                CollectionAssert.AreEqual(decimal.GetBits(d), decimal.GetBits(Rt.RoundTrip(d)), d.ToString());
                CollectionAssert.AreEqual(decimal.GetBits(d), decimal.GetBits(Rt.RoundTrip(new[] { d })[0]));
            }

            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local })
            {
                foreach (var dt in new[] { DateTime.MinValue, DateTime.MaxValue, new DateTime(2000, 1, 1) })
                {
                    var withKind = DateTime.SpecifyKind(dt, kind);
                    var back = Rt.RoundTrip(withKind);
                    Assert.AreEqual(withKind.Ticks, back.Ticks);
                    Assert.AreEqual(kind, back.Kind);
                    Assert.AreEqual(kind, Rt.RoundTrip(new List<DateTime> { withKind })[0].Kind);
                }
            }

            foreach (var offset in new[]
                     {
                         DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                         new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.FromHours(14)),
                         new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.FromHours(-14)),
                         new DateTimeOffset(1, 1, 1, 14, 0, 0, TimeSpan.FromHours(14)),
                         new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.FromHours(-12)),
                     })
            {
                var back = Rt.RoundTrip(offset);
                Assert.AreEqual(offset.UtcTicks, back.UtcTicks);
                Assert.AreEqual(offset.Offset, back.Offset);
                Assert.AreEqual(10, NinoSerializer.Serialize(offset).Length);
                var nullable = Rt.RoundTrip<DateTimeOffset?>(offset);
                Assert.AreEqual(offset.Offset, nullable.Value.Offset);
                Assert.AreEqual(offset.Offset, Rt.RoundTrip(new[] { offset })[0].Offset);
            }
        }

        [TestMethod]
        public void StringsUtf16AndUtf8()
        {
            var samples = new[]
            {
                null, "", "a", "\0", "a\0b", "héllo wörld", "日本語テキスト", "\U0001D11E\U0001F600", "é",
                new string('x', 1 << 20), string.Concat(Enumerable.Repeat("😀", 70000)),
            };
            foreach (var s in samples)
            {
                var back = Rt.RoundTrip(new StringHolder { Utf16 = s, Utf8 = s, Utf8Prop = s, List = new List<string> { s }, Array = new[] { s } });
                Assert.AreEqual(s, back.Utf16);
                Assert.AreEqual(s, back.Utf8);
                Assert.AreEqual(s, back.Utf8Prop);
                Assert.AreEqual(s, back.List[0]);
                Assert.AreEqual(s, back.Array[0]);
                Assert.AreEqual(s, Rt.RoundTrip(s));
            }

            // UTF-8 member: byte count prefix, then the bytes.
            var only = new StringHolder { Utf8 = "é😀" };
            var bytes = NinoSerializer.Serialize(only);
            var utf8 = Encoding.UTF8.GetBytes("é😀");
            var at = IndexOf(bytes, utf8);
            Assert.IsTrue(at >= 4);
            Assert.AreEqual(utf8.Length, BitConverter.ToInt32(bytes, at - 4));

            // UTF-16 null is -1, empty is 0.
            CollectionAssert.AreEqual(BitConverter.GetBytes(-1), NinoSerializer.Serialize<string>(null));
            CollectionAssert.AreEqual(BitConverter.GetBytes(0), NinoSerializer.Serialize(""));
        }

        [TestMethod]
        public void LoneSurrogates()
        {
            foreach (var s in new[] { "\ud800", "a\udc00b", "\udbff\ud800", "x\ud83d" })
            {
                // UTF-16 keeps code units as they are.
                Assert.AreEqual(s, Rt.RoundTrip(s));
                Assert.AreEqual(s, Rt.RoundTrip(new StringHolder { Utf16 = s }).Utf16);
                // UTF-8 cannot encode them: each becomes U+FFFD, as Encoding.UTF8 does; it must not throw.
                var utf8 = Rt.RoundTrip(new StringHolder { Utf8 = s }).Utf8;
                Assert.AreEqual(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)), utf8);
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                    return i;
            return -1;
        }
    }
}
