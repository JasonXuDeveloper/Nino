using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using static Nino.UnitTests.Rt;
using static Nino.UnitTests.CollectionFastPathFixtures;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// SortedList of raw keys and values written and read as raw pairs: unaligned writes, in-order and out-of-order reads,
/// duplicates, signed and unsigned keys, and keys split by their own comparer.
/// </summary>
[TestClass]
public class SortedListFastPathTests
{
    [TestMethod]
    public void SortedListWritesRawPairsAtAnUnalignedOffset()
    {
        Check<int>(i => i, i => -13 * i - 1);
        Check<uint>(i => (uint)i, i => unchecked((uint)(-13 * i - 1)));
        Check<short>(i => (short)i, i => (short)(-13 * i - 1));
        Check<long>(i => (long)i << 34, i => -13L * i - 1);

        static void Check<T>(Func<int, T> key, Func<int, T> value)
        {
            for (int count = 0; count <= 65; count++)
            {
                var list = new SortedList<T, T>();
                for (int i = 0; i < count; i++)
                    list.Add(key(i), value(i));

                byte[] expected = NinoSerializer.Serialize(new Dictionary<T, T>(list));
                using var buffer = new NinoArrayBufferWriter();
                buffer.GetSpan(expected.Length + 2).Fill(0xA5);
                buffer.Advance(1);
                NinoSerializer.Serialize(list, buffer);
                CollectionAssert.AreEqual(expected, buffer.WrittenSpan.Slice(1).ToArray(), $"{typeof(T)} count={count}");
                Assert.AreEqual((byte)0xA5, buffer.WrittenSpan[0]);
                Assert.AreEqual((byte)0xA5, buffer.GetSpan(1)[0]);
            }
        }
    }

    [TestMethod]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(31)]
    [DataRow(32)]
    [DataRow(35)]
    [DataRow(65)]
    public void SortedListReadsRawPairsInOrderAndOutOfOrder(int count)
    {
        var expected = new SortedList<int, int>();
        for (int i = 0; i < count; i++)
            expected.Add(i * 3 - 5, -i * 13 - 1);

        using var buffer = OffsetWriter();
        NinoSerializer.Serialize(expected, buffer);
        byte[] bytes = buffer.WrittenSpan.Slice(1).ToArray();
        var list = new SortedList<int, int>(48) { [123] = 456 };
        NinoDeserializer.Deserialize(buffer.WrittenSpan.Slice(1), ref list);
        CollectionAssert.AreEqual(expected.ToList(), list.ToList());

        byte[] descending = NinoSerializer.Serialize(expected.Reverse().ToDictionary(p => p.Key, p => p.Value));
        NinoDeserializer.Deserialize(descending, ref list);
        CollectionAssert.AreEqual(expected.ToList(), list.ToList());
        var reverseComparer = new SortedList<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        NinoDeserializer.Deserialize(bytes, ref reverseComparer);
        CollectionAssert.AreEqual(expected.Reverse().ToList(), reverseComparer.ToList());
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<SortedList<int, int>>(bytes.AsSpan(0, bytes.Length - 1)));

        foreach (int index in new[] { 1, 5, 9, count - 2 })
        {
            // Duplicates at the start, between vector groups and in the scalar tail.
            var duplicate = (byte[])bytes.Clone();
            Buffer.BlockCopy(bytes, 4 + (index - 1) * 8, duplicate, 4 + index * 8, 4);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<SortedList<int, int>>(duplicate));
            var reused = new SortedList<int, int>(48) { [123] = 456 };
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(duplicate, ref reused));
            var prefix = expected.Take(index).ToList();
            // The indexer overwrites the repeated key before AddUnique reports it.
            prefix[index - 1] = new KeyValuePair<int, int>(expected.Keys[index - 1], expected.Values[index]);
            CollectionAssert.AreEqual(prefix, reused.ToList());
        }
    }

    private enum UInt16Key : ushort { }
    private enum Int32Key : int { }
    private enum UInt64Key : ulong { }

    [TestMethod]
    public void SortedListReusesSignedAndUnsignedIntegralPairs()
    {
        Check(i => (sbyte)(i - 18), i => (sbyte)(i * 7));
        Check(i => (byte)(112 + i), i => (byte)~i);
        Check(i => (short)((i - 18) * 1_000), i => (short)~i);
        Check(i => (ushort)(0x7FF0 + i * 2), i => (ushort)~i);
        Check(i => 0x7FFF_FFE0u + (uint)i * 2, i => ~(uint)i);
        Check(i => i == 0 ? long.MinValue : ((long)i - 18) << 40, i => -((long)i << 42) - 1);
        Check(i => 0x7FFF_FFFF_FFFF_FFE0ul + (ulong)i * 2, i => ~(ulong)i);

        Check(i => (Int8Key)(i - 18), i => (Int8Key)(i * 7));
        Check(i => (UInt16Key)(0x7FF0 + i * 2), i => (UInt16Key)(ushort)~i);
        Check(i => (Int32Key)(i - 18), i => (Int32Key)~i);
        Check(i => (UInt64Key)(0x7FFF_FFFF_FFFF_FFE0ul + (ulong)i * 2), i => (UInt64Key)~(ulong)i);

        static void Check<T>(Func<int, T> key, Func<int, T> value) where T : unmanaged
        {
            var expected = new SortedList<T, T>();
            for (int i = 0; i < 35; i++)
                expected.Add(key(i), value(i));

            using var buffer = OffsetWriter();
            NinoSerializer.Serialize(expected, buffer);
            byte[] bytes = buffer.WrittenSpan.Slice(1).ToArray();
            var reused = new SortedList<T, T>(48) { [key(0)] = value(1) };
            var original = reused;
            NinoDeserializer.Deserialize(buffer.WrittenSpan.Slice(1), ref reused);
            Assert.AreSame(original, reused);
            Assert.AreSequenceEqual(expected, reused);
            Assert.AreEqual(48, reused.Capacity);

            byte[] descending = NinoSerializer.Serialize(expected.Reverse().ToDictionary(p => p.Key, p => p.Value));
            NinoDeserializer.Deserialize(descending, ref reused);
            Assert.AreSequenceEqual(expected, reused);
            var reverseComparer = new SortedList<T, T>(48, Comparer<T>.Create((a, b) => Comparer<T>.Default.Compare(b, a)));
            NinoDeserializer.Deserialize(bytes, ref reverseComparer);
            Assert.AreSequenceEqual(expected.Reverse(), reverseComparer);

            var truncated = bytes.AsSpan(0, bytes.Length - 1).ToArray();
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(truncated, ref reused));
        }
    }

    [TestMethod]
    public void SortedListSplitsRawPairsUsingTheIndexComparer()
    {
        var expected = new SortedList<ReverseIndex, uint>();
        for (int i = 0; i < 65; i++)
            expected.Add(new ReverseIndex(i), (uint)(i * 13));

        using var buffer = OffsetWriter();
        NinoSerializer.Serialize(expected, buffer);
        var bytes = buffer.WrittenSpan.Slice(1).ToArray();
        var reused = new SortedList<ReverseIndex, uint>(80);
        var original = reused;
        NinoDeserializer.Deserialize(buffer.WrittenSpan.Slice(1), ref reused);
        Assert.AreSame(original, reused);
        Assert.AreEqual(80, reused.Capacity);
        Assert.AreSequenceEqual(expected, reused);
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(reused));

        var unordered = expected.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(unordered), ref reused);
        Assert.AreSequenceEqual(expected, reused);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize(bytes.AsSpan(0, bytes.Length - 1), ref reused));
    }
}
