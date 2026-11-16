using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Tuples, pairs and other raw items moved as blocks: every count around the vector widths, every alignment of the
/// destination and of the array read into, and the bytes of each value written alone.
/// </summary>
[TestClass]
public class BlockFastPathTests
{
    private static void BlocksRoundTrip<T>(Func<int, T> make)
    {
        // Every length around the vector widths (16 and 64 bytes): the bytes are each value's own, after the count.
        foreach (int n in Enumerable.Range(0, 70).Concat(new[] { 100, 1000 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var expected = BitConverter.GetBytes(n).Concat(values.SelectMany(v => NinoSerializer.Serialize(v))).ToArray();
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()));
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<T[]>(expected));
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(expected));
            var reused = Enumerable.Repeat(make(7), n + 3).ToList();
            NinoDeserializer.Deserialize(expected, ref reused);
            CollectionAssert.AreEqual(values, reused);
        }
    }

    [TestMethod]
    public void BlocksReadAtEveryAlignment()
    {
        // Arrays are 8-byte aligned: 16-byte values start on a 64-byte boundary in some and 8 bytes past one in the
        // others, read with different plans (AVX-512). Both, into memory full of garbage first.
        ReadsAtEveryAlignment(i => (i, -7L * i));
        ReadsAtEveryAlignment(i => new KeyValuePair<int, long>(i, i * 1000L));
        ReadsAtEveryAlignment(i => (i * 3L, (byte)i));
        ReadsAtEveryAlignment(i => ((short)i, (double)i / 3));
        // 20 bytes in memory: no group of whole vectors fits in four, so groups that end inside a vector.
        ReadsAtEveryAlignment(i => ((byte)i, i, i * 3, i * 5, i * 7));
        // Not a block (a tuple item is not raw): read a field at a time, its padding left as it was, and a new array
        // of it allocated zeroed.
        ReadsAtEveryAlignment(i => (i * 1f, i * 2f, (i, (short)i), i % 2 == 0), block: false);
    }

    private static void ReadsAtEveryAlignment<T>(Func<int, T> make, bool block = true) where T : unmanaged
    {
        var seen = new HashSet<long>();
        var keep = new List<object>();
        // Every count up to a few groups: a group from the first value and one ending at the last, over the aligned ones.
        foreach (int n in Enumerable.Range(30, 100).Concat(new[] { 1000, 1001 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var bytes = NinoSerializer.Serialize(values);
            for (int shift = 0; shift < 8; shift++)
            {
                keep.Add(new byte[shift * 8 + 1]);
                var read = new T[n];
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(read.AsSpan()).Fill(0xFF);
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(read, System.Runtime.InteropServices.GCHandleType.Pinned);
                seen.Add((long)pin.AddrOfPinnedObject() % 16);
                pin.Free();
                NinoDeserializer.Deserialize(bytes, ref read);
                CollectionAssert.AreEqual(values, read, $"{typeof(T)} n={n} shift={shift}");
                // Every byte written, padding as zero, whatever the memory held: new arrays read as blocks are
                // allocated uninitialized (NinoElements.CopiedWhole), and stale bytes must not show through. The
                // values were built in zeroed memory, so their padding is zero.
                if (block)
                    AssertSameBytes(values, read, $"{typeof(T)} n={n} shift={shift}");
            }

            // A new array, allocated uninitialized, after memory of the same size was filled with 0xCC and dropped.
            var garbage = new T[n];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(garbage.AsSpan()).Fill(0xCC);
            garbage = null;
            GC.Collect(0);
            AssertSameBytes(values, NinoDeserializer.Deserialize<T[]>(bytes), $"{typeof(T)} n={n} new array");
        }

        GC.KeepAlive(keep);
        if (System.Runtime.CompilerServices.Unsafe.SizeOf<T>() % 16 == 0)
            Assert.IsTrue(seen.Contains(0) && seen.Contains(8), $"{typeof(T)}: both alignments read ({string.Join(",", seen)})");
    }

    private static void AssertSameBytes<T>(T[] expected, T[] actual, string message) where T : unmanaged
    {
        var want = System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.AsSpan());
        var got = System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual.AsSpan());
        int at = 0;
        while (at < got.Length && at < want.Length && got[at] == want[at])
            at++;
        if (at < want.Length || got.Length != want.Length)
            Assert.Fail($"{message}: byte {at} is {(at < got.Length ? got[at] : -1):X2}, not {(at < want.Length ? want[at] : -1):X2}");
    }

    [TestMethod]
    public void BlocksWriteAsEachValue()
    {
        // Groups of values move as vectors (AVX-512, AVX2, SSSE3, Arm64 and WebAssembly each their own plan), with
        // loads before and after a group's values: every count, every destination alignment, every layout below.
        WritesAsEachValue(i => (i, -7L * i));
        WritesAsEachValue(i => new KeyValuePair<int, long>(i, i * 1000L));
        WritesAsEachValue(i => (i * 3L, (byte)i));
        WritesAsEachValue(i => ((byte)i, i * 3L));
        WritesAsEachValue(i => ((short)i, (double)i / 3));
        WritesAsEachValue(i => ((short)i, i * 5));
        WritesAsEachValue(i => ((byte)i, i * 5));
        WritesAsEachValue(i => (i, (short)i, i * 9L));
        WritesAsEachValue(i => ((double)i, i, (byte)i));
        WritesAsEachValue(i => ((byte)i, i, i * 3, i * 5, i * 7));
        // Not a block (a tuple item is not raw), written a value at a time; BlockStructTests has the block of this
        // shape (BlockWaypoint).
        WritesAsEachValue(i => (i * 1f, i * 2f, (i, (short)i), i % 2 == 0));
    }

    private static void WritesAsEachValue<T>(Func<int, T> make) where T : unmanaged
    {
        foreach (int n in Enumerable.Range(0, 80).Concat(new[] { 255, 1000, 1001 }))
        {
            var values = Enumerable.Range(0, n).Select(make).ToArray();
            var expected = new List<byte>(BitConverter.GetBytes(n));
            foreach (var value in values)
                expected.AddRange(NinoSerializer.Serialize(value));
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), $"{typeof(T)}[] n={n}");
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), $"List<{typeof(T)}> n={n}");
            // Every alignment of the blocks to a 64-byte boundary from a few groups on (below, every 8 bytes).
            for (int shift = 1; shift < (n >= 32 ? 64 : 8); shift++)
            {
                var buffer = new NinoArrayBufferWriter();
                buffer.GetSpan(shift);
                buffer.Advance(shift);
                NinoSerializer.Serialize(values.ToList(), buffer);
                CollectionAssert.AreEqual(expected, buffer.WrittenSpan.Slice(shift).ToArray(), $"{typeof(T)} n={n} shift={shift}");
            }

            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(NinoSerializer.Serialize(values)));
        }

        Assert.AreEqual(4, NinoSerializer.Serialize((List<T>)null).Length);
        Assert.IsNull(NinoDeserializer.Deserialize<List<T>>(NinoSerializer.Serialize((T[])null)));
    }

    [TestMethod]
    public void TuplesAndPairsOfRawItemsAsBlocks()
    {
        // Copied as memory when it is the block ((int, int)), moved by byte shuffles when the runtime reorders or pads
        // the items ((int, long) is a long, then an int and 4 padding bytes), or one at a time (over 16 bytes).
        BlocksRoundTrip(i => (i, i * 3));
        BlocksRoundTrip(i => (i, -7L * i));
        BlocksRoundTrip(i => ((byte)i, i * 11, (short)-i));
        BlocksRoundTrip(i => (long.MaxValue - i, i * 5L, i));
        BlocksRoundTrip(i => (i % 2 == 0, (double)i / 3));
        BlocksRoundTrip(i => new KeyValuePair<int, int>(i, -i));
        BlocksRoundTrip(i => new KeyValuePair<int, long>(i, i * 1000L));
        BlocksRoundTrip(i => new KeyValuePair<long, byte>(i * 3L, (byte)i));
        BlocksRoundTrip(i => (i, i % 3 == 0 ? null : "t" + i));
    }
}
