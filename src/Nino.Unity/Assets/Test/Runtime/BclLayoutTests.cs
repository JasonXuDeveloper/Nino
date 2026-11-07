using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core;
using NUnit.Framework;
using UnityEngine;

// A formatter of the user's whose parameter is [NinoRaw]: the generated registry registers that mark, which IL2CPP,
// keeping no attributes on generic parameters, binds it by.
[assembly: NinoFormatter(typeof(Test.Runtime.CellFormatter<>))]

namespace Test.Runtime
{
    /// <summary>One value, or null: a type Nino has no formatter for but <see cref="CellFormatter{T}"/>.</summary>
    public sealed class Cell<T>
    {
        public T Value;
    }

    /// <summary>A presence byte, then the value as raw bytes: only for types written raw ([NinoRaw]).</summary>
    public struct CellFormatter<[NinoRaw] T> : INinoFormatter<Cell<T>> where T : unmanaged
    {
        public void Serialize(Cell<T> value, ref Writer writer)
        {
            writer.Write(value != null);
            if (value != null)
                writer.WriteValue(value.Value);
        }

        public void Deserialize(ref Reader reader, ref Cell<T> value)
        {
            reader.Read(out bool present);
            value = present ? new Cell<T> { Value = reader.ReadValue<T>() } : null;
        }
    }

    /// <summary>
    /// BCL structs inside plain structs (value tuples, pairs, DateTimeOffset, nint, Nullable) and tuples and pairs on
    /// their own: this runtime (Mono in the editor, IL2CPP in a player) must write the bytes 64-bit CoreCLR writes (the
    /// canonical layout: <see cref="BclFixtures"/>, which tests/Nino.RuntimeTests writes on CoreCLR x64 and checks on
    /// every other runtime) and read them back. It also logs how this runtime lays the tuples out (docs/layouts.md).
    /// </summary>
    public class BclLayoutTests
    {
        private static readonly DateTime Dt1 = new DateTime(0x0123456789ABCDEF & 0x0FFFFFFFFFFFFFFF, DateTimeKind.Utc);
        private static readonly DateTimeOffset Dto1 = new DateTimeOffset(637_000_000_000_000_000, TimeSpan.FromMinutes(-150));
        private static readonly Guid G1 = new Guid(0x01020304, 0x0506, 0x0708, 9, 10, 11, 12, 13, 14, 15, 16);

        [Test]
        public void CoreClrBytesBothWays()
        {
            Debug.Log($"Nino layouts ({Application.platform}, {RuntimeName()}): " + Layouts());
            var failed = new List<string>();
            void Check<T>(string name, T value)
            {
                try
                {
                    var expected = Hex(BclFixtures.Bytes[name]);
                    byte[] bytes = NinoSerializer.Serialize(value);
                    if (!bytes.SequenceEqual(expected))
                        failed.Add($"{name} writes {BitConverter.ToString(bytes)}");
                    byte[] again = NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(expected));
                    if (!again.SequenceEqual(expected))
                        failed.Add($"{name} reads back as {BitConverter.ToString(again)}");
                }
                catch (Exception e)
                {
                    failed.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }

            Check("Around<(int,long)>[]", new[] { new Around<(int, long)> { A = 1, B = (-1, long.MinValue), C = -1 }, new Around<(int, long)> { A = 0xFE, B = (2, 0x0102030405060708), C = int.MaxValue } });
            Check("Around<(byte,short)>", new Around<(byte, short)> { A = 1, B = (1, -2), C = -1 });
            Check("Around<(byte,long,int)>", new Around<(byte, long, int)> { A = 1, B = (7, -8, 9), C = -1 });
            Check("Around<(int,int,int)>", new Around<(int, int, int)> { A = 1, B = (1, 2, 3), C = -1 });
            Check("Around<(bool,char,byte)>", new Around<(bool, char, byte)> { A = 1, B = (true, 'x', 3), C = -1 });
            Check("Around<(int,DateTime)>", new Around<(int, DateTime)> { A = 1, B = (1, Dt1), C = -1 });
            Check("Around<(byte,decimal)>", new Around<(byte, decimal)> { A = 1, B = (1, 1.5m), C = -1 });
            Check("Around<(byte,Guid)>", new Around<(byte, Guid)> { A = 1, B = (1, G1), C = -1 });
            Check("Around<(byte,int?)>", new Around<(byte, int?)> { A = 1, B = (1, null), C = -1 });
            Check("Around<(byte,(int,long))>", new Around<(byte, (int, long))> { A = 1, B = (1, (2, 3L)), C = -1 });
            Check("Around<(int,(byte,short),long)>", new Around<(int, (byte, short), long)> { A = 1, B = (1, (2, 3), 4L), C = -1 });
            Check("Around<(int,IntPtr)>", new Around<(int, IntPtr)> { A = 1, B = (1, new IntPtr(-2)), C = -1 });
            Check("Around<(byte,DateTimeOffset)>", new Around<(byte, DateTimeOffset)> { A = 1, B = (1, Dto1), C = -1 });
            Check("Around<(byte,long,byte,long,byte,long,byte,int)>", new Around<(byte, long, byte, long, byte, long, byte, int)> { A = 1, B = (1, 2L, 3, 4L, 5, 6L, 7, 8), C = -1 });
            Check("Around<KVP<int,long>>", new Around<KeyValuePair<int, long>> { A = 1, B = new KeyValuePair<int, long>(1, -2), C = -1 });
            Check("Around<KVP<long,byte>>", new Around<KeyValuePair<long, byte>> { A = 1, B = new KeyValuePair<long, byte>(-1, 2), C = -1 });
            Check("Around<DateTimeOffset>", new Around<DateTimeOffset> { A = 1, B = Dto1, C = -1 });
            Check("Around<decimal>", new Around<decimal> { A = 1, B = 1.5m, C = -1 });
            Check("Around<IntPtr>", new Around<IntPtr> { A = 1, B = new IntPtr(-1), C = -1 });
            Check("Around<(int,long)?>", new Around<(int, long)?> { A = 1, B = (1, 2L), C = -1 });
            Check("(int,long)[]", new[] { (1, 2L), (-3, long.MinValue) });
            Check("List<(byte,long,int)>", new List<(byte, long, int)> { (1, 2, 3), (255, -1, int.MaxValue) });
            Check("KVP<int,long>[]", new[] { new KeyValuePair<int, long>(1, 2), new KeyValuePair<int, long>(-3, -4) });
            Check("Dictionary<int,(byte,long)>", new Dictionary<int, (byte, long)> { [1] = (2, 3), [-4] = (5, -6) });
            CollectionAssert.IsEmpty(failed);
        }

        [Test]
        public void UserRawFormatterIsBoundByItsMarks()
        {
            // Resolved at run time (Unity's compiler has no interceptors): bound to a raw long...
            var bytes = NinoSerializer.Serialize(new Cell<long> { Value = -7 });
            Assert.AreEqual("01-F9-FF-FF-FF-FF-FF-FF-FF", BitConverter.ToString(bytes));
            Assert.AreEqual(-7L, NinoDeserializer.Deserialize<Cell<long>>(bytes).Value);
            // ...and never to a value tuple, which is not raw: IL2CPP bound it before the generated registry
            // registered the [NinoRaw] mark, and wrote the tuple's memory in its own layout.
            Assert.Throws<InvalidOperationException>(() =>
                NinoSerializer.Serialize(new Cell<(byte, long, int)> { Value = (1, 2, 3) }));
        }

        [Test]
        public void VectorTIsRefusedAndFixedLanesReplaceIt()
        {
            // Vector<T> is 16 bytes here and 32 on x64 with AVX2: Nino refuses it, alone or inside (NINO029, suppressed
            // here to reach the run-time check).
#pragma warning disable NINO029
            Assert.Throws<NotSupportedException>(() => NinoSerializer.Serialize(new System.Numerics.Vector<int>(7)));
            Assert.Throws<NotSupportedException>(() =>
                NinoDeserializer.Deserialize<Around<System.Numerics.Vector<int>>[]>(new byte[64]));
#pragma warning restore NINO029
            // Its replacement writes CoreCLR x64's bytes (BclFixtures) whatever this CPU's Vector<float>.Count.
            Debug.Log($"Vector<float>.Count {System.Numerics.Vector<float>.Count}");
            var bytes = NinoSerializer.Serialize(LaneParticle.Doubled(3));
            Assert.AreEqual(BclFixtures.Bytes["LaneParticle"], BitConverter.ToString(bytes).Replace("-", ""));
            Assert.AreEqual(30f, NinoDeserializer.Deserialize<LaneParticle>(bytes).Lanes[15]);
        }

        /// <summary>Offsets of some tuples' items and of DateTimeOffset's fields on this runtime.</summary>
        private static string Layouts()
        {
            var a = default((int, long));
            var b = default((byte, short));
            var c = default((byte, long, int));
            var views = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.ValueViews");
            string Field(string name) => views?.GetField(name, BindingFlags.Static | BindingFlags.Public)?.GetValue(null)?.ToString();
            var marks = typeof(NinoSerializer).Assembly.GetType("Nino.Core.CompilerServices.NinoParameterMarks");
            return $"attributes of generic parameters visible: {marks?.GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)}; " +
                   $"(int,long) {Unsafe.SizeOf<(int, long)>()}: {Offset(ref a, ref a.Item1)},{Offset(ref a, ref a.Item2)}; " +
                   $"(byte,short) {Unsafe.SizeOf<(byte, short)>()}: {Offset(ref b, ref b.Item1)},{Offset(ref b, ref b.Item2)}; " +
                   $"(byte,long,int) {Unsafe.SizeOf<(byte, long, int)>()}: {Offset(ref c, ref c.Item1)},{Offset(ref c, ref c.Item2)},{Offset(ref c, ref c.Item3)}; " +
                   $"KVP<int,long> {Unsafe.SizeOf<KeyValuePair<int, long>>()}; " +
                   $"DateTimeOffset {Unsafe.SizeOf<DateTimeOffset>()}: UTC at {Field("DateTimeOffsetUtc")}, minutes at {Field("DateTimeOffsetMinutes")} ({Field("DateTimeOffsetMinutesSize")} bytes); " +
                   $"Around<(int,long)> {Unsafe.SizeOf<Around<(int, long)>>()}";
        }

        private static long Offset<TS, TF>(ref TS s, ref TF f) =>
            (long)Unsafe.ByteOffset(ref Unsafe.As<TS, byte>(ref s), ref Unsafe.As<TF, byte>(ref f));

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }

        private static string RuntimeName()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }
    }
}
