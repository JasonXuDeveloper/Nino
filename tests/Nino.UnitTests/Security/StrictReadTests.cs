using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>Version tolerant and recursive: past the inline depth its bodies go to the outermost region's section.</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class StrictChain
{
    public int Value;
    public StrictChain Next;

    public static StrictChain Of(int depth)
    {
        StrictChain node = null;
        for (int i = depth; i > 0; i--)
            node = new StrictChain { Value = i, Next = node };
        return node;
    }

    public int Depth()
    {
        int depth = 0;
        for (var node = this; node != null; node = node.Next)
            depth++;
        return depth;
    }
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class StrictKeyed
{
    public int Health;
    public string Name;
    public long Score;
}

/// <summary>Not raw (it holds a string): its bool is read as a single value.</summary>
[NinoType]
public struct StrictFlags
{
    public bool On;
    public string Name;
}

/// <summary>
/// Bytes Nino never writes are errors in every mode: a bool or presence byte other than 0 or 1, a keyed member whose
/// value does not fill its entry, a deferred section that does not match its region.
/// </summary>
[TestClass]
public class StrictReadTests
{
    private static void Invalid<T>(byte[] data)
    {
        var e = Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(data), typeof(T).Name);
        StringAssert.StartsWith(e.Message, "Nino: ");
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] With(byte[] data, int at, int value)
    {
        var copy = (byte[])data.Clone();
        BitConverter.GetBytes(value).CopyTo(copy, at);
        return copy;
    }

    private static byte[] WithByte(byte[] data, int at, byte value)
    {
        var copy = (byte[])data.Clone();
        copy[at] = value;
        return copy;
    }

    [TestMethod]
    public void BoolsOtherThanZeroOrOneAreErrors()
    {
        Assert.IsTrue(NinoDeserializer.Deserialize<bool>(new byte[] { 1 }));
        CollectionAssert.AreEqual(new[] { true, false }, NinoDeserializer.Deserialize<bool[]>(Concat(Ints(2), new byte[] { 1, 0 })));
        foreach (byte b in new byte[] { 2, 0x80, 0xFF })
        {
            Invalid<bool>(new[] { b });
            // In a block, at the start, in the middle (past the first 8 and 16) and at the end.
            foreach (int length in new[] { 1, 9, 33, 100 })
            foreach (int at in new[] { 0, length / 2, length - 1 })
            {
                var block = new byte[length];
                block[at] = b;
                Invalid<bool[]>(Concat(Ints(length), block));
                Invalid<List<bool>>(Concat(Ints(length), block));
            }

            Invalid<Dictionary<int, bool>>(Concat(Ints(1, 5), new[] { b }));
            Invalid<Dictionary<bool, int>>(Concat(Ints(1), new[] { b }, Ints(5)));
            Invalid<HashSet<bool>>(Concat(Ints(1), new[] { b }));
            Invalid<StrictFlags>(Concat(new[] { b }, NinoSerializer.Serialize("a")));
            Invalid<(bool, string)>(Concat(new[] { b }, NinoSerializer.Serialize("a")));
            // Tuples and pairs of raw items read as blocks: the vector paths move the bytes without ReadBlock, so the
            // bools are checked before them, wherever the bad one is (every place in a step of eight, and the tail).
            foreach (int length in new[] { 1, 9, 33, 100 })
            foreach (int at in new[] { 0, 1, 2, 3, 4, 5, 6, 7, length / 2, length - 1 }.Where(a => a < length).Distinct())
            {
                var pairs = Enumerable.Range(0, length).Select(i => (i % 2 == 0, i)).ToArray();
                Invalid<(bool, int)[]>(WithByte(NinoSerializer.Serialize(pairs), 4 + at * 5, b));
                Invalid<List<(bool, int)>>(WithByte(NinoSerializer.Serialize(pairs.ToList()), 4 + at * 5, b));
                var swapped = pairs.Select(p => (p.Item2, (short)1, p.Item1)).ToArray();
                Invalid<(int, short, bool)[]>(WithByte(NinoSerializer.Serialize(swapped), 4 + at * 7 + 6, b));
                var kvps = pairs.Select(p => new KeyValuePair<bool, int>(p.Item1, p.Item2)).ToArray();
                Invalid<KeyValuePair<bool, int>[]>(WithByte(NinoSerializer.Serialize(kvps), 4 + at * 5, b));
                // Not a block (a tuple item is not raw): read a value at a time.
                var padded = pairs.Select(p => (1f, 2f, (p.Item2, (short)3), p.Item1)).ToArray();
                Invalid<(float, float, (int, short), bool)[]>(WithByte(NinoSerializer.Serialize(padded), 4 + at * 15 + 14, b));
            }

            // bool?[]: [has value][value] each; the second value is the bad one.
            var nullables = NinoSerializer.Serialize(new bool?[] { true, false, null });
            Assert.AreEqual(4 + 3 * 2, nullables.Length);
            nullables[4 + 2 + 1] = b;
            Invalid<bool?[]>(nullables);
        }
    }

    [TestMethod]
    public void PresenceBytesOtherThanZeroOrOneAreErrors()
    {
        Assert.AreEqual(7, NinoDeserializer.Deserialize<int?>(Concat(new byte[] { 1 }, Ints(7))));
        Assert.IsNull(NinoDeserializer.Deserialize<UntrustedBigClass>(new byte[] { 0 }));
        CollectionAssert.AreEqual(new int?[] { 7, null },
            NinoDeserializer.Deserialize<int?[]>(NinoSerializer.Serialize(new int?[] { 7, null })));
        foreach (byte b in new byte[] { 2, 0xFF })
        {
            Invalid<int?>(Concat(new[] { b }, Ints(7)));
            Invalid<long?>(Concat(new[] { b }, new byte[8]));
            Invalid<UntrustedBigClass>(Concat(new[] { b }, new byte[600]));
            Invalid<StrictKeyed>(Concat(new[] { b }, NinoSerializer.Serialize(new StrictKeyed()).Skip(1).ToArray()));
            Invalid<List<int?>>(Concat(Ints(1), new[] { b }, Ints(7)));
            // int?[]: a fixed stride of flag, 3 bytes of padding, value; the second flag is the bad one.
            var array = NinoSerializer.Serialize(new int?[] { 7, null, 9 });
            int stride = (array.Length - 4) / 3;
            array[4 + stride] = b;
            Invalid<int?[]>(array);
        }
    }

    [TestMethod]
    public void KeyedMembersMustFillTheirEntries()
    {
        var keyed = NinoSerializer.Serialize(new StrictKeyed { Health = 1, Name = "ab", Score = 2 });
        // [null flag][body length] then entries of [key][length][value]: Health first, at 5.
        int body = BitConverter.ToInt32(keyed, 1);
        Assert.AreEqual(4, BitConverter.ToInt32(keyed, 9));
        var read = NinoDeserializer.Deserialize<StrictKeyed>(keyed);
        Assert.AreEqual((1, "ab", 2L), (read.Health, read.Name, read.Score));

        // Health's entry 1 byte longer than its int: the byte would be skipped.
        var longInt = Concat(With(With(keyed, 1, body + 1), 9, 5).AsSpan(0, 17).ToArray(), new byte[] { 0 },
            keyed.AsSpan(17).ToArray());
        Invalid<StrictKeyed>(longInt);

        // Name's entry 1 byte longer than its string.
        int nameLength = BitConverter.ToInt32(keyed, 21);
        int nameEnd = 25 + nameLength;
        var longString = Concat(With(With(keyed, 1, body + 1), 21, nameLength + 1).AsSpan(0, nameEnd).ToArray(),
            new byte[] { 0 }, keyed.AsSpan(nameEnd).ToArray());
        Invalid<StrictKeyed>(longString);

        // An unknown key is skipped whole, whatever its length: still valid.
        var unknown = Concat(With(keyed, 1, body + 12).AsSpan(0, 5 + body).ToArray(), Ints(999, 4, 77),
            keyed.AsSpan(5 + body).ToArray());
        Assert.AreEqual("ab", NinoDeserializer.Deserialize<StrictKeyed>(unknown).Name);
    }

    [TestMethod]
    public void DeferredSectionsMustMatchTheirRegion()
    {
        var deep = NinoSerializer.Serialize(StrictChain.Of(300));
        Assert.AreEqual(300, NinoDeserializer.Deserialize<StrictChain>(deep).Depth());
        // [null flag][region length | 0x80000000][members ... section][offset of the section].
        int length = BitConverter.ToInt32(deep, 1);
        Assert.IsTrue(length < 0, "the region holds a section");
        length &= int.MaxValue;
        int offsetAt = 5 + length - 4;
        int section = BitConverter.ToInt32(deep, offsetAt);

        Invalid<StrictChain>(With(deep, offsetAt, 0));               // the members read as the section
        Invalid<StrictChain>(With(deep, offsetAt, 4));               // into the members
        Invalid<StrictChain>(With(deep, offsetAt, length - 4));      // an empty section
        Invalid<StrictChain>(With(deep, offsetAt, -1));
        Invalid<StrictChain>(With(deep, offsetAt, length));
        Invalid<StrictChain>(With(deep, 5 + section, 0x7FFF0000));   // a key past its entry
        Invalid<StrictChain>(With(deep, 5 + section + 4, -1));       // an entry's length
        Invalid<StrictChain>(With(deep, 1, length));                 // the flag cleared

        // The flag on a region with no section: [value][Next: null][offset 0].
        Invalid<StrictChain>(Concat(new byte[] { 1 }, Ints(9 | int.MinValue, 1), new byte[] { 0 }, Ints(0)));
        // The flag on a region inside another: [1][outer][value 1][1][inner | flag][value 2][null].
        var two = NinoSerializer.Serialize(StrictChain.Of(2));
        Assert.AreEqual(2, NinoDeserializer.Deserialize<StrictChain>(two).Depth());
        int inner = BitConverter.ToInt32(two, 10);
        Invalid<StrictChain>(With(two, 10, inner | int.MinValue));
    }
}
